using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record NativeNetworkProbe(
    string Name, string Description, double SpeedMbps, int Mtu,
    string Dns, bool Ipv6, string Gateway, double GatewayMs,
    bool RssEnabled, int RssQueues, bool RscEnabled, string Duplex,
    string DriverProvider, string DriverVersion, string AdvancedSummary);

public sealed record NativeDiskProbe(
    string Name, string MediaType, string BusType, string Health,
    string Operational, double SizeGb, long WriteCacheBytes,
    string DriverProvider, string DriverVersion, string Firmware,
    double? TemperatureC, bool TrimEnabled);

public static partial class NativeSystemProbeService
{
    private sealed record DiskDriverMetadata(string DeviceId, string Provider, string Version);
    private sealed record DiskTemperatureSample(uint DeviceId, double? TemperatureC);
    private sealed record TrimPersistentState(bool Enabled);
    private const string PersistentDiskDriverKey = "storage-disk-drivers-v2";
    private const string PersistentDiskTemperatureKey = "storage-temperatures-v1";
    private const string PersistentTrimKey = "storage-trim-v1";
    private static readonly TimeSpan PersistentDiskDriverTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan PersistentDiskTemperatureTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PersistentDiskTemperatureDisplayTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PersistentTrimTtl = TimeSpan.FromMinutes(10);
    private static readonly object ProbeCacheLock = new();
    private static NativeNetworkProbe? _networkProbeCache;
    private static DateTime _networkProbeAt;
    private static int _networkProbeGeneration;
    private static IReadOnlyList<NativeDiskProbe>? _storageProbeCache;
    private static DateTime _storageProbeAt;
    private static Dictionary<string, (string Provider, string Version)>? _diskDriverCache;
    private static DateTime _diskDriverCacheAt;
    private static bool _trimCache;
    private static DateTime _trimCacheAt;
    private static readonly object StorageScopeLock = new();
    private static readonly object StorageDiskScopeLock = new();
    private static readonly object StorageDriverScopeLock = new();
    private static readonly object StorageTemperatureRefreshLock = new();
    private static ManagementScope? _storageReliabilityScope;
    private static ManagementScope? _storageDiskScope;
    private static ManagementScope? _storageDriverScope;
    private static Task? _storageTemperatureRefreshTask;
    private static int _storageTemperatureRefreshGeneration = -1;
    private static int _storageTemperatureGeneration;
    private static int _storageTemperatureStaleHitCount;
    private static int _storageTemperatureFreshLoadCount;
    private static readonly Dictionary<uint, double?> EmptyStorageTemperatures = new();

    public static void InvalidateNetworkCache()
    {
        Interlocked.Increment(ref _networkProbeGeneration);
        lock (ProbeCacheLock) { _networkProbeCache = null; _networkProbeAt = default; }
        lock (NetworkProbeGate) _networkProbeInFlight = null;
    }

    public static void InvalidateStorageCache(
        bool preserveStableDriverMetadata = false,
        bool invalidateTemperatures = true,
        bool invalidateTrim = false)
    {
        if (invalidateTemperatures)
            Interlocked.Increment(ref _storageTemperatureGeneration);

        lock (ProbeCacheLock)
        {
            _storageProbeCache = null;
            _storageProbeAt = default;
            if (invalidateTrim) _trimCacheAt = default;
            if (!preserveStableDriverMetadata)
            {
                _diskDriverCache = null;
                _diskDriverCacheAt = default;
            }
        }

        if (invalidateTemperatures)
            PersistentHardwareCacheService.Remove(PersistentDiskTemperatureKey);
        if (invalidateTrim)
            PersistentHardwareCacheService.Remove(PersistentTrimKey);
        if (!preserveStableDriverMetadata)
            PersistentHardwareCacheService.Remove(PersistentDiskDriverKey);
    }

    private static readonly object NetworkProbeGate = new();
    private static Task<NativeNetworkProbe?>? _networkProbeInFlight;

    public static async Task<NativeNetworkProbe?> GetNetworkAsync(CancellationToken token = default)
    {
        lock (ProbeCacheLock)
            if (_networkProbeCache != null && DateTime.UtcNow - _networkProbeAt < TimeSpan.FromMinutes(2)) return _networkProbeCache;
        Task<NativeNetworkProbe?> probe;
        lock (NetworkProbeGate)
        {
            if (_networkProbeInFlight == null)
            {
                int generation = Volatile.Read(ref _networkProbeGeneration);
                var created = Task.Run(() => ReadNetworkCore(generation));
                _networkProbeInFlight = created;
                probe = created;
                _ = created.ContinueWith(t =>
                {
                    if (t.IsFaulted) _ = t.Exception;
                    lock (NetworkProbeGate)
                        if (ReferenceEquals(_networkProbeInFlight, created))
                            _networkProbeInFlight = null;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else
            {
                probe = _networkProbeInFlight;
            }
        }
        return await probe.WaitAsync(token).ConfigureAwait(false);
    }

    public static NativeNetworkProbe? ReadNetwork()
    {
        lock (NetworkProbeGate)
        {
            lock (ProbeCacheLock)
                if (_networkProbeCache != null && DateTime.UtcNow - _networkProbeAt < TimeSpan.FromMinutes(2)) return _networkProbeCache;
            return ReadNetworkCore(Volatile.Read(ref _networkProbeGeneration));
        }
    }

    private static NativeNetworkProbe? ReadNetworkCore(int generation)
    {
        lock (ProbeCacheLock)
            if (_networkProbeCache != null && DateTime.UtcNow - _networkProbeAt < TimeSpan.FromMinutes(2)) return _networkProbeCache;
        NetworkInterface? nic = null;
        foreach (var candidate in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (candidate.OperationalStatus != OperationalStatus.Up ||
                candidate.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            if (nic == null || candidate.Speed > nic.Speed)
                nic = candidate;
        }
        if (nic == null) return null;

        var ip = nic.GetIPProperties();
        var ipv4 = ip.GetIPv4Properties();

        var dnsBuilder = new StringBuilder(32);
        foreach (var address in ip.DnsAddresses)
        {
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                continue;
            if (dnsBuilder.Length > 0) dnsBuilder.Append(", ");
            dnsBuilder.Append(address);
        }
        string dns = dnsBuilder.ToString();

        string gateway = string.Empty;
        foreach (var item in ip.GatewayAddresses)
        {
            if (item.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                continue;
            gateway = item.Address.ToString();
            break;
        }

        bool ipv6 = false;
        foreach (var item in ip.UnicastAddresses)
        {
            if (item.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                continue;
            ipv6 = true;
            break;
        }
        double latency = PingGateway(gateway);

        string provider = string.Empty, version = string.Empty, duplex = string.Empty, advanced = string.Empty;
        bool rss = false, rsc = false; int queues = 0;
        ReadNetAdapterCim(nic.Description, nic.Name, ref provider, ref version, ref duplex, ref advanced, ref rss, ref queues, ref rsc);

        var result = new NativeNetworkProbe(nic.Name, nic.Description, nic.Speed / 1_000_000d,
            ipv4?.Mtu ?? 0, dns, ipv6, gateway, latency, rss, queues, rsc,
            duplex, provider, version, advanced);
        if (generation == Volatile.Read(ref _networkProbeGeneration))
        {
            lock (ProbeCacheLock)
            {
                if (generation == Volatile.Read(ref _networkProbeGeneration))
                {
                    _networkProbeCache = result;
                    _networkProbeAt = DateTime.UtcNow;
                }
            }
        }
        return result;
    }

    private static double PingGateway(string gateway)
    {
        if (string.IsNullOrWhiteSpace(gateway)) return 0;
        try { using var ping = new Ping(); var r = ping.Send(gateway, 500); return r.Status == IPStatus.Success ? r.RoundtripTime : 0; }
        catch { return 0; }
    }
    private static void ReadNetAdapterCim(string description, string name,
        ref string provider, ref string version, ref string duplex, ref string advanced,
        ref bool rss, ref int queues, ref bool rsc)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\CIMV2"), new ObjectQuery(
                "SELECT DeviceName,DriverProviderName,DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceClass='NET' AND DeviceName=" + Wql(description)), QueryOptions());
            var rows = searcher.Get();
            var metadata = WmiFirstRowScope.ReadFirst<ManagementObject, (string Provider, string Version)>(
                rows, rows, x => (
                    x["DriverProviderName"]?.ToString() ?? string.Empty,
                    x["DriverVersion"]?.ToString() ?? string.Empty),
                (provider, version));
            provider = metadata.Provider;
            version = metadata.Version;
        }
        catch { }

        try
        {
            var scope = new ManagementScope(@"\\.\root\StandardCimv2"); scope.Connect();
            using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FullDuplex FROM MSFT_NetAdapter WHERE InterfaceDescription=" + Wql(description)), QueryOptions());
            var rows = q.Get();
            duplex = WmiFirstRowScope.ReadFirst<ManagementObject, string>(
                rows, rows,
                x => x["FullDuplex"] is bool fd ? (fd ? "Full Duplex" : "Half Duplex") : string.Empty,
                duplex);
            ReadRssRsc(scope, name, ref rss, ref queues, ref rsc);
        }
        catch { }
    }

    private static void ReadRssRsc(ManagementScope scope, string name, ref bool rss, ref int queues, ref bool rsc)
    {
        try
        {
            using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Enabled,NumberOfReceiveQueues FROM MSFT_NetAdapterRssSettingData WHERE Name=" + Wql(name)), QueryOptions());
            var rows = q.Get();
            var status = WmiFirstRowScope.ReadFirst<ManagementObject, (bool Rss, int Queues)>(
                rows, rows, x => (Convert.ToBoolean(x["Enabled"] ?? false),
                    Convert.ToInt32(x["NumberOfReceiveQueues"] ?? 0)), (rss, queues));
            rss = status.Rss;
            queues = status.Queues;
        }
        catch { }
        try
        {
            using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT IPv4Enabled FROM MSFT_NetAdapterRscSettingData WHERE Name=" + Wql(name)), QueryOptions());
            var rows = q.Get();
            rsc = WmiFirstRowScope.ReadFirst<ManagementObject, bool>(
                rows, rows, x => Convert.ToBoolean(x["IPv4Enabled"] ?? false), rsc);
        }
        catch { }
    }
    public static IReadOnlyList<NativeDiskProbe> ReadStorage()
    {
        lock (ProbeCacheLock)
            if (_storageProbeCache != null && DateTime.UtcNow - _storageProbeAt < TimeSpan.FromSeconds(60)) return _storageProbeCache;
        var result = new List<NativeDiskProbe>();
        var phase = System.Diagnostics.Stopwatch.StartNew();

        Dictionary<string, (string Provider, string Version)> drivers = null!;
        Dictionary<uint, double?> temperatures = null!;
        bool trim = false;
        var disks = new List<(uint Index, string Name, string Media, string Bus, string Status, double SizeGb, string Firmware, string Pnp)>();
        long driverMs = 0;
        long temperatureMs = 0;
        long trimMs = 0;
        long diskMs = 0;

        Parallel.Invoke(
            () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                drivers = ReadDiskDrivers();
                driverMs = sw.ElapsedMilliseconds;
            },
            () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                temperatures = ReadStorageTemperatures();
                temperatureMs = sw.ElapsedMilliseconds;
            },
            () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                trim = ReadTrimState();
                trimMs = sw.ElapsedMilliseconds;
            },
            () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var q = new ManagementObjectSearcher(
                        GetStorageDiskScope(),
                        new ObjectQuery("SELECT Index,Model,MediaType,InterfaceType,Status,Size,FirmwareRevision,PNPDeviceID FROM Win32_DiskDrive"),
                        QueryOptions());
                    var results = q.Get();
                    WmiEnumerationScope.ForEach<ManagementObject>(results, results,
                        CancellationToken.None, x =>
                    {
                        string name = x["Model"]?.ToString() ?? "Disk";
                        string pnp = x["PNPDeviceID"]?.ToString() ?? string.Empty;
                        string iface = x["InterfaceType"]?.ToString() ?? string.Empty;
                        string bus = pnp.Contains("NVME", StringComparison.OrdinalIgnoreCase) ? "NVMe" : iface;
                        string media = bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase)
                            ? "SSD"
                            : (x["MediaType"]?.ToString() ?? "Disk");
                        string status = x["Status"]?.ToString() ?? "Unknown";
                        uint index = x["Index"] is null ? uint.MaxValue : Convert.ToUInt32(x["Index"]);
                        disks.Add((
                            index,
                            name,
                            media,
                            bus,
                            status,
                            ToDouble(x["Size"]) / 1024d / 1024d / 1024d,
                            x["FirmwareRevision"]?.ToString() ?? string.Empty,
                            pnp));
                    });
                }
                catch (Exception ex)
                {
                    LogService.Error("Native storage disk inventory failed", ex);
                }
                diskMs = sw.ElapsedMilliseconds;
            });

        long prepWallMs = phase.ElapsedMilliseconds;
        long mergeStartMs = phase.ElapsedMilliseconds;
        foreach (var disk in disks)
        {
            var driver = drivers.TryGetValue(disk.Pnp, out var exactDriver)
                ? exactDriver
                : (Provider: string.Empty, Version: string.Empty);
            double? temperature = temperatures.TryGetValue(disk.Index, out var mappedTemperature)
                ? mappedTemperature
                : null;
            if (temperature <= 0) temperature = null;
            result.Add(new NativeDiskProbe(
                disk.Name,
                disk.Media,
                disk.Bus,
                disk.Status == "OK" ? "Healthy" : disk.Status,
                disk.Status,
                disk.SizeGb,
                0,
                driver.Provider,
                driver.Version,
                disk.Firmware,
                temperature,
                trim));
        }
        long mergeMs = phase.ElapsedMilliseconds - mergeStartMs;
        LogService.Info("performance",
            $"STORAGE_NATIVE_PHASE driversMs={driverMs} temperaturesMs={temperatureMs} trimMs={trimMs} disksMs={diskMs} parallelWallMs={prepWallMs} mergeMs={mergeMs} totalMs={phase.ElapsedMilliseconds} count={result.Count}");
        lock (ProbeCacheLock) { _storageProbeCache = result.ToArray(); _storageProbeAt = DateTime.UtcNow; }
        return _storageProbeCache;
    }

    private static Dictionary<uint, double?> ReadStorageTemperatures()
    {
        if (PersistentHardwareCacheService.TryReadMachineStable<DiskTemperatureSample[]>(
                PersistentDiskTemperatureKey, PersistentDiskTemperatureTtl, out var persisted) &&
            persisted != null)
        {
            var cached = new Dictionary<uint, double?>(persisted.Length);
            foreach (var sample in persisted)
                cached.TryAdd(sample.DeviceId, sample.TemperatureC);
            LogService.Info("performance", $"STORAGE_TEMPERATURE_PERSISTENT_HIT count={cached.Count}");
            return cached;
        }

        if (PersistentHardwareCacheService.TryReadMachineStable<DiskTemperatureSample[]>(
                PersistentDiskTemperatureKey, PersistentDiskTemperatureDisplayTtl, out var stale) &&
            stale != null)
        {
            var cached = new Dictionary<uint, double?>(stale.Length);
            foreach (var sample in stale)
                cached.TryAdd(sample.DeviceId, sample.TemperatureC);
            Interlocked.Increment(ref _storageTemperatureStaleHitCount);
            QueueStorageTemperatureRefresh();
            LogService.Info("performance", $"STORAGE_TEMPERATURE_STALE_HIT count={cached.Count} refreshQueued=true");
            return cached;
        }

        QueueStorageTemperatureRefresh();
        LogService.Info("performance", "STORAGE_TEMPERATURE_COLD_DEFERRED refreshQueued=true");
        return EmptyStorageTemperatures;
    }

    private static bool ReuseStorageTemperatureRefresh(
        Task? pending, int pendingGeneration, int requestedGeneration) =>
        pending is { IsCompleted: false } && pendingGeneration == requestedGeneration;

    // A previous generation's query may still be running after invalidation.
    // Serialize its successor rather than discarding the new refresh request.
    private static Task CreateStorageTemperatureRefreshTask(
        Task? predecessor, int generation, Func<int, Task> refresh)
    {
        return Task.Run(async () =>
        {
            if (predecessor != null)
            {
                // The prior task owns its own exception reporting. A failed
                // or superseded probe must not suppress the new generation.
                try { await predecessor.ConfigureAwait(false); }
                catch { }
            }
            await refresh(generation).ConfigureAwait(false);
        });
    }

    internal static async Task<string> RunStorageTemperatureGenerationSmokeAsync()
    {
        var old = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ReuseStorageTemperatureRefresh(old.Task, pendingGeneration: 4, requestedGeneration: 5))
            throw new InvalidOperationException(
                "An in-flight previous-generation disk temperature refresh suppresses the new refresh.");
        if (!ReuseStorageTemperatureRefresh(old.Task, pendingGeneration: 5, requestedGeneration: 5))
            throw new InvalidOperationException("Same-generation refresh was not coalesced.");

        int executed = 0, observedGeneration = -1;
        Task next = CreateStorageTemperatureRefreshTask(
            old.Task, 5, generation =>
            {
                Interlocked.Increment(ref executed);
                observedGeneration = generation;
                return Task.CompletedTask;
            });
        if (next.IsCompleted || Volatile.Read(ref executed) != 0)
            throw new InvalidOperationException("New-generation disk refresh started before its predecessor finished.");
        old.TrySetResult(true);
        await next.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (Volatile.Read(ref executed) != 1 || observedGeneration != 5)
            throw new InvalidOperationException("Queued disk refresh did not execute exactly once in new generation.");
        return "STORAGE_TEMPERATURE_GENERATION_SMOKE_OK priorGenerationReplaced=true " +
               "sameGenerationCoalesced=true successorSerialized=true hardwareCalls=0";
    }

    private static void QueueStorageTemperatureRefresh()
    {
        lock (StorageTemperatureRefreshLock)
        {
            int generation = Volatile.Read(ref _storageTemperatureGeneration);
            Task? current = _storageTemperatureRefreshTask;
            if (ReuseStorageTemperatureRefresh(
                current, _storageTemperatureRefreshGeneration, generation))
                return;

            Task? predecessor = current is { IsCompleted: false } ? current : null;
            var created = CreateStorageTemperatureRefreshTask(
                predecessor, generation, requested =>
                {
                    _ = ReadStorageTemperaturesFresh(requested);
                    return Task.CompletedTask;
                });
            _storageTemperatureRefreshGeneration = generation;
            _storageTemperatureRefreshTask = created;
            _ = created.ContinueWith(t =>
            {
                if (t.IsFaulted) _ = t.Exception;
                if (t.Status == TaskStatus.RanToCompletion && generation == Volatile.Read(ref _storageTemperatureGeneration))
                {
                    lock (ProbeCacheLock)
                    {
                        _storageProbeCache = null;
                        _storageProbeAt = default;
                    }
                }
                lock (StorageTemperatureRefreshLock)
                    if (ReferenceEquals(_storageTemperatureRefreshTask, created))
                        _storageTemperatureRefreshTask = null;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
