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

    internal static string[] FindUnexpectedResolvers(
        IEnumerable<string> expected,
        IEnumerable<string> current) =>
        current
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => !expected.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToArray();

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
        return "DNS_READBACK_GUARD_SMOKE_OK";
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
        string[] v4 = ipv4.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        string[] v6 = ipv6.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();

        if (v4.Length == 0 && v6.Length == 0)
            throw new InvalidOperationException("Nebyla vybrána žádná použitelná DNS adresa.");

        var adapters = ActiveAdapters().ToArray();
        if (adapters.Length == 0)
            throw new InvalidOperationException("Nenalezen žádný aktivní síťový adaptér.");

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

        await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        await Task.Delay(350, token);

        string[] expected = v4.Concat(v6).ToArray();
        var verified = new List<string>();

        foreach (var adapter in ActiveAdapters())
        {
            string[] current = adapter.GetIPProperties().DnsAddresses
                .Select(x => x.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!expected.All(x => current.Contains(x, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Read-back nepotvrdil DNS na adaptéru {adapter.Name}. Aktuálně: {string.Join(", ", current)}");

            string[] unexpected = FindUnexpectedResolvers(expected, current);
            if (unexpected.Length > 0)
                throw new InvalidOperationException(
                    $"DNS fallback/leak kontrola našla na adaptéru {adapter.Name} neočekávaný resolver: {string.Join(", ", unexpected)}");

            verified.Add($"{adapter.Name}: {string.Join(", ", current)}");
        }

        AdvancedCenterService.InvalidateNetworkAnalyzer();
        return new DnsApplyResult(v4, v6, verified.ToArray());
    }

    internal static async Task<string> RollbackAsync(CancellationToken token = default)
    {
        if (!File.Exists(RollbackPath))
            throw new InvalidOperationException("Není uložená předchozí DNS konfigurace.");

        string json = await File.ReadAllTextAsync(RollbackPath, token);
        var snapshot = JsonSerializer.Deserialize<DnsRollbackSnapshot>(json)
            ?? throw new InvalidOperationException("DNS rollback snapshot je neplatný.");

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

        await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        AdvancedCenterService.InvalidateNetworkAnalyzer();
        return $"Předchozí DNS konfigurace obnovena ({snapshot.CapturedAtUtc.ToLocalTime():g}).";
    }

    private static async Task SaveRollbackSnapshotAsync(NetworkInterface[] adapters, CancellationToken token)
    {
        Directory.CreateDirectory(LogService.Root);
        var snapshot = new DnsRollbackSnapshot(
            DateTime.UtcNow,
            adapters.Select(a => new DnsAdapterSnapshot(
                a.Name,
                a.GetIPProperties().DnsAddresses.Select(x => x.ToString()).ToArray())).ToArray());

        await File.WriteAllTextAsync(
            RollbackPath,
            JsonSerializer.Serialize(snapshot, IndentedJson),
            token);
    }

    private static async Task RunNetshAsync(string args, CancellationToken token)
    {
        var result = await ProcessRunner.RunAsync("netsh.exe", args, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"netsh {args} selhal: {result.StdErr}{result.StdOut}");
    }
}
