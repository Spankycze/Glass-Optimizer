using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // Independent targets/pacing may overlap, but at most two real ping
    // sessions run together. Same-key callers serialize and reuse the result.
    private static readonly SemaphoreSlim MeasureGate = new(2, 2);
    // Longer 10+ sample latency/jitter trials must not share the wire with a
    // dashboard health probe: reserve both slots for the benchmark. Serialize
    // benchmark reservations to prevent two jobs each holding one slot.
    private static readonly SemaphoreSlim BenchmarkGate = new(1, 1);
    private sealed class KeyGate
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int Users;
    }
    private static readonly Dictionary<(string Target, int Samples, int IntervalMs), KeyGate>
        KeyGates = new();

    private static KeyGate RentKeyGate((string Target, int Samples, int IntervalMs) key)
    {
        lock (CacheLock)
        {
            if (!KeyGates.TryGetValue(key, out var gate))
            {
                gate = new KeyGate();
                KeyGates.Add(key, gate);
            }
            gate.Users++;
            return gate;
        }
    }

    private static void ReturnKeyGate((string Target, int Samples, int IntervalMs) key, KeyGate gate)
    {
        lock (CacheLock)
        {
            if (--gate.Users == 0)
            {
                KeyGates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }
    // UI cards, network diagnosis and tuning benchmarks request different
    // sample counts/pacing. A single-slot cache made them evict each other.
    // Bound entries to avoid a growing cache for arbitrary user targets.
    private static readonly Dictionary<(string Target, int Samples, int IntervalMs),
        (NetworkLatencyReport Report, DateTime At)> Cache = new();
    private const int CacheLimit = 8;
    private static int _generation;

    public static async Task<NetworkLatencyReport> MeasureAsync(
        string target = "1.1.1.1", int samples = 8,
        CancellationToken token = default, int intervalMs = 80)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(samples, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalMs, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(intervalMs, 2000);
        // Pacing is part of the result: never reuse a short health probe as
        // a longer latency/jitter benchmark (or vice versa).
        token.ThrowIfCancellationRequested();
        var key = (target, samples, intervalMs);
        lock (CacheLock)
            if (Cache.TryGetValue(key, out var entry) &&
                DateTime.UtcNow - entry.At < TimeSpan.FromSeconds(20))
                return entry.Report;

        // A fresh measurement rents a transient per-key gate. Unlike a
        // dictionary of permanent semaphores, idle keys consume no handles.
        // Shared cache hits avoid allocating a gate entirely.
        var keyGate = RentKeyGate(key);
        bool keyedAcquired = false;
        try
        {
            await keyGate.Semaphore.WaitAsync(token).ConfigureAwait(false);
            keyedAcquired = true;
            lock (CacheLock)
                if (Cache.TryGetValue(key, out var existing) &&
                    DateTime.UtcNow - existing.At < TimeSpan.FromSeconds(20))
                    return existing.Report;

            bool exclusive = samples >= 10;
            bool benchmarkAcquired = false;
            int permitsHeld = 0;
            try
            {
                if (exclusive)
                {
                    await BenchmarkGate.WaitAsync(token).ConfigureAwait(false);
                    benchmarkAcquired = true;
                }
                await MeasureGate.WaitAsync(token).ConfigureAwait(false);
                permitsHeld++;
                if (exclusive)
                {
                    await MeasureGate.WaitAsync(token).ConfigureAwait(false);
                    permitsHeld++;
                }
                int generation;
                lock (CacheLock) generation = _generation;
                var values = new List<double>(samples);
                int sent = 0;
                using var ping = new Ping();
                for (int i = 0; i < samples; i++)
                {
                    token.ThrowIfCancellationRequested();
                    sent++;
                    try
                    {
                        var reply = await ping.SendPingAsync(target, 500).WaitAsync(token).ConfigureAwait(false);
                        if (reply.Status == IPStatus.Success) values.Add(reply.RoundtripTime);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (PingException ex) { LogService.Info("network", $"Ping sample failed target={target}: {ex.Message}"); }
                    catch (InvalidOperationException ex) { LogService.Info("network", $"Ping sample transient-state failure target={target}: {ex.Message}"); }
                    if (i + 1 < samples && intervalMs > 0)
                        await Task.Delay(intervalMs, token).ConfigureAwait(false);
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
                lock (CacheLock)
                {
                    if (generation == _generation)
                    {
                        if (!Cache.ContainsKey(key) && Cache.Count >= CacheLimit)
                        {
                            // Eight entries only: trivial eviction, no permanent
                            // stale network reports retained after cache turnover.
                            var oldest = Cache.OrderBy(x => x.Value.At).First().Key;
                            Cache.Remove(oldest);
                        }
                        Cache[key] = (result, DateTime.UtcNow);
                    }
                }
                return result;
            }
            finally
            {
                if (permitsHeld > 0) MeasureGate.Release(permitsHeld);
                if (benchmarkAcquired) BenchmarkGate.Release();
            }
        }
        finally
        {
            if (keyedAcquired) keyGate.Semaphore.Release();
            ReturnKeyGate(key, keyGate);
        }
    }

    // Deterministic contract test over loopback only: no external packets,
    // configuration changes, privilege escalation or persistent state changes.
    internal static async Task VerifyCacheReadOnlyAsync(CancellationToken token)
    {
        const string loopback = "127.0.0.1";
        Invalidate();
        try
        {
            var normal = await MeasureAsync(loopback, samples: 2, token: token, intervalMs: 0);
            var repeated = await MeasureAsync(loopback, samples: 2, token: token, intervalMs: 0);
            if (!ReferenceEquals(normal, repeated))
                throw new InvalidOperationException("Network same-key cache missed.");

            var paced = await MeasureAsync(loopback, samples: 2, token: token, intervalMs: 5);
            var larger = await MeasureAsync(loopback, samples: 3, token: token, intervalMs: 0);
            if (ReferenceEquals(normal, paced) || ReferenceEquals(normal, larger))
                throw new InvalidOperationException("Network cache conflated distinct probe configurations.");

            var normalAgain = await MeasureAsync(loopback, samples: 2, token: token, intervalMs: 0);
            if (!ReferenceEquals(normal, normalAgain))
                throw new InvalidOperationException("Network cache evicted an unrelated fresh key.");

            // Concurrent equal-key callers must share the serialized result.
            var concurrent = await Task.WhenAll(
                MeasureAsync(loopback, samples: 4, token: token, intervalMs: 3),
                MeasureAsync(loopback, samples: 4, token: token, intervalMs: 3));
            if (!ReferenceEquals(concurrent[0], concurrent[1]))
                throw new InvalidOperationException("Network concurrent callers repeated a probe.");

            Invalidate();
            var fresh = await MeasureAsync(loopback, samples: 2, token: token, intervalMs: 0);
            if (ReferenceEquals(normal, fresh))
                throw new InvalidOperationException("Network invalidation kept stale data.");

            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancelled.Cancel();
            bool observedCancellation = false;
            try
            {
                _ = await MeasureAsync(loopback, samples: 2, token: cancelled.Token, intervalMs: 0);
            }
            catch (OperationCanceledException) { observedCancellation = true; }
            if (!observedCancellation)
                throw new InvalidOperationException("Network cache returned a result after cancellation.");

            // Cancelling one same-key waiter must not abort the owner or
            // prevent the other caller from caching a successful result.
            Invalidate();
            var owner = MeasureAsync(loopback, samples: 7, token: token, intervalMs: 15);
            using var waiterCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var waiter = MeasureAsync(loopback, samples: 7,
                token: waiterCancellation.Token, intervalMs: 15);
            waiterCancellation.Cancel();
            observedCancellation = false;
            try { _ = await waiter.ConfigureAwait(false); }
            catch (OperationCanceledException) { observedCancellation = true; }
            if (!observedCancellation)
                throw new InvalidOperationException("Network queued same-key waiter ignored cancellation.");
            var owned = await owner.ConfigureAwait(false);
            var cachedOwned = await MeasureAsync(loopback, samples: 7,
                token: token, intervalMs: 15).ConfigureAwait(false);
            if (!ReferenceEquals(owned, cachedOwned))
                throw new InvalidOperationException("Cancelled network waiter poisoned owner cache.");

            // Bound both successful-result cache and transient gate lifetime.
            Invalidate();
            var burst = new Task<NetworkLatencyReport>[12];
            for (int i = 0; i < burst.Length; i++)
                burst[i] = MeasureAsync(loopback, samples: i + 1,
                    token: token, intervalMs: 0);
            _ = await Task.WhenAll(burst).ConfigureAwait(false);
            lock (CacheLock)
            {
                if (KeyGates.Count != 0 || Cache.Count > CacheLimit)
                    throw new InvalidOperationException(
                        $"Network transient gate/cache leaked: gates={KeyGates.Count} cache={Cache.Count}");
            }

            LogService.Info("network",
                "NETWORK_CACHE_READONLY_SMOKE_OK multiKey=true concurrentSingleFlight=true invalidate=true cancel=true loopback=true");
        }
        finally { Invalidate(); }
    }

    // Measures two independent read-only loopback probes. The former global
    // gate serialized these distinct keys. The test records wall time without
    // a brittle performance threshold on a potentially busy Windows host.
    internal static async Task MeasureIndependentConcurrencySmokeAsync(CancellationToken token)
    {
        Invalidate();
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var sw = Stopwatch.StartNew();
            var left = MeasureAsync("127.0.0.1", samples: 5, token: token, intervalMs: 90);
            var right = MeasureAsync("127.0.0.1", samples: 6, token: token, intervalMs: 90);
            var result = await Task.WhenAll(left, right).ConfigureAwait(false);
            sw.Stop();
            process.Refresh();
            if (result[0].Sent != 5 || result[1].Sent != 6 ||
                ReferenceEquals(result[0], result[1]))
                throw new InvalidOperationException("Independent network measurements were mixed.");
            LogService.Info("performance",
                $"NETWORK_PARALLEL_SMOKE_OK wallMs={sw.Elapsed.TotalMilliseconds:F2}" +
                $" cpuMs={(process.TotalProcessorTime - cpuBefore).TotalMilliseconds:F2}" +
                $" allocatedKB={(GC.GetTotalAllocatedBytes(false) - allocatedBefore) / 1024}" +
                $" wsMB={process.WorkingSet64 / 1048576d:F2}" +
                $" samples=5+6 pacingMs=90 loopback=true writes=false");

            // A longer tuning-style benchmark must own both permits, so
            // a simultaneous short dashboard probe cannot skew its samples.
            Invalidate();
            var tuning = MeasureAsync("127.0.0.1", samples: 10, token: token, intervalMs: 25);
            var dashboard = MeasureAsync("127.0.0.1", samples: 3, token: token, intervalMs: 0);
            if (!ReferenceEquals(await Task.WhenAny(tuning, dashboard).ConfigureAwait(false), tuning))
                throw new InvalidOperationException("Short network probe overlapped exclusive benchmark.");
            await Task.WhenAll(tuning, dashboard).ConfigureAwait(false);

            // Two exclusive benchmark contenders must never each hold one
            // permit indefinitely, and cancellation cannot leak permits.
            Invalidate();
            var owner = MeasureAsync("127.0.0.1", samples: 10, token: token, intervalMs: 15);
            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var queued = MeasureAsync("127.0.0.1", samples: 11,
                    token: cancelled.Token, intervalMs: 15);
                cancelled.Cancel();
                bool cancelledAsExpected = false;
                try { _ = await queued.ConfigureAwait(false); }
                catch (OperationCanceledException) { cancelledAsExpected = true; }
                if (!cancelledAsExpected)
                    throw new InvalidOperationException("Queued benchmark ignored cancellation.");
            }
            _ = await owner.ConfigureAwait(false);
            _ = await MeasureAsync("127.0.0.1", samples: 11,
                token: token, intervalMs: 0).ConfigureAwait(false);
            lock (CacheLock)
            {
                if (KeyGates.Count != 0)
                    throw new InvalidOperationException("Exclusive network benchmark leaked key gates.");
            }
            LogService.Info("network", "NETWORK_EXCLUSIVE_SMOKE_OK isolated=true cancellation=true permitsReleased=true");
        }
        finally { Invalidate(); }
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
        lock (CacheLock) { _generation++; Cache.Clear(); }
    }
}
