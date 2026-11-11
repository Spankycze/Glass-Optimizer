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
        using var adapter = FindPrimaryAdapter() ?? throw new InvalidOperationException("Nebyl nalezen adaptér aktivní trasy.");
        string adapterName = adapter["Name"]?.ToString() ?? string.Empty;
        InvalidateAdvancedPropertyCache();
        var prop = FindProperty(ReadAdvancedProperties(adapterName), names)
            ?? throw new PlatformNotSupportedException("Driver nepodporuje: " + string.Join(" / ", names));
        if (prop.DisplayValue.Equals(value, StringComparison.OrdinalIgnoreCase))
            return adapterName + " • " + prop.DisplayName + " = " + prop.DisplayValue;
        if (prop.RegistryValues.Length == 0 || string.IsNullOrWhiteSpace(prop.Path))
            throw new InvalidOperationException("NIC snapshot missing: " + prop.DisplayName);
        bool hadConnectivity = CanReachInternet();
        bool attempted = false;
        return RunNicTransaction(() =>
        {
            token.ThrowIfCancellationRequested();
            attempted = true;
            ApplyAdvancedPropertyWithCmdlet(adapterName, prop.DisplayName, value, token);
            RestartAdapterWithCmdlet(adapterName, token);
            WaitForLink(adapterName, token);
            InvalidateAdvancedPropertyCache();
            var verify = ReadAdvancedProperties(adapterName).FirstOrDefault(x =>
                x.RegistryKeyword.Equals(prop.RegistryKeyword, StringComparison.OrdinalIgnoreCase));
            if (verify == null || !verify.DisplayValue.Equals(value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Read-back nepotvrdil změnu NIC vlastnosti.");
            if (hadConnectivity && !CanReachInternet())
                throw new InvalidOperationException("Po změně NIC vlastnosti se ztratila konektivita.");
            token.ThrowIfCancellationRequested();
            return adapterName + " • " + prop.DisplayName + " = " + verify.DisplayValue;
        }, () =>
        {
            if (!attempted) return;
            var errors = new List<Exception>();
            try { ApplyRegistryValues(prop.Path, prop.RegistryValues); } catch (Exception ex) { errors.Add(ex); }
            try { RestartAdapterWithCmdlet(adapterName); } catch (Exception ex) { errors.Add(ex); }
            try { VerifyRecoveredConnectivity(adapterName, hadConnectivity); } catch (Exception ex) { errors.Add(ex); }
            try { VerifyRegistrySnapshot(adapterName, prop); } catch (Exception ex) { errors.Add(ex); }
            if (errors.Count > 0) throw new AggregateException("NIC property rollback failed.", errors);
        });
    }


    /// <summary>
    /// Read-only, exact Realtek driver check. A different primary adapter or
    /// unexpected driver values must not be reported as a confirmed drift.
    /// </summary>
    public static string? ReadRealtekGreenEthernetStatus(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var adapter = FindPrimaryAdapter();
        token.ThrowIfCancellationRequested();
        if (adapter == null ||
            !string.Equals(adapter["InterfaceDescription"]?.ToString(),
                "Realtek Gaming 2.5GbE Family Controller", StringComparison.OrdinalIgnoreCase))
            return null;
        string name = adapter["Name"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var advancedProperties = ReadAdvancedProperties(name);
        token.ThrowIfCancellationRequested();
        var matches = advancedProperties.Where(x =>
            string.Equals(x.RegistryKeyword, "EnableGreenEthernet", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].RegistryValues.Length != 1)
            return null;
        return DecodeGreenEthernetRegistryValue(matches[0].RegistryValues[0]);
    }

    internal static string? DecodeGreenEthernetRegistryValue(string? raw) => raw switch
    {
        "0" => "Disabled",
        "1" => "Enabled",
        _ => null
    };

    /// <summary>
    /// Explicit user action only. Writes the Realtek Green Ethernet driver setting
    /// without restarting/disabling the adapter. Never called by periodic monitoring.
    /// A driver may still require its next ordinary initialization to apply the change.
    /// </summary>
    public static string DisableRealtekGreenEthernetNoRestart(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var adapter = FindPrimaryAdapter()
            ?? throw new InvalidOperationException("Primární aktivní síťový adaptér není dostupný.");
        string name = adapter["Name"]?.ToString() ?? string.Empty;
        string description = adapter["InterfaceDescription"]?.ToString() ?? string.Empty;
        if (!string.Equals(description, "Realtek Gaming 2.5GbE Family Controller",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Green Ethernet lze tímto tlačítkem změnit pouze na ověřené Realtek Gaming 2.5GbE.");

        var prop = ReadAdvancedProperties(name).SingleOrDefault(x =>
            string.Equals(x.RegistryKeyword, "EnableGreenEthernet", StringComparison.OrdinalIgnoreCase));
        if (prop == null)
            throw new PlatformNotSupportedException("Driver neposkytuje jednoznačnou vlastnost EnableGreenEthernet.");
        if (prop.RegistryValues.SequenceEqual(["0"], StringComparer.Ordinal))
            return "Green Ethernet už je vypnutý. Nebyl proveden žádný zápis.";
        if (!prop.RegistryValues.SequenceEqual(["1"], StringComparer.Ordinal))
            throw new InvalidOperationException("Neznámá původní hodnota driveru. Nic nebylo změněno.");
        string targetDisplay = SelectGreenEthernetDisabledDisplayValue(
            prop.ValidDisplayValues, prop.ValidRegistryValues);
        string operationId = Guid.NewGuid().ToString("N");
        // A failed Prepared record blocks the driver mutation.
        WriteGreenEthernetJournalEntry(operationId, "Prepared", name, description,
            prop.RegistryKeyword, prop.RegistryValues[0]);
        token.ThrowIfCancellationRequested();
        // Resolve the actual driver's localized display value for RegistryValue=0.
        // This operation uses -NoRestart; no adapter reset is requested.
        ApplyAdvancedPropertyWithCmdlet(name, prop.DisplayName, targetDisplay, token);
        InvalidateAdvancedPropertyCache();
        var after = ReadAdvancedProperties(name).SingleOrDefault(x =>
            string.Equals(x.RegistryKeyword, "EnableGreenEthernet", StringComparison.OrdinalIgnoreCase));
        if (after == null || !after.RegistryValues.SequenceEqual(["0"], StringComparer.Ordinal))
            throw new InvalidOperationException("Ovladač nepotvrdil Green Ethernet=0. Restart adaptéru nebyl proveden.");
        try
        {
            WriteGreenEthernetJournalEntry(operationId, "Applied", name, description,
                prop.RegistryKeyword, prop.RegistryValues[0]);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            LogService.Error("default-guard",
                "Green Ethernet=0 was verified but journal Applied receipt could not be saved", ex);
        }
        return name + ": Green Ethernet = Disabled (0); adaptér nebyl restartován.";
    }

    // ValidDisplayValues and ValidRegistryValues are parallel arrays provided
    // by MSFT_NetAdapterAdvancedPropertySettingData. Never assume English text.
    internal static string SelectGreenEthernetDisabledDisplayValue(
        string[] displayValues, string[] registryValues)
    {
        if (displayValues == null || registryValues == null ||
            displayValues.Length == 0 || displayValues.Length != registryValues.Length)
            throw new InvalidOperationException("Driver neposkytl jednoznačný seznam hodnot Green Ethernet.");
        int match = -1;
        for (int i = 0; i < registryValues.Length; i++)
        {
            if (!string.Equals(registryValues[i], "0", StringComparison.Ordinal)) continue;
            if (match != -1)
                throw new InvalidOperationException("Driver poskytl více nejednoznačných vypnutých hodnot.");
            match = i;
        }
        if (match < 0 || string.IsNullOrWhiteSpace(displayValues[match]))
            throw new InvalidOperationException("Driver neposkytuje vypnutou hodnotu Green Ethernet=0.");
        return displayValues[match];
    }

    private static void WriteGreenEthernetJournalEntry(string operationId, string phase,
        string adapter, string description, string keyword, string originalRawValue)
    {
        string snapshot = System.Text.Json.JsonSerializer.Serialize(new
        {
            AtUtc = DateTime.UtcNow, OperationId = operationId, Phase = phase,
            GroupId = "green-ethernet", Adapter = adapter,
            Device = description, Keyword = keyword,
            OldRegistryValues = new[] { originalRawValue }, Action = "Set 0 -NoRestart"
        });
        // Share the bounded, crash-conscious append implementation with all
        // other Windows Default Guard writers (periodic + manual UAC).
        WindowsDefaultGuardService.AppendExternalJournalLine(snapshot);
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
        using var adapter = FindPrimaryAdapter() ?? throw new InvalidOperationException("Nebyl nalezen adaptér aktivní trasy.");
        string adapterName = adapter["Name"]?.ToString() ?? string.Empty;
        var before = ReadRscState(adapterName);
        if (before.State != ProviderReadState.Supported)
            throw new PlatformNotSupportedException("RSC není tímto driverem/providerem podporováno: " + FormatRscState(before));
        if (before.Ipv4Enabled == enabled && before.Ipv6Enabled == enabled)
            return $"{adapterName} • RSC = {FormatRscState(before)}";
        bool hadConnectivity = CanReachInternet();
        bool attempted = false;
        return RunNicTransaction(() =>
        {
            token.ThrowIfCancellationRequested();
            attempted = true;
            SetRscEnabledCore(adapterName, enabled, token);
            WaitForLink(adapterName, token);
            var verify = ReadRscState(adapterName);
            if (verify.State != ProviderReadState.Supported || verify.Ipv4Enabled != enabled || verify.Ipv6Enabled != enabled)
                throw new InvalidOperationException("RSC read-back nepotvrdil požadovaný stav.");
            if (hadConnectivity && !CanReachInternet())
                throw new InvalidOperationException("Po změně RSC se ztratila konektivita.");
            token.ThrowIfCancellationRequested();
            return $"{adapterName} • RSC = {FormatRscState(verify)}";
        }, () =>
        {
            if (!attempted) return;
            var errors = new List<Exception>();
            try { SetRscStateCore(adapterName, before.Ipv4Enabled, before.Ipv6Enabled); }
            catch (Exception ex) { errors.Add(ex); }
            try { VerifyRecoveredConnectivity(adapterName, hadConnectivity); }
            catch (Exception ex) { errors.Add(ex); }
            try
            {
                var restored = ReadRscState(adapterName);
                if (restored.State != ProviderReadState.Supported ||
                    restored.Ipv4Enabled != before.Ipv4Enabled || restored.Ipv6Enabled != before.Ipv6Enabled)
                    throw new InvalidOperationException("RSC rollback read-back mismatch.");
            }
            catch (Exception ex) { errors.Add(ex); }
            if (errors.Count > 0) throw new AggregateException("RSC rollback failed.", errors);
        });
    }


    private static void SetRscEnabledCore(string name, bool enabled, CancellationToken token = default) => SetRscStateCore(name, enabled, enabled, token);

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
    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string RunNicCommand(string script, int timeoutMs, CancellationToken token = default) => RunNicCommandCore(script, timeoutMs, token, false);

    private static string RunNicMutationCommand(string script, int timeoutMs, CancellationToken token = default) => RunNicCommandCore(script, timeoutMs, token, true);

    private static string RunNicCommandCore(string script, int timeoutMs, CancellationToken token, bool waitForMutationSettlement)
    {
        token.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("NIC command could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeoutMs);
        try
        {
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            if (waitForMutationSettlement)
            {
                // A dispatched CIM write is not cancelled by terminating its client. Keep the mutation gate
                // until the provider call settles, then let the transaction restore and verify its snapshot.
                LogService.Info("nic", "NIC mutation deadline/cancellation reached; waiting for provider settlement before rollback.");
                process.WaitForExitAsync(CancellationToken.None).GetAwaiter().GetResult();
                Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            }
            else { try { process.Kill(entireProcessTree: true); } catch { } }
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("NIC command timeout.");
        }
        string stderrText = stderr.GetAwaiter().GetResult();
        string stdoutText = stdout.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderrText) ? "NIC command failed." : stderrText.Trim());
        return stdoutText;
    }

    private static void ApplyAdvancedPropertyWithCmdlet(string adapterName, string displayName, string displayValue, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(adapterName) || string.IsNullOrWhiteSpace(displayName))
            throw new InvalidOperationException("Missing adapter or property name.");
        RunNicMutationCommand("$ErrorActionPreference='Stop'; Set-NetAdapterAdvancedProperty -Name " + PsQuote(adapterName) +
            " -DisplayName " + PsQuote(displayName) + " -DisplayValue " + PsQuote(displayValue) +
            " -NoRestart -ErrorAction Stop", 15000, token);
    }

    private static void RestartAdapterWithCmdlet(string adapterName, CancellationToken token = default)
    {
        FaultInjectionService.ThrowIfInjected("network.adapter-restart");
        RunNicMutationCommand("$ErrorActionPreference='Stop'; Restart-NetAdapter -Name " + PsQuote(adapterName) +
            " -Confirm:$false -ErrorAction Stop", 20000, token);
    }

    private static void ApplyRegistryValue(string path, string value, CancellationToken token = default) => ApplyRegistryValues(path, new[] { value }, token);

    private static void ApplyRegistryValues(string path, IReadOnlyList<string> values, CancellationToken token = default)
    {
        // NetAdapter CDXML requires CIM ModifyInstance operation options, unavailable through legacy WMI Put.
        using var prop = new ManagementObject(GetReadScope(), new ManagementPath(path), null);
        prop.Get();
        string adapterName = prop["Name"]?.ToString() ?? string.Empty;
        string keyword = prop["RegistryKeyword"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(adapterName) || string.IsNullOrWhiteSpace(keyword) || values.Count == 0)
            throw new InvalidOperationException("NIC snapshot is missing adapter, keyword or registry values.");
        // Match exact identity: driver keywords often contain literal wildcard characters.
        string script = "$ErrorActionPreference='Stop'; $p=Get-NetAdapterAdvancedProperty -Name " + PsQuote(adapterName) +
            " -AllProperties -ErrorAction Stop | Where-Object { $_.RegistryKeyword -eq " + PsQuote(keyword) +
            " }; if (@($p).Count -ne 1) { throw 'NIC property identity is ambiguous or missing' }; " +
            "Set-NetAdapterAdvancedProperty -InputObject $p -RegistryValue @(" + string.Join(",", values.Select(PsQuote)) +
            ") -NoRestart -ErrorAction Stop";
        RunNicMutationCommand(script, 15000, token);
    }


    private static void RestartAdapter(string name) => RestartAdapterWithCmdlet(name);

    private static void WaitForLink(string name, CancellationToken token)
    {
        string escaped = name.Replace("'", "''");
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            using var searcher = new ManagementObjectSearcher(@"\\.\root\StandardCimv2",
                $"SELECT MediaConnectState FROM MSFT_NetAdapter WHERE Name='{escaped}'");
            using var resultSet = searcher.Get();
            foreach (ManagementObject adapter in resultSet)
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
            using var results = searcher.Get();
            foreach (ManagementObject x in results)
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
            using var results = searcher.Get();
            foreach (ManagementObject x in results)
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

    private static void SetRssEnabled(string name, bool enabled, CancellationToken token = default)
    {
        RunNicMutationCommand("$ErrorActionPreference='Stop'; " +
            (enabled ? "Enable-NetAdapterRss" : "Disable-NetAdapterRss") +
            " -Name " + PsQuote(name) + " -NoRestart -ErrorAction Stop", 20000, token);
        InvalidateAdvancedPropertyCache();
    }

    private static void SetSelectiveSuspend(string name, bool enabled, CancellationToken token)
    {
        RunNicMutationCommand("$ErrorActionPreference='Stop'; Set-NetAdapterPowerManagement -Name " + PsQuote(name) +
            " -SelectiveSuspend " + (enabled ? "Enabled" : "Disabled") + " -NoRestart -ErrorAction Stop", 20000, token);
        InvalidateAdvancedPropertyCache();
    }


    private static SelectiveSuspendReadResult ReadSelectiveSuspend(string name)
    {
        try
        {
            string escaped = name.Replace("'", "''");
            using var searcher = new ManagementObjectSearcher(
                GetReadScope(),
                new ObjectQuery($"SELECT SelectiveSuspend FROM MSFT_NetAdapterPowerManagementSettingData WHERE Name='{escaped}'"));
            using var results = searcher.Get();
            foreach (ManagementObject x in results)
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
