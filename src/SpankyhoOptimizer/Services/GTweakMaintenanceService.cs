using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static class GTweakMaintenanceService
{
    public static bool IsNtfs(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root)) return false;
        var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
        return drive?.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static async Task<ProcessResult> SetNtfsCompressionAsync(string path, bool compress, CancellationToken token)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        if (!IsNtfs(path)) throw new NotSupportedException("Vybraná složka není na NTFS svazku.");
        string mode = compress ? "/C" : "/U";
        var result = await ProcessRunner.RunAsync("compact.exe", $"{mode} /S:\"{path}\" /I /Q \"{path}\\*\"", TimeSpan.FromMinutes(30), token).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.StdErr + result.StdOut);
        return result;
    }

    public static string WindowsOldPath =>
        Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\", "Windows.old");

    public static bool HasWindowsOld => Directory.Exists(WindowsOldPath);

    public sealed record EventLogCleanupReport(int Discovered, int Cleared, int Failed);

    public static async Task<EventLogCleanupReport> ClearEventLogsAsync(CancellationToken token)
    {
        var listed = await ProcessRunner.RunAsync("wevtutil.exe", "el", TimeSpan.FromMinutes(2), token)
            .ConfigureAwait(false);
        if (!listed.Success)
            throw new InvalidOperationException("Seznam Event Viewer logů se nepodařilo načíst. " + listed.StdErr + listed.StdOut);

        var channels = listed.StdOut
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static name => name.Trim())
            .Where(IsSafeEventChannelName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int cleared = 0;
        int failed = 0;
        foreach (string channel in channels)
        {
            token.ThrowIfCancellationRequested();
            var result = await ProcessRunner.RunAsync("wevtutil.exe", $"cl \"{channel}\"", TimeSpan.FromSeconds(20), token)
                .ConfigureAwait(false);
            if (result.Success) cleared++;
            else failed++;
        }
        return new EventLogCleanupReport(channels.Length, cleared, failed);
    }

    private static bool IsSafeEventChannelName(string name) =>
        name.Length is > 0 and <= 256 &&
        !name.Contains('"') &&
        !name.Contains('\0');

    public static async Task RemoveWindowsOldAsync(CancellationToken token)
    {
        string path = WindowsOldPath;
        if (!Directory.Exists(path)) return;
        var own = await ProcessRunner.RunAsync("takeown.exe", $"/F \"{path}\" /R /D Y", TimeSpan.FromMinutes(10), token).ConfigureAwait(false);
        if (!own.Success) LogService.Info("storage", "Windows.old takeown returned: " + own.ExitCode);
        var acl = await ProcessRunner.RunAsync("icacls.exe", $"\"{path}\" /grant *S-1-5-32-544:F /T /C /Q", TimeSpan.FromMinutes(10), token).ConfigureAwait(false);
        if (!acl.Success) LogService.Info("storage", "Windows.old icacls returned: " + acl.ExitCode);
        var remove = await ProcessRunner.RunAsync("cmd.exe", $"/d /c rd /s /q \"{path}\"", TimeSpan.FromMinutes(30), token).ConfigureAwait(false);
        if (!remove.Success || Directory.Exists(path))
            throw new InvalidOperationException("Windows.old se nepodařilo kompletně odstranit. " + remove.StdErr + remove.StdOut);
    }

    public static Task ClearIconCacheAsync(CancellationToken token) =>
        ClearExplorerCacheAsync(clearIconCache: true, clearThumbnailCache: false, token);

    public static Task ClearThumbnailCacheAsync(CancellationToken token) =>
        ClearExplorerCacheAsync(clearIconCache: false, clearThumbnailCache: true, token);

    private static async Task ClearExplorerCacheAsync(bool clearIconCache, bool clearThumbnailCache, CancellationToken token)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string explorerCache = Path.Combine(local, "Microsoft", "Windows", "Explorer");

        // Explorer keeps icon/thumb DB files mapped. Stop the shell before deleting them.
        // A cancellation must release ALL process handles and must not leave the shell stopped.
        bool explorerStopped = false;
        bool explorerRestarted = false;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                await ProcessSnapshotCleanup.InspectAsync(
                    () => Process.GetProcessesByName("explorer"),
                    async processes =>
                    {
                        foreach (Process process in processes)
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                process.Kill();
                                explorerStopped = true;
                                using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                                exitCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                                try { await process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false); }
                                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                            catch { }
                        }
                    }).ConfigureAwait(false);

            await Task.Delay(120, token);
            await Task.Run(() =>
            {
                if (clearIconCache) TryDelete(Path.Combine(local, "IconCache.db"));
                if (!Directory.Exists(explorerCache)) return;

                if (clearIconCache)
                    foreach (string file in Directory.EnumerateFiles(explorerCache, "iconcache*", SearchOption.TopDirectoryOnly))
                        TryDelete(file);

                if (clearThumbnailCache)
                    foreach (string file in Directory.EnumerateFiles(explorerCache, "thumbcache*", SearchOption.TopDirectoryOnly))
                        TryDelete(file);
            }, token);

            bool iconLeft = clearIconCache && Directory.Exists(explorerCache) &&
                            Directory.EnumerateFiles(explorerCache, "iconcache*", SearchOption.TopDirectoryOnly).Any();
            bool thumbLeft = clearThumbnailCache && Directory.Exists(explorerCache) &&
                             Directory.EnumerateFiles(explorerCache, "thumbcache*", SearchOption.TopDirectoryOnly).Any();
            if (!iconLeft && !thumbLeft) break;
        }

            await WindowsActions.RestartExplorerAsync(token).ConfigureAwait(false);
            explorerRestarted = true;
            LogService.Info("storage", $"Explorer cache rebuilt icons={clearIconCache} thumbnails={clearThumbnailCache}");
        }
        finally
        {
            if (explorerStopped && !explorerRestarted)
            {
                // Cancellation after Kill must not strand the user's desktop shell.
                // Do not kill Explorer again: start it only if it is still absent.
                try
                {
                    bool running = ProcessSnapshotCleanup.Inspect(
                        () => Process.GetProcessesByName("explorer"), processes => processes.Length > 0);
                    if (!running)
                    {
                        using Process? restored = Process.Start(new ProcessStartInfo("explorer.exe")
                        {
                            UseShellExecute = true
                        });
                        if (restored is null)
                            LogService.Info("storage", "Explorer recovery returned no process handle.");
                    }
                }
                catch (Exception ex)
                {
                    LogService.Error("storage", "Explorer recovery after cache cleanup interruption failed", ex);
                }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { LogService.Error("storage", "Explorer cache delete failed: " + path, ex); }
    }
}
