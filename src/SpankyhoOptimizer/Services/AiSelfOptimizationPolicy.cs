using System;

namespace SpankyhoOptimizer.Services;

public static class AiSelfOptimizationPolicy
{
    public static double ComputePressureScore(
        double cpuEwmaPercent,
        double workingSetEwmaMb,
        int threads,
        int handles,
        double ramGrowthMbPerMinute,
        double handleGrowthPerMinute)
    {
        // A malformed native/EMA reading must never produce NaN pressure.
        // Treat it as maximum overhead so autonomous maintenance backs off.
        if (!double.IsFinite(cpuEwmaPercent) || cpuEwmaPercent < 0 ||
            !double.IsFinite(workingSetEwmaMb) || workingSetEwmaMb < 0 ||
            !double.IsFinite(ramGrowthMbPerMinute) || ramGrowthMbPerMinute < 0 ||
            !double.IsFinite(handleGrowthPerMinute) || handleGrowthPerMinute < 0 ||
            threads < 0 || handles < 0)
            return 10d;
        double basePressure =
            Math.Max(cpuEwmaPercent / 1.6, workingSetEwmaMb / 115d) +
            Math.Max(0, threads - 32) / 32d +
            Math.Max(0, handles - 700) / 1400d;

        double growthPressure =
            Math.Max(0, ramGrowthMbPerMinute - 3) / 18d +
            Math.Max(0, handleGrowthPerMinute - 15) / 250d;

        return Math.Clamp(basePressure + Math.Min(0.75, growthPressure), 0, 10);
    }

    // Reset the sampling epoch if any EWMA lane is non-finite.
    // A bad sensor reading must not become a persistent history value.
    internal static bool RequiresEwmaReset(double cpu, double workingSet,
        double ramGrowth, double handleGrowth) =>
        !double.IsFinite(cpu) || !double.IsFinite(workingSet) ||
        !double.IsFinite(ramGrowth) || !double.IsFinite(handleGrowth);

    public static bool IsPressured(
        double pressureScore,
        double managedMemoryMb,
        double ramGrowthMbPerMinute,
        double handleGrowthPerMinute) =>
        !double.IsFinite(pressureScore) || pressureScore < 0 || pressureScore > 10 ||
        !double.IsFinite(managedMemoryMb) || managedMemoryMb < 0 ||
        !double.IsFinite(ramGrowthMbPerMinute) || ramGrowthMbPerMinute < 0 ||
        !double.IsFinite(handleGrowthPerMinute) || handleGrowthPerMinute < 0 ||
        pressureScore >= 1.15 ||
        managedMemoryMb >= 80 ||
        ramGrowthMbPerMinute >= 12 ||
        handleGrowthPerMinute >= 120;

    public static bool IsCalm(
        double pressureScore,
        double managedMemoryMb,
        double ramGrowthMbPerMinute,
        double handleGrowthPerMinute) =>
        double.IsFinite(pressureScore) && pressureScore >= 0 && pressureScore <= 10 &&
        double.IsFinite(managedMemoryMb) && managedMemoryMb >= 0 &&
        double.IsFinite(ramGrowthMbPerMinute) && ramGrowthMbPerMinute >= 0 &&
        double.IsFinite(handleGrowthPerMinute) && handleGrowthPerMinute >= 0 &&
        pressureScore <= 0.70 &&
        managedMemoryMb <= 55 &&
        ramGrowthMbPerMinute <= 2 &&
        handleGrowthPerMinute <= 15;

    internal static int SamplingIntervalMilliseconds(AiWorkloadClass workload) =>
        workload switch
        {
            AiWorkloadClass.Interactive => 45_000,
            AiWorkloadClass.Background or AiWorkloadClass.Idle => 30_000,
            _ => 60_000
        };

    public static TimeSpan SamplingInterval(AiWorkloadClass workload) =>
        TimeSpan.FromMilliseconds(SamplingIntervalMilliseconds(workload));

    // A suspended process, a clock correction, or a disabled monitoring period
    // must never be blended into the next CPU/RAM growth estimate.
    public static bool RequiresFreshBaseline(
        DateTime lastSampleUtc, DateTime nowUtc, bool? previouslyEnabled) =>
        previouslyEnabled != true ||
        lastSampleUtc == DateTime.MinValue ||
        nowUtc < lastSampleUtc ||
        nowUtc - lastSampleUtc > TimeSpan.FromMinutes(10);

    // Process.TotalProcessorTime is monotonic. NTP/manual clock changes must
    // not inflate or deflate a CPU percentage computed from that counter.
    // Reject a resumed/invalid epoch instead of learning false low pressure.
    internal static bool TryMonotonicElapsedMilliseconds(
        long previousTicks, long currentTicks, long frequency, out double elapsedMs)
    {
        elapsedMs = 0;
        if (previousTicks <= 0 || currentTicks <= previousTicks || frequency <= 0)
            return false;
        double result = (currentTicks - (double)previousTicks) * 1000d / frequency;
        if (!double.IsFinite(result) || result <= 0 || result > 600_000d)
            return false;
        elapsedMs = Math.Max(1d, result);
        return true;
    }

    // The live process monitor must NOT use wall clock to decide if a new
    // native CPU baseline is needed. A manual/NTP adjustment only affects
    // timestamps written for people, not the Stopwatch sampling epoch.
    internal static bool RequiresMonotonicBaseline(
        long previousTicks, long currentTicks, long frequency, bool? previouslyEnabled) =>
        previouslyEnabled != true ||
        !TryMonotonicElapsedMilliseconds(previousTicks, currentTicks, frequency, out _);

    // A reset/partial native CPU counter is unknown evidence, not real 0%.
    // An impossible positive jump must not produce an artificial 100% CPU
    // pressure reading. This check uses data already collected once/30-60s.
    internal static bool TryProcessCpuPercent(
        TimeSpan previousCpu, TimeSpan currentCpu,
        double elapsedMs, int logicalProcessors, out double percent)
    {
        percent = 0;
        if (logicalProcessors < 1 || !double.IsFinite(elapsedMs) ||
            elapsedMs <= 0 || elapsedMs > 600_000 ||
            previousCpu < TimeSpan.Zero || currentCpu < TimeSpan.Zero ||
            currentCpu < previousCpu)
            return false;
        double deltaMs = (currentCpu - previousCpu).TotalMilliseconds;
        if (!double.IsFinite(deltaMs) || deltaMs < 0 ||
            deltaMs > elapsedMs * logicalProcessors * 1.05d + 250d)
            return false;
        percent = Math.Clamp(deltaMs / elapsedMs / logicalProcessors * 100d, 0, 100);
        return true;
    }

    // A legitimate 0% CPU / zero RAM growth is a sampled VALUE, not an
    // uninitialized EMA. Keep a separate initialization bit to avoid sudden
    // one-sample spikes after an idle period.
    internal static double BlendEwma(
        double previous, double observed, double previousWeight, bool initialized)
    {
        // Invalid telemetry must propagate as UNKNOWN, not as a convincing
        // low-pressure sample. Downstream pressure policies fail closed on NaN.
        if (!double.IsFinite(observed) || observed < 0 ||
            !double.IsFinite(previousWeight) || previousWeight < 0 || previousWeight > 1 ||
            (initialized && (!double.IsFinite(previous) || previous < 0)))
            return double.NaN;
        double blended = initialized ? previous * previousWeight + observed * (1d - previousWeight)
                                     : observed;
        // Even finite operands can overflow when summed; never report a valid EMA then.
        return double.IsFinite(blended) && blended >= 0 ? blended : double.NaN;
    }

    public static int ApplyWorkloadFloor(int intervalSeconds, AiWorkloadClass workload) =>
        workload switch
        {
            AiWorkloadClass.Game => Math.Max(intervalSeconds, 180),
            AiWorkloadClass.Interactive => Math.Max(intervalSeconds, 120),
            AiWorkloadClass.Background => Math.Max(intervalSeconds, 60),
            AiWorkloadClass.Unknown => Math.Max(intervalSeconds, 120),
            _ => intervalSeconds
        };

    public static string State(
        double pressureScore,
        int maintenanceIntervalSeconds,
        double ramGrowthMbPerMinute,
        double handleGrowthPerMinute)
    {
        // A bad native sensor/EMA sample is unknown evidence, not normal AI
        // optimization. Do not display a healthy status while fresh safe
        // telemetry is unavailable.
        if (!double.IsFinite(pressureScore) || pressureScore < 0 || pressureScore > 10 ||
            !double.IsFinite(ramGrowthMbPerMinute) || ramGrowthMbPerMinute < 0 ||
            !double.IsFinite(handleGrowthPerMinute) || handleGrowthPerMinute < 0 ||
            maintenanceIntervalSeconds <= 0)
            return "Čekám na platnou telemetrii";
        if (ramGrowthMbPerMinute >= 12 || handleGrowthPerMinute >= 120)
            return "Rostoucí režie";
        if (pressureScore >= 1.15)
            return "Omezuji vlastní údržbu";
        if (maintenanceIntervalSeconds > 60)
            return "Méně časté kontroly";
        return "Průběžná automatická optimalizace";
    }
}
