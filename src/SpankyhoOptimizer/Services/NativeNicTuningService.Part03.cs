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
