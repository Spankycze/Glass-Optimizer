using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record OverviewSnapshot(
    string Windows,
    string Cpu,
    string Gpu,
    string Ram,
    string RamHardware,
    string Motherboard,
    string Disk,
    string Network,
    string PowerPlan);

public static class SystemInfoService
{
    private static readonly char[] CleanTextSeparators = { ' ', '\t', '\r', '\n' };
    private static readonly string[] GpuRegistryChildren = { "0000", "0001" };
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    private static readonly object CacheLock = new();
    private static readonly object HardwareIdentityLock = new();
    private static OverviewSnapshot? _cached;
    private static HardwareIdentity? _hardwareIdentity;
    private static DateTime _cachedAt;
    private static Task<OverviewSnapshot>? _flight;
    private static int _generation;
    private static readonly object NetworkSummaryLock = new();
    private static string? _networkSummary;
    private static DateTime _networkSummaryAt;
    private static readonly TimeSpan NetworkSummaryTtl = TimeSpan.FromSeconds(10);

    internal sealed record HardwareIdentity(string Cpu, string Motherboard, string Ram);
    private sealed record RamModuleInfo(ulong Capacity, uint Speed, uint ConfiguredSpeed, ushort MemoryType, string Manufacturer, string PartNumber);
    private const string HardwareIdentityPersistentKey = "overview-hardware-identity-v1";
    private static readonly TimeSpan HardwareIdentityPersistentTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(2);

    public static OverviewSnapshot? TryGetFreshSnapshot()
    {
        lock (CacheLock)
            return _cached != null && DateTime.UtcNow - _cachedAt < SnapshotTtl ? _cached : null;
    }

    public static Task<OverviewSnapshot> GetSnapshotAsync(bool forceRefresh = false)
    {
        lock (CacheLock)
        {
            if (!forceRefresh && _cached != null && DateTime.UtcNow - _cachedAt < SnapshotTtl)
                return Task.FromResult(_cached);
            if (!forceRefresh && _flight != null)
                return _flight;
            if (forceRefresh)
            {
                // Refresh only dynamic Overview data. CPU/baseboard/RAM identity is machine-stable
                // and is invalidated explicitly by HardwareSnapshotService.Invalidate() on hardware changes.
                // Clearing it here made every ordinary force refresh pay persistent JSON/WMI identity cost.
                _generation++;
                _cached = null;
                _cachedAt = default;
            }

            int generation = _generation;
            var created = Task.Run(() =>
            {
                var snapshot = BuildSnapshot();
                lock (CacheLock)
                {
                    if (generation == _generation)
                    {
                        _cached = snapshot;
                        _cachedAt = DateTime.UtcNow;
                    }
                }
                return snapshot;
            });
            _flight = created;
            _ = created.ContinueWith(t =>
            {
                if (t.IsFaulted) _ = t.Exception;
                lock (CacheLock)
                    if (ReferenceEquals(_flight, created)) _flight = null;
            }, System.Threading.CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return created;
        }
    }

    public static void InvalidateDynamic()
    {
        lock (CacheLock)
        {
            _generation++;
            _cached = null;
            _cachedAt = default;
            _flight = null;
        }
    }

    public static void Invalidate()
    {
        lock (CacheLock)
        {
            _generation++;
            _cached = null;
            _cachedAt = default;
            _flight = null;
        }

        lock (HardwareIdentityLock)
        {
            _hardwareIdentity = null;
            PersistentHardwareCacheService.Remove(HardwareIdentityPersistentKey);
        }
    }

    private static OverviewSnapshot BuildSnapshot()
    {
        var sw = Stopwatch.StartNew();
        long p0 = sw.ElapsedMilliseconds;
        string windows = GetWindows();
        long p1 = sw.ElapsedMilliseconds;
        HardwareIdentity identity = GetHardwareIdentity();
        long p2 = sw.ElapsedMilliseconds;
        string gpu = GetGpuNameFast();
        long p3 = sw.ElapsedMilliseconds;
        string ram = GetRam();
        long p4 = sw.ElapsedMilliseconds;
        string disk = GetDisk();
        long p5 = sw.ElapsedMilliseconds;
        string network = GetNetwork();
        long p6 = sw.ElapsedMilliseconds;
        string power = GetPowerPlan();
        long p7 = sw.ElapsedMilliseconds;
        LogService.Info("performance", $"OVERVIEW_SNAPSHOT_PHASE windowsMs={p1-p0} identityMs={p2-p1} gpuMs={p3-p2} ramMs={p4-p3} diskMs={p5-p4} networkMs={p6-p5} powerMs={p7-p6} totalMs={p7}");
        return new OverviewSnapshot(windows, identity.Cpu, gpu, ram, identity.Ram, identity.Motherboard, disk, network, power);
    }
    private static string GetWindows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = key?.GetValue("ProductName")?.ToString() ?? "Windows";
            string buildText = key?.GetValue("CurrentBuildNumber")?.ToString() ?? Environment.OSVersion.Version.Build.ToString();
            int build = int.TryParse(buildText, out int parsedBuild) ? parsedBuild : Environment.OSVersion.Version.Build;
            if (build >= 22000 && product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            else if (build >= 22000 && !product.Contains("Windows 11", StringComparison.OrdinalIgnoreCase))
                product = "Windows 11 " + product.Replace("Microsoft", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("Windows", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            string display = key?.GetValue("DisplayVersion")?.ToString() ?? string.Empty;
            string ubr = key?.GetValue("UBR")?.ToString() ?? string.Empty;
            string fullBuild = string.IsNullOrWhiteSpace(ubr) ? buildText : buildText + "." + ubr;
            return string.IsNullOrWhiteSpace(display) ? $"{product} • build {fullBuild}" : $"{product} • {display} • build {fullBuild}";
        }
        catch
        {
            int build = Environment.OSVersion.Version.Build;
            return build >= 22000 ? $"Windows 11 • build {build}" : Environment.OSVersion.VersionString;
        }
    }

    private static HardwareIdentity GetHardwareIdentity()
    {
        lock (HardwareIdentityLock)
        {
            if (_hardwareIdentity != null) return _hardwareIdentity;

            var persistedSw = Stopwatch.StartNew();
            if (PersistentHardwareCacheService.TryReadMachineStable<HardwareIdentity>(
                    HardwareIdentityPersistentKey, HardwareIdentityPersistentTtl, out var persisted) &&
                persisted != null)
            {
                _hardwareIdentity = persisted;
                LogService.Info("performance", $"HARDWARE_IDENTITY_PERSISTENT_HIT ms={persistedSw.ElapsedMilliseconds}");
                return _hardwareIdentity;
            }

            var built = BuildHardwareIdentity();
            PersistentHardwareCacheService.WriteMachineStable(HardwareIdentityPersistentKey, built);
            _hardwareIdentity = built;
            return _hardwareIdentity;
        }
    }

    private static HardwareIdentity BuildHardwareIdentity()
    {
        string cpu = GetCpu();
        string motherboard = "Základní deska neuvedena";
        string ramHardware = "RAM hardware neuveden";

        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2");
            scope.Options.Timeout = TimeSpan.FromSeconds(2);
            scope.Connect();

            try
            {
                using var boardSearcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Manufacturer,Product FROM Win32_BaseBoard"),
                    new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = false, Rewindable = false });
                foreach (ManagementObject board in boardSearcher.Get())
                {
                    motherboard = FormatMotherboard(Convert.ToString(board["Manufacturer"]), Convert.ToString(board["Product"]));
                    break;
                }
            }
            catch (Exception ex) { LogService.Error("hardware", "Baseboard identity probe failed", ex); }

            try
            {
                var modules = new List<RamModuleInfo>();
                using var ramSearcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Manufacturer,PartNumber,Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType FROM Win32_PhysicalMemory"),
                    new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = false, Rewindable = false });
                foreach (ManagementObject module in ramSearcher.Get())
                {
                    modules.Add(new RamModuleInfo(
                        ToUInt64(module["Capacity"]),
                        ToUInt32(module["Speed"]),
                        ToUInt32(module["ConfiguredClockSpeed"]),
                        ToUInt16(module["SMBIOSMemoryType"]),
                        CleanText(Convert.ToString(module["Manufacturer"])),
                        CleanText(Convert.ToString(module["PartNumber"]))));
                }
                if (modules.Count > 0) ramHardware = FormatRamHardware(modules);
            }
            catch (Exception ex) { LogService.Error("hardware", "RAM identity probe failed", ex); }
        }
        catch (Exception ex) { LogService.Error("hardware", "Hardware identity WMI connection failed", ex); }

        LogService.Info("hardware", $"HARDWARE_IDENTITY cpu={cpu} board={motherboard} ram={ramHardware}");
        return new HardwareIdentity(cpu, motherboard, ramHardware);
    }

    private static string GetCpu()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string value = CleanText(key?.GetValue("ProcessorNameString")?.ToString());
            return string.IsNullOrWhiteSpace(value) ? "CPU neuvedeno" : value;
        }
        catch { return "CPU neuvedeno"; }
    }

    private static string FormatMotherboard(string? manufacturer, string? product)
    {
        string maker = CleanText(manufacturer);
        string model = CleanText(product);
        if (IsPlaceholder(maker)) maker = string.Empty;
        if (IsPlaceholder(model)) model = string.Empty;
        if (maker.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase)) maker = "GIGABYTE";
        else if (maker.Contains("ASUSTeK", StringComparison.OrdinalIgnoreCase)) maker = "ASUS";
        else if (maker.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase)) maker = "MSI";
        else if (maker.Contains("ASRock", StringComparison.OrdinalIgnoreCase)) maker = "ASRock";

        if (!string.IsNullOrWhiteSpace(maker) && model.StartsWith(maker, StringComparison.OrdinalIgnoreCase))
            maker = string.Empty;
        string result = CleanText(maker + " " + model);
        return string.IsNullOrWhiteSpace(result) ? "Základní deska neuvedena" : result;
    }

    private static string FormatRamHardware(IReadOnlyList<RamModuleInfo> modules)
    {
        double totalGb = modules.Sum(x => (double)x.Capacity) / 1073741824d;
        uint clock = modules.Select(x => x.ConfiguredSpeed > 0 ? x.ConfiguredSpeed : x.Speed)
            .Where(x => x > 0).GroupBy(x => x).OrderByDescending(x => x.Count()).ThenByDescending(x => x.Key)
            .Select(x => x.Key).FirstOrDefault();
        ushort memoryType = modules.Where(x => x.MemoryType > 0).GroupBy(x => x.MemoryType)
            .OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();
        string type = MemoryTypeName(memoryType);

        string[] parts = modules.Select(x => CleanText(x.PartNumber)).Where(x => !IsPlaceholder(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string part = parts.Length == 1 ? parts[0] : string.Join(" / ", parts.Take(2));

        string[] vendors = modules.Select(x => NormalizeMemoryVendor(x.Manufacturer, x.PartNumber))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string vendor = vendors.Length == 1 ? vendors[0] : string.Join(" / ", vendors.Take(2));

        string speedText = clock > 0 ? (type == "RAM" ? $"{clock} MHz" : $"{type}-{clock}") : type;
        string identity = CleanText(vendor + " " + part);
        string moduleText = string.Empty;
        if (modules.Count > 1 && modules.All(x => x.Capacity == modules[0].Capacity))
            moduleText = $"{modules.Count}×{modules[0].Capacity / 1073741824d:0.#} GB";

        var pieces = new[] { $"{totalGb:0.#} GB {speedText}", identity, moduleText }
            .Where(x => !string.IsNullOrWhiteSpace(x));
        return string.Join(" • ", pieces);
    }

    private static string NormalizeMemoryVendor(string manufacturer, string partNumber)
    {
        string maker = CleanText(manufacturer);
        string part = CleanText(partNumber);
        if (!IsPlaceholder(maker) && !maker.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return maker;
        if (part.StartsWith("F4-", StringComparison.OrdinalIgnoreCase) || part.StartsWith("F5-", StringComparison.OrdinalIgnoreCase))
            return "G.SKILL";
        if (part.StartsWith("CM", StringComparison.OrdinalIgnoreCase)) return "Corsair";
        if (part.StartsWith("KF", StringComparison.OrdinalIgnoreCase) || part.StartsWith("KHX", StringComparison.OrdinalIgnoreCase))
            return "Kingston";
        if (part.StartsWith("CT", StringComparison.OrdinalIgnoreCase)) return "Crucial";
        return string.Empty;
    }

    private static string MemoryTypeName(ushort type) => type switch
    {
        20 => "DDR",
        21 or 22 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "RAM"
    };

    private static string CleanText(string? value) =>
        string.Join(" ", (value ?? string.Empty).Split(CleanTextSeparators, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsPlaceholder(string? value)
    {
        string text = CleanText(value);
        return string.IsNullOrWhiteSpace(text) ||
               text.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("Not Specified", StringComparison.OrdinalIgnoreCase);
    }

    private static ulong ToUInt64(object? value) { try { return Convert.ToUInt64(value ?? 0); } catch { return 0; } }
    private static uint ToUInt32(object? value) { try { return Convert.ToUInt32(value ?? 0); } catch { return 0; } }
    private static ushort ToUInt16(object? value) { try { return Convert.ToUInt16(value ?? 0); } catch { return 0; } }
    public static long GetTotalPhysicalMemoryMb()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return 0;
            return (long)(status.TotalPhys / 1048576UL);
        }
        catch { return 0; }
    }

    public static string GetLiveRamSummary() => GetRam();

    private static string GetRam()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return "RAM neuvedena";
            double total = status.TotalPhys / 1073741824d;
            double free = status.AvailPhys / 1073741824d;
            double used = Math.Max(0, total - free);
            return $"{used:0.0} / {total:0.0} GB • {status.MemoryLoad}% využito";
        }
        catch { return "RAM neuvedena"; }
    }

    public static string GetLiveDiskSummary() => GetDisk();

    private static string GetDisk()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            double total = drive.TotalSize / 1073741824d;
            double free = drive.AvailableFreeSpace / 1073741824d;
            return $"{drive.Name} {total - free:0} / {total:0} GB • {free:0} GB volno";
        }
        catch { return "Disk neuveden"; }
    }
    internal static void QueueNetworkSummaryWarmup()
    {
        _ = Task.Run(static () => _ = GetNetwork());
    }

    private static string GetNetwork()
    {
        lock (NetworkSummaryLock)
        {
            if (_networkSummary != null && DateTime.UtcNow - _networkSummaryAt < NetworkSummaryTtl)
                return _networkSummary;

            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(x => x.OperationalStatus == OperationalStatus.Up &&
                        x.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        x.NetworkInterfaceType != NetworkInterfaceType.Tunnel);
                if (nic == null)
                    _networkSummary = "Síť odpojena";
                else
                {
                    double mbps = nic.Speed > 0 ? nic.Speed / 1_000_000d : 0;
                    _networkSummary = mbps > 0 ? $"{nic.Name} • {mbps:0} Mb/s" : nic.Name;
                }
            }
            catch
            {
                _networkSummary = "Síť neuvedena";
            }

            _networkSummaryAt = DateTime.UtcNow;
            return _networkSummary;
        }
    }

    private static string GetPowerPlan()
    {
        try { return NativeCore.GetActivePowerSchemeName(); }
        catch { return "Power plan neuveden"; }
    }


    private static string GetGpuNameFast()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            if (root != null)
            {
                foreach (string adapter in root.GetSubKeyNames())
                    foreach (string child in GpuRegistryChildren)
                    {
                        using var key = root.OpenSubKey(adapter + "\\" + child);
                        string? name = key?.GetValue("DriverDesc")?.ToString();
                        if (!string.IsNullOrWhiteSpace(name) && !name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) return name.Trim();
                    }
            }
        }
        catch { }
        return GetWmiValue("Win32_VideoController", "Name", "GPU neuvedeno");
    }

    private static string GetWmiValue(string cls, string prop, string fallback)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
            return searcher.Get().Cast<ManagementObject>()
                .Select(x => Convert.ToString(x[prop]))
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? fallback;
        }
        catch { return fallback; }
    }
}
