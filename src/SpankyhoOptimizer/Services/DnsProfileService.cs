using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal sealed record DnsAdapterSnapshot(string Name, string[] Servers);
internal sealed record DnsRollbackSnapshot(DateTime CapturedAtUtc, DnsAdapterSnapshot[] Adapters);
internal sealed record DnsApplyResult(string[] IPv4, string[] IPv6, string[] VerifiedAdapters)
{
    internal string Primary => IPv4.Concat(IPv6).FirstOrDefault() ?? "—";
    internal string Secondary => IPv4.Concat(IPv6).Skip(1).FirstOrDefault() ?? "—";
}

internal static class DnsProfileService
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly string RollbackPath = Path.Combine(LogService.Root, "dns-rollback.json");

    // Validate every submitted resolver before saving the rollback snapshot or
    // invoking netsh. In particular, do not silently pass an IPv6 resolver to
    // the IPv4 slot (or vice versa) and risk a partial adapter update.
    private static string[] ValidateDnsAddresses(
        IReadOnlyList<string> addresses, AddressFamily expectedFamily)
    {
        var validated = new List<string>(2);
        foreach (string raw in addresses)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string candidate = raw.Trim();
            if (!IPAddress.TryParse(candidate, out IPAddress? parsed) ||
                parsed.AddressFamily != expectedFamily ||
                parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any) ||
                parsed.Equals(IPAddress.Broadcast) || parsed.IsIPv6Multicast ||
                (expectedFamily == AddressFamily.InterNetwork &&
                 !string.Equals(candidate, parsed.ToString(), StringComparison.Ordinal)))
                throw new ArgumentException($"Neplatná {(expectedFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} DNS adresa: {candidate}");
            string normalized = parsed.ToString();
            if (!validated.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                validated.Add(normalized);
        }
        return validated.Take(2).ToArray();
    }

    internal static string[] FindUnexpectedResolvers(
        IEnumerable<string> expected,
        IEnumerable<string> current)
    {
        var expectedSet = expected
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => IPAddress.Parse(x))
            .ToArray();
        var expectedFamilies = expectedSet.Select(x => x.AddressFamily).Distinct().ToHashSet();
        return current
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x =>
            {
                if (!IPAddress.TryParse(x, out var ip)) return true;
                // Ignore current resolvers from address families not modified by expected.
                if (!expectedFamilies.Contains(ip.AddressFamily)) return false;
                return !expected.Contains(x, StringComparer.OrdinalIgnoreCase);
            })
            .ToArray();
    }

    // Verify the same adapters that were actually modified, not whichever
    // adapters happen to be active by the time netsh/flushdns has finished.
    // An adapter disappearing must never produce a false successful apply.
    private static string[] VerifyAppliedAdapterReadback(
        IEnumerable<string> targetAdapterNames, IReadOnlyList<string> expectedResolvers,
        IEnumerable<DnsAdapterSnapshot> observedAdapters)
    {
        string[] targets = targetAdapterNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException("DNS read-back: žádný cílový adaptér.");
        var observed = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (DnsAdapterSnapshot adapter in observedAdapters)
        {
            if (!targets.Contains(adapter.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (!observed.TryAdd(adapter.Name, adapter.Servers))
                throw new InvalidOperationException("DNS read-back: duplicitní adaptér " + adapter.Name);
        }
        var verified = new List<string>(targets.Length);
        foreach (string adapterName in targets)
        {
            if (!observed.TryGetValue(adapterName, out string[]? current))
                throw new InvalidOperationException("DNS read-back: původní adaptér zmizel: " + adapterName);
            if (!expectedResolvers.All(x => current.Contains(x, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Read-back nepotvrdil DNS na adaptéru {adapterName}. Aktuálně: {string.Join(", ", current)}");
            string[] unexpected = FindUnexpectedResolvers(expectedResolvers, current);
            if (unexpected.Length > 0)
                throw new InvalidOperationException(
                    $"DNS fallback/leak kontrola našla na adaptéru {adapterName} neočekávaný resolver: {string.Join(", ", unexpected)}");
            verified.Add($"{adapterName}: {string.Join(", ", current)}");
        }
        return verified.ToArray();
    }

    internal static string RunReadbackGuardSmoke()
    {
        string[] expected = ["1.1.1.1", "9.9.9.9"];
        string[] clean = ["1.1.1.1", "9.9.9.9"];
        string[] leaked = ["1.1.1.1", "9.9.9.9", "192.168.1.1"];
        if (FindUnexpectedResolvers(expected, clean).Length != 0)
            throw new InvalidOperationException("DNS read-back guard označil čistý pár jako leak.");
        var unexpected = FindUnexpectedResolvers(expected, leaked);
        if (unexpected.Length != 1 || unexpected[0] != "192.168.1.1")
            throw new InvalidOperationException("DNS read-back guard nezachytil neočekávaný resolver.");

        // Pure pre-apply validation; no DNS or adapter configuration changes.
        string[] v4 = ValidateDnsAddresses(
            [" 1.1.1.1 ", "1.1.1.1", "9.9.9.9"], AddressFamily.InterNetwork);
        string[] v6 = ValidateDnsAddresses(
            ["2606:4700:4700::1111", "2606:4700:4700:0:0:0:0:1111"],
            AddressFamily.InterNetworkV6);
        if (v4.Length != 2 || v4[0] != "1.1.1.1" || v4[1] != "9.9.9.9" ||
            v6.Length != 1 || v6[0] != "2606:4700:4700::1111")
            throw new InvalidOperationException("DNS pre-apply canonicalization/deduplication failed.");
        static void ExpectReject(string[] values, AddressFamily family)
        {
            bool rejected = false;
            try { _ = ValidateDnsAddresses(values, family); }
            catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException(
                "DNS pre-apply accepted an invalid or cross-family resolver.");
        }
        ExpectReject(["2606:4700:4700::1111"], AddressFamily.InterNetwork);
        ExpectReject(["1.1.1.1"], AddressFamily.InterNetworkV6);
        ExpectReject(["not-an-ip-address"], AddressFamily.InterNetwork);
        ExpectReject(["0.0.0.0"], AddressFamily.InterNetwork);
        ExpectReject(["255.255.255.255"], AddressFamily.InterNetwork);
        ExpectReject(["ff02::1"], AddressFamily.InterNetworkV6);
        ExpectReject(["1"], AddressFamily.InterNetwork);

        // Pure read-back tests: a vanished modified adapter must fail, while
        // an adapter that only appeared afterward must not be treated as a leak.
        var ethernet = new DnsAdapterSnapshot("Ethernet", clean);
        var lateAdapter = new DnsAdapterSnapshot("Later VPN", leaked);
        string[] verified = VerifyAppliedAdapterReadback(["Ethernet"], expected,
            [ethernet, lateAdapter]);
        if (verified.Length != 1 || !verified[0].StartsWith("Ethernet:", StringComparison.Ordinal))
            throw new InvalidOperationException("DNS read-back included a non-target adapter.");
        static void RejectReadback(string[] targets, DnsAdapterSnapshot[] observed)
        {
            bool failed = false;
            try { _ = VerifyAppliedAdapterReadback(targets, ["1.1.1.1", "9.9.9.9"], observed); }
            catch (InvalidOperationException) { failed = true; }
            if (!failed)
                throw new InvalidOperationException("DNS read-back wrongly accepted a missing or mismatched target.");
        }
        RejectReadback(["Ethernet"], []);
        RejectReadback(["Ethernet", "Wi-Fi"], [ethernet]);
        RejectReadback(["Ethernet"], [new DnsAdapterSnapshot("Ethernet", ["1.1.1.1"])]);
        RejectReadback(["Ethernet"], [new DnsAdapterSnapshot("Ethernet", leaked)]);
        RejectReadback(["Ethernet"], [ethernet, ethernet]);
        RejectReadback([], [ethernet]);

        // Rollback preflight is entirely in-memory: never call netsh, save a
        // real snapshot, or enumerate/change live network adapters in tests.
        var backup = new DnsRollbackSnapshot(DateTime.UtcNow,
            [new DnsAdapterSnapshot("Ethernet", ["1.1.1.1", "2606:4700:4700:0:0:0:0:1111"]),
             new DnsAdapterSnapshot("Wi-Fi", [])]);
        var checkedBackup = ValidateRollbackSnapshot(backup,
            ["Ethernet", "Wi-Fi", "New unrelated VPN"]);
        if (checkedBackup.Adapters.Length != 2 ||
            checkedBackup.Adapters[0].Servers[1] != "2606:4700:4700::1111" ||
            checkedBackup.Adapters[1].Servers.Length != 0)
            throw new InvalidOperationException("DNS rollback rejected a valid static/DHCP snapshot.");
        static void RejectRollback(DnsRollbackSnapshot? backup,
            string[] available)
        {
            bool rejected = false;
            try { _ = ValidateRollbackSnapshot(backup, available); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException(
                    "DNS rollback accepted untrusted, stale or incomplete snapshot data.");
        }
        RejectRollback(null, ["Ethernet"]);
        RejectRollback(new DnsRollbackSnapshot(DateTime.UtcNow, []), ["Ethernet"]);
        RejectRollback(backup, ["Ethernet"]); // one original adapter vanished
        RejectRollback(new DnsRollbackSnapshot(DateTime.UtcNow,
            [new DnsAdapterSnapshot("Ethernet", clean),
             new DnsAdapterSnapshot("ethernet", clean)]), ["Ethernet"]);
        RejectRollback(new DnsRollbackSnapshot(DateTime.UtcNow,
            [new DnsAdapterSnapshot("Ethernet\\\" -x", clean)]), ["Ethernet\\\" -x"]);
        RejectRollback(new DnsRollbackSnapshot(DateTime.UtcNow,
            [new DnsAdapterSnapshot("Ethernet\nmalformed", clean)]),
            ["Ethernet\nmalformed"]);
        RejectRollback(new DnsRollbackSnapshot(DateTime.UtcNow,
            [new DnsAdapterSnapshot("Ethernet", ["1.1.1.1 -x"])]), ["Ethernet"]);
        RejectRollback(new DnsRollbackSnapshot(DateTime.UtcNow,
            [new DnsAdapterSnapshot("Ethernet", ["0.0.0.0"])]), ["Ethernet"]);
        RejectRollback(new DnsRollbackSnapshot(DateTime.UtcNow,
            [new DnsAdapterSnapshot("Ethernet", new string[17])]), ["Ethernet"]);
        if (!IsSafeAdapterName("Ethernet") ||
            IsSafeAdapterName("Ethernet" + (char)34 + " -x") ||
            IsSafeAdapterName("Ethernet\nmalformed"))
            throw new InvalidOperationException("DNS adapter name argument fencing regressed.");
        string temp = Path.Combine(Path.GetTempPath(),
            "spanky-dns-rollback-smoke-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(temp, "existing-complete-rollback");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            bool refused = false;
            try
            {
                WriteSnapshotAtomicallyAsync(temp, "interrupted", canceled.Token)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { refused = true; }
            if (!refused || File.ReadAllText(temp) != "existing-complete-rollback")
                throw new InvalidOperationException(
                    "Cancelled DNS snapshot write destroyed the last rollback.");
            WriteSnapshotAtomicallyAsync(temp, "complete-new-rollback", CancellationToken.None)
                .GetAwaiter().GetResult();
            if (File.ReadAllText(temp) != "complete-new-rollback" ||
                Directory.GetFiles(Path.GetTempPath(),
                    Path.GetFileName(temp) + ".*.new").Length != 0)
                throw new InvalidOperationException("DNS atomic rollback left incomplete staging data.");
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
        return "DNS_READBACK_GUARD_SMOKE_OK preApplyValidation=7 targetedReadback=6 " +
            "rollbackPreflight=9 atomicFileCancelAndCommit=true networkWrites=0";
    }

    private static bool IsDnsTargetAdapter(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up) return false;
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;

        string identity = (nic.Name + " " + nic.Description).ToLowerInvariant();
        string[] blocked = { "wfp", "lightweight filter", "filter-", "hyper-v", "vethernet", "virtual", "vpn", "wireguard", "tap", "tun", "loopback", "bluetooth" };
        if (blocked.Any(identity.Contains)) return false;

        try
        {
            var props = nic.GetIPProperties();
            bool hasIpv4 = props.UnicastAddresses.Any(x =>
                x.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.Address));
            bool hasDefaultGateway = props.GatewayAddresses.Any(x =>
                x.Address.AddressFamily == AddressFamily.InterNetwork &&
                !x.Address.Equals(IPAddress.Any) && !x.Address.Equals(IPAddress.None));
            return hasIpv4 && hasDefaultGateway;
        }
        catch { return false; }
    }

    private static IEnumerable<NetworkInterface> ActiveAdapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(IsDnsTargetAdapter)
        .OrderByDescending(x => x.Speed);

    // netsh wraps adapter names in quotes. Validate before preparing a
    // rollback snapshot or invoking ANY network command, including restore.
    private static bool IsSafeAdapterName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 255 &&
        !name.Any(c => c == '"' || char.IsControl(c));

    // A rollback is a write boundary. Check the entire saved transaction and
    // ALL original adapter identities before changing even the first NIC.
    // Treat this local file as untrusted: malformed/old data cannot become
    // arbitrary netsh arguments or a partially applied recovery.
    private static DnsRollbackSnapshot ValidateRollbackSnapshot(
        DnsRollbackSnapshot? source, IEnumerable<string> currentAdapterNames)
    {
        if (source?.Adapters is not { Length: > 0 and <= 32 })
            throw new InvalidOperationException("DNS rollback: prázdný nebo neplatný snapshot.");
        var existing = new HashSet<string>(currentAdapterNames,
            StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validated = new List<DnsAdapterSnapshot>(source.Adapters.Length);
        foreach (DnsAdapterSnapshot adapter in source.Adapters)
        {
            if (adapter == null || !IsSafeAdapterName(adapter.Name) ||
                !visited.Add(adapter.Name) || !existing.Contains(adapter.Name))
                throw new InvalidOperationException(
                    "DNS rollback: neplatný, duplicitní nebo chybějící adaptér.");
            if (adapter.Servers == null || adapter.Servers.Length > 16)
                throw new InvalidOperationException(
                    "DNS rollback: neplatný seznam původních resolverů.");
            var servers = new List<string>(adapter.Servers.Length);
            foreach (string value in adapter.Servers)
            {
                if (string.IsNullOrWhiteSpace(value) ||
                    !IPAddress.TryParse(value, out var ip) ||
                    ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
                    ip.Equals(IPAddress.Broadcast) || ip.IsIPv6Multicast ||
                    (ip.AddressFamily == AddressFamily.InterNetwork &&
                     !string.Equals(value, ip.ToString(), StringComparison.Ordinal)))
                    throw new InvalidOperationException(
                        "DNS rollback: neplatná původní DNS adresa.");
                string normalized = ip.ToString();
                if (!servers.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    servers.Add(normalized);
            }
            validated.Add(new DnsAdapterSnapshot(adapter.Name, servers.ToArray()));
        }
        return new DnsRollbackSnapshot(source.CapturedAtUtc, validated.ToArray());
    }

    internal static bool HasRollbackSnapshot => File.Exists(RollbackPath);

    internal static string GetActiveDnsSummary()
    {
        FaultInjectionService.ThrowIfInjected("dns.read");
        try
        {
            var adapter = ActiveAdapters().FirstOrDefault();
            if (adapter == null) return "Aktuální DNS: adaptér nenalezen";
            string[] servers = adapter.GetIPProperties().DnsAddresses.Select(x => x.ToString()).ToArray();
            string dns = servers.Length == 0 ? "DHCP / nezjištěno" : string.Join(" / ", servers.Take(2));
            return $"{adapter.Name} • DNS {dns}";
        }
        catch { return "Aktuální DNS: nelze načíst"; }
    }

    internal static async Task<DnsApplyResult> ApplyServersAsync(
        IReadOnlyList<string> ipv4,
        IReadOnlyList<string> ipv6,
        CancellationToken token = default)
    {
        await FaultInjectionService.InjectAsync("dns.apply", token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        string[] v4 = ValidateDnsAddresses(ipv4, AddressFamily.InterNetwork);
        string[] v6 = ValidateDnsAddresses(ipv6, AddressFamily.InterNetworkV6);

        if (v4.Length == 0 && v6.Length == 0)
            throw new InvalidOperationException("Nebyla vybrána žádná použitelná DNS adresa.");

        var adapters = ActiveAdapters().ToArray();
        if (adapters.Length == 0)
            throw new InvalidOperationException("Nenalezen žádný aktivní síťový adaptér.");
        if (adapters.Any(adapter => !IsSafeAdapterName(adapter.Name)) ||
            adapters.Select(adapter => adapter.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != adapters.Length)
            throw new InvalidOperationException("DNS: neplatný nebo duplicitní název cílového adaptéru.");

        await SaveRollbackSnapshotAsync(adapters, token);

        foreach (var adapter in adapters)
        {
            string[] ordered = v4.Concat(v6).ToArray();
            await RunNetshAsync(
                $"dnsclient set dnsserver name=\"{adapter.Name}\" source=static address={ordered[0]} validate=no",
                token);

            for (int i = 1; i < ordered.Length; i++)
            {
                await RunNetshAsync(
                    $"dnsclient add dnsserver name=\"{adapter.Name}\" address={ordered[i]} index={i + 1} validate=no",
                    token);
            }
        }

        await PrivilegedProcessRunner.RunAsync("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        await Task.Delay(350, token);

        string[] expected = v4.Concat(v6).ToArray();
        var readback = ActiveAdapters().Select(adapter =>
            new DnsAdapterSnapshot(adapter.Name, adapter.GetIPProperties().DnsAddresses
                .Select(x => x.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
        string[] verified = VerifyAppliedAdapterReadback(
            adapters.Select(adapter => adapter.Name), expected, readback);

        AdvancedCenterService.InvalidateNetworkAnalyzer();
        return new DnsApplyResult(v4, v6, verified);
    }

    internal static async Task<string> RollbackAsync(CancellationToken token = default)
    {
        if (!File.Exists(RollbackPath))
            throw new InvalidOperationException("Není uložená předchozí DNS konfigurace.");

        string json = await File.ReadAllTextAsync(RollbackPath, token);
        var snapshot = ValidateRollbackSnapshot(
            JsonSerializer.Deserialize<DnsRollbackSnapshot>(json),
            NetworkInterface.GetAllNetworkInterfaces().Select(adapter => adapter.Name));
        token.ThrowIfCancellationRequested();

        foreach (var adapter in snapshot.Adapters)
        {
            token.ThrowIfCancellationRequested();
            string[] servers = adapter.Servers.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            if (servers.Length == 0)
            {
                await RunNetshAsync($"dnsclient set dnsserver name=\"{adapter.Name}\" source=dhcp", token);
                continue;
            }

            await RunNetshAsync(
                $"dnsclient set dnsserver name=\"{adapter.Name}\" source=static address={servers[0]} validate=no",
                token);

            for (int i = 1; i < servers.Length; i++)
                await RunNetshAsync(
                    $"dnsclient add dnsserver name=\"{adapter.Name}\" address={servers[i]} index={i + 1} validate=no",
                    token);
        }

        await PrivilegedProcessRunner.RunAsync("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(15), token).ConfigureAwait(false);

        // Static snapshots must match after rollback. DHCP snapshots intentionally skip
        // exact server comparison because the lease can legitimately return different resolvers.
        var currentAdapters = NetworkInterface.GetAllNetworkInterfaces()
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in snapshot.Adapters)
        {
            string[] expected = adapter.Servers.Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (expected.Length == 0) continue;
            if (!currentAdapters.TryGetValue(adapter.Name, out var currentAdapter))
                throw new InvalidOperationException($"DNS rollback read-back: adaptér {adapter.Name} nebyl nalezen.");
            string[] current = currentAdapter.GetIPProperties().DnsAddresses
                .Select(x => x.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!expected.All(x => current.Contains(x, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"DNS rollback read-back nepotvrdil snapshot na adaptéru {adapter.Name}. Aktuálně: {string.Join(", ", current)}");
            string[] unexpected = FindUnexpectedResolvers(expected, current);
            if (unexpected.Length > 0)
                throw new InvalidOperationException(
                    $"DNS rollback read-back našel neočekávaný resolver na adaptéru {adapter.Name}: {string.Join(", ", unexpected)}");
        }

        AdvancedCenterService.InvalidateNetworkAnalyzer();
        return $"Předchozí DNS konfigurace obnovena a ověřena ({snapshot.CapturedAtUtc.ToLocalTime():g}).";
    }

    private static async Task SaveRollbackSnapshotAsync(NetworkInterface[] adapters, CancellationToken token)
    {
        Directory.CreateDirectory(LogService.Root);
        var snapshot = new DnsRollbackSnapshot(
            DateTime.UtcNow,
            adapters.Select(a => new DnsAdapterSnapshot(
                a.Name,
                a.GetIPProperties().DnsAddresses.Select(x => x.ToString()).ToArray())).ToArray());

        await WriteSnapshotAtomicallyAsync(RollbackPath,
            JsonSerializer.Serialize(snapshot, IndentedJson), token);
    }

    private static async Task WriteSnapshotAtomicallyAsync(
        string path, string json, CancellationToken token)
    {
        // Keep the last complete rollback until the new file is fully written.
        // A same-directory atomic replacement avoids truncated JSON on cancel.
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await File.WriteAllTextAsync(pending, json, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); }
            catch (IOException) { /* Preserve original failure; next attempt uses unique filename. */ }
        }
    }

    private static async Task RunNetshAsync(string args, CancellationToken token)
    {
        var result = await PrivilegedProcessRunner.RunAsync("netsh.exe", args, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"netsh {args} selhal: {result.StdErr}{result.StdOut}");
    }
}
