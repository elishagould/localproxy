using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace localproxy;

public static class ConnectTunnelHandler
{
    public static async Task HandleConnectTunnel(NetworkStream clientStream, string hostPort, HttpClient httpClient, SspiCredentialCache credentialCache, AuthenticatedConnectionPool connectionPool, ProxyConfiguration config, ILoggerFactory loggerFactory, ProxyExclusionMatcher exclusionMatcher)
    {
        var logger = loggerFactory.CreateLogger(typeof(ConnectTunnelHandler));
        
        try
        {
            var parts = hostPort.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
            {
                logger.LogWarning("Invalid CONNECT target: {HostPort}", hostPort);
                await HttpResponseWriter.WriteBadRequest(clientStream);
                return;
            }

            var host = parts[0];
            
            // Check if this host should bypass the proxy
            var shouldBypass = exclusionMatcher.ShouldBypassProxy(host, port);
            
            var targetUri = new Uri($"https://{host}:{port}");
            var upstreamProxy = HttpClient.DefaultProxy?.GetProxy(targetUri); ;
            var useUpstreamProxy = upstreamProxy != null && upstreamProxy.Host != host && !shouldBypass;

            if (shouldBypass)
            {
                logger.LogTrace("Host {Host}:{Port} matches exclusion list - using direct connection", host, port);
                await HandleDirectConnection(clientStream, host, port, config, logger);
            }
            else if (useUpstreamProxy)
            {
                logger.LogTrace("Using upstream proxy {ProxyHost}:{ProxyPort} for {Host}:{Port}", 
                    upstreamProxy.Host, upstreamProxy.Port, host, port);
                logger.LogDebug("Attempting to establish tunnel with Windows authentication");
                
                await ProxyAuthenticationHandler.AuthenticatedProxyConnectAsync(clientStream, host, port, upstreamProxy, credentialCache, connectionPool, config, loggerFactory);
            }
            else
            {
                await HandleDirectConnection(clientStream, host, port, config, logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CONNECT tunnel error");
            try
            {
                await HttpResponseWriter.WriteBadRequest(clientStream);
            }
            catch { }
        }
    }

    private static async Task HandleDirectConnection(NetworkStream clientStream, string host, int port, ProxyConfiguration config, ILogger logger)
    {
        logger.LogTrace("Direct connection to {Host}:{Port}", host, port);
        
        var targetClient = new TcpClient();
        await targetClient.ConnectAsync(host, port);
        var targetStream = targetClient.GetStream();

        var successResponse = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
        await clientStream.WriteAsync(successResponse, 0, successResponse.Length);
        await clientStream.FlushAsync();

        logger.LogTrace("Tunnel established to {Host}:{Port} (direct)", host, port);

        var clientToTarget = StreamCopier.CopyStreamAsync(clientStream, targetStream, targetClient, config.Proxy.BufferSize);
        var targetToClient = StreamCopier.CopyStreamAsync(targetStream, clientStream, targetClient, config.Proxy.BufferSize);

        await Task.WhenAny(clientToTarget, targetToClient);
        
        logger.LogTrace("Tunnel closed to {Host}:{Port}", host, port);
        targetClient.Dispose();
    }
}
