using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static partial class StartupManagerService
{
    private static readonly object CacheLock = new();
    private static IReadOnlyList<StartupEntry>? _cache;
    private static DateTime _cacheAt;
    private static IReadOnlyList<StartupEntry>? _displayCache;
    private static bool _displayCacheNeedsRefresh;
    private static StartupEntry[]? _primaryPersistentSnapshotCache;
    private static Task<IReadOnlyList<StartupEntry>>? _flight;
    private static int _cacheGeneration;
    private static int _flightGeneration = -1;
    private static int _loadCount;
    private static bool _cacheNeedsBackgroundRefresh;
    private static readonly TimeSpan PersistentSnapshotTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DisplaySnapshotTtl = TimeSpan.FromMinutes(30);
    private const string PersistentSnapshotKey = "startup-inventory-v1";
    private const string DeltaBaselineKey = "startup-inventory-delta-baseline-v1";
    private static StartupInventoryDelta _lastDelta = new(0, 0, 0);
    private static StartupEntry[]? _deltaBaselineCache;
    private static bool _deltaBaselineLoaded;
    // The delta baseline is persisted off the inventory critical path.
    // Serialize commits and skip identical snapshots; otherwise queued writes
    // can finish out of order and leave a stale baseline on disk.
    private static readonly object DeltaWriteGate = new();
    private static StartupEntry[]? _lastCommittedDeltaBaseline;
    private static DateTime _lastCommittedDeltaUtc;
    private static int _deltaWriteRevision;
    private static readonly TimeSpan DeltaBaselineRefreshInterval = TimeSpan.FromHours(12);
    private static readonly Dictionary<string, string> PublisherCache = new(StringComparer.OrdinalIgnoreCase);
    private const string DisabledSuffix = "\\SpankyhoDisabled";
    private static readonly string[] RunPaths =
    {
        "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
        "Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce"
    };

    // Dedicated small AI inventory: automatic writes are intentionally
    // limited to HKCU Run + our reversible disabled partner, in both views.
    // Do not enumerate Task Scheduler, HKLM, folders or publisher caches of
    // unrelated startup entries on every AI evaluation.
    internal static Task<IReadOnlyList<StartupEntry>> GetAiUserRunAsync(
        CancellationToken token) =>
        Task.Run<IReadOnlyList<StartupEntry>>(() =>
        {
            var rows = new List<StartupEntry>();
            foreach (RegistryView view in new[]
                { RegistryView.Registry64, RegistryView.Registry32 })
            {
                token.ThrowIfCancellationRequested();
                ReadKey(rows, RegistryHive.CurrentUser, view, RunPaths[0],
                    true, includePublisher: false);
                ReadKey(rows, RegistryHive.CurrentUser, view,
                    RunPaths[0] + DisabledSuffix, false, includePublisher: false);
            }
            return rows;
        }, token);

    public static Task<IReadOnlyList<StartupEntry>> GetAsync(
        bool forceRefresh = false,
        bool forceTaskSchedulerRefresh = false)
    {
        lock (CacheLock)
        {
            // The caller, potentially the WinUI dispatcher, must only touch
            // the in-memory cache. JSON disk hydration belongs to the shared
            // worker flight, not the synchronous GetAsync prelude.
            if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(2))
                return Task.FromResult(_cache);
            if (_flight != null && !_flight.IsCompleted && _flightGeneration == _cacheGeneration)
                return _flight;

            int generation = _cacheGeneration;
            _flightGeneration = generation;
            _flight = Task.Run(async () =>
            {
                if (!forceRefresh)
                {
                    var persisted = TryReadPersistentSnapshot();
                    if (persisted != null)
                    {
                        lock (CacheLock)
                        {
                            // A manual startup edit may invalidate the cache
                            // while JSON is being read. Reject that old file
                            // rather than restoring stale enabled states.
                            if (generation == _cacheGeneration)
                            {
                                SeedPublisherCacheFromSnapshot(persisted);
                                _cache = persisted;
                                _cacheAt = DateTime.UtcNow;
                                _displayCache = persisted;
                                _displayCacheNeedsRefresh = true;
                                _cacheNeedsBackgroundRefresh = true;
                                _primaryPersistentSnapshotCache = persisted;
                                LogService.Info("startup", $"STARTUP_PERSISTENT_HIT count={persisted.Length} via=async");
                                return (IReadOnlyList<StartupEntry>)persisted;
                            }
                        }
                    }
                }
                return await LoadAsync(forceTaskSchedulerRefresh, generation).ConfigureAwait(false);
            });
            return _flight;
        }
    }

    private static async Task<IReadOnlyList<StartupEntry>> LoadAsync(bool forceTaskSchedulerRefresh, int generation)
    {
        Interlocked.Increment(ref _loadCount);
        var perf = Stopwatch.StartNew();

        // Registry/startup-folder inventory and Task Scheduler enumeration are independent.
        // Start both together so cold Startup inventory pays max(local, tasks), not local + tasks.
        var schedulerPerf = Stopwatch.StartNew();
        var scheduledTask = new TaskSchedulerService().GetAsync(forceTaskSchedulerRefresh);
        var localPerf = Stopwatch.StartNew();
        var localTask = Task.Run(() =>
        {
            var local = new List<StartupEntry>();
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    foreach (var path in RunPaths)
                    {
                        ReadKey(local, hive, view, path, true);
                        ReadKey(local, hive, view, path + DisabledSuffix, false);
                    }

            ReadStartupFolder(local, Environment.GetFolderPath(Environment.SpecialFolder.Startup));
            ReadStartupFolder(local, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
            return local;
        });

        var result = await localTask.ConfigureAwait(false);
        long localMs = localPerf.ElapsedMilliseconds;
        long taskMs = 0;

        try
        {
            var tasks = await scheduledTask.ConfigureAwait(false);
            taskMs = schedulerPerf.ElapsedMilliseconds;
            foreach (var task in tasks)
            {
                if (!task.StartupTrigger) continue;
                bool canToggle = !task.Enabled || TaskSafetyService.CanDisable(task.Path);
                string command = string.IsNullOrWhiteSpace(task.Executable) ? task.Path : task.Executable;
                result.Add(new StartupEntry(
                    $"task|{task.Path}", task.Name, command, RegistryHive.CurrentUser, string.Empty, task.Enabled,
                    "Task Scheduler", task.Executable, "ScheduledTask", task.Path, canToggle));
            }
        }
        catch (Exception ex)
        {
            taskMs = schedulerPerf.ElapsedMilliseconds;
            LogService.Error("startup", "Startup scheduled-task inventory failed", ex);
        }

        var finalizePerf = Stopwatch.StartNew();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<StartupEntry>(result.Count);
        foreach (var item in result)
        {
            if (seenIds.Add(item.Id))
                unique.Add(item);
        }
        unique.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        StartupEntry[] final = unique.ToArray();
        long finalizeMs = finalizePerf.ElapsedMilliseconds;

        bool publish;
        lock (CacheLock)
        {
            publish = generation == _cacheGeneration;
            if (publish)
            {
                _cache = final;
                _cacheAt = DateTime.UtcNow;
                _displayCache = final;
                _displayCacheNeedsRefresh = false;
                _cacheNeedsBackgroundRefresh = false;
            }
            if (_flightGeneration == generation)
                _flight = null;
        }

        if (publish)
        {
            // The sorted inventory is already a newly owned array. Reuse it for
            // persistence/delta rather than allocating and copying every entry again.
            StartupEntry[] persistedFinal = final;
            var delta = ComputeDelta(persistedFinal);
            int deltaRevision;
            lock (CacheLock)
            {
                // An invalidation may occur while comparing the inventory.
                // Never publish an obsolete delta or queue a stale disk write.
                if (generation != _cacheGeneration)
                    return final;
                _lastDelta = delta;
                _deltaBaselineCache = persistedFinal;
                _deltaBaselineLoaded = true;
                deltaRevision = ++_deltaWriteRevision;
            }

            LogService.Info("startup", $"STARTUP_DELTA added={delta.Added} removed={delta.Removed} changed={delta.Changed}");

            // The primary snapshot is part of GetAsync's contract: callers may clear
            // memory immediately and still expect persistent hydration to work.
            // Avoid rewriting identical JSON on ordinary refreshes.
            lock (CacheLock)
            {
                // Commit atomically with InvalidateCache: an older completed
                // inventory must not recreate the persistent file after a
                // newer startup configuration invalidated it.
                if (generation == _cacheGeneration &&
                    !(_primaryPersistentSnapshotCache is { Length: > 0 } previousPrimary &&
                      previousPrimary.SequenceEqual(persistedFinal)) &&
                    UserScopedCacheService.TryWrite(PersistentSnapshotKey, persistedFinal))
                    _primaryPersistentSnapshotCache = persistedFinal;
            }

            // Delta baseline is only needed by the next inventory pass. Commit
            // off-thread, but serialize writes and reject superseded revisions:
            // ThreadPool callbacks are not guaranteed to execute in FIFO order.
            _ = ThreadPool.QueueUserWorkItem(_ =>
            {
                    try
                    {
                        lock (DeltaWriteGate)
                        {
                            DateTime now = DateTime.UtcNow;
                            bool needsWrite;
                            lock (CacheLock)
                            {
                                needsWrite = generation == _cacheGeneration &&
                                    deltaRevision == _deltaWriteRevision &&
                                    ShouldPersistDeltaBaseline(_lastCommittedDeltaBaseline,
                                        persistedFinal, _lastCommittedDeltaUtc, now);
                            }
                            if (!needsWrite) return;

                            // Only successfully committed snapshots can suppress
                            // later writes. An I/O failure must leave retry open.
                            if (!UserScopedCacheService.TryWrite(DeltaBaselineKey, persistedFinal))
                                return;
                            lock (CacheLock)
                            {
                                // Even if a newer revision published DURING this
                                // commit, these are the bytes now on disk. A newer
                                // queued writer must see them and correct the file.
                                _lastCommittedDeltaBaseline = persistedFinal;
                                _lastCommittedDeltaUtc = now;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("startup", "Deferred startup delta baseline persistence failed", ex);
                    }
            });
        }

        LogService.Info("startup", $"STARTUP_INVENTORY_OK total={final.Length} registry={final.Count(x => x.SourceKind == "Registry")} folders={final.Count(x => x.SourceKind == "StartupFolder")} tasks={final.Count(x => x.SourceKind == "ScheduledTask")}");
        LogService.Info("performance", $"STARTUP_INVENTORY_PHASE localMs={localMs} taskMs={taskMs} finalizeMs={finalizeMs} totalMs={perf.ElapsedMilliseconds}");
        return final;
    }

    public static IReadOnlyList<StartupEntry>? TryGetFreshSnapshot()
    {
        lock (CacheLock)
            if (_cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(2))
                return _cache;

        var persisted = TryReadPersistentSnapshot();
        if (persisted != null)
        {
            lock (CacheLock)
            {
                SeedPublisherCacheFromSnapshot(persisted);
                _cache = persisted;
                _cacheAt = DateTime.UtcNow;
                _displayCache = persisted;
                _displayCacheNeedsRefresh = true;
                _cacheNeedsBackgroundRefresh = true;
                _primaryPersistentSnapshotCache = persisted;
            }
            LogService.Info("startup", $"STARTUP_PERSISTENT_HIT count={persisted.Length}");
            return persisted;
        }

        return null;
    }

    public static IReadOnlyList<StartupEntry>? TryGetMemoryDisplaySnapshot()
    {
        lock (CacheLock)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(2))
                return _cache;
            if (_displayCache != null)
            {
                _cacheNeedsBackgroundRefresh = _displayCacheNeedsRefresh;
                return _displayCache;
            }
        }
        return null;
    }

    public static IReadOnlyList<StartupEntry>? TryGetDisplaySnapshot()
    {
        var memory = TryGetMemoryDisplaySnapshot();
        if (memory != null)
            return memory;

        if (!UserScopedCacheService.TryRead<StartupEntry[]>(
                PersistentSnapshotKey, DisplaySnapshotTtl, out var persisted, out var age) ||
            persisted is not { Length: > 0 })
            return null;

        bool fresh = age <= PersistentSnapshotTtl;
        lock (CacheLock)
        {
            SeedPublisherCacheFromSnapshot(persisted);
            _displayCache = persisted;
            _displayCacheNeedsRefresh = true;
            _primaryPersistentSnapshotCache = persisted;
            if (fresh)
            {
                _cache = persisted;
                _cacheAt = DateTime.UtcNow;
            }
            _cacheNeedsBackgroundRefresh = true;
        }

        LogService.Info("startup", fresh
            ? $"STARTUP_PERSISTENT_HIT count={persisted.Length}"
            : $"STARTUP_STALE_DISPLAY_HIT count={persisted.Length} ageSec={age.TotalSeconds:0}");
        return persisted;
    }

    public static Task<bool> TryWarmDisplaySnapshotAsync(CancellationToken token = default) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return TryGetDisplaySnapshot() != null;
        }, token);

    internal static Task<bool> TryWarmDeltaBaselineAsync(CancellationToken token = default) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return EnsureDeltaBaselineLoaded() is { Length: > 0 };
        }, token);

    public static bool NeedsBackgroundRefresh
    {
        get { lock (CacheLock) return _cacheNeedsBackgroundRefresh; }
    }

    internal static int LoadCountForDiagnostics => Volatile.Read(ref _loadCount);

    internal static void ClearMemoryCacheForSmoke()
    {
        lock (CacheLock)
        {
            _cache = null;
            _cacheAt = default;
            _displayCache = null;
            _displayCacheNeedsRefresh = false;
            _cacheNeedsBackgroundRefresh = false;
            _flight = null;
            _flightGeneration = -1;
            _primaryPersistentSnapshotCache = null;
            _deltaBaselineCache = null;
            _deltaBaselineLoaded = false;
            PublisherCache.Clear();
        }
    }

    private static void MarkDisplayCacheForRefreshLocked()
    {
        // Must run inside CacheLock. Retain stale UI rows and force hydration.
        _displayCacheNeedsRefresh = _displayCache != null;
        _cacheNeedsBackgroundRefresh = _displayCacheNeedsRefresh;
    }

    public static void InvalidateCache(bool invalidateTaskScheduler = false)
    {
        lock (CacheLock)
        {
            _cache = null;
            _cacheAt = default;
            // Retain the old display while a refresh runs, but ALWAYS
            // schedule new inventory hydration; previously manual Obnovit
            // left _displayCache with refreshPending=false forever.
            MarkDisplayCacheForRefreshLocked();
            _cacheGeneration++;
            // Same lock as fresh disk publication. Do not remove the file
            // outside the critical section and let a stale writer restore it.
            UserScopedCacheService.Remove(PersistentSnapshotKey);
        }
        if (invalidateTaskScheduler)
            TaskSchedulerService.InvalidateCache();
    }

    private static StartupEntry[]? TryReadPersistentSnapshot() =>
        UserScopedCacheService.TryRead<StartupEntry[]>(PersistentSnapshotKey, PersistentSnapshotTtl, out var value) ? value : null;

    private static void SeedPublisherCacheFromSnapshot(IEnumerable<StartupEntry> items)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.FilePath) ||
                string.IsNullOrWhiteSpace(item.Publisher))
                continue;
            PublisherCache[item.FilePath] = item.Publisher;
        }
    }

    public static StartupInventoryDelta LastDelta
    {
        get { lock (CacheLock) return _lastDelta; }
    }

    private static StartupEntry[]? EnsureDeltaBaselineLoaded()
    {
        lock (CacheLock)
            if (_deltaBaselineLoaded)
                return _deltaBaselineCache;

        StartupEntry[]? loaded = null;
        if (UserScopedCacheService.TryRead<StartupEntry[]>(
                DeltaBaselineKey, TimeSpan.FromDays(30), out var previous, out _) &&
            previous is { Length: > 0 })
            loaded = previous;

        lock (CacheLock)
        {
            if (!_deltaBaselineLoaded)
            {
                _deltaBaselineCache = loaded;
                _deltaBaselineLoaded = true;
            }
            return _deltaBaselineCache;
        }
    }

    private static bool ShouldPersistDeltaBaseline(
        IReadOnlyList<StartupEntry>? committed,
        IReadOnlyList<StartupEntry> current,
        DateTime committedUtc, DateTime nowUtc) =>
        committed == null ||
        committed.Count != current.Count ||
        committedUtc == default ||
        nowUtc <= committedUtc ||
        nowUtc - committedUtc >= DeltaBaselineRefreshInterval ||
        !committed.SequenceEqual(current);

    private static StartupInventoryDelta ComputeDelta(IReadOnlyList<StartupEntry> current)
    {
        var previous = EnsureDeltaBaselineLoaded();
        return previous is { Length: > 0 }
            ? ComputeDeltaFrom(previous, current)
            : new StartupInventoryDelta(0, 0, 0);
    }

    private static StartupInventoryDelta ComputeDeltaFrom(
        IReadOnlyList<StartupEntry> previous,
        IReadOnlyList<StartupEntry> current)
    {
        // A single old-ID dictionary and a current-ID set replace two
        // ToDictionary allocations and three LINQ sweeps. Remove matched
        // old entries so dictionary.Count is exactly the removed count.
        // Duplicate legacy snapshot IDs are ignored rather than throwing.
        var oldMap = new Dictionary<string, StartupEntry>(
            previous.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var item in previous)
            oldMap.TryAdd(item.Id, item);
        var currentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int added = 0, changed = 0;
        foreach (var item in current)
        {
            if (!currentIds.Add(item.Id)) continue;
            if (!oldMap.Remove(item.Id, out var old))
            {
                added++;
                continue;
            }
            if (!string.Equals(old.Command, item.Command, StringComparison.Ordinal) ||
                old.Enabled != item.Enabled ||
                !string.Equals(old.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                changed++;
        }
        return new StartupInventoryDelta(added, oldMap.Count, changed);
    }
}
