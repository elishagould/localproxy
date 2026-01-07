using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace localproxy;

public static class StreamCopier
{
    public static async Task CopyStreamAsync(Stream source, Stream destination, TcpClient client, int bufferSize = 8192)
    {
        try
        {
            var buffer = new byte[bufferSize];
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await destination.WriteAsync(buffer, 0, bytesRead);
                await destination.FlushAsync();
            }
        }
        catch
        {
            try { client?.Close(); } catch { }
        }
    }
}
