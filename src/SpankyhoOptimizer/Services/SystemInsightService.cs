using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record NetworkAdapterInsight(
    string Name, string Description, double SpeedMbps, int Mtu,
    bool RssEnabled, int RssQueues, string DriverProvider,
    string DriverVersion, string AdvancedSummary, string Recommendation);

public sealed record StorageDeviceInsight(
    string Name, string MediaType, string BusType, string Health,
    string Operational, double SizeGb, long WriteCacheBytes,
    string DriverProvider, string DriverVersion, string Firmware,
    double? TemperatureC, bool TrimEnabled);

public sealed record StartupInsight(
    string Id, string Impact, string SignatureStatus, string Signer,
    bool Protected, string Recommendation, string Reason);

public sealed record StartupInsightCachePayload(
    string Fingerprint, Dictionary<string, StartupInsight> Items);

public static class SystemInsightService
{
    static SystemInsightService()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => InvalidateNetworkFromEvent();
        NetworkChange.NetworkAvailabilityChanged += (_, _) => InvalidateNetworkFromEvent();
        PersistentHardwareCacheService.Remove("startup-insights-v1");
    }

    private static IReadOnlyDictionary<string, StartupInsight>? _startupInsightCache;
    private static string _startupInsightFingerprint = string.Empty;
    private static DateTime _startupInsightCacheAt;
    private const string StartupInsightCacheKey = "startup-insights-v1";
    private static readonly object NetworkCacheLock = new();
    private static readonly object StorageCacheLock = new();
    private static readonly SemaphoreSlim NetworkLoadGate = new(1, 1);
    private static readonly SemaphoreSlim StorageLoadGate = new(1, 1);
    private static readonly SemaphoreSlim StartupInsightLoadGate = new(1, 1);
    // Exact inventory identity allows WinUI to render already verified insights
    // at first paint without waiting on deferred low-priority UI batches.
    // The background verifier still recomputes the fingerprint so file changes
    // and expiry are not hidden by this presentation-only fast path.
    private static readonly object StartupInsightUiSnapshotGate = new();
    private static IReadOnlyList<StartupEntry>? _startupInsightUiInventory;
    private static IReadOnlyDictionary<string, StartupInsight>? _startupInsightUiMap;
    private static DateTime _startupInsightUiAtUtc;

    internal static IReadOnlyDictionary<string, StartupInsight>? TryGetStartupInsightsForExactSnapshot(
        IReadOnlyList<StartupEntry> inventory)
    {
        lock (StartupInsightUiSnapshotGate)
            return ReferenceEquals(_startupInsightUiInventory, inventory) &&
                   _startupInsightUiMap != null &&
                   DateTime.UtcNow - _startupInsightUiAtUtc is var age &&
                   age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(2)
                ? _startupInsightUiMap : null;
    }

    private static void RememberStartupInsightUiSnapshot(
        IReadOnlyList<StartupEntry> inventory,
        IReadOnlyDictionary<string, StartupInsight> insights)
    {
        lock (StartupInsightUiSnapshotGate)
        {
            _startupInsightUiInventory = inventory;
            _startupInsightUiMap = insights;
            _startupInsightUiAtUtc = DateTime.UtcNow;
        }
    }
    // Serialized by StartupInsightLoadGate. Signer metadata is independent
    // of the row's Enabled/Command and reusable after unrelated row changes.
    private readonly record struct StartupSignatureEntry(
        long FileStamp, DateTime AtUtc, string Status, string Signer);
    private static readonly Dictionary<string, StartupSignatureEntry> StartupSignatureCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Func<string, (string Status, string Signer)> StartupSignatureReader =
        ReadStartupSignatureMetadata;
    private const int StartupSignatureCacheLimit = 256;
    private static readonly TimeSpan StartupSignatureCacheTtl = TimeSpan.FromMinutes(30);
    private static NetworkAdapterInsight? _networkCache;
    private static DateTime _networkCacheAt;
    private static IReadOnlyList<StorageDeviceInsight>? _storageCache;
    private static DateTime _storageCacheAt;
    private static long _lastNetworkEventTick;

    private static void InvalidateNetworkFromEvent()
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Exchange(ref _lastNetworkEventTick, now);
        if (previous != 0 && now - previous < 500) return;
        InvalidateNetwork();
    }

    public static void InvalidateNetwork(bool dropPersistent = true, bool invalidateAnalyzer = true)
    {
        lock (NetworkCacheLock) { _networkCache = null; _networkCacheAt = default; }
        NativeSystemProbeService.InvalidateNetworkCache();
        NetworkDiagnosticsService.Invalidate();
        if (invalidateAnalyzer) AdvancedCenterService.InvalidateNetworkAnalyzer();
        if (dropPersistent) PersistentHardwareCacheService.Remove("network-active");
    }

    public static void InvalidateStorage(
        bool dropPersistent = true,
        bool preserveStableDriverMetadata = false,
        bool invalidateTemperatures = true,
        bool invalidateTrim = false)
    {
        lock (StorageCacheLock) { _storageCache = null; _storageCacheAt = default; }
        NativeSystemProbeService.InvalidateStorageCache(preserveStableDriverMetadata, invalidateTemperatures, invalidateTrim);
        if (!dropPersistent) return;
        PersistentHardwareCacheService.Remove("storage-devices");
        PersistentHardwareCacheService.Remove("storage-devices-v2");
    }

    public static async Task<NetworkAdapterInsight?> GetActiveNetworkAsync(CancellationToken token = default, bool forceRefresh = false)
    {
        if (forceRefresh) InvalidateNetwork();
        lock (NetworkCacheLock)
            if (_networkCache != null && DateTime.UtcNow - _networkCacheAt < TimeSpan.FromMinutes(2)) return _networkCache;

        await NetworkLoadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (NetworkCacheLock)
                if (!forceRefresh && _networkCache != null && DateTime.UtcNow - _networkCacheAt < TimeSpan.FromMinutes(2)) return _networkCache;

            token.ThrowIfCancellationRequested();
            if (!forceRefresh && PersistentHardwareCacheService.TryRead<NetworkAdapterInsight>("network-active", TimeSpan.FromHours(6), out var persisted) && persisted != null)
            {
                lock (NetworkCacheLock) { _networkCache = persisted; _networkCacheAt = DateTime.UtcNow; }
                return persisted;
            }
            var x = await NativeSystemProbeService.GetNetworkAsync(token).ConfigureAwait(false);
            if (x == null) return null;
            string recommendation = x.RssEnabled
                ? "RSS je aktivní. Recommended ponechá Interrupt Moderation, Flow Control i Packet Coalescing beze změny."
                : "RSS není aktivní; Recommended ho může bezpečně zapnout, pokud ho adaptér podporuje.";
            var result = new NetworkAdapterInsight(x.Name, x.Description, x.SpeedMbps, x.Mtu, x.RssEnabled,
                x.RssQueues, x.DriverProvider, x.DriverVersion, x.AdvancedSummary, recommendation);
            lock (NetworkCacheLock) { _networkCache = result; _networkCacheAt = DateTime.UtcNow; }
            PersistentHardwareCacheService.Write("network-active", result);
            return result;
        }
        finally { NetworkLoadGate.Release(); }
    }

    public static async Task<IReadOnlyList<StorageDeviceInsight>> GetStorageAsync(CancellationToken token = default, bool forceRefresh = false)
    {
        if (forceRefresh) InvalidateStorage(preserveStableDriverMetadata: true);
        lock (StorageCacheLock)
            if (_storageCache != null && DateTime.UtcNow - _storageCacheAt < TimeSpan.FromMinutes(2)) return _storageCache;

        await StorageLoadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (StorageCacheLock)
                if (!forceRefresh && _storageCache != null && DateTime.UtcNow - _storageCacheAt < TimeSpan.FromMinutes(2)) return _storageCache;

            return await Task.Run<IReadOnlyList<StorageDeviceInsight>>(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!forceRefresh && PersistentHardwareCacheService.TryRead<StorageDeviceInsight[]>("storage-devices-v2", TimeSpan.FromMinutes(15), out var persisted) && persisted != null)
                {
                    lock (StorageCacheLock) { _storageCache = persisted; _storageCacheAt = DateTime.UtcNow; }
                    return persisted;
                }
                var native = NativeSystemProbeService.ReadStorage();
                var result = new StorageDeviceInsight[native.Count];
                for (int i = 0; i < native.Count; i++)
                {
                    var x = native[i];
                    result[i] = new StorageDeviceInsight(x.Name, x.MediaType, x.BusType, x.Health, x.Operational,
                        x.SizeGb, x.WriteCacheBytes, x.DriverProvider, x.DriverVersion, x.Firmware, x.TemperatureC, x.TrimEnabled);
                }
                lock (StorageCacheLock) { _storageCache = result; _storageCacheAt = DateTime.UtcNow; }
                PersistentHardwareCacheService.Write("storage-devices-v2", result);
                return result;
            }, token).ConfigureAwait(false);
        }
        finally { StorageLoadGate.Release(); }
    }

    public static async Task<IReadOnlyDictionary<string, StartupInsight>> GetStartupInsightsAsync(
        IReadOnlyList<StartupEntry> items, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string fingerprint = ComputeStartupInsightFingerprint(items, out var fileStamps, token);
        if (_startupInsightCache != null && fingerprint == _startupInsightFingerprint && DateTime.UtcNow - _startupInsightCacheAt < TimeSpan.FromMinutes(2))
        {
            RememberStartupInsightUiSnapshot(items, _startupInsightCache);
            return _startupInsightCache;
        }

        await StartupInsightLoadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_startupInsightCache != null && fingerprint == _startupInsightFingerprint && DateTime.UtcNow - _startupInsightCacheAt < TimeSpan.FromMinutes(2))
            {
                RememberStartupInsightUiSnapshot(items, _startupInsightCache);
                return _startupInsightCache;
            }

            var persisted = TryReadStartupInsightSnapshot();
            if (persisted != null && string.Equals(persisted.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                _startupInsightCache = persisted.Items;
                _startupInsightFingerprint = fingerprint;
                _startupInsightCacheAt = DateTime.UtcNow;
                RememberStartupInsightUiSnapshot(items, persisted.Items);
                return persisted.Items;
            }

            // Reuse timestamp probes from the fingerprint: no second File.Exists
            // per startup path, no LINQ distinct arrays, no duplicate cert reads.
            var signature = new Dictionary<string, (string Status, string Signer)>(
                fileStamps.Count, StringComparer.OrdinalIgnoreCase);
            DateTime nowUtc = DateTime.UtcNow;
            foreach (var pair in fileStamps)
            {
                token.ThrowIfCancellationRequested();
                if (pair.Value == 0) continue; // missing target, as before
                signature[pair.Key] = GetCachedStartupSignature(pair.Key, pair.Value,
                    StartupSignatureCache, StartupSignatureReader, nowUtc);
            }
            var map = new Dictionary<string, StartupInsight>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                string hay = (item.Name + " " + item.Command + " " + item.Publisher).ToLowerInvariant();
                bool protectedItem = hay.Contains("securityhealth") || hay.Contains("windows defender") ||
                    hay.Contains("audio service") || hay.Contains("rtkaud") || hay.Contains("synaptics") || hay.Contains("touchpad");
                string impact = EstimateImpact(hay, item.Enabled);
                string status = "Unknown", signer = string.Empty;
                if (item.FilePath != null && signature.TryGetValue(item.FilePath, out var sig)) { status = sig.Status; signer = sig.Signer; }
                string recommendation = protectedItem ? "Ponechat" : impact == "High" ? "Zakázat" : impact == "Medium" ? "Volitelné" : "Ponechat";
                string reason = protectedItem ? "Systémová/driver položka — Optimizer ji nedoporučuje vypínat."
                    : impact == "High" ? "Vyšší dopad na přihlášení; pokud aplikaci nepotřebuješ hned po startu, je vhodný kandidát k vypnutí."
                    : impact == "Medium" ? "Střední dopad; ponech podle toho, zda aplikaci používáš po každém přihlášení." : "Nízký dopad; není důvod ji vypínat jen kvůli výkonu.";
                map[item.Id] = new StartupInsight(item.Id, impact, status, signer, protectedItem, recommendation, reason);
            }
            _startupInsightCache = map;
            _startupInsightFingerprint = fingerprint;
            _startupInsightCacheAt = DateTime.UtcNow;
            RememberStartupInsightUiSnapshot(items, map);
            WriteStartupInsightSnapshot(new StartupInsightCachePayload(fingerprint, map));
            return map;
        }
        finally { StartupInsightLoadGate.Release(); }
    }

    // This fingerprint is persisted on disk. Keep the EXACT serialized form of
    // the previous LINQ/string.Join implementation so existing cache files
    // remain valid after an update. A single pass and one builder replace the
    // iterator chain and an intermediate string for every startup entry.
    private static string ComputeStartupInsightFingerprint(IReadOnlyList<StartupEntry> items) =>
        ComputeStartupInsightFingerprint(items, out _);

    private static string ComputeStartupInsightFingerprint(
        IReadOnlyList<StartupEntry> items, out Dictionary<string, long> fileStamps,
        CancellationToken token = default)
    {
        fileStamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // Typical entries average >128 characters; the old 128*n hint missed
        // a 96-row/12,449-char snapshot by only 161 chars and forced a second
        // large backing char[] allocation in every startup fingerprint pass.
        var builder = new StringBuilder(checked(items.Count * 160));
        for (int i = 0; i < items.Count; i++)
        {
            if ((i & 31) == 0) token.ThrowIfCancellationRequested();
            StartupEntry item = items[i];
            long stamp = 0;
            string? path = item.FilePath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!fileStamps.TryGetValue(path, out stamp))
                {
                    try { if (File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path).Ticks; }
                    catch { stamp = 0; }
                    fileStamps.Add(path, stamp);
                }
            }
            if (i != 0) builder.Append('|');
            builder.Append(item.Id).Append(':').Append(item.Enabled).Append(':')
                .Append(item.Command).Append(':').Append(stamp);
        }
        return builder.ToString();
    }

    private static (string Status, string Signer) GetCachedStartupSignature(
        string path, long stamp,
        Dictionary<string, StartupSignatureEntry> cache,
        Func<string, (string Status, string Signer)> reader,
        DateTime nowUtc)
    {
        if (cache.TryGetValue(path, out var cached) &&
            cached.FileStamp == stamp && nowUtc >= cached.AtUtc &&
            nowUtc - cached.AtUtc < StartupSignatureCacheTtl)
            return (cached.Status, cached.Signer);

        (string status, string signer) = reader(path);
        // Transient I/O/access errors must be retried. Only definitive
        // Signed/Unsigned observations are safe to reuse.
        if (status is not ("Signed" or "Unsigned"))
        {
            cache.Remove(path);
            return (status, signer);
        }
        if (cache.Count >= StartupSignatureCacheLimit && !cache.ContainsKey(path))
            cache.Clear();
        cache[path] = new StartupSignatureEntry(stamp, nowUtc, status, signer);
        return (status, signer);
    }

    internal static string RunStartupSignatureCacheSmoke()
    {
        // Synthetic signature reader: no real certificate I/O, no user changes.
        var cache = new Dictionary<string, StartupSignatureEntry>(StringComparer.OrdinalIgnoreCase);
        DateTime now = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        int reads = 0;
        (string Status, string Signer) Read(string path)
        {
            reads++;
            return ("Signed", "Fixture " + reads.ToString());
        }
        var first = GetCachedStartupSignature(@"C:\Apps\Notion.exe", 100, cache, Read, now);
        var hit = GetCachedStartupSignature(@"c:\apps\NOTION.EXE", 100, cache, Read, now.AddMinutes(2));
        if (reads != 1 || first != hit || first.Status != "Signed")
            throw new InvalidOperationException("Unchanged startup signature re-read.");
        var changed = GetCachedStartupSignature(@"C:\Apps\Notion.exe", 101, cache, Read, now.AddMinutes(3));
        if (reads != 2 || changed == first)
            throw new InvalidOperationException("Modified executable reused stale signer.");
        _ = GetCachedStartupSignature(@"C:\Apps\Notion.exe", 101, cache, Read, now.AddMinutes(34));
        if (reads != 3)
            throw new InvalidOperationException("Expired signer cache not refreshed.");
        Func<string, (string Status, string Signer)> sharedReader = Read;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        const int iterations = 32768;
        for (int i = 0; i < iterations; i++)
            _ = GetCachedStartupSignature(@"C:\Apps\Notion.exe", 101, cache, sharedReader, now.AddMinutes(34));
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (reads != 3 || allocated != 0)
            throw new InvalidOperationException(
                $"Startup signature hot cache repeated reads={reads} or allocated={allocated} bytes.");
        int transientProbes = 0;
        (string Status, string Signer) Recover(string _)
        {
            transientProbes++;
            return transientProbes == 1 ? ("Unknown", string.Empty) : ("Signed", "Recovered");
        }
        var transientFirst = GetCachedStartupSignature(@"C:\Apps\Locked.exe", 12, cache, Recover, now);
        var transientRetry = GetCachedStartupSignature(@"C:\Apps\Locked.exe", 12, cache, Recover, now);
        if (transientFirst.Status != "Unknown" || transientRetry.Status != "Signed" ||
            transientProbes != 2)
            throw new InvalidOperationException("Transient startup signer error was incorrectly cached.");
        for (int i = 0; i <= StartupSignatureCacheLimit; i++)
            _ = GetCachedStartupSignature($"C:\\Apps\\Fixture-{i}.exe", i + 1, cache, Read, now);
        if (cache.Count > StartupSignatureCacheLimit)
            throw new InvalidOperationException("Startup signature cache grew past the bound.");
        return $"STARTUP_SIGNATURE_CACHE_SMOKE_OK hotReads=0 hotCalls={iterations} " +
            $"hotMs={elapsedMs:F3} allocatedB={allocated} " +
            $"sameFileCaseInsensitive=true changedStampInvalidated=true ttlInvalidated=true " +
            $"boundedCount={cache.Count} max={StartupSignatureCacheLimit} transientRetry=true writes=0";
    }
    internal static string RunStartupInsightPreparationBenchmark()
    {
        // Read-only synthetic inputs: the comparison reproduces the exact old
        // fingerprint contract and its impact classification, then benchmarks
        // both implementations on an identical 96-entry startup inventory.
        const int rowCount = 96, fingerprintIterations = 144, impactIterations = 1800;
        string existing = typeof(SystemInsightService).Assembly.Location;
        string missing = Path.Combine(Path.GetTempPath(),
            "SpankyStartupBenchMissing" + Guid.NewGuid().ToString("N"));
        var rows = new StartupEntry[rowCount];
        string[] haystacks = new string[rowCount];
        for (int i = 0; i < rows.Length; i++)
        {
            string? path = i % 4 == 0 ? existing :
                i % 4 == 1 ? Path.Combine(missing, $"notion-{i % 12}.exe") :
                i % 4 == 2 ? null : Path.Combine(missing, $"spotify-{i % 8}.exe");
            string name = i % 3 == 0 ? "Discord launcher" : i % 3 == 1 ? "Spotify music" : "Generic utility";
            bool enabled = i % 7 != 0;
            rows[i] = new StartupEntry($"item-{i}", name, $"\"{path}\" --start {i}",
                Microsoft.Win32.RegistryHive.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                enabled, "Publisher", path);
            haystacks[i] = (name + " " + rows[i].Command + " " + rows[i].Publisher).ToLowerInvariant();
        }

        static string LegacyFingerprint(IReadOnlyList<StartupEntry> items)
        {
            var stamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in items.Select(x => x.FilePath)
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                long stamp = 0;
                try { if (File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
                stamps[path] = stamp;
            }
            return string.Join("|", items.Select(x =>
            {
                long stamp = x.FilePath != null && stamps.TryGetValue(x.FilePath, out var value) ? value : 0;
                return x.Id + ":" + x.Enabled + ":" + x.Command + ":" + stamp;
            }));
        }

        static string LegacyImpact(string hay, bool enabled)
        {
            if (!enabled) return "Low";
            string[] high = { "steam", "discord", "teams", "epic", "battle.net", "adobe", "onedrive", "launcher", "updater", "update.exe" };
            string[] medium = { "spotify", "dropbox", "icloud", "java", "razer", "logitech", "corsair", "asus", "armoury" };
            if (high.Any(hay.Contains)) return "High";
            if (medium.Any(hay.Contains)) return "Medium";
            return "Low";
        }

        static void Same(IReadOnlyList<StartupEntry> entries)
        {
            if (!string.Equals(LegacyFingerprint(entries),
                    ComputeStartupInsightFingerprint(entries), StringComparison.Ordinal))
                throw new InvalidOperationException("Startup cache fingerprint changed across optimization.");
        }
        Same(rows);
        var changed = (StartupEntry[])rows.Clone();
        changed[0] = changed[0] with { Enabled = !changed[0].Enabled };
        Same(changed);
        if (ComputeStartupInsightFingerprint(rows) == ComputeStartupInsightFingerprint(changed))
            throw new InvalidOperationException("Startup cache did not notice an enabled-state change.");
        changed[0] = rows[0] with { Command = "different-command" };
        Same(changed);
        if (ComputeStartupInsightFingerprint(rows) == ComputeStartupInsightFingerprint(changed))
            throw new InvalidOperationException("Startup cache did not notice a command change.");
        for (int i = 0; i < rowCount; i++)
        {
            if (LegacyImpact(haystacks[i], rows[i].Enabled) !=
                EstimateImpact(haystacks[i], rows[i].Enabled))
                throw new InvalidOperationException("Startup impact classification changed.");
        }

        _ = LegacyFingerprint(rows);
        _ = ComputeStartupInsightFingerprint(rows);
        long oldBytes = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int oldTotal = 0;
        for (int i = 0; i < fingerprintIterations; i++)
            oldTotal += LegacyFingerprint(rows).Length;
        double oldMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        oldBytes = GC.GetAllocatedBytesForCurrentThread() - oldBytes;

        long newBytes = GC.GetAllocatedBytesForCurrentThread();
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        int newTotal = 0;
        for (int i = 0; i < fingerprintIterations; i++)
            newTotal += ComputeStartupInsightFingerprint(rows).Length;
        double newMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        newBytes = GC.GetAllocatedBytesForCurrentThread() - newBytes;

        long oldImpactBytes = GC.GetAllocatedBytesForCurrentThread();
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        int oldHigh = 0;
        for (int j = 0; j < impactIterations; j++)
            for (int i = 0; i < rowCount; i++)
                if (LegacyImpact(haystacks[i], rows[i].Enabled) == "High") oldHigh++;
        double oldImpactMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        oldImpactBytes = GC.GetAllocatedBytesForCurrentThread() - oldImpactBytes;

        long newImpactBytes = GC.GetAllocatedBytesForCurrentThread();
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        int newHigh = 0;
        for (int j = 0; j < impactIterations; j++)
            for (int i = 0; i < rowCount; i++)
                if (EstimateImpact(haystacks[i], rows[i].Enabled) == "High") newHigh++;
        double newImpactMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        newImpactBytes = GC.GetAllocatedBytesForCurrentThread() - newImpactBytes;

        if (oldTotal != newTotal || oldHigh != newHigh ||
            newBytes >= oldBytes || newImpactBytes >= oldImpactBytes)
            throw new InvalidOperationException($"Startup preparation output or allocation regression. oldTotal={oldTotal} newTotal={newTotal} oldHigh={oldHigh} newHigh={newHigh} oldFpAllocB={oldBytes} newFpAllocB={newBytes} oldImpactAllocB={oldImpactBytes} newImpactAllocB={newImpactBytes}");

        return $"STARTUP_INSIGHT_PREPARATION_BENCH_OK entries={rowCount} fpIterations={fingerprintIterations} " +
            $"oldFpMs={oldMs:F3} newFpMs={newMs:F3} oldFpAllocB={oldBytes} newFpAllocB={newBytes} " +
            $"impactIterations={impactIterations} oldImpactMs={oldImpactMs:F3} newImpactMs={newImpactMs:F3} " +
            $"oldImpactAllocB={oldImpactBytes} newImpactAllocB={newImpactBytes} " +
            "samePersistedFingerprint=true enabledChange=true commandChange=true sameImpact=true diskWrites=0";
    }
    private static StartupInsightCachePayload? TryReadStartupInsightSnapshot() =>
        UserScopedCacheService.TryRead<StartupInsightCachePayload>(
            StartupInsightCacheKey, TimeSpan.FromHours(12), out var value) ? value : null;

    private static void WriteStartupInsightSnapshot(StartupInsightCachePayload payload) =>
        UserScopedCacheService.Write(StartupInsightCacheKey, payload);

    private static (string Status, string Signer) ReadStartupSignatureMetadata(string path)
    {
        if (!File.Exists(path)) return ("NotFound", string.Empty);
        try
        {
#pragma warning disable SYSLIB0057
            // CreateFromSignedFile owns a native certificate handle of its own;
            // X509Certificate2 copies it, so both wrappers must be released.
            using var signedFileCertificate = X509Certificate.CreateFromSignedFile(path);
            using var cert = new X509Certificate2(signedFileCertificate);
#pragma warning restore SYSLIB0057
            string signer = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (string.IsNullOrWhiteSpace(signer)) signer = cert.Subject ?? string.Empty;
            return ("Signed", signer);
        }
        catch (CryptographicException) { return ("Unsigned", string.Empty); }
        catch (Exception ex)
        {
            LogService.Error("startup-signature", $"Metadata probe failed for {path}", ex);
            return ("Unknown", string.Empty);
        }
    }

    private static readonly string[] StartupHighImpactTokens =
        ["steam", "discord", "teams", "epic", "battle.net", "adobe", "onedrive", "launcher", "updater", "update.exe"];
    private static readonly string[] StartupMediumImpactTokens =
        ["spotify", "dropbox", "icloud", "java", "razer", "logitech", "corsair", "asus", "armoury"];

    private static string EstimateImpact(string hay, bool enabled)
    {
        if (!enabled) return "Low";
        foreach (string key in StartupHighImpactTokens)
            if (hay.Contains(key, StringComparison.Ordinal)) return "High";
        foreach (string key in StartupMediumImpactTokens)
            if (hay.Contains(key, StringComparison.Ordinal)) return "Medium";
        return "Low";
    }

}
