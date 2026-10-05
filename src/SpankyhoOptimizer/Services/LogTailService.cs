using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class LogTailService
{
    public static async Task<string[]> ReadTailLinesAsync(string path, int maxBytes = 2 * 1024 * 1024,
        int maxLines = 5000, CancellationToken token = default)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        long end = stream.Length;
        if (end == 0) return Array.Empty<string>();
        long lower = Math.Max(0, end - Math.Max(4096, maxBytes));
        long pos = end, start = lower;
        int lines = 0;
        byte[] scan = new byte[64 * 1024];
        bool exactStart = false;

        while (pos > lower && !exactStart)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(scan.Length, pos - lower);
            long readAt = pos - count;
            stream.Seek(readAt, SeekOrigin.Begin);
            int got = 0;
            while (got < count)
            {
                int n = await stream.ReadAsync(scan.AsMemory(got, count - got), token);
                if (n == 0) break;
                got += n;
            }
            for (int i = got - 1; i >= 0; i--)
            {
                if (scan[i] != (byte)'\n') continue;
                if (++lines > maxLines) { start = readAt + i + 1; exactStart = true; break; }
            }
            pos = readAt;
        }
        int length = checked((int)(end - start));
        byte[] data = GC.AllocateUninitializedArray<byte>(length);
        stream.Seek(start, SeekOrigin.Begin);
        int read = 0;
        while (read < length)
        {
            int n = await stream.ReadAsync(data.AsMemory(read, length - read), token);
            if (n == 0) break;
            read += n;
        }
        if (read == 0) return Array.Empty<string>();

        int offset = 0;
        if (!exactStart && start > 0)
        {
            while (offset < read && data[offset] != (byte)'\n') offset++;
            if (offset < read) offset++;
        }
        string text = Encoding.UTF8.GetString(data, offset, read - offset);
        string[] all = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        if (all.Length <= maxLines) return all;
        var tail = new string[maxLines];
        Array.Copy(all, all.Length - maxLines, tail, 0, maxLines);
        return tail;
    }
}
