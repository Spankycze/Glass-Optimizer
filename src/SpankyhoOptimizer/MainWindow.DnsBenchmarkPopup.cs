using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private int _dnsApplyBusy;
    private sealed record DnsApplyPlan(string[] IPv4, string[] IPv6)
    {
        internal string Primary => IPv4.Concat(IPv6).FirstOrDefault() ?? "—";
        internal string Secondary => IPv4.Concat(IPv6).Skip(1).FirstOrDefault() ?? "—";
    }

    private static DnsApplyPlan BuildDnsApplyPlan(
        DnsBenchmarkResult selected,
        IReadOnlyList<DnsBenchmarkResult> pool)
    {
        static bool IsV4(string address) => IPAddress.TryParse(address, out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        static bool IsV6(string address) => IPAddress.TryParse(address, out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

        string[] ChooseServers(Func<string, bool> familyMatch)
        {
            var family = new List<DnsBenchmarkResult>(pool.Count);
            foreach (DnsBenchmarkResult item in pool)
            {
                if (item.IsAlive && string.IsNullOrWhiteSpace(item.Error) &&
                    !item.RedirectsNxDomain && item.Protocol == DnsResolverProtocol.UdpTcp &&
                    familyMatch(item.Address))
                    family.Add(item);
            }

            family.Sort(static (a, b) =>
            {
                int score = b.OverallScore.CompareTo(a.OverallScore);
                return score != 0 ? score : a.AverageMs.CompareTo(b.AverageMs);
            });

            DnsBenchmarkResult? primary = null;
            foreach (DnsBenchmarkResult item in family)
            {
                if (!string.Equals(item.Name, selected.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                primary ??= item;
                if (string.Equals(item.Address, selected.Address, StringComparison.OrdinalIgnoreCase))
                {
                    primary = item;
                    break;
                }
            }
            primary ??= family.Count > 0 ? family[0] : null;
            if (primary == null) return Array.Empty<string>();

            DnsBenchmarkResult? companion = null;
            foreach (DnsBenchmarkResult item in family)
            {
                if (string.Equals(item.Address, primary.Address, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(item.Name, primary.Name, StringComparison.OrdinalIgnoreCase))
                {
                    companion = item;
                    break;
                }
            }

            if (companion == null)
            {
                foreach (DnsBenchmarkResult item in family)
                {
                    if (!string.Equals(item.Address, primary.Address, StringComparison.OrdinalIgnoreCase))
                    {
                        companion = item;
                        break;
                    }
                }
            }

            return companion == null
                ? new[] { primary.Address }
                : new[] { primary.Address, companion.Address };
        }

        return new DnsApplyPlan(ChooseServers(IsV4), ChooseServers(IsV6));
    }

    internal static string RunDnsApplyPlanSmoke()
    {
        static DnsBenchmarkResult R(string provider, string address, double ms, double jitter) =>
            new(provider, address, DnsResolverProtocol.UdpTcp,
                ms, ms + 1, ms + 2, ms, ms, ms + 2, jitter,
                9, 9, true, false, null);

        var selected = R("Provider A", "1.1.1.1", 10, 1);
        DnsBenchmarkResult[] pool =
        [
            selected,
            R("Provider A", "1.0.0.1", 11, 1),
            R("Provider B", "9.9.9.9", 12, 1)
        ];

        var plan = BuildDnsApplyPlan(selected, pool);
        if (plan.IPv4.Length < 2 ||
            plan.IPv4[0] != "1.1.1.1" ||
            plan.IPv4[1] != "9.9.9.9")
            throw new InvalidOperationException(
                $"DNS apply plan provider diversity selhala: {string.Join(", ", plan.IPv4)}");

        return "DNS_APPLY_PLAN_SMOKE_OK primary=ProviderA secondary=ProviderB";
    }
}
