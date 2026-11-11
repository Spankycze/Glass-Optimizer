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

public sealed record StartupEntry(
    string Id, string Name, string Command, RegistryHive Hive, string RunPath, bool Enabled,
    string Publisher, string? FilePath, string SourceKind = "Registry", string? SourceId = null, bool CanToggle = true,
    RegistryView View = RegistryView.Registry64);

public sealed record StartupInventoryDelta(int Added, int Removed, int Changed)
{
    public bool HasChanges => Added > 0 || Removed > 0 || Changed > 0;
    public string Summary => HasChanges
        ? $"+{Added} / -{Removed} / ~{Changed}"
        : "Bez změn";
}

public static class StartupManagerService
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

    internal static string RunDeltaSmoke()
    {
        StartupEntry A(string id, string command, bool enabled) =>
            new(id, id, command, RegistryHive.CurrentUser, @"Software\Smoke", enabled,
                "Smoke", null);

        StartupEntry[] previous =
        [
            A("A", "a.exe", true),
            A("B", "b.exe", true),
            A("C", "c.exe", false)
        ];
        StartupEntry[] current =
        [
            A("A", "a2.exe", true),
            A("C", "c.exe", false),
            A("D", "d.exe", true)
        ];

        var delta = ComputeDeltaFrom(previous, current);
        if (delta.Added != 1 || delta.Removed != 1 || delta.Changed != 1)
            throw new InvalidOperationException(
                $"Startup delta smoke selhal: +{delta.Added} -{delta.Removed} ~{delta.Changed}");
        var duplicates = ComputeDeltaFrom([previous[0], previous[0], previous[1]],
            [previous[1], previous[1], previous[0] with { Enabled = false }]);
        if (duplicates.Added != 0 || duplicates.Removed != 0 || duplicates.Changed != 1)
            throw new InvalidOperationException("Duplicate legacy Startup IDs corrupted delta comparison.");
        var oldPath = A("path-case", "tool.exe", true) with { FilePath = @"C:\Apps\Tool.exe" };
        if (ComputeDeltaFrom([oldPath], [oldPath with { FilePath = @"c:\apps\TOOL.EXE" }]).HasChanges)
            throw new InvalidOperationException("Startup path case-only change falsely invalidated delta.");

        // Unchanged inventories must not rewrite the atomic JSON baseline at
        // every refresh. Publisher/metadata-only changes still persist, and
        // the 12h refresh prevents a long-lived process from aging its file out.
        DateTime sampleUtc = new(2026, 10, 17, 18, 0, 0, DateTimeKind.Utc);
        if (ShouldPersistDeltaBaseline(previous, previous, sampleUtc, sampleUtc.AddMinutes(2)) ||
            !ShouldPersistDeltaBaseline(null, previous, sampleUtc, sampleUtc.AddMinutes(2)) ||
            !ShouldPersistDeltaBaseline(previous, current, sampleUtc, sampleUtc.AddMinutes(2)) ||
            !ShouldPersistDeltaBaseline(previous,
                [previous[0] with { Publisher = "New Publisher" }, previous[1], previous[2]],
                sampleUtc, sampleUtc.AddMinutes(2)) ||
            !ShouldPersistDeltaBaseline(previous, previous, sampleUtc, sampleUtc.AddHours(12)) ||
            !ShouldPersistDeltaBaseline(previous, previous, sampleUtc, sampleUtc.AddMinutes(-1)))
            throw new InvalidOperationException("Startup delta baseline write coalescing regressed.");

        // Read-only policy fixture: do not invalidate actual persisted cache or
        // touch startup registry; preserve original static state under lock.
        lock (CacheLock)
        {
            var previousDisplay = _displayCache;
            bool previousPending = _displayCacheNeedsRefresh;
            bool previousNeedsRefresh = _cacheNeedsBackgroundRefresh;
            try
            {
                _displayCache = previous;
                _displayCacheNeedsRefresh = false;
                _cacheNeedsBackgroundRefresh = false;
                MarkDisplayCacheForRefreshLocked();
                if (!_displayCacheNeedsRefresh || !_cacheNeedsBackgroundRefresh)
                    throw new InvalidOperationException("Manual refresh did not schedule rehydration.");
                _displayCache = null;
                MarkDisplayCacheForRefreshLocked();
                if (_displayCacheNeedsRefresh || _cacheNeedsBackgroundRefresh)
                    throw new InvalidOperationException("Refresh scheduled without cached display.");
            }
            finally
            {
                _displayCache = previousDisplay;
                _displayCacheNeedsRefresh = previousPending;
                _cacheNeedsBackgroundRefresh = previousNeedsRefresh;
            }
        }

        return "STARTUP_DELTA_SMOKE_OK added=1 removed=1 changed=1 " +
            "duplicates=true pathCase=true manualRefreshFlag=true persistentWrites=0";
    }

    private static void WritePersistentSnapshot(IReadOnlyList<StartupEntry> items) =>
        UserScopedCacheService.Write(PersistentSnapshotKey, items as StartupEntry[] ?? items.ToArray());

    // The AI presence/restore path has no reason to open every EXE to read
    // version metadata: executable identity comes from the registry command.
    internal static bool NeedsPublisherMetadata(bool aiInventory) => !aiInventory;

    // Registry enumeration is not a transaction. GetValueNames may include
    // a name deleted by another process before its GetValue is performed.
    // Only an actual non-null read is evidence that the slot still exists;
    // a legitimate present empty REG_SZ is preserved as a row so inventory
    // can still show it (restore policy separately rejects blank commands).
    internal static bool TryReadPresentRegistryCommand(object? rawValue, out string command)
    {
        command = rawValue?.ToString() ?? string.Empty;
        return rawValue is not null;
    }

    // An unnamed/default Run registry value is not a Windows autorun entry.
    // Other non-string or blank values remain visible for diagnosis but must
    // never offer a toggle: registry moves require real nonempty commands.
    // No registry access here; safe for synthetic inventory and policy tests.
    internal static bool TryClassifyRegistryStartupSlot(
        string? name, object? rawValue, out string command, out bool canToggle)
    {
        canToggle = false;
        if (!TryReadPresentRegistryCommand(rawValue, out command) ||
            string.IsNullOrWhiteSpace(name))
            return false;
        canToggle = rawValue is string && !string.IsNullOrWhiteSpace(command);
        return true;
    }

    private static void ReadKey(List<StartupEntry> result, RegistryHive hive,
        RegistryView view, string path, bool enabled, bool includePublisher = true)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(path);
        if (key == null) return;
        foreach (var name in key.GetValueNames())
        {
            // A removed value MUST NOT become a phantom enabled Run slot.
            // Null default distinguishes disappearance from a present empty
            // string. This matters for the AI-owned startup recovery journal.
            object? rawValue = key.GetValue(
                name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (!TryClassifyRegistryStartupSlot(
                    name, rawValue, out string value, out bool canToggle))
                continue;
            var executable = canToggle ? ExecutableOf(value) : null;
            result.Add(new StartupEntry($"{hive}|{view}|{path}|{name}", name, value, hive, path.Replace(DisabledSuffix, ""), enabled,
                includePublisher ? PublisherOfExecutable(executable) : "Neznámý vydavatel",
                executable, "Registry", null, canToggle, view));
        }
    }

    private static void ReadStartupFolder(List<StartupEntry> result, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        string disabledFolder = Path.Combine(folder, "SpankyhoDisabled");

        void AddFiles(string sourceFolder, bool enabled)
        {
            if (!Directory.Exists(sourceFolder)) return;
            foreach (string file in Directory.EnumerateFiles(sourceFolder))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                result.Add(new StartupEntry(
                    $"folder|{file}", name, file, RegistryHive.CurrentUser, folder, enabled,
                    PublisherOfExecutable(file), file, "StartupFolder", file, true));
            }
        }

        AddFiles(folder, true);
        AddFiles(disabledFolder, false);
    }

    // Read a direct absolute executable target even if it no longer exists:
    // dropping a missing path here made old Spotify/Notion/launcher Run entries
    // invisible to StartupRowViewModel.IsOrphaned and the Startup hero count.
    // Relative/PATH commands and shell wrappers are intentionally unknown,
    // not marked orphaned because their resolution requires a process shell.
    private static string? ExecutableOf(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        string candidate;
        if (expanded.StartsWith('"'))
        {
            int closing = expanded.IndexOf('"', 1);
            if (closing <= 1 || (closing + 1 < expanded.Length &&
                !char.IsWhiteSpace(expanded[closing + 1])))
                return null;
            candidate = expanded[1..closing];
        }
        else
        {
            // Windows Run commands sometimes contain unquoted Program Files
            // paths followed by arguments. Find the actual .exe boundary
            // instead of truncating the path at its first space.
            int ending = 0;
            do
            {
                ending = expanded.IndexOf(".exe", ending, StringComparison.OrdinalIgnoreCase);
                if (ending < 0) return null;
                int after = ending + 4;
                if (after == expanded.Length || char.IsWhiteSpace(expanded[after]))
                {
                    candidate = expanded[..after];
                    break;
                }
                ending = after;
            } while (true);
        }

        // Do not resolve relative executable names or UNC paths with blocking
        // network file probes on the UI thread; a direct local absolute EXE is
        // the only file whose missing status we can report with confidence.
        try
        {
            if (!Path.IsPathFullyQualified(candidate) ||
                !string.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(@"\\", StringComparison.Ordinal))
                return null;
        }
        catch (ArgumentException) { return null; }
        return candidate;
    }

    internal static string RunOrphanedRegistryCommandSmoke()
    {
        string missing = Path.Combine(Path.GetTempPath(), "Spanky Missing Startup " + Guid.NewGuid().ToString("N"), "Notion.exe");
        string quoted = $"\"{missing}\" --processStart Notion.exe";
        string bare = $"{missing} --processStart";
        if (File.Exists(missing) ||
            ExecutableOf(quoted) != missing ||
            ExecutableOf(bare) != missing ||
            ExecutableOf($"\"{missing}\"") != missing ||
            ExecutableOf(missing + ".bak") != null ||
            ExecutableOf("notion.exe --processStart") != null ||
            ExecutableOf($"cmd.exe /c \"{missing}\"") != null ||
            ExecutableOf($"\"{missing}\"bad-suffix") != null ||
            ExecutableOf(@"\\test-server\share\Missing.exe") != null ||
            ExecutableOf("") != null)
            throw new InvalidOperationException("Orphaned registry startup target was not parsed safely.");

        return "STARTUP_ORPHAN_REGISTRY_TARGET_OK quoted=true unquotedSpaces=true " +
            "missingPreserved=true relativeExcluded=true wrappersExcluded=true uncExcluded=true writes=0";
    }

    private static string PublisherOfExecutable(string? executable)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executable)) return "Neznámý vydavatel";
            lock (CacheLock)
                if (PublisherCache.TryGetValue(executable, out var cached)) return cached;

            string publisher = FileVersionInfo.GetVersionInfo(executable).CompanyName ?? "Neznámý vydavatel";
            lock (CacheLock) PublisherCache[executable] = publisher;
            return publisher;
        }
        catch { return "Neznámý vydavatel"; }
    }

    // The startup inventory / worker payload is NOT an authority for
    // arbitrary registry paths. Only actual Windows Run / RunOnce slots
    // enumerated by this service are legal. Fail closed before creating a
    // subkey, starting a worker, or moving any native startup value.
    internal static bool IsAdmissibleRegistryStartupMove(StartupEntry item) =>
        string.Equals(item.SourceKind, "Registry", StringComparison.OrdinalIgnoreCase) &&
        item.CanToggle &&
        !string.IsNullOrWhiteSpace(item.Name) &&
        !string.IsNullOrWhiteSpace(item.Command) &&
        item.Hive is RegistryHive.CurrentUser or RegistryHive.LocalMachine &&
        item.View is RegistryView.Registry32 or RegistryView.Registry64 &&
        RunPaths.Any(path => string.Equals(
            item.RunPath, path, StringComparison.OrdinalIgnoreCase));

    internal static bool IsMovableRegistryStartupValueKind(RegistryValueKind kind) =>
        kind is RegistryValueKind.String or RegistryValueKind.ExpandString;

    // The inventory is only a snapshot. A user or another program may change
    // a Run value before we move it. Never migrate an unexpected command:
    // such a move would mutate somebody else's startup preference.
    internal static bool MatchesExpectedRegistryCommand(
        object? currentValue, string expectedCommand) =>
        currentValue is string text &&
        string.Equals(text, expectedCommand, StringComparison.Ordinal);

    // Never remove an unowned destination during rollback. A failed native
    // SetValue may leave an independently-created target of the same command
    // and type; equality is NOT proof that our write succeeded. Only an
    // actually completed own target SetValue may authorize target cleanup,
    // and only when the original source is safely restored and still matches.
    internal static bool MayDeleteStartupRollbackTarget(
        bool targetWrittenByUs, bool sourceRestored, object? targetValue,
        RegistryValueKind targetKind, string originalCommand,
        RegistryValueKind originalKind) =>
        targetWrittenByUs && sourceRestored && targetKind == originalKind &&
        MatchesExpectedRegistryCommand(targetValue, originalCommand);

    // The inventory only offers direct files from the two actual Windows
    // startup folders. Never move an arbitrary path supplied by a stale UI
    // snapshot or a malformed worker payload outside that narrow scope.
    internal static bool IsExpectedStartupFolderSource(
        string? sourceId, string? runPath, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(sourceId) ||
            string.IsNullOrWhiteSpace(runPath) ||
            !Path.IsPathFullyQualified(sourceId) ||
            !Path.IsPathFullyQualified(runPath))
            return false;
        try
        {
            string root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(runPath));
            string userStartup = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup)));
            string commonStartup = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)));
            if (!root.Equals(userStartup, StringComparison.OrdinalIgnoreCase) &&
                !root.Equals(commonStartup, StringComparison.OrdinalIgnoreCase))
                return false;

            string expectedParent = enabled
                ? root : Path.Combine(root, "SpankyhoDisabled");
            string? actualParent = Path.GetDirectoryName(Path.GetFullPath(sourceId));
            return !string.IsNullOrWhiteSpace(Path.GetFileName(sourceId)) &&
                actualParent is not null &&
                actualParent.Equals(expectedParent, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // Registry Run value names are case-insensitive. A disabled entry and an
    // enabled entry can legitimately share a name; never replace the target.
    private static void RequireVacantRegistryTarget(IEnumerable<string> targetNames, string entryName)
    {
        // Registry value names are case-insensitive. Overwriting an existing
        // target here could destroy a distinct active startup command, and the
        // old rollback would then remove that unrelated target completely.
        if (targetNames.Contains(entryName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Položku '{entryName}' nelze přesunout: cílový startup záznam se stejným názvem již existuje.");
    }

    // The first list-based collision check is a snapshot. A second
    // just-in-time read detects a value written by another process since
    // that enumeration, BEFORE any SetValue or rollback cleanup can occur.
    // This narrows the race; Registry does not offer an atomic move/CAS.
    internal static void RequireVacantRegistryTargetAtWrite(
        IEnumerable<string> targetNames, object? currentValue, string entryName)
    {
        RequireVacantRegistryTarget(targetNames, entryName);
        if (currentValue is not null)
            throw new InvalidOperationException(
                "Startup destination was populated after enumeration; move rejected.");
    }

    // DeleteValue(name, throwOnMissingValue:false) silently succeeds when
    // another process removed the value since the last readback. Such a
    // no-op must never mark sourceDeletedByUs and authorize resurrection.
    // A strict delete throws on missing; callers only mark own deletion
    // completed AFTER the injected native operation actually returns.
    internal static bool CompleteStrictStartupSourceDelete(
        Action<bool> deleteValue)
    {
        ArgumentNullException.ThrowIfNull(deleteValue);
        deleteValue(true);
        return true;
    }

    // Restoring a missing source when we have NOT successfully completed
    // our own DeleteValue would resurrect a startup item removed by the
    // user/another process. Only our completed delete authorizes rollback.
    internal static bool MayRecreateStartupSourceDuringRollback(
        bool sourceDeletedByUs, object? currentSource) =>
        sourceDeletedByUs && currentSource is null;

    // Do not call CreateSubKey merely to discover that a destination
    // already exists and contains a colliding Run value. First open any
    // existing key, create only if absent, and validate its names. Keep
    // the opened handle owned by the caller on success; on rejection,
    // dispose it without touching its values or deleting the source.
    // The injected operations make ordering verifiable without live registry.
    internal static T OpenVacantRegistryMoveTarget<T>(
        Func<T?> openExisting, Func<T> createIfMissing,
        Func<T, IEnumerable<string>> getValueNames, string entryName)
        where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(openExisting);
        ArgumentNullException.ThrowIfNull(createIfMissing);
        ArgumentNullException.ThrowIfNull(getValueNames);
        if (string.IsNullOrWhiteSpace(entryName))
            throw new ArgumentException(
                "Registry startup value name must be nonempty.", nameof(entryName));

        T target = openExisting() ?? createIfMissing();
        if (target is null)
            throw new InvalidOperationException(
                "Startup registry target could not be opened or created.");
        try
        {
            RequireVacantRegistryTarget(getValueNames(target), entryName);
            return target;
        }
        catch
        {
            // A failed destination admission must not leak the opened key.
            // A broken Dispose must not mask the original collision.
            try { target.Dispose(); }
            catch { }
            throw;
        }
    }

    internal static string RunRegistryMoveCollisionSmoke()
    {
        string[] existing = { "Spotify", "Notion" };
        RequireVacantRegistryTarget(existing, "DifferentApp");
        int rejected = 0;
        foreach (string colliding in new[] { "Spotify", "SPOTIFY", "notion" })
        {
            try { RequireVacantRegistryTarget(existing, colliding); }
            catch (InvalidOperationException) { rejected++; }
        }
        if (rejected != 3)
            throw new InvalidOperationException(
                "Startup registry move would overwrite an existing active/disabled entry.");

        // v16.183: the collision check must use OpenSubKey first, without
        // invoking CreateSubKey for a pre-existing destination. Pure synthetic
        // IDisposable handles stand in for registry keys; no OS key is opened.
        int createdDuringCollision = 0;
        using var occupiedHandle = new MemoryStream();
        bool collisionRejected = false;
        try
        {
            _ = OpenVacantRegistryMoveTarget(
                () => occupiedHandle,
                () => { createdDuringCollision++; return new MemoryStream(); },
                _ => new[] { "SPOTIFY", "Notion" }, "Spotify");
        }
        catch (InvalidOperationException) { collisionRejected = true; }
        if (!collisionRejected || createdDuringCollision != 0 ||
            occupiedHandle.CanRead)
            throw new InvalidOperationException(
                "Existing startup collision created target or leaked registry handle.");

        int createsForMissingKey = 0;
        using var createdHandle = new MemoryStream();
        var admitted = OpenVacantRegistryMoveTarget(
            () => (MemoryStream?)null,
            () => { createsForMissingKey++; return createdHandle; },
            _ => Array.Empty<string>(), "Spotify");
        if (createsForMissingKey != 1 || !ReferenceEquals(admitted, createdHandle) ||
            !admitted.CanRead)
            throw new InvalidOperationException(
                "Missing startup registry destination was not created exactly once.");

        // v16.184: the destination may be populated after GetValueNames()
        // but before SetValue, and an unrelated user can remove the source
        // before our own DeleteValue. Neither scenario authorizes rollback
        // to overwrite somebody else's choice. No registry operations.
        RequireVacantRegistryTargetAtWrite(Array.Empty<string>(), null, "Spotify");
        bool lateTargetOccupied = false;
        try
        {
            RequireVacantRegistryTargetAtWrite(
                Array.Empty<string>(), "external-override.exe", "Spotify");
        }
        catch (InvalidOperationException) { lateTargetOccupied = true; }
        bool newlyEnumeratedCollision = false;
        try
        {
            RequireVacantRegistryTargetAtWrite(
                new[] { "SPOTIFY" }, null, "Spotify");
        }
        catch (InvalidOperationException) { newlyEnumeratedCollision = true; }
        if (!lateTargetOccupied || !newlyEnumeratedCollision ||
            MayRecreateStartupSourceDuringRollback(false, null) ||
            MayRecreateStartupSourceDuringRollback(false, "old.exe") ||
            MayRecreateStartupSourceDuringRollback(true, "user-new.exe") ||
            !MayRecreateStartupSourceDuringRollback(true, null))
            throw new InvalidOperationException(
                "Registry startup rollback would resurrect an external deletion or overwrite the target.");

        // v16.185: native DeleteValue must throw if another process
        // removed the source between readback and our own deletion.
        // Only an ACTUALLY completed strict delete grants rollback rights.
        var syntheticSource = new Dictionary<string, string>
        {
            ["Spotify"] = "original.exe"
        };
        bool sourceWasDeletedByUs = false, strictMissingRejected = false;
        syntheticSource.Remove("Spotify"); // external user removed it
        try
        {
            sourceWasDeletedByUs = CompleteStrictStartupSourceDelete(
                throwOnMissing =>
                {
                    if (!syntheticSource.Remove("Spotify") && throwOnMissing)
                        throw new ArgumentException("Synthetic source is already gone.");
                });
        }
        catch (ArgumentException) { strictMissingRejected = true; }
        if (!strictMissingRejected || sourceWasDeletedByUs ||
            MayRecreateStartupSourceDuringRollback(
                sourceWasDeletedByUs, syntheticSource.GetValueOrDefault("Spotify")))
            throw new InvalidOperationException(
                "Strict registry source delete treated an external deletion as our own.");

        syntheticSource["Spotify"] = "original.exe";
        sourceWasDeletedByUs = CompleteStrictStartupSourceDelete(
            throwOnMissing =>
            {
                if (!syntheticSource.Remove("Spotify") && throwOnMissing)
                    throw new ArgumentException("Synthetic source is absent.");
            });
        if (!sourceWasDeletedByUs || syntheticSource.ContainsKey("Spotify") ||
            !MayRecreateStartupSourceDuringRollback(
                sourceWasDeletedByUs, syntheticSource.GetValueOrDefault("Spotify")))
            throw new InvalidOperationException(
                "Strict registry source delete did not grant legitimate rollback rights.");

        // No registry reads/writes: exercise stale inventory, unchanged
        // command, changed command, wrong data type and null values.
        if (!MatchesExpectedRegistryCommand("app.exe --silent", "app.exe --silent") ||
            MatchesExpectedRegistryCommand("app.exe --update", "app.exe --silent") ||
            MatchesExpectedRegistryCommand("APP.exe --silent", "app.exe --silent") ||
            MatchesExpectedRegistryCommand(null, "app.exe --silent") ||
            MatchesExpectedRegistryCommand(new[] { "app.exe" }, "app.exe"))
            throw new InvalidOperationException(
                "Startup Run command changed after inventory: stale move admission regressed.");

        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Spotify"] = "disabled-command"
        };
        var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPOTIFY"] = "original-active-command"
        };
        bool blockedBeforeWrite = false;
        try
        {
            RequireVacantRegistryTarget(target.Keys, "Spotify");
            target["Spotify"] = source["Spotify"];
            source.Remove("Spotify");
        }
        catch (InvalidOperationException) { blockedBeforeWrite = true; }
        if (!blockedBeforeWrite || source["Spotify"] != "disabled-command" ||
            target["Spotify"] != "original-active-command")
            throw new InvalidOperationException(
                "Startup collision validation did not preserve both existing commands.");

        // The rollback must not delete a target when the native SetValue
        // never completed, when source restore failed, or when data/kind differ.
        if (MayDeleteStartupRollbackTarget(false, true, "app.exe", RegistryValueKind.String,
                "app.exe", RegistryValueKind.String) ||
            MayDeleteStartupRollbackTarget(true, false, "app.exe", RegistryValueKind.String,
                "app.exe", RegistryValueKind.String) ||
            !MayDeleteStartupRollbackTarget(true, true, "app.exe", RegistryValueKind.String,
                "app.exe", RegistryValueKind.String) ||
            MayDeleteStartupRollbackTarget(true, true, "other.exe", RegistryValueKind.String,
                "app.exe", RegistryValueKind.String) ||
            MayDeleteStartupRollbackTarget(true, true, "app.exe", RegistryValueKind.ExpandString,
                "app.exe", RegistryValueKind.String) ||
            MayDeleteStartupRollbackTarget(true, true, null, RegistryValueKind.None,
                "app.exe", RegistryValueKind.String))
            throw new InvalidOperationException(
                "Startup rollback might discard the sole surviving registry command.");

        // v16.182: a stale inventory/worker payload must not authorize
        // arbitrary native registry mutations outside real Run/RunOnce
        // slots, nor a default/blank value. All test entries are synthetic.
        var validRun = new StartupEntry("synthetic", "Spotify",
            "\"C:\\Apps\\Spotify.exe\" --silent",
            RegistryHive.CurrentUser, RunPaths[0], true,
            "Spotify", @"C:\Apps\Spotify.exe",
            View: RegistryView.Registry64);
        if (!IsAdmissibleRegistryStartupMove(validRun) ||
            !IsAdmissibleRegistryStartupMove(validRun with
                { RunPath = RunPaths[1], Hive = RegistryHive.LocalMachine,
                  View = RegistryView.Registry32 }) ||
            IsAdmissibleRegistryStartupMove(validRun with { Name = "" }) ||
            IsAdmissibleRegistryStartupMove(validRun with { Command = " " }) ||
            IsAdmissibleRegistryStartupMove(validRun with
                { RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run\Other" }) ||
            IsAdmissibleRegistryStartupMove(validRun with
                { RunPath = RunPaths[0] + DisabledSuffix }) ||
            IsAdmissibleRegistryStartupMove(validRun with
                { Hive = RegistryHive.ClassesRoot }) ||
            IsAdmissibleRegistryStartupMove(validRun with
                { View = RegistryView.Default }) ||
            IsAdmissibleRegistryStartupMove(validRun with
                { SourceKind = "ScheduledTask" }) ||
            !IsMovableRegistryStartupValueKind(RegistryValueKind.String) ||
            !IsMovableRegistryStartupValueKind(RegistryValueKind.ExpandString) ||
            IsMovableRegistryStartupValueKind(RegistryValueKind.DWord) ||
            IsMovableRegistryStartupValueKind(RegistryValueKind.MultiString))
            throw new InvalidOperationException(
                "Startup registry move allowed a malformed location, kind or worker payload.");

        // Pure path fixtures: no Windows startup files are created or moved.
        string startupFolder = Environment.GetFolderPath(
            Environment.SpecialFolder.Startup);
        string launcher = Path.Combine(startupFolder, "fixture.lnk");
        string disabled = Path.Combine(startupFolder,
            "SpankyhoDisabled", "fixture.lnk");
        if (!IsExpectedStartupFolderSource(launcher, startupFolder, true) ||
            !IsExpectedStartupFolderSource(disabled, startupFolder, false) ||
            IsExpectedStartupFolderSource(launcher, startupFolder, false) ||
            IsExpectedStartupFolderSource(disabled, startupFolder, true) ||
            IsExpectedStartupFolderSource(
                Path.Combine(startupFolder, "Subfolder", "fixture.lnk"),
                startupFolder, true) ||
            IsExpectedStartupFolderSource(launcher,
                Path.GetTempPath(), true) ||
            IsExpectedStartupFolderSource(@"fixture.lnk",
                startupFolder, true))
            throw new InvalidOperationException(
                "Startup folder source admission is not restricted to Windows startup folders.");

        return "STARTUP_REGISTRY_MOVE_COLLISION_SMOKE_OK cases=5 collisionsRejected=4 " +
            "existingSourcePreserved=true existingTargetPreserved=true registryWrites=0";
    }

    public static async Task SetEnabledAsync(StartupEntry item, bool enabled, CancellationToken token = default)
    {
        // The AI can be switched off while the registry operation is still
        // queued. Reject cancellation before any native startup mutation.
        token.ThrowIfCancellationRequested();
        if (item.Enabled == enabled) return;
        if (!item.CanToggle) throw new InvalidOperationException("Tato startup položka je chráněná.");
        if (item.SourceKind.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase))
            TaskSchedulerService.EnsureTaskMutationAllowed(
                item.SourceId ?? string.Empty, enabled);
        if (!string.Equals(item.SourceKind, "StartupFolder", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.SourceKind, "ScheduledTask", StringComparison.OrdinalIgnoreCase) &&
            !IsAdmissibleRegistryStartupMove(item))
            throw new InvalidOperationException(
                "Startup registry cesta, název, příkaz nebo registr view nejsou v povoleném rozsahu Run/RunOnce.");

        bool requiresElevation =
            item.Hive != RegistryHive.CurrentUser ||
            (item.SourceKind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase) &&
             item.RunPath.Equals(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StringComparison.OrdinalIgnoreCase));

        if (!WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator() && requiresElevation)
        {
            try
            {
                await WorkerClientService.RunAsync<WorkerAck>(
                    "startup-set",
                    WorkerPayloads.Encode(new StartupStateChangeRequest(item, enabled)),
                    TimeSpan.FromSeconds(45),
                    token).ConfigureAwait(false);
            }
            finally
            {
                // A worker timeout/failed acknowledgement does not prove
                // that its native write never started. Drop stale inventory.
                InvalidateCache();
            }
            LogService.Info($"Startup privileged state changed and verified via worker: {item.Name}, enabled={enabled}");
            return;
        }

        if (item.SourceKind.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            string path = item.SourceId ?? throw new InvalidOperationException("Task path chybí.");
            try
            {
                await new TaskSchedulerService().SetEnabledAsync(path, enabled, token).ConfigureAwait(false);
            }
            finally
            {
                // Readback/cancellation failures may follow a successful COM
                // mutation. Do not retain a stale Startup page snapshot.
                InvalidateCache();
            }
            LogService.Info($"Startup task changed and verified: {path}, enabled={enabled}");
            return;
        }

        if (item.SourceKind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase))
        {
            Task fileMove = Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                string source = item.SourceId ?? throw new InvalidOperationException("Startup file path chybí.");
                if (!IsExpectedStartupFolderSource(source, item.RunPath, item.Enabled))
                    throw new InvalidOperationException(
                        "Startup soubor neleží v očekávané složce Po spuštění, přesun zablokován.");
                string disabledDir = Path.Combine(item.RunPath, "SpankyhoDisabled");
                string target = enabled
                    ? Path.Combine(item.RunPath, Path.GetFileName(source))
                    : Path.Combine(disabledDir, Path.GetFileName(source));
                if (!File.Exists(source)) throw new InvalidOperationException("Startup soubor už neexistuje.");
                if (File.Exists(target)) throw new IOException("Cílový startup soubor už existuje.");
                token.ThrowIfCancellationRequested();
                // Re-enabling never needs to create the disabled folder.
                // Only a pending disable creates its destination directory.
                if (!enabled)
                {
                    Directory.CreateDirectory(disabledDir);
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(target)) throw new IOException("Cílový startup soubor už existuje.");
                }
                File.Move(source, target);
                if (!File.Exists(target) || File.Exists(source))
                {
                    try { if (File.Exists(target) && !File.Exists(source)) File.Move(target, source); } catch { }
                    throw new InvalidOperationException("Read-back nepotvrdil přesun startup souboru.");
                }
            }, token);
            try { await fileMove.ConfigureAwait(false); }
            finally { InvalidateCache(); }
            LogService.Info($"Startup folder item changed and verified: {item.Name}, enabled={enabled}");
            return;
        }

        Task registryMove = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var root = RegistryKey.OpenBaseKey(item.Hive, item.View);
            var sourcePath = item.RunPath + (enabled ? DisabledSuffix : "");
            var targetPath = item.RunPath + (enabled ? "" : DisabledSuffix);
            using var source = root.OpenSubKey(sourcePath, writable: true) ?? throw new InvalidOperationException("Položka po spuštění už neexistuje.");
            var value = source.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? throw new InvalidOperationException("Hodnotu nelze přečíst.");
            // Revalidate actual registry contents immediately before the
            // write, not only the earlier StartupEntry/AI inventory object.
            // Fail closed rather than moving a replaced/updated Run command.
            if (!MatchesExpectedRegistryCommand(value, item.Command))
                throw new InvalidOperationException(
                    "Startup příkaz se od inventáře změnil, přesun je zablokován.");
            var kind = source.GetValueKind(item.Name);
            // Revalidate the native REG_SZ / REG_EXPAND_SZ type as well as
            // the command before creating any destination. A stale, forged
            // or unexpected value kind must never be migrated.
            if (!IsMovableRegistryStartupValueKind(kind))
                throw new InvalidOperationException(
                    "Typ položky startup registru není REG_SZ ani REG_EXPAND_SZ, přesun zablokován.");
            token.ThrowIfCancellationRequested();
            using var target = OpenVacantRegistryMoveTarget(
                () => root.OpenSubKey(targetPath, writable: true),
                () => root.CreateSubKey(targetPath, writable: true)
                    ?? throw new InvalidOperationException(
                        "Startup registry target could not be created."),
                key => key.GetValueNames(), item.Name);
            // Existing targets are checked without a CreateSubKey attempt.
            // If the target does not exist, create it and check again after
            // creation, before writing. After writing, finish or rollback;
            // never intentionally strand half a startup move.
            token.ThrowIfCancellationRequested();
            // The source may also have been removed or replaced by the
            // user since the initial read. Do not resurrect that command
            // by copying it to the target from our stale snapshot.
            if (!MatchesExpectedRegistryCommand(
                    source.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                    item.Command) || source.GetValueKind(item.Name) != kind)
                throw new InvalidOperationException(
                    "Startup source changed before the move write; operation cancelled.");
            // Fail before entering the rollback catch when another writer
            // fills the target between the first inventory check and now.
            // Never allow the rollback to delete someone else's value.
            RequireVacantRegistryTargetAtWrite(
                target.GetValueNames(),
                target.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                item.Name);
            bool sourceDeletedByUs = false;
            bool targetWrittenByUs = false;
            try
            {
                target.SetValue(item.Name, value, kind);
                // A native SetValue that throws is ambiguous: never delete a
                // potentially external matching target in our rollback.
                targetWrittenByUs = true;
                // A successful SetValue call is not enough to delete the
                // only original. Verify the exact copied command and type,
                // and check the source again before deleting it.
                if (!MatchesExpectedRegistryCommand(
                        target.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                        item.Command) || target.GetValueKind(item.Name) != kind ||
                    !MatchesExpectedRegistryCommand(
                        source.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                        item.Command) || source.GetValueKind(item.Name) != kind)
                    throw new InvalidOperationException(
                        "Startup položka se změnila během přesunu, zdroj ponechán.");
                // A concurrent external delete must throw, not be
                // mistaken for our completed deletion. Otherwise rollback
                // could resurrect the user-removed Run entry.
                sourceDeletedByUs = CompleteStrictStartupSourceDelete(
                    throwOnMissing => source.DeleteValue(item.Name, throwOnMissing));
                var targetValue = target.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var sourceValue = source.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (!MatchesExpectedRegistryCommand(targetValue, item.Command) ||
                    sourceValue != null || target.GetValueKind(item.Name) != kind)
                    throw new InvalidOperationException("Read-back nepotvrdil příkaz, typ a přesun startup položky.");
            }
            catch
            {
                // Restore source only if it is still vacant. Never overwrite
                // a user-modified value that raced with this operation.
                bool originalRestored = false;
                try
                {
                    object? currentSource = source.GetValue(
                        item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (MayRecreateStartupSourceDuringRollback(
                            sourceDeletedByUs, currentSource))
                    {
                        source.SetValue(item.Name, value, kind);
                        currentSource = source.GetValue(
                            item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    }
                    originalRestored = MatchesExpectedRegistryCommand(
                        currentSource, item.Command) && source.GetValueKind(item.Name) == kind;
                }
                catch { }
                // Critical: if restoring the source FAILED, the matching
                // target may be the only surviving copy. Never delete it.
                // Also preserve a target changed by any external writer.
                try
                {
                    object? currentTarget = target.GetValue(
                        item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    RegistryValueKind currentKind = currentTarget is null
                        ? RegistryValueKind.None : target.GetValueKind(item.Name);
                    if (MayDeleteStartupRollbackTarget(
                            targetWrittenByUs, originalRestored, currentTarget,
                            currentKind, item.Command, kind))
                        target.DeleteValue(item.Name, false);
                }
                catch { }
                throw;
            }
        }, token);
        try { await registryMove.ConfigureAwait(false); }
        finally { InvalidateCache(); }
        LogService.Info($"Startup registry state changed and verified: {item.Name}, enabled={enabled}, view={item.View}");
    }

    // Permanent removal of an app-owned startup entry. Scheduled tasks stay
    // toggle-only; registry Run values and Startup-folder shortcuts are the
    // narrow, reversible-by-reinstall scope offered by the UI.
    public static async Task DeleteAsync(StartupEntry item, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!item.CanToggle)
            throw new InvalidOperationException("Tato startup položka je chráněná.");

        if (item.SourceKind.Equals("Registry", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAdmissibleRegistryStartupMove(item))
                throw new InvalidOperationException(
                    "Startup registry cesta, název, příkaz nebo registr view nejsou v povoleném rozsahu Run/RunOnce.");
            bool requiresElevation = item.Hive != RegistryHive.CurrentUser;
            if (!WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator() && requiresElevation)
            {
                try
                {
                    await WorkerClientService.RunAsync<WorkerAck>(
                        "startup-delete",
                        WorkerPayloads.Encode(new StartupDeleteRequest(item)),
                        TimeSpan.FromSeconds(45),
                        token).ConfigureAwait(false);
                }
                finally { InvalidateCache(); }
                LogService.Info($"Startup registry item deleted via worker: {item.Name}");
                return;
            }

            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var root = RegistryKey.OpenBaseKey(item.Hive, item.View);
                string path = item.RunPath + (item.Enabled ? "" : DisabledSuffix);
                using var key = root.OpenSubKey(path, writable: true)
                    ?? throw new InvalidOperationException("Položka po spuštění už neexistuje.");
                object? value = key.GetValue(item.Name, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value is null ||
                    !MatchesExpectedRegistryCommand(value, item.Command) ||
                    !IsMovableRegistryStartupValueKind(key.GetValueKind(item.Name)))
                    throw new InvalidOperationException(
                        "Startup příkaz se od inventáře změnil, smazání je zablokováno.");
                token.ThrowIfCancellationRequested();
                key.DeleteValue(item.Name, throwOnMissingValue: false);
                if (key.GetValueNames().Any(x =>
                        string.Equals(x, item.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        "Read-back stále vidí startup hodnotu.");
            }, token).ConfigureAwait(false);
            InvalidateCache();
            LogService.Info($"Startup registry item deleted and verified: {item.Name}");
            return;
        }

        if (item.SourceKind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase))
        {
            bool requiresElevation = item.RunPath.Equals(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                StringComparison.OrdinalIgnoreCase);
            if (!WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator() && requiresElevation)
            {
                try
                {
                    await WorkerClientService.RunAsync<WorkerAck>(
                        "startup-delete",
                        WorkerPayloads.Encode(new StartupDeleteRequest(item)),
                        TimeSpan.FromSeconds(45),
                        token).ConfigureAwait(false);
                }
                finally { InvalidateCache(); }
                LogService.Info($"Startup folder item deleted via worker: {item.Name}");
                return;
            }

            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                string source = item.SourceId
                    ?? throw new InvalidOperationException("Startup file path chybí.");
                if (!IsExpectedStartupFolderSource(source, item.RunPath, item.Enabled))
                    throw new InvalidOperationException(
                        "Startup soubor neleží v očekávané složce Po spuštění, smazání zablokováno.");
                if (!File.Exists(source))
                    throw new InvalidOperationException("Startup soubor už neexistuje.");
                token.ThrowIfCancellationRequested();
                File.Delete(source);
                if (File.Exists(source))
                    throw new InvalidOperationException(
                        "Read-back stále vidí startup soubor.");
            }, token).ConfigureAwait(false);
            InvalidateCache();
            LogService.Info($"Startup folder item deleted and verified: {item.Name}");
            return;
        }

        if (item.SourceKind.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase))
        {
            string path = item.SourceId
                ?? throw new InvalidOperationException("Task path chybí.");
            if (path.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"\SpankyhoOptimizer-", StringComparison.OrdinalIgnoreCase) ||
                !TaskSafetyService.CanDisable(path))
                throw new InvalidOperationException("Tato plánovaná úloha je chráněná.");

            if (!WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator())
            {
                try
                {
                    await WorkerClientService.RunAsync<WorkerAck>(
                        "startup-delete",
                        WorkerPayloads.Encode(new StartupDeleteRequest(item)),
                        TimeSpan.FromSeconds(45),
                        token).ConfigureAwait(false);
                }
                finally { InvalidateCache(invalidateTaskScheduler: true); }
                LogService.Info($"Startup scheduled task deleted via worker: {path}");
                return;
            }

            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                _ = TaskSchedulerComService.DeleteIfExists(path);
                if (TaskSchedulerComService.TryGetTaskXml(path) != null)
                    throw new InvalidOperationException(
                        "Read-back stále vidí plánovanou úlohu.");
            }, token).ConfigureAwait(false);
            InvalidateCache(invalidateTaskScheduler: true);
            LogService.Info($"Startup scheduled task deleted and verified: {path}");
            return;
        }

        throw new InvalidOperationException(
            "Mazání je povoleno jen pro registry, složku Po spuštění a bezpečné plánované úlohy.");
    }
}

public static class GhostUsbService
{
    private const string PersistentCacheKey = "ghost-usb-v1";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(3);
    private static readonly object CacheLock = new();
    private static IReadOnlyList<string>? _cache;
    private static DateTime _cacheAt;
    private static Task<IReadOnlyList<string>>? _flight;

    public static IReadOnlyList<string>? TryGetFreshSnapshot()
    {
        lock (CacheLock)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < CacheTtl)
                return _cache;
        }

        if (PersistentHardwareCacheService.TryReadMachineStable<string[]>(
                PersistentCacheKey, CacheTtl, out var persisted) &&
            persisted != null)
        {
            lock (CacheLock)
            {
                _cache = persisted;
                _cacheAt = DateTime.UtcNow;
            }
            LogService.Info("performance", $"GHOST_USB_PERSISTENT_HIT count={persisted.Length}");
            return persisted;
        }

        return null;
    }

    public static Task<IReadOnlyList<string>> GetAsync()
    {
        var cached = TryGetFreshSnapshot();
        if (cached != null) return Task.FromResult(cached);

        lock (CacheLock)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < CacheTtl) return Task.FromResult(_cache);
            if (_flight != null && !_flight.IsCompleted) return _flight;
            _flight = LoadAsync();
            return _flight;
        }
    }

    private static async Task<IReadOnlyList<string>> LoadAsync()
    {
        try
        {
            var result = await ProcessRunner.RunAsync("pnputil.exe", "/enum-devices /disconnected /class USB", TimeSpan.FromSeconds(30));
            IReadOnlyList<string> data = !result.Success ? Array.Empty<string>() : Regex.Matches(result.StdOut, "(?:USB|HID)\\[^\r\n]+", RegexOptions.IgnoreCase)
                .Select(x => x.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            PersistentHardwareCacheService.WriteMachineStable(PersistentCacheKey, data.ToArray());
            lock (CacheLock) { _cache = data; _cacheAt = DateTime.UtcNow; _flight = null; }
            return data;
        }
        catch { lock (CacheLock) _flight = null; throw; }
    }

    public static void InvalidateCache()
    {
        lock (CacheLock) { _cache = null; _cacheAt = default; }
        PersistentHardwareCacheService.Remove(PersistentCacheKey);
    }

    internal static async Task RunPersistentCacheSmokeAsync(CancellationToken token)
    {
        InvalidateCache();

        var freshSw = Stopwatch.StartNew();
        var fresh = await GetAsync().WaitAsync(token).ConfigureAwait(false);
        freshSw.Stop();

        lock (CacheLock)
        {
            _cache = null;
            _cacheAt = default;
            _flight = null;
        }

        var persistentSw = Stopwatch.StartNew();
        var persisted = await GetAsync().WaitAsync(token).ConfigureAwait(false);
        persistentSw.Stop();

        if (!fresh.SequenceEqual(persisted, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ghost USB persistent cache mismatch.");
        if (persistentSw.ElapsedMilliseconds > 150)
            throw new InvalidOperationException($"Ghost USB persistent cache is too slow: {persistentSw.ElapsedMilliseconds} ms.");

        LogService.Info($"GHOST_USB_CACHE_SMOKE_OK freshMs={freshSw.ElapsedMilliseconds} persistentMs={persistentSw.ElapsedMilliseconds} count={persisted.Count}");
    }

    private static async Task<bool> IsCurrentlyDisconnectedAsync(string instanceId)
    {
        var result = await ProcessRunner.RunAsync("pnputil.exe", "/enum-devices /disconnected", TimeSpan.FromSeconds(30));
        if (!result.Success) throw new InvalidOperationException("Fresh disconnected-device read-back selhal: " + (result.StdErr + result.StdOut).Trim());
        return Regex.Matches(result.StdOut, "Instance ID:\\s*(.+)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Trim())
            .Any(id => string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task RemoveAsync(string instanceId)
    {
        if (!Regex.IsMatch(instanceId, "^(USB|HID)\\\\", RegexOptions.IgnoreCase)) throw new ArgumentException("Neplatné USB ID.");
        if (!await IsCurrentlyDisconnectedAsync(instanceId).ConfigureAwait(false))
            throw new InvalidOperationException("Zařízení už není odpojené; odebrání bylo bezpečně zrušeno.");

        var result = await PrivilegedProcessRunner.RunAsync("pnputil.exe", $"/remove-device \"{instanceId}\"", TimeSpan.FromSeconds(30));
        if (!result.Success) throw new InvalidOperationException(result.StdErr + result.StdOut);
        if (await IsCurrentlyDisconnectedAsync(instanceId).ConfigureAwait(false))
            throw new InvalidOperationException("Read-back stále vidí zařízení mezi odpojenými položkami.");
        InvalidateCache();
        LogService.Info("Removed verified disconnected USB device: " + instanceId);
    }
}

public static class LauncherCacheService
{
    public static IReadOnlyDictionary<string, string[]> Catalog { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Steam"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam", "htmlcache") },
        ["Battle.net"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Battle.net", "Cache"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Battle.net", "Cache") },
        ["Epic Games"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "webcache*") },
        ["EA app"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Electronic Arts", "EA Desktop", "CEF", "Cache") },
        ["Ubisoft Connect"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ubisoft Game Launcher", "cache") },
        ["Riot Client"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Cache") },
        ["GOG Galaxy"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GOG Galaxy", "webcache") },
        ["Discord"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "Cache"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "Code Cache"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "GPUCache") }
    };

    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!path.Contains('*')) { yield return path; continue; }
            var parent = Path.GetDirectoryName(path); var pattern = Path.GetFileName(path);
            if (parent == null || !Directory.Exists(parent)) continue;
            foreach (var dir in Directory.EnumerateDirectories(parent, pattern))
            {
                // Never follow a junction/symlink into an unrelated tree.
                if (!IsReparsePoint(dir)) yield return dir;
            }
        }
    }

    public static Task<long> GetSizeAsync(IEnumerable<string> paths, CancellationToken token = default) => Task.Run(() =>
    {
        long total = 0;
        foreach (string path in Expand(paths))
        {
            token.ThrowIfCancellationRequested();
            total += DirectorySize(path, token);
        }
        return total;
    }, token);

    // Cache cleanup must never traverse a junction or symbolic link. A planted
    // or redirected link inside TEMP/a launcher cache would otherwise make the
    // recursive delete remove files outside the selected cache root.
    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; } // Unreadable path: fail closed, do not traverse.
    }

    private static (List<string> Files, List<string> Directories) EnumerateTreeNoReparse(
        string root, CancellationToken token)
    {
        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            if (IsReparsePoint(directory)) continue;
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsReparsePoint(file)) files.Add(file);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            try
            {
                foreach (string subdirectory in Directory.EnumerateDirectories(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (IsReparsePoint(subdirectory)) continue;
                    directories.Add(subdirectory);
                    pending.Push(subdirectory);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return (files, directories);
    }

    private static long DirectorySize(string path, CancellationToken token = default)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        var (files, _) = EnumerateTreeNoReparse(path, token);
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            try { total += new FileInfo(file).Length; } catch { }
        }
        return total;
    }

    public sealed record CleanupResult(long BeforeBytes, long AfterBytes, int FailedFiles)
    {
        public long FreedBytes => Math.Max(0, BeforeBytes - AfterBytes);
        public bool FullyCleared => AfterBytes == 0 && FailedFiles == 0;
    }

    public static Task<CleanupResult> ClearVerifiedAsync(IEnumerable<string> paths, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var expanded = Expand(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        long before = 0;
        foreach (string path in expanded)
        {
            token.ThrowIfCancellationRequested();
            before += DirectorySize(path, token);
        }

        int failed = 0;
        foreach (var path in expanded)
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(path)) continue;
            // A redirected cache root is refused, not followed. Report it as a
            // failure so the UI never claims a junctioned tree was cleared.
            if (IsReparsePoint(path)) { failed++; continue; }
            var (files, dirs) = EnumerateTreeNoReparse(path, token);

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                try { File.Delete(file); } catch { failed++; }
            }

            foreach (var dir in dirs.OrderByDescending(x => x.Length))
            {
                token.ThrowIfCancellationRequested();
                try { Directory.Delete(dir, false); } catch { if (Directory.Exists(dir)) failed++; }
            }
        }

        long after = 0;
        foreach (string path in expanded)
        {
            token.ThrowIfCancellationRequested();
            after += DirectorySize(path, token);
        }
        return new CleanupResult(before, after, failed);
    }, token);

    public static async Task ClearAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        _ = await ClearVerifiedAsync(paths, token);
    }
}

public static class LauncherGamingModeService
{
    private static readonly Dictionary<string, string[]> Processes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Steam"] = new[] { "steamwebhelper" },
        ["Battle.net"] = new[] { "Battle.net", "Agent" },
        ["Epic Games"] = new[] { "EpicWebHelper" },
        ["EA app"] = new[] { "EADesktop", "EABackgroundService" },
        ["Ubisoft Connect"] = new[] { "upc", "UbisoftConnect", "UplayWebCore" },
        ["Riot Client"] = new[] { "RiotClientServices", "RiotClientUxRender" },
        ["GOG Galaxy"] = new[] { "GalaxyClient Helper", "GalaxyClientService" },
        ["Discord"] = new[] { "Discord" }
    };

    public static Task<string> ApplyAsync(IEnumerable<string> selected) => Task.Run(() =>
    {
        var changed = new List<string>();
        var wanted = LauncherProcessMatcher.BuildIndex(selected, Processes);
        if (wanted.Count > 0)
        {
            // A single native snapshot replaces repeated full-table searches
            // for every selected launcher. Dispose *all* Process wrappers even
            // when an individual process cannot be inspected or modified.
            ProcessSnapshotCleanup.Inspect(Process.GetProcesses, processes =>
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (!wanted.TryGetValue(name, out List<string>? launchers)) continue;
                        process.PriorityClass = ProcessPriorityClass.BelowNormal;
                        foreach (string launcher in launchers)
                            changed.Add($"{launcher}: {name}");
                    }
                    catch { /* Protected or already exited process. */ }
                }
                return true;
            });
        }
        LogService.Info("Launcher gaming mode: " + string.Join(", ", changed));
        return changed.Count == 0 ? "Neběží žádný vybraný pomocný proces." : "Priorita BelowNormal nastavena:\n" + string.Join("\n", changed.Distinct());
    });

    public static async Task<string> SetStartupAsync(IEnumerable<string> selected, bool enabled)
    {
        var wanted = selected.ToArray();
        var entries = await StartupManagerService.GetAsync();
        var changed = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Enabled == enabled) continue;
            var launcher = wanted.FirstOrDefault(x =>
                Processes.TryGetValue(x, out var names) &&
                LauncherProcessMatcher.StartupMatches(x, names, entry.Name, entry.Command));
            if (launcher == null) continue;
            await StartupManagerService.SetEnabledAsync(entry, enabled); changed.Add(entry.Name);
        }
        return changed.Count == 0 ? "Nebyla nalezena odpovídající položka po spuštění." : $"{(enabled ? "Obnoveno" : "Vypnuto")}:\n" + string.Join("\n", changed.Distinct());
    }
}

public static class StorageMaintenanceService
{
    private static readonly object HealthGate = new();
    private static string? _healthCache;
    private static DateTime _healthCacheAt;
    private static Task<string>? _healthInFlight;

    public static async Task<string> GetHealthAsync()
    {
        Task<string> task;
        lock (HealthGate)
        {
            if (_healthCache != null && DateTime.UtcNow - _healthCacheAt < TimeSpan.FromMinutes(15)) return _healthCache;
            if (_healthInFlight == null)
            {
                var created = LoadHealthCoreAsync();
                _healthInFlight = created;
                task = created;
                _ = ObserveHealthAsync(created);
            }
            else
            {
                task = _healthInFlight;
            }
        }

        var completed = await Task.WhenAny(task, Task.Delay(800)).ConfigureAwait(false);
        if (ReferenceEquals(completed, task)) return await task.ConfigureAwait(false);
        return DriveFallback();
    }


    private static async Task ObserveHealthAsync(Task<string> created)
    {
        try
        {
            string result = await created.ConfigureAwait(false);
            lock (HealthGate)
            {
                _healthCache = result;
                _healthCacheAt = DateTime.UtcNow;
                if (ReferenceEquals(_healthInFlight, created)) _healthInFlight = null;
            }
        }
        catch
        {
            lock (HealthGate)
                if (ReferenceEquals(_healthInFlight, created)) _healthInFlight = null;
        }
    }

    private static Task<string> LoadHealthCoreAsync()
    {
        // wmic.exe is absent on current Windows 11 builds. Query the same
        // Win32_DiskDrive data through System.Management instead of silently
        // degrading every storage page to free-space-only output.
        return Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"\\.\root\CIMV2"),
                    new ObjectQuery("SELECT Model,Status,Size FROM Win32_DiskDrive"),
                    new System.Management.EnumerationOptions
                    {
                        Timeout = TimeSpan.FromSeconds(3),
                        ReturnImmediately = false,
                        Rewindable = false
                    });
                using var results = searcher.Get();
                var blocks = new List<string>(4);
                foreach (ManagementObject disk in results)
                {
                    using (disk)
                    {
                        string model = disk["Model"]?.ToString()?.Trim() ?? "Neznámý";
                        string status = disk["Status"]?.ToString()?.Trim() ?? "Unknown";
                        string size = disk["Size"]?.ToString() ?? "0";
                        blocks.Add($"Model={model}{Environment.NewLine}Status={status}{Environment.NewLine}Size={size}");
                    }
                }
                return blocks.Count == 0
                    ? DriveFallback()
                    : string.Join(Environment.NewLine + Environment.NewLine, blocks);
            }
            catch
            {
                return DriveFallback();
            }
        });
    }

    private static string DriveFallback() => string.Join("\n", DriveInfo.GetDrives().Where(x => x.IsReady)
        .Select(x => $"{x.Name} — {x.DriveFormat} — {x.AvailableFreeSpace / 1073741824d:0.0} GB volných"));

    public sealed record CleanupCategoryScan(string Id, string Name, IReadOnlyList<string> Paths, long Bytes);
    public sealed record CleanupScanResult(IReadOnlyList<CleanupCategoryScan> Categories, long TotalBytes);

    private static (string Id, string Name, string[] Paths)[] CleanupCategories()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            ("temp", "Uživatelský Temp", new[] { Path.GetTempPath() }),
            ("d3d", "Direct3D shader cache", new[] { Path.Combine(local, "D3DSCache") }),
            ("nvidia", "NVIDIA shader cache", new[] { Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache") }),
            ("amd", "AMD shader cache", new[] { Path.Combine(local, "AMD", "DxCache") })
        };
    }

    public static async Task<CleanupScanResult> ScanSafeCleanupAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var definitions = CleanupCategories();
        var tasks = new Task<long>[definitions.Length];
        for (int i = 0; i < definitions.Length; i++)
            tasks[i] = LauncherCacheService.GetSizeAsync(definitions[i].Paths, token);

        await Task.WhenAll(tasks).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var categories = new CleanupCategoryScan[definitions.Length];
        long total = 0;
        for (int i = 0; i < definitions.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            long bytes = await tasks[i].ConfigureAwait(false);
            categories[i] = new CleanupCategoryScan(definitions[i].Id, definitions[i].Name, definitions[i].Paths, bytes);
            total += bytes;
        }
        return new CleanupScanResult(categories, total);
    }

    public static async Task<long> GetSafeCleanupSizeAsync(CancellationToken token = default) =>
        (await ScanSafeCleanupAsync(token).ConfigureAwait(false)).TotalBytes;

    public static Task<LauncherCacheService.CleanupResult> ClearSafeCachesAsync(CancellationToken token = default) =>
        LauncherCacheService.ClearVerifiedAsync(SafeCleanupPaths(), token);

    public static Task<LauncherCacheService.CleanupResult> ClearSafeCachesAsync(IEnumerable<string> categoryIds, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var selected = categoryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = CleanupCategories()
            .Where(x => selected.Contains(x.Id))
            .SelectMany(x => x.Paths)
            .ToArray();
        return LauncherCacheService.ClearVerifiedAsync(paths, token);
    }

    public static Task<long> GetShaderCacheSizeAsync(CancellationToken token = default) => LauncherCacheService.GetSizeAsync(ShaderCachePaths(), token);
    public static Task<LauncherCacheService.CleanupResult> ClearShaderCachesAsync(CancellationToken token = default) => LauncherCacheService.ClearVerifiedAsync(ShaderCachePaths(), token);

    private static IEnumerable<string> SafeCleanupPaths()
    {
        foreach (var category in CleanupCategories())
            foreach (string path in category.Paths)
                yield return path;
    }

    private static IEnumerable<string> ShaderCachePaths()
    {
        foreach (var category in CleanupCategories())
        {
            if (category.Id.Equals("temp", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string path in category.Paths)
                yield return path;
        }
    }
}
