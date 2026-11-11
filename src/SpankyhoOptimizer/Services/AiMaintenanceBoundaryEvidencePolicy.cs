using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Final read-only gate before an AI-owned Windows service/task write.
/// Live candidate revalidation runs separately. Do not allow a formerly
/// valid workload sample to remain an authorization indefinitely.
/// </summary>
internal static class AiMaintenanceBoundaryEvidencePolicy
{
    internal static bool CanStartNewTrial(
        AdaptivePowerRuntimeStatus? workloadSample,
        DateTime nowUtc,
        bool smoothnessStillSafe,
        bool interruptedRecoveryPending) =>
        !interruptedRecoveryPending &&
        smoothnessStillSafe &&
        AdaptivePowerRuntimeService.IsStatusFresh(workloadSample, nowUtc) &&
        nowUtc.Kind == DateTimeKind.Utc &&
        workloadSample is { GameActive: false, RealIdle: true } &&
        workloadSample.UpdatedUtc.Kind == DateTimeKind.Utc &&
        double.IsFinite(workloadSample.LoadPercent) &&
        workloadSample.LoadPercent >= 0 &&
        workloadSample.LoadPercent <= AiBackgroundPerformancePolicy.MaximumQuietLoadPercent &&
        // Final write admission is stricter than monitoring: a Background
        // classification with active keyboard/mouse input is NOT unattended
        // permission. This applies to first B, verified B reapply and drivers.
        AiSharedWorkloadPolicy.AllowsBackgroundAutomation(
            AiSharedWorkloadPolicy.Resolve(workloadSample,
                workloadSample.RealIdle, workloadSample.LoadPercent, nowUtc));
}
