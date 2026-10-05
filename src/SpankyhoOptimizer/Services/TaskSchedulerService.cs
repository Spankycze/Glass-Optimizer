using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed class TaskSchedulerService
{
    private static readonly object CacheLock = new();
    private static readonly SemaphoreSlim LoadGate = new(1, 1);
    private static readonly SemaphoreSlim BasicLoadGate = new(1, 1);
    private static IReadOnlyList<TaskInfo>? _cache;
    private static DateTime _cacheAt;
    private static IReadOnlyList<TaskInfo>? _basicCache;
    private static DateTime _basicCacheAt;
    private static int _scanCount;
    private static int _basicScanCount;
    private static int _cacheGeneration;
    private static bool _needsBackgroundRefresh;
    private static bool _basicNeedsBackgroundRefresh;
    private const string PersistentCacheKey = "task-scheduler-inventory-v2";
    private const string BasicPersistentCacheKey = "task-scheduler-basic-inventory-v1";
    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PersistentCacheTtl = TimeSpan.FromMinutes(30);

    public async Task SetEnabledAsync(string path, bool enabled, CancellationToken token = default)
    {
        ValidatePath(path);
        await Task.Run(() => TaskSchedulerComService.SetEnabled(path, enabled, token), token).ConfigureAwait(false);
        LogService.Info("tasks", $"Task state changed via COM: {path}, enabled={enabled}");
        UpdateCachedStates(new[] { (path, enabled) });
    }
    public async Task<string[]> SetEnabledBatchAsync(IReadOnlyList<(string Path, bool Enabled)> changes, CancellationToken token = default)
    {
        foreach (var change in changes) ValidatePath(change.Path);
        var result = await Task.Run(() => TaskSchedulerComService.SetEnabledBatch(changes, token), CancellationToken.None).WaitAsync(token).ConfigureAwait(false);
        if (result.Length == 0)
            UpdateCachedStates(changes);
        else
            InvalidateCache();
        LogService.Info("tasks", $"Task batch changed via COM: requested={changes.Count} errors={result.Length}");
        return result;
    }

    public async Task<bool> GetEnabledAsync(string path, CancellationToken token = default)
    {
        ValidatePath(path);
        // Enabled-state detection does not need task XML/startup-trigger enrichment.
        // Prefer the basic inventory so tweak detection avoids hundreds of COM Xml reads.
        var snapshot = TryGetBasicFreshSnapshot() ?? await GetBasicAsync(forceRefresh: false, token);
        var item = snapshot.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        return item?.Enabled ?? false;
    }

    public static IReadOnlyList<TaskInfo>? TryGetFreshSnapshot()
    {
        lock (CacheLock)
            return _cache != null && DateTime.UtcNow - _cacheAt < MemoryCacheTtl ? _cache : null;
    }

    public static IReadOnlyList<TaskInfo>? TryGetBasicFreshSnapshot()
    {
        lock (CacheLock)
        {
            if (_basicCache != null && DateTime.UtcNow - _basicCacheAt < MemoryCacheTtl)
                return _basicCache;
            if (_cache != null && DateTime.UtcNow - _cacheAt < MemoryCacheTtl)
                return _cache;
            return null;
        }
    }

    public static Task<bool> TryWarmBasicDisplaySnapshotAsync(CancellationToken token = default) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return TryGetBasicDisplaySnapshot() != null;
        }, token);

    public static Task<bool> TryWarmDisplaySnapshotAsync(CancellationToken token = default) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return TryGetDisplaySnapshot() != null;
        }, token);

    public static IReadOnlyList<TaskInfo>? TryGetBasicDisplaySnapshot()
    {
        var memory = TryGetBasicFreshSnapshot();
        if (memory != null) return memory;

        TaskInfo[]? persisted = null;
        if (!PersistentHardwareCacheService.TryReadMachineStable<TaskInfo[]>(
                BasicPersistentCacheKey, PersistentCacheTtl, out persisted) || persisted == null)
        {
            _ = PersistentHardwareCacheService.TryReadMachineStable<TaskInfo[]>(
                PersistentCacheKey, PersistentCacheTtl, out persisted);
        }
        if (persisted == null) return null;

        lock (CacheLock)
        {
            _basicCache = persisted;
            _basicCacheAt = DateTime.UtcNow;
            _basicNeedsBackgroundRefresh = true;
        }
        LogService.Info("tasks", $"Persistent basic task inventory display hit count={persisted.Length} refreshPending=true");
        return persisted;
    }

    public static IReadOnlyList<TaskInfo>? TryGetDisplaySnapshot()
    {
        var memory = TryGetFreshSnapshot();
        if (memory != null) return memory;

        if (!PersistentHardwareCacheService.TryReadMachineStable<TaskInfo[]>(
                PersistentCacheKey, PersistentCacheTtl, out var persisted) ||
            persisted == null)
            return null;

        lock (CacheLock)
        {
            _cache = persisted;
            _cacheAt = DateTime.UtcNow;
            _needsBackgroundRefresh = true;
        }
        LogService.Info("tasks", $"Persistent task inventory display hit count={persisted.Length} refreshPending=true");
        return persisted;
    }
    public async Task<IReadOnlyList<TaskInfo>> GetBasicAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        DateTime requestStartedAt = DateTime.UtcNow;
        lock (CacheLock)
        {
            if (!forceRefresh && _basicCache != null && DateTime.UtcNow - _basicCacheAt < MemoryCacheTtl)
                return _basicCache;
            if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < MemoryCacheTtl)
                return _cache;
        }

        if (!forceRefresh &&
            PersistentHardwareCacheService.TryReadMachineStable<TaskInfo[]>(
                BasicPersistentCacheKey, PersistentCacheTtl, out var persisted) &&
            persisted != null)
        {
            lock (CacheLock)
            {
                _basicCache = persisted;
                _basicCacheAt = DateTime.UtcNow;
                _basicNeedsBackgroundRefresh = true;
            }
            LogService.Info("tasks", $"Persistent basic task inventory cache hit count={persisted.Length} refreshPending=true");
            return persisted;
        }

        await BasicLoadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (CacheLock)
            {
                if (_basicCache != null && DateTime.UtcNow - _basicCacheAt < MemoryCacheTtl &&
                    (!forceRefresh || _basicCacheAt >= requestStartedAt))
                    return _basicCache;
                if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < MemoryCacheTtl)
                    return _cache;
            }

            int generation;
            lock (CacheLock) generation = _cacheGeneration;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Interlocked.Increment(ref _basicScanCount);
            IReadOnlyList<TaskInfo> parsed = await Task.Run(
                () => TaskSchedulerComService.GetAll(token, includeStartupTrigger: false),
                token).ConfigureAwait(false);
            sw.Stop();
            LogService.Info("tasks", $"COM basic inventory count={parsed.Count} ms={sw.Elapsed.TotalMilliseconds:0.0}");
            bool publish;
            lock (CacheLock)
            {
                publish = generation == _cacheGeneration;
                if (publish)
                {
                    _basicCache = parsed;
                    _basicCacheAt = DateTime.UtcNow;
                    _basicNeedsBackgroundRefresh = false;
                }
            }
            if (publish)
                PersistentHardwareCacheService.WriteMachineStable(BasicPersistentCacheKey, parsed.ToArray());
            else
                LogService.Info("tasks", $"TASK_BASIC_STALE_RESULT_DROPPED generation={generation} current={_cacheGeneration}");
            return parsed;
        }
        finally { BasicLoadGate.Release(); }
    }

    public async Task<IReadOnlyList<TaskInfo>> GetAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        DateTime requestStartedAt = DateTime.UtcNow;
        lock (CacheLock)
        {
            if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < MemoryCacheTtl)
                return _cache;
        }
        if (!forceRefresh && PersistentHardwareCacheService.TryReadMachineStable<TaskInfo[]>(PersistentCacheKey, PersistentCacheTtl, out var persisted) && persisted != null)
        {
            lock (CacheLock)
            {
                _cache = persisted;
                _cacheAt = DateTime.UtcNow;
                _needsBackgroundRefresh = true;
            }
            LogService.Info("tasks", $"Persistent task inventory cache hit count={persisted.Length} refreshPending=true");
            return persisted;
        }
        await LoadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (CacheLock)
            {
                if (_cache != null && DateTime.UtcNow - _cacheAt < MemoryCacheTtl &&
                    (!forceRefresh || _cacheAt >= requestStartedAt))
                    return _cache;
            }
            int generation;
            lock (CacheLock) generation = _cacheGeneration;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Interlocked.Increment(ref _scanCount);
            IReadOnlyList<TaskInfo> parsed = await Task.Run(() => TaskSchedulerComService.GetAll(token), token).ConfigureAwait(false);
            sw.Stop();
            LogService.Info("tasks", $"COM inventory count={parsed.Count} ms={sw.Elapsed.TotalMilliseconds:0.0}");
            bool publish;
            lock (CacheLock)
            {
                publish = generation == _cacheGeneration;
                if (publish)
                {
                    _cache = parsed;
                    _cacheAt = DateTime.UtcNow;
                    _needsBackgroundRefresh = false;
                }
            }
            if (publish)
                PersistentHardwareCacheService.WriteMachineStable(PersistentCacheKey, parsed.ToArray());
            else
                LogService.Info("tasks", $"TASK_STALE_RESULT_DROPPED generation={generation} current={_cacheGeneration}");
            return parsed;
        }
        finally { LoadGate.Release(); }
    }
    private static TaskInfo[] MergeCachedStates(IReadOnlyList<TaskInfo> source, IReadOnlyList<(string Path, bool Enabled)> changes)
    {
        var items = source.ToArray();
        var index = items
            .Select((item, i) => (item.Path, Index: i))
            .ToDictionary(x => x.Path, x => x.Index, StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
            if (index.TryGetValue(change.Path, out int i))
                items[i] = items[i] with { Enabled = change.Enabled };
        return items;
    }

    private static void UpdateCachedStates(IReadOnlyList<(string Path, bool Enabled)> changes)
    {
        TaskInfo[]? persisted = null;
        TaskInfo[]? basicPersisted = null;
        lock (CacheLock)
        {
            // Invalidate any in-flight scans so an older COM result cannot overwrite the
            // state we have just verified through SetEnabled/SetEnabledBatch.
            _cacheGeneration++;

            if (_cache != null)
            {
                persisted = MergeCachedStates(_cache, changes);
                _cache = persisted;
                _cacheAt = DateTime.UtcNow;
                _needsBackgroundRefresh = false;
            }

            if (_basicCache != null)
            {
                basicPersisted = MergeCachedStates(_basicCache, changes);
                _basicCache = basicPersisted;
                _basicCacheAt = DateTime.UtcNow;
                _basicNeedsBackgroundRefresh = false;
            }
        }

        if (persisted != null)
            PersistentHardwareCacheService.WriteMachineStable(PersistentCacheKey, persisted);
        else
            PersistentHardwareCacheService.Remove(PersistentCacheKey);

        if (basicPersisted != null)
            PersistentHardwareCacheService.WriteMachineStable(BasicPersistentCacheKey, basicPersisted);
        else
            PersistentHardwareCacheService.Remove(BasicPersistentCacheKey);
    }
    public static bool NeedsBackgroundRefresh
    {
        get { lock (CacheLock) return _needsBackgroundRefresh; }
    }

    public static bool NeedsBasicBackgroundRefresh
    {
        get { lock (CacheLock) return _basicNeedsBackgroundRefresh; }
    }

    internal static int ScanCountForDiagnostics => Volatile.Read(ref _scanCount);
    internal static int BasicScanCountForDiagnostics => Volatile.Read(ref _basicScanCount);

    internal static void ClearMemoryCacheForSmoke()
    {
        lock (CacheLock)
        {
            _cache = null;
            _cacheAt = default;
            _needsBackgroundRefresh = false;
        }
    }

    internal static void RunCacheMergeSmoke()
    {
        TaskInfo[] source =
        {
            new(@"\Smoke\A", "A", true),
            new(@"\Smoke\B", "B", false)
        };
        var merged = MergeCachedStates(source, new[] { (@"\Smoke\A", false), (@"\Smoke\B", true) });
        if (merged.Length != 2 || merged[0].Enabled || !merged[1].Enabled || !source[0].Enabled || source[1].Enabled)
            throw new InvalidOperationException("Task Scheduler cache merge smoke failed.");
        LogService.Info("TASK_CACHE_MERGE_SMOKE_OK");
    }

    internal static async Task RunBasicCachePreservationSmokeAsync(CancellationToken token = default)
    {
        InvalidateCache();
        var service = new TaskSchedulerService();
        var fresh = await service.GetBasicAsync(forceRefresh: true, token).ConfigureAwait(false);
        if (fresh.Count == 0)
            throw new InvalidOperationException("Task Scheduler basic cache preservation smoke has empty inventory.");

        int scansBefore = BasicScanCountForDiagnostics;
        UpdateCachedStates(Array.Empty<(string Path, bool Enabled)>());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cached = await service.GetBasicAsync(forceRefresh: false, token).ConfigureAwait(false);
        sw.Stop();

        int extraScans = BasicScanCountForDiagnostics - scansBefore;
        if (extraScans != 0)
            throw new InvalidOperationException($"Task Scheduler basic cache was discarded after verified state update: extraScans={extraScans}.");
        if (cached.Count != fresh.Count)
            throw new InvalidOperationException($"Task Scheduler basic cache count changed after state update: fresh={fresh.Count} cached={cached.Count}.");
        if (sw.ElapsedMilliseconds > 25)
            throw new InvalidOperationException($"Task Scheduler basic cache post-update read is too slow: {sw.ElapsedMilliseconds} ms.");

        LogService.Info("TASK_BASIC_CACHE_PRESERVATION_SMOKE_OK " +
            $"tasks={cached.Count} cachedMs={sw.ElapsedMilliseconds} extraScans={extraScans}");
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('\\') || path.EndsWith('\\'))
            throw new ArgumentException("Invalid scheduled task path.", nameof(path));
    }

    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _cacheGeneration++;
            _cache = null;
            _cacheAt = default;
            _basicCache = null;
            _basicCacheAt = default;
            _needsBackgroundRefresh = false;
            _basicNeedsBackgroundRefresh = false;
        }
        PersistentHardwareCacheService.Remove(PersistentCacheKey);
        PersistentHardwareCacheService.Remove(BasicPersistentCacheKey);
    }
}
