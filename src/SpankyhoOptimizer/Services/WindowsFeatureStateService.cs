using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static class WindowsFeatureStateService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object ClientLock = new();
    private static readonly object ClientScopeLock = new();
    private static ManagementScope? _clientFeatureScope;
    private static readonly Dictionary<string, (bool Enabled, DateTime At)> ClientFeatureCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<bool>> ClientFeatureFlights = new(StringComparer.OrdinalIgnoreCase);
    private static Task<Dictionary<string, bool>>? ClientSnapshotFlight;
    private static Dictionary<string, bool>? ClientSnapshot;
    private static DateTime ClientSnapshotAt;
    private static Dictionary<string, bool>? _cache;
    private static DateTime _cacheAt;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PersistentTtl = TimeSpan.FromHours(6);
    private const string LegacyPersistentKey = "windows-features";
    private static string PersistentKey => $"windows-features-b{Environment.OSVersion.Version.Build}";

    public static async Task<bool> IsDisabledAsync(string feature, CancellationToken token = default)
    {
        if (WorkerExecutionContext.IsWorker) return await IsDisabledLocalAsync(feature, token);
        bool enabled = await GetClientFeatureEnabledAsync(feature, token);
        return !enabled;
    }

    internal static async Task<bool> IsDisabledLocalAsync(string feature, CancellationToken token = default)
    {
        var states = await GetStatesAsync(token);
        return states.TryGetValue(feature, out bool enabled) && !enabled;
    }

    public static Task<bool> TryWarmPersistentClientSnapshotAsync(CancellationToken token = default)
    {
        if (WorkerExecutionContext.IsWorker) return Task.FromResult(false);
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!PersistentHardwareCacheService.TryReadMachineStable<Dictionary<string, bool>>(
                    PersistentKey, PersistentTtl, out var persisted) ||
                persisted == null || persisted.Count == 0)
                return false;

            DateTime now = DateTime.UtcNow;
            lock (ClientLock)
            {
                ClientSnapshot = new Dictionary<string, bool>(persisted, StringComparer.OrdinalIgnoreCase);
                ClientSnapshotAt = now;
                foreach (var pair in ClientSnapshot)
                    ClientFeatureCache[pair.Key] = (pair.Value, now);
            }
            LogService.Info("performance", $"WINDOWS_FEATURE_PERSISTENT_PREWARM count={persisted.Count}");
            return true;
        }, token);
    }

    public static async Task PrewarmClientSnapshotAsync(CancellationToken token = default)
    {
        if (WorkerExecutionContext.IsWorker) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = await GetClientFeatureEnabledAsync("VirtualMachinePlatform", token).ConfigureAwait(false);
        LogService.Info("performance", $"WINDOWS_FEATURE_PREWARM workMs={sw.ElapsedMilliseconds}");
    }

    public static void Invalidate()
    {
        lock (ClientLock)
        {
            ClientFeatureCache.Clear(); ClientFeatureFlights.Clear();
            ClientSnapshot = null; ClientSnapshotAt = default; ClientSnapshotFlight = null;
        }
        _cache = null;
        _cacheAt = default;
        PersistentHardwareCacheService.Remove(PersistentKey);
        PersistentHardwareCacheService.Remove(LegacyPersistentKey);
    }

    private static async Task<bool> GetClientFeatureEnabledAsync(string feature, CancellationToken token)
    {
        if (feature.Equals("Microsoft-Hyper-V-All", StringComparison.OrdinalIgnoreCase))
        {
            string edition = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "")?.ToString() ?? string.Empty;
            if (edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase)) return false;
        }

        lock (ClientLock)
        {
            if (ClientFeatureCache.TryGetValue(feature, out var cached) && DateTime.UtcNow - cached.At < Ttl) return cached.Enabled;
            if (ClientSnapshot != null && DateTime.UtcNow - ClientSnapshotAt < Ttl && ClientSnapshot.TryGetValue(feature, out bool nativeCached))
                return nativeCached;
        }

        if (PersistentHardwareCacheService.TryReadMachineStable<Dictionary<string, bool>>(PersistentKey, PersistentTtl, out var persisted) && persisted != null)
        {
            lock (ClientLock)
            {
                ClientSnapshot = new Dictionary<string, bool>(persisted, StringComparer.OrdinalIgnoreCase);
                ClientSnapshotAt = DateTime.UtcNow;
                foreach (var pair in ClientSnapshot) ClientFeatureCache[pair.Key] = (pair.Value, ClientSnapshotAt);
                if (ClientSnapshot.TryGetValue(feature, out bool persistedEnabled)) return persistedEnabled;
            }
        }

        Task<Dictionary<string, bool>> snapshotWork;
        lock (ClientLock)
        {
            if (ClientSnapshotFlight == null)
            {
                var created = ReadClientSnapshotNativeAsync();
                ClientSnapshotFlight = created;
                snapshotWork = created;
                _ = created.ContinueWith(t =>
                {
                    if (t.IsFaulted) _ = t.Exception;
                    lock (ClientLock)
                        if (ReferenceEquals(ClientSnapshotFlight, created))
                            ClientSnapshotFlight = null;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else
            {
                snapshotWork = ClientSnapshotFlight;
            }
        }

        var snapshot = await snapshotWork.WaitAsync(token).ConfigureAwait(false);
        if (snapshot.Count > 0) PersistentHardwareCacheService.WriteMachineStable(PersistentKey, snapshot);
        lock (ClientLock)
        {
            ClientSnapshot = snapshot; ClientSnapshotAt = DateTime.UtcNow;
            foreach (var pair in snapshot) ClientFeatureCache[pair.Key] = (pair.Value, ClientSnapshotAt);
            if (snapshot.TryGetValue(feature, out bool enabled)) return enabled;
        }

        Task<bool> work;
        lock (ClientLock)
        {
            if (!ClientFeatureFlights.TryGetValue(feature, out work!))
            {
                work = ReadClientFeatureEnabledAsync(feature);
                ClientFeatureFlights[feature] = work;
                var created = work;
                _ = created.ContinueWith(t =>
                {
                    if (t.IsFaulted) _ = t.Exception;
                    lock (ClientLock)
                    {
                        if (ClientFeatureFlights.TryGetValue(feature, out var current) && ReferenceEquals(current, created))
                            ClientFeatureFlights.Remove(feature);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        bool fallbackEnabled = await work.WaitAsync(token).ConfigureAwait(false);
        lock (ClientLock)
            ClientFeatureCache[feature] = (fallbackEnabled, DateTime.UtcNow);
        return fallbackEnabled;
    }

    private static ManagementScope GetClientFeatureScope()
    {
        lock (ClientScopeLock)
        {
            if (_clientFeatureScope == null || !_clientFeatureScope.IsConnected)
            {
                _clientFeatureScope = new ManagementScope(@"\\.\root\cimv2");
                _clientFeatureScope.Connect();
            }
            return _clientFeatureScope;
        }
    }

    private static Task<Dictionary<string, bool>> ReadClientSnapshotNativeAsync() => Task.Run(() =>
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string query = "SELECT Name, InstallState FROM Win32_OptionalFeature WHERE Name='Microsoft-Hyper-V-All' OR Name='VirtualMachinePlatform' OR Name='HypervisorPlatform' OR Name='SMB1Protocol'";
            using var searcher = new ManagementObjectSearcher(GetClientFeatureScope(), new ObjectQuery(query));
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    string name = item["Name"]?.ToString() ?? string.Empty;
                    if (name.Length == 0 || item["InstallState"] == null) continue;
                    map[name] = Convert.ToUInt32(item["InstallState"]) == 1u;
                }
            }

            // Win32_OptionalFeature may omit features that are disabled or unavailable on
            // the current edition. For the known feature set used by tweak detection,
            // an omitted item from a successful query is effectively disabled and should
            // not trigger a 1-2 second DISM fallback on the UI/background detect path.
            string[] known =
            {
                "Microsoft-Hyper-V-All",
                "VirtualMachinePlatform",
                "HypervisorPlatform",
                "SMB1Protocol"
            };
            foreach (string name in known)
                map.TryAdd(name, false);
        }
        catch (Exception ex)
        {
            lock (ClientScopeLock) _clientFeatureScope = null;
            LogService.Error("features", "Win32_OptionalFeature snapshot failed; DISM fallback will be used.", ex);
            map.Clear();
        }
        return map;
    });

    private static async Task<bool> ReadClientFeatureEnabledAsync(string feature)
    {
        var result = await ProcessRunner.RunAsync("dism.exe", $"/Online /Get-FeatureInfo /FeatureName:{feature} /English", TimeSpan.FromSeconds(20), CancellationToken.None).ConfigureAwait(false);
        if (!result.Success) return false;
        string output = result.StdOut ?? string.Empty;
        return output.IndexOf("State : Enabled", StringComparison.OrdinalIgnoreCase) >= 0 ||
               output.IndexOf("State : Enable Pending", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static async Task<Dictionary<string, bool>> GetStatesLocalSnapshotAsync(CancellationToken token = default)
        => new(await GetStatesAsync(token), StringComparer.OrdinalIgnoreCase);

    private static async Task<IReadOnlyDictionary<string, bool>> GetStatesAsync(CancellationToken token)
    {
        if (_cache != null && DateTime.UtcNow - _cacheAt < Ttl) return _cache;
        await Gate.WaitAsync(token);
        try
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < Ttl) return _cache;
            string edition = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "")?.ToString() ?? string.Empty;
            bool homeEdition = edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase);
            string[] features = homeEdition
                ? new[] { "VirtualMachinePlatform", "HypervisorPlatform", "SMB1Protocol" }
                : new[] { "Microsoft-Hyper-V-All", "VirtualMachinePlatform", "HypervisorPlatform", "SMB1Protocol" };

            if (PersistentHardwareCacheService.TryReadMachineStable<Dictionary<string, bool>>(PersistentKey, PersistentTtl, out var persisted) && persisted != null)
            {
                var cached = new Dictionary<string, bool>(persisted, StringComparer.OrdinalIgnoreCase);
                if (homeEdition) cached["Microsoft-Hyper-V-All"] = false;
                _cache = cached; _cacheAt = DateTime.UtcNow;
                return cached;
            }
            var map = await ReadClientSnapshotNativeAsync().WaitAsync(token).ConfigureAwait(false);
            if (homeEdition) map["Microsoft-Hyper-V-All"] = false;

            var missing = features.Where(name => !map.ContainsKey(name)).ToArray();
            if (missing.Length > 0)
            {
                var tasks = missing.Select(async name => (name, result: await ProcessRunner.RunAsync(
                    "dism.exe", $"/Online /Get-FeatureInfo /FeatureName:{name} /English", TimeSpan.FromSeconds(20), token))).ToArray();
                foreach (var item in await Task.WhenAll(tasks).ConfigureAwait(false))
                {
                    if (!item.result.Success) { map[item.name] = false; continue; }
                    string output = item.result.StdOut ?? string.Empty;
                    bool enabled = output.IndexOf("State : Enabled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   output.IndexOf("State : Enable Pending", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool disabled = output.IndexOf("State : Disabled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    output.IndexOf("State : Disable Pending", StringComparison.OrdinalIgnoreCase) >= 0;
                    map[item.name] = enabled || !disabled ? enabled : false;
                }
            }

            if (map.Count > 0) PersistentHardwareCacheService.WriteMachineStable(PersistentKey, map);
            _cache = map; _cacheAt = DateTime.UtcNow;
            return map;
        }
        finally { Gate.Release(); }
    }
}
