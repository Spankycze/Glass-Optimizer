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
        EnsureTaskMutationAllowed(path, enabled);
        if (!WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator())
        {
            await WorkerClientService.RunAsync<WorkerAck>(
                "task-set",
                WorkerPayloads.Encode(new TaskStateChangeRequest(path, enabled)),
                TimeSpan.FromSeconds(45),
                token).ConfigureAwait(false);
        }
        else
        {
            await Task.Run(() => TaskSchedulerComService.SetEnabled(path, enabled, token), token).ConfigureAwait(false);
        }
        TaskRuntimeState verified = await Task.Run(() => TaskSchedulerComService.GetRuntimeState(path, token), CancellationToken.None)
            .WaitAsync(token).ConfigureAwait(false);
        if (verified.Enabled != enabled)
        {
            InvalidateCache();
            throw new InvalidOperationException($"Task Scheduler read-back nepotvrdil stav {path}. Požadováno={enabled}, skutečnost={verified.Enabled}.");
        }
        LogService.Info("tasks", $"Task state changed and verified via COM/worker: {path}, enabled={enabled}");
        UpdateCachedStates(new[] { (path, enabled) });
    }
    public async Task<string[]> SetEnabledBatchAsync(IReadOnlyList<(string Path, bool Enabled)> changes, CancellationToken token = default)
    {
        // Snapshot and validate ONCE before await/IPC. The caller may own a
        // mutable list; changes after admission must never affect this batch.
        var approved = PrepareTaskMutationBatch(changes);
        if (approved.Length == 0) return Array.Empty<string>();
        string[] result;
        if (!WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator())
        {
            var payload = new TaskStateBatchRequest(approved.Select(x => new TaskStateChangeRequest(x.Path, x.Enabled)).ToArray());
            result = await WorkerClientService.RunAsync<string[]>(
                "task-set-batch",
                WorkerPayloads.Encode(payload),
                TimeSpan.FromMinutes(2),
                token).ConfigureAwait(false);
        }
        else
        {
            result = await Task.Run(() => TaskSchedulerComService.SetEnabledBatch(approved, token), CancellationToken.None)
                .WaitAsync(token).ConfigureAwait(false);
        }
        if (result.Length == 0 && approved.Length > 0)
        {
            var paths = approved.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var states = await Task.Run(() => TaskSchedulerComService.GetRuntimeStates(paths, token), CancellationToken.None)
                .WaitAsync(token).ConfigureAwait(false);
            var verifyErrors = approved
                .Where(x => !states.TryGetValue(x.Path, out var state) || state.Enabled != x.Enabled)
                .Select(x => $"{x.Path}: read-back nepotvrdil stav {x.Enabled}.")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (verifyErrors.Length == 0)
                UpdateCachedStates(approved);
            else
            {
                InvalidateCache();
                result = verifyErrors;
            }
        }
        else if (result.Length != 0)
        {
            InvalidateCache();
        }
        LogService.Info("tasks", $"Task batch changed and read-back checked: requested={approved.Length} errors={result.Length}");
        return result;
    }

    public async Task<bool> GetEnabledAsync(string path, CancellationToken token = default)
    {
        ValidatePath(path);
        // Batch enabled-state detection through the shared basic inventory.
        // A series of direct COM connections measurably increased full-smoke CPU usage.
        var snapshot = TryGetBasicFreshSnapshot() ?? await GetBasicAsync(forceRefresh: false, token);
        return snapshot.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Enabled ?? false;
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

        await BasicLoadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (CacheLock)
            {
                if (_basicCache != null && DateTime.UtcNow - _basicCacheAt < MemoryCacheTtl &&
                    (!forceRefresh || _basicCacheAt >= requestStartedAt))
                    return _basicCache;
                if (_cache != null && DateTime.UtcNow - _cacheAt < MemoryCacheTtl)
                {
                    if (!forceRefresh) return _cache;
                    if (_cacheAt >= requestStartedAt)
                    {
                        _basicCache = _cache;
                        _basicCacheAt = _cacheAt;
                        _basicNeedsBackgroundRefresh = false;
                        return _cache;
                    }
                }
            }

            // Run persisted JSON reads after the first suspension and under the
            // basic single-flight gate. Eight UI callers must not each deserialize
            // the same file or block the WinUI dispatcher before their first await.
            int restoreGeneration;
            lock (CacheLock) restoreGeneration = _cacheGeneration;
            if (!forceRefresh)
            {
                TaskInfo[]? restored = await Task.Run(() =>
                {
                    if (PersistentHardwareCacheService.TryReadMachineStable<TaskInfo[]>(
                            BasicPersistentCacheKey, PersistentCacheTtl, out var basic))
                        return basic;
                    return PersistentHardwareCacheService.TryReadMachineStable<TaskInfo[]>(
                        PersistentCacheKey, PersistentCacheTtl, out var full) ? full : null;
                }, token).ConfigureAwait(false);
                if (restored != null)
                {
                    lock (CacheLock)
                    {
                        if (restoreGeneration == _cacheGeneration)
                        {
                            if (_cache != null && DateTime.UtcNow - _cacheAt < MemoryCacheTtl)
                                return _cache;
                            _basicCache = restored;
                            _basicCacheAt = DateTime.UtcNow;
                            _basicNeedsBackgroundRefresh = true;
                            LogService.Info("tasks", $"Persistent basic task inventory cache hit count={restored.Length} refreshPending=true via=async");
                            return restored;
                        }
                    }
                }
            }

            // A full inventory scan includes all basic task fields. If a full
            // COM walk is already running, wait for it instead of launching
            // a second 256-task COM walk in parallel. Forced refresh remains
            // correct: only accept a full result published after this request.
            if (LoadGate.CurrentCount == 0)
            {
                await LoadGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    lock (CacheLock)
                    {
                        if (_cache != null &&
                            DateTime.UtcNow - _cacheAt < MemoryCacheTtl &&
                            _cacheAt >= requestStartedAt)
                        {
                            _basicCache = _cache;
                            _basicCacheAt = _cacheAt;
                            _basicNeedsBackgroundRefresh = false;
                            return _cache;
                        }
                    }
                }
                finally { LoadGate.Release(); }
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
                    // A full scan may have completed after this slower basic
                    // scan began. Never overwrite that newer complete view.
                    if (_basicCache != null && _basicCacheAt >= requestStartedAt)
                        return _basicCache;
                    _basicCache = parsed;
                    _basicCacheAt = DateTime.UtcNow;
                    _basicNeedsBackgroundRefresh = false;
                    PersistentHardwareCacheService.WriteMachineStable(BasicPersistentCacheKey,
                        parsed as TaskInfo[] ?? parsed.ToArray());
                }
            }
            if (!publish)
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
            if (!forceRefresh)
            {
                TaskInfo[]? restored = await Task.Run(() =>
                    PersistentHardwareCacheService.TryReadMachineStable<TaskInfo[]>(
                        PersistentCacheKey, PersistentCacheTtl, out var value) ? value : null, token)
                    .ConfigureAwait(false);
                if (restored != null)
                {
                    lock (CacheLock)
                    {
                        if (generation == _cacheGeneration)
                        {
                            _cache = restored;
                            _cacheAt = DateTime.UtcNow;
                            _needsBackgroundRefresh = true;
                            _basicCache = restored;
                            _basicCacheAt = _cacheAt;
                            _basicNeedsBackgroundRefresh = true;
                            LogService.Info("tasks", $"Persistent task inventory cache hit count={restored.Length} refreshPending=true via=async");
                            return restored;
                        }
                    }
                }
            }
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
                    // The enriched inventory is also a complete basic view.
                    // Reuse the same immutable snapshot; do not queue another
                    // basic COM walk just because that page loads next.
                    _basicCache = parsed;
                    _basicCacheAt = _cacheAt;
                    _basicNeedsBackgroundRefresh = false;
                    PersistentHardwareCacheService.WriteMachineStable(PersistentCacheKey,
                        parsed as TaskInfo[] ?? parsed.ToArray());
                }
            }
            if (!publish)
                LogService.Info("tasks", $"TASK_STALE_RESULT_DROPPED generation={generation} current={_cacheGeneration}");
            return parsed;
        }
        finally { LoadGate.Release(); }
    }
    private static TaskInfo[] MergeCachedStates(IReadOnlyList<TaskInfo> source, IReadOnlyList<(string Path, bool Enabled)> changes)
    {
        var items = source.ToArray();
        if (changes.Count == 0) return items;

        // Ordinary single-task toggles should not allocate and hash a full
        // scheduler inventory dictionary. Preserve the original last-match
        // path behavior, including case-insensitive comparison and repeated
        // changes to the same scheduled task.
        if (changes.Count <= 4)
        {
            foreach (var change in changes)
                for (int i = items.Length - 1; i >= 0; i--)
                    if (string.Equals(items[i].Path, change.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        items[i] = items[i] with { Enabled = change.Enabled };
                        break;
                    }
            return items;
        }

        // Larger batches retain O(N+M) lookup, without a LINQ projection and
        // tuple allocation for each of the inventory rows.
        var index = new Dictionary<string, int>(items.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Length; i++) index.Add(items[i].Path, i);
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
            if (persisted != null)
                PersistentHardwareCacheService.WriteMachineStable(PersistentCacheKey, persisted);
            else
                PersistentHardwareCacheService.Remove(PersistentCacheKey);

            if (basicPersisted != null)
                PersistentHardwareCacheService.WriteMachineStable(BasicPersistentCacheKey, basicPersisted);
            else
                PersistentHardwareCacheService.Remove(BasicPersistentCacheKey);
        }
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
        var unchanged = MergeCachedStates(source, Array.Empty<(string Path, bool Enabled)>());
        var repeated = MergeCachedStates(source, new[]
        {
            (@"\smoke\a", false), (@"\Smoke\A", true),
            (@"\Smoke\A", false), (@"\SMOKE\B", true)
        });
        var batch = MergeCachedStates(source, new[]
        {
            (@"\smoke\a", false), (@"\Smoke\B", true),
            (@"\Missing", true), (@"\Smoke\A", true), (@"\SMOKE\A", false)
        });
        if (ReferenceEquals(unchanged, source) || unchanged.Length != 2 ||
            !unchanged[0].Enabled || unchanged[1].Enabled ||
            repeated[0].Enabled || !repeated[1].Enabled ||
            batch[0].Enabled || !batch[1].Enabled ||
            !source[0].Enabled || source[1].Enabled)
            throw new InvalidOperationException("Task Scheduler empty/small/large merge parity failed.");
        LogService.Info("TASK_CACHE_MERGE_SMOKE_OK");
    }

    internal static async Task RunBasicCachePreservationSmokeAsync(CancellationToken token = default)
    {
        InvalidateCache();
        var service = new TaskSchedulerService();

        // Force both read-only COM consumers to start against an invalidated
        // cache. The basic request must join the in-flight full scan rather
        // than issuing a duplicate 256-task COM enumeration.
        int basicBeforeJoin = BasicScanCountForDiagnostics;
        Task<IReadOnlyList<TaskInfo>> fullScan =
            service.GetAsync(forceRefresh: true, token);
        Task<IReadOnlyList<TaskInfo>> basicJoin =
            service.GetBasicAsync(forceRefresh: true, token);
        var fullResult = await fullScan.ConfigureAwait(false);
        var basicResult = await basicJoin.ConfigureAwait(false);
        if (fullResult.Count == 0 || basicResult.Count != fullResult.Count ||
            BasicScanCountForDiagnostics != basicBeforeJoin)
            throw new InvalidOperationException(
                $"Task basic/full scan coalescing failed: full={fullResult.Count} basic={basicResult.Count} duplicateScans={BasicScanCountForDiagnostics - basicBeforeJoin}.");
        LogService.Info("TASK_BASIC_FULL_COALESCE_SMOKE_OK tasks=" +
            basicResult.Count + " extraBasicScans=0");

        InvalidateCache();
        var fullOnly = await service.GetAsync(forceRefresh: true, token).ConfigureAwait(false);
        int basicScansAfterFull = BasicScanCountForDiagnostics;
        var basicFromFull = await service.GetBasicAsync(forceRefresh: false, token)
            .ConfigureAwait(false);
        if (!ReferenceEquals(fullOnly, basicFromFull) ||
            BasicScanCountForDiagnostics != basicScansAfterFull)
            throw new InvalidOperationException(
                "Full task inventory did not publish its basic view for cache reuse.");
        LogService.Info("TASK_BASIC_FULL_PUBLISH_SMOKE_OK tasks=" +
            basicFromFull.Count + " extraBasicScans=0");

        // Cold cache hydration has to be nonblocking for its caller and single-flight.
        // The full scan above has written the enriched persistent TaskInfo[].
        ClearMemoryCacheForSmoke();
        int fullScansBefore = ScanCountForDiagnostics;
        var callSw = System.Diagnostics.Stopwatch.StartNew();
        var concurrent = new Task<IReadOnlyList<TaskInfo>>[8];
        for (int i = 0; i < concurrent.Length; i++)
            concurrent[i] = service.GetAsync(forceRefresh: false, token);
        callSw.Stop();
        var loaded = await Task.WhenAll(concurrent).ConfigureAwait(false);
        if (callSw.ElapsedMilliseconds > 100 ||
            loaded.Any(x => !ReferenceEquals(x, loaded[0])) ||
            ScanCountForDiagnostics != fullScansBefore ||
            loaded[0].Count != fullOnly.Count)
            throw new InvalidOperationException(
                $"Task persistent async sharing failed: callerMs={callSw.ElapsedMilliseconds} scans={ScanCountForDiagnostics - fullScansBefore}.");
        LogService.Info("TASK_ASYNC_PERSISTENT_SMOKE_OK readers=8 callerMs=" +
            callSw.ElapsedMilliseconds + " extraComScans=0");

        InvalidateCache();
        var fresh = await service.GetBasicAsync(forceRefresh: true, token).ConfigureAwait(false);
        if (fresh.Count == 0)
            throw new InvalidOperationException("Task Scheduler basic cache preservation smoke has empty inventory.");

        lock (CacheLock)
        {
            _basicCache = null;
            _basicCacheAt = default;
        }
        int persistedBasicBefore = BasicScanCountForDiagnostics;
        var basicCallSw = System.Diagnostics.Stopwatch.StartNew();
        var parallelBasic = new Task<IReadOnlyList<TaskInfo>>[8];
        for (int i = 0; i < parallelBasic.Length; i++)
            parallelBasic[i] = service.GetBasicAsync(forceRefresh: false, token);
        basicCallSw.Stop();
        var basicReads = await Task.WhenAll(parallelBasic).ConfigureAwait(false);
        if (basicCallSw.ElapsedMilliseconds > 100 ||
            basicReads.Any(x => !ReferenceEquals(x, basicReads[0])) ||
            BasicScanCountForDiagnostics != persistedBasicBefore)
            throw new InvalidOperationException(
                $"Task basic persistent sharing failed: callerMs={basicCallSw.ElapsedMilliseconds} scans={BasicScanCountForDiagnostics - persistedBasicBefore}.");
        LogService.Info("TASK_BASIC_ASYNC_PERSISTENT_SMOKE_OK readers=8 callerMs=" +
            basicCallSw.ElapsedMilliseconds + " extraComScans=0");

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

    // Mutable caller-owned batch lists cannot be trusted across await/IPC or
    // between batch COM operations. Capture a stable snapshot, validate all
    // mutations, and fail closed if one physical task has both requested
    // enable and disable states. Duplicates with the same state are harmless
    // and kept for compatibility; conflicts are never partially applied.
    internal static (string Path, bool Enabled)[] PrepareTaskMutationBatch(
        IReadOnlyList<(string Path, bool Enabled)> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        (string Path, bool Enabled)[] approved = changes.ToArray();
        var requested = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in approved)
        {
            EnsureTaskMutationAllowed(change.Path, change.Enabled);
            if (requested.TryGetValue(change.Path, out bool previous))
            {
                if (previous != change.Enabled)
                    throw new InvalidOperationException(
                        $"Konfliktní dávka Plánovače úloh: '{change.Path}' má současně povolení i zakázání.");
            }
            else requested.Add(change.Path, change.Enabled);
        }
        return approved;
    }

    internal static bool RunTaskBatchConflict189Smoke()
    {
        var mutable = new List<(string Path, bool Enabled)>
        {
            (@"\ThirdParty\Updater", false),
            (@"\THIRDPARTY\UPDATER", false)
        };
        var approved = PrepareTaskMutationBatch(mutable);
        mutable[0] = (@"\ThirdParty\Updater", true);
        if (approved.Length != 2 || approved[0].Enabled ||
            approved[1].Enabled || approved[0].Path != @"\ThirdParty\Updater")
            return false;
        bool conflictRejected = false, criticalRejected = false;
        try
        {
            _ = PrepareTaskMutationBatch(new[]
            {
                (Path: @"\ThirdParty\Updater", Enabled: false),
                (Path: @"\THIRDPARTY\Updater", Enabled: true)
            });
        }
        catch (InvalidOperationException) { conflictRejected = true; }
        try
        {
            _ = PrepareTaskMutationBatch(new[]
            {
                (Path: @"\ThirdParty\Updater", Enabled: false),
                (Path: @"\Microsoft\Windows\TaskScheduler\Maintenance", Enabled: false)
            });
        }
        catch (InvalidOperationException) { criticalRejected = true; }
        return conflictRejected && criticalRejected &&
            PrepareTaskMutationBatch(
                Array.Empty<(string Path, bool Enabled)>()).Length == 0;
    }

    // This policy applies identically to the GUI service, privileged worker
    // and native COM. Enable remains available for recovery of protected
    // tasks; disabling critical Windows tasks is never an allowed mutation.
    internal static void EnsureTaskMutationAllowed(string path, bool enabled)
    {
        ValidatePath(path);
        if (!enabled && !TaskSafetyService.CanDisable(path))
            throw new InvalidOperationException(
                "Kritickou systémovou úlohu Windows nelze vypnout.");
    }

    internal static bool RunTaskMutationSafety187Smoke()
    {
        EnsureTaskMutationAllowed(@"\ThirdParty\Updater", false);
        EnsureTaskMutationAllowed(@"\ThirdParty\Updater", true);
        EnsureTaskMutationAllowed(@"\Microsoft\Windows\TaskScheduler\Maintenance", true);
        bool rejected = false;
        try { EnsureTaskMutationAllowed(@"\Microsoft\Windows\TaskScheduler\Maintenance", false); }
        catch (InvalidOperationException) { rejected = true; }
        bool batchRejected = false;
        try
        {
            foreach (var change in new[]
            {
                (Path: @"\ThirdParty\Updater", Enabled: false),
                (Path: @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan", Enabled: false)
            })
                EnsureTaskMutationAllowed(change.Path, change.Enabled);
        }
        catch (InvalidOperationException) { batchRejected = true; }
        return rejected && batchRejected;
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('\\') ||
            path.EndsWith('\\') || path.Contains("\\\\", StringComparison.Ordinal) ||
            path.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            path.Split('\\', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
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
            PersistentHardwareCacheService.Remove(PersistentCacheKey);
            PersistentHardwareCacheService.Remove(BasicPersistentCacheKey);
        }
    }
}
