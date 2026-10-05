using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static class WorkerClientService
{
    private static int _ipcCleanupDone;

    public static Task<T> RunAsync<T>(string action, string? arg = null, TimeSpan? timeout = null, CancellationToken token = default) =>
        RunCoreAsync<T>(action, arg, timeout, token, null, null);

    private static void CleanupStaleIpc(string dir)
    {
        if (Interlocked.Exchange(ref _ipcCleanupDone, 1) != 0) return;
        try
        {
            if (!Directory.Exists(dir)) return;
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            var files = Directory.EnumerateFiles(dir, "*.json")
                .Select(path => new FileInfo(path))
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ToArray();

            for (int i = 0; i < files.Length; i++)
            {
                bool expired = files[i].LastWriteTimeUtc < cutoff;
                bool overCount = i >= 64;
                if (!expired && !overCount) continue;
                try { files[i].Delete(); } catch { }
            }
        }
        catch (Exception ex) { LogService.Error("worker-ipc", "Stale IPC cleanup failed", ex); }
    }

    public static Task<T> RunStreamingAsync<T>(string action, string? arg, TimeSpan? timeout, CancellationToken token,
        Action<string>? onStdOut, Action<string>? onStdErr) =>
        RunCoreAsync<T>(action, arg, timeout, token, onStdOut, onStdErr);

    private static async Task<T> RunCoreAsync<T>(string action, string? arg, TimeSpan? timeout, CancellationToken token,
        Action<string>? onStdOut, Action<string>? onStdErr)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "SpankyhoOptimizer.exe");
        string dir = Path.Combine(LogService.Root, "worker-ipc");
        Directory.CreateDirectory(dir);
        CleanupStaleIpc(dir);
        string output = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        string args = $"--worker \"{Escape(action)}\" --worker-out \"{Escape(output)}\"";
        if (!string.IsNullOrWhiteSpace(arg)) args += $" --worker-arg \"{Escape(arg)}\"";
        try
        {
            ProcessResult result = onStdOut == null && onStdErr == null
                ? await ProcessRunner.RunAsync(exe, args, timeout ?? TimeSpan.FromSeconds(60), token)
                : await ProcessRunner.RunStreamingAsync(exe, args, timeout ?? TimeSpan.FromSeconds(60), token, onStdOut, onStdErr);
            if (!result.Success) throw new InvalidOperationException($"Worker {action} selhal: {(result.StdErr + result.StdOut).Trim()}");
            if (!File.Exists(output)) throw new InvalidDataException($"Worker {action} nevytvořil odpověď.");
            string json = await File.ReadAllTextAsync(output, token);
            var value = JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException($"Worker {action} vrátil neplatná data.");
            WorkerHealthService.Success(action);
            return value;
        }
        catch (Exception ex)
        {
            WorkerHealthService.Failure(action, ex);
            throw;
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
        }
    }

    internal static void RunIpcCleanupSmoke()
    {
        string dir = Path.Combine(LogService.Root, "worker-ipc");
        Directory.CreateDirectory(dir);
        string stale = Path.Combine(dir, "smoke-stale-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(stale, "{}");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));
            Interlocked.Exchange(ref _ipcCleanupDone, 0);
            CleanupStaleIpc(dir);
            if (File.Exists(stale))
                throw new InvalidOperationException("Stale worker IPC file was not pruned.");
            LogService.Info("worker-ipc", "WORKER_IPC_CLEANUP_SMOKE_OK");
        }
        finally
        {
            try { if (File.Exists(stale)) File.Delete(stale); } catch { }
        }
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");
}
