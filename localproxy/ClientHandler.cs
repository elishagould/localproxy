using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace localproxy;

public static class ClientHandler
{
    public static async Task HandleClientAsync(TcpClient client, HttpClient httpClient, SspiCredentialCache credentialCache, AuthenticatedConnectionPool connectionPool, ProxyConfiguration config, ILoggerFactory loggerFactory, ProxyExclusionMatcher exclusionMatcher, ProxyExclusionMatcher blocklistMatcher)
    {
        var logger = loggerFactory.CreateLogger(typeof(ClientHandler));
        var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        
        using (client)
        {
            using var ns = client.GetStream();
            try
            {
                var reader = new StreamReader(ns, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(requestLine)) return;

                logger.LogTrace("Request from {ClientEndpoint}: {RequestLine}", clientEndpoint, requestLine);

                string line;
                var headers = new WebHeaderCollection();
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    var idx = line.IndexOf(':');
                    if (idx > 0)
                    {
                        var name = line.Substring(0, idx).Trim();
                        var value = line.Substring(idx + 1).Trim();
                        headers.Add(name, value);
                    }
                }

                var parts = requestLine.Split(' ');
                if (parts.Length < 3)
                {
                    logger.LogWarning("Bad request from {ClientEndpoint}", clientEndpoint);
                    await HttpResponseWriter.WriteBadRequest(ns);
                    return;
                }

                var method = parts[0];
                var uriPart = parts[1];

                if (string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogTrace("CONNECT tunnel to {Target}", uriPart);
                    await ConnectTunnelHandler.HandleConnectTunnel(ns, uriPart, httpClient, credentialCache, connectionPool, config, loggerFactory, exclusionMatcher, blocklistMatcher);
                    return;
                }

                await HttpRequestHandler.HandleHttpRequest(ns, reader, headers, method, uriPart, httpClient, clientEndpoint, loggerFactory, exclusionMatcher, blocklistMatcher);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error from {ClientEndpoint}", clientEndpoint);
                try
                {
                    var sw = new StreamWriter(ns, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                    await sw.WriteLineAsync("HTTP/1.1 500 Internal Server Error");
                    await sw.WriteLineAsync();
                }
                catch { }
            }
        }
    }
}
