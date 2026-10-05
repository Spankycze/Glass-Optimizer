using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record NicPropertyOptionState(
    string DisplayName,
    string CurrentValue,
    IReadOnlyList<string> ValidValues);

internal static class NativeNicTuningService
{
    private static readonly object ReadScopeLock = new();
    private static ManagementScope? _readScope;

    private static ManagementScope GetReadScope()
    {
        lock (ReadScopeLock)
        {
            if (_readScope == null || !_readScope.IsConnected)
            {
                _readScope = new ManagementScope(@"\\.\root\StandardCimv2");
                _readScope.Connect();
            }
            return _readScope;
        }
    }

    internal static void PrewarmReadScope()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = GetReadScope();
        long scopeMs = sw.ElapsedMilliseconds;

        string name = FindPrimaryAdapterNameFast();
        if (!string.IsNullOrWhiteSpace(name))
            _ = ReadCapabilities(CancellationToken.None);

        LogService.Info("performance",
            $"NIC_PROVIDER_PREWARM scopeMs={scopeMs} capabilityMs={sw.ElapsedMilliseconds - scopeMs} totalMs={sw.ElapsedMilliseconds}");
    }

    private static bool CanReachInternet()
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("1.1.1.1", 443);
            return task.Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch { return false; }
    }

    private static readonly string[] ReadbackTargets =
    {
        "Energy-Efficient Ethernet", "Energy Efficient Ethernet", "EEE", "Advanced EEE",
        "Green Ethernet", "Green Ethernet Lite", "Power Saving Mode", "System Idle Power Saver",
        "Ultra Low Power Mode", "Gigabit Lite", "Auto Disable Gigabit", "Receive Side Scaling",
        "RSS", "Reduce Speed On Power Down", "Interrupt Moderation", "Flow Control",
        "Packet Coalescing", "Large Send Offload v2 (IPv4)", "Large Send Offload v2 (IPv6)",
        "Receive Buffers", "Transmit Buffers"
    };
    private static readonly string[] ReceiveBufferNames = { "Receive Buffers", "ReceiveBuffers", "*ReceiveBuffers" };
    private static readonly string[] TransmitBufferNames = { "Transmit Buffers", "TransmitBuffers", "*TransmitBuffers" };
    private static readonly string[] InterruptModerationNames = { "Interrupt Moderation", "InterruptModeration" };
    private static readonly string[] FlowControlNames = { "Flow Control", "FlowControl" };
    private static readonly string[] PacketCoalescingNames = { "Packet Coalescing", "DMA Coalescing", "PacketCoalescing", "DMACoalescing" };
    private static readonly string[] EeeNames = { "Energy-Efficient Ethernet", "Energy Efficient Ethernet", "EEE", "Advanced EEE" };
    private static readonly string[] GreenEthernetNames = { "Green Ethernet", "Green Ethernet Lite", "EnableGreenEthernet" };
    private static readonly string[] PowerSavingNames = { "Power Saving Mode", "System Idle Power Saver", "Ultra Low Power Mode", "PowerSavingMode" };
    private static readonly string[] LsoIpv4Names = { "Large Send Offload v2 (IPv4)", "Large Send Offload V2 (IPv4)", "*LsoV2IPv4" };
    private static readonly string[] LsoIpv6Names = { "Large Send Offload v2 (IPv6)", "Large Send Offload V2 (IPv6)", "*LsoV2IPv6" };

    public static string ReadSummary(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var adapter = FindPrimaryAdapter() ?? throw new InvalidOperationException("Nebyl nalezen aktivní síťový adaptér.");
        string name = adapter["Name"]?.ToString() ?? string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine(name + " — " + (adapter["InterfaceDescription"]?.ToString() ?? string.Empty));
        foreach (var prop in ReadAdvancedProperties(name))
            if (ReadbackTargets.Contains(prop.DisplayName, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(prop.DisplayName + " = " + prop.DisplayValue);
        var rss = ReadRssState(name);
        sb.AppendLine("RSS = " + FormatRssState(rss));
        var rsc = ReadRscState(name);
        sb.AppendLine("RSC = " + FormatRscState(rsc));
        var selective = ReadSelectiveSuspend(name);
        sb.AppendLine("Selective Suspend = " + FormatSelectiveSuspendState(selective));
        return sb.ToString().TrimEnd();
    }

    public static string ReadDetailedSummary(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var adapter = FindPrimaryAdapter() ?? throw new InvalidOperationException("Nebyl nalezen aktivní síťový adaptér.");
        string name = adapter["Name"]?.ToString() ?? string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("Adapter: " + name);
        sb.AppendLine("Description: " + (adapter["InterfaceDescription"]?.ToString() ?? string.Empty));
        long receiveLink = ToLong(adapter["ReceiveLinkSpeed"]);
        long transmitLink = ToLong(adapter["TransmitLinkSpeed"]);
        sb.AppendLine("Link: " + FormatLink(Math.Max(receiveLink, transmitLink)));
        sb.AppendLine("Driver: " + (adapter["DriverVersionString"]?.ToString() ?? string.Empty));
        sb.AppendLine();
        sb.AppendLine("Advanced properties:");
        foreach (var prop in ReadAdvancedProperties(name))
            if (ReadbackTargets.Contains(prop.DisplayName, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(prop.DisplayName + " = " + prop.DisplayValue);
        var rss = ReadRssState(name);
        sb.AppendLine("RSS = " + FormatRssState(rss));
        var rsc = ReadRscState(name);
        sb.AppendLine("RSC = " + FormatRscState(rsc));
        var selective = ReadSelectiveSuspend(name);
        sb.AppendLine("Selective Suspend = " + FormatSelectiveSuspendState(selective));
        return sb.ToString().TrimEnd();
    }

    public static string SetPropertyEnabled(string[] names, bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var adapter = FindPrimaryAdapter() ?? throw new InvalidOperationException("Nebyl nalezen primární aktivní adaptér.");
        string adapterName = adapter["Name"]?.ToString() ?? string.Empty;
        var prop = FindProperty(ReadAdvancedProperties(adapterName), names);
        if (prop == null)
            throw new PlatformNotSupportedException($"Aktivní NIC driver vlastnost '{string.Join(" / ", names)}' nepodporuje.");

        string? target = enabled
            ? prop.ValidDisplayValues.FirstOrDefault(x => x.Equals("Rx & Tx Enabled", StringComparison.OrdinalIgnoreCase))
                ?? prop.ValidDisplayValues.FirstOrDefault(x => x.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                ?? prop.ValidDisplayValues.FirstOrDefault(x => x.Equals("On", StringComparison.OrdinalIgnoreCase))
                ?? prop.ValidDisplayValues.FirstOrDefault(x =>
                    !x.Contains("Disabled", StringComparison.OrdinalIgnoreCase) &&
                    !x.Equals("Off", StringComparison.OrdinalIgnoreCase) &&
                    !x.Equals("No", StringComparison.OrdinalIgnoreCase))
            : prop.ValidDisplayValues.FirstOrDefault(x => x.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                ?? prop.ValidDisplayValues.FirstOrDefault(x => x.Equals("Off", StringComparison.OrdinalIgnoreCase))
                ?? prop.ValidDisplayValues.FirstOrDefault(x => x.Equals("No", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException($"Driver nenabízí {(enabled ? "zapnutou" : "vypnutou")} hodnotu pro {prop.DisplayName}.");

        return SetProperty(names, target, token);
    }

    public static string SetProperty(string[] names, string value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var adapter = FindPrimaryAdapter() ?? throw new InvalidOperationException("Nebyl nalezen primární aktivní adaptér.");
        string adapterName = adapter["Name"]?.ToString() ?? string.Empty;
        InvalidateAdvancedPropertyCache();
        var prop = FindProperty(ReadAdvancedProperties(adapterName), names);
        if (prop == null)
            throw new PlatformNotSupportedException($"Aktivní NIC driver vlastnost '{string.Join(" / ", names)}' nepodporuje.");
        if (prop.DisplayValue.Equals(value, StringComparison.OrdinalIgnoreCase))
            return adapterName + " • " + prop.DisplayName + " = " + prop.DisplayValue;

        string oldDisplayValue = prop.DisplayValue;
        bool hadConnectivity = CanReachInternet();
        try
        {
            ApplyAdvancedPropertyWithCmdlet(adapterName, prop.DisplayName, value);
            InvalidateAdvancedPropertyCache();
            RestartAdapterWithCmdlet(adapterName);
            WaitForLink(adapterName, token);
            var verify = ReadAdvancedProperties(adapterName)
                .FirstOrDefault(x => x.DisplayName.Equals(prop.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (verify == null || !verify.DisplayValue.Equals(value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Read-back nepotvrdil změnu NIC vlastnosti.");
            if (hadConnectivity && !CanReachInternet())
                throw new InvalidOperationException("Po změně NIC vlastnosti se ztratila konektivita; změna bude automaticky vrácena.");
            return adapterName + " • " + prop.DisplayName + " = " + verify.DisplayValue;
        }
        catch
        {
            try
            {
                ApplyAdvancedPropertyWithCmdlet(adapterName, prop.DisplayName, oldDisplayValue);
                InvalidateAdvancedPropertyCache();
                RestartAdapterWithCmdlet(adapterName);
            }
            catch (Exception rollbackEx)
            {
                LogService.Error("nic", "NIC property rollback failed: " + prop.DisplayName, rollbackEx);
            }
            throw;
        }
    }

    public static NicPropertyOptionState? ReadPropertyOptions(string[] names, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var adapter = FindPrimaryAdapter() ?? throw new InvalidOperationException("Nebyl nalezen primární aktivní adaptér.");
        string adapterName = adapter["Name"]?.ToString() ?? string.Empty;
        var prop = FindProperty(ReadAdvancedProperties(adapterName), names);
        if (prop == null) return null;
        return new NicPropertyOptionState(
            prop.DisplayName,
            prop.DisplayValue,
            prop.ValidDisplayValues.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static string SetRscEnabled(bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var adapter = FindPrimaryAdapter() ?? throw new InvalidOperationException("Nebyl nalezen primární aktivní adaptér.");
        string adapterName = adapter["Name"]?.ToString() ?? string.Empty;
        var before = ReadRscState(adapterName);
        if (before.State != ProviderReadState.Supported)
            throw new PlatformNotSupportedException("RSC není tímto NIC driverem/providerem podporováno.");

        bool beforeEnabled = before.Ipv4Enabled || before.Ipv6Enabled;
        if (beforeEnabled == enabled)
            return $"{adapterName} • RSC = {FormatRscState(before)}";

        bool hadConnectivity = CanReachInternet();
        try
        {
            SetRscEnabledCore(adapterName, enabled);
            WaitForLink(adapterName, token);
            var verify = ReadRscState(adapterName);
            if (verify.State != ProviderReadState.Supported ||
                (verify.Ipv4Enabled || verify.Ipv6Enabled) != enabled)
                throw new InvalidOperationException("RSC read-back nepotvrdil požadovaný stav.");
            if (hadConnectivity && !CanReachInternet())
                throw new InvalidOperationException("Po změně RSC se ztratila konektivita.");
            return $"{adapterName} • RSC = {FormatRscState(verify)}";
        }
        catch
        {
            try { SetRscEnabledCore(adapterName, beforeEnabled); }
            catch (Exception rollbackEx) { LogService.Error("nic", "RSC rollback failed", rollbackEx); }
            throw;
        }
    }

    private static void SetRscEnabledCore(string name, bool enabled)
    {
        string escaped = name.Replace("'", "''");
        using var searcher = new ManagementObjectSearcher(@"\\.\root\StandardCimv2",
            $"SELECT * FROM MSFT_NetAdapterRscSettingData WHERE Name='{escaped}'");
        foreach (ManagementObject x in searcher.Get())
        {
            string method = enabled ? "Enable" : "Disable";
            using var output = x.InvokeMethod(method, null, null);
            uint code = output?["ReturnValue"] == null ? 0 : Convert.ToUInt32(output["ReturnValue"]);
            x.Dispose();
            if (code != 0) throw new InvalidOperationException("RSC změna selhala: " + code);
            InvalidateAdvancedPropertyCache();
            return;
        }
        throw new PlatformNotSupportedException("RSC provider není dostupný.");
    }

    public static string ApplyPerformanceProfile(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var perf = System.Diagnostics.Stopwatch.StartNew();

        string adapterName = FindPrimaryAdapterNameFast();
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            using var adapter = FindPrimaryAdapter() ?? throw new InvalidOperationException("Nebyl nalezen primární aktivní adaptér.");
            adapterName = adapter["Name"]?.ToString() ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(adapterName))
            throw new InvalidOperationException("Primární aktivní adaptér nemá platný název.");
        long adapterMs = perf.ElapsedMilliseconds;

        InvalidateAdvancedPropertyCache();
        var propertiesSw = System.Diagnostics.Stopwatch.StartNew();
        List<NicProperty> properties = ReadAdvancedProperties(adapterName);
        long propertiesMs = propertiesSw.ElapsedMilliseconds;
        var log = new List<string> { "Adapter: " + adapterName };
        var changed = new List<(NicProperty Prop, string[] OldValues, string TargetDisplay)>();
        bool? hadConnectivity = null;
        bool ConnectivityBeforeMutation()
        {
            if (!hadConnectivity.HasValue)
                hadConnectivity = CanReachInternet();
            return hadConnectivity.Value;
        }

        long advancedStartMs = perf.ElapsedMilliseconds;
        var targets = new (string Label, string[] Names, string Value)[]
        {
            ("Energy-Efficient Ethernet", new[]{"Energy-Efficient Ethernet","Energy Efficient Ethernet","EEE","Advanced EEE"}, "Disabled"),
            ("Green Ethernet", new[]{"Green Ethernet","Green Ethernet Lite","EnableGreenEthernet"}, "Disabled"),
            ("Power Saving Mode", new[]{"Power Saving Mode","System Idle Power Saver","Ultra Low Power Mode","PowerSavingMode"}, "Disabled"),
            ("Gigabit Lite", new[]{"Gigabit Lite","Auto Disable Gigabit","GigaLite"}, "Disabled"),
            ("Reduce Speed On Power Down", new[]{"Reduce Speed On Power Down","ReduceSpeedOnPowerDown"}, "Disabled")
        };

        try
        {
            foreach (var target in targets)
            {
                token.ThrowIfCancellationRequested();
                var matches = FindProperties(properties, target.Names);
                if (matches.Count == 0)
                {
                    log.Add(target.Label + " = Unsupported by driver");
                    continue;
                }

                foreach (var prop in matches)
                {
                    if (prop.DisplayValue.Equals(target.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        log.Add(prop.DisplayName + " = " + prop.DisplayValue + " (already)");
                        continue;
                    }

                    string registryValue = ResolveRegistryValue(prop, target.Value);
                    string[] oldValues = prop.RegistryValues.ToArray();
                    _ = ConnectivityBeforeMutation();
                    ApplyRegistryValue(prop.Path, registryValue);
                    InvalidateAdvancedPropertyCache();
                    changed.Add((prop, oldValues, target.Value));
                    log.Add(prop.DisplayName + " -> " + target.Value);
                }
            }

            if (changed.Count > 0)
            {
                RestartAdapter(adapterName);
                WaitForLink(adapterName, token);
                var verifyProperties = ReadAdvancedProperties(adapterName);
                foreach (var change in changed)
                {
                    var verify = FindProperty(verifyProperties, new[] { change.Prop.DisplayName, change.Prop.RegistryKeyword });
                    if (verify == null || !verify.DisplayValue.Equals(change.TargetDisplay, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Read-back nepotvrdil NIC změnu: " + change.Prop.DisplayName);
                }
                if (hadConnectivity == true && !CanReachInternet())
                    throw new InvalidOperationException("Po NIC Recommended se ztratila konektivita; driver změny budou automaticky vráceny.");
                log.Add($"Adapter restart = OK ({changed.Count} změn jedním restartem)");
            }
            else
            {
                log.Add("Adapter restart = nebyl potřeba");
            }
        }
        catch
        {
            foreach (var change in changed)
            {
                try
                {
                    ApplyRegistryValues(change.Prop.Path, change.OldValues);
                    InvalidateAdvancedPropertyCache();
                }
                catch (Exception rollbackEx) { LogService.Error("nic", "NIC batch rollback write failed: " + change.Prop.DisplayName, rollbackEx); }
            }
            if (changed.Count > 0)
            {
                try { RestartAdapter(adapterName); }
                catch (Exception rollbackEx) { LogService.Error("nic", "NIC batch rollback restart failed", rollbackEx); }
            }
            throw;
        }

        long advancedMs = perf.ElapsedMilliseconds - advancedStartMs;
        var rssSw = System.Diagnostics.Stopwatch.StartNew();
        var rss = ReadRssState(adapterName);
        long rssMs = rssSw.ElapsedMilliseconds;
        if (rss.State == ProviderReadState.Error)
        {
            log.Add("RSS = Detection failed: " + rss.ErrorMessage);
        }
        else if (rss.State == ProviderReadState.Unsupported)
        {
            log.Add("RSS = Unsupported by driver/provider");
        }
        else if (rss.Enabled)
        {
            log.Add("RSS = Enabled (already)");
        }
        else
        {
            bool rssChanged = false;
            try
            {
                _ = ConnectivityBeforeMutation();
                SetRssEnabled(adapterName, true);
                rssChanged = true;
                var verify = ReadRssState(adapterName);
                if (verify.State == ProviderReadState.Error)
                    throw new InvalidOperationException("RSS read-back selhal: " + verify.ErrorMessage);
                if (verify.State != ProviderReadState.Supported || !verify.Enabled)
                    throw new InvalidOperationException("RSS read-back nepotvrdil Enabled.");
                if (hadConnectivity == true && !CanReachInternet())
                    throw new InvalidOperationException("Po zapnutí RSS se ztratila konektivita.");
                log.Add("RSS = Enabled");
            }
            catch (Exception ex)
            {
                if (rssChanged)
                {
                    try { SetRssEnabled(adapterName, rss.Enabled); }
                    catch (Exception rollbackEx) { LogService.Error("nic", "RSS rollback failed", rollbackEx); }
                }
                log.Add("RSS = Failed: " + ex.Message);
                LogService.Error("nic", "RSS enable failed", ex);
            }
        }


        var selectiveSw = System.Diagnostics.Stopwatch.StartNew();
        var selective = ReadSelectiveSuspend(adapterName);
        long selectiveReadMs = selectiveSw.ElapsedMilliseconds;
        if (selective.State == ProviderReadState.Error)
        {
            log.Add("Selective Suspend = Detection failed: " + selective.ErrorMessage);
        }
        else if (selective.State == ProviderReadState.Unsupported)
        {
            log.Add("Selective Suspend = Unsupported by driver/provider");
        }
        else if (selective.Value.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            log.Add("Selective Suspend = Disabled (already)");
        }
        else
        {
            bool selectiveChanged = false;
            bool selectiveWasEnabled = selective.Value.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
            try
            {
                _ = ConnectivityBeforeMutation();
                SetSelectiveSuspend(false, token);
                selectiveChanged = true;
                var verify = ReadSelectiveSuspend(adapterName);
                if (verify.State != ProviderReadState.Supported ||
                    !verify.Value.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Selective Suspend read-back nepotvrdil Disabled.");
                if (hadConnectivity == true && !CanReachInternet())
                    throw new InvalidOperationException("Po vypnutí Selective Suspend se ztratila konektivita.");
                log.Add("Selective Suspend = " + FormatSelectiveSuspendState(verify));
            }
            catch (Exception ex)
            {
                if (selectiveChanged)
                {
                    try { SetSelectiveSuspend(selectiveWasEnabled, CancellationToken.None); }
                    catch (Exception rollbackEx) { LogService.Error("nic", "Selective Suspend rollback failed", rollbackEx); }
                }
                log.Add("Selective Suspend = Failed: " + ex.Message);
                LogService.Error("nic", "Selective Suspend disable failed", ex);
            }
        }

        LogService.Info("performance",
            $"NIC_PROFILE_PHASE adapterMs={adapterMs} propertiesMs={propertiesMs} advancedMs={advancedMs} rssMs={rssMs} selectiveMs={selectiveReadMs} connectivityProbed={hadConnectivity.HasValue} totalMs={perf.ElapsedMilliseconds}");
        return string.Join(Environment.NewLine, log);
    }

    private static ManagementObject? FindPrimaryAdapter()
    {
        using var searcher = new ManagementObjectSearcher(@"\\.\root\StandardCimv2",
            "SELECT Name,HardwareInterface,InterfaceDescription,ReceiveLinkSpeed,TransmitLinkSpeed,DriverVersionString FROM MSFT_NetAdapter WHERE MediaConnectState=1");
        foreach (ManagementObject x in searcher.Get())
            if (Convert.ToBoolean(x["HardwareInterface"] ?? true)) return x;
        return null;
    }

    private static string FindPrimaryAdapterNameFast()
    {
        NetworkInterface? best = null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;

            if (best == null || nic.Speed > best.Speed)
                best = nic;
        }
        return best?.Name ?? string.Empty;
    }
    private enum ProviderReadState
    {
        Supported,
        Unsupported,
        Error
    }

    private sealed record RssReadResult(ProviderReadState State, bool Enabled, string? ErrorMessage = null);
    private sealed record RscReadResult(ProviderReadState State, bool Ipv4Enabled, bool Ipv6Enabled, string? ErrorMessage = null);
    private sealed record SelectiveSuspendReadResult(ProviderReadState State, string Value, string? ErrorMessage = null);

    private sealed record NicProperty(string DisplayName, string DisplayValue, string RegistryKeyword, string Path,
        string[] RegistryValues, string[] ValidDisplayValues, string[] ValidRegistryValues);

    private static readonly object AdvancedPropertyCacheLock = new();
    private static readonly TimeSpan AdvancedPropertyCacheTtl = TimeSpan.FromSeconds(8);
    private static string _advancedPropertyCacheAdapter = string.Empty;
    private static DateTime _advancedPropertyCacheAt;
    private static List<NicProperty>? _advancedPropertyCache;

    private static readonly object CapabilityCacheLock = new();
    private static string _capabilityCacheAdapter = string.Empty;
    private static DateTime _capabilityCacheAt;
    private static IReadOnlyDictionary<string, string>? _capabilityCache;

    private static void InvalidateAdvancedPropertyCache()
    {
        lock (AdvancedPropertyCacheLock)
        {
            _advancedPropertyCache = null;
            _advancedPropertyCacheAdapter = string.Empty;
            _advancedPropertyCacheAt = default;
        }
        lock (CapabilityCacheLock)
        {
            _capabilityCache = null;
            _capabilityCacheAdapter = string.Empty;
            _capabilityCacheAt = default;
        }
    }

    private static List<NicProperty> ReadAdvancedProperties(string name)
    {
        lock (AdvancedPropertyCacheLock)
        {
            if (_advancedPropertyCache != null &&
                string.Equals(_advancedPropertyCacheAdapter, name, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - _advancedPropertyCacheAt <= AdvancedPropertyCacheTtl)
                return _advancedPropertyCache;
        }

        var list = new List<NicProperty>(32);
        string escaped = name.Replace("'", "''");
        using var searcher = new ManagementObjectSearcher(
            GetReadScope(),
            new ObjectQuery($"SELECT DisplayName,DisplayValue,RegistryKeyword,RegistryValue,ValidDisplayValues,ValidRegistryValues FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE Name='{escaped}'"));
        foreach (ManagementObject x in searcher.Get())
        {
            list.Add(new NicProperty(
                x["DisplayName"]?.ToString() ?? string.Empty,
                x["DisplayValue"]?.ToString() ?? string.Empty,
                x["RegistryKeyword"]?.ToString() ?? string.Empty,
                x.Path.Path,
                ToStrings(x["RegistryValue"]),
                ToStrings(x["ValidDisplayValues"]),
                ToStrings(x["ValidRegistryValues"])));
            x.Dispose();
        }

        lock (AdvancedPropertyCacheLock)
        {
            _advancedPropertyCache = list;
            _advancedPropertyCacheAdapter = name;
            _advancedPropertyCacheAt = DateTime.UtcNow;
        }
        return list;
    }

    private static List<NicProperty> FindProperties(List<NicProperty> properties, string[] names)
    {
        List<NicProperty>? matches = null;
        for (int i = 0; i < properties.Count; i++)
        {
            var prop = properties[i];
            for (int n = 0; n < names.Length; n++)
            {
                string name = names[n];
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!PropertyTokenEquals(prop.DisplayName, name) &&
                    !PropertyTokenEquals(prop.RegistryKeyword, name))
                    continue;

                (matches ??= new List<NicProperty>()).Add(prop);
                break;
            }
        }
        if (matches == null) return new List<NicProperty>(0);
        return matches;
    }

    private static NicProperty? FindProperty(List<NicProperty> properties, string[] names)
    {
        for (int i = 0; i < properties.Count; i++)
        {
            var prop = properties[i];
            for (int n = 0; n < names.Length; n++)
            {
                string name = names[n];
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (PropertyTokenEquals(prop.DisplayName, name) ||
                    PropertyTokenEquals(prop.RegistryKeyword, name))
                    return prop;
            }
        }
        return null;
    }

    private static bool PropertyTokenEquals(string left, string right)
    {
        int li = 0;
        int ri = 0;
        while (true)
        {
            while (li < left.Length && !char.IsLetterOrDigit(left[li])) li++;
            while (ri < right.Length && !char.IsLetterOrDigit(right[ri])) ri++;

            bool leftDone = li >= left.Length;
            bool rightDone = ri >= right.Length;
            if (leftDone || rightDone)
                return leftDone && rightDone;

            if (char.ToLowerInvariant(left[li]) != char.ToLowerInvariant(right[ri]))
                return false;
            li++;
            ri++;
        }
    }

    private static string ResolveRegistryValue(NicProperty prop, string displayValue)
    {
        for (int i = 0; i < prop.ValidDisplayValues.Length; i++)
            if (prop.ValidDisplayValues[i].Equals(displayValue, StringComparison.OrdinalIgnoreCase) && i < prop.ValidRegistryValues.Length)
                return prop.ValidRegistryValues[i];
        if (prop.ValidRegistryValues.Contains(displayValue, StringComparer.OrdinalIgnoreCase)) return displayValue;
        throw new InvalidOperationException($"Driver nenabízí hodnotu {displayValue} pro {prop.DisplayName}.");
    }
    private static void ApplyAdvancedPropertyWithCmdlet(string adapterName, string displayName, string displayValue)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
            throw new InvalidOperationException("Chybí název síťového adaptéru.");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new InvalidOperationException("Driver neposkytl název NIC vlastnosti.");

        static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

        string script =
            "$ErrorActionPreference='Stop'; " +
            "Set-NetAdapterAdvancedProperty -Name " + PsQuote(adapterName) +
            " -DisplayName " + PsQuote(displayName) +
            " -DisplayValue " + PsQuote(displayValue) +
            " -NoRestart -ErrorAction Stop";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Nepodařilo se spustit PowerShell pro NIC vlastnost.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("Set-NetAdapterAdvancedProperty timeout.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "Set-NetAdapterAdvancedProperty selhal." : stderr.Trim());

        LogService.Info("nic", $"NIC_CMDLET_SET adapter={adapterName} display={displayName} value={displayValue} output={stdout.Trim()}");
    }

    private static void RestartAdapterWithCmdlet(string adapterName)
    {
        FaultInjectionService.ThrowIfInjected("network.adapter-restart");
        static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
        string script =
            "$ErrorActionPreference='Stop'; " +
            "Restart-NetAdapter -Name " + PsQuote(adapterName) +
            " -Confirm:$false -ErrorAction Stop";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Nepodařilo se spustit PowerShell pro restart NIC.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(20000))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("Restart-NetAdapter timeout.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "Restart-NetAdapter selhal." : stderr.Trim());

        LogService.Info("nic", $"NIC_CMDLET_RESTART adapter={adapterName} output={stdout.Trim()}");
    }

    private static void ApplyRegistryValue(string path, string value) => ApplyRegistryValues(path, new[] { value });

    private static void ApplyRegistryValues(string path, IReadOnlyList<string> values)
    {
        var scope = new ManagementScope(@"\\.\root\StandardCimv2");
        scope.Connect();
        using var prop = new ManagementObject(scope, new ManagementPath(path), null);
        prop.Get();
        prop["RegistryValue"] = values.ToArray();
        prop.Put();
    }

    private static void RestartAdapter(string name)
    {
        FaultInjectionService.ThrowIfInjected("network.adapter-restart");
        string escaped = name.Replace("'", "''");
        using var searcher = new ManagementObjectSearcher(@"\\.\root\StandardCimv2",
            $"SELECT * FROM MSFT_NetAdapter WHERE Name='{escaped}'");
        foreach (ManagementObject adapter in searcher.Get())
        {
            using var output = adapter.InvokeMethod("Restart", null, null);
            uint code = output?["ReturnValue"] == null ? 0 : Convert.ToUInt32(output["ReturnValue"]);
            adapter.Dispose();
            if (code != 0) throw new InvalidOperationException("Restart síťového adaptéru selhal: " + code);
            return;
        }
        throw new InvalidOperationException("Síťový adaptér nebyl nalezen pro restart.");
    }

    private static void WaitForLink(string name, CancellationToken token)
    {
        string escaped = name.Replace("'", "''");
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            using var searcher = new ManagementObjectSearcher(@"\\.\root\StandardCimv2",
                $"SELECT MediaConnectState FROM MSFT_NetAdapter WHERE Name='{escaped}'");
            foreach (ManagementObject adapter in searcher.Get())
            {
                try
                {
                    int mediaState = Convert.ToInt32(adapter["MediaConnectState"] ?? 0);
                    if (mediaState == 1) return;
                }
                finally { adapter.Dispose(); }
            }
            Thread.Sleep(400);
        }
        throw new InvalidOperationException("Adaptér se po restartu nevrátil do stavu Up.");
    }
    public static IReadOnlyDictionary<string, string> ReadCapabilities(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var total = System.Diagnostics.Stopwatch.StartNew();
        string name = FindPrimaryAdapterNameFast();
        if (string.IsNullOrWhiteSpace(name))
        {
            using var adapter = FindPrimaryAdapter() ?? throw new InvalidOperationException("Nebyl nalezen aktivní síťový adaptér.");
            name = adapter["Name"]?.ToString() ?? string.Empty;
        }
        long adapterMs = total.ElapsedMilliseconds;

        lock (CapabilityCacheLock)
        {
            if (_capabilityCache != null &&
                string.Equals(_capabilityCacheAdapter, name, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - _capabilityCacheAt <= AdvancedPropertyCacheTtl)
            {
                LogService.Info("performance",
                    $"NIC_CAPABILITY_PHASE adapterMs={adapterMs} cacheHit=true totalMs={total.ElapsedMilliseconds}");
                return _capabilityCache;
            }
        }

        List<NicProperty> properties = new(0);
        RssReadResult rss = new(ProviderReadState.Unsupported, false);
        RscReadResult rsc = new(ProviderReadState.Unsupported, false, false);
        SelectiveSuspendReadResult selective = new(ProviderReadState.Unsupported, "Unsupported");
        long propertiesMs = 0, rssMs = 0, rscMs = 0, selectiveMs = 0;

        Parallel.Invoke(
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            () => { var sw = System.Diagnostics.Stopwatch.StartNew(); properties = ReadAdvancedProperties(name); propertiesMs = sw.ElapsedMilliseconds; },
            () => { var sw = System.Diagnostics.Stopwatch.StartNew(); rss = ReadRssState(name); rssMs = sw.ElapsedMilliseconds; },
            () => { var sw = System.Diagnostics.Stopwatch.StartNew(); rsc = ReadRscState(name); rscMs = sw.ElapsedMilliseconds; },
            () => { var sw = System.Diagnostics.Stopwatch.StartNew(); selective = ReadSelectiveSuspend(name); selectiveMs = sw.ElapsedMilliseconds; });

        token.ThrowIfCancellationRequested();
        LogService.Info("performance",
            $"NIC_CAPABILITY_PHASE adapterMs={adapterMs} propertiesMs={propertiesMs} rssMs={rssMs} rscMs={rscMs} selectiveMs={selectiveMs} totalMs={total.ElapsedMilliseconds}");

        var rxBuffers = FindProperty(properties, ReceiveBufferNames);
        var txBuffers = FindProperty(properties, TransmitBufferNames);
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Adapter"] = name,
            ["Interrupt Moderation"] = FindProperty(properties, InterruptModerationNames)?.DisplayValue ?? "Unsupported",
            ["Flow Control"] = FindProperty(properties, FlowControlNames)?.DisplayValue ?? "Unsupported",
            ["Packet Coalescing"] = FindProperty(properties, PacketCoalescingNames)?.DisplayValue ?? "Unsupported",
            ["Energy Efficient Ethernet"] = FindProperty(properties, EeeNames)?.DisplayValue ?? "Unsupported",
            ["Green Ethernet"] = FindProperty(properties, GreenEthernetNames)?.DisplayValue ?? "Unsupported",
            ["Power Saving Mode"] = FindProperty(properties, PowerSavingNames)?.DisplayValue ?? "Unsupported",
            ["LSO IPv4"] = FindProperty(properties, LsoIpv4Names)?.DisplayValue ?? "Unsupported",
            ["LSO IPv6"] = FindProperty(properties, LsoIpv6Names)?.DisplayValue ?? "Unsupported",
            ["Receive Buffers"] = rxBuffers?.DisplayValue ?? "Unsupported",
            ["Receive Buffers Options"] = rxBuffers == null ? string.Empty : string.Join("\u001F", rxBuffers.ValidDisplayValues),
            ["Transmit Buffers"] = txBuffers?.DisplayValue ?? "Unsupported",
            ["Transmit Buffers Options"] = txBuffers == null ? string.Empty : string.Join("\u001F", txBuffers.ValidDisplayValues),
            ["RSS"] = FormatRssState(rss),
            ["RSC"] = FormatRscState(rsc),
            ["Selective Suspend"] = FormatSelectiveSuspendState(selective)
        };

        lock (CapabilityCacheLock)
        {
            _capabilityCache = snapshot;
            _capabilityCacheAdapter = name;
            _capabilityCacheAt = DateTime.UtcNow;
        }
        return snapshot;
    }

    private static RssReadResult ReadRssState(string name)
    {
        try
        {
            string escaped = name.Replace("'", "''");
            using var searcher = new ManagementObjectSearcher(
                GetReadScope(),
                new ObjectQuery($"SELECT Enabled FROM MSFT_NetAdapterRssSettingData WHERE Name='{escaped}'"));
            foreach (ManagementObject x in searcher.Get())
            {
                try
                {
                    bool enabled = Convert.ToBoolean(x["Enabled"] ?? false);
                    return new RssReadResult(ProviderReadState.Supported, enabled);
                }
                finally { x.Dispose(); }
            }

            return new RssReadResult(ProviderReadState.Unsupported, false);
        }
        catch (Exception ex)
        {
            LogService.Error("nic", "RSS read-back failed for " + name, ex);
            return new RssReadResult(ProviderReadState.Error, false, ex.Message);
        }
    }

    private static RscReadResult ReadRscState(string name)
    {
        try
        {
            string escaped = name.Replace("'", "''");
            using var searcher = new ManagementObjectSearcher(
                GetReadScope(),
                new ObjectQuery($"SELECT IPv4Enabled,IPv6Enabled FROM MSFT_NetAdapterRscSettingData WHERE Name='{escaped}'"));
            foreach (ManagementObject x in searcher.Get())
            {
                try
                {
                    bool ipv4Enabled = Convert.ToBoolean(x["IPv4Enabled"] ?? false);
                    bool ipv6Enabled = Convert.ToBoolean(x["IPv6Enabled"] ?? false);
                    return new RscReadResult(ProviderReadState.Supported, ipv4Enabled, ipv6Enabled);
                }
                finally { x.Dispose(); }
            }

            return new RscReadResult(ProviderReadState.Unsupported, false, false);
        }
        catch (Exception ex)
        {
            LogService.Error("nic", "RSC read-back failed for " + name, ex);
            return new RscReadResult(ProviderReadState.Error, false, false, ex.Message);
        }
    }

    private static void SetRssEnabled(string name, bool enabled)
    {
        string escaped = name.Replace("'", "''");
        using var searcher = new ManagementObjectSearcher(@"\\.\root\StandardCimv2",
            $"SELECT * FROM MSFT_NetAdapterRssSettingData WHERE Name='{escaped}'");
        foreach (ManagementObject x in searcher.Get())
        {
            string method = enabled ? "Enable" : "Disable";
            using var output = x.InvokeMethod(method, null, null);
            uint code = output?["ReturnValue"] == null ? 0 : Convert.ToUInt32(output["ReturnValue"]);
            x.Dispose();
            if (code != 0) throw new InvalidOperationException("RSS změna selhala: " + code);
            InvalidateAdvancedPropertyCache();
            return;
        }
        throw new PlatformNotSupportedException("RSS není tímto NIC driverem/providerem podporováno.");
    }

    private static SelectiveSuspendReadResult ReadSelectiveSuspend(string name)
    {
        try
        {
            string escaped = name.Replace("'", "''");
            using var searcher = new ManagementObjectSearcher(
                GetReadScope(),
                new ObjectQuery($"SELECT SelectiveSuspend FROM MSFT_NetAdapterPowerManagementSettingData WHERE Name='{escaped}'"));
            foreach (ManagementObject x in searcher.Get())
            {
                try
                {
                    int state = Convert.ToInt32(x["SelectiveSuspend"] ?? 0);
                    return state switch
                    {
                        1 => new SelectiveSuspendReadResult(ProviderReadState.Supported, "Disabled"),
                        2 => new SelectiveSuspendReadResult(ProviderReadState.Supported, "Enabled"),
                        _ => new SelectiveSuspendReadResult(ProviderReadState.Unsupported, "Unsupported")
                    };
                }
                finally { x.Dispose(); }
            }

            return new SelectiveSuspendReadResult(ProviderReadState.Unsupported, "Unsupported");
        }
        catch (Exception ex)
        {
            LogService.Error("nic", "Selective Suspend read-back failed for " + name, ex);
            return new SelectiveSuspendReadResult(ProviderReadState.Error, "Error", ex.Message);
        }
    }

    private static string FormatRssState(RssReadResult result) =>
        result.State switch
        {
            ProviderReadState.Supported => result.Enabled ? "Enabled" : "Disabled",
            ProviderReadState.Unsupported => "Unsupported",
            _ => "Detection error: " + (string.IsNullOrWhiteSpace(result.ErrorMessage) ? "provider query failed" : result.ErrorMessage)
        };

    private static string FormatRscState(RscReadResult result) =>
        result.State switch
        {
            ProviderReadState.Supported => result.Ipv4Enabled || result.Ipv6Enabled
                ? $"Enabled (IPv4={(result.Ipv4Enabled ? "On" : "Off")}, IPv6={(result.Ipv6Enabled ? "On" : "Off")})"
                : "Disabled",
            ProviderReadState.Unsupported => "Unsupported",
            _ => "Detection error: " + (string.IsNullOrWhiteSpace(result.ErrorMessage) ? "provider query failed" : result.ErrorMessage)
        };

    private static string FormatSelectiveSuspendState(SelectiveSuspendReadResult result) =>
        result.State switch
        {
            ProviderReadState.Supported => result.Value,
            ProviderReadState.Unsupported => "Unsupported",
            _ => "Detection error: " + (string.IsNullOrWhiteSpace(result.ErrorMessage) ? "provider query failed" : result.ErrorMessage)
        };

    internal static void ValidateProviderReadStateClassificationForSmoke()
    {
        string rssUnsupported = FormatRssState(new RssReadResult(ProviderReadState.Unsupported, false));
        string rssError = FormatRssState(new RssReadResult(ProviderReadState.Error, false, "synthetic rss failure"));
        string rscUnsupported = FormatRscState(new RscReadResult(ProviderReadState.Unsupported, false, false));
        string rscError = FormatRscState(new RscReadResult(ProviderReadState.Error, false, false, "synthetic rsc failure"));
        string selectiveUnsupported = FormatSelectiveSuspendState(new SelectiveSuspendReadResult(ProviderReadState.Unsupported, "Unsupported"));
        string selectiveError = FormatSelectiveSuspendState(new SelectiveSuspendReadResult(ProviderReadState.Error, "Error", "synthetic selective failure"));

        if (rssUnsupported != "Unsupported" || rscUnsupported != "Unsupported" || selectiveUnsupported != "Unsupported")
            throw new InvalidOperationException("NIC provider classification smoke: Unsupported formatting changed.");
        if (!rssError.StartsWith("Detection error:", StringComparison.Ordinal) ||
            !rscError.StartsWith("Detection error:", StringComparison.Ordinal) ||
            !selectiveError.StartsWith("Detection error:", StringComparison.Ordinal))
            throw new InvalidOperationException("NIC provider classification smoke: provider errors are being mislabeled as Unsupported.");
    }

    private static void SetSelectiveSuspend(bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var adapter = FindPrimaryAdapter() ?? throw new InvalidOperationException("Nebyl nalezen aktivní síťový adaptér.");
        string name = adapter["Name"]?.ToString() ?? string.Empty;
        string escaped = name.Replace("'", "''");
        using var searcher = new ManagementObjectSearcher(@"\\.\root\StandardCimv2",
            $"SELECT * FROM MSFT_NetAdapterPowerManagementSettingData WHERE Name='{escaped}'");
        foreach (ManagementObject x in searcher.Get())
        {
            string method = enabled ? "Enable" : "Disable";
            using var input = x.GetMethodParameters(method);
            input["SelectiveSuspend"] = true;
            using var output = x.InvokeMethod(method, input, null);
            uint code = output?["ReturnValue"] == null ? 0 : Convert.ToUInt32(output["ReturnValue"]);
            x.Dispose();
            if (code != 0) throw new InvalidOperationException("Selective Suspend změna selhala: " + code);
            InvalidateAdvancedPropertyCache();
            return;
        }
        throw new InvalidOperationException("Selective Suspend není podporován.");
    }
    private static string[] ToStrings(object? value)
    {
        if (value is Array a)
        {
            int length = a.Length;
            if (length == 0) return Array.Empty<string>();
            var result = new string[length];
            for (int i = 0; i < length; i++)
                result[i] = Convert.ToString(a.GetValue(i)) ?? string.Empty;
            return result;
        }

        string s = Convert.ToString(value) ?? string.Empty;
        return s.Length == 0 ? Array.Empty<string>() : new[] { s };
    }

    private static long ToLong(object? value)
    {
        try { return Convert.ToInt64(value ?? 0); }
        catch { return 0; }
    }

    private static string FormatLink(long bitsPerSecond) => bitsPerSecond >= 1_000_000_000
        ? $"{bitsPerSecond / 1_000_000_000d:0.##} Gbps"
        : bitsPerSecond > 0 ? $"{bitsPerSecond / 1_000_000d:0.##} Mbps" : "Unknown";
}
