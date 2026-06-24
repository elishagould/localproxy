using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace localproxy;

public sealed class TrayProxyRunner : IDisposable
{
    private readonly ProxyConfiguration _config;
    private readonly IConfigurationRoot _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TrayProxyRunner> _logger;
    private readonly object _syncLock = new();

    private ProxyServer? _proxyServer;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;
    private IDisposable? _reloadRegistration;
    private bool _stopping;
    private bool _restartRequested;

    public TrayProxyRunner(
        ProxyConfiguration config,
        IConfigurationRoot configuration,
        ILoggerFactory loggerFactory,
        ILogger<TrayProxyRunner> logger)
    {
        _config = config;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public Task StartAsync()
    {
        _reloadRegistration = ChangeToken.OnChange(
            _configuration.GetReloadToken,
            OnConfigurationChanged);

        StartServer();
        return Task.CompletedTask;
    }

    public void Stop()
    {
        lock (_syncLock)
        {
            _stopping = true;
            _reloadRegistration?.Dispose();
            _reloadRegistration = null;
            _serverCts?.Cancel();
            _proxyServer?.Stop();
        }

        try
        {
            _serverTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while waiting for tray proxy server shutdown");
        }
    }

    public void Reload()
    {
        lock (_syncLock)
        {
            if (_stopping)
            {
                return;
            }

            _restartRequested = true;
            _logger.LogInformation("Reloading tray proxy server");
            _serverCts?.Cancel();
            _proxyServer?.Stop();
        }
    }

    private void OnConfigurationChanged()
    {
        lock (_syncLock)
        {
            if (_stopping)
            {
                return;
            }

            try
            {
                var reloadedConfig = new ProxyConfiguration();
                _configuration.Bind(reloadedConfig);
                ApplyReloadedConfiguration(reloadedConfig);

                _restartRequested = true;
                _serverCts?.Cancel();
                _proxyServer?.Stop();
                _logger.LogInformation("Configuration file updated, reloading tray proxy server");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload tray configuration after file change");
            }
        }
    }

    private void ApplyReloadedConfiguration(ProxyConfiguration reloadedConfig)
    {
        _config.Proxy = reloadedConfig.Proxy;
        _config.Logging = reloadedConfig.Logging;
        _config.Authentication = reloadedConfig.Authentication;
    }

    private void StartServer()
    {
        lock (_syncLock)
        {
            if (_stopping)
            {
                return;
            }

            _serverCts?.Dispose();
            _serverCts = new CancellationTokenSource();
            _proxyServer = new ProxyServer(_config, _loggerFactory);
            _serverTask = RunServerAsync(_proxyServer, _serverCts.Token);
        }
    }

    private async Task RunServerAsync(ProxyServer server, CancellationToken cancellationToken)
    {
        try
        {
            await server.StartAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray proxy server stopped with an unexpected error");
        }
        finally
        {
            var shouldRestart = false;

            lock (_syncLock)
            {
                if (!_stopping && _restartRequested)
                {
                    shouldRestart = true;
                    _restartRequested = false;
                }
            }

            if (shouldRestart)
            {
                StartServer();
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
