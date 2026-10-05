using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record NetworkLatencyReport(
    string Target, int Sent, int Received,
    double MinMs, double AvgMs, double MaxMs,
    double JitterMs, double LossPercent);

public static class NetworkDiagnosticsService
{
    private static readonly object CacheLock = new();
    private static readonly SemaphoreSlim MeasureGate = new(1, 1);
    private static NetworkLatencyReport? _cache;
    private static string _cacheKey = string.Empty;
    private static DateTime _cacheAt;

    public static async Task<NetworkLatencyReport> MeasureAsync(
        string target = "1.1.1.1", int samples = 8,
        CancellationToken token = default)
    {
        string key = target + "|" + samples;
        lock (CacheLock)
            if (_cache != null && _cacheKey == key && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(20)) return _cache;

        await MeasureGate.WaitAsync(token);
        try
        {
            lock (CacheLock)
                if (_cache != null && _cacheKey == key && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(20)) return _cache;

            var values = new List<double>();
            int sent = 0;
            for (int i = 0; i < samples; i++)
            {
                token.ThrowIfCancellationRequested();
                sent++;
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(target, 500).WaitAsync(token).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success) values.Add(reply.RoundtripTime);
                }
                catch (OperationCanceledException) { throw; }
                catch (PingException ex) { LogService.Info("network", $"Ping sample failed target={target}: {ex.Message}"); }
                catch (InvalidOperationException ex) { LogService.Info("network", $"Ping sample transient-state failure target={target}: {ex.Message}"); }
                if (i + 1 < samples) await Task.Delay(80, token).ConfigureAwait(false);
            }

            NetworkLatencyReport result;
            if (values.Count == 0)
                result = new NetworkLatencyReport(target, sent, 0, 0, 0, 0, 0, 100);
            else
            {
                double jitter = 0;
                if (values.Count > 1)
                {
                    double sum = 0;
                    for (int i = 1; i < values.Count; i++) sum += Math.Abs(values[i] - values[i - 1]);
                    jitter = sum / (values.Count - 1);
                }
                result = new NetworkLatencyReport(target, sent, values.Count,
                    values.Min(), values.Average(), values.Max(), jitter,
                    100.0 * (sent - values.Count) / sent);
            }
            lock (CacheLock) { _cache = result; _cacheKey = key; _cacheAt = DateTime.UtcNow; }
            return result;
        }
        finally { MeasureGate.Release(); }
    }

    public static Task<string> GetAdvancedAsync(CancellationToken token = default)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var lines = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n => $"{n.Name} • {n.NetworkInterfaceType} • {n.Speed / 1_000_000} Mb/s • {n.OperationalStatus}");
            string text = string.Join(Environment.NewLine, lines);
            return string.IsNullOrWhiteSpace(text)
                ? "Žádné aktivní síťové rozhraní."
                : text;
        }, token);
    }

    public static void Invalidate()
    {
        lock (CacheLock) { _cache = null; _cacheKey = string.Empty; _cacheAt = default; }
    }
}
