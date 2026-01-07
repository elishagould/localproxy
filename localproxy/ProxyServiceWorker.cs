using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace localproxy;

public class ProxyServiceWorker : BackgroundService
{
    private readonly ProxyConfiguration _config;
    private readonly ILogger<ProxyServiceWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private ProxyServer? _proxyServer;

    public ProxyServiceWorker(ProxyConfiguration config, ILoggerFactory loggerFactory, ILogger<ProxyServiceWorker> logger)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Proxy service is starting");

            _proxyServer = new ProxyServer(_config, _loggerFactory);

            // Register cancellation handler
            stoppingToken.Register(() =>
            {
                _logger.LogInformation("Proxy service is stopping");
                _proxyServer?.Stop();
            });

            await _proxyServer.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Proxy service stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in proxy service");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Proxy service is stopping gracefully");
        _proxyServer?.Stop();
        await base.StopAsync(cancellationToken);
    }
}
