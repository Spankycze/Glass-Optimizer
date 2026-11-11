using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class AtomicFileService
{
    // A crashed writer leaves its uniquely named sidecar behind. Only remove
    // clearly stale ones so a live writer in another process is never harmed.
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(10);

    private static void RemoveStaleTemps(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            string pattern = Path.GetFileName(path) + ".tmp.*";
            foreach (string stale in Directory.EnumerateFiles(dir, pattern))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) > StaleTempAge)
                        File.Delete(stale);
                }
                catch { }
            }
        }
        catch { }
    }

    public static async Task WriteTextAsync(string path, string content, CancellationToken token = default)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        RemoveStaleTemps(path);
        string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
#pragma warning disable CA1849 // Intentional durability barrier: Flush(true) commits file contents/metadata to stable storage.
                stream.Flush(true);
#pragma warning restore CA1849
            }
            Replace(temp, path);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
    public static void WriteText(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        RemoveStaleTemps(path);
        string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None)) stream.Flush(true);
            Replace(temp, path);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    // Byte payloads (already-serialized UTF-8 JSON) skip the transient string
    // while keeping the same unique-sidecar, cross-process-safe replacement.
    internal static void WriteAllBytesAtomic(string path, byte[] bytes)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        RemoveStaleTemps(path);
        string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, bytes);
            Replace(temp, path);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    internal static async Task<string> RunAtomicReplacementSmokeAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "spanky-atomic-smoke-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(directory, "health-fixture.json");
        string legacyBackup = target + ".bak";
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(target, "OLD");
            // An unrelated legacy backup is not a disposable scratch file.
            File.WriteAllText(legacyBackup, "KEEP-LEGACY-BACKUP");

            await WriteTextAsync(target, "ASYNC").ConfigureAwait(false);
            if (File.ReadAllText(target) != "ASYNC" ||
                File.ReadAllText(legacyBackup) != "KEEP-LEGACY-BACKUP")
                throw new InvalidOperationException("Atomic async replacement clobbered a preexisting backup.");

            WriteText(target, "SYNC");
            if (File.ReadAllText(target) != "SYNC" ||
                File.ReadAllText(legacyBackup) != "KEEP-LEGACY-BACKUP")
                throw new InvalidOperationException("Atomic sync replacement clobbered a preexisting backup.");

            // A crashed writer's stale sidecar must be reclaimed; a live
            // writer's fresh sidecar in another process must survive.
            string staleTemp = target + ".tmp." + new string('a', 32);
            string liveTemp = target + ".tmp." + new string('b', 32);
            File.WriteAllText(staleTemp, "stale");
            File.SetLastWriteTimeUtc(staleTemp, DateTime.UtcNow.AddMinutes(-30));
            File.WriteAllText(liveTemp, "live");
            WriteText(target, "STALE-CLEANUP");
            if (File.Exists(staleTemp) || !File.Exists(liveTemp))
                throw new InvalidOperationException(
                    "Atomic stale-temp cleanup kept a crashed sidecar or removed a live writer's temp.");
            File.Delete(liveTemp);

            // Pre-serialized byte payloads must use the same safe replacement.
            byte[] payload = Encoding.UTF8.GetBytes("{\"payload\":\"BYTES\"}");
            WriteAllBytesAtomic(target, payload);
            if (!File.ReadAllBytes(target).SequenceEqual(payload) ||
                Directory.GetFiles(directory, "health-fixture.json.tmp.*").Length != 0)
                throw new InvalidOperationException(
                    "Atomic byte replacement lost its payload or left a sidecar behind.");

            // Several independent callers (GUI + RuntimeHost) may publish the
            // same status file. Each must own its temp file without ever
            // renaming, deleting or overwriting another caller's sidecar.
            var tasks = new Task[8];
            for (int i = 0; i < tasks.Length; i++)
            {
                int index = i;
                tasks[i] = Task.Run(() => WriteText(target, "WRITER-" + index));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            string final = File.ReadAllText(target);
            if (!final.StartsWith("WRITER-", StringComparison.Ordinal) ||
                !int.TryParse(final.AsSpan("WRITER-".Length), out int indexOfWriter) ||
                indexOfWriter is < 0 or >= 8 ||
                File.ReadAllText(legacyBackup) != "KEEP-LEGACY-BACKUP" ||
                Directory.GetFiles(directory, "health-fixture.json.tmp.*").Length != 0)
                throw new InvalidOperationException("Atomic concurrent writes lost a complete result or damaged a sidecar.");
            return "ATOMIC_REPLACEMENT_SMOKE_OK sync=true async=true legacyBackupPreserved=true " +
                "concurrentWriters=8 staleTempReclaimed=true liveTempPreserved=true bytesAtomic=true";
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    // Reuse the same bounded atomic replace policy for completed, same-volume
    // streamed artifacts such as large registry exports.
    internal static void Replace(string temp, string path)
    {
        // An atomic replace does not need a backup: our former fixed ".bak"
        // filename could clobber an existing file or race another writer.
        // Retrying the *atomic* operation first also avoids noisy logs and
        // degraded overwrite fallback for transient cross-process read locks.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Replace(temp, path, null, true);
                else
                    File.Move(temp, path);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                break;
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(20 + attempt * 30);
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(20 + attempt * 30);
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
        }

        // File.Replace is not supported by every filesystem. Preserve the
        // previous overwrite-rename compatibility path, but only as a last
        // resort and with a bounded wait for transient sharing conflicts.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(20 + attempt * 30);
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(20 + attempt * 30);
            }
        }
    }
}
