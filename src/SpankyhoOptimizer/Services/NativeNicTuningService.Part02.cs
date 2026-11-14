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

internal static partial class NativeNicTuningService
{
    private static void SetRscStateCore(string name, bool ipv4, bool ipv6, CancellationToken token = default)
    {
        RunNicMutationCommand("$ErrorActionPreference='Stop'; Set-NetAdapterRsc -Name " + PsQuote(name) +
            " -IPv4Enabled $" + ipv4.ToString().ToLowerInvariant() +
            " -IPv6Enabled $" + ipv6.ToString().ToLowerInvariant() + " -ErrorAction Stop", 20000, token);
        InvalidateAdvancedPropertyCache();
    }


    private static string RunNicTransaction(Func<string> apply, Action restore)
    {
        try { return apply(); }
        catch (Exception original)
        {
            try { restore(); }
            catch (Exception recovery)
            {
                LogService.Error("nic", "NIC rollback incomplete", recovery);
                throw new AggregateException("NIC změna selhala a obnovení původního stavu nebylo potvrzeno.",
                    original, recovery);
            }
            throw;
        }
    }

    private static void VerifyRegistrySnapshot(string name, NicProperty before)
    {
        InvalidateAdvancedPropertyCache();
        var actual = ReadAdvancedProperties(name).FirstOrDefault(x =>
            x.RegistryKeyword.Equals(before.RegistryKeyword, StringComparison.OrdinalIgnoreCase));
        if (actual == null || !actual.RegistryValues.SequenceEqual(before.RegistryValues, StringComparer.Ordinal))
            throw new InvalidOperationException("NIC rollback read-back mismatch: " + before.DisplayName);
    }

    private static void VerifyRecoveredConnectivity(string name, bool hadConnectivity)
    {
        WaitForLink(name, CancellationToken.None);
        if (hadConnectivity && !CanReachInternet())
            throw new InvalidOperationException("NIC rollback neobnovil původní konektivitu.");
    }

    public static string ApplyPerformanceProfile(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string adapterName = FindPrimaryAdapterNameFast();
        if (string.IsNullOrWhiteSpace(adapterName))
            throw new InvalidOperationException("Nebyl nalezen adaptér aktivní internetové trasy.");
        InvalidateAdvancedPropertyCache();
        var properties = ReadAdvancedProperties(adapterName);
        var rssBefore = ReadRssState(adapterName);
        var selectiveBefore = ReadSelectiveSuspend(adapterName);
        var log = new List<string> { "Adapter: " + adapterName };
        var planned = new List<(NicProperty Prop, string Value)>();
        var attempted = new List<NicProperty>();
        var targets = new (string Label, string[] Names)[]
        {
            ("Energy-Efficient Ethernet", EeeNames),
            ("Green Ethernet", GreenEthernetNames),
            ("Power Saving Mode", PowerSavingNames),
            ("Gigabit Lite", new[]{"Gigabit Lite","Auto Disable Gigabit","GigaLite"}),
            ("Reduce Speed On Power Down", new[]{"Reduce Speed On Power Down","ReduceSpeedOnPowerDown"})
        };
        foreach (var target in targets)
        {
            var matches = FindProperties(properties, target.Names);
            if (matches.Count == 0) log.Add(target.Label + " = Unsupported by driver");
            foreach (var prop in matches)
            {
                if (prop.DisplayValue.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                {
                    log.Add(prop.DisplayName + " = Disabled (already)");
                    continue;
                }
                // Preflight every value before any write. No mutation without an exact recoverable snapshot.
                if (prop.RegistryValues.Length == 0 || string.IsNullOrWhiteSpace(prop.Path))
                    throw new InvalidOperationException("NIC snapshot missing: " + prop.DisplayName);
                planned.Add((prop, ResolveRegistryValue(prop, "Disabled")));
            }
        }
        bool changeRss = rssBefore.State == ProviderReadState.Supported && !rssBefore.Enabled;
        bool changeSelective = selectiveBefore.State == ProviderReadState.Supported &&
            selectiveBefore.Value.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        if (!changeRss) log.Add("RSS = " + FormatRssState(rssBefore));
        if (!changeSelective) log.Add("Selective Suspend = " + FormatSelectiveSuspendState(selectiveBefore));
        bool rssAttempted = false, selectiveAttempted = false;
        bool hadConnectivity = planned.Count > 0 || changeRss || changeSelective ? CanReachInternet() : false;
        return RunNicTransaction(() =>
        {
            foreach (var change in planned)
            {
                token.ThrowIfCancellationRequested();
                attempted.Add(change.Prop); // Also restore a provider that writes and then throws.
                ApplyRegistryValue(change.Prop.Path, change.Value, token);
                log.Add(change.Prop.DisplayName + " -> Disabled");
            }
            if (changeRss)
            {
                token.ThrowIfCancellationRequested();
                rssAttempted = true;
                SetRssEnabled(adapterName, true, token);
            }
            if (changeSelective)
            {
                token.ThrowIfCancellationRequested();
                selectiveAttempted = true;
                SetSelectiveSuspend(adapterName, false, token);
            }
            token.ThrowIfCancellationRequested();
            bool changed = attempted.Count > 0 || rssAttempted || selectiveAttempted;
            if (changed)
            {
                RestartAdapterWithCmdlet(adapterName, token);
                WaitForLink(adapterName, token);
                InvalidateAdvancedPropertyCache();
                var actual = ReadAdvancedProperties(adapterName);
                foreach (var change in planned)
                {
                    var value = actual.FirstOrDefault(x => x.RegistryKeyword.Equals(change.Prop.RegistryKeyword,
                        StringComparison.OrdinalIgnoreCase));
                    if (value == null || !value.RegistryValues.SequenceEqual(new[] { change.Value }, StringComparer.Ordinal))
                        throw new InvalidOperationException("NIC read-back mismatch: " + change.Prop.DisplayName);
                }
                if (rssAttempted)
                {
                    var rss = ReadRssState(adapterName);
                    if (rss.State != ProviderReadState.Supported || !rss.Enabled)
                        throw new InvalidOperationException("RSS read-back nepotvrdil Enabled.");
                    log.Add("RSS = Enabled");
                }
                if (selectiveAttempted)
                {
                    var selective = ReadSelectiveSuspend(adapterName);
                    if (selective.State != ProviderReadState.Supported || selective.Value != "Disabled")
                        throw new InvalidOperationException("Selective Suspend read-back nepotvrdil Disabled.");
                    log.Add("Selective Suspend = Disabled");
                }
                if (hadConnectivity && !CanReachInternet())
                    throw new InvalidOperationException("Po NIC Recommended se ztratila konektivita.");
                log.Add("Adapter restart = OK");
            }
            else log.Add("Adapter restart = nebyl potřeba");
            token.ThrowIfCancellationRequested();
            return string.Join(Environment.NewLine, log);
        }, () =>
        {
            var errors = new List<Exception>();
            void Recover(Action action) { try { action(); } catch (Exception ex) { errors.Add(ex); } }
            // Recovery ignores the caller's cancellation and attempts every restoration.
            if (selectiveAttempted) Recover(() => SetSelectiveSuspend(adapterName,
                selectiveBefore.Value == "Enabled", CancellationToken.None));
            if (rssAttempted) Recover(() => SetRssEnabled(adapterName, rssBefore.Enabled));
            for (int i = attempted.Count - 1; i >= 0; i--)
            {
                var snapshot = attempted[i];
                Recover(() => ApplyRegistryValues(snapshot.Path, snapshot.RegistryValues));
            }
            if (attempted.Count > 0 || rssAttempted || selectiveAttempted)
            {
                Recover(() => RestartAdapterWithCmdlet(adapterName));
                Recover(() => VerifyRecoveredConnectivity(adapterName, hadConnectivity));
                foreach (var snapshot in attempted) Recover(() => VerifyRegistrySnapshot(adapterName, snapshot));
                if (rssAttempted) Recover(() =>
                {
                    var rss = ReadRssState(adapterName);
                    if (rss.State != ProviderReadState.Supported || rss.Enabled != rssBefore.Enabled)
                        throw new InvalidOperationException("RSS rollback read-back mismatch.");
                });
                if (selectiveAttempted) Recover(() =>
                {
                    var selective = ReadSelectiveSuspend(adapterName);
                    if (selective.State != ProviderReadState.Supported || selective.Value != selectiveBefore.Value)
                        throw new InvalidOperationException("Selective Suspend rollback read-back mismatch.");
                });
            }
            InvalidateAdvancedPropertyCache();
            if (errors.Count > 0) throw new AggregateException("NIC profile rollback failed.", errors);
        });
    }


    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterface(uint destination, out uint interfaceIndex);

    internal static string FindPrimaryAdapterNameFast()
    {
        // The same destination as the connectivity check; this only reads Windows' routing table.
        if (GetBestInterface(0x01010101, out uint index) != 0) return string.Empty;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                if (nic.GetIPProperties().GetIPv4Properties()?.Index == index) return nic.Name;
            }
            catch (NetworkInformationException) { }
        }
        return string.Empty;
    }

    private static ManagementObject? FindPrimaryAdapter()
    {
        string name = FindPrimaryAdapterNameFast();
        if (string.IsNullOrWhiteSpace(name)) return null;
        string escaped = name.Replace("'", "''");
        using var searcher = new ManagementObjectSearcher(GetReadScope(),
            new ObjectQuery($"SELECT * FROM MSFT_NetAdapter WHERE Name='{escaped}' AND MediaConnectState=1"));
        // The returned adapter is owned by the caller; the query result set
        // itself must not retain its native WMI enumeration handle.
        var results = searcher.Get();
        return WmiFirstRowScope.TakeFirst<ManagementObject>(results, results);
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

    internal static void InvalidateAdvancedPropertyCache()
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
            new ObjectQuery($"SELECT __PATH,DisplayName,DisplayValue,RegistryKeyword,RegistryValue,ValidDisplayValues,ValidRegistryValues FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE Name='{escaped}'"));
        using var results = searcher.Get();
        foreach (ManagementObject x in results)
        {
            using (x)
            {
                list.Add(new NicProperty(
                    x["DisplayName"]?.ToString() ?? string.Empty,
                    x["DisplayValue"]?.ToString() ?? string.Empty,
                    x["RegistryKeyword"]?.ToString() ?? string.Empty,
                    x.Path.Path,
                    ToStrings(x["RegistryValue"]),
                    ToStrings(x["ValidDisplayValues"]),
                    ToStrings(x["ValidRegistryValues"])));
            }
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
}
