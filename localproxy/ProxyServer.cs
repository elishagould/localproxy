using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace localproxy;

public class ProxyServer
{
    private readonly HttpClient _httpClient;
    private readonly SspiCredentialCache _credentialCache;
    private readonly AuthenticatedConnectionPool _connectionPool;
    private readonly TcpListener _listener;
    private readonly ProxyConfiguration _config;
    private readonly ILogger<ProxyServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private ProxyExclusionMatcher _exclusionMatcher;
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

        var handler = new HttpClientHandler
        {
            Proxy = sysProxy,
            UseProxy = sysProxy != null,
            UseDefaultCredentials = true
        };

        _httpClient = new HttpClient(handler, disposeHandler: true);
        _credentialCache = new SspiCredentialCache(_loggerFactory.CreateLogger<SspiCredentialCache>());
        _connectionPool = new AuthenticatedConnectionPool(_loggerFactory.CreateLogger<AuthenticatedConnectionPool>());
        _listener = new TcpListener(IPAddress.Any, _config.Proxy.Port);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting simple forward proxy on http://localhost:{Port}/", _config.Proxy.Port);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Start();

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = ClientHandler.HandleClientAsync(client, _httpClient, _credentialCache, _connectionPool, _config, _loggerFactory, _exclusionMatcher);
            }
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

    public void Stop()
    {
        _logger.LogInformation("Stopping proxy server");
        _cts?.Cancel();
        _listener.Stop();
        _httpClient.Dispose();
        _logger.LogInformation("Proxy server stopped");
    }

    public void RefreshExclusionMatcher()
    {
        var activeProfile = _config.Proxy.ActiveProfile;
        _exclusionMatcher = new ProxyExclusionMatcher(activeProfile.NoProxy, activeProfile.EnableUpstreamProxy);
        _logger.LogInformation("Proxy exclusion matcher refreshed with {Count} patterns: {Patterns}",
            activeProfile.NoProxy.Count,
            string.Join(", ", activeProfile.NoProxy));
    }
}
