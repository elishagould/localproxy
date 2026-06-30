using System.Windows.Forms;

namespace localproxy;

public sealed class ConnectionsWindow : Form
{
    private readonly ConnectionTracker _connectionTracker;
    private readonly ProxyConfiguration _config;
    private readonly DataGridView _grid;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public ConnectionsWindow(ConnectionTracker connectionTracker, ProxyConfiguration config)
    {
        _connectionTracker = connectionTracker;
        _config = config;

        Text = "Proxy Connections";
        Width = 1100;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "State", Width = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Protocol", HeaderText = "Protocol", Width = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Source", Width = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Destination", HeaderText = "Destination", Width = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "BytesUp", HeaderText = "Bytes Up", Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "BytesDown", HeaderText = "Bytes Down", Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "BytesTotal", HeaderText = "Bytes Total", Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ConnectedAt", HeaderText = "Connected", Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DisconnectedAt", HeaderText = "Disconnected", Width = 150 });

        Controls.Add(_grid);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshRows();
        _refreshTimer.Start();

        RefreshRows();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RefreshRows()
    {
        var retention = TimeSpan.FromSeconds(Math.Max(0, _config.ConnectionMonitor.InactiveConnectionRetentionSeconds));
        var sessions = _connectionTracker.GetSnapshot(retention);

        _grid.Rows.Clear();

        foreach (var s in sessions)
        {
            _grid.Rows.Add(
                s.IsActive ? "Active" : "Inactive",
                s.Protocol.ToString(),
                s.Source,
                s.Destination,
                FormatBytes(s.BytesClientToTarget),
                FormatBytes(s.BytesTargetToClient),
                FormatBytes(s.TotalBytes),
                s.ConnectedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                s.DisconnectedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty);
        }
    }

    private static string FormatBytes(long? bytes)
    {
        return bytes.HasValue ? bytes.Value.ToString("N0") : "N/A";
    }
}
