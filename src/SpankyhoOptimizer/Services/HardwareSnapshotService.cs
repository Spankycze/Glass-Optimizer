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
    // PnP events can invalidate a snapshot while the asynchronous native scan
    // is still running. Never publish an inventory from an older generation.
    private static readonly object SnapshotLock = new();
    private static long _generation;
    private static int _presentDeviceScanCount;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PresentDevicesPersistentTtl = TimeSpan.FromHours(2);
    private const string PresentDevicesPersistentKey = "present-devices-v1";
    internal static int PresentDeviceScanCountForDiagnostics => Volatile.Read(ref _presentDeviceScanCount);

    public static async Task<HardwareSnapshot> GetAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        lock (SnapshotLock)
        {
            if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < Ttl)
                return _cache;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                long generation;
                lock (SnapshotLock)
                {
                    if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < Ttl)
                        return _cache;
                    generation = _generation;
                }

                var overviewTask = SystemInfoService.GetSnapshotAsync(forceRefresh);
                var gpuTask = GpuDriverService.DetectPrimaryGpuAsync(token);
                var devicesTask = GetPresentDevicesAsync(forceRefresh, generation, token);
                await Task.WhenAll(overviewTask, gpuTask, devicesTask).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var snapshot = new HardwareSnapshot(await overviewTask, await gpuTask, await devicesTask,
                    Environment.OSVersion.Version.Build, DateTime.UtcNow);
                lock (SnapshotLock)
                {
                    if (generation != _generation)
                    {
                        // A device was connected/removed during this scan.
                        // Start over rather than republishing stale hardware.
                        LogService.Info("hardware", "HARDWARE_SNAPSHOT_RETRY reason=pnp-generation-changed");
                        continue;
                    }
                    _cache = snapshot;
                    _cacheAt = DateTime.UtcNow;
                }
                LogService.Info("performance",
                    $"HARDWARE_SNAPSHOT_PHASE overviewDone={overviewTask.IsCompletedSuccessfully} gpuDone={gpuTask.IsCompletedSuccessfully} devices={snapshot.Devices.Count} totalMs={sw.ElapsedMilliseconds}");
                return snapshot;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    // An immutable cached Overview task is already completed in most
    // navigations. Return that exact task instead of allocating an async
    // state machine + continuation for each cached Overview entry.
    public static Task<OverviewSnapshot> GetOverviewAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task<OverviewSnapshot> snapshot = SystemInfoService.GetSnapshotAsync(forceRefresh);
        if (snapshot.IsCompletedSuccessfully)
        {
            // Preserve the previous API contract: a cancellation occurring
            // after the initial check but before completed-task return wins.
            token.ThrowIfCancellationRequested();
            return snapshot;
        }
        return AwaitOverviewSnapshotAsync(snapshot, token);
    }

    private static async Task<OverviewSnapshot> AwaitOverviewSnapshotAsync(
        Task<OverviewSnapshot> snapshot, CancellationToken token)
    {
        OverviewSnapshot value = await snapshot.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return value;
    }

    public static Task<HardwareSnapshot> GetAsync(CancellationToken token) => GetAsync(false, token);

    private static async Task<IReadOnlyList<DeviceInfo>> GetPresentDevicesAsync(bool forceRefresh, long generation, CancellationToken token)
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

        // The native scan can take ~100ms. If PnP changed mid-scan, never
        // resurrect its old inventory in the persistent cache. Serialize
        // the file commit with invalidation/removal of that same cache key.
        lock (SnapshotLock)
        {
            if (generation == _generation)
                PersistentHardwareCacheService.WriteMachineStable(PresentDevicesPersistentKey, devices);
        }
        LogService.Info("performance", $"PRESENT_DEVICES_CACHE hit=false count={devices.Length} ms={sw.ElapsedMilliseconds}");
        return devices;
    }

    public static void TrimMemoryCache()
    {
        // A memory-only trim is not a PnP event. The stable on-disk inventory
        // remains reusable, while publication stays serialized with readers.
        lock (SnapshotLock)
        {
            _cache = null;
            _cacheAt = default;
        }
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

        // Simulate a startup-settle PnP notification: invalidate the in-memory
        // inventory/generation but retain valid persistent device data.
        long staleGeneration;
        lock (SnapshotLock) staleGeneration = _generation;
        InvalidateVolatile(dropPersistentDevices: false);
        lock (SnapshotLock)
        {
            if (_generation == staleGeneration || _cache != null)
                throw new InvalidOperationException("PnP generation did not invalidate the active hardware snapshot.");
        }
        var afterPnp = await GetAsync(false, token).ConfigureAwait(false);
        if (ReferenceEquals(afterPnp, persisted) ||
            afterPnp.Devices.Count != persisted.Devices.Count ||
            PresentDeviceScanCountForDiagnostics != scansAfterTrimReload)
            throw new InvalidOperationException("PnP memory invalidation lost the reusable persisted device snapshot.");

        LogService.Info("performance", $"HARDWARE_MEMORY_TRIM_CACHE_SMOKE_OK devices={fresh.Devices.Count} nativeScans={scansAfterTrimReload - scansBefore} pnpGenerationChanged=true persistentRetained=true");
    }

    public static void InvalidateVolatile() => InvalidateVolatile(true);

    public static void InvalidateVolatile(bool dropPersistentDevices)
    {
        lock (SnapshotLock)
        {
            _generation++;
            _cache = null;
            _cacheAt = default;
            if (dropPersistentDevices)
                PersistentHardwareCacheService.Remove(PresentDevicesPersistentKey);
            // Keep SystemInfo's dynamic cache invalidation in the same
            // publication barrier as the device generation transition.
            SystemInfoService.InvalidateDynamic();
        }
    }

    public static void Invalidate()
    {
        lock (SnapshotLock)
        {
            _generation++;
            _cache = null;
            _cacheAt = default;
            PersistentHardwareCacheService.Remove(PresentDevicesPersistentKey);
            SystemInfoService.Invalidate();
        }
    }
}
