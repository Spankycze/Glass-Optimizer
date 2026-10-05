using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal enum DnsResolverProtocol { UdpTcp, Doh, Dot, Doq }

internal sealed record DnsResolverDefinition(
    string Provider,
    string Address,
    DnsResolverProtocol Protocol,
    int Port,
    string TlsHost = "")
{
    internal string EndpointDisplay => Protocol switch
    {
        DnsResolverProtocol.Doh => Address,
        DnsResolverProtocol.Dot => $"{Address}:{Port}",
        DnsResolverProtocol.Doq => $"{Address}:{Port}",
        _ => Address
    };
}

internal sealed record DnsBenchmarkResult(
    string Name,
    string Address,
    DnsResolverProtocol Protocol,
    double? CachedMs,
    double? UncachedMs,
    double? DotComMs,
    double BestMs,
    double MinMs,
    double MaxMs,
    double JitterMs,
    int Successful,
    int Attempts,
    bool SupportsDnssec,
    bool RedirectsNxDomain,
    string? Error)
{
    internal bool IsAlive => CachedMs.HasValue || UncachedMs.HasValue || DotComMs.HasValue;
    internal double Reliability => Attempts == 0 ? 0 : (double)Successful / Attempts;
    internal double AverageMs => new double?[] { CachedMs, UncachedMs, DotComMs }.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(double.MaxValue).Average();
    internal double CachedScore => LatencyScore(CachedMs);
    internal double UncachedScore => LatencyScore(UncachedMs);
    internal double DotComScore => LatencyScore(DotComMs);

    private static double LatencyScore(double? value)
    {
        if (!value.HasValue || value.Value < 0 || double.IsInfinity(value.Value) || double.IsNaN(value.Value)) return 0;
        return Math.Round(100d / (1d + value.Value / 20d), 1);
    }

    internal double OverallScore
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Error) || !IsAlive || RedirectsNxDomain || double.IsInfinity(AverageMs) || double.IsNaN(AverageMs)) return 0;
            double latency = 1d / (1d + AverageMs / 20d);
            double stability = 1d / (1d + Math.Max(0, JitterMs) / 10d);
            double dnssec = SupportsDnssec ? 1d : 0d;
            double clean = RedirectsNxDomain ? 0d : 1d;
            return Math.Round((latency * 45d) + (stability * 25d) + (Reliability * 15d) + (dnssec * 10d) + (clean * 5d), 1);
        }
    }

    internal string HealthStatus
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Error)) return Error;
            if (RedirectsNxDomain) return "Hijacking / NXDOMAIN redirect";
            if (!IsAlive)
            {
                if (Protocol is DnsResolverProtocol.Dot or DnsResolverProtocol.Doq) return "TLS/QUIC nebo timeout";
                if (Protocol == DnsResolverProtocol.Doh) return "DoH/HTTP nebo timeout";
                if (IPAddress.TryParse(Address, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6) return "IPv6 nedostupné / timeout";
                return "Timeout / bez odpovědi";
            }
            if (!SupportsDnssec) return "DNSSEC neověřen";
            if (Reliability < 0.8) return "Nestabilní";
            return "Healthy";
        }
    }
}

internal sealed record DnsBenchmarkProgress(
    int Completed,
    int Total,
    string CurrentName,
    string CurrentAddress,
    DnsBenchmarkResult? Result,
    int CompletedQueries = 0,
    int TotalQueries = 0,
    string Phase = "");

internal sealed record DnsBenchmarkRunOptions(
    int TimeoutMs = 2500,
    int Concurrency = 8,
    int AttemptsPerProbe = 3,
    bool EnableDnssec = true,
    bool EnableHijack = true);

internal static class DnsBenchmarkService
{
    private static readonly HttpClient DohClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly string[] CachedDomains = ["google.com", "apple.com", "microsoft.com"];
    private static readonly object ResultCacheLock = new();
    private static IReadOnlyList<DnsBenchmarkResult>? _cachedResults;
    private static DateTime _cachedResultsUtc;
    private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromMinutes(2);
    private const string DnssecBroken = "dnssec-failed.org";
    private const string DnssecControl = "iana.org";

    internal static async Task<IReadOnlyList<DnsBenchmarkResult>> RunAsync(
        IProgress<DnsBenchmarkProgress>? progress = null,
        CancellationToken token = default,
        bool force = false)
    {
        if (!force)
        {
            lock (ResultCacheLock)
            {
                if (_cachedResults != null && DateTime.UtcNow - _cachedResultsUtc < ResultCacheTtl)
                    return _cachedResults;
            }
        }

        var results = await RunTwoStageAsync(progress, token).ConfigureAwait(false);
        lock (ResultCacheLock)
        {
            _cachedResults = results.ToArray();
            _cachedResultsUtc = DateTime.UtcNow;
        }
        return results;
    }

    internal static void InvalidateCache()
    {
        lock (ResultCacheLock)
        {
            _cachedResults = null;
            _cachedResultsUtc = default;
        }
    }

    internal static Task<IReadOnlyList<DnsBenchmarkResult>> RunSmokeAsync(CancellationToken token = default)
        => RunTwoStageAsync(
            null,
            token,
            preflightLimit: 24,
            shortlistLimit: 8,
            fullOptions: new DnsBenchmarkRunOptions(1200, 4, 1, true, true));

    internal static Task<DnsBenchmarkResult> RetestUdpAsync(
        string name,
        string address,
        CancellationToken token = default)
    {
        var server = new DnsResolverDefinition(name, address, DnsResolverProtocol.UdpTcp, 53);
        return BenchmarkOneAsync(server, new DnsBenchmarkRunOptions(1500, 1, 2, true, true), _ => { }, token);
    }

    private static async Task<IReadOnlyList<DnsBenchmarkResult>> RunTwoStageAsync(
        IProgress<DnsBenchmarkProgress>? progress,
        CancellationToken token,
        int? preflightLimit = null,
        int shortlistLimit = 60,
        DnsBenchmarkRunOptions? fullOptions = null)
    {
        var all = (await DnsResolverCatalogService.GetServersAsync(token).ConfigureAwait(false)).ToArray();
        var catalog = preflightLimit.HasValue ? all.Take(preflightLimit.Value).ToArray() : all;
        if (catalog.Length == 0) return Array.Empty<DnsBenchmarkResult>();

        var preflight = new (DnsResolverDefinition Server, QueryResult Result)[catalog.Length];
        using var gate = new SemaphoreSlim(24);
        int completed = 0;

        var tasks = Enumerable.Range(0, catalog.Length).Select(async i =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var server = catalog[i];
                var result = await QueryAsync(server, "google.com", 1, 1200, token).ConfigureAwait(false);
                preflight[i] = (server, result);
                int done = Interlocked.Increment(ref completed);
                progress?.Report(new DnsBenchmarkProgress(
                    done, catalog.Length, server.Provider, server.EndpointDisplay, null,
                    done, catalog.Length, "preflight"));
            }
            finally { gate.Release(); }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var shortlisted = preflight
            .Where(x => x.Result.Success)
            .OrderBy(x => x.Result.Milliseconds)
            .Take(Math.Max(1, shortlistLimit))
            .Select(x => x.Server)
            .ToArray();

        var shortlistKeys = shortlisted.Select(ServerKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = preflight
            .Where(x => !shortlistKeys.Contains(ServerKey(x.Server)))
            .Select(x =>
            {
                bool ok = x.Result.Success;
                double ms = ok ? x.Result.Milliseconds : double.MaxValue;
                string reason = ok ? "Předvýběr – mimo TOP 60" : PreflightFailure(x.Server);
                return new DnsBenchmarkResult(
                    x.Server.Provider, x.Server.EndpointDisplay, x.Server.Protocol,
                    ok ? ms : null, null, null,
                    ms, ms, ms, 0,
                    ok ? 1 : 0, 1, false, false, reason);
            })
            .ToArray();

        if (shortlisted.Length == 0)
            return excluded;

        var full = await RunInternalAsync(
            null,
            fullOptions ?? new DnsBenchmarkRunOptions(),
            progress,
            token,
            shortlisted).ConfigureAwait(false);

        return full
            .Concat(excluded)
            .OrderBy(x => x.IsAlive ? 0 : 1)
            .ThenBy(x => x.RedirectsNxDomain ? 1 : 0)
            .ThenByDescending(x => x.OverallScore)
            .ThenBy(x => x.AverageMs)
            .ToArray();
    }

    private static string ServerKey(DnsResolverDefinition x) =>
        $"{x.Protocol}|{x.Address}|{x.Port}|{x.TlsHost}";

    private static string PreflightFailure(DnsResolverDefinition server)
    {
        if (server.Protocol is DnsResolverProtocol.Dot or DnsResolverProtocol.Doq)
            return "Předvýběr – TLS/QUIC nebo timeout";
        if (server.Protocol == DnsResolverProtocol.Doh)
            return "Předvýběr – DoH/HTTP nebo timeout";
        if (IPAddress.TryParse(server.Address, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
            return "Předvýběr – IPv6 nedostupné / timeout";
        return "Předvýběr – timeout / bez odpovědi";
    }
    private static async Task<IReadOnlyList<DnsBenchmarkResult>> RunInternalAsync(
        int? maxServers,
        DnsBenchmarkRunOptions options,
        IProgress<DnsBenchmarkProgress>? progress,
        CancellationToken token,
        IReadOnlyList<DnsResolverDefinition>? selectedServers = null)
    {
        IReadOnlyList<DnsResolverDefinition> catalog = selectedServers
            ?? await DnsResolverCatalogService.GetServersAsync(token).ConfigureAwait(false);
        var servers = maxServers.HasValue ? catalog.Take(maxServers.Value).ToArray() : catalog.ToArray();
        if (servers.Length == 0) return Array.Empty<DnsBenchmarkResult>();

        int perServerQueries = (options.AttemptsPerProbe * 3)
            + (options.EnableDnssec ? options.AttemptsPerProbe * 2 : 0)
            + (options.EnableHijack ? Math.Max(3, options.AttemptsPerProbe) : 0);
        int totalQueries = servers.Length * perServerQueries;
        int completedQueries = 0;
        int completedServers = 0;
        var sw = Stopwatch.StartNew();

        using var gate = new SemaphoreSlim(Math.Clamp(options.Concurrency, 1, 16));
        var tasks = servers.Select(async server =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                void Tick(string phase)
                {
                    int q = Interlocked.Increment(ref completedQueries);
                    progress?.Report(new DnsBenchmarkProgress(
                        Volatile.Read(ref completedServers), servers.Length,
                        server.Provider, server.EndpointDisplay, null,
                        q, totalQueries, phase));
                }

                var result = await BenchmarkOneAsync(server, options, Tick, token).ConfigureAwait(false);
                int done = Interlocked.Increment(ref completedServers);
                progress?.Report(new DnsBenchmarkProgress(
                    done, servers.Length, server.Provider, server.EndpointDisplay, result,
                    Volatile.Read(ref completedQueries), totalQueries, "done"));
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                int done = Interlocked.Increment(ref completedServers);
                return new DnsBenchmarkResult(server.Provider, server.EndpointDisplay, server.Protocol,
                    null, null, null, double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue,
                    0, perServerQueries, false, false, ex.Message);
            }
            finally { gate.Release(); }
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        LogService.Info("network", $"DNS_BENCHMARK_COMPLETE servers={servers.Length} queries={completedQueries} elapsedMs={sw.ElapsedMilliseconds}");
        return results
            .OrderBy(x => x.IsAlive ? 0 : 1)
            .ThenBy(x => x.RedirectsNxDomain ? 1 : 0)
            .ThenByDescending(x => x.OverallScore)
            .ThenBy(x => x.AverageMs)
            .ToArray();
    }

    private static async Task<DnsBenchmarkResult> BenchmarkOneAsync(
        DnsResolverDefinition server,
        DnsBenchmarkRunOptions options,
        Action<string> tick,
        CancellationToken token)
    {
        int ok = 0, attempts = 0;
        double best = double.MaxValue;
        var allSamples = new List<double>(options.AttemptsPerProbe * 3);

        async Task<double?> ProbeAsync(string domain, ushort type, string phase)
        {
            var samples = new List<double>();
            for (int i = 0; i < options.AttemptsPerProbe; i++)
            {
                token.ThrowIfCancellationRequested();
                attempts++;
                var r = await QueryAsync(server, domain, type, options.TimeoutMs, token).ConfigureAwait(false);
                tick(phase);
                if (r.Success)
                {
                    ok++;
                    samples.Add(r.Milliseconds);
                    allSamples.Add(r.Milliseconds);
                    best = Math.Min(best, r.Milliseconds);
                }
            }
            return samples.Count == 0 ? null : samples.Average();
        }

        double? cached = null;
        var cachedSamples = new List<double>();
        for (int i = 0; i < options.AttemptsPerProbe; i++)
        {
            attempts++;
            var r = await QueryAsync(server, CachedDomains[i % CachedDomains.Length], 1, options.TimeoutMs, token).ConfigureAwait(false);
            tick("cached");
            if (r.Success) { ok++; cachedSamples.Add(r.Milliseconds); allSamples.Add(r.Milliseconds); best = Math.Min(best, r.Milliseconds); }
        }
        if (cachedSamples.Count > 0) cached = cachedSamples.Average();

        var uncachedSamples = new List<double>();
        for (int i = 0; i < options.AttemptsPerProbe; i++)
        {
            attempts++;
            string domain = $"{Guid.NewGuid():N}.com";
            var r = await QueryAsync(server, domain, 1, options.TimeoutMs, token).ConfigureAwait(false);
            tick("uncached");
            if (r.Success) { ok++; uncachedSamples.Add(r.Milliseconds); allSamples.Add(r.Milliseconds); best = Math.Min(best, r.Milliseconds); }
        }
        double? uncached = uncachedSamples.Count == 0 ? null : uncachedSamples.Average();

        double? dotcom = await ProbeAsync("com", 2, "dotcom").ConfigureAwait(false);

        bool dnssec = false;
        if (options.EnableDnssec)
        {
            bool brokenServFail = false, controlOk = false;
            for (int i = 0; i < options.AttemptsPerProbe; i++)
            {
                attempts++;
                var broken = await QueryAsync(server, DnssecBroken, 1, options.TimeoutMs, token).ConfigureAwait(false);
                tick("dnssec-broken");
                if (broken.TransportOk) { ok++; brokenServFail |= broken.Rcode == 2; }
                attempts++;
                var control = await QueryAsync(server, DnssecControl, 1, options.TimeoutMs, token).ConfigureAwait(false);
                tick("dnssec-control");
                if (control.Success) { ok++; controlOk = true; }
            }
            dnssec = brokenServFail && controlOk;
        }
        bool redirects = false;
        if (options.EnableHijack)
        {
            int hijackCount = Math.Max(3, options.AttemptsPerProbe);
            for (int i = 0; i < hijackCount; i++)
            {
                attempts++;
                string invalid = $"{Guid.NewGuid():N}.invalid";
                var r = await QueryAsync(server, invalid, 1, options.TimeoutMs, token).ConfigureAwait(false);
                tick("hijack");
                if (r.TransportOk)
                {
                    ok++;
                    if (r.Rcode == 0) redirects = true;
                }
            }
        }

        double min = allSamples.Count == 0 ? double.MaxValue : allSamples.Min();
        double max = allSamples.Count == 0 ? double.MaxValue : allSamples.Max();
        double jitter = 0;
        if (allSamples.Count > 1)
        {
            double mean = allSamples.Average();
            jitter = Math.Sqrt(allSamples.Sum(x => Math.Pow(x - mean, 2)) / allSamples.Count);
        }

        return new DnsBenchmarkResult(
            server.Provider, server.EndpointDisplay, server.Protocol,
            cached, uncached, dotcom,
            best, min, max, jitter, ok, attempts, dnssec, redirects, null);
    }

    private sealed record QueryResult(bool TransportOk, bool Success, int Rcode, double Milliseconds);

    private static async Task<QueryResult> QueryAsync(
        DnsResolverDefinition server,
        string domain,
        ushort type,
        int timeoutMs,
        CancellationToken token)
    {
        byte[] request = BuildQuery(domain, type);
        return server.Protocol switch
        {
            DnsResolverProtocol.Doh => await QueryDohAsync(server, request, timeoutMs, token).ConfigureAwait(false),
            DnsResolverProtocol.Dot => await QueryDotAsync(server, request, timeoutMs, token).ConfigureAwait(false),
            DnsResolverProtocol.Doq => await QueryDoqAsync(server, request, timeoutMs, token).ConfigureAwait(false),
            _ => await QueryUdpAsync(server, request, timeoutMs, token).ConfigureAwait(false)
        };
    }

    private static async Task<QueryResult> QueryUdpAsync(DnsResolverDefinition server, byte[] request, int timeoutMs, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!IPAddress.TryParse(server.Address, out var ip)) return new(false, false, -1, 0);
            using var udp = new UdpClient(ip.AddressFamily);
            udp.Connect(ip, server.Port);
            await udp.SendAsync(request, request.Length).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeoutMs);
            var response = await udp.ReceiveAsync(cts.Token).AsTask().ConfigureAwait(false);
            sw.Stop();
            return ParseResponse(response.Buffer, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, false, -1, timeoutMs); }
        catch (SocketException) { return new(false, false, -1, sw.Elapsed.TotalMilliseconds); }
    }

    private static async Task<QueryResult> QueryDohAsync(DnsResolverDefinition server, byte[] request, int timeoutMs, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, server.Address);
            msg.Headers.Accept.ParseAdd("application/dns-message");
            msg.Content = new ByteArrayContent(request);
            msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeoutMs);
            using var response = await DohClient.SendAsync(msg, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            byte[] data = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            sw.Stop();
            return response.IsSuccessStatusCode ? ParseResponse(data, sw.Elapsed.TotalMilliseconds) : new(true, false, (int)response.StatusCode, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, false, -1, timeoutMs); }
        catch (HttpRequestException) { return new(false, false, -1, sw.Elapsed.TotalMilliseconds); }
    }
    private static async Task<QueryResult> QueryDotAsync(DnsResolverDefinition server, byte[] request, int timeoutMs, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeoutMs);
            string host = server.Address;
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, server.Port, cts.Token).ConfigureAwait(false);
            await using var ssl = new SslStream(tcp.GetStream(), false);
            string tlsHost = string.IsNullOrWhiteSpace(server.TlsHost) ? host : server.TlsHost;
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = tlsHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, cts.Token).ConfigureAwait(false);

            byte[] prefix = [(byte)(request.Length >> 8), (byte)(request.Length & 0xff)];
            await ssl.WriteAsync(prefix, cts.Token).ConfigureAwait(false);
            await ssl.WriteAsync(request, cts.Token).ConfigureAwait(false);
            await ssl.FlushAsync(cts.Token).ConfigureAwait(false);

            byte[] len = new byte[2];
            await ReadExactAsync(ssl, len, cts.Token).ConfigureAwait(false);
            int size = (len[0] << 8) | len[1];
            if (size <= 0 || size > 65535) return new(true, false, -1, sw.Elapsed.TotalMilliseconds);
            byte[] data = new byte[size];
            await ReadExactAsync(ssl, data, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return ParseResponse(data, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, false, -1, timeoutMs); }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException) { return new(false, false, -1, sw.Elapsed.TotalMilliseconds); }
    }

    private static async Task<QueryResult> QueryDoqAsync(DnsResolverDefinition server, byte[] request, int timeoutMs, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!QuicConnection.IsSupported) return new(false, false, -1, 0);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeoutMs);

            string sni = string.IsNullOrWhiteSpace(server.TlsHost) ? server.Address : server.TlsHost;
            var options = new QuicClientConnectionOptions
            {
                RemoteEndPoint = new DnsEndPoint(server.Address, server.Port),
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = [new SslApplicationProtocol("doq")],
                    TargetHost = sni,
                    EnabledSslProtocols = SslProtocols.Tls13
                }
            };

            await using var connection = await QuicConnection.ConnectAsync(options, cts.Token).ConfigureAwait(false);
            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cts.Token).ConfigureAwait(false);
            request[0] = 0;
            request[1] = 0;
            byte[] prefix = [(byte)(request.Length >> 8), (byte)(request.Length & 0xff)];
            await stream.WriteAsync(prefix, cts.Token).ConfigureAwait(false);
            await stream.WriteAsync(request, completeWrites: true, cts.Token).ConfigureAwait(false);

            byte[] len = new byte[2];
            await ReadExactAsync(stream, len, cts.Token).ConfigureAwait(false);
            int size = (len[0] << 8) | len[1];
            if (size <= 0 || size > 65535) return new(true, false, -1, sw.Elapsed.TotalMilliseconds);
            byte[] data = new byte[size];
            await ReadExactAsync(stream, data, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return ParseResponse(data, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, false, -1, timeoutMs); }
        catch (Exception ex) when (ex is QuicException or SocketException or IOException or AuthenticationException)
        {
            return new(false, false, -1, sw.Elapsed.TotalMilliseconds);
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), token).ConfigureAwait(false);
            if (n == 0) throw new IOException("DNS stream ended early.");
            read += n;
        }
    }

    private static QueryResult ParseResponse(byte[] data, double ms)
    {
        if (data.Length < 12) return new(true, false, -1, ms);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));
        bool response = (flags & 0x8000) != 0;
        int rcode = flags & 0x000f;
        return new(true, response && rcode is 0 or 3, rcode, ms);
    }

    private static byte[] BuildQuery(string domain, ushort type)
    {
        ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        using var ms = new MemoryStream(96);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[..2], id);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(2, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(4, 2), 1);
        ms.Write(header);
        foreach (string label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail[..2], type);
        BinaryPrimitives.WriteUInt16BigEndian(tail.Slice(2, 2), 1);
        ms.Write(tail);
        return ms.ToArray();
    }
}
