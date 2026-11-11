using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// The adaptive-power record rounds the FanAgent's CPU estimate to 0.1pp.
/// A stale or unrelated persisted workload record must never authorize
/// an A/B/A sample or keep an AI-owned Windows service/task trial active.
/// This only compares already captured immutable telemetry; no OS reads.
/// </summary>
internal static class AiMaintenanceTelemetryAgreementPolicy
{
    internal const double MaximumCpuRoundingDifferencePp = 0.11;

    internal static bool Matches(
        AdaptivePowerRuntimeStatus? power, double cpuPercent,
        bool realIdle, bool gameActive, DateTime nowUtc) =>
        AiCpuLoadSelectionPolicy.IsValid(cpuPercent) &&
        power is not null &&
        AdaptivePowerRuntimeService.IsStatusFresh(power, nowUtc) &&
        power.UpdatedUtc.Kind == DateTimeKind.Utc &&
        nowUtc.Kind == DateTimeKind.Utc &&
        double.IsFinite(power.LoadPercent) &&
        Math.Abs(power.LoadPercent - cpuPercent) <= MaximumCpuRoundingDifferencePp &&
        power.RealIdle == realIdle &&
        power.GameActive == gameActive;
}
