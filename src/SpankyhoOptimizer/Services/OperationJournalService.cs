using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record OperationJournalEntry(DateTimeOffset Time, string Id, string Area, string Action,
    string Target, string State, string? Detail);

public static class OperationJournalService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static long KnownLength = -1;
    private static string JournalPath => Path.Combine(LogService.Root, "operation-journal.jsonl");

    public static async Task WarmAsync(CancellationToken token = default)
    {
        _ = JsonSerializer.Serialize(new OperationJournalEntry(default, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, null), OptimizerJsonContext.Default.OperationJournalEntry);
        await Gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(LogService.Root);
            await using var stream = new FileStream(JournalPath, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            KnownLength = stream.Length;
        }
        finally { Gate.Release(); }
    }

    public static async Task WriteAsync(string id, string area, string action, string target, string state,
        string? detail = null, CancellationToken token = default)
    {
        var entry = new OperationJournalEntry(DateTimeOffset.Now, id, area, action, target, state, detail);
        string line = JsonSerializer.Serialize(entry, OptimizerJsonContext.Default.OperationJournalEntry) + Environment.NewLine;
        await Gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(LogService.Root);
            if (KnownLength < 0)
                KnownLength = File.Exists(JournalPath) ? new FileInfo(JournalPath).Length : 0;
            await File.AppendAllTextAsync(JournalPath, line, System.Text.Encoding.UTF8, token);
            KnownLength += System.Text.Encoding.UTF8.GetByteCount(line);
            await TrimIfNeededAsync(token);
        }
        finally { Gate.Release(); }
    }

    private static async Task TrimIfNeededAsync(CancellationToken token)
    {
        if (KnownLength < 4 * 1024 * 1024) return;
        string[] lines = await File.ReadAllLinesAsync(JournalPath, token);
        string[] tail = lines.TakeLast(2500).ToArray();
        string trimmed = string.Join(Environment.NewLine, tail) + Environment.NewLine;
        await AtomicFileService.WriteTextAsync(JournalPath, trimmed, token);
        KnownLength = System.Text.Encoding.UTF8.GetByteCount(trimmed);
    }
}

public static class OperationJournalReader
{
    public static async Task<OperationJournalEntry[]> ReadRecentAsync(string? area = null, int max = 200, CancellationToken token = default)
    {
        string path = Path.Combine(LogService.Root, "operation-journal.jsonl");
        if (!File.Exists(path)) return Array.Empty<OperationJournalEntry>();
        var lines = await ReadTailLinesAsync(path, Math.Max(256, max * 4), token);
        var result = new System.Collections.Generic.List<OperationJournalEntry>(Math.Min(max, lines.Count));
        for (int i = lines.Count - 1; i >= 0 && result.Count < Math.Max(1, max); i--)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var item = JsonSerializer.Deserialize(lines[i], OptimizerJsonContext.Default.OperationJournalEntry);
                if (item != null && (string.IsNullOrWhiteSpace(area) || item.Area.Equals(area, StringComparison.OrdinalIgnoreCase))) result.Add(item);
            }
            catch { }
        }
        return result.ToArray();
    }

    private static async Task<System.Collections.Generic.List<string>> ReadTailLinesAsync(string path, int wanted, CancellationToken token)
    {
        const int chunkSize = 64 * 1024;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize, useAsync: true);
        if (fs.Length == 0) return new();
        long pos = fs.Length;
        var chunks = new System.Collections.Generic.List<byte[]>();
        int lineCount = 0;
        while (pos > 0 && lineCount <= wanted)
        {
            token.ThrowIfCancellationRequested();
            int take = (int)Math.Min(chunkSize, pos); pos -= take; fs.Position = pos;
            var buf = new byte[take]; int read = await fs.ReadAsync(buf.AsMemory(0, take), token);
            if (read != take) Array.Resize(ref buf, read);
            chunks.Add(buf);
            for (int i = 0; i < buf.Length; i++) if (buf[i] == (byte)'\n') lineCount++;
        }
        int total = chunks.Sum(x => x.Length); var all = new byte[total]; int offset = 0;
        for (int i = chunks.Count - 1; i >= 0; i--) { Buffer.BlockCopy(chunks[i], 0, all, offset, chunks[i].Length); offset += chunks[i].Length; }
        string text = System.Text.Encoding.UTF8.GetString(all);
        return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).TakeLast(wanted).ToList();
    }
}
