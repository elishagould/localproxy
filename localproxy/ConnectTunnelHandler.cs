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
    public static async Task HandleConnectTunnel(NetworkStream clientStream, string hostPort, HttpClient httpClient, SspiCredentialCache credentialCache, AuthenticatedConnectionPool connectionPool, ProxyConfiguration config, ILoggerFactory loggerFactory, ProxyExclusionMatcher exclusionMatcher, ProxyExclusionMatcher blocklistMatcher)
    {
        var logger = loggerFactory.CreateLogger(typeof(ConnectTunnelHandler));

        try
        {
            if (!TryParseHostPort(hostPort, out var host, out var port))
            {
                logger.LogWarning("Invalid CONNECT target: {HostPort}", hostPort);
                await HttpResponseWriter.WriteBadRequest(clientStream);
                return;
            }

            if (blocklistMatcher.ShouldBypassProxy(host, port))
            {
                logger.LogWarning("Host {Host}:{Port} is blocked by configuration", host, port);
                await HttpResponseWriter.WriteBadRequest(clientStream);
                return;
            }

            var shouldBypass = exclusionMatcher.ShouldBypassProxy(host, port);

            var targetUri = new Uri($"https://{host}:{port}");
            var upstreamProxy = HttpClient.DefaultProxy?.GetProxy(targetUri);
            var useUpstreamProxy = upstreamProxy != null && upstreamProxy.Host != host && !shouldBypass;

            if (shouldBypass)
            {
                logger.LogTrace("Host {Host}:{Port} matches exclusion list - using direct connection", host, port);
                await HandleDirectConnection(clientStream, host, port, config, logger);
            }
            else if (useUpstreamProxy)
            {
                logger.LogTrace("Using upstream proxy {ProxyScheme}://{ProxyHost}:{ProxyPort} for {Host}:{Port}",
                    upstreamProxy.Scheme, upstreamProxy.Host, upstreamProxy.Port, host, port);

                if (IsSocksProxy(upstreamProxy))
                {
                    await HandleSocksProxyConnection(clientStream, host, port, upstreamProxy, config, logger);
                }
                else
                {
                    logger.LogDebug("Attempting to establish tunnel with Windows authentication");
                    await ProxyAuthenticationHandler.AuthenticatedProxyConnectAsync(clientStream, host, port, upstreamProxy, credentialCache, connectionPool, config, loggerFactory);
                }
            }
            else
            {
                await HandleDirectConnection(clientStream, host, port, config, logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "CONNECT tunnel error: {HostPort}", hostPort);
            try
            {
                await HttpResponseWriter.WriteBadRequest(clientStream);
            }
            catch { }
        }
    }

    private static bool TryParseHostPort(string hostPort, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (string.IsNullOrWhiteSpace(hostPort))
            return false;

        if (hostPort.StartsWith("[", StringComparison.Ordinal))
        {
            var closeBracket = hostPort.IndexOf(']');
            if (closeBracket <= 1 || closeBracket >= hostPort.Length - 2 || hostPort[closeBracket + 1] != ':')
                return false;

            host = hostPort.Substring(1, closeBracket - 1);
            return int.TryParse(hostPort[(closeBracket + 2)..], out port);
        }

        var colonIndex = hostPort.LastIndexOf(':');
        if (colonIndex <= 0)
            return false;

        host = hostPort[..colonIndex];
        return int.TryParse(hostPort[(colonIndex + 1)..], out port);
    }

    private static bool IsSocksProxy(Uri proxyUri)
    {
        return proxyUri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task HandleSocksProxyConnection(NetworkStream clientStream, string host, int port, Uri proxyUri, ProxyConfiguration config, ILogger logger)
    {
        try
        {
            var proxyClient = new TcpClient();
            await proxyClient.ConnectAsync(proxyUri.Host, proxyUri.Port);
            var proxyStream = proxyClient.GetStream();

            if (proxyUri.Scheme.Equals("socks4", StringComparison.OrdinalIgnoreCase) ||
                proxyUri.Scheme.Equals("socks4a", StringComparison.OrdinalIgnoreCase))
            {
                await PerformSocks4Connect(proxyStream, host, port);
            }
            else
            {
                await PerformSocks5Connect(proxyStream, host, port);
            }

            var successResponse = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
            await clientStream.WriteAsync(successResponse, 0, successResponse.Length);
            await clientStream.FlushAsync();

            logger.LogTrace("Tunnel established to {Host}:{Port} via SOCKS proxy {ProxyHost}:{ProxyPort}", host, port, proxyUri.Host, proxyUri.Port);

            var clientToTarget = StreamCopier.CopyStreamAsync(clientStream, proxyStream, proxyClient, config.Proxy.BufferSize);
            var targetToClient = StreamCopier.CopyStreamAsync(proxyStream, clientStream, proxyClient, config.Proxy.BufferSize);

            await Task.WhenAny(clientToTarget, targetToClient);

            logger.LogTrace("Tunnel closed to {Host}:{Port}", host, port);
            proxyClient.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "SOCKS proxy connection error to {Host}:{Port}", host, port);
            try
            {
                await HttpResponseWriter.WriteBadRequest(clientStream);
            }
            catch { }
        }
    }

    private static async Task PerformSocks5Connect(NetworkStream proxyStream, string host, int port)
    {
        await proxyStream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        await proxyStream.FlushAsync();

        var authReply = new byte[2];
        await ReadExactAsync(proxyStream, authReply);

        if (authReply[0] != 0x05 || authReply[1] == 0xFF)
            throw new InvalidOperationException("SOCKS5 server rejected supported authentication methods");

        if (authReply[1] != 0x00)
            throw new InvalidOperationException($"Unsupported SOCKS5 authentication method: 0x{authReply[1]:X2}");

        byte atyp;
        byte[] addressBytes;

        if (IPAddress.TryParse(host, out var ipAddress))
        {
            addressBytes = ipAddress.GetAddressBytes();
            atyp = ipAddress.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04;
        }
        else
        {
            var hostBytes = Encoding.ASCII.GetBytes(host);
            if (hostBytes.Length > byte.MaxValue)
                throw new InvalidOperationException("SOCKS5 domain name is too long");

            atyp = 0x03;
            addressBytes = new byte[hostBytes.Length + 1];
            addressBytes[0] = (byte)hostBytes.Length;
            Buffer.BlockCopy(hostBytes, 0, addressBytes, 1, hostBytes.Length);
        }

        var connectRequest = new byte[4 + addressBytes.Length + 2];
        connectRequest[0] = 0x05;
        connectRequest[1] = 0x01;
        connectRequest[2] = 0x00;
        connectRequest[3] = atyp;
        Buffer.BlockCopy(addressBytes, 0, connectRequest, 4, addressBytes.Length);
        connectRequest[^2] = (byte)((port >> 8) & 0xFF);
        connectRequest[^1] = (byte)(port & 0xFF);

        await proxyStream.WriteAsync(connectRequest);
        await proxyStream.FlushAsync();

        var replyHeader = new byte[4];
        await ReadExactAsync(proxyStream, replyHeader);

        if (replyHeader[0] != 0x05)
            throw new InvalidOperationException("Invalid SOCKS5 reply version");

        if (replyHeader[1] != 0x00)
            throw new InvalidOperationException($"SOCKS5 connect failed with status 0x{replyHeader[1]:X2}");

        var addrLen = replyHeader[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => await ReadSocks5DomainLength(proxyStream),
            _ => throw new InvalidOperationException("Unsupported SOCKS5 address type in reply")
        };

        var tail = new byte[addrLen + 2];
        await ReadExactAsync(proxyStream, tail);
    }

    private static async Task<int> ReadSocks5DomainLength(NetworkStream proxyStream)
    {
        var lengthByte = new byte[1];
        await ReadExactAsync(proxyStream, lengthByte);
        return lengthByte[0];
    }

    private static async Task PerformSocks4Connect(NetworkStream proxyStream, string host, int port)
    {
        var request = new MemoryStream();
        request.WriteByte(0x04);
        request.WriteByte(0x01);
        request.WriteByte((byte)((port >> 8) & 0xFF));
        request.WriteByte((byte)(port & 0xFF));

        if (IPAddress.TryParse(host, out var ipAddress) && ipAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            request.Write(ipAddress.GetAddressBytes());
            request.WriteByte(0x00);
        }
        else
        {
            request.Write(new byte[] { 0x00, 0x00, 0x00, 0x01 });
            request.WriteByte(0x00);
            var hostBytes = Encoding.ASCII.GetBytes(host);
            request.Write(hostBytes, 0, hostBytes.Length);
            request.WriteByte(0x00);
        }

        await proxyStream.WriteAsync(request.ToArray());
        await proxyStream.FlushAsync();

        var reply = new byte[8];
        await ReadExactAsync(proxyStream, reply);

        if (reply[1] != 0x5A)
            throw new InvalidOperationException($"SOCKS4 connect failed with status 0x{reply[1]:X2}");
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset);
            if (read <= 0)
                throw new IOException("Connection closed while reading SOCKS response");

            offset += read;
        }
    }

    private static async Task HandleDirectConnection(NetworkStream clientStream, string host, int port, ProxyConfiguration config, ILogger logger)
    {
        try
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
        catch (Exception ex)
        {
            logger.LogTrace(ex, "Direct connection error to {Host}:{Port}", host, port);
            try
            {
                await HttpResponseWriter.WriteBadRequest(clientStream);
            }
            catch { }
        }
    }
}
