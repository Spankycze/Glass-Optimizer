using System;
using System.Collections.Generic;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record MemoryStateSnapshot(
    bool CompressionEnabled,
    bool? AutomaticManagedPagefile,
    string PagefileSummary,
    int LargeSystemCache,
    int Win32PrioritySeparation,
    double TotalRamGb,
    double AvailableRamGb);

public static class MemoryStateService
{
    private static readonly object CacheLock = new();
    private static MemoryStateSnapshot? _cache;
    private static DateTime _cacheAt;
    private static Task<MemoryStateSnapshot>? _inFlight;

    public static Task<MemoryStateSnapshot> ReadAsync(CancellationToken token = default)
    {
        lock (CacheLock)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(10)) return Task.FromResult(_cache);
            _inFlight ??= ReadCoreAsync();
            return _inFlight.WaitAsync(token);
        }
    }

    private static async Task<MemoryStateSnapshot> ReadCoreAsync()
    {
        try
        {
            bool compression = (await WindowsActions.GetMemoryCompressionAsync(CancellationToken.None).ConfigureAwait(false))
                .Contains("True", StringComparison.OrdinalIgnoreCase);
            var result = await Task.Run(() => ReadNative(compression, CancellationToken.None)).ConfigureAwait(false);
            lock (CacheLock) { _cache = result; _cacheAt = DateTime.UtcNow; }
            return result;
        }
        finally { lock (CacheLock) _inFlight = null; }
    }

    public static void Invalidate()
    {
        lock (CacheLock) { _cache = null; _cacheAt = default; }
    }
    private static MemoryStateSnapshot ReadNative(bool compression, CancellationToken token)
    {
        bool? autoPagefile = null;
        double totalGb = 0, availableGb = 0;
        var pagefiles = new List<string>();
        int largeSystemCache = ReadDword("SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management", "LargeSystemCache");
        int priority = ReadDword("SYSTEM\\CurrentControlSet\\Control\\PriorityControl", "Win32PrioritySeparation");

        var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = true };
        try
        {
            using var q = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\CIMV2"),
                new ObjectQuery("SELECT AutomaticManagedPagefile,TotalPhysicalMemory FROM Win32_ComputerSystem"), options);
            using var results = q.Get();
            foreach (ManagementObject x in results)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (x["AutomaticManagedPagefile"] != null) autoPagefile = Convert.ToBoolean(x["AutomaticManagedPagefile"]);
                    if (x["TotalPhysicalMemory"] != null) totalGb = Convert.ToDouble(x["TotalPhysicalMemory"]) / 1073741824d;
                }
                finally { x.Dispose(); }
                break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LogService.Error("memory", "ComputerSystem memory state read failed", ex); }
        try
        {
            using var q = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\CIMV2"),
                new ObjectQuery("SELECT FreePhysicalMemory,TotalVisibleMemorySize FROM Win32_OperatingSystem"), options);
            using var results = q.Get();
            foreach (ManagementObject x in results)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (x["FreePhysicalMemory"] != null) availableGb = Convert.ToDouble(x["FreePhysicalMemory"]) * 1024d / 1073741824d;
                    if (totalGb <= 0 && x["TotalVisibleMemorySize"] != null) totalGb = Convert.ToDouble(x["TotalVisibleMemorySize"]) * 1024d / 1073741824d;
                }
                finally { x.Dispose(); }
                break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LogService.Error("memory", "OperatingSystem memory state read failed", ex); }

        try
        {
            using var q = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\CIMV2"),
                new ObjectQuery("SELECT Name,AllocatedBaseSize,CurrentUsage,PeakUsage FROM Win32_PageFileUsage"), options);
            using var results = q.Get();
            foreach (ManagementObject x in results)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    string name = x["Name"]?.ToString() ?? "pagefile";
                    int allocated = Convert.ToInt32(x["AllocatedBaseSize"] ?? 0);
                    int used = Convert.ToInt32(x["CurrentUsage"] ?? 0);
                    pagefiles.Add($"{name}: {used}/{allocated} MB");
                }
                finally { x.Dispose(); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LogService.Error("memory", "Pagefile usage read failed", ex); }
        string pagefileSummary = pagefiles.Count == 0
            ? (autoPagefile == true ? "System managed" : autoPagefile == false ? "Manual / disabled" : "Nezjištěno")
            : string.Join(" • ", pagefiles);
        return new MemoryStateSnapshot(compression, autoPagefile, pagefileSummary,
            largeSystemCache, priority, totalGb, availableGb);
    }

    public static async Task<MemoryStateSnapshot> ApplyRecommendedAsync(CancellationToken token = default)
    {
        var before = await ReadAsync(token).ConfigureAwait(false);
        bool compressionChanged = false;
        bool pagefileChanged = false;
        bool cacheChanged = false;

        try
        {
            if (!before.CompressionEnabled)
            {
                await WindowsActions.SetMemoryCompressionAsync(true, token).ConfigureAwait(false);
                compressionChanged = true;
            }

            if (before.AutomaticManagedPagefile == false)
            {
                await Task.Run(() => SetAutomaticManagedPagefile(true), token).ConfigureAwait(false);
                pagefileChanged = true;
            }

            if (before.LargeSystemCache != 0)
            {
                await Task.Run(() => SetLargeSystemCache(0), token).ConfigureAwait(false);
                cacheChanged = true;
            }

            Invalidate();
            var after = await ReadAsync(token).ConfigureAwait(false);
            if (!after.CompressionEnabled)
                throw new InvalidOperationException("Memory Compression read-back nepotvrdil ON.");
            if (before.AutomaticManagedPagefile == false && after.AutomaticManagedPagefile != true)
                throw new InvalidOperationException("Pagefile read-back nepotvrdil System managed.");
            if (after.LargeSystemCache != 0)
                throw new InvalidOperationException("LargeSystemCache read-back nepotvrdil hodnotu 0.");

            LogService.Info("memory",
                $"MEMORY_RECOMMENDED_OK compression={after.CompressionEnabled} pagefile={after.AutomaticManagedPagefile} largeSystemCache={after.LargeSystemCache}");
            return after;
        }
        catch
        {
            try
            {
                if (cacheChanged) await Task.Run(() => SetLargeSystemCache(before.LargeSystemCache), CancellationToken.None).ConfigureAwait(false);
                if (pagefileChanged && before.AutomaticManagedPagefile.HasValue)
                    await Task.Run(() => SetAutomaticManagedPagefile(before.AutomaticManagedPagefile.Value), CancellationToken.None).ConfigureAwait(false);
                if (compressionChanged)
                    await WindowsActions.SetMemoryCompressionAsync(before.CompressionEnabled, CancellationToken.None).ConfigureAwait(false);
                Invalidate();
            }
            catch (Exception rollbackEx)
            {
                LogService.Error("memory", "Memory Recommended rollback failed", rollbackEx);
            }
            throw;
        }
    }

    private static void SetAutomaticManagedPagefile(bool enabled)
    {
        var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = false };
        using var q = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\CIMV2"),
            new ObjectQuery("SELECT * FROM Win32_ComputerSystem"), options);
        using var results = q.Get();
        foreach (ManagementObject x in results)
        {
            try
            {
                x["AutomaticManagedPagefile"] = enabled;
                x.Put();
                return;
            }
            finally { x.Dispose(); }
        }
        throw new InvalidOperationException("Win32_ComputerSystem není dostupný pro změnu pagefile režimu.");
    }

    private static void SetLargeSystemCache(int value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", true)
            ?? throw new InvalidOperationException("Memory Management registry není dostupný.");
        key.SetValue("LargeSystemCache", value, RegistryValueKind.DWord);
        int readBack = Convert.ToInt32(key.GetValue("LargeSystemCache", -1));
        if (readBack != value)
            throw new InvalidOperationException($"LargeSystemCache read-back={readBack}, požadováno={value}.");
    }

    private static int ReadDword(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, false);
            object? raw = key?.GetValue(name);
            return raw == null ? 0 : Convert.ToInt32(raw);
        }
        catch (Exception ex)
        {
            LogService.Error("memory", $"Registry state read failed: {path}\\{name}", ex);
            return 0;
        }
    }
}
