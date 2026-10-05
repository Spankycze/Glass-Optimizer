using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SpankyhoOptimizer.Services;

public sealed record CrashFingerprintEntry(
    string Fingerprint, int Count, DateTimeOffset LastUtc,
    string Source, string Stage, string ExceptionType, string Message);

public static class CrashFingerprintService
{
    private static readonly object Sync = new();
    private static string FileName => Path.Combine(LogService.Root, "crash-fingerprints.json");

    public static void Record(Exception? ex, string source, string stage)
    {
        if (ex == null) return;
        try
        {
            string type = ex.GetType().FullName ?? ex.GetType().Name;
            string top = (ex.StackTrace ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
            string fp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(type + "|" + stage + "|" + top)))[..16];
            lock (Sync) Update(fp, ex, source, stage, type);
        }
        catch { }
    }
    private static void Update(string fp, Exception ex, string source, string stage, string type)
    {
        var items = SelfHealingJsonService.ReadOrDefault(FileName, () => Array.Empty<CrashFingerprintEntry>(), "crash-fingerprint").ToList();
        int i = items.FindIndex(x => x.Fingerprint.Equals(fp, StringComparison.OrdinalIgnoreCase));
        var next = new CrashFingerprintEntry(fp, i >= 0 ? items[i].Count + 1 : 1, DateTimeOffset.UtcNow,
            source, stage, type, ex.Message.Length > 500 ? ex.Message[..500] : ex.Message);
        if (i >= 0) items[i] = next; else items.Add(next);
        items = items.OrderByDescending(x => x.LastUtc).Take(50).ToList();
        SelfHealingJsonService.Write(FileName, items.ToArray());
    }

    public static CrashFingerprintEntry[] Snapshot()
    {
        lock (Sync)
            return SelfHealingJsonService.ReadOrDefault(FileName, () => Array.Empty<CrashFingerprintEntry>(), "crash-fingerprint")
                .OrderByDescending(x => x.Count).ThenByDescending(x => x.LastUtc).ToArray();
    }

    public static void Reset()
    {
        lock (Sync) { try { if (File.Exists(FileName)) File.Delete(FileName); } catch { } }
    }
}
