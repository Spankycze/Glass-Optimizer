using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        var fileStamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in items
                     .Select(x => x.FilePath)
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Select(x => x!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            long stamp = 0;
            try { if (File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
            fileStamps[path] = stamp;
        }

        string fingerprint = string.Join("|", items.Select(x =>
        {
            long stamp = x.FilePath != null && fileStamps.TryGetValue(x.FilePath, out var cachedStamp) ? cachedStamp : 0;
            return x.Id + ":" + x.Enabled + ":" + x.Command + ":" + stamp;
        }));
        if (_startupInsightCache != null && fingerprint == _startupInsightFingerprint && DateTime.UtcNow - _startupInsightCacheAt < TimeSpan.FromMinutes(2))
            return _startupInsightCache;

        await StartupInsightLoadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_startupInsightCache != null && fingerprint == _startupInsightFingerprint && DateTime.UtcNow - _startupInsightCacheAt < TimeSpan.FromMinutes(2))
                return _startupInsightCache;

            var persisted = TryReadStartupInsightSnapshot();
            if (persisted != null && string.Equals(persisted.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                _startupInsightCache = persisted.Items;
                _startupInsightFingerprint = fingerprint;
                _startupInsightCacheAt = DateTime.UtcNow;
                return persisted.Items;
            }

            var paths = items.Where(x => !string.IsNullOrWhiteSpace(x.FilePath) && File.Exists(x.FilePath!))
                .Select(x => x.FilePath!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var signature = new Dictionary<string, (string Status, string Signer)>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                signature[path] = ReadStartupSignatureMetadata(path);
            }
            var map = new Dictionary<string, StartupInsight>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
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
            WriteStartupInsightSnapshot(new StartupInsightCachePayload(fingerprint, map));
            return map;
        }
        finally { StartupInsightLoadGate.Release(); }
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
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
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

    private static string EstimateImpact(string hay, bool enabled)
    {
        if (!enabled) return "Low";
        string[] high = { "steam", "discord", "teams", "epic", "battle.net", "adobe", "onedrive", "launcher", "updater", "update.exe" };
        string[] medium = { "spotify", "dropbox", "icloud", "java", "razer", "logitech", "corsair", "asus", "armoury" };
        if (high.Any(hay.Contains)) return "High";
        if (medium.Any(hay.Contains)) return "Medium";
        return "Low";
    }

}
