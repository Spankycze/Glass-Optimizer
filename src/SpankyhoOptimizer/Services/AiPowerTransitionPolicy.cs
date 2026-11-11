using System;

namespace SpankyhoOptimizer.Services;

public static class AiPowerTransitionPolicy
{
    // With AI Smart disabled, ordinary load-based Performance still responds
    // promptly to a clearly identified heavy foreground workload. Thermal
    // guards are evaluated before this policy in ChooseMode.
    public static int FallbackInteractiveVoteGain(
        AiWorkloadClass workload, double cpuLoadPercent) =>
        workload == AiWorkloadClass.Interactive &&
        double.IsFinite(cpuLoadPercent) &&
        cpuLoadPercent >= 60 && cpuLoadPercent <= 100 ? 2 : 1;

    public static int PerformanceVoteGain(
        bool performanceSignal,
        AiWorkloadClass workload,
        double confidence,
        double loadPercent,
        double? packagePowerW)
    {
        if (!performanceSignal)
            return 0;

        // Missing load telemetry must not delay an already signaled request
        // for performance; avoid treating NaN as a quiet CPU sample.
        if (!double.IsFinite(loadPercent) || loadPercent < 0 || loadPercent > 100)
            return 3;
        double load = loadPercent;
        double conf = double.IsFinite(confidence) ? Math.Clamp(confidence, 0, 100) : 0;

        if (workload == AiWorkloadClass.Game)
            return 3;

        bool urgentMeasuredDemand =
            load >= 75 ||
            (AiPowerTelemetryEvidencePolicy.HasUsablePower(packagePowerW) && packagePowerW!.Value >= 100 && load >= 35);

        if (urgentMeasuredDemand)
            return 3;

        // Confirmed interactive bursts should enter Performance on this
        // already-sampled FanAgent tick, even when AI confidence is still
        // accumulating. Do not accelerate unknown/background/idle classes.
        if (workload == AiWorkloadClass.Interactive && load >= 60)
            return 2;

        return workload switch
        {
            AiWorkloadClass.Interactive => conf >= 60 ? 2 : 1,
            AiWorkloadClass.Background => 1,
            AiWorkloadClass.Idle => 1,
            AiWorkloadClass.Unknown => 1,
            _ => 1
        };
    }
}
