using System;
using System.Collections.Generic;
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
    private const int MaxHeaderBytes = 64 * 1024;

    public static async Task HandleClientAsync(TcpClient client, HttpClient httpClient, SspiCredentialCache credentialCache, AuthenticatedConnectionPool connectionPool, ProxyConfiguration config, ILoggerFactory loggerFactory, ProxyExclusionMatcher exclusionMatcher, ProxyExclusionMatcher blocklistMatcher)
    {
        var logger = loggerFactory.CreateLogger(typeof(ClientHandler));
        var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

        using (client)
        {
            using var ns = client.GetStream();
            try
            {
                var bufferedBytes = new List<byte>(config.Proxy.BufferSize);
                await ReadIntoBufferAsync(ns, bufferedBytes, config.Proxy.BufferSize);

                if (bufferedBytes.Count == 0)
                {
                    return;
                }

                if (bufferedBytes[0] == 0x05)
                {
                    await HandleSocks5ClientAsync(ns, bufferedBytes, config, blocklistMatcher, logger);
                    return;
                }

                await ReadHttpHeaderIntoBufferAsync(ns, bufferedBytes, config.Proxy.BufferSize);

                if (!TryParseHttpRequest(bufferedBytes, out var requestLine, out var headers, out var bodyStartIndex))
                {
                    logger.LogWarning("Bad request from {ClientEndpoint}", clientEndpoint);
                    await HttpResponseWriter.WriteBadRequest(ns);
                    return;
                }

                logger.LogTrace("Request from {ClientEndpoint}: {RequestLine}", clientEndpoint, requestLine);

                var parts = requestLine.Split(' ');
                if (parts.Length < 3)
                {
                    logger.LogWarning("Bad request from {ClientEndpoint}", clientEndpoint);
                    await HttpResponseWriter.WriteBadRequest(ns);
                    return;
                }

                var method = parts[0];
                var uriPart = parts[1];
                var bufferedBody = bodyStartIndex < bufferedBytes.Count
                    ? bufferedBytes.GetRange(bodyStartIndex, bufferedBytes.Count - bodyStartIndex).ToArray()
                    : Array.Empty<byte>();

                if (string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogTrace("CONNECT tunnel to {Target}", uriPart);
                    await ConnectTunnelHandler.HandleConnectTunnel(ns, uriPart, httpClient, credentialCache, connectionPool, config, loggerFactory, exclusionMatcher, blocklistMatcher);
                    return;
                }

                using var prefixedStream = new PrefixedStream(ns, bufferedBody);
                using var reader = new StreamReader(prefixedStream, Encoding.ASCII, leaveOpen: true);

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

    private static async Task HandleSocks5ClientAsync(NetworkStream ns, List<byte> bufferedBytes, ProxyConfiguration config, ProxyExclusionMatcher blocklistMatcher, ILogger logger)
    {
        try
        {
            await EnsureBufferLengthAsync(ns, bufferedBytes, 2, config.Proxy.BufferSize);

            var methodCount = bufferedBytes[1];
            await EnsureBufferLengthAsync(ns, bufferedBytes, 2 + methodCount, config.Proxy.BufferSize);

            var supportsNoAuth = false;
            for (var i = 0; i < methodCount; i++)
            {
                if (bufferedBytes[2 + i] == 0x00)
                {
                    supportsNoAuth = true;
                    break;
                }
            }

            if (!supportsNoAuth)
            {
                await ns.WriteAsync(new byte[] { 0x05, 0xFF });
                await ns.FlushAsync();
                return;
            }

            await ns.WriteAsync(new byte[] { 0x05, 0x00 });
            await ns.FlushAsync();

            var offset = 2 + methodCount;
            await EnsureBufferLengthAsync(ns, bufferedBytes, offset + 4, config.Proxy.BufferSize);

            var version = bufferedBytes[offset];
            var command = bufferedBytes[offset + 1];
            var addressType = bufferedBytes[offset + 3];

            if (version != 0x05 || command != 0x01)
            {
                await SendSocks5ReplyAsync(ns, 0x07);
                return;
            }

            offset += 4;
            string host;

            if (addressType == 0x01)
            {
                await EnsureBufferLengthAsync(ns, bufferedBytes, offset + 4 + 2, config.Proxy.BufferSize);
                host = new IPAddress(bufferedBytes.GetRange(offset, 4).ToArray()).ToString();
                offset += 4;
            }
            else if (addressType == 0x04)
            {
                await EnsureBufferLengthAsync(ns, bufferedBytes, offset + 16 + 2, config.Proxy.BufferSize);
                host = new IPAddress(bufferedBytes.GetRange(offset, 16).ToArray()).ToString();
                offset += 16;
            }
            else if (addressType == 0x03)
            {
                await EnsureBufferLengthAsync(ns, bufferedBytes, offset + 1, config.Proxy.BufferSize);
                var domainLength = bufferedBytes[offset];
                offset += 1;
                await EnsureBufferLengthAsync(ns, bufferedBytes, offset + domainLength + 2, config.Proxy.BufferSize);
                host = Encoding.ASCII.GetString(bufferedBytes.GetRange(offset, domainLength).ToArray());
                offset += domainLength;
            }
            else
            {
                await SendSocks5ReplyAsync(ns, 0x08);
                return;
            }

            var port = (bufferedBytes[offset] << 8) | bufferedBytes[offset + 1];
            offset += 2;

            if (blocklistMatcher.ShouldBypassProxy(host, port))
            {
                logger.LogWarning("Host {Host}:{Port} is blocked by configuration", host, port);
                await SendSocks5ReplyAsync(ns, 0x02);
                return;
            }

            var remainingData = offset < bufferedBytes.Count
                ? bufferedBytes.GetRange(offset, bufferedBytes.Count - offset).ToArray()
                : Array.Empty<byte>();

            using var targetClient = new TcpClient();
            await targetClient.ConnectAsync(host, port);
            var targetStream = targetClient.GetStream();

            await SendSocks5ReplyAsync(ns, 0x00);

            if (remainingData.Length > 0)
            {
                await targetStream.WriteAsync(remainingData, 0, remainingData.Length);
                await targetStream.FlushAsync();
            }

            var clientToTarget = StreamCopier.CopyStreamAsync(ns, targetStream, targetClient, config.Proxy.BufferSize);
            var targetToClient = StreamCopier.CopyStreamAsync(targetStream, ns, targetClient, config.Proxy.BufferSize);

            await Task.WhenAny(clientToTarget, targetToClient);
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "SOCKS5 handling error");
            try
            {
                await SendSocks5ReplyAsync(ns, 0x01);
            }
            catch { }
        }
    }

    private static async Task SendSocks5ReplyAsync(NetworkStream ns, byte replyCode)
    {
        var reply = new byte[] { 0x05, replyCode, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        await ns.WriteAsync(reply, 0, reply.Length);
        await ns.FlushAsync();
    }

    private static async Task ReadHttpHeaderIntoBufferAsync(NetworkStream ns, List<byte> bufferedBytes, int chunkSize)
    {
        while (!TryFindHttpHeaderTerminator(bufferedBytes, out _))
        {
            if (bufferedBytes.Count > MaxHeaderBytes)
            {
                throw new InvalidOperationException("HTTP header too large");
            }

            var bytesRead = await ReadIntoBufferAsync(ns, bufferedBytes, chunkSize);
            if (bytesRead == 0)
            {
                throw new IOException("Connection closed before HTTP headers were complete");
            }
        }
    }

    private static bool TryParseHttpRequest(List<byte> bufferedBytes, out string requestLine, out WebHeaderCollection headers, out int bodyStartIndex)
    {
        requestLine = string.Empty;
        headers = new WebHeaderCollection();
        bodyStartIndex = 0;

        if (!TryFindHttpHeaderTerminator(bufferedBytes, out var headerEndIndex))
        {
            return false;
        }

        var headerBytes = bufferedBytes.GetRange(0, headerEndIndex).ToArray();
        var headerText = Encoding.ASCII.GetString(headerBytes).TrimEnd('\r', '\n');
        var lines = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
        {
            return false;
        }

        requestLine = lines[0];

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var idx = line.IndexOf(':');
            if (idx > 0)
            {
                var name = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                headers.Add(name, value);
            }
        }

        bodyStartIndex = headerEndIndex;
        return true;
    }

    private static bool TryFindHttpHeaderTerminator(List<byte> bufferedBytes, out int headerEndIndex)
    {
        for (var i = 0; i < bufferedBytes.Count - 3; i++)
        {
            if (bufferedBytes[i] == 13 && bufferedBytes[i + 1] == 10 && bufferedBytes[i + 2] == 13 && bufferedBytes[i + 3] == 10)
            {
                headerEndIndex = i + 4;
                return true;
            }
        }

        for (var i = 0; i < bufferedBytes.Count - 1; i++)
        {
            if (bufferedBytes[i] == 10 && bufferedBytes[i + 1] == 10)
            {
                headerEndIndex = i + 2;
                return true;
            }
        }

        headerEndIndex = 0;
        return false;
    }

    private static async Task EnsureBufferLengthAsync(NetworkStream ns, List<byte> bufferedBytes, int requiredLength, int chunkSize)
    {
        while (bufferedBytes.Count < requiredLength)
        {
            var bytesRead = await ReadIntoBufferAsync(ns, bufferedBytes, chunkSize);
            if (bytesRead == 0)
            {
                throw new IOException("Connection closed while reading client data");
            }
        }
    }

    private static async Task<int> ReadIntoBufferAsync(NetworkStream ns, List<byte> bufferedBytes, int chunkSize)
    {
        var chunk = new byte[Math.Max(1, chunkSize)];
        var bytesRead = await ns.ReadAsync(chunk, 0, chunk.Length);
        if (bytesRead > 0)
        {
            bufferedBytes.AddRange(chunk.AsSpan(0, bytesRead).ToArray());
        }

        return bytesRead;
    }

    private sealed class PrefixedStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _prefix;
        private int _offset;

        public PrefixedStream(Stream inner, byte[] prefix)
        {
            _inner = inner;
            _prefix = prefix;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < _prefix.Length)
            {
                var toCopy = Math.Min(count, _prefix.Length - _offset);
                Buffer.BlockCopy(_prefix, _offset, buffer, offset, toCopy);
                _offset += toCopy;
                return toCopy;
            }

            return _inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
        {
            if (_offset < _prefix.Length)
            {
                var toCopy = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsMemory(_offset, toCopy).CopyTo(buffer);
                _offset += toCopy;
                return toCopy;
            }

            return await _inner.ReadAsync(buffer, cancellationToken);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken)
        {
            if (_offset < _prefix.Length)
            {
                var toCopy = Math.Min(count, _prefix.Length - _offset);
                Buffer.BlockCopy(_prefix, _offset, buffer, offset, toCopy);
                _offset += toCopy;
                return toCopy;
            }

            return await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(System.Threading.CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
