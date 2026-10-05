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

public static class NativeSystemProbeService
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
            foreach (ManagementObject x in searcher.Get())
            {
                provider = x["DriverProviderName"]?.ToString() ?? string.Empty;
                version = x["DriverVersion"]?.ToString() ?? string.Empty;
                break;
            }
        }
        catch { }

        try
        {
            var scope = new ManagementScope(@"\\.\root\StandardCimv2"); scope.Connect();
            using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FullDuplex FROM MSFT_NetAdapter WHERE InterfaceDescription=" + Wql(description)), QueryOptions());
            foreach (ManagementObject x in q.Get())
            {
                duplex = x["FullDuplex"] is bool fd ? (fd ? "Full Duplex" : "Half Duplex") : string.Empty;
                break;
            }
            ReadRssRsc(scope, name, ref rss, ref queues, ref rsc);
        }
        catch { }
    }

    private static void ReadRssRsc(ManagementScope scope, string name, ref bool rss, ref int queues, ref bool rsc)
    {
        try
        {
            using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Enabled,NumberOfReceiveQueues FROM MSFT_NetAdapterRssSettingData WHERE Name=" + Wql(name)), QueryOptions());
            foreach (ManagementObject x in q.Get()) { rss = Convert.ToBoolean(x["Enabled"] ?? false); queues = Convert.ToInt32(x["NumberOfReceiveQueues"] ?? 0); break; }
        }
        catch { }
        try
        {
            using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT IPv4Enabled FROM MSFT_NetAdapterRscSettingData WHERE Name=" + Wql(name)), QueryOptions());
            foreach (ManagementObject x in q.Get()) { rsc = Convert.ToBoolean(x["IPv4Enabled"] ?? false); break; }
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
                    foreach (ManagementObject x in q.Get())
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
                    }
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

    private static void QueueStorageTemperatureRefresh()
    {
        lock (StorageTemperatureRefreshLock)
        {
            if (_storageTemperatureRefreshTask is { IsCompleted: false }) return;
            int generation = Volatile.Read(ref _storageTemperatureGeneration);
            var created = Task.Run(() => ReadStorageTemperaturesFresh(generation));
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

    private static Dictionary<uint, double?> ReadStorageTemperaturesFresh(int generation)
    {
        Interlocked.Increment(ref _storageTemperatureFreshLoadCount);
        var map = new Dictionary<uint, double?>();
        ManagementScope? scope = null;
        try
        {
            lock (StorageScopeLock)
            {
                scope = _storageReliabilityScope;
                if (scope == null || !scope.IsConnected)
                {
                    scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                    scope.Connect();
                    _storageReliabilityScope = scope;
                }
            }

            using var q = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT DeviceId,Temperature FROM MSFT_StorageReliabilityCounter"),
                QueryOptions());

            foreach (ManagementObject counter in q.Get())
            {
                if (counter["DeviceId"] is null) continue;
                if (!uint.TryParse(counter["DeviceId"]?.ToString(), out uint deviceId)) continue;
                double? temp = counter["Temperature"] is null
                    ? null
                    : Convert.ToDouble(counter["Temperature"]);
                map[deviceId] = temp;
            }

            if (generation == Volatile.Read(ref _storageTemperatureGeneration))
            {
                var samples = new DiskTemperatureSample[map.Count];
                int sampleIndex = 0;
                foreach (var item in map)
                    samples[sampleIndex++] = new DiskTemperatureSample(item.Key, item.Value);
                PersistentHardwareCacheService.WriteMachineStable(
                    PersistentDiskTemperatureKey,
                    samples);
            }
        }
        catch (Exception ex)
        {
            lock (StorageScopeLock)
            {
                if (ReferenceEquals(_storageReliabilityScope, scope))
                    _storageReliabilityScope = null;
            }
            LogService.Error("storage", "Storage temperature probe failed", ex);
        }
        return map;
    }

    internal static async Task RunStorageTemperatureStaleCacheSmokeAsync(CancellationToken token)
    {
        InvalidateStorageCache(preserveStableDriverMetadata: true);
        _ = await Task.Run(() => ReadStorageTemperaturesFresh(Volatile.Read(ref _storageTemperatureGeneration)), token).ConfigureAwait(false);
        var fresh = await Task.Run(ReadStorage, token).ConfigureAwait(false);
        if (fresh.Count == 0)
            throw new InvalidOperationException("Storage temperature stale-cache smoke could not establish a fresh disk snapshot.");

        int freshLoadsBeforePreserve = Volatile.Read(ref _storageTemperatureFreshLoadCount);
        InvalidateStorageCache(preserveStableDriverMetadata: true, invalidateTemperatures: false);
        var preserveSw = System.Diagnostics.Stopwatch.StartNew();
        var preserved = await Task.Run(ReadStorage, token).ConfigureAwait(false);
        long preserveReadMs = preserveSw.ElapsedMilliseconds;
        int freshLoadsAfterPreserve = Volatile.Read(ref _storageTemperatureFreshLoadCount);
        if (preserved.Count != fresh.Count || freshLoadsAfterPreserve != freshLoadsBeforePreserve)
            throw new InvalidOperationException(
                $"Storage temperature preserve invalidation failed. disks={preserved.Count}/{fresh.Count} freshLoads={freshLoadsBeforePreserve}->{freshLoadsAfterPreserve}.");

        if (!PersistentHardwareCacheService.BackdateForSmoke(PersistentDiskTemperatureKey, TimeSpan.FromMinutes(5)))
            throw new InvalidOperationException("Storage temperature stale-cache smoke could not backdate the persistent temperature cache.");

        lock (ProbeCacheLock)
        {
            _storageProbeCache = null;
            _storageProbeAt = default;
        }

        int staleBefore = Volatile.Read(ref _storageTemperatureStaleHitCount);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var staleRead = await Task.Run(ReadStorage, token).ConfigureAwait(false);
        long staleReadMs = sw.ElapsedMilliseconds;
        int staleAfter = Volatile.Read(ref _storageTemperatureStaleHitCount);
        if (staleRead.Count == 0 || staleAfter != staleBefore + 1)
            throw new InvalidOperationException($"Storage temperature stale-cache path was not used. before={staleBefore} after={staleAfter} count={staleRead.Count}.");

        Task? refresh;
        lock (StorageTemperatureRefreshLock) refresh = _storageTemperatureRefreshTask;
        if (refresh != null)
            await refresh.WaitAsync(token).ConfigureAwait(false);

        lock (ProbeCacheLock)
        {
            _storageProbeCache = null;
            _storageProbeAt = default;
        }

        int staleBeforeFreshRead = Volatile.Read(ref _storageTemperatureStaleHitCount);
        var refreshed = await Task.Run(ReadStorage, token).ConfigureAwait(false);
        int staleAfterFreshRead = Volatile.Read(ref _storageTemperatureStaleHitCount);
        if (refreshed.Count == 0 || staleAfterFreshRead != staleBeforeFreshRead)
            throw new InvalidOperationException("Storage temperature background refresh did not restore a fresh persistent cache.");

        LogService.Info("performance",
            $"STORAGE_TEMPERATURE_STALE_CACHE_SMOKE_OK disks={fresh.Count} preserveReadMs={preserveReadMs} staleReadMs={staleReadMs} staleHits={staleAfter - staleBefore}");
    }

    private static Dictionary<string, (string Provider, string Version)> ReadDiskDrivers()
    {
        lock (ProbeCacheLock)
            if (_diskDriverCache != null && DateTime.UtcNow - _diskDriverCacheAt < TimeSpan.FromMinutes(10))
                return _diskDriverCache;

        if (PersistentHardwareCacheService.TryReadMachineStable<DiskDriverMetadata[]>(
                PersistentDiskDriverKey, PersistentDiskDriverTtl, out var persisted) &&
            persisted is { Length: > 0 })
        {
            var persistedMap = persisted
                .Where(x => !string.IsNullOrWhiteSpace(x.DeviceId))
                .GroupBy(x => x.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => (g.First().Provider ?? string.Empty, g.First().Version ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase);

            if (persistedMap.Count > 0)
            {
                lock (ProbeCacheLock)
                {
                    _diskDriverCache = persistedMap;
                    _diskDriverCacheAt = DateTime.UtcNow;
                }
                LogService.Info("performance", $"STORAGE_DRIVER_PERSISTENT_HIT count={persistedMap.Count}");
                return persistedMap;
            }
        }

        var map = new Dictionary<string, (string Provider, string Version)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var q = new ManagementObjectSearcher(
                GetStorageDriverScope(),
                new ObjectQuery("SELECT DeviceID,DriverProviderName,DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceClass='DISKDRIVE'"),
                QueryOptions());
            foreach (ManagementObject x in q.Get())
            {
                string deviceId = x["DeviceID"]?.ToString() ?? string.Empty;
                if (deviceId.Length > 0)
                    map[deviceId] = (
                        x["DriverProviderName"]?.ToString() ?? string.Empty,
                        x["DriverVersion"]?.ToString() ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("storage", "Disk driver metadata probe failed", ex);
        }

        if (map.Count > 0)
        {
            PersistentHardwareCacheService.WriteMachineStable(
                PersistentDiskDriverKey,
                map.Select(x => new DiskDriverMetadata(x.Key, x.Value.Provider, x.Value.Version)).ToArray());
        }

        lock (ProbeCacheLock)
        {
            _diskDriverCache = map;
            _diskDriverCacheAt = DateTime.UtcNow;
        }
        return map;
    }
    internal static bool ReadTrimStateCached() => ReadTrimState();

    private static bool ReadTrimState()
    {
        lock (ProbeCacheLock) if (_trimCacheAt != default && DateTime.UtcNow - _trimCacheAt < TimeSpan.FromMinutes(5)) return _trimCache;

        if (PersistentHardwareCacheService.TryReadMachineStable<TrimPersistentState>(
                PersistentTrimKey, PersistentTrimTtl, out var persisted) &&
            persisted != null)
        {
            lock (ProbeCacheLock) { _trimCache = persisted.Enabled; _trimCacheAt = DateTime.UtcNow; }
            LogService.Info("performance", $"STORAGE_TRIM_PERSISTENT_HIT enabled={persisted.Enabled}");
            return persisted.Enabled;
        }

        try
        {
            using (RegistryKey? fileSystem = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem"))
            {
                object? raw = fileSystem?.GetValue("NtfsDisableDeleteNotify");
                if (raw != null)
                {
                    bool registryValue = Convert.ToInt32(raw) == 0;
                    lock (ProbeCacheLock) { _trimCache = registryValue; _trimCacheAt = DateTime.UtcNow; }
                    PersistentHardwareCacheService.WriteMachineStable(PersistentTrimKey, new TrimPersistentState(registryValue));
                    return registryValue;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Info("storage", "TRIM registry fast-path unavailable: " + ex.Message);
        }

        try
        {
            using var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("fsutil.exe", "behavior query DisableDeleteNotify")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!p.Start())
            {
                LogService.Error("storage", "TRIM state probe failed to start fsutil.exe");
                return false;
            }
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(true); } catch { }
                LogService.Error("storage", "TRIM state probe timed out", new System.TimeoutException("fsutil.exe behavior query DisableDeleteNotify exceeded 3 seconds."));
                return false;
            }
            bool value = p.ExitCode == 0 && output.Contains("= 0", StringComparison.OrdinalIgnoreCase);
            lock (ProbeCacheLock) { _trimCache = value; _trimCacheAt = DateTime.UtcNow; }
            PersistentHardwareCacheService.WriteMachineStable(PersistentTrimKey, new TrimPersistentState(value));
            return value;
        }
        catch (Exception ex)
        {
            LogService.Error("storage", "TRIM state probe failed", ex);
            return false;
        }
    }

    public static (string Status, string Signer) ReadSignature(string path)
    {
        if (!File.Exists(path)) return ("NotFound", string.Empty);
        try
        {
            bool valid = VerifyAuthenticode(path);
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return (valid ? "Valid" : "Invalid", cert.Subject ?? string.Empty);
        }
        catch { return ("NotSigned", string.Empty); }
    }

    public static WindowsUpdateCenterStatus ReadWindowsUpdateStatus()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DateTime? pauseUntil = ReadPauseUntil();
        bool paused = pauseUntil.HasValue && pauseUntil.Value > DateTime.Now;
        long pauseMs = sw.ElapsedMilliseconds;

        bool reboot;
        using (var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
        using (var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            reboot = cbs != null || wu != null;
        long rebootMs = sw.ElapsedMilliseconds - pauseMs;

        string services = string.Join("; ", new[] { "wuauserv", "bits", "cryptsvc", "usosvc" }.Select(ServiceState));
        long servicesMs = sw.ElapsedMilliseconds - pauseMs - rebootMs;

        var errors = ReadWindowsUpdateErrors();
        long errorsMs = sw.ElapsedMilliseconds - pauseMs - rebootMs - servicesMs;

        LogService.Info("performance",
            $"WINDOWS_UPDATE_STATUS_PHASE pauseMs={pauseMs} rebootMs={rebootMs} servicesMs={servicesMs} errorsMs={errorsMs} totalMs={sw.ElapsedMilliseconds}");
        return new WindowsUpdateCenterStatus(reboot, paused, pauseUntil, services, errors.Count, errors.LastError);
    }
    private static DateTime? ReadPauseUntil()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");
            string? raw = key?.GetValue("PauseUpdatesExpiryTime")?.ToString();
            return DateTime.TryParse(raw, out var dt) ? dt.ToLocalTime() : null;
        }
        catch { return null; }
    }

    private static string ServiceState(string name)
    {
        try { using var s = new ServiceController(name); return name + "=" + s.Status; }
        catch { return name + "=Unknown"; }
    }

    private static (int Count, string LastError) ReadWindowsUpdateErrors()
    {
        int count = 0; string last = string.Empty;
        try
        {
            var query = new EventLogQuery("Microsoft-Windows-WindowsUpdateClient/Operational", PathType.LogName,
                "*[System[(Level=2)]]")
            { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (int i = 0; i < 80; i++)
            {
                using var ev = reader.ReadEvent(); if (ev == null) break;
                count++; if (last.Length == 0) last = ev.FormatDescription() ?? string.Empty;
            }
        }
        catch { }
        return (count, last);
    }

    public static IReadOnlyList<AvailableDriverUpdate> ReadAvailableDriverUpdates()
    {
        var list = new List<AvailableDriverUpdate>();
        Type? t = Type.GetTypeFromProgID("Microsoft.Update.Session");
        if (t == null) return list;
        dynamic? session = null;
        try
        {
            session = Activator.CreateInstance(t);
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver'");
            for (int i = 0; i < result.Updates.Count; i++)
            {
                dynamic u = result.Updates.Item(i);
                list.Add(new AvailableDriverUpdate((string)u.Title, SafeDynamic(u, "DriverClass"), SafeDynamic(u, "DriverManufacturer"), SafeDynamic(u, "DriverVerDate")));
            }
        }
        catch (Exception ex) { LogService.Error("Native driver update query failed", ex); }
        finally { if (session != null && System.Runtime.InteropServices.Marshal.IsComObject(session)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(session); }
        return list;
    }
    private static string SafeDynamic(dynamic value, string member)
    {
        try
        {
            return member switch
            {
                "DriverClass" => Convert.ToString(value.DriverClass) ?? string.Empty,
                "DriverManufacturer" => Convert.ToString(value.DriverManufacturer) ?? string.Empty,
                "DriverVerDate" => Convert.ToString(value.DriverVerDate) ?? string.Empty,
                _ => string.Empty
            };
        }
        catch { return string.Empty; }
    }

    private static bool VerifyAuthenticode(string path)
    {
        IntPtr filePtr = IntPtr.Zero;
        try
        {
            var file = new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                pcwszFilePath = path,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };
            filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,
                pFile = filePtr,
                dwStateAction = 0,
                dwProvFlags = 0x00000020,
                dwUIContext = 0
            };
            Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    private static ManagementScope GetStorageDiskScope()
    {
        lock (StorageDiskScopeLock)
        {
            if (_storageDiskScope == null || !_storageDiskScope.IsConnected)
            {
                _storageDiskScope = new ManagementScope(@"\\.\root\CIMV2");
                _storageDiskScope.Connect();
            }
            return _storageDiskScope;
        }
    }

    private static ManagementScope GetStorageDriverScope()
    {
        lock (StorageDriverScopeLock)
        {
            if (_storageDriverScope == null || !_storageDriverScope.IsConnected)
            {
                _storageDriverScope = new ManagementScope(@"\\.\root\CIMV2");
                _storageDriverScope.Connect();
            }
            return _storageDriverScope;
        }
    }

    private static System.Management.EnumerationOptions QueryOptions() => new()
    {
        Timeout = TimeSpan.FromSeconds(2),
        ReturnImmediately = false,
        Rewindable = false
    };

    private static string Wql(string s) => "'" + s.Replace("'", "''") + "'";
    private static double ToDouble(object? x) { try { return Convert.ToDouble(x ?? 0); } catch { return 0; } }
}
