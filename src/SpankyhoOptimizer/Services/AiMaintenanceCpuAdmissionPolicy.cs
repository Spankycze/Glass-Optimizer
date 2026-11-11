using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Distinguishes a previous finite CPU display value from fresh evidence
/// authorizing service/task trials and autonomous scheduling changes.
/// These checks never sample native process counters or allocate.
/// </summary>
internal static class AiMaintenanceCpuAdmissionPolicy
{
    internal static AiWorkloadClass Workload(
        bool cpuEvidenceAvailable, AdaptivePowerRuntimeStatus? power,
        bool realIdle, double loadPercent, DateTime nowUtc) =>
        cpuEvidenceAvailable && AiCpuLoadSelectionPolicy.IsValid(loadPercent)
            ? AiSharedWorkloadPolicy.Resolve(power, realIdle, loadPercent, nowUtc)
            : AiWorkloadClass.Unknown;

    // A read-only background sample may be collected while the user is active,
    // but that activity MUST interrupt service/task A/B/A quiet-epoch credit.
    internal static bool MayAccrueQuietEvidence(
        bool comparable, bool realIdle, bool recoveryPending) =>
        comparable && realIdle && !recoveryPending;

    // Disabling AI must still reach the previously owned tweak rollback path.
    internal static bool MayQueueTweaks(bool cpuEvidenceAvailable, bool aiEnabled) =>
        cpuEvidenceAvailable || !aiEnabled;

    internal static bool MustRestoreTrial(
        bool cpuEvidenceAvailable, AiWorkloadClass workload, bool gameActive) =>
        !cpuEvidenceAvailable ||
        AiMaintenanceTrialConsistencyPolicy.MustRestoreForWorkload(workload, gameActive);

    // A still-running A/B/A service/task experiment cannot remain active
    // just because the overall CPU counter is readable. Missing/expired
    // smoothness or a non-Normal state means we cannot verify its safety.
    // Reuse the same strict pressure floor as owned process-priority rollback.
    internal static bool HasTrustedTrialSafetyEvidence(
        bool cpuEvidenceAvailable, AiSystemSmoothnessHealth? smooth, DateTime nowUtc) =>
        cpuEvidenceAvailable && smooth is { State: "Normal", GameActive: false } &&
        !AiAutomaticPriorityTrialPolicy.DangerousAfterApplying(smooth, nowUtc);

    internal static bool MustRestoreTrial(
        bool cpuEvidenceAvailable, AiWorkloadClass workload, bool gameActive,
        AiSystemSmoothnessHealth? smooth, DateTime nowUtc) =>
        MustRestoreTrial(cpuEvidenceAvailable, workload, gameActive) ||
        !HasTrustedTrialSafetyEvidence(cpuEvidenceAvailable, smooth, nowUtc);

    // A background-classified session can still have active user input.
    // Do not leave an owned service/task experiment running in that case.
    internal static bool MustRestoreTrial(
        bool cpuEvidenceAvailable, AiWorkloadClass workload, bool gameActive,
        AiSystemSmoothnessHealth? smooth, DateTime nowUtc, bool realIdle) =>
        !realIdle || MustRestoreTrial(cpuEvidenceAvailable, workload, gameActive, smooth, nowUtc);

    // A recovery-only tick should restore owned service/task trials without
    // re-enumerating every unrelated service, task and optional driver.
    // Respect disabled category settings: those require explicit restoration.
    internal static bool RecoveryOnlyWithoutDiscovery(
        bool cpuEvidenceAvailable, bool hadPendingTrial,
        bool servicesEnabled, bool tasksEnabled, bool devicesEnabled) =>
        !cpuEvidenceAvailable && hadPendingTrial &&
        servicesEnabled && tasksEnabled && devicesEnabled;

    // A crash-recovery journal is more important than NEW autonomous work.
    // Optional baseline/probe/tweak scheduling waits for durable restoration;
    // turning AI OFF must still be allowed to queue owned tweak rollback.
    internal static bool MayStartOptionalWork(bool aiEnabled, bool recoveryPending) =>
        aiEnabled && !recoveryPending;

    internal static bool MayQueueTweaksDuringRecovery(
        bool cpuEvidenceAvailable, bool aiEnabled, bool recoveryPending) =>
        (!recoveryPending || !aiEnabled) &&
        MayQueueTweaks(cpuEvidenceAvailable, aiEnabled);

    // Safety recovery outranks adaptive maintenance throttling. If a
    // write-ahead checkpoint/readback failed after a restart, retry via the
    // EXISTING FanAgent callback within 5s, without a new task or timer.
    // This does not permit new Windows changes while recovery remains armed.
    internal static TimeSpan TickInterval(
        int ordinarySeconds, bool cpuEvidenceAvailable, bool pendingTrial,
        bool interruptedRecoveryPending = false)
    {
        int seconds = Math.Clamp(ordinarySeconds, 5, 240);
        return TimeSpan.FromSeconds(interruptedRecoveryPending ||
            (!cpuEvidenceAvailable && pendingTrial) ? 5 : seconds);
    }
}
