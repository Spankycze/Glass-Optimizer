using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record MtuAdapterInfo(
    string Name,
    string Description,
    int Ipv4Mtu,
    int Ipv6Mtu,
    long Speed,
    string? Gateway);

public sealed record MtuProbeResult(
    MtuAdapterInfo Adapter,
    string Target,
    int CurrentMtu,
    int RecommendedMtu,
    bool Conclusive,
    string Detail);

internal sealed record MtuRollbackSnapshot(DateTime CapturedAtUtc, string AdapterName, int Ipv4Mtu, int Ipv6Mtu);

public static class MtuOptimizerService
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private const ushort AfInet = 2;
    private const ushort AfInet6 = 23;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpInterfaceRow
    {
        public ushort Family;
        private ushort _familyPad1;
        private uint _familyPad2;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;
        [MarshalAs(UnmanagedType.U1)] public bool AdvertisingEnabled;
        [MarshalAs(UnmanagedType.U1)] public bool ForwardingEnabled;
        [MarshalAs(UnmanagedType.U1)] public bool WeakHostSend;
        [MarshalAs(UnmanagedType.U1)] public bool WeakHostReceive;
        [MarshalAs(UnmanagedType.U1)] public bool UseAutomaticMetric;
        [MarshalAs(UnmanagedType.U1)] public bool UseNeighborUnreachabilityDetection;
        [MarshalAs(UnmanagedType.U1)] public bool ManagedAddressConfigurationSupported;
        [MarshalAs(UnmanagedType.U1)] public bool OtherStatefulConfigurationSupported;
        [MarshalAs(UnmanagedType.U1)] public bool AdvertiseDefaultRoute;
        private byte _boolPad1;
        private byte _boolPad2;
        private byte _boolPad3;
        public int RouterDiscoveryBehavior;
        public uint DadTransmits;
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;
        public int LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public uint[] ZoneIndices;
        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;
        [MarshalAs(UnmanagedType.U1)] public bool Connected;
        [MarshalAs(UnmanagedType.U1)] public bool SupportsWakeUpPatterns;
        [MarshalAs(UnmanagedType.U1)] public bool SupportsNeighborDiscovery;
        [MarshalAs(UnmanagedType.U1)] public bool SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload;
        public byte ReceiveOffload;
        [MarshalAs(UnmanagedType.U1)] public bool DisableDefaultRoutes;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpInterfaceEntry(ref MibIpInterfaceRow row);

    private static int TryGetNativeNlMtu(int interfaceIndex, ushort family)
    {
        if (interfaceIndex <= 0) return 0;
        try
        {
            var row = new MibIpInterfaceRow
            {
                Family = family,
                InterfaceIndex = (uint)interfaceIndex,
                ZoneIndices = new uint[16]
            };
            uint error = GetIpInterfaceEntry(ref row);
            if (error != 0 || row.NlMtu < 576 || row.NlMtu > 9000) return 0;
            return checked((int)row.NlMtu);
        }
        catch
        {
            return 0;
        }
    }

    private static readonly string RollbackPath = Path.Combine(LogService.Root, "mtu-rollback.json");
    private static readonly object RecommendationCacheLock = new();
    private static string _recommendationCacheKey = string.Empty;
    private static MtuProbeResult? _recommendationCache;
    private static DateTime _recommendationCacheAtUtc;
    private static readonly TimeSpan RecommendationCacheTtl = TimeSpan.FromMinutes(10);
    public static bool HasRollbackSnapshot => File.Exists(RollbackPath);

    private static bool IsUsable(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up) return false;
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
        string text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        string[] blocked = { "vpn", "tap", "tun", "hyper-v", "virtual", "loopback", "wireguard", "wintun", "wfp", "lightweight filter", "filter-", "vethernet", "bluetooth" };
        return !blocked.Any(text.Contains);
    }

    private static MtuAdapterInfo? ToInfo(NetworkInterface nic)
    {
        try
        {
            var props = nic.GetIPProperties();
            int ipv4 = 0;
            int ipv6 = 0;
            try
            {
                var v4 = props.GetIPv4Properties();
                if (v4 != null)
                    ipv4 = TryGetNativeNlMtu(v4.Index, AfInet);
                if (ipv4 <= 0)
                    ipv4 = v4?.Mtu ?? 0;
            }
            catch { }
            try
            {
                var v6 = props.GetIPv6Properties();
                if (v6 != null)
                    ipv6 = TryGetNativeNlMtu(v6.Index, AfInet6);
                if (ipv6 <= 0)
                    ipv6 = v6?.Mtu ?? 0;
            }
            catch { }
            string? gateway = props.GatewayAddresses
                .Select(x => x.Address)
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(x))
                ?.ToString();
            return new(nic.Name, nic.Description, ipv4, ipv6, nic.Speed, gateway);
        }
        catch { return null; }
    }
    public static Task<MtuAdapterInfo> GetActiveAdapterAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        FaultInjectionService.ThrowIfInjected("network.adapter-enumeration");

        var info = NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsUsable)
            .Select(ToInfo)
            .Where(x => x != null)
            .Cast<MtuAdapterInfo>()
            .Where(x => !string.IsNullOrWhiteSpace(x.Gateway))
            .OrderByDescending(x => x.Speed)
            .FirstOrDefault();

        info ??= NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsUsable)
            .Select(ToInfo)
            .Where(x => x != null)
            .Cast<MtuAdapterInfo>()
            .OrderByDescending(x => x.Speed)
            .FirstOrDefault();

        if (info == null)
            throw new InvalidOperationException("Nebyl nalezen aktivní fyzický síťový adaptér.");

        return Task.FromResult(info);
    }

    private static MtuAdapterInfo? GetAdapterByName(string adapterName)
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(x => x.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase)) is { } nic
            ? ToInfo(nic)
            : null;
    }

    private static string[] ReadDnsFingerprint(string adapterName)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(x => x.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
            if (nic == null) return Array.Empty<string>();
            return nic.GetIPProperties().DnsAddresses
                .Select(x => x.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static async Task<bool> CanReachInternetAsync(CancellationToken token)
    {
        await FaultInjectionService.InjectAsync("network.connectivity", token).ConfigureAwait(false);
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(IPAddress.Parse("1.1.1.1"), 443, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch { return false; }
    }

    private static async Task<MtuAdapterInfo> ReadBackAsync(
        MtuAdapterInfo before,
        int expectedIpv4,
        int? expectedIpv6,
        CancellationToken token)
    {
        int[] delays = { 80, 180, 350, 700 };
        MtuAdapterInfo? last = null;
        foreach (int delay in delays)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(delay, token).ConfigureAwait(false);
            last = GetAdapterByName(before.Name);
            if (last == null) continue;
            bool ipv4Ok = last.Ipv4Mtu == expectedIpv4;
            bool ipv6Ok = !expectedIpv6.HasValue || last.Ipv6Mtu == expectedIpv6.Value;
            if (ipv4Ok && ipv6Ok) return last;
        }

        return last ?? throw new InvalidOperationException(
            $"Síťový adaptér '{before.Name}' po MTU změně zmizel.");
    }

    private static string BuildRecommendationCacheKey(MtuAdapterInfo adapter) =>
        $"{adapter.Name}|{adapter.Ipv4Mtu}|{adapter.Gateway}|{string.Join(",", ReadDnsFingerprint(adapter.Name))}";

    private static MtuProbeResult CacheRecommendation(string key, MtuProbeResult result)
    {
        lock (RecommendationCacheLock)
        {
            _recommendationCacheKey = key;
            _recommendationCache = result;
            _recommendationCacheAtUtc = DateTime.UtcNow;
        }
        return result;
    }

    public static void InvalidateRecommendationCache()
    {
        lock (RecommendationCacheLock)
        {
            _recommendationCacheKey = string.Empty;
            _recommendationCache = null;
            _recommendationCacheAtUtc = default;
        }
    }

    public static async Task<MtuProbeResult> DetectRecommendedAsync(CancellationToken token = default)
    {
        var adapter = await GetActiveAdapterAsync(token).ConfigureAwait(false);
        string cacheKey = BuildRecommendationCacheKey(adapter);
        lock (RecommendationCacheLock)
        {
            if (_recommendationCache != null &&
                string.Equals(_recommendationCacheKey, cacheKey, StringComparison.Ordinal) &&
                DateTime.UtcNow - _recommendationCacheAtUtc < RecommendationCacheTtl)
            {
                LogService.Info("network", $"MTU_RECOMMENDATION_CACHE_HIT adapter={adapter.Name} mtu={_recommendationCache.RecommendedMtu}");
                return _recommendationCache;
            }
        }

        string[] targets = { "1.1.1.1", "8.8.8.8", "9.9.9.9" };
        var results = new List<MtuProbeResult>(targets.Length);

        foreach (string target in targets)
        {
            token.ThrowIfCancellationRequested();
            var result = await ProbeTargetAsync(adapter, target, token).ConfigureAwait(false);
            if (result.Conclusive) results.Add(result);
        }

        if (results.Count < 2)
            return CacheRecommendation(cacheKey,
                new(adapter, string.Join(", ", targets), adapter.Ipv4Mtu, adapter.Ipv4Mtu, false,
                    $"Path-MTU byl průkazný jen na {results.Count}/{targets.Length} cílech. Automatická změna se z bezpečnostních důvodů neprovede."));

        int min = results.Min(x => x.RecommendedMtu);
        int max = results.Max(x => x.RecommendedMtu);
        if (max - min > 64)
            return CacheRecommendation(cacheKey,
                new(adapter, string.Join(", ", results.Select(x => x.Target)), adapter.Ipv4Mtu, adapter.Ipv4Mtu, false,
                    $"Výsledky se příliš liší ({min}–{max}). Síť může používat rozdílné cesty; MTU zůstalo beze změny."));

        int recommended = min;
        string summary = string.Join(" • ", results.Select(x => $"{x.Target}: {x.RecommendedMtu}"));
        return CacheRecommendation(cacheKey,
            new(adapter, string.Join(", ", results.Select(x => x.Target)), adapter.Ipv4Mtu, recommended, true,
                $"Vícecestná detekce {results.Count}/{targets.Length}: {summary}. Použita bezpečná nejnižší potvrzená hodnota {recommended}."));
    }

    private static async Task<MtuProbeResult> ProbeTargetAsync(MtuAdapterInfo adapter, string target, CancellationToken token)
    {
        const int minPayload = 548;
        const int maxPayload = 1472;
        int low = minPayload;
        int high = maxPayload;
        int best = -1;
        int replies = 0;

        IPAddress? targetAddress = null;
        if (IPAddress.TryParse(target, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            targetAddress = parsed;
        }
        else
        {
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(target, token).ConfigureAwait(false);
                targetAddress = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        if (targetAddress == null)
            return new(adapter, target, adapter.Ipv4Mtu, adapter.Ipv4Mtu, false,
                $"Cíl {target} nemá dostupnou IPv4 adresu pro DF MTU test.");

        while (low <= high)
        {
            token.ThrowIfCancellationRequested();
            int mid = low + ((high - low) / 2);
            bool success = await ProbeNativeDfPayloadAsync(targetAddress, mid, token).ConfigureAwait(false);

            if (success)
            {
                replies++;
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        if (best < 0 || replies == 0)
            return new(adapter, target, adapter.Ipv4Mtu, adapter.Ipv4Mtu, false,
                $"Cíl {target} neumožnil spolehlivý DF MTU test.");

        int mtu = Math.Clamp(best + 28, 576, 1500);
        return new(adapter, target, adapter.Ipv4Mtu, mtu, true,
            $"Největší potvrzený DF payload {best} B → MTU {mtu}.");
    }

    private static async Task<bool> ProbeNativeDfPayloadAsync(IPAddress target, int payloadBytes, CancellationToken token)
    {
        await FaultInjectionService.InjectAsync("network.mtu-probe", token).ConfigureAwait(false);
        try
        {
            using var ping = new Ping();
            var options = new PingOptions(64, true);
            byte[] buffer = new byte[payloadBytes];
            PingReply reply = await ping.SendPingAsync(target, 1200, buffer, options)
                .WaitAsync(TimeSpan.FromSeconds(2), token)
                .ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (PingException)
        {
            return false;
        }
    }

    private static async Task EnsureRollbackSnapshotAsync(MtuAdapterInfo adapter, CancellationToken token)
    {
        Directory.CreateDirectory(LogService.Root);
        if (File.Exists(RollbackPath))
        {
            try
            {
                string existingJson = await File.ReadAllTextAsync(RollbackPath, token).ConfigureAwait(false);
                var existing = JsonSerializer.Deserialize<MtuRollbackSnapshot>(existingJson);
                if (existing != null && existing.AdapterName.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch { }
        }

        var snapshot = new MtuRollbackSnapshot(DateTime.UtcNow, adapter.Name, adapter.Ipv4Mtu, adapter.Ipv6Mtu);
        await File.WriteAllTextAsync(
            RollbackPath,
            JsonSerializer.Serialize(snapshot, IndentedJson),
            token).ConfigureAwait(false);
    }

    public static async Task<MtuAdapterInfo> RestoreOriginalAsync(CancellationToken token = default)
    {
        if (!File.Exists(RollbackPath))
            throw new InvalidOperationException("Není uložený původní MTU stav.");

        string json = await File.ReadAllTextAsync(RollbackPath, token).ConfigureAwait(false);
        var snapshot = JsonSerializer.Deserialize<MtuRollbackSnapshot>(json)
            ?? throw new InvalidOperationException("MTU rollback snapshot je neplatný.");

        var current = GetAdapterByName(snapshot.AdapterName)
            ?? throw new InvalidOperationException($"Adaptér '{snapshot.AdapterName}' už není aktivní.");

        if (snapshot.Ipv4Mtu > 0)
        {
            var v4 = await ProcessRunner.RunAsync(
                "netsh.exe",
                $"interface ipv4 set subinterface \"{snapshot.AdapterName}\" mtu={snapshot.Ipv4Mtu} store=persistent",
                TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            if (!v4.Success)
                throw new InvalidOperationException(("Obnova IPv4 MTU selhala: " + v4.StdErr + " " + v4.StdOut).Trim());
        }

        bool restoreIpv6 = snapshot.Ipv6Mtu >= 1280;
        if (restoreIpv6)
        {
            var v6 = await ProcessRunner.RunAsync(
                "netsh.exe",
                $"interface ipv6 set subinterface \"{snapshot.AdapterName}\" mtu={snapshot.Ipv6Mtu} store=persistent",
                TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            if (!v6.Success)
                throw new InvalidOperationException(("Obnova IPv6 MTU selhala: " + v6.StdErr + " " + v6.StdOut).Trim());
        }

        var after = await ReadBackAsync(current, snapshot.Ipv4Mtu, restoreIpv6 ? snapshot.Ipv6Mtu : null, token).ConfigureAwait(false);
        if (after.Ipv4Mtu != snapshot.Ipv4Mtu || (restoreIpv6 && after.Ipv6Mtu != snapshot.Ipv6Mtu))
            throw new InvalidOperationException("Read-back nepotvrdil obnovený původní MTU stav.");

        InvalidateRecommendationCache();
        LogService.Info("network", $"MTU_RESTORE_OK adapter={after.Name} ipv4={after.Ipv4Mtu} ipv6={after.Ipv6Mtu}");
        return after;
    }

    public static async Task<MtuAdapterInfo> ApplyAsync(
        int mtu,
        bool applyIpv6 = false,
        CancellationToken token = default)
    {
        if (mtu < 576 || mtu > 9000)
            throw new ArgumentOutOfRangeException(nameof(mtu), "MTU musí být v rozsahu 576–9000.");

        var before = await GetActiveAdapterAsync(token).ConfigureAwait(false);
        await EnsureRollbackSnapshotAsync(before, token).ConfigureAwait(false);
        string name = before.Name;
        string[] dnsBefore = ReadDnsFingerprint(name);
        bool hadConnectivity = await CanReachInternetAsync(token).ConfigureAwait(false);

        async Task RollbackAsync()
        {
            try
            {
                if (before.Ipv4Mtu > 0)
                    await ProcessRunner.RunAsync("netsh.exe",
                        $"interface ipv4 set subinterface \"{name}\" mtu={before.Ipv4Mtu} store=persistent",
                        TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
                if (applyIpv6 && before.Ipv6Mtu >= 1280)
                    await ProcessRunner.RunAsync("netsh.exe",
                        $"interface ipv6 set subinterface \"{name}\" mtu={before.Ipv6Mtu} store=persistent",
                        TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
                LogService.Info("network", $"MTU_AUTO_ROLLBACK_OK adapter={name} ipv4={before.Ipv4Mtu} ipv6={before.Ipv6Mtu}");
            }
            catch (Exception ex) { LogService.Error("network", "MTU auto rollback failed", ex); }
        }

        try
        {
            var ipv4 = await ProcessRunner.RunAsync(
                "netsh.exe",
                $"interface ipv4 set subinterface \"{name}\" mtu={mtu} store=persistent",
                TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            if (!ipv4.Success)
                throw new InvalidOperationException(("IPv4 MTU apply selhal: " + ipv4.StdErr + " " + ipv4.StdOut).Trim());

            if (applyIpv6)
            {
                if (mtu < 1280)
                    throw new ArgumentOutOfRangeException(nameof(mtu), "IPv6 MTU nesmí být nižší než 1280.");
                var ipv6 = await ProcessRunner.RunAsync(
                    "netsh.exe",
                    $"interface ipv6 set subinterface \"{name}\" mtu={mtu} store=persistent",
                    TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                if (!ipv6.Success)
                    throw new InvalidOperationException(("IPv6 MTU apply selhal: " + ipv6.StdErr + " " + ipv6.StdOut).Trim());
            }

            var after = await ReadBackAsync(before, mtu, applyIpv6 ? mtu : null, token).ConfigureAwait(false);
            var activeAfter = await GetActiveAdapterAsync(token).ConfigureAwait(false);
            if (!string.Equals(activeAfter.Name, before.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Aktivní adaptér se během MTU změny změnil.");
            bool ipv4ClampedDown = after.Ipv4Mtu > 0 && after.Ipv4Mtu < mtu;
            if (after.Ipv4Mtu != mtu && !ipv4ClampedDown)
                throw new InvalidOperationException($"Read-back IPv4 MTU nepotvrdil {mtu}; aktuální hodnota je {after.Ipv4Mtu}.");
            if (applyIpv6 && after.Ipv6Mtu != mtu)
                throw new InvalidOperationException($"Read-back IPv6 MTU nepotvrdil {mtu}; aktuální hodnota je {after.Ipv6Mtu}.");

            string[] dnsAfter = ReadDnsFingerprint(name);
            if (!dnsBefore.SequenceEqual(dnsAfter, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("DNS se během MTU operace změnilo. MTU bude vráceno; DNS Optimizer touto operací neupravuje.");

            if (hadConnectivity && !await CanReachInternetAsync(token).ConfigureAwait(false))
                throw new InvalidOperationException("Po změně MTU se ztratila konektivita. Původní MTU bude automaticky obnoveno.");

            if (ipv4ClampedDown)
                LogService.Info("network", $"MTU_APPLY_CLAMPED adapter={after.Name} requested={mtu} accepted={after.Ipv4Mtu}");
            InvalidateRecommendationCache();
            LogService.Info("network", $"MTU_APPLY_OK adapter={after.Name} ipv4={after.Ipv4Mtu} ipv6={after.Ipv6Mtu} dnsInvariant=unchanged connectivity={(hadConnectivity ? "verified" : "baseline-offline")}");
            return after;
        }
        catch
        {
            await RollbackAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<string> RunReadbackSmokeAsync(CancellationToken token = default)
    {
        var adapter = await GetActiveAdapterAsync(token).ConfigureAwait(false);
        if (adapter.Ipv4Mtu <= 0) throw new InvalidOperationException("MTU smoke: IPv4 MTU nebylo načteno.");
        return $"MTU_SMOKE_OK adapter={adapter.Name} ipv4={adapter.Ipv4Mtu} ipv6={adapter.Ipv6Mtu}";
    }
}