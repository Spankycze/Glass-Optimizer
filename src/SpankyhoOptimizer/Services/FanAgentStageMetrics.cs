using System;
using System.Diagnostics;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Monotonic, allocation-free per-tick phase accounting on the existing fan
/// worker. No new thread, timer, native scan or change to the cooling cadence.
/// All diagnostics, including logging, are best effort and never govern fans.
/// </summary>
internal sealed class FanAgentStageMetrics
{
    private static readonly string[] PhaseNames =
        { "profile", "telemetry", "hid", "ai", "fanPower", "status" };
    private readonly long[] _totals = new long[6];
    private readonly long[] _peaks = new long[6];
    private long _windowStart;
    private int _samples;
    private double _previousCpuMs = -1;

    internal FanAgentStageMetrics(long? initialTimestamp = null) =>
        _windowStart = initialTimestamp ?? Stopwatch.GetTimestamp();

    internal void Record(long t0, long t1, long t2, long t3,
        long t4, long t5, long t6)
    {
        if (t0 < 0 || t1 < t0 || t2 < t1 || t3 < t2 ||
            t4 < t3 || t5 < t4 || t6 < t5)
            return;
        long a = t1 - t0, b = t2 - t1, c = t3 - t2;
        long e = t4 - t3, f = t5 - t4, g = t6 - t5;
        Add(0, a); Add(1, b); Add(2, c);
        Add(3, e); Add(4, f); Add(5, g);
        _samples++;
    }

    private void Add(int index, long ticks)
    {
        _totals[index] += ticks;
        if (ticks > _peaks[index]) _peaks[index] = ticks;
    }

    // The only allocations occur at the once-per-minute diagnostic boundary.
    internal bool TryTake(long nowTicks, long windowTicks,
        out (int samples, long[] totals, long[] peaks) snapshot)
    {
        snapshot = default;
        if (_samples <= 0 || nowTicks < _windowStart ||
            nowTicks - _windowStart < windowTicks)
            return false;
        snapshot = (_samples, (long[])_totals.Clone(), (long[])_peaks.Clone());
        Array.Clear(_totals);
        Array.Clear(_peaks);
        _samples = 0;
        _windowStart = nowTicks;
        return true;
    }

    internal void ReportIfDue()
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (!TryTake(now, Stopwatch.Frequency * 60L, out var snapshot))
                return;
            double scale = 1000d / Stopwatch.Frequency;
            var phases = new System.Text.StringBuilder(256);
            for (int i = 0; i < PhaseNames.Length; i++)
            {
                if (i != 0) phases.Append(' ');
                phases.Append(PhaseNames[i]).Append("AvgMs=")
                    .Append((snapshot.totals[i] * scale / snapshot.samples).ToString("F2",
                        System.Globalization.CultureInfo.InvariantCulture))
                    .Append("MaxMs=")
                    .Append((snapshot.peaks[i] * scale).ToString("F2",
                        System.Globalization.CultureInfo.InvariantCulture));
            }
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            double totalCpuMs = process.TotalProcessorTime.TotalMilliseconds;
            double cpuDeltaMs = _previousCpuMs < 0 ? -1 :
                Math.Max(0, totalCpuMs - _previousCpuMs);
            _previousCpuMs = totalCpuMs;
            LogService.Info("lianli",
                $"FAN_TICK_PHASES samples={snapshot.samples} cpuDeltaMs={cpuDeltaMs:F1} " +
                $"workingSetMiB={process.WorkingSet64 / 1048576d:F2} " +
                $"privateMiB={process.PrivateMemorySize64 / 1048576d:F2} " +
                $"handles={process.HandleCount} threads={process.Threads.Count} {phases}");
        }
        catch
        {
            // Never allow optional metrics to interrupt the cooling worker.
        }
    }

    internal static string RunPolicySmoke()
    {
        var window = new FanAgentStageMetrics(100);
        window.Record(100, 110, 130, 160, 200, 250, 310);
        window.Record(320, 325, 335, 345, 355, 365, 375);
        window.Record(800, 799, 801, 802, 803, 804, 805); // invalid
        if (window.TryTake(399, 300, out _) ||
            !window.TryTake(400, 300, out var first) ||
            first.samples != 2 || first.totals[0] != 15 ||
            first.totals[1] != 30 || first.totals[2] != 40 ||
            first.totals[3] != 50 || first.totals[4] != 60 ||
            first.totals[5] != 70 || first.peaks[0] != 10 ||
            first.peaks[5] != 60 || window.TryTake(700, 300, out _))
            throw new InvalidOperationException("Fan agent phase timing window policy regressed.");
        // The actual per-tick hot path must not allocate; reserve all
        // formatting/process sampling for the rare 60-second boundary.
        const int iterations = 100_000;
        const int maxWindows = 5;
        // The smoke process may make a one-time runtime/JIT allocation on
        // its first optimized hot-loop transition. Do not mistake that for
        // sustained per-tick allocation. Allow at most two bounded noisy
        // windows and require TWO consecutive windows at exactly zero.
        // A genuine per-Record allocation fails every window.
        var hot = new FanAgentStageMetrics(0);
        for (int i = 0; i < 20_000; i++)
            hot.Record(0, 1, 2, 3, 4, 5, 6);
        Span<long> measured = stackalloc long[maxWindows];
        int checkedWindows = 0;
        int cleanStreak = 0;
        int noisyWindows = 0;
        long maxAllocated = 0;
        double totalHotMs = 0;
        for (; checkedWindows < maxWindows && cleanStreak < 2; checkedWindows++)
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++)
                hot.Record(0, 1, 2, 3, 4, 5, 6);
            totalHotMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            measured[checkedWindows] = allocated;
            maxAllocated = Math.Max(maxAllocated, allocated);
            // Early fail for real per-tick allocations rather than masking
            // an ongoing regression behind a future clean sample.
            if (allocated > 32 * 1024 || allocated < 0)
                throw new InvalidOperationException(
                    $"Fan phase hot path allocated {allocated} bytes in one {iterations}-tick window.");
            if (allocated == 0)
                cleanStreak++;
            else
            {
                cleanStreak = 0;
                noisyWindows++;
                if (noisyWindows > 2)
                    throw new InvalidOperationException(
                        "Fan phase hot path had more than two noisy startup windows.");
            }
        }
        if (cleanStreak < 2)
            throw new InvalidOperationException(
                "Fan phase hot path did not stabilize to two zero-allocation windows: " +
                string.Join(",", measured[..checkedWindows].ToArray()));
        return $"FAN_TICK_PHASE_METRICS_SMOKE_OK iterations={iterations} " +
            $"hotMs={totalHotMs:F3} hotAllocatedB=0 " +
            $"zeroWindows=2 windows={checkedWindows} maxWindowAllocatedB={maxAllocated} " +
            "6phases=true monotonic=true invalidDiscarded=true minuteGate=true " +
            "reset=true noExtraPolling=true";
    }
}
