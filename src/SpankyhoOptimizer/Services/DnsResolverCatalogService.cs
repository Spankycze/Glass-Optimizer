using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal sealed record DnsCatalogUpdateResult(int Count, int UdpTcp, int Doh, int Dot, int Doq, DateTime UpdatedAtUtc, string Source);

internal static class DnsResolverCatalogService
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private const string UpstreamUrl = "https://raw.githubusercontent.com/center2055/DNS-Hop/main/src/DNSHop.App/Services/DnsServerListService.cs";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly string CatalogPath = Path.Combine(LogService.Root, "dns-resolvers.json");
    private static readonly string MetaPath = Path.Combine(LogService.Root, "dns-resolvers.meta.json");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IReadOnlyList<DnsResolverDefinition>? _cache;

    internal static async Task<IReadOnlyList<DnsResolverDefinition>> GetServersAsync(CancellationToken token = default)
    {
        if (_cache != null) return _cache;
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_cache != null) return _cache;
            var disk = await TryLoadDiskAsync(token).ConfigureAwait(false);
            _cache = disk.Count > 0 ? disk : DnsBenchmarkCatalog.Servers;
            return _cache;
        }
        finally { Gate.Release(); }
    }

    internal static async Task<DnsCatalogUpdateResult> UpdateAsync(CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string source = await Http.GetStringAsync(UpstreamUrl, token).ConfigureAwait(false);
            var parsed = ParseUpstreamSource(source);
            if (parsed.Count < 100)
                throw new InvalidOperationException($"Upstream DNS katalog vypadá neúplně ({parsed.Count} položek).");

            Directory.CreateDirectory(LogService.Root);
            string json = JsonSerializer.Serialize(parsed, IndentedJson);
            await AtomicFileService.WriteTextAsync(CatalogPath, json, token).ConfigureAwait(false);

            var result = BuildResult(parsed, DateTime.UtcNow, "DNS-Hop upstream");
            await AtomicFileService.WriteTextAsync(MetaPath, JsonSerializer.Serialize(result, IndentedJson), token).ConfigureAwait(false);
            _cache = parsed;
            LogService.Info("network", $"DNS_CATALOG_UPDATED count={result.Count} udp={result.UdpTcp} doh={result.Doh} dot={result.Dot} doq={result.Doq}");
            return result;
        }
        finally { Gate.Release(); }
    }

    internal static async Task<DnsCatalogUpdateResult> EnsureFreshAsync(
        TimeSpan maxAge,
        CancellationToken token = default)
    {
        DateTime updated = File.Exists(CatalogPath) ? File.GetLastWriteTimeUtc(CatalogPath) : DateTime.MinValue;
        if (updated != DateTime.MinValue && DateTime.UtcNow - updated <= maxAge)
            return await GetStatusAsync(token).ConfigureAwait(false);

        try
        {
            return await UpdateAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Error("network", "Automatic DNS catalog refresh failed; using current catalog", ex);
            return await GetStatusAsync(token).ConfigureAwait(false);
        }
    }

    internal static async Task<DnsCatalogUpdateResult> GetStatusAsync(CancellationToken token = default)
    {
        var servers = await GetServersAsync(token).ConfigureAwait(false);
        DateTime updated = File.Exists(CatalogPath) ? File.GetLastWriteTimeUtc(CatalogPath) : DateTime.MinValue;
        return BuildResult(servers, updated, File.Exists(CatalogPath) ? "Local updated catalog" : "Built-in DNS-Hop snapshot");
    }
    private static async Task<IReadOnlyList<DnsResolverDefinition>> TryLoadDiskAsync(CancellationToken token)
    {
        try
        {
            if (!File.Exists(CatalogPath)) return Array.Empty<DnsResolverDefinition>();
            string json = await File.ReadAllTextAsync(CatalogPath, token).ConfigureAwait(false);
            var items = JsonSerializer.Deserialize<List<DnsResolverDefinition>>(json);
            return items?.Where(IsValid).GroupBy(Key, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToArray()
                   ?? Array.Empty<DnsResolverDefinition>();
        }
        catch (Exception ex)
        {
            LogService.Error("network", "DNS catalog load failed; using built-in fallback", ex);
            return Array.Empty<DnsResolverDefinition>();
        }
    }

    internal static IReadOnlyList<DnsResolverDefinition> ParseUpstreamSource(string source)
    {
        var list = new List<DnsResolverDefinition>();

        foreach (Match m in Regex.Matches(source, @"CreateUdpTcp\(""([^""]+)""\s*,\s*""([^""]+)""(?:\s*,\s*(\d+))?\)"))
        {
            int port = m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out int p) ? p : 53;
            list.Add(new DnsResolverDefinition(m.Groups[2].Value, m.Groups[1].Value, DnsResolverProtocol.UdpTcp, port));
        }

        foreach (Match m in Regex.Matches(source, @"CreateDoh\(""([^""]+)""\s*,\s*""([^""]+)""\)"))
        {
            string endpoint = m.Groups[1].Value;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) continue;
            list.Add(new DnsResolverDefinition(m.Groups[2].Value, endpoint, DnsResolverProtocol.Doh, uri.Port));
        }

        foreach (Match m in Regex.Matches(source, @"CreateDot\(""([^""]+)""\s*,\s*""([^""]+)""\s*,\s*""([^""]+)""(?:\s*,\s*(\d+))?\)"))
        {
            int port = m.Groups[4].Success && int.TryParse(m.Groups[4].Value, out int p) ? p : 853;
            list.Add(new DnsResolverDefinition(m.Groups[3].Value, m.Groups[1].Value, DnsResolverProtocol.Dot, port, m.Groups[2].Value));
        }

        foreach (Match m in Regex.Matches(source, @"CreateDoq\(""([^""]+)""\s*,\s*""([^""]+)""(?:\s*,\s*""([^""]*)"")?(?:\s*,\s*(\d+))?\)"))
        {
            int port = m.Groups[4].Success && int.TryParse(m.Groups[4].Value, out int p) ? p : 853;
            string tls = m.Groups[3].Success && !string.IsNullOrWhiteSpace(m.Groups[3].Value) ? m.Groups[3].Value : m.Groups[1].Value;
            list.Add(new DnsResolverDefinition(m.Groups[2].Value, m.Groups[1].Value, DnsResolverProtocol.Doq, port, tls));
        }

        return list.Where(IsValid)
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
    }
    private static bool IsValid(DnsResolverDefinition x)
    {
        if (string.IsNullOrWhiteSpace(x.Provider) || string.IsNullOrWhiteSpace(x.Address)) return false;
        if (x.Port is < 1 or > 65535) return false;
        if (x.Protocol == DnsResolverProtocol.Doh) return Uri.TryCreate(x.Address, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
        return true;
    }

    private static string Key(DnsResolverDefinition x) => $"{x.Protocol}|{x.Address}|{x.Port}|{x.TlsHost}";

    private static DnsCatalogUpdateResult BuildResult(IEnumerable<DnsResolverDefinition> servers, DateTime updated, string source)
    {
        var a = servers.ToArray();
        return new DnsCatalogUpdateResult(
            a.Length,
            a.Count(x => x.Protocol == DnsResolverProtocol.UdpTcp),
            a.Count(x => x.Protocol == DnsResolverProtocol.Doh),
            a.Count(x => x.Protocol == DnsResolverProtocol.Dot),
            a.Count(x => x.Protocol == DnsResolverProtocol.Doq),
            updated,
            source);
    }
}
