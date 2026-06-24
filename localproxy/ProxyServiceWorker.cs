using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace localproxy;

public class ProxyServiceWorker : BackgroundService
{
    private readonly ProxyConfiguration _config;
    private readonly IConfigurationRoot _configuration;
    private readonly ILogger<ProxyServiceWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _syncLock = new();
    private ProxyServer? _proxyServer;
    private CancellationTokenSource? _serverCts;
    private IDisposable? _reloadRegistration;
    private bool _reloadRequested;

    public ProxyServiceWorker(ProxyConfiguration config, IConfigurationRoot configuration, ILoggerFactory loggerFactory, ILogger<ProxyServiceWorker> logger)
    {
        _config = config;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Proxy service is starting");

        _reloadRegistration = ChangeToken.OnChange(
            _configuration.GetReloadToken,
            OnConfigurationChanged);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _reloadRequested = false;

                lock (_syncLock)
                {
                    _serverCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    _proxyServer = new ProxyServer(_config, _loggerFactory);
                }

                try
                {
                    await _proxyServer.StartAsync(_serverCts.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _reloadRequested)
                {
                    if (_reloadRequested && !stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Proxy service restarting after configuration reload");
                    }
                }
                finally
                {
                    lock (_syncLock)
                    {
                        _proxyServer?.Stop();
                        _proxyServer = null;
                        _serverCts?.Dispose();
                        _serverCts = null;
                    }
                }

                if (!_reloadRequested)
                {
                    break;
                }
            }

            _logger.LogInformation("Proxy service stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in proxy service");
            throw;
        }
    }

    private void OnConfigurationChanged()
    {
        try
        {
            var reloadedConfig = new ProxyConfiguration();
            _configuration.Bind(reloadedConfig);

            lock (_syncLock)
            {
                ApplyReloadedConfiguration(reloadedConfig);
                _reloadRequested = true;
                _serverCts?.Cancel();
            }

            _logger.LogInformation("Configuration file updated, reloading proxy server");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload configuration after file change");
        }
    }

    private void ApplyReloadedConfiguration(ProxyConfiguration reloadedConfig)
    {
        _config.Proxy = reloadedConfig.Proxy;
        _config.Logging = reloadedConfig.Logging;
        _config.Authentication = reloadedConfig.Authentication;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Proxy service is stopping gracefully");
        _reloadRegistration?.Dispose();

        lock (_syncLock)
        {
            _serverCts?.Cancel();
            _proxyServer?.Stop();
        }

        await base.StopAsync(cancellationToken);
    }
}
