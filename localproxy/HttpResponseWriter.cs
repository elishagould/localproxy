using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace localproxy;

public static class HttpResponseWriter
{
    public static async Task WriteBadRequest(NetworkStream ns)
    {
        var sw = new StreamWriter(ns, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        await sw.WriteLineAsync("HTTP/1.1 400 Bad Request");
        await sw.WriteLineAsync();
    }

    public static async Task WriteResponse(NetworkStream ns, HttpResponseMessage response, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(HttpResponseWriter));

        var statusLine = $"HTTP/{response.Version.Major}.{response.Version.Minor} {(int)response.StatusCode} {response.ReasonPhrase}\r\n";
        var writer = new StreamWriter(ns, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteAsync(statusLine);

        // Write response headers, skipping hop-by-hop headers
        foreach (var header in response.Headers)
        {
            if (IsHopByHopHeader(header.Key))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                await writer.WriteLineAsync($"{header.Key}: {value}");
            }
        }

        if (response.Content != null)
        {
            foreach (var header in response.Content.Headers)
            {
                // Skip Transfer-Encoding since HttpClient has already decoded it
                if (IsHopByHopHeader(header.Key))
                {
                    continue;
                }

                foreach (var value in header.Value)
                {
                    await writer.WriteLineAsync($"{header.Key}: {value}");
                }
            }
        }

        await writer.WriteLineAsync();

        if (response.Content != null)
        {
            using var responseStream = await response.Content.ReadAsStreamAsync();
            var contentLengthStr = response.Content.Headers.ContentLength?.ToString() ?? "unknown";
            logger.LogDebug("Sending response body: {ContentLength} bytes", contentLengthStr);
            await responseStream.CopyToAsync(ns);
        }
    }

    private static bool IsHopByHopHeader(string headerName)
    {
        return headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);
    }
}
