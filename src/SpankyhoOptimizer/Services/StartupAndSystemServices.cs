using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private static readonly Dictionary<string, string> PublisherCache = new(StringComparer.OrdinalIgnoreCase);
    private const string DisabledSuffix = "\\SpankyhoDisabled";
    private static readonly string[] RunPaths =
    {
        "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
        "Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce"
    };

    public static Task<IReadOnlyList<StartupEntry>> GetAsync(
        bool forceRefresh = false,
        bool forceTaskSchedulerRefresh = false)
    {
        if (!forceRefresh)
        {
            var snapshot = TryGetFreshSnapshot();
            if (snapshot != null)
                return Task.FromResult(snapshot);
        }

        lock (CacheLock)
        {
            if (!forceRefresh && _cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(2))
                return Task.FromResult(_cache);
            if (_flight != null && !_flight.IsCompleted && _flightGeneration == _cacheGeneration)
                return _flight;

            int generation = _cacheGeneration;
            _flightGeneration = generation;
            _flight = LoadAsync(forceTaskSchedulerRefresh, generation);
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
            StartupEntry[] persistedFinal = final.ToArray();
            var delta = ComputeDelta(persistedFinal);
            lock (CacheLock)
            {
                if (generation == _cacheGeneration)
                    _lastDelta = delta;
                _deltaBaselineCache = persistedFinal;
                _deltaBaselineLoaded = true;
            }

            LogService.Info("startup", $"STARTUP_DELTA added={delta.Added} removed={delta.Removed} changed={delta.Changed}");

            // The primary snapshot is part of GetAsync's contract: callers may clear
            // memory immediately and still expect persistent hydration to work.
            // Avoid rewriting identical JSON on ordinary refreshes.
            bool primaryUnchanged;
            lock (CacheLock)
                primaryUnchanged = _primaryPersistentSnapshotCache is { Length: > 0 } previousPrimary &&
                                   previousPrimary.SequenceEqual(persistedFinal);

            if (!primaryUnchanged)
            {
                WritePersistentSnapshot(persistedFinal);
                lock (CacheLock)
                    _primaryPersistentSnapshotCache = persistedFinal;
            }

            // Delta baseline is only needed by a later inventory pass, so keep this
            // secondary filesystem write off the current critical path.
            _ = ThreadPool.QueueUserWorkItem(_ =>
            {
                bool stillCurrent;
                lock (CacheLock)
                    stillCurrent = generation == _cacheGeneration;
                if (!stillCurrent) return;

                try
                {
                    UserScopedCacheService.Write(DeltaBaselineKey, persistedFinal);
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

    public static void InvalidateCache(bool invalidateTaskScheduler = false)
    {
        lock (CacheLock)
        {
            _cache = null;
            _cacheAt = default;
            _cacheNeedsBackgroundRefresh = false;
            _cacheGeneration++;
        }
        UserScopedCacheService.Remove(PersistentSnapshotKey);
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
        var oldMap = previous.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var newMap = current.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        int added = newMap.Keys.Count(x => !oldMap.ContainsKey(x));
        int removed = oldMap.Keys.Count(x => !newMap.ContainsKey(x));
        int changed = newMap.Count(pair =>
            oldMap.TryGetValue(pair.Key, out var old) &&
            (!string.Equals(old.Command, pair.Value.Command, StringComparison.Ordinal) ||
             old.Enabled != pair.Value.Enabled ||
             !string.Equals(old.FilePath, pair.Value.FilePath, StringComparison.OrdinalIgnoreCase)));
        return new StartupInventoryDelta(added, removed, changed);
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
        return "STARTUP_DELTA_SMOKE_OK added=1 removed=1 changed=1";
    }

    private static void WritePersistentSnapshot(IReadOnlyList<StartupEntry> items) =>
        UserScopedCacheService.Write(PersistentSnapshotKey, items.ToArray());

    private static void ReadKey(List<StartupEntry> result, RegistryHive hive, RegistryView view, string path, bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(path);
        if (key == null) return;
        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
            var executable = ExecutableOf(value);
            result.Add(new StartupEntry($"{hive}|{view}|{path}|{name}", name, value, hive, path.Replace(DisabledSuffix, ""), enabled,
                PublisherOfExecutable(executable), executable, "Registry", null, true, view));
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

    private static string? ExecutableOf(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        string candidate = expanded.StartsWith('"') ? expanded.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "" : Regex.Match(expanded, @"^[^ ]+\.exe", RegexOptions.IgnoreCase).Value;
        candidate = candidate.Trim('"');
        return File.Exists(candidate) ? candidate : null;
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

    public static async Task SetEnabledAsync(StartupEntry item, bool enabled)
    {
        if (item.Enabled == enabled) return;
        if (!item.CanToggle) throw new InvalidOperationException("Tato startup položka je chráněná.");

        if (item.SourceKind.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase))
        {
            string path = item.SourceId ?? throw new InvalidOperationException("Task path chybí.");
            await new TaskSchedulerService().SetEnabledAsync(path, enabled).ConfigureAwait(false);
            InvalidateCache();
            LogService.Info($"Startup task changed and verified: {path}, enabled={enabled}");
            return;
        }

        if (item.SourceKind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() =>
            {
                string source = item.SourceId ?? throw new InvalidOperationException("Startup file path chybí.");
                string disabledDir = Path.Combine(item.RunPath, "SpankyhoDisabled");
                Directory.CreateDirectory(disabledDir);
                string target = enabled
                    ? Path.Combine(item.RunPath, Path.GetFileName(source))
                    : Path.Combine(disabledDir, Path.GetFileName(source));
                if (!File.Exists(source)) throw new InvalidOperationException("Startup soubor už neexistuje.");
                if (File.Exists(target)) throw new IOException("Cílový startup soubor už existuje.");
                File.Move(source, target);
                if (!File.Exists(target) || File.Exists(source))
                {
                    try { if (File.Exists(target) && !File.Exists(source)) File.Move(target, source); } catch { }
                    throw new InvalidOperationException("Read-back nepotvrdil přesun startup souboru.");
                }
            }).ConfigureAwait(false);
            InvalidateCache();
            LogService.Info($"Startup folder item changed and verified: {item.Name}, enabled={enabled}");
            return;
        }

        await Task.Run(() =>
        {
            using var root = RegistryKey.OpenBaseKey(item.Hive, item.View);
            var sourcePath = item.RunPath + (enabled ? DisabledSuffix : "");
            var targetPath = item.RunPath + (enabled ? "" : DisabledSuffix);
            using var source = root.OpenSubKey(sourcePath, writable: true) ?? throw new InvalidOperationException("Položka po spuštění už neexistuje.");
            var value = source.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? throw new InvalidOperationException("Hodnotu nelze přečíst.");
            var kind = source.GetValueKind(item.Name);
            using var target = root.CreateSubKey(targetPath, writable: true);
            target.SetValue(item.Name, value, kind);
            try
            {
                source.DeleteValue(item.Name, false);
                var targetValue = target.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var sourceValue = source.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (targetValue == null || sourceValue != null)
                    throw new InvalidOperationException("Read-back nepotvrdil přesun položky po spuštění.");
            }
            catch
            {
                try { source.SetValue(item.Name, value, kind); } catch { }
                try { target.DeleteValue(item.Name, false); } catch { }
                throw;
            }
        }).ConfigureAwait(false);
        InvalidateCache();
        LogService.Info($"Startup registry state changed and verified: {item.Name}, enabled={enabled}, view={item.View}");
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

        var result = await ProcessRunner.RunAsync("pnputil.exe", $"/remove-device \"{instanceId}\"", TimeSpan.FromSeconds(30));
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
            if (parent != null && Directory.Exists(parent))
                foreach (var dir in Directory.EnumerateDirectories(parent, pattern)) yield return dir;
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

    private static long DirectorySize(string path, CancellationToken token = default)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                try { total += new FileInfo(file).Length; } catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
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
            string[] files;
            string[] dirs;
            try { files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray(); }
            catch (OperationCanceledException) { throw; }
            catch { failed++; continue; }

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                try { File.Delete(file); } catch { failed++; }
            }

            try { dirs = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length).ToArray(); }
            catch (OperationCanceledException) { throw; }
            catch { failed++; dirs = Array.Empty<string>(); }

            foreach (var dir in dirs)
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
        foreach (var launcher in selected.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Processes.TryGetValue(launcher, out var names)) continue;
            foreach (var name in names)
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try { process.PriorityClass = ProcessPriorityClass.BelowNormal; changed.Add($"{launcher}: {process.ProcessName}"); }
                    catch { }
                    finally { process.Dispose(); }
                }
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
            var haystack = entry.Name + " " + entry.Command;
            var launcher = wanted.FirstOrDefault(x => Processes.TryGetValue(x, out var names) && (haystack.Contains(x, StringComparison.OrdinalIgnoreCase) || names.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase))));
            if (launcher == null) continue;
            await StartupManagerService.SetEnabledAsync(entry, enabled); changed.Add(entry.Name);
        }
        return changed.Count == 0 ? "Nebyla nalezena odpovídající položka po spuštění." : $"{(enabled ? "Obnoveno" : "Vypnuto")}:\n" + string.Join("\n", changed.Distinct());
    }
}

public sealed record FirefoxAdvancedState(bool WebRender, bool DiskCacheOff, bool SpeculativeOff, bool DohMode2, int ProfilesTotal, int ProfilesManaged);

public static class FirefoxAdvancedService
{
    private const string Start = "// SPANKYHO OPTIMIZER START";
    private const string End = "// SPANKYHO OPTIMIZER END";

    public static IReadOnlyList<string> Profiles()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles");
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static FirefoxAdvancedState GetState()
    {
        var profiles = Profiles();
        if (profiles.Count == 0) return new(false, false, false, false, 0, 0);
        int managed = 0; bool web = true, cache = true, spec = true, doh = true;
        foreach (var profile in profiles)
        {
            string file = Path.Combine(profile, "user.js");
            string text = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
            if (!HasManagedBlock(text)) { web = cache = spec = doh = false; continue; }
            managed++;
            web &= Has(text, "user_pref(\"gfx.webrender.all\", true);");
            cache &= Has(text, "user_pref(\"browser.cache.disk.enable\", false);");
            spec &= Has(text, "user_pref(\"network.prefetch-next\", false);") && Has(text, "user_pref(\"network.dns.disablePrefetch\", true);") && Has(text, "user_pref(\"network.predictor.enabled\", false);");
            doh &= Has(text, "user_pref(\"network.trr.mode\", 2);");
        }
        return new(web && managed == profiles.Count, cache && managed == profiles.Count, spec && managed == profiles.Count, doh && managed == profiles.Count, profiles.Count, managed);
    }

    public static Task<int> ApplyAsync(bool webRender, bool diskCacheOff, bool speculativeOff, bool dohMode2) => Task.Run(() =>
    {
        var profiles = Profiles();
        var backup = profiles.ToDictionary(x => Path.Combine(x, "user.js"), x => File.Exists(Path.Combine(x, "user.js")) ? File.ReadAllText(Path.Combine(x, "user.js")) : null);
        try
        {
            foreach (var profile in profiles)
            {
                var file = Path.Combine(profile, "user.js");
                var text = backup[file] ?? string.Empty;
                var lines = new List<string> { Start };
                if (webRender) lines.Add("user_pref(\"gfx.webrender.all\", true);");
                lines.Add(diskCacheOff
                    ? "user_pref(\"browser.cache.disk.enable\", false);"
                    : "user_pref(\"browser.cache.disk.enable\", true);");
                if (speculativeOff)
                {
                    lines.Add("user_pref(\"network.prefetch-next\", false);");
                    lines.Add("user_pref(\"network.dns.disablePrefetch\", true);");
                    lines.Add("user_pref(\"network.dns.disablePrefetchFromHTTPS\", true);");
                    lines.Add("user_pref(\"network.http.speculative-parallel-limit\", 0);");
                    lines.Add("user_pref(\"browser.urlbar.speculativeConnect.enabled\", false);");
                    lines.Add("user_pref(\"network.predictor.enabled\", false);");
                }
                else
                {
                    // Performance-safe defaults. Writing these explicitly also overrides obsolete settings.
                    // prefs that may still exist earlier in user.js.
                    lines.Add("user_pref(\"network.prefetch-next\", true);");
                    lines.Add("user_pref(\"network.dns.disablePrefetch\", false);");
                    lines.Add("user_pref(\"network.dns.disablePrefetchFromHTTPS\", false);");
                    lines.Add("user_pref(\"network.http.speculative-parallel-limit\", 6);");
                    lines.Add("user_pref(\"browser.urlbar.speculativeConnect.enabled\", true);");
                    lines.Add("user_pref(\"network.predictor.enabled\", true);");
                }
                if (dohMode2) lines.Add("user_pref(\"network.trr.mode\", 2);");
                lines.Add(End);
                AtomicFileService.WriteText(file, RemoveBlock(text).TrimEnd() + Environment.NewLine + string.Join(Environment.NewLine, lines) + Environment.NewLine);
                Verify(file, webRender, diskCacheOff, speculativeOff, dohMode2);
            }
            LogService.Info($"Firefox advanced profile applied and verified on {profiles.Count} profiles.");
            return profiles.Count;
        }
        catch
        {
            RestoreBackup(backup);
            throw;
        }
    });

    public static Task<int> RestoreAsync() => Task.Run(() =>
    {
        var profiles = Profiles();
        var backup = profiles.ToDictionary(x => Path.Combine(x, "user.js"), x => File.Exists(Path.Combine(x, "user.js")) ? File.ReadAllText(Path.Combine(x, "user.js")) : null);
        try
        {
            foreach (var profile in profiles)
            {
                var file = Path.Combine(profile, "user.js");
                if (!File.Exists(file)) continue;
                AtomicFileService.WriteText(file, RemoveBlock(File.ReadAllText(file)).TrimEnd() + Environment.NewLine);
                string verify = File.ReadAllText(file);
                if (HasManagedBlock(verify)) throw new InvalidOperationException($"Firefox user.js restore read-back selhal: {profile}");
            }
            LogService.Info($"Firefox advanced block restored and verified on {profiles.Count} profiles.");
            return profiles.Count;
        }
        catch
        {
            RestoreBackup(backup);
            throw;
        }
    });

    private static bool HasManagedBlock(string text) => text.Contains(Start, StringComparison.Ordinal) && text.Contains(End, StringComparison.Ordinal);
    private static bool Has(string text, string value) => text.Contains(value, StringComparison.Ordinal);

    private static void Verify(string file, bool webRender, bool diskCacheOff, bool speculativeOff, bool dohMode2)
    {
        string text = File.ReadAllText(file);
        if (!HasManagedBlock(text)) throw new InvalidOperationException($"Firefox user.js read-back nenalezl managed blok: {file}");
        if (Has(text, "user_pref(\"gfx.webrender.all\", true);") != webRender) throw new InvalidOperationException($"Firefox WebRender read-back nesouhlasí: {file}");
        if (Has(text, "user_pref(\"browser.cache.disk.enable\", false);") != diskCacheOff) throw new InvalidOperationException($"Firefox disk cache read-back nesouhlasí: {file}");
        bool spec = Has(text, "user_pref(\"network.prefetch-next\", false);") &&
                    Has(text, "user_pref(\"network.dns.disablePrefetch\", true);") &&
                    Has(text, "user_pref(\"network.http.speculative-parallel-limit\", 0);") &&
                    Has(text, "user_pref(\"browser.urlbar.speculativeConnect.enabled\", false);");
        if (spec != speculativeOff) throw new InvalidOperationException($"Firefox speculative networking read-back nesouhlasí: {file}");
        if (!speculativeOff)
        {
            bool performanceDefaults =
                Has(text, "user_pref(\"network.prefetch-next\", true);") &&
                Has(text, "user_pref(\"network.dns.disablePrefetch\", false);") &&
                Has(text, "user_pref(\"network.dns.disablePrefetchFromHTTPS\", false);") &&
                Has(text, "user_pref(\"network.http.speculative-parallel-limit\", 6);") &&
                Has(text, "user_pref(\"browser.urlbar.speculativeConnect.enabled\", true);");
            if (!performanceDefaults)
                throw new InvalidOperationException($"Firefox performance networking read-back nesouhlasí: {file}");
        }
        if (Has(text, "user_pref(\"network.trr.mode\", 2);") != dohMode2) throw new InvalidOperationException($"Firefox DoH read-back nesouhlasí: {file}");
    }

    private static void RestoreBackup(Dictionary<string, string?> backup)
    {
        foreach (var pair in backup)
        {
            if (pair.Value == null) { if (File.Exists(pair.Key)) File.Delete(pair.Key); }
            else AtomicFileService.WriteText(pair.Key, pair.Value);
        }
    }

    private static string RemoveBlock(string text) => Regex.Replace(text, Regex.Escape(Start) + ".*?" + Regex.Escape(End) + "\\s*", "", RegexOptions.Singleline);
}

public static class BrowserCacheService
{
    public static IReadOnlyList<string> Paths(string browser)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string? userData = browser switch
        {
            "Chrome" => Path.Combine(local, "Google", "Chrome", "User Data"),
            "Edge" => Path.Combine(local, "Microsoft", "Edge", "User Data"),
            "Brave" => Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
            "Vivaldi" => Path.Combine(local, "Vivaldi", "User Data"),
            _ => null
        };
        var paths = new List<string>();
        if (userData != null)
        {
            paths.Add(Path.Combine(userData, "Default", "Cache")); paths.Add(Path.Combine(userData, "ShaderCache"));
            if (Directory.Exists(userData)) foreach (var profile in Directory.EnumerateDirectories(userData, "Profile *")) paths.Add(Path.Combine(profile, "Cache"));
        }
        if (browser == "Firefox") foreach (var profile in FirefoxAdvancedService.Profiles()) paths.Add(Path.Combine(profile, "cache2"));
        if (browser == "Opera") { paths.Add(Path.Combine(roaming, "Opera Software", "Opera Stable", "Cache")); paths.Add(Path.Combine(local, "Opera Software", "Opera Stable", "Cache")); }
        if (browser == "Opera GX") { paths.Add(Path.Combine(roaming, "Opera Software", "Opera GX Stable", "Cache")); paths.Add(Path.Combine(local, "Opera Software", "Opera GX Stable", "Cache")); }
        return paths;
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

    private static async Task<string> LoadHealthCoreAsync()
    {
        var r = await ProcessRunner.RunAsync("wmic.exe", "diskdrive get Model,Status,Size /format:list", TimeSpan.FromSeconds(8), CancellationToken.None).ConfigureAwait(false);
        return !r.Success || string.IsNullOrWhiteSpace(r.StdOut) ? DriveFallback() : Regex.Replace(r.StdOut.Trim(), "(\r?\n){2,}", "\n");
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
