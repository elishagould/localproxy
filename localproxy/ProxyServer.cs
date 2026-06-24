using System;
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
    private readonly HttpClient _proxyHttpClient;
    private readonly HttpClient _directHttpClient;
    private readonly SspiCredentialCache _credentialCache;
    private readonly AuthenticatedConnectionPool _connectionPool;
    private readonly List<TcpListener> _listeners = new();
    private readonly ProxyConfiguration _config;
    private readonly ILogger<ProxyServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private ProxyExclusionMatcher _exclusionMatcher;
    private ProxyExclusionMatcher _blocklistMatcher;
    private CancellationTokenSource? _cts;

    public ProxyServer(ProxyConfiguration config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _loggerFactory = loggerFactory;
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
        InitializeListeners();

        if (_listeners.Count == 0)
        {
            throw new InvalidOperationException("No proxy listeners were configured.");
        }

        foreach (var listener in _listeners)
        {
            listener.Start();
        }

        _logger.LogInformation("Starting simple forward proxy on {Count} listener(s): {Listeners}",
            _listeners.Count,
            string.Join(", ", _listeners.Select(l => l.LocalEndpoint?.ToString() ?? "unknown")));

        var acceptTasks = _listeners
            .Select(listener => AcceptClientsAsync(listener, _cts.Token))
            .ToList();

        try
        {
            await Task.WhenAll(acceptTasks);
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

    private async Task AcceptClientsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (client is null)
            {
                continue;
            }

            _ = ClientHandler.HandleClientAsync(client, _proxyHttpClient, _directHttpClient, _credentialCache, _connectionPool, _config, _loggerFactory, _exclusionMatcher, _blocklistMatcher);
        }
    }

    public void Stop()
    {
        _logger.LogInformation("Stopping proxy server");
        _cts?.Cancel();
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }
        _proxyHttpClient.Dispose();
        _directHttpClient.Dispose();
        _logger.LogInformation("Proxy server stopped");
    }

    private void InitializeListeners()
    {
        _listeners.Clear();

        var configuredListeners = _config.Proxy.EffectiveListeners;
        foreach (var listenerConfig in configuredListeners)
        {
            var bind = string.IsNullOrWhiteSpace(listenerConfig.Bind) ? "any" : listenerConfig.Bind;
            var endpoint = ResolveListenerEndpoint(bind, listenerConfig.Port);
            _listeners.Add(new TcpListener(endpoint));
            _logger.LogInformation("Configured listener on {Endpoint} (bind='{Bind}')", endpoint, bind);
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
