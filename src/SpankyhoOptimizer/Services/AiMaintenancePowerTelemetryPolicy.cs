namespace SpankyhoOptimizer.Services;

/// <summary>
/// An idle fallback from CPU alone is sufficient to DISPLAY an estimated
/// workload, but not to authorize autonomous OS mutations while current
/// workload/game power telemetry is unavailable.
/// </summary>
internal static class AiMaintenancePowerTelemetryPolicy
{
    internal static bool MayCollectQuietEvidence(
        bool powerTelemetryFresh, bool otherEvidenceComparable) =>
        powerTelemetryFresh && otherEvidenceComparable;

    internal static bool MayScheduleProcessTrial(
        bool powerTelemetryFresh, bool otherwiseAllowed) =>
        powerTelemetryFresh && otherwiseAllowed;

    internal static bool StopOwnedTrial(
        bool powerTelemetryFresh, bool alreadyUnsafe) =>
        !powerTelemetryFresh || alreadyUnsafe;

    internal static bool MayQueueTweaks(
        bool powerTelemetryFresh, bool cpuTelemetryFresh,
        bool aiEnabled, bool interruptedRecoveryPending) =>
        AiMaintenanceCpuAdmissionPolicy.MayQueueTweaksDuringRecovery(
            powerTelemetryFresh && cpuTelemetryFresh,
            aiEnabled, interruptedRecoveryPending);
}
