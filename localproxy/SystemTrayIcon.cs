using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace localproxy;

public class SystemTrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ILogger<SystemTrayIcon> _logger;
    private readonly Action _onExit;
    private readonly TrayProxyRunner _trayRunner;
    private readonly ProxyConfiguration _config;
    private readonly ConnectionTracker _connectionTracker;
    private readonly ILoggerFactory _loggerFactory;
    private ConnectionsWindow? _connectionsWindow;

    public SystemTrayIcon(ProxyConfiguration config, TrayProxyRunner trayRunner, ConnectionTracker connectionTracker, ILoggerFactory loggerFactory, ILogger<SystemTrayIcon> logger, Action onExit)
    {
        _config = config;
        _trayRunner = trayRunner;
        _connectionTracker = connectionTracker;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _onExit = onExit;

        var listenerSummary = GetListenerSummary(config);

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = $"Simple Proxy - {listenerSummary}",
            Visible = true
        };

        var contextMenu = new ContextMenuStrip();
        
        // Status item (non-clickable)
        var statusItem = new ToolStripMenuItem($"Proxy listeners: {listenerSummary}")
        {
            Enabled = false
        };
        contextMenu.Items.Add(statusItem);
        
        contextMenu.Items.Add(new ToolStripSeparator());

        // Profile selection submenu
        var profileMenu = new ToolStripMenuItem("Select Proxy Profile");
        foreach (var profile in config.Proxy.Profiles)
        {
            var item = new ToolStripMenuItem(profile.Name)
            {
                Checked = profile.Name == config.Proxy.ActiveProfileName
            };
            item.Click += (s, e) => {
                config.Proxy.ActiveProfileName = profile.Name;
                foreach (ToolStripMenuItem mi in profileMenu.DropDownItems)
                    mi.Checked = false;
                item.Checked = true;
                _trayRunner.Reload();
                ShowBalloonTip("Profile Changed", $"Active profile set to: {profile.Name}", ToolTipIcon.Info);
            };
            profileMenu.DropDownItems.Add(item);
        }
        contextMenu.Items.Add(profileMenu);
        contextMenu.Items.Add(new ToolStripSeparator());

        // Show Connections
        var showConnectionsItem = new ToolStripMenuItem("Show Connections")
        {
            Image = null
        };
        showConnectionsItem.Click += ShowConnections_Click;
        contextMenu.Items.Add(showConnectionsItem);

        // Show Logs
        var showLogsItem = new ToolStripMenuItem("Show Logs")
        {
            Image = null
        };
        showLogsItem.Click += ShowLogs_Click;
        contextMenu.Items.Add(showLogsItem);

        // Open Configuration
        var openConfigItem = new ToolStripMenuItem("Open Configuration")
        {
            Image = null
        };
        openConfigItem.Click += OpenConfig_Click;
        contextMenu.Items.Add(openConfigItem);
        
        contextMenu.Items.Add(new ToolStripSeparator());
        
        // Exit
        var exitItem = new ToolStripMenuItem("Exit")
        {
            Image = null
        };
        exitItem.Click += Exit_Click;
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
        
        // Double-click to show status
        _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;

        _logger.LogInformation("System tray icon initialized");
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
    {
        ShowBalloonTip("Proxy Status", $"Proxy is running and accepting connections", ToolTipIcon.Info);
    }

    private void ShowConnections_Click(object? sender, EventArgs e)
    {
        if (_connectionsWindow is { IsDisposed: false })
        {
            _connectionsWindow.BringToFront();
            _connectionsWindow.Activate();
            return;
        }

        _connectionsWindow = new ConnectionsWindow(_connectionTracker, _config);
        _connectionsWindow.FormClosed += (_, _) => _connectionsWindow = null;
        _connectionsWindow.Show();
    }

    private void ShowLogs_Click(object? sender, EventArgs e)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (Directory.Exists(logPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", logPath);
            }
            else
            {
                ShowBalloonTip("Logs", "Log directory not found", ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open logs directory");
            ShowBalloonTip("Error", "Failed to open logs directory", ToolTipIcon.Error);
        }
    }

    private void OpenConfig_Click(object? sender, EventArgs e)
    {
        try
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (File.Exists(configPath))
            {
                System.Diagnostics.Process.Start("notepad.exe", configPath);
            }
            else
            {
                ShowBalloonTip("Configuration", "Configuration file not found", ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open configuration file");
            ShowBalloonTip("Error", "Failed to open configuration file", ToolTipIcon.Error);
        }
    }

    private void Exit_Click(object? sender, EventArgs e)
    {
        _logger.LogInformation("Exit requested from system tray");
        _onExit();
    }

    public void ShowBalloonTip(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3000, title, text, icon);
    }

    private static string GetListenerSummary(ProxyConfiguration config)
    {
        var listeners = config.Proxy.EffectiveListeners;
        if (listeners.Count == 0)
        {
            return "No listeners";
        }

        var entries = listeners
            .Select(l => $"{l.Bind}:{l.Port}")
            .ToArray();

        return string.Join(", ", entries);
    }

    private static Icon CreateIcon()
    {
        // Create a simple icon with a green circle
        var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            
            // Draw a green circle
            using (var brush = new SolidBrush(Color.FromArgb(76, 175, 80)))
            {
                g.FillEllipse(brush, 2, 2, 12, 12);
            }
            
            // Draw border
            using (var pen = new Pen(Color.FromArgb(56, 142, 60), 2))
            {
                g.DrawEllipse(pen, 2, 2, 12, 12);
            }
        }
        
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        if (_connectionsWindow is { IsDisposed: false })
        {
            _connectionsWindow.Close();
            _connectionsWindow.Dispose();
            _connectionsWindow = null;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _logger.LogInformation("System tray icon disposed");
    }
}
