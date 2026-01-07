using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace localproxy;

public static class ProxyAuthenticationHandler
{
    public static async Task AuthenticatedProxyConnectAsync(NetworkStream clientStream, string targetHost, int targetPort, Uri proxyUri, SspiCredentialCache credentialCache, AuthenticatedConnectionPool connectionPool, ProxyConfiguration config, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(ProxyAuthenticationHandler));
        var targetClient = new TcpClient();
        
        try
        {
            logger.LogTrace("Connecting to proxy {ProxyHost}:{ProxyPort}", proxyUri.Host, proxyUri.Port);
            await targetClient.ConnectAsync(proxyUri.Host, proxyUri.Port);
            var targetStream = targetClient.GetStream();

            var connectRequest = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n";
            connectRequest += $"Host: {targetHost}:{targetPort}\r\n";
            connectRequest += $"User-Agent: SimpleProxy/1.0\r\n";
            connectRequest += $"Proxy-Connection: Keep-Alive\r\n";
            connectRequest += "\r\n";

            logger.LogDebug("Sending CONNECT request");
            var requestBytes = Encoding.ASCII.GetBytes(connectRequest);
            await targetStream.WriteAsync(requestBytes, 0, requestBytes.Length);
            await targetStream.FlushAsync();

            var reader = new StreamReader(targetStream, Encoding.ASCII, leaveOpen: true);
            var responseLine = await reader.ReadLineAsync();
            
            logger.LogDebug("Proxy response: {ResponseLine}", responseLine);

            var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var authMethods = new List<string>();
            string line;
            var responseBody = new StringBuilder();
            bool isHtmlResponse = false;
            
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                logger.LogDebug("Response header: {Header}", line);
                var idx = line.IndexOf(':');
                if (idx > 0)
                {
                    var name = line.Substring(0, idx).Trim();
                    var value = line.Substring(idx + 1).Trim();
                    
                    if (name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase))
                    {
                        authMethods.Add(value);
                    }
                    
                    if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && 
                        value.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                    {
                        isHtmlResponse = true;
                    }
                    
                    responseHeaders[name] = value;
                }
            }

            await DiscardHtmlBody(reader, isHtmlResponse, responseHeaders, logger);

            if (responseLine != null && responseLine.Contains("200"))
            {
                await EstablishTunnel(clientStream, targetStream, targetClient, targetHost, targetPort, config, logger);
            }
            else if (responseLine != null && responseLine.Contains("407"))
            {
                await HandleAuthenticationRequired(clientStream, targetStream, targetClient, targetHost, targetPort, proxyUri, credentialCache, authMethods, config, loggerFactory);
            }
            else
            {
                logger.LogWarning("Unexpected proxy response");
                await HttpResponseWriter.WriteBadRequest(clientStream);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Authenticated proxy connect error");
            throw;
        }
        finally
        {
            targetClient.Dispose();
        }
    }

    private static async Task DiscardHtmlBody(StreamReader reader, bool isHtmlResponse, Dictionary<string, string> responseHeaders, ILogger logger)
    {
        if (isHtmlResponse && responseHeaders.TryGetValue("Content-Length", out var contentLengthStr))
        {
            if (int.TryParse(contentLengthStr, out var contentLength) && contentLength > 0)
            {
                var bodyBuffer = new char[4096];
                var totalRead = 0;
                while (totalRead < contentLength)
                {
                    var toRead = Math.Min(bodyBuffer.Length, contentLength - totalRead);
                    var read = await reader.ReadAsync(bodyBuffer, 0, toRead);
                    if (read == 0) break;
                    totalRead += read;
                }
                logger.LogDebug("Discarded {BytesRead} bytes of HTML response", totalRead);
            }
        }
    }

    private static async Task EstablishTunnel(NetworkStream clientStream, NetworkStream targetStream, TcpClient targetClient, string targetHost, int targetPort, ProxyConfiguration config, ILogger logger)
    {
        var successResponse = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
        await clientStream.WriteAsync(successResponse, 0, successResponse.Length);
        await clientStream.FlushAsync();

        logger.LogTrace("Tunnel established to {Host}:{Port} via proxy", targetHost, targetPort);

        var clientToTarget = StreamCopier.CopyStreamAsync(clientStream, targetStream, targetClient, config.Proxy.BufferSize);
        var targetToClient = StreamCopier.CopyStreamAsync(targetStream, clientStream, targetClient, config.Proxy.BufferSize);

        await Task.WhenAny(clientToTarget, targetToClient);
        logger.LogTrace("Tunnel closed to {Host}:{Port}", targetHost, targetPort);
    }

    private static async Task HandleAuthenticationRequired(NetworkStream clientStream, NetworkStream targetStream, TcpClient targetClient, string targetHost, int targetPort, Uri proxyUri, SspiCredentialCache credentialCache, List<string> authMethods, ProxyConfiguration config, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(ProxyAuthenticationHandler));
        logger.LogTrace("Proxy requires authentication (407)");
        
        if (authMethods.Count > 0)
        {
            logger.LogTrace("Supported auth methods ({Count}): {Methods}", authMethods.Count, string.Join("; ", authMethods));
            
            bool hasNtlmOrNegotiate = authMethods.Any(m => 
                m.StartsWith("NTLM", StringComparison.OrdinalIgnoreCase) || 
                m.StartsWith("Negotiate", StringComparison.OrdinalIgnoreCase));
            
            if (hasNtlmOrNegotiate)
            {
                await PerformNtlmAuthentication(clientStream, targetHost, targetPort, proxyUri, credentialCache, authMethods, config, loggerFactory);
            }
            else
            {
                logger.LogError("No NTLM/Negotiate auth available. Available methods: {Methods}", string.Join(", ", authMethods));
                await HttpResponseWriter.WriteBadRequest(clientStream);
            }
        }
        else
        {
            logger.LogWarning("No authentication methods advertised");
            await HttpResponseWriter.WriteBadRequest(clientStream);
        }
    }

    private static async Task PerformNtlmAuthentication(NetworkStream clientStream, string targetHost, int targetPort, Uri proxyUri, SspiCredentialCache credentialCache, List<string> authMethods, ProxyConfiguration config, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(ProxyAuthenticationHandler));
        var targetClient = new TcpClient();
        
        try
        {
            await targetClient.ConnectAsync(proxyUri.Host, proxyUri.Port);
            var targetStream = targetClient.GetStream();
            
            var cachedScheme = credentialCache.GetAuthScheme(proxyUri.Host, proxyUri.Port);
            var authScheme = cachedScheme ?? (authMethods.Any(m => m.StartsWith("NTLM", StringComparison.OrdinalIgnoreCase)) 
                ? "NTLM"
                : "Negotiate");
            
            if (cachedScheme != null)
            {
                logger.LogTrace("Using cached auth scheme: {AuthScheme}", authScheme);
            }
            else
            {
                logger.LogTrace("Performing {AuthScheme} authentication", authScheme);
            }
            
            bool success = await NtlmAuthenticator.PerformNtlmHandshake(targetStream, targetHost, targetPort, authScheme, proxyUri, credentialCache, loggerFactory);
            
            if (success)
            {
                await EstablishTunnel(clientStream, targetStream, targetClient, targetHost, targetPort, config, logger);
            }
            else
            {
                logger.LogWarning("Authentication failed");
                await HttpResponseWriter.WriteBadRequest(clientStream);
            }
        }
        finally
        {
            targetClient.Dispose();
        }
    }
}
