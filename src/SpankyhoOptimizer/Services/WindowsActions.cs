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

public static class WindowsActions
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
                foreach (ManagementObject item in searcher.Get())
                {
                    string d = item["Description"]?.ToString() ?? string.Empty;
                    string raw = item["CreationTime"]?.ToString() ?? string.Empty;
                    if (!d.StartsWith("Spankyho Optimizer", StringComparison.OrdinalIgnoreCase) || raw.Length == 0) continue;
                    DateTime created = ManagementDateTimeConverter.ToDateTime(raw);
                    if (DateTime.Now - created <= TimeSpan.FromMinutes(maxAgeMinutes)) return true;
                }
                return false;
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

    public static Task RunIdleMaintenanceAsync(CancellationToken token = default(CancellationToken))
    {
        return ProcessRunner.RunAsync("rundll32.exe", "advapi32.dll,ProcessIdleTasks", TimeSpan.FromSeconds(30L), token);
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
            foreach (Process process in processesByName) process.Dispose();

            if (preserveManagedShortcutOverlay)
                PurgeExplorerIconCacheForManagedOverlay();

            using var startedExplorer = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            if (startedExplorer == null) throw new InvalidOperationException("Explorer se nepodařilo spustit.");
            var until = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < until)
            {
                token.ThrowIfCancellationRequested();
                using var explorer = Process.GetProcessesByName("explorer").FirstOrDefault();
                if (explorer != null) { LogService.Info("explorer", "Explorer restart verified."); return; }
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
            foreach (ManagementObject item in results) { list.Add($"{item["DeviceName"]} — {item["DriverVersion"]} ({item["InfName"]})"); item.Dispose(); }
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
        string[] tasks = { "\\Microsoft\\Windows\\Windows Defender\\Windows Defender Cache Maintenance", "\\Microsoft\\Windows\\Windows Defender\\Windows Defender Cleanup", "\\Microsoft\\Windows\\Windows Defender\\Windows Defender Scheduled Scan", "\\Microsoft\\Windows\\Windows Defender\\Windows Defender Verification" };
        var scheduler = new TaskSchedulerService();
        foreach (string path in tasks) { try { await scheduler.SetEnabledAsync(path, false, token); } catch (Exception ex) { LogService.Error("Defender scheduled task disable " + path, ex); } }
    }

    public static async Task RestoreDefenderAsync(CancellationToken token = default(CancellationToken))
    {
        var defenderTweaks = CatalogService.GetDefenderTweaks();
        await new TweakEngine().DetectAsync(defenderTweaks, token);
        foreach (var t in defenderTweaks) t.Pending = false;
        var errors = await new TweakEngine().ApplyAsync(defenderTweaks, token);
        if (errors.Count > 0) throw new InvalidOperationException("Defender restore nebyl plně ověřen: " + string.Join(" | ", errors));
        string[] tasks = { "\\Microsoft\\Windows\\Windows Defender\\Windows Defender Cache Maintenance", "\\Microsoft\\Windows\\Windows Defender\\Windows Defender Cleanup", "\\Microsoft\\Windows\\Windows Defender\\Windows Defender Scheduled Scan", "\\Microsoft\\Windows\\Windows Defender\\Windows Defender Verification" };
        var scheduler = new TaskSchedulerService();
        foreach (string path in tasks) { try { await scheduler.SetEnabledAsync(path, true, token); } catch (Exception ex) { LogService.Error("Defender scheduled task restore " + path, ex); } }
    }

    public static bool IsTamperProtectionOn()
    {
        try
        {
            using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows Defender\\Features");
            int num = Convert.ToInt32(registryKey?.GetValue("TamperProtection", 0) ?? ((object)0));
            return (uint)(num - 4) <= 1u;
        }
        catch (Exception ex)
        {
            LogService.Error("defender", "Tamper Protection state read failed", ex);
            return false;
        }
    }

    public static async Task RestoreBalancedPowerPlanAsync(CancellationToken token = default(CancellationToken))
    {
        await PowerPlanGuardService.DisableAsync(token);
        var result = await ProcessRunner.RunAsync("powercfg.exe", "/setactive SCHEME_BALANCED", TimeSpan.FromSeconds(10L), token);
        if (!result.Success) throw new InvalidOperationException(result.StdErr + result.StdOut);
        PowerPlanStateService.InvalidateCache();
    }

    public static async Task<string> GetMemoryCompressionAsync(CancellationToken token = default)
    {
        lock (MemoryCompressionLock)
        {
            if (_memoryCompressionCache != null && DateTime.UtcNow - _memoryCompressionCacheAt < MemoryCompressionCacheTtl)
                return _memoryCompressionCache;
        }

        if (PersistentHardwareCacheService.TryReadMachineStable<MemoryCompressionPersistentState>(
                MemoryCompressionPersistentKey, MemoryCompressionCacheTtl, out var persisted) &&
            persisted != null)
        {
            string persistedText = "Memory Compression: " + persisted.Enabled;
            lock (MemoryCompressionLock)
            {
                _memoryCompressionCache = persistedText;
                _memoryCompressionCacheAt = DateTime.UtcNow;
            }
            LogService.Info("performance", $"MEMORY_COMPRESSION_PERSISTENT_HIT enabled={persisted.Enabled}");
            return persistedText;
        }

        Task<bool> work;
        lock (MemoryCompressionLock)
        {
            if (_memoryCompressionCache != null && DateTime.UtcNow - _memoryCompressionCacheAt < MemoryCompressionCacheTtl)
                return _memoryCompressionCache;

            if (_memoryCompressionInFlight == null)
            {
                var created = ReadMemoryCompressionSerializedAsync();
                _memoryCompressionInFlight = created;
                work = created;
                _ = ObserveMemoryCompressionAsync(created);
            }
            else
            {
                work = _memoryCompressionInFlight;
            }
        }

        bool enabled = await work.WaitAsync(token).ConfigureAwait(false);
        return "Memory Compression: " + enabled;
    }

    internal static async Task<string> GetMemoryCompressionForUiAsync(CancellationToken token = default)
    {
        lock (MemoryCompressionLock)
        {
            if (_memoryCompressionCache != null)
            {
                TimeSpan age = DateTime.UtcNow - _memoryCompressionCacheAt;
                if (age < MemoryCompressionCacheTtl)
                    return _memoryCompressionCache;
                if (age < MemoryCompressionUiStaleTtl)
                {
                    QueueMemoryCompressionRefreshLocked();
                    LogService.Info("performance", $"MEMORY_COMPRESSION_UI_STALE_MEMORY_HIT ageMs={age.TotalMilliseconds:0}");
                    return _memoryCompressionCache;
                }
            }
        }

        if (PersistentHardwareCacheService.TryReadMachineStable<MemoryCompressionPersistentState>(
                MemoryCompressionPersistentKey, MemoryCompressionUiStaleTtl, out var persisted, out var persistedAge) &&
            persisted != null)
        {
            string text = "Memory Compression: " + persisted.Enabled;
            lock (MemoryCompressionLock)
            {
                _memoryCompressionCache = text;
                _memoryCompressionCacheAt = DateTime.UtcNow - persistedAge;
                if (persistedAge >= MemoryCompressionCacheTtl)
                    QueueMemoryCompressionRefreshLocked();
            }
            LogService.Info("performance", $"MEMORY_COMPRESSION_UI_PERSISTENT_HIT ageMs={persistedAge.TotalMilliseconds:0}");
            return text;
        }

        return await GetMemoryCompressionAsync(token).ConfigureAwait(false);
    }

    private static void QueueMemoryCompressionRefreshLocked()
    {
        if (_memoryCompressionInFlight is { IsCompleted: false }) return;
        var created = Task.Run(async () =>
        {
            await Task.Yield();
            return await ReadMemoryCompressionSerializedAsync().ConfigureAwait(false);
        });
        _memoryCompressionInFlight = created;
        _ = ObserveMemoryCompressionAsync(created);
    }

    private static async Task ObserveMemoryCompressionAsync(Task<bool> created)
    {
        try
        {
            bool result = await created.ConfigureAwait(false);
            PersistentHardwareCacheService.WriteMachineStable(
                MemoryCompressionPersistentKey,
                new MemoryCompressionPersistentState(result));
            lock (MemoryCompressionLock)
            {
                _memoryCompressionCache = "Memory Compression: " + result;
                _memoryCompressionCacheAt = DateTime.UtcNow;
                if (ReferenceEquals(_memoryCompressionInFlight, created))
                    _memoryCompressionInFlight = null;
            }
        }
        catch
        {
            lock (MemoryCompressionLock)
                if (ReferenceEquals(_memoryCompressionInFlight, created))
                    _memoryCompressionInFlight = null;
        }
    }

    internal static async Task RunMemoryCompressionCacheSmokeAsync(CancellationToken token)
    {
        PersistentHardwareCacheService.Remove(MemoryCompressionPersistentKey);
        lock (MemoryCompressionLock)
        {
            _memoryCompressionCache = null;
            _memoryCompressionCacheAt = default;
            _memoryCompressionInFlight = null;
        }

        var freshSw = Stopwatch.StartNew();
        string fresh = await GetMemoryCompressionAsync(token).ConfigureAwait(false);
        freshSw.Stop();

        lock (MemoryCompressionLock)
        {
            _memoryCompressionCache = null;
            _memoryCompressionCacheAt = default;
            _memoryCompressionInFlight = null;
        }

        var persistentSw = Stopwatch.StartNew();
        string persisted = await GetMemoryCompressionAsync(token).ConfigureAwait(false);
        persistentSw.Stop();

        if (!string.Equals(fresh, persisted, StringComparison.Ordinal))
            throw new InvalidOperationException($"Memory Compression persistent cache mismatch fresh={fresh} persisted={persisted}.");
        if (persistentSw.ElapsedMilliseconds > 150)
            throw new InvalidOperationException($"Memory Compression persistent cache is too slow: {persistentSw.ElapsedMilliseconds} ms.");

        LogService.Info($"MEMORY_COMPRESSION_CACHE_SMOKE_OK freshMs={freshSw.ElapsedMilliseconds} persistentMs={persistentSw.ElapsedMilliseconds} state={persisted}");
    }

    private static async Task<bool> ReadMemoryCompressionSerializedAsync()
    {
        await MemoryCompressionProviderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => GetMemoryCompressionNative(CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            MemoryCompressionProviderGate.Release();
        }
    }

    public static async Task SetMemoryCompressionAsync(bool enabled, CancellationToken token = default)
    {
        await MemoryCompressionProviderGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() => SetMemoryCompressionNative(enabled, token), token).ConfigureAwait(false);
            PersistentHardwareCacheService.WriteMachineStable(
                MemoryCompressionPersistentKey,
                new MemoryCompressionPersistentState(enabled));
            lock (MemoryCompressionLock)
            {
                _memoryCompressionCache = "Memory Compression: " + enabled;
                _memoryCompressionCacheAt = DateTime.UtcNow;
            }
        }
        finally
        {
            MemoryCompressionProviderGate.Release();
        }
        MemoryStateService.Invalidate();
    }

    private static ManagementScope GetMemoryCompressionScope()
    {
        if (_memoryCompressionScope is { IsConnected: true })
            return _memoryCompressionScope;

        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\PS_MMAgent");
        scope.Connect();
        _memoryCompressionScope = scope;
        return scope;
    }

    private static bool GetMemoryCompressionNative(CancellationToken token)
    {
        if (!WindowsCapabilityService.Supports("ps_mmagent")) throw new PlatformNotSupportedException("Memory Compression provider PS_MMAgent není na tomto Windows buildu dostupný.");
        token.ThrowIfCancellationRequested();
        try
        {
            var scope = GetMemoryCompressionScope();
            using var cls = new ManagementClass(scope, new ManagementPath("PS_MMAgent"), null);
            using var output = cls.InvokeMethod("Get", null, null);
            if (output?["CmdletOutput"] is not ManagementBaseObject state)
                throw new InvalidOperationException("MMAgent nevrátil stav Memory Compression.");
            return Convert.ToBoolean(state["MemoryCompression"] ?? false);
        }
        catch (ManagementException)
        {
            _memoryCompressionScope = null;
            throw;
        }
    }

    private static void SetMemoryCompressionNative(bool enabled, CancellationToken token)
    {
        if (!WindowsCapabilityService.Supports("ps_mmagent")) throw new PlatformNotSupportedException("Memory Compression provider PS_MMAgent není na tomto Windows buildu dostupný.");
        token.ThrowIfCancellationRequested();
        var scope = GetMemoryCompressionScope();
        using var cls = new ManagementClass(scope, new ManagementPath("PS_MMAgent"), null);
        string method = enabled ? "Enable" : "Disable";
        using var input = cls.GetMethodParameters(method);
        input["MemoryCompression"] = true;
        using var output = cls.InvokeMethod(method, input, null);
        if (output != null && output["ReturnValue"] != null && Convert.ToUInt32(output["ReturnValue"]) != 0)
            throw new InvalidOperationException($"MMAgent {method} selhal: {output["ReturnValue"]}");
        if (GetMemoryCompressionNative(token) != enabled)
            throw new InvalidOperationException("Memory Compression read-back nepotvrdil požadovaný stav.");
    }

    private const string UsbPowerSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSelectiveSuspendSetting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    public static async Task<bool?> GetUsbSelectiveSuspendDisabledAsync(CancellationToken token = default)
    {
        var query = await ProcessRunner.RunAsync(
            "powercfg.exe",
            $"/query SCHEME_CURRENT {UsbPowerSubgroup} {UsbSelectiveSuspendSetting}",
            TimeSpan.FromSeconds(15),
            token).ConfigureAwait(false);
        if (!query.Success) return null;

        Match ac = Regex.Match(
            query.StdOut ?? string.Empty,
            @"Current AC Power Setting Index:\s*(0x[0-9a-fA-F]+)",
            RegexOptions.IgnoreCase);
        if (!ac.Success) return null;

        uint actual = Convert.ToUInt32(ac.Groups[1].Value[2..], 16);
        return actual == 0;
    }

    public static async Task SetUsbSelectiveSuspendAsync(bool disabled, CancellationToken token = default)
    {
        var value = disabled ? 0 : 1;
        var r = await ProcessRunner.RunAsync("powercfg.exe", $"/setacvalueindex SCHEME_CURRENT {UsbPowerSubgroup} {UsbSelectiveSuspendSetting} {value}", TimeSpan.FromSeconds(15), token);
        if (!r.Success) throw new InvalidOperationException(r.StdErr + r.StdOut);
        var active = await ProcessRunner.RunAsync("powercfg.exe", "/setactive SCHEME_CURRENT", TimeSpan.FromSeconds(15), token);
        if (!active.Success) throw new InvalidOperationException(active.StdErr + active.StdOut);
        await VerifyCurrentPowerOptionAsync(UsbPowerSubgroup, UsbSelectiveSuspendSetting, value, token);
        PowerPlanStateService.InvalidateCache();
    }

    public static async Task<string> GetActivePowerPlanAsync(CancellationToken token = default)
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", "/getactivescheme", TimeSpan.FromSeconds(10), token);
        return string.IsNullOrWhiteSpace(r.StdOut) ? "Aktivní plán nelze načíst." : r.StdOut.Trim();
    }

    public static async Task SetCurrentPowerOptionAsync(string subgroup, string setting, int value, CancellationToken token = default)
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", $"/setacvalueindex SCHEME_CURRENT {subgroup} {setting} {value}", TimeSpan.FromSeconds(15), token);
        if (!r.Success) throw new InvalidOperationException(r.StdErr + r.StdOut);
        var active = await ProcessRunner.RunAsync("powercfg.exe", "/setactive SCHEME_CURRENT", TimeSpan.FromSeconds(15), token);
        if (!active.Success) throw new InvalidOperationException(active.StdErr + active.StdOut);
        await VerifyCurrentPowerOptionAsync(subgroup, setting, value, token);
        PowerPlanStateService.InvalidateCache();
    }

    private static async Task VerifyCurrentPowerOptionAsync(string subgroup, string setting, int expectedValue, CancellationToken token)
    {
        var query = await ProcessRunner.RunAsync("powercfg.exe", $"/query SCHEME_CURRENT {subgroup} {setting}", TimeSpan.FromSeconds(15), token);
        if (!query.Success) throw new InvalidOperationException("Nelze ověřit power setting: " + query.StdErr + query.StdOut);
        Match ac = Regex.Match(
            query.StdOut ?? string.Empty,
            @"Current AC Power Setting Index:\s*(0x[0-9a-fA-F]+)",
            RegexOptions.IgnoreCase);
        if (!ac.Success)
            throw new InvalidOperationException($"Read-back {setting} nevrátil AC index.");

        uint actual = Convert.ToUInt32(ac.Groups[1].Value[2..], 16);
        if (actual != (uint)expectedValue)
            throw new InvalidOperationException($"Read-back {setting} nepotvrdil AC hodnotu {expectedValue}; aktuálně {actual}.");
    }

    public static Task FlushDnsAsync(CancellationToken token = default) =>
        ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(20), token);

    public static Task ResetWinsockAsync(CancellationToken token = default) =>
        ProcessRunner.RunAsync("netsh.exe", "winsock reset", TimeSpan.FromSeconds(30), token);

    public static Task RetrimAllAsync(CancellationToken token = default) =>
        ProcessRunner.RunAsync("defrag.exe", "/C /L /O /U", TimeSpan.FromMinutes(20), token);

    public static async Task RemoveAppPackageAsync(string packageFamilyOrName, CancellationToken token = default(CancellationToken))
    {
        PackageManager pm = new PackageManager();
        List<Package> list = (from p in pm.FindPackagesForUser(string.Empty)
                              where p.Id.Name.Contains(packageFamilyOrName, StringComparison.OrdinalIgnoreCase) || p.Id.FamilyName.Contains(packageFamilyOrName, StringComparison.OrdinalIgnoreCase)
                              select p).ToList();
        foreach (Package item in list)
        {
            token.ThrowIfCancellationRequested();
            await pm.RemovePackageAsync(item.Id.FullName).AsTask(token);
        }
    }

    public static async Task RunSfcAsync(CancellationToken token = default(CancellationToken))
    {
        await WorkerClientService.RunAsync<WorkerAck>("sfc", timeout: TimeSpan.FromMinutes(35), token: token).ConfigureAwait(false);
    }

    public static async Task RunDismRepairAsync(CancellationToken token = default(CancellationToken))
    {
        await WorkerClientService.RunAsync<WorkerAck>("dism-repair", timeout: TimeSpan.FromMinutes(50), token: token).ConfigureAwait(false);
    }
}
