using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace localproxy;

public static class HttpRequestHandler
{
    public static async Task HandleHttpRequest(System.Net.Sockets.NetworkStream ns, StreamReader reader, WebHeaderCollection headers, string method, string uriPart, HttpClient proxyHttpClient, HttpClient directHttpClient, string clientEndpoint, ILoggerFactory loggerFactory, ProxyExclusionMatcher exclusionMatcher, ProxyExclusionMatcher blocklistMatcher, ConnectionTracker connectionTracker, ConnectionSessionHandle session)
    {
        var logger = loggerFactory.CreateLogger(typeof(HttpRequestHandler));
        
        Uri requestUri;
        if (Uri.IsWellFormedUriString(uriPart, UriKind.Absolute))
        {
            requestUri = new Uri(uriPart);
        }
        else
        {
            var host = headers["Host"];
            if (string.IsNullOrEmpty(host))
            {
                logger.LogWarning("Missing Host header from {ClientEndpoint}", clientEndpoint);
                await HttpResponseWriter.WriteBadRequest(ns);
                return;
            }
            var scheme = "http";
            requestUri = new Uri($"{scheme}://{host}{uriPart}");
        }

        connectionTracker.SetDestination(session, $"{requestUri.Host}:{requestUri.Port}");

        // Check if this host is blocked
        if (blocklistMatcher.ShouldBypassProxy(requestUri.Host, requestUri.Port))
        {
            logger.LogWarning("Host {Host}:{Port} is blocked by configuration", requestUri.Host, requestUri.Port);
            await HttpResponseWriter.WriteBadRequest(ns);
            return;
        }

        // Check if this host should bypass the proxy
        var shouldBypass = exclusionMatcher.ShouldBypassProxy(requestUri.Host, requestUri.Port);
        if (shouldBypass)
        {
            logger.LogTrace("Host {Host} matches exclusion list - forwarding directly", requestUri.Host);
        }

        logger.LogTrace("Forwarding {Method} to {Host}{PathAndQuery}", method, requestUri.Host, requestUri.PathAndQuery);

        var selectedHttpClient = shouldBypass ? directHttpClient : proxyHttpClient;
        var sendStart = DateTime.UtcNow;

        using var requestMessage = new HttpRequestMessage(new HttpMethod(method), requestUri);

        foreach (var key in headers.AllKeys)
        {
            var k = key;
            var v = headers[k];
            if (string.Equals(k, "Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "Connection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!requestMessage.Headers.TryAddWithoutValidation(k, v))
            {
                requestMessage.Content ??= new ByteArrayContent(Array.Empty<byte>());
                requestMessage.Content.Headers.TryAddWithoutValidation(k, v);
            }
        }

        if (int.TryParse(headers["Content-Length"], out var contentLength) && contentLength > 0)
        {
            logger.LogDebug("Reading request body: {ContentLength} bytes", contentLength);
            var bodyBytes = new byte[contentLength];
            var totalRead = 0;

            while (totalRead < contentLength)
            {
                var read = await reader.BaseStream.ReadAsync(bodyBytes, totalRead, contentLength - totalRead);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead != contentLength)
            {
                Array.Resize(ref bodyBytes, totalRead);
            }

            requestMessage.Content = new ByteArrayContent(bodyBytes);
        }

        using var response = await selectedHttpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

        var elapsedMs = (DateTime.UtcNow - sendStart).TotalMilliseconds;
        logger.LogTrace("HTTP upstream {Method} {Uri} -> {StatusCode} in {ElapsedMs}ms via {Route}",
            method,
            requestUri,
            (int)response.StatusCode,
            elapsedMs,
            shouldBypass ? "direct" : "proxy");

        logger.LogTrace("Response: {StatusCode} {ReasonPhrase}", (int)response.StatusCode, response.ReasonPhrase);

        await HttpResponseWriter.WriteResponse(ns, response, loggerFactory);

        logger.LogTrace("Completed request to {Host}", requestUri.Host);
    }
}
