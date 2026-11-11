using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SpankyhoOptimizer.Services;

public static class SelfHealingJsonService
{
    public static T ReadOrDefault<T>(string path, Func<T> fallback, string area = "state")
    {
        if (!File.Exists(path)) return fallback();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? fallback();
        }
        catch (JsonException ex)
        {
            // Only malformed JSON is quarantined. An unavailable/locked file
            // may contain valid user settings and must remain untouched.
            Quarantine(path, area, ex);
            return fallback();
        }
        catch (Exception ex)
        {
            LogService.Error(area, "State read failed; original file preserved", ex);
            return fallback();
        }
    }

    public static T ReadOrDefault<T>(string path, Func<T> fallback, JsonTypeInfo<T> typeInfo, string area = "state")
    {
        if (!File.Exists(path)) return fallback();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo) ?? fallback();
        }
        catch (JsonException ex)
        {
            // Only malformed JSON is quarantined. An unavailable/locked file
            // may contain valid user settings and must remain untouched.
            Quarantine(path, area, ex);
            return fallback();
        }
        catch (Exception ex)
        {
            LogService.Error(area, "State read failed; original file preserved", ex);
            return fallback();
        }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? LogService.Root);
        AtomicFileService.WriteText(path, JsonSerializer.Serialize(value));
    }
    internal static string RunQuarantinePreservationSmoke()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "spanky-quarantine-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "ui-state.json");
            DateTime fixedUtc = new DateTime(2026, 10, 21, 2, 0, 0, DateTimeKind.Utc);
            for (int i = 1; i <= 4; i++)
            {
                File.WriteAllText(file, "invalid-json-" + i);
                Quarantine(file, "quarantine-smoke", new JsonException("fixture"), fixedUtc);
                if (File.Exists(file))
                    throw new InvalidOperationException("Corrupt state was not moved to quarantine.");
                string[] snapshots = Directory.GetFiles(folder, "ui-state.corrupt-*.json");
                if (snapshots.Length != Math.Min(i, 3))
                    throw new InvalidOperationException(
                        "Two corrupt states within the same second overwrote an earlier recovery snapshot.");
            }
            string[] preserved = Directory.GetFiles(folder, "ui-state.corrupt-*.json");
            string[] content = preserved.Select(File.ReadAllText).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (!content.SequenceEqual(new[] { "invalid-json-2", "invalid-json-3", "invalid-json-4" }))
                throw new InvalidOperationException("Corrupt state snapshots were overwritten or pruned in the wrong order.");

            // A temporary sharing violation is not proof of JSON corruption.
            string valid = Path.Combine(folder, "valid.json");
            File.WriteAllText(valid, "42");
            if (ReadOrDefault(valid, () => -1, "quarantine-smoke") != 42)
                throw new InvalidOperationException("Healthy state failed to deserialize.");
            using (var heldOpen = new FileStream(valid, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (ReadOrDefault(valid, () => -1, "quarantine-smoke") != -1 ||
                    !File.Exists(valid) ||
                    Directory.GetFiles(folder, "valid.corrupt-*.json").Length != 0)
                    throw new InvalidOperationException("Locked but valid state was incorrectly quarantined.");
            }
            if (ReadOrDefault(valid, () => -1, "quarantine-smoke") != 42)
                throw new InvalidOperationException("Valid state was lost after transient sharing violation.");

            return "JSON_QUARANTINE_PRESERVATION_SMOKE_OK distinctSnapshots=3 " +
                   "newestPreserved=3 olderPruned=1 lockedHealthyStatePreserved=true realUserFilesTouched=0";
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    private static void Quarantine(string path, string area, Exception ex, DateTime? testTimeUtc = null)
    {
        try
        {
            string dir = Path.GetDirectoryName(path) ?? LogService.Root;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string stamp = (testTimeUtc ?? DateTime.UtcNow).ToString("yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture);
            string? target = null;
            // Atomic no-replace File.Move is important: two corrupt state
            // versions in the same second must never overwrite one another.
            for (int attempt = 0; attempt < 64; attempt++)
            {
                string candidate = Path.Combine(dir, $"{name}.corrupt-{stamp}-{attempt:D4}{ext}");
                try
                {
                    File.Move(path, candidate);
                    target = candidate;
                    break;
                }
                catch (IOException) when (File.Exists(candidate) && attempt < 63)
                {
                    // A prior copy has this name. Try the next free suffix.
                }
            }
            if (target == null)
                throw new IOException("No free unique quarantine filename was available.");

            // Suffix and timestamp make lexical order chronological, including
            // several snapshots created within the same second. No reliance on
            // NTFS LastWriteTime resolution or preserved source file timestamps.
            foreach (string old in Directory.GetFiles(dir, name + ".corrupt-*" + ext)
                         .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).Skip(3))
                try { File.Delete(old); } catch { }
            LogService.Error(area, $"Corrupt state quarantined: {Path.GetFileName(target)}", ex);
        }
        catch (Exception quarantineEx)
        {
            LogService.Error(area, "Corrupt state quarantine failed", quarantineEx);
        }
    }
}
