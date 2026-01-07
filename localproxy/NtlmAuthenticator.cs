using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace localproxy;

public static class NtlmAuthenticator
{
    public static async Task<bool> PerformNtlmHandshake(NetworkStream proxyStream, string targetHost, int targetPort, string authScheme, Uri proxyUri, SspiCredentialCache credentialCache, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(NtlmAuthenticator));
        
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            logger.LogDebug("Using Windows identity: {Identity}", identity.Name);

            var sspiHelper = new SspiHelper(authScheme);
            
            byte[] type1Token = sspiHelper.GetClientToken(null);
            var type1Base64 = Convert.ToBase64String(type1Token);
            
            logger.LogDebug("Type 1 Token ({Length} bytes)", type1Token.Length);
            
            await SendType1Message(proxyStream, targetHost, targetPort, authScheme, type1Base64, logger);

            var reader = new StreamReader(proxyStream, Encoding.ASCII, leaveOpen: true);
            var (responseLine, authHeaders, isHtmlResponse, responseHeaders) = await ReadProxyResponse(reader);
            
            logger.LogDebug("Type 2 response: {ResponseLine}", responseLine);

            await DiscardHtmlBody(reader, isHtmlResponse, responseHeaders, logger);

            if (!ValidateType2Response(responseLine, authHeaders, sspiHelper, logger))
            {
                sspiHelper.Dispose();
                return false;
            }

            var type2Base64 = ExtractChallengeToken(authHeaders, authScheme);
            if (type2Base64 == null)
            {
                logger.LogWarning("No {AuthScheme} challenge token found", authScheme);
                sspiHelper.Dispose();
                return false;
            }

            byte[] type2Token = Convert.FromBase64String(type2Base64);
            logger.LogDebug("Decoded Type 2 token: {Length} bytes", type2Token.Length);

            byte[] type3Token = sspiHelper.GetClientToken(type2Token);
            var type3Base64 = Convert.ToBase64String(type3Token);
            
            logger.LogDebug("Type 3 Token ({Length} bytes)", type3Token.Length);

            await SendType3Message(proxyStream, targetHost, targetPort, authScheme, type3Base64, logger);

            var finalResponse = await reader.ReadLineAsync();
            logger.LogTrace("Final response: {FinalResponse}", finalResponse);

            string line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) { }

            sspiHelper.Dispose();

            if (finalResponse != null && finalResponse.Contains("200"))
            {
                logger.LogTrace("Authentication successful!");
                credentialCache.CacheAuthScheme(proxyUri.Host, proxyUri.Port, authScheme);
                return true;
            }
            else
            {
                logger.LogWarning("Authentication failed");
                return false;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NTLM handshake error");
            return false;
        }
    }

    private static async Task SendType1Message(NetworkStream proxyStream, string targetHost, int targetPort, string authScheme, string type1Base64, ILogger logger)
    {
        var authRequest = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n";
        authRequest += $"Host: {targetHost}:{targetPort}\r\n";
        authRequest += $"Proxy-Authorization: {authScheme} {type1Base64}\r\n";
        authRequest += $"Proxy-Connection: Keep-Alive\r\n";
        authRequest += "\r\n";

        logger.LogDebug("Sending {AuthScheme} Type 1 message", authScheme);
        var requestBytes = Encoding.ASCII.GetBytes(authRequest);
        await proxyStream.WriteAsync(requestBytes, 0, requestBytes.Length);
        await proxyStream.FlushAsync();
    }

    private static async Task SendType3Message(NetworkStream proxyStream, string targetHost, int targetPort, string authScheme, string type3Base64, ILogger logger)
    {
        var type3Request = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n";
        type3Request += $"Host: {targetHost}:{targetPort}\r\n";
        type3Request += $"Proxy-Authorization: {authScheme} {type3Base64}\r\n";
        type3Request += $"Proxy-Connection: Keep-Alive\r\n";
        type3Request += "\r\n";

        logger.LogDebug("Sending {AuthScheme} Type 3 message", authScheme);
        var type3Bytes = Encoding.ASCII.GetBytes(type3Request);
        await proxyStream.WriteAsync(type3Bytes, 0, type3Bytes.Length);
        await proxyStream.FlushAsync();
    }

    private static async Task<(string responseLine, List<string> authHeaders, bool isHtmlResponse, Dictionary<string, string> responseHeaders)> ReadProxyResponse(StreamReader reader)
    {
        var responseLine = await reader.ReadLineAsync();
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var authHeaders = new List<string>();
        bool isHtmlResponse = false;
        
        string line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            var idx = line.IndexOf(':');
            if (idx > 0)
            {
                var name = line.Substring(0, idx).Trim();
                var value = line.Substring(idx + 1).Trim();
                
                if (name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase))
                {
                    authHeaders.Add(value);
                }
                
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && 
                    value.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    isHtmlResponse = true;
                }
                
                responseHeaders[name] = value;
            }
        }

        return (responseLine, authHeaders, isHtmlResponse, responseHeaders);
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

    private static bool ValidateType2Response(string responseLine, List<string> authHeaders, SspiHelper sspiHelper, ILogger logger)
    {
        if (responseLine == null || !responseLine.Contains("407"))
        {
            logger.LogWarning("Unexpected response during authentication. Expected 407, got: {ResponseLine}", responseLine);
            return false;
        }

        if (authHeaders.Count == 0)
        {
            logger.LogError("No Proxy-Authenticate headers in 407 response");
            return false;
        }

        return true;
    }

    private static string ExtractChallengeToken(List<string> authHeaders, string authScheme)
    {
        foreach (var authValue in authHeaders)
        {
            if (authValue.StartsWith(authScheme, StringComparison.OrdinalIgnoreCase))
            {
                var parts = authValue.Split(new[] { ' ' }, 2);
                if (parts.Length >= 2)
                {
                    return parts[1].Trim();
                }
            }
        }
        return null;
    }
}
