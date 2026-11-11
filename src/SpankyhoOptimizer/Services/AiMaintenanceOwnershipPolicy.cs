using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Decides if a persisted AI intervention still owns the Windows setting.
/// Manual/OS changes must never be overwritten by RestoreAll or stale
/// recovery after a runtime-host restart.
/// </summary>
internal static class AiMaintenanceOwnershipPolicy
{
    internal static bool ServiceStartStillOwned(
        int? originalStart, int actualStart, string? phase)
    {
        // AI never legitimately owns a service originally Disabled/Boot/System.
        // Reject old or corrupt journals instead of restoring that Start mode.
        if (originalStart is not (2 or 3))
            return false;
        int original = originalStart.Value;
        bool reference = string.Equals(phase, "ReferenceA",
            StringComparison.OrdinalIgnoreCase);
        int aiApplied = !reference && original == 2 ? 3 : original;
        return actualStart == aiApplied;
    }

    // A task rollback can only reverse our own Candidate-B disable.
    // During reference A, a user/Windows disable is an external choice.
    internal static bool TaskEnableStillOwned(
        bool? originalEnabled, bool actualEnabled, string? phase) =>
        originalEnabled == true &&
        AiMaintenanceRevalidationPolicy.MatchesTrialPhase(actualEnabled, phase);

    internal static bool DriverStartStillOwned(
        int? originalStart, int actualStart) =>
        // Only Demand Start (3) is ever eligible for AI driver changes.
        // Legacy Boot/System/Auto/Disabled journal entries are not owners.
        originalStart == 3 && actualStart == 4;

    internal static bool DriverAlreadyRestored(
        int? originalStart, int actualStart) =>
        originalStart == 3 && actualStart == 3;

    internal static bool HasInterruptedTrial(AiSystemRuntimeItemState state) =>
        state.Managed &&
        state.VerificationStartedUtc != null &&
        (state.Kind.Equals("service", StringComparison.OrdinalIgnoreCase) ||
         state.Kind.Equals("task", StringComparison.OrdinalIgnoreCase));

    internal static AiSystemRuntimeItemState Relinquish(
        AiSystemRuntimeItemState state, DateTime nowUtc, string reason,
        TimeSpan cooldown) =>
        state with
        {
            Managed = false,
            Sleeping = false,
            // Relinquishment is terminal: old pending intent belongs to
            // an owner that no longer has permission to restore Windows.
            PendingMutation = string.Empty,
            OriginalTaskNextRunUtc = null,
            OriginalTaskEnabled = null,
            OriginalServiceStart = null,
            PerformanceVerified = false,
            ObservationSinceUtc = null,
            DriverObservationLastUtc = null,
            CooldownUntilUtc = nowUtc.Add(cooldown),
            BlockedReason = reason,
            LastAction = reason,
            LastChangedUtc = nowUtc,
            BaselineLoadPercent = null,
            VerificationStartedUtc = null,
            VerificationLastSampleUtc = null,
            VerificationSamples = 0,
            VerificationLoadTotal = 0,
            VerificationPhase = string.Empty,
            CandidateLoadPercent = null,
            VerificationLoadMin = null,
            VerificationLoadMax = null,
            CandidateLoadMin = null,
            CandidateLoadMax = null,
            // A released owner cannot reuse sample totals or an old agent
            // memory epoch in a future A/B/A trial or after a process restart.
            BaselineAvailableRamMiB = null,
            VerificationMemorySamples = 0,
            VerificationMemoryTotalMiB = 0,
            VerificationMemoryMinMiB = null,
            VerificationMemoryMaxMiB = null,
            VerificationLastMemorySnapshotUtc = null,
            CandidateMemorySamples = 0,
            CandidateAvailableRamMiB = null,
            CandidateMemoryMinMiB = null,
            CandidateMemoryMaxMiB = null
        };

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        AiSystemRuntimeItemState candidate = new(
            "service", "Fixture", true, true, "", now.AddMinutes(10),
            null, 2, null, true, 0, "candidate",
            VerificationStartedUtc: now.AddMinutes(-5),
            VerificationPhase: "CandidateB", PerformanceVerified: false);
        var released = Relinquish(candidate, now, "manual override", TimeSpan.FromHours(1));
        var reference = candidate with { VerificationPhase = "ReferenceA", Sleeping = false };
        if (!HasInterruptedTrial(candidate) || !HasInterruptedTrial(reference) ||
            HasInterruptedTrial(candidate with { Managed = false }) ||
            HasInterruptedTrial(candidate with { VerificationStartedUtc = null }) ||
            !ServiceStartStillOwned(2, 3, "CandidateB") ||
            !ServiceStartStillOwned(2, 2, "ReferenceA") ||
            !ServiceStartStillOwned(3, 3, "CandidateB") ||
            ServiceStartStillOwned(2, 4, "CandidateB") ||
            ServiceStartStillOwned(null, 3, "CandidateB") ||
            !DriverStartStillOwned(3, 4) ||
            DriverStartStillOwned(3, 3) ||
            DriverStartStillOwned(4, 4) ||
            !DriverAlreadyRestored(3, 3) ||
            DriverAlreadyRestored(3, 4) ||
            released.Managed || released.Sleeping ||
            released.VerificationStartedUtc != null ||
            released.VerificationPhase.Length != 0 ||
            released.PerformanceVerified ||
            released.OriginalServiceStart != null ||
            released.CooldownUntilUtc != now.AddHours(1) ||
            released.Rollbacks != candidate.Rollbacks ||
            released.FailedBenefitTrials != candidate.FailedBenefitTrials)
            throw new InvalidOperationException(
                "AI ownership/recovery would overwrite an external setting or mislearn rollback.");
        return "AI_OWNERSHIP_RECOVERY_SMOKE_OK servicePhase=true driverManual=true " +
               "interruptedAba=true cleanRelinquish=true learningPreserved=true writes=0";
    }
}