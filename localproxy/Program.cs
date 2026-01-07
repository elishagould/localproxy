using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using localproxy;
using System.Windows.Forms;

// Build configuration
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

var config = new ProxyConfiguration();
configuration.Bind(config);

// Configure Serilog
var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Information();

if (config.Logging.Console.Enabled)
{
    loggerConfig.WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
}

if (config.Logging.File.Enabled)
{
    loggerConfig.WriteTo.File(
        config.Logging.File.Path,
        rollingInterval: Enum.Parse<RollingInterval>(config.Logging.File.RollingInterval),
        retainedFileCountLimit: config.Logging.File.RetainedFileCountLimit,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {Message:lj}{NewLine}{Exception}");
}

Log.Logger = loggerConfig.CreateLogger();

try
{
    Log.Information("Starting application");
    
    // Check run mode from arguments
    var runAsConsole = args.Contains("--console");
    var runAsService = args.Contains("--service");
    var runAsTray = !runAsConsole && !runAsService && Environment.UserInteractive;

    if (runAsTray)
    {
        Log.Information("Running in system tray mode");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        
        var host = CreateHostBuilder(args, config).Build();
        
        using var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<SystemTrayIcon>();
        
        // Start the proxy in background
        _ = host.RunAsync();
        
        // Show balloon tip
        using var trayIcon = new SystemTrayIcon(config, logger, () => 
        {
            Application.Exit();
        });
        
        //trayIcon.ShowBalloonTip("Proxy Started", $"Simple Proxy is running on port {config.Proxy.Port}", System.Windows.Forms.ToolTipIcon.Info);
        
        Application.Run();
        
        // Cleanup
        await host.StopAsync(TimeSpan.FromSeconds(5));
        host.Dispose();
    }
    else
    {
        var host = CreateHostBuilder(args, config).Build();
        
        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        
        if (runAsService)
        {
            logger.LogInformation("Running as Windows Service");
        }
        else
        {
            logger.LogInformation("Running in console mode");
            logger.LogInformation("Press Ctrl+C to stop");
        }

        await host.RunAsync();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;

static IHostBuilder CreateHostBuilder(string[] args, ProxyConfiguration config)
{
    var builder = Host.CreateDefaultBuilder(args);

    // Configure logging
    builder.ConfigureLogging((context, loggingBuilder) =>
    {
        loggingBuilder.ClearProviders();
        loggingBuilder.AddSerilog(Log.Logger, dispose: true);
    });

    // Add configuration
    builder.ConfigureServices((context, services) =>
    {
        services.AddSingleton(config);
    });

    // Configure Windows Service support
    if (OperatingSystem.IsWindows())
    {
        builder.UseWindowsService(options =>
        {
            options.ServiceName = "SimpleProxyService";
        });
    }

    // Add the proxy worker
    builder.ConfigureServices((context, services) =>
    {
        services.AddHostedService<ProxyServiceWorker>();
    });

    return builder;
}
