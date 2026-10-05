using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record HardwareSnapshot(
    OverviewSnapshot Overview,
    GpuDriverInfo? Gpu,
    IReadOnlyList<DeviceInfo> Devices,
    int WindowsBuild,
    DateTime CapturedUtc)
{
    public bool HasClass(string value) => Devices.Any(x => x.Present && x.Class.Contains(value, StringComparison.OrdinalIgnoreCase));
    public bool HasName(string value) => Devices.Any(x => x.Present && x.FriendlyName.Contains(value, StringComparison.OrdinalIgnoreCase));
}

public static class HardwareSnapshotService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static HardwareSnapshot? _cache;
    private static DateTime _cacheAt;
    private static int _presentDeviceScanCount;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PresentDevicesPersistentTtl = TimeSpan.FromHours(2);
    private const string PresentDevicesPersistentKey = "present-devices-v1";
    internal static int PresentDeviceScanCountForDiagnostics => Volatile.Read(ref _presentDeviceScanCount);

    public static async Task<HardwareSnapshot> GetAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < Ttl) return _cache;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Gate.WaitAsync(token);
        try
        {
            if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < Ttl) return _cache;
            var overviewTask = SystemInfoService.GetSnapshotAsync(forceRefresh);
            var gpuTask = GpuDriverService.DetectPrimaryGpuAsync(token);
            var devicesTask = GetPresentDevicesAsync(forceRefresh, token);
            await Task.WhenAll(overviewTask, gpuTask, devicesTask);
            long totalMs = sw.ElapsedMilliseconds;
            var snapshot = new HardwareSnapshot(await overviewTask, await gpuTask, await devicesTask,
                Environment.OSVersion.Version.Build, DateTime.UtcNow);
            _cache = snapshot;
            _cacheAt = DateTime.UtcNow;
            LogService.Info("performance",
                $"HARDWARE_SNAPSHOT_PHASE overviewDone={overviewTask.IsCompletedSuccessfully} gpuDone={gpuTask.IsCompletedSuccessfully} devices={snapshot.Devices.Count} totalMs={totalMs}");
            return snapshot;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<OverviewSnapshot> GetOverviewAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = await SystemInfoService.GetSnapshotAsync(forceRefresh);
        token.ThrowIfCancellationRequested();
        return snapshot;
    }

    public static Task<HardwareSnapshot> GetAsync(CancellationToken token) => GetAsync(false, token);

    private static async Task<IReadOnlyList<DeviceInfo>> GetPresentDevicesAsync(bool forceRefresh, CancellationToken token)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!forceRefresh &&
            PersistentHardwareCacheService.TryReadMachineStable<DeviceInfo[]>(
                PresentDevicesPersistentKey, PresentDevicesPersistentTtl, out var persisted) &&
            persisted != null)
        {
            LogService.Info("performance", $"PRESENT_DEVICES_CACHE hit=true count={persisted.Length} ms={sw.ElapsedMilliseconds}");
            return persisted;
        }

        var devices = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _presentDeviceScanCount);
            return NativeCore.GetPresentDevices().Where(x => x.Present).ToArray();
        }, token).ConfigureAwait(false);

        PersistentHardwareCacheService.WriteMachineStable(PresentDevicesPersistentKey, devices);
        LogService.Info("performance", $"PRESENT_DEVICES_CACHE hit=false count={devices.Length} ms={sw.ElapsedMilliseconds}");
        return devices;
    }

    public static void TrimMemoryCache()
    {
        _cache = null;
        _cacheAt = default;
    }

    internal static async Task RunMemoryTrimPersistentCacheSmokeAsync(CancellationToken token)
    {
        Invalidate();
        int scansBefore = PresentDeviceScanCountForDiagnostics;
        var fresh = await GetAsync(false, token).ConfigureAwait(false);
        int scansAfterFresh = PresentDeviceScanCountForDiagnostics;
        if (scansAfterFresh != scansBefore + 1 || fresh.Devices.Count == 0)
            throw new InvalidOperationException($"Hardware memory-trim smoke failed to establish fresh snapshot: before={scansBefore} after={scansAfterFresh} devices={fresh.Devices.Count}.");

        TrimMemoryCache();
        var persisted = await GetAsync(false, token).ConfigureAwait(false);
        int scansAfterTrimReload = PresentDeviceScanCountForDiagnostics;
        if (scansAfterTrimReload != scansAfterFresh)
            throw new InvalidOperationException($"Memory trim dropped persistent present-device cache: freshScans={scansAfterFresh} afterTrim={scansAfterTrimReload}.");
        if (persisted.Devices.Count != fresh.Devices.Count)
            throw new InvalidOperationException($"Hardware memory-trim snapshot count mismatch: fresh={fresh.Devices.Count} persisted={persisted.Devices.Count}.");

        LogService.Info("performance", $"HARDWARE_MEMORY_TRIM_CACHE_SMOKE_OK devices={fresh.Devices.Count} nativeScans={scansAfterTrimReload - scansBefore}");
    }

    public static void InvalidateVolatile() => InvalidateVolatile(true);

    public static void InvalidateVolatile(bool dropPersistentDevices)
    {
        _cache = null;
        _cacheAt = default;
        if (dropPersistentDevices)
            PersistentHardwareCacheService.Remove(PresentDevicesPersistentKey);
        // PnP/device changes can affect GPU, storage, network and the present-device set,
        // but they do not require throwing away the machine-stable CPU/baseboard/RAM identity.
        SystemInfoService.InvalidateDynamic();
    }

    public static void Invalidate()
    {
        _cache = null;
        _cacheAt = default;
        PersistentHardwareCacheService.Remove(PresentDevicesPersistentKey);
        SystemInfoService.Invalidate();
    }
}
