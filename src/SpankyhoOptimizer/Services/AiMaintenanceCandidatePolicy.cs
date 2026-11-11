using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Autonomous optimization candidate exploration and measured-outcome learning.
/// Only changes selection cadence, never relaxes a mutation's safety checks.
/// </summary>
internal static class AiMaintenanceCandidatePolicy
{
    // Cursor/offset may originate from persisted state. Normalize each
    // remainder before bounded addition, including Int32 edge cases.
    internal static int NextCursor(int cursor, int count)
    {
        if (count <= 0) return 0;
        int normalized = cursor % count;
        if (normalized < 0) normalized += count;
        return normalized == count - 1 ? 0 : normalized + 1;
    }

    internal static int IndexAt(int cursor, int offset, int count)
    {
        if (count <= 0) return 0;
        int start = cursor % count;
        if (start < 0) start += count;
        int delta = offset % count;
        if (delta < 0) delta += count;
        // Avoid a third modulo and avoid int overflow even when count is
        // int.MaxValue. Both normalized operands are strictly < count.
        int room = count - start;
        return delta < room ? start + delta : delta - room;
    }

    internal static bool IsMeasuredNoBenefit(AiMaintenanceFeedbackVerdict verdict) =>
        verdict.Ready && !verdict.Accepted &&
        (verdict.Reason.StartsWith("No repeatable background-load benefit", StringComparison.Ordinal) ||
         verdict.Reason.StartsWith("Candidate did not beat restored reference", StringComparison.Ordinal));

    // Historical or imported failure counters may be Int32.MaxValue.
    // Saturate before incrementing so corrupted state cannot wrap negative
    // and silently erase the backoff learned from unsuccessful experiments.
    internal static int NextFailureCount(int current) =>
        current >= 3 ? 3 : Math.Max(0, current) + 1;

    internal static TimeSpan RetryCooldown(int failureCount) => failureCount switch
    {
        <= 1 => TimeSpan.FromHours(12),
        2 => TimeSpan.FromDays(2),
        _ => TimeSpan.FromDays(7)
    };

    // Cooldown gates NEW experimental mutations only. Managed CandidateB
    // and ReferenceA, including non-sleeping A/B/A verification/rollback,
    // need immediate readback. An incomplete pending journal also needs
    // review; a separate fail-closed guard prevents an unowned pending row
    // from ever gaining authority for a fresh Windows mutation.
    internal static bool ShouldDeferForCooldown(AiSystemRuntimeItemState state, DateTime nowUtc) =>
        !state.Managed && string.IsNullOrWhiteSpace(state.PendingMutation) &&
        nowUtc.Kind == DateTimeKind.Utc &&
        state.CooldownUntilUtc is DateTime until &&
        until.Kind == DateTimeKind.Utc && until > nowUtc;

    // User-requested restore / AI scope disable is not evidence that the
    // operating system needed to undo an experimental optimization.
    internal static int RollbacksAfterRestore(int previous, bool externallyNeeded) =>
        externallyNeeded ? (previous >= int.MaxValue ? int.MaxValue : Math.Max(0, previous) + 1) :
        Math.Max(0, previous);

    internal static TimeSpan RestoreCooldown(int rollbackCount, bool externallyNeeded) =>
        !externallyNeeded ? TimeSpan.FromMinutes(10) :
        rollbackCount >= 2 ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(30);

    // A verified win remains useful for ranking after a planned six-hour
    // release, but not after Windows/application demand disproved that the
    // stopped component could remain unused. Never favor a candidate using
    // benefit evidence that was followed by an external wakeup.
    internal static AiSystemRuntimeItemState AfterRestoreEvidence(
        AiSystemRuntimeItemState state, bool externallyNeeded) =>
        state with
        {
            PerformanceVerified = false,
            LastVerifiedBenefitAbsolute = externallyNeeded
                ? 0 : state.LastVerifiedBenefitAbsolute,
            LastVerifiedBenefitPercent = externallyNeeded
                ? 0 : state.LastVerifiedBenefitPercent,
            LastVerifiedAvailableRamBenefitMiB = externallyNeeded
                ? null : state.LastVerifiedAvailableRamBenefitMiB,
            LastBenefitVerifiedUtc = externallyNeeded
                ? null : state.LastBenefitVerifiedUtc
        };

    internal static string RunFunctionalSmoke()
    {
        // Every candidate must eventually lead the pass, including those at
        // the end of each allowlist. Within one pass no entry can disappear.
        foreach (int count in new[] { 0, 1, 3, 14, 20 })
        {
            int start = 0;
            for (int cycle = 0; cycle < count; cycle++)
            {
                var seen = new bool[count];
                for (int offset = 0; offset < count; offset++)
                {
                    int index = IndexAt(start, offset, count);
                    if (index < 0 || index >= count || seen[index])
                        throw new InvalidOperationException("AI candidate exploration skipped or duplicated a target.");
                    seen[index] = true;
                }
                if (start != cycle)
                    throw new InvalidOperationException("AI candidate selection starved the later targets.");
                start = NextCursor(start, count);
            }
            if (start != 0)
                throw new InvalidOperationException("AI candidate rotation did not wrap safely.");
        }

        AiMaintenanceFeedbackVerdict noGain = new(true, false,
            "No repeatable background-load benefit (2.00% -> 2.05%)", -2.5, -0.05);
        AiMaintenanceFeedbackVerdict noise = new(true, false,
            "Candidate feedback is noisy: spread 3.0", 0, 0);
        AiMaintenanceFeedbackVerdict gain = new(true, true,
            "A/B/A confirmed", 12, 0.2);
        AiMaintenanceFeedbackVerdict referenceMiss = new(true, false,
            "Candidate did not beat restored reference (1.2% vs 1.1%)", -9, -0.1);
        if (!IsMeasuredNoBenefit(noGain) || !IsMeasuredNoBenefit(referenceMiss) ||
            IsMeasuredNoBenefit(noise) || IsMeasuredNoBenefit(gain) ||
            NextFailureCount(0) != 1 || NextFailureCount(1) != 2 ||
            NextFailureCount(2) != 3 || NextFailureCount(3) != 3 ||
            RetryCooldown(1) != TimeSpan.FromHours(12) ||
            RetryCooldown(2) != TimeSpan.FromDays(2) ||
            RetryCooldown(3) != TimeSpan.FromDays(7))
            throw new InvalidOperationException("AI measured-outcome learning policy regression.");
        // New learned evidence is durable across agent restarts, while old
        // persisted records lacking the optional field deserialize to zero.
        var currentState = new AiSystemRuntimeItemState(
            "service", "Fixture", false, false, string.Empty,
            null, null, null, null, false, 0, "none") with { FailedBenefitTrials = 2 };
        string currentJson = System.Text.Json.JsonSerializer.Serialize(currentState);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AiSystemRuntimeItemState>(currentJson);
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(currentJson)!.AsObject();
        legacy.Remove("FailedBenefitTrials");
        var upgraded = System.Text.Json.JsonSerializer.Deserialize<AiSystemRuntimeItemState>(legacy.ToJsonString());
        if (restored?.FailedBenefitTrials != 2 || upgraded?.FailedBenefitTrials != 0)
            throw new InvalidOperationException("AI learned optimization history was not restart/backward compatible.");
        var managed = new AiSystemRuntimeItemState(
            "task", "Fixture", true, true, string.Empty, null,
            DateTime.UtcNow.AddHours(1), null, true, false, 0, "candidate");
        DateTime testNow = DateTime.UtcNow;
        if (ShouldDeferForCooldown(managed, testNow) ||
            !ShouldDeferForCooldown(managed with { Managed = false }, testNow) ||
            // Reference A remains owned while its task/service is awake.
            // Recovery and verification must not be hidden by cooldown.
            ShouldDeferForCooldown(managed with { Sleeping = false }, testNow) ||
            ShouldDeferForCooldown(managed with {
                Managed = false, PendingMutation = AiMaintenanceWriteAheadPolicy.TaskDisable
            }, testNow) ||
            ShouldDeferForCooldown(managed with {
                Managed = false,
                CooldownUntilUtc = DateTime.SpecifyKind(testNow.AddHours(1), DateTimeKind.Unspecified)
            }, testNow) ||
            ShouldDeferForCooldown(managed with {
                Managed = false,
                CooldownUntilUtc = DateTime.SpecifyKind(testNow.AddHours(1), DateTimeKind.Local)
            }, testNow) ||
            ShouldDeferForCooldown(managed with { CooldownUntilUtc = testNow.AddSeconds(-1) }, testNow) ||
            RollbacksAfterRestore(1, false) != 1 ||
            RollbacksAfterRestore(1, true) != 2 ||
            RollbacksAfterRestore(int.MaxValue, true) != int.MaxValue ||
            RestoreCooldown(1, false) != TimeSpan.FromMinutes(10) ||
            RestoreCooldown(2, true) != TimeSpan.FromHours(6))
            throw new InvalidOperationException(
                "AI managed-resource wakeup or voluntary-restore policy regression.");
        return "AI_CANDIDATE_LEARNING_SMOKE_OK rotation=0,1,3,14,20 " +
            "measuredNoBenefit=true noisySampleNotPenalized=true " +
            "cooldowns=12h,48h,7d durable=true legacyDefaultZero=true managedWakeup=true restoreDoesNotTeachFailure=true writes=0";
    }
}