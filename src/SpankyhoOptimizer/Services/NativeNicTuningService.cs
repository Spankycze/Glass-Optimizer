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

internal static partial class NativeNicTuningService
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
}
