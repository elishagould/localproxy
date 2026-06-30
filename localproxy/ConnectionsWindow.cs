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

        var existingRows = new Dictionary<Guid, DataGridViewRow>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is Guid sessionId)
            {
                existingRows[sessionId] = row;
            }
        }

        foreach (var session in sessions)
        {
            if (existingRows.TryGetValue(session.SessionId, out var existingRow))
            {
                UpdateRow(existingRow, session);
                existingRows.Remove(session.SessionId);
                continue;
            }

            var rowIndex = _grid.Rows.Add();
            var row = _grid.Rows[rowIndex];
            row.Tag = session.SessionId;
            UpdateRow(row, session);
        }

        foreach (var staleRow in existingRows.Values.ToList())
        {
            _grid.Rows.Remove(staleRow);
        }
    }

    private void UpdateRow(DataGridViewRow row, ConnectionSessionSnapshot session)
    {
        row.Cells["State"].Value = session.IsActive ? "Active" : "Inactive";
        row.Cells["Protocol"].Value = session.Protocol.ToString();
        row.Cells["Source"].Value = session.Source;
        row.Cells["Destination"].Value = session.Destination;
        row.Cells["BytesUp"].Value = FormatBytes(session.BytesClientToTarget);
        row.Cells["BytesDown"].Value = FormatBytes(session.BytesTargetToClient);
        row.Cells["BytesTotal"].Value = FormatBytes(session.TotalBytes);
        row.Cells["ConnectedAt"].Value = session.ConnectedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        row.Cells["DisconnectedAt"].Value = session.DisconnectedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
    }

    private static string FormatBytes(long? bytes)
    {
        return bytes.HasValue ? bytes.Value.ToString("N0") : "N/A";
    }
}
