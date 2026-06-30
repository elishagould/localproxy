using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace localproxy;

public class ProxyServer
{
    private class ListenerState
    {
        public required ListenerSettings Config { get; init; }
        public string Bind { get; init; } = string.Empty;
        public IPEndPoint? Endpoint { get; set; }
        public TcpListener? Listener { get; set; }
        public Task? AcceptTask { get; set; }
        public bool IsActive { get; set; }
        public DateTime LastRetryAtUtc { get; set; }
        public Exception? LastError { get; set; }
    }

    private readonly HttpClient _proxyHttpClient;
    private readonly HttpClient _directHttpClient;
    private readonly SspiCredentialCache _credentialCache;
    private readonly AuthenticatedConnectionPool _connectionPool;
    private readonly ProxyConfiguration _config;
    private readonly ConnectionTracker _connectionTracker;
    private readonly ILogger<ProxyServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<ListenerState> _listenerStates = new();
    private readonly object _listenerLock = new();
    private ProxyExclusionMatcher _exclusionMatcher;
    private ProxyExclusionMatcher _blocklistMatcher;
    private CancellationTokenSource? _cts;
    private Task? _retryTask;

    public ProxyServer(ProxyConfiguration config, ILoggerFactory loggerFactory, ConnectionTracker connectionTracker)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _connectionTracker = connectionTracker;
        _logger = loggerFactory.CreateLogger<ProxyServer>();
        
        var activeProfile = _config.Proxy.ActiveProfile;
        // Initialize exclusion matcher with useProxy awareness
        _exclusionMatcher = new ProxyExclusionMatcher(activeProfile.NoProxy, activeProfile.EnableUpstreamProxy);
        if (activeProfile.NoProxy.Any())
        {
            _logger.LogInformation("Proxy exclusion list configured with {Count} patterns: {Patterns}", 
                activeProfile.NoProxy.Count, 
                string.Join(", ", activeProfile.NoProxy));
        }
        
        // Initialize blocklist matcher
        _blocklistMatcher = new ProxyExclusionMatcher(activeProfile.BlockedHosts, true);
        if (activeProfile.BlockedHosts.Any())
        {
            _logger.LogInformation("Blocklist configured with {Count} patterns: {Patterns}", 
                activeProfile.BlockedHosts.Count, 
                string.Join(", ", activeProfile.BlockedHosts));
        }

        var sysProxy = WebRequest.DefaultWebProxy;
        if (activeProfile.EnableUpstreamProxy && sysProxy != null)
        {
            sysProxy.Credentials = CredentialCache.DefaultCredentials;
            _logger.LogInformation("Using system proxy: {Proxy}", sysProxy.GetProxy(new Uri("http://example.com")));
        }
        else
        {
            sysProxy = null;
            _logger.LogInformation("Upstream proxy is disabled or not configured - direct connections will be used");
        }

        var proxyHandler = new HttpClientHandler
        {
            Proxy = sysProxy,
            UseProxy = sysProxy != null,
            UseDefaultCredentials = true,
            AllowAutoRedirect = false,
            UseCookies = false
        };

        var directHandler = new HttpClientHandler
        {
            UseProxy = false,
            UseDefaultCredentials = true,
            AllowAutoRedirect = false,
            UseCookies = false
        };

        _proxyHttpClient = new HttpClient(proxyHandler, disposeHandler: true);
        _directHttpClient = new HttpClient(directHandler, disposeHandler: true);
        _credentialCache = new SspiCredentialCache(_loggerFactory.CreateLogger<SspiCredentialCache>());
        _connectionPool = new AuthenticatedConnectionPool(_loggerFactory.CreateLogger<AuthenticatedConnectionPool>());
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        InitializeListenerStates();

        if (_listenerStates.Count == 0)
        {
            throw new InvalidOperationException("No proxy listeners were configured.");
        }

        _logger.LogInformation("Starting simple forward proxy with {Count} listener(s)", _listenerStates.Count);

        // Start retry background task
        _retryTask = RetryPendingListenersAsync(_cts.Token);

        // Start accept loops for all listeners
        var acceptTasks = new List<Task>();
        lock (_listenerLock)
        {
            foreach (var state in _listenerStates)
            {
                if (TryStartListener(state))
                {
                    acceptTasks.Add(AcceptClientsAsync(state, _cts.Token));
                }
            }
        }

        try
        {
            await Task.WhenAll(acceptTasks.Concat(new[] { _retryTask }));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Proxy server is shutting down");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in proxy server main loop");
            throw;
        }
    }

    private bool TryStartListener(ListenerState state)
    {
        try
        {
            if (state.Listener != null)
            {
                return false;
            }

            state.Endpoint = ResolveListenerEndpoint(state.Bind, state.Config.Port);
            state.Listener = new TcpListener(state.Endpoint);
            state.Listener.Start();
            state.IsActive = true;
            state.LastError = null;
            _logger.LogInformation("Listener started on {Endpoint} (bind='{Bind}')", state.Endpoint, state.Bind);
            return true;
        }
        catch (Exception ex)
        {
            state.LastError = ex;
            state.IsActive = false;
            state.LastRetryAtUtc = DateTime.UtcNow;
            _logger.LogWarning(ex, "Failed to start listener for bind='{Bind}', will retry", state.Bind);
            return false;
        }
    }

    private async Task RetryPendingListenersAsync(CancellationToken cancellationToken)
    {
        var retryInterval = TimeSpan.FromSeconds(Math.Max(1, _config.ConnectionMonitor.ListenerRetryIntervalSeconds));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(retryInterval, cancellationToken);

                List<ListenerState> statesToRetry;
                lock (_listenerLock)
                {
                    statesToRetry = _listenerStates
                        .Where(s => !s.IsActive)
                        .ToList();
                }

                foreach (var state in statesToRetry)
                {
                    if (TryStartListener(state))
                    {
                        _ = AcceptClientsAsync(state, cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AcceptClientsAsync(ListenerState state, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                if (state.Listener is null)
                {
                    break;
                }

                client = await state.Listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                lock (_listenerLock)
                {
                    state.IsActive = false;
                    state.LastError = ex;
                    state.LastRetryAtUtc = DateTime.UtcNow;
                }
                _logger.LogWarning(ex, "Listener for bind='{Bind}' encountered socket error, moving to retry state", state.Bind);
                break;
            }

            if (client is null)
            {
                continue;
            }

            _ = ClientHandler.HandleClientAsync(client, _proxyHttpClient, _directHttpClient, _credentialCache, _connectionPool, _config, _loggerFactory, _exclusionMatcher, _blocklistMatcher, _connectionTracker);
        }
    }

    public void Stop()
    {
        _logger.LogInformation("Stopping proxy server");
        _cts?.Cancel();
        lock (_listenerLock)
        {
            foreach (var state in _listenerStates)
            {
                state.Listener?.Stop();
            }
        }
        _proxyHttpClient.Dispose();
        _directHttpClient.Dispose();
        _logger.LogInformation("Proxy server stopped");
    }

    private void InitializeListenerStates()
    {
        _listenerStates.Clear();

        var configuredListeners = _config.Proxy.EffectiveListeners;
        foreach (var listenerConfig in configuredListeners)
        {
            var bind = string.IsNullOrWhiteSpace(listenerConfig.Bind) ? "any" : listenerConfig.Bind;
            _listenerStates.Add(new ListenerState
            {
                Config = listenerConfig,
                Bind = bind,
                IsActive = false,
                LastRetryAtUtc = DateTime.UtcNow
            });
        }
    }

    private IPEndPoint ResolveListenerEndpoint(string bindTarget, int port)
    {
        if (port <= 0 || port > 65535)
        {
            throw new InvalidOperationException($"Invalid listener port '{port}'.");
        }

        if (string.Equals(bindTarget, "any", StringComparison.OrdinalIgnoreCase))
        {
            return new IPEndPoint(IPAddress.Any, port);
        }

        if (string.Equals(bindTarget, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return new IPEndPoint(IPAddress.Loopback, port);
        }

        if (IPAddress.TryParse(bindTarget, out var ipAddress))
        {
            return new IPEndPoint(ipAddress, port);
        }

        var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(i =>
                string.Equals(i.Name, bindTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.Description, bindTarget, StringComparison.OrdinalIgnoreCase));

        if (networkInterface is null)
        {
            throw new InvalidOperationException($"Listener bind target '{bindTarget}' is neither 'any', 'localhost', an IP address, nor a valid network interface name.");
        }

        var interfaceAddress = networkInterface
            .GetIPProperties()
            .UnicastAddresses
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

        if (interfaceAddress is null)
        {
            throw new InvalidOperationException($"Network interface '{bindTarget}' does not have an IPv4 unicast address.");
        }

        return new IPEndPoint(interfaceAddress, port);
    }

    public void RefreshExclusionMatcher()
    {
        var activeProfile = _config.Proxy.ActiveProfile;
        _exclusionMatcher = new ProxyExclusionMatcher(activeProfile.NoProxy, activeProfile.EnableUpstreamProxy);
        _logger.LogInformation("Proxy exclusion matcher refreshed with {Count} patterns: {Patterns}",
            activeProfile.NoProxy.Count,
            string.Join(", ", activeProfile.NoProxy));

        _blocklistMatcher = new ProxyExclusionMatcher(activeProfile.BlockedHosts, true);
        _logger.LogInformation("Blocklist matcher refreshed with {Count} patterns: {Patterns}",
            activeProfile.BlockedHosts.Count,
            string.Join(", ", activeProfile.BlockedHosts));
    }
}
