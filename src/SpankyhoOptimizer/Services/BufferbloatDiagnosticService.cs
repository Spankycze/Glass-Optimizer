using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

/// <summary>Manual network loaded-latency test. No changes to NIC, TCP, DNS,
/// router or system settings. Bounded HTTPS test traffic is sent only after
/// an explicit user confirmation in the existing Internet section.</summary>
internal static class BufferbloatDiagnosticService
{
    internal sealed record Phase(string Name, int Sent, int Received, double AverageMs,
        double P95Ms, double JitterMs, long TransferBytes, int ProbesDuringTraffic);
    internal sealed record Report(Phase Idle, Phase Download, Phase Upload,
        bool DownEvidence, bool UpEvidence)
    {
        internal double DownloadDeltaMs => Math.Max(0, Download.AverageMs - Idle.AverageMs);
        internal double UploadDeltaMs => Math.Max(0, Upload.AverageMs - Idle.AverageMs);
        internal bool HasValidComparison => Idle.Received >= 5 && DownEvidence && UpEvidence;
    }

    internal const long MaxDownloadBytes = 192L * 1024 * 1024;
    internal const long MaxUploadBytes = 32L * 1024 * 1024;
    private const string PingHost = "1.1.1.1";
    private static readonly SemaphoreSlim TestGate = new(1, 1);

    internal static async Task<Report> RunAsync(CancellationToken token)
    {
        if (!await TestGate.WaitAsync(0, token).ConfigureAwait(false))
            throw new InvalidOperationException("Jiný bufferbloat test již běží.");
        try
        {
            token.ThrowIfCancellationRequested();
            using var http = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AutomaticDecompression = DecompressionMethods.None
            }) { Timeout = TimeSpan.FromSeconds(10) };
            var idle = await SampleAsync("Bez zátěže", () => false, token).ConfigureAwait(false);
            var down = await LoadedPhaseAsync(http, download: true, token).ConfigureAwait(false);
            var up = await LoadedPhaseAsync(http, download: false, token).ConfigureAwait(false);
            return new Report(idle, down, up,
                HasEvidence(idle, down, 8L * 1024 * 1024),
                HasEvidence(idle, up, 1L * 1024 * 1024));
        }
        finally { TestGate.Release(); }
    }

    internal static bool HasEvidence(Phase idle, Phase loaded, long minimumBytes) =>
        idle.Received >= 5 && loaded.Received >= 5 &&
        loaded.ProbesDuringTraffic >= 5 && loaded.TransferBytes >= minimumBytes;

    internal static string Interpretation(Report report)
    {
        if (!report.HasValidComparison)
            return "Nedostatečná zátěž nebo ICMP odpovědi: bufferbloat nelze spolehlivě vyhodnotit. " +
                   "Tento omezený test nemusí nasytit rychlou přípojku.";
        double rise = Math.Max(report.DownloadDeltaMs, report.UploadDeltaMs);
        return rise >= 60
            ? "Při zátěži výrazně roste odezva. Zvaž SQM (CAKE nebo fq_codel) na routeru a porovnej test před/po."
            : rise >= 20
                ? "Při zátěži odezva znatelně roste. Prověř SQM/QoS v routeru a zopakuj test."
                : "V tomto omezeném testu je nárůst latence nízký. Nejde o důkaz saturace celé přípojky.";
    }

    private static async Task<Phase> LoadedPhaseAsync(HttpClient http, bool download,
        CancellationToken token)
    {
        long bytes = 0;
        int active = 0;
        int workers = download ? 4 : 2;
        long maxBytes = download ? MaxDownloadBytes : MaxUploadBytes;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(TimeSpan.FromSeconds(8));
        var jobs = new Task[workers];
        for (int i = 0; i < workers; i++)
        {
            jobs[i] = TrafficAsync();
        }
        try
        {
            // Start real transfers before measuring latency under load.
            await Task.Delay(250, token).ConfigureAwait(false);
            var result = await SampleAsync(download ? "Download zátěž" : "Upload zátěž",
                () => Volatile.Read(ref active) > 0 && Interlocked.Read(ref bytes) > 0, token)
                .ConfigureAwait(false);
            return result with { TransferBytes = Interlocked.Read(ref bytes) };
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(jobs).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }

        async Task TrafficAsync()
        {
            byte[] send = download ? Array.Empty<byte>() : new byte[512 * 1024];
            byte[] receive = download ? new byte[64 * 1024] : Array.Empty<byte>();
            while (!stop.IsCancellationRequested && Interlocked.Read(ref bytes) < maxBytes)
            {
                try
                {
                    Interlocked.Increment(ref active);
                    if (download)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get,
                            "https://speed.cloudflare.com/__down?bytes=4194304");
                        using var response = await http.SendAsync(request,
                            HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        await using var stream = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
                        int read;
                        while (!stop.IsCancellationRequested &&
                               Interlocked.Read(ref bytes) < maxBytes &&
                               (read = await stream.ReadAsync(receive, stop.Token).ConfigureAwait(false)) > 0)
                            Interlocked.Add(ref bytes, read);
                    }
                    else
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post,
                            "https://speed.cloudflare.com/__up")
                        { Content = new ByteArrayContent(send) };
                        using var response = await http.SendAsync(request,
                            HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        Interlocked.Add(ref bytes, send.Length);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (HttpRequestException) { break; }
                catch (IOException) { break; }
                finally { Interlocked.Decrement(ref active); }
            }
        }
    }

    private static async Task<Phase> SampleAsync(string phase, Func<bool> trafficActive,
        CancellationToken token)
    {
        var readings = new List<double>(12);
        int sent = 0;
        int during = 0;
        using var ping = new Ping();
        for (int i = 0; i < 12; i++)
        {
            token.ThrowIfCancellationRequested();
            sent++;
            bool active = trafficActive();
            try
            {
                var reply = await ping.SendPingAsync(PingHost, 800)
                    .WaitAsync(token).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    readings.Add(reply.RoundtripTime);
                    if (active && trafficActive()) during++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is PingException or InvalidOperationException or Win32Exception)
            { /* Missing ICMP evidence is inconclusive, not zero latency. */ }
            if (i != 11) await Task.Delay(125, token).ConfigureAwait(false);
        }
        if (readings.Count == 0) return new Phase(phase, sent, 0, 0, 0, 0, 0, during);
        var sorted = readings.OrderBy(x => x).ToArray();
        double jitter = 0;
        for (int i = 1; i < readings.Count; i++)
            jitter += Math.Abs(readings[i] - readings[i - 1]);
        if (readings.Count > 1) jitter /= readings.Count - 1;
        return new Phase(phase, sent, readings.Count, readings.Average(),
            sorted[(int)Math.Ceiling(.95 * sorted.Length) - 1], jitter, 0, during);
    }

    internal static string RunPolicySmoke()
    {
        var idle = new Phase("Idle", 12, 12, 10, 15, 1, 0, 0);
        var down = new Phase("Download", 12, 12, 45, 62, 4, 16 * 1024 * 1024, 10);
        var up = new Phase("Upload", 12, 11, 86, 102, 7, 2 * 1024 * 1024, 9);
        var good = new Report(idle, down, up,
            HasEvidence(idle, down, 8L * 1024 * 1024),
            HasEvidence(idle, up, 1L * 1024 * 1024));
        if (!good.HasValidComparison || good.DownloadDeltaMs != 35 ||
            good.UploadDeltaMs != 76 || !Interpretation(good).Contains("SQM"))
            throw new InvalidOperationException("Loaded-latency comparison failed.");
        var weak = good with { Download = down with { ProbesDuringTraffic = 0 }, DownEvidence = false };
        if (weak.HasValidComparison || !Interpretation(weak).Contains("Nedostatečná"))
            throw new InvalidOperationException("Incomplete load was incorrectly called bufferbloat.");
        if (MaxDownloadBytes > 192L * 1024 * 1024 || MaxUploadBytes > 32L * 1024 * 1024)
            throw new InvalidOperationException("Traffic guard limits regressed.");
        return "BUFFERBLOAT_POLICY_SMOKE_OK phases=3 noTrafficDuringSmoke=true loadedEvidenceRequired=true boundedTraffic=true routerWrites=0";
    }
}
