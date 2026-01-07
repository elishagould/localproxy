using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace localproxy;

public class AuthenticatedConnectionPool
{
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private readonly Dictionary<string, TcpClient> _connections = new Dictionary<string, TcpClient>();
    private readonly ILogger<AuthenticatedConnectionPool> _logger;
    
    public AuthenticatedConnectionPool(ILogger<AuthenticatedConnectionPool> logger)
    {
        _logger = logger;
    }
    
    public async Task<TcpClient> GetConnectionAsync(string host, int port)
    {
        var key = $"{host}:{port}";
        await _semaphore.WaitAsync();
        try
        {
            if (_connections.TryGetValue(key, out var client))
            {
                if (client.Connected)
                {
                    _logger.LogDebug("Reusing existing connection to {Key}", key);
                    return client;
                }
                else
                {
                    _logger.LogDebug("Removing stale connection to {Key}", key);
                    client.Dispose();
                    _connections.Remove(key);
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }

        _logger.LogDebug("Creating new connection to {Key}", key);
        var newClient = new TcpClient();
        await newClient.ConnectAsync(host, port);
        
        return newClient;
    }

    public void ReturnConnection(string host, int port, TcpClient client)
    {
        var key = $"{host}:{port}";
        _semaphore.Wait();
        try
        {
            _connections[key] = client;
            _logger.LogDebug("Returned connection to pool for {Key}", key);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
