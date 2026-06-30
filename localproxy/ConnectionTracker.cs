using System.Collections.Concurrent;

namespace localproxy;

public enum ConnectionProtocol
{
    Http,
    ConnectTunnel,
    Socks5
}

public sealed class ConnectionSessionSnapshot
{
    public required Guid SessionId { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public required ConnectionProtocol Protocol { get; init; }
    public required bool IsActive { get; init; }
    public required DateTime ConnectedAtUtc { get; init; }
    public DateTime? DisconnectedAtUtc { get; init; }
    public long? BytesClientToTarget { get; init; }
    public long? BytesTargetToClient { get; init; }
    public long? TotalBytes => BytesClientToTarget.HasValue && BytesTargetToClient.HasValue
        ? BytesClientToTarget.Value + BytesTargetToClient.Value
        : null;
}

public sealed class ConnectionSessionHandle
{
    internal ConnectionSessionHandle(Guid sessionId)
    {
        SessionId = sessionId;
    }

    public Guid SessionId { get; }
}

internal sealed class ConnectionSessionState
{
    public required Guid SessionId { get; init; }
    public required string Source { get; set; }
    public string Destination { get; set; } = "pending";
    public required ConnectionProtocol Protocol { get; init; }
    public DateTime ConnectedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? DisconnectedAtUtc { get; set; }
    public long? BytesClientToTarget { get; set; }
    public long? BytesTargetToClient { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, ConnectionSessionState> _sessions = new();

    public ConnectionSessionHandle RegisterSession(string source, ConnectionProtocol protocol)
    {
        var sessionId = Guid.NewGuid();
        var state = new ConnectionSessionState
        {
            SessionId = sessionId,
            Source = source,
            Protocol = protocol,
            ConnectedAtUtc = DateTime.UtcNow,
            IsActive = true
        };

        _sessions[sessionId] = state;
        return new ConnectionSessionHandle(sessionId);
    }

    public void SetDestination(ConnectionSessionHandle handle, string destination)
    {
        if (_sessions.TryGetValue(handle.SessionId, out var session))
        {
            session.Destination = destination;
        }
    }

    public void AddClientToTargetBytes(ConnectionSessionHandle handle, long bytes)
    {
        if (bytes <= 0 || !_sessions.TryGetValue(handle.SessionId, out var session))
        {
            return;
        }

        session.BytesClientToTarget = (session.BytesClientToTarget ?? 0) + bytes;
    }

    public void AddTargetToClientBytes(ConnectionSessionHandle handle, long bytes)
    {
        if (bytes <= 0 || !_sessions.TryGetValue(handle.SessionId, out var session))
        {
            return;
        }

        session.BytesTargetToClient = (session.BytesTargetToClient ?? 0) + bytes;
    }

    public void MarkDisconnected(ConnectionSessionHandle handle)
    {
        if (_sessions.TryGetValue(handle.SessionId, out var session) && session.IsActive)
        {
            session.IsActive = false;
            session.DisconnectedAtUtc = DateTime.UtcNow;
        }
    }

    public IReadOnlyList<ConnectionSessionSnapshot> GetSnapshot(TimeSpan inactiveRetention)
    {
        var now = DateTime.UtcNow;
        PurgeExpired(now, inactiveRetention);

        return _sessions.Values
            .Select(s => new ConnectionSessionSnapshot
            {
                SessionId = s.SessionId,
                Source = s.Source,
                Destination = s.Destination,
                Protocol = s.Protocol,
                IsActive = s.IsActive,
                ConnectedAtUtc = s.ConnectedAtUtc,
                DisconnectedAtUtc = s.DisconnectedAtUtc,
                BytesClientToTarget = s.BytesClientToTarget,
                BytesTargetToClient = s.BytesTargetToClient
            })
            .OrderByDescending(s => s.ConnectedAtUtc)
            .ToList();
    }

    private void PurgeExpired(DateTime nowUtc, TimeSpan inactiveRetention)
    {
        var retention = inactiveRetention < TimeSpan.Zero ? TimeSpan.Zero : inactiveRetention;

        foreach (var kvp in _sessions)
        {
            var session = kvp.Value;
            if (session.IsActive || session.DisconnectedAtUtc is null)
            {
                continue;
            }

            if (nowUtc - session.DisconnectedAtUtc.Value > retention)
            {
                _sessions.TryRemove(kvp.Key, out _);
            }
        }
    }
}
