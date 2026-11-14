using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace SpankyhoOptimizer.Services;

public static partial class WindowsActions
{
    private sealed record MemoryCompressionPersistentState(bool Enabled);
    private const string MemoryCompressionPersistentKey = "memory-compression-v1";
    private static readonly TimeSpan MemoryCompressionCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MemoryCompressionUiStaleTtl = TimeSpan.FromHours(1);
    private static readonly object MemoryCompressionLock = new();
    private static readonly SemaphoreSlim MemoryCompressionProviderGate = new(1, 1);
    private static readonly object ExplorerRestartLock = new();
    private static Task? _explorerRestartInFlight;
    private static ManagementScope? _memoryCompressionScope;
    private static string? _memoryCompressionCache;
    private static DateTime _memoryCompressionCacheAt;
    private static Task<bool>? _memoryCompressionInFlight;
    private static readonly object RealtekSummaryLock = new();
    private static readonly object RealtekScopeLock = new();
    private static readonly HashSet<string> WorkingSetTrimProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ChatGPT", "chrome", "firefox", "msedge", "msedgewebview2",
        "RAMGuardRuntime", "SpankyhoOptimizer", "SpankyhoOptimizer.RuntimeHost",
        "portmaster-core", "node", "codex", "codex-computer-use-swift",
        "explorer", "dwm", "audiodg", "svchost", "services", "lsass", "winlogon", "csrss", "smss", "System"
    };
    private const string RealtekSummaryPersistentKey = "realtek-summary-v1";
    private static readonly TimeSpan RealtekSummaryPersistentTtl = TimeSpan.FromMinutes(30);
    private static string? _realtekSummaryCache;
    private static DateTime _realtekSummaryCacheAt;
    private static Task<string>? _realtekSummaryInFlight;
    private static ManagementScope? _realtekScope;

    public static async Task<bool> EnsureRecentRestorePointAsync(string description, int maxAgeMinutes = 20)
    {
        try
        {
            bool recent = await Task.Run(() =>
            {
                var scope = new ManagementScope("\\\\.\\root\\default");
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Description, CreationTime FROM SystemRestore"));
                var results = searcher.Get();
                return WmiMatchingRowScope.Any<ManagementObject>(results, results, item =>
                {
                    string d = item["Description"]?.ToString() ?? string.Empty;
                    string raw = item["CreationTime"]?.ToString() ?? string.Empty;
                    if (!d.StartsWith("Spankyho Optimizer", StringComparison.OrdinalIgnoreCase) || raw.Length == 0)
                        return false;
                    DateTime created = ManagementDateTimeConverter.ToDateTime(raw);
                    return DateTime.Now - created <= TimeSpan.FromMinutes(maxAgeMinutes);
                });
            });
            if (recent)
            {
                LogService.Info("restore", $"Recent restore point reused maxAge={maxAgeMinutes}m");
                return true;
            }
        }
        catch (Exception ex) { LogService.Error("Restore point freshness check", ex); }
        return await CreateRestorePointAsync(description);
    }

    public static async Task<bool> CreateRestorePointAsync(string description = "Spankyho Optimizer")
    {
        try
        {
            return await Task.Run(delegate
            {
                ManagementScope managementScope = new ManagementScope("\\\\.\\root\\default");
                managementScope.Connect();
                using ManagementClass managementClass = new ManagementClass(managementScope, new ManagementPath("SystemRestore"), null);
                using ManagementBaseObject managementBaseObject = managementClass.GetMethodParameters("CreateRestorePoint");
                managementBaseObject["Description"] = description;
                managementBaseObject["RestorePointType"] = 12u;
                managementBaseObject["EventType"] = 100u;
                using ManagementBaseObject managementBaseObject2 = managementClass.InvokeMethod("CreateRestorePoint", managementBaseObject, null);
                return Convert.ToUInt32(managementBaseObject2?["ReturnValue"] ?? ((object)1u)) == 0;
            });
        }
        catch (Exception ex)
        {
            LogService.Error("Create restore point", ex);
            return false;
        }
    }

    public static async Task RunIdleMaintenanceAsync(CancellationToken token = default(CancellationToken))
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            "rundll32.exe", "advapi32.dll,ProcessIdleTasks", TimeSpan.FromSeconds(30L), token).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                $"Idle maintenance selhala kódem {result.ExitCode}: {(result.StdErr + result.StdOut).Trim()}");
    }

    public static async Task TrimWorkingSetsAsync()
    {
        await Task.Run(delegate
        {
            Process[] processes = Process.GetProcesses();
            foreach (Process process in processes)
            {
                try
                {
                    string processName = process.ProcessName;
                    if (WorkingSetTrimProtectedProcesses.Contains(processName))
                        continue;

                    EmptyWorkingSet(process.Handle);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        });
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(nint hProcess);

    public static async Task ClearTempAsync()
    {
        string[] array =
        {
            Path.GetTempPath()
        };
        foreach (string dir in array)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }
            await Task.Run(delegate
            {
                foreach (string item in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(item);
                    }
                    catch
                    {
                    }
                }
                foreach (string item2 in from x in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                                         orderby x.Length descending
                                         select x)
                {
                    try
                    {
                        Directory.Delete(item2, recursive: true);
                    }
                    catch
                    {
                    }
                }
            });
        }
    }

    public static Task RestartExplorerAsync(CancellationToken token = default(CancellationToken))
    {
        lock (ExplorerRestartLock)
        {
            if (_explorerRestartInFlight is { IsCompleted: false })
            {
                LogService.Info("explorer", "Explorer restart joined existing in-flight operation.");
                return _explorerRestartInFlight;
            }

            _explorerRestartInFlight = RestartExplorerCoreAsync(token);
            return _explorerRestartInFlight;
        }
    }

    private static async Task RestartExplorerCoreAsync(CancellationToken token)
    {
        try
        {
            await Task.Run(async delegate
            {
                bool preserveManagedShortcutOverlay = ShortcutAppearanceService.PrepareManagedOverlayForExplorerRestart();
            Process[] processesByName = Process.GetProcessesByName("explorer");
            try
            {
            var exitTasks = new List<Task>(processesByName.Length);
            foreach (Process process in processesByName)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        exitTasks.Add(WaitForExplorerExitAsync(process, token));
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process exited between enumeration and kill.
                }
                catch (Exception ex)
                {
                    LogService.Error("explorer", "Explorer kill failed", ex);
                }
            }
            if (exitTasks.Count > 0)
                await Task.WhenAll(exitTasks).ConfigureAwait(false);
            }
            finally
            {
                ProcessSnapshotCleanup.DisposeAll(processesByName);
            }

            if (preserveManagedShortcutOverlay)
                PurgeExplorerIconCacheForManagedOverlay();

            using var startedExplorer = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            if (startedExplorer == null) throw new InvalidOperationException("Explorer se nepodařilo spustit.");
            var until = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < until)
            {
                token.ThrowIfCancellationRequested();
                // Even if the first explorer is enough for verification, close
                // every wrapper returned by GetProcessesByName.
                bool explorerRunning = ProcessSnapshotCleanup.Inspect(
                    () => Process.GetProcessesByName("explorer"), matches => matches.Length > 0);
                if (explorerRunning) { LogService.Info("explorer", "Explorer restart verified."); return; }
                await Task.Delay(200, token);
            }
                throw new InvalidOperationException("Explorer se po restartu znovu nespustil.");
            }, token).ConfigureAwait(false);
        }
        finally
        {
            lock (ExplorerRestartLock)
            {
                _explorerRestartInFlight = null;
            }
        }
    }

    private static async Task WaitForExplorerExitAsync(Process process, CancellationToken token)
    {
        if (process.HasExited) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (!process.HasExited)
                throw new TimeoutException("Původní Explorer se po ukončení do 2 sekund nezavřel.");
        }
    }

    private static void PurgeExplorerIconCacheForManagedOverlay()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        TryDeleteExplorerCacheFile(Path.Combine(local, "IconCache.db"));

        string explorerCache = Path.Combine(local, "Microsoft", "Windows", "Explorer");
        if (!Directory.Exists(explorerCache)) return;
        foreach (string file in Directory.EnumerateFiles(explorerCache, "iconcache*", SearchOption.TopDirectoryOnly))
            TryDeleteExplorerCacheFile(file);

        LogService.Info("explorer", "Managed shortcut overlay: icon cache purged before Explorer start.");
    }

    private static void TryDeleteExplorerCacheFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { LogService.Error("explorer", "Managed shortcut overlay cache delete failed: " + path, ex); }
    }

    public static async Task RealtekScanAsync(CancellationToken token = default(CancellationToken))
    {
        await ProcessRunner.RunAsync("pnputil.exe", "/scan-devices", TimeSpan.FromSeconds(60L), token);
        string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "UsoClient.exe");
        if (File.Exists(text))
        {
            try
            {
                await ProcessRunner.RunAsync(text, "StartScan", TimeSpan.FromSeconds(30L), token);
            }
            catch
            {
            }
        }
        ProcessRunner.Open("ms-settings:windowsupdate-optionalupdates");
    }

    public static async Task<string> GetRealtekSummaryAsync(CancellationToken token = default)
    {
        lock (RealtekSummaryLock)
            if (_realtekSummaryCache != null && DateTime.UtcNow - _realtekSummaryCacheAt < TimeSpan.FromMinutes(5))
                return _realtekSummaryCache;

        if (PersistentHardwareCacheService.TryReadMachineStable<string>(
                RealtekSummaryPersistentKey, RealtekSummaryPersistentTtl, out var persisted) &&
            !string.IsNullOrWhiteSpace(persisted))
        {
            lock (RealtekSummaryLock)
            {
                _realtekSummaryCache = persisted;
                _realtekSummaryCacheAt = DateTime.UtcNow;
            }
            LogService.Info("performance", "REALTEK_SUMMARY_PERSISTENT_HIT");
            return persisted;
        }

        Task<string> probe;
        lock (RealtekSummaryLock)
        {
            if (_realtekSummaryInFlight == null)
            {
                var created = Task.Run(LoadRealtekSummaryCore);
                _realtekSummaryInFlight = created;
                probe = created;
                _ = ObserveRealtekSummaryAsync(created);
            }
            else
            {
                probe = _realtekSummaryInFlight;
            }
        }
        return await probe.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task ObserveRealtekSummaryAsync(Task<string> created)
    {
        try
        {
            string result = await created.ConfigureAwait(false);
            lock (RealtekSummaryLock)
            {
                _realtekSummaryCache = result;
                _realtekSummaryCacheAt = DateTime.UtcNow;
                if (ReferenceEquals(_realtekSummaryInFlight, created))
                    _realtekSummaryInFlight = null;
            }
            PersistentHardwareCacheService.WriteMachineStable(RealtekSummaryPersistentKey, result);
        }
        catch
        {
            lock (RealtekSummaryLock)
                if (ReferenceEquals(_realtekSummaryInFlight, created))
                    _realtekSummaryInFlight = null;
        }
    }

    private static ManagementScope GetRealtekScope()
    {
        lock (RealtekScopeLock)
        {
            if (_realtekScope == null || !_realtekScope.IsConnected)
            {
                _realtekScope = new ManagementScope(@"\\.\root\CIMV2");
                _realtekScope.Connect();
            }
            return _realtekScope;
        }
    }

    private static string LoadRealtekSummaryCore()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                GetRealtekScope(),
                new ObjectQuery("SELECT DeviceName, DriverVersion, DriverDate, Manufacturer, InfName FROM Win32_PnPSignedDriver WHERE Manufacturer LIKE '%Realtek%' OR DeviceName LIKE '%Realtek%'"),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = false, Rewindable = false });
            List<string> list = new();
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                    list.Add($"{item["DeviceName"]} — {item["DriverVersion"]} ({item["InfName"]})");
            }
            return list.Count == 0 ? "Realtek zařízení nebylo nalezeno." : string.Join(Environment.NewLine, list.Take(6));
        }
        catch (Exception ex)
        {
            lock (RealtekScopeLock) _realtekScope = null;
            return "Detekce Realtek driveru selhala: " + ex.Message;
        }
    }

    public static async Task MaxDisableDefenderAsync(CancellationToken token = default(CancellationToken))
    {
        List<TweakDefinition> defenderTweaks = CatalogService.GetDefenderTweaks().Where(x => x.Id != "def.firewall").ToList();
        await new TweakEngine().DetectAsync(defenderTweaks, token);
        foreach (var t in defenderTweaks) t.Pending = true;
        var errors = await new TweakEngine().ApplyAsync(defenderTweaks, token);
        if (errors.Count > 0) throw new InvalidOperationException("Defender změny nebyly plně ověřeny: " + string.Join(" | ", errors));
        await SetExistingDefenderTasksAsync(enabled: false, token).ConfigureAwait(false);
    }
}
