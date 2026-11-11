using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class LogTailService
{
    internal static async Task<string> RunTailBoundarySmokeAsync()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "spanky-log-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "fixture.log");
            async Task CheckAsync(string input, int take, params string[] expected)
            {
                await File.WriteAllTextAsync(file, input, new UTF8Encoding(false)).ConfigureAwait(false);
                string[] actual = await ReadTailLinesAsync(file, maxLines: take).ConfigureAwait(false);
                if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"Log tail incorrect for take={take}: actual=[{string.Join("|", actual)}], expected=[{string.Join("|", expected)}]");
            }

            await CheckAsync("jedna\ndvě\ntři\n", 2, "dvě", "tři").ConfigureAwait(false);
            await CheckAsync("jedna\r\ndvě\r\ntři\r\n", 2, "dvě", "tři").ConfigureAwait(false);
            await CheckAsync("jedna\ndvě\ntři", 2, "dvě", "tři").ConfigureAwait(false);
            await CheckAsync("jedna\ndvě\ntři\n", 1, "tři").ConfigureAwait(false);
            await CheckAsync("jedna\ndvě\n\n", 2, "dvě", "").ConfigureAwait(false);
            await CheckAsync("jediný\n", 4, "jediný").ConfigureAwait(false);
            await CheckAsync("jediný", 0).ConfigureAwait(false);

            var large = new StringBuilder();
            for (int i = 1; i <= 2502; i++)
                large.Append('L').Append(i.ToString("D4")).Append('\n');
            await File.WriteAllTextAsync(file, large.ToString(), Encoding.UTF8).ConfigureAwait(false);
            string[] tail = await ReadTailLinesAsync(file, maxLines: 2500).ConfigureAwait(false);
            if (tail.Length != 2500 || tail[0] != "L0003" || tail[^1] != "L2502")
                throw new InvalidOperationException("Last-2500-log-lines view dropped a real line.");

            return "LOG_TAIL_BOUNDARY_SMOKE_OK cases=8 LF=true CRLF=true UTF8=true " +
                   "trailingBlankPreserved=true last2500Exact=true realLogsTouched=0";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    public static async Task<string[]> ReadTailLinesAsync(string path, int maxBytes = 2 * 1024 * 1024,
        int maxLines = 5000, CancellationToken token = default)
    {
        if (maxLines <= 0 || !File.Exists(path)) return Array.Empty<string>();
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
        // A terminal newline ends the last real record; Split contributes an
        // extra empty element that is not another log line. Preserve genuine
        // blank lines before it, and never drop an actual line to include it.
        int actualLineCount = all.Length;
        if (actualLineCount > 0 && all[actualLineCount - 1].Length == 0 && text.EndsWith('\n'))
            actualLineCount--;
        if (actualLineCount == 0) return Array.Empty<string>();
        int take = Math.Min(maxLines, actualLineCount);
        var tail = new string[take];
        Array.Copy(all, actualLineCount - take, tail, 0, take);
        return tail;
    }
}
