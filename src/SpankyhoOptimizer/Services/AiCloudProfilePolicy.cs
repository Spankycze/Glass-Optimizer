using System;
using System.Collections.Generic;
using System.Linq;

namespace SpankyhoOptimizer.Services;

public static class AiCloudProfilePolicy
{
    public static bool ShouldBackgroundSync(AiWorkloadClass workload) =>
        AiSharedWorkloadPolicy.AllowsBackgroundAutomation(workload);

    public static AiSystemRuntimeConfig SanitizeConfig(AiSystemRuntimeConfig config) =>
        AiRuntimeScopePolicy.Sanitize(config);

    // Cloud restore is learned *caution*, not a replayable Windows mutation
    // journal. A remote row must never replace any locally owned A/B/A trial,
    // restore original, task schedule, or confirmed device-demand observation.
    internal static AiSystemRuntimeItemState? MergeLearnedProtection(
        AiSystemRuntimeItemState? local, AiSystemRuntimeItemState remote,
        DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc ||
            local is { Managed: true } or { Sleeping: true } ||
            local?.VerificationStartedUtc is not null ||
            !string.IsNullOrEmpty(local?.PendingMutation) ||
            local?.OriginalServiceStart is not null ||
            local?.OriginalTaskEnabled is not null)
            return null;

        int learnedRollbacks = Math.Max(local?.Rollbacks ?? 0,
            Math.Max(0, remote.Rollbacks));
        int learnedFailures = Math.Max(local?.FailedBenefitTrials ?? 0,
            Math.Clamp(remote.FailedBenefitTrials, 0, 3));
        if (local is not null && learnedRollbacks == local.Rollbacks &&
            learnedFailures == local.FailedBenefitTrials)
            return null;
        if (local is null && learnedRollbacks == 0 && learnedFailures == 0)
            return null;

        AiSystemRuntimeItemState baseline = local ?? new AiSystemRuntimeItemState(
            remote.Kind, remote.Id, false, false, string.Empty, null, null,
            null, null, false, 0, "none");
        return baseline with
        {
            Rollbacks = learnedRollbacks,
            FailedBenefitTrials = learnedFailures,
            LastAction = "cloud protection merged (no OS ownership imported)",
            LastChangedUtc = nowUtc
        };
    }

    public static AiSystemRuntimeItemState[] SanitizeItems(
        IEnumerable<AiSystemRuntimeItemState> items) =>
        items.Where(x => !(string.Equals(x.Kind, "service", StringComparison.OrdinalIgnoreCase) &&
            x.FeatureObservationSamples > 0 &&
            !x.Managed && !x.Sleeping && x.Rollbacks == 0 &&
            x.VerificationSamples == 0 && x.LastAction == "none"))
        .Select(x => x with
        {
            Managed = false,
            Sleeping = false,
            ObservationSinceUtc = null,
            CooldownUntilUtc = null,
            OriginalServiceStart = null,
            OriginalTaskEnabled = null,
            OriginalTaskNextRunUtc = null,
            PendingMutation = string.Empty,
            WasRunningBefore = false,
            PerformanceVerified = false,
            // CPU/RAM wins are measurements on a particular Windows host,
            // not portable proof that stopping the same service on a second
            // machine will save resources. Export only cautionary learning.
            LastVerifiedBenefitAbsolute = 0,
            LastVerifiedBenefitPercent = 0,
            LastVerifiedAvailableRamBenefitMiB = null,
            LastBenefitVerifiedUtc = null,
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
            BaselineAvailableRamMiB = null,
            VerificationMemorySamples = 0,
            VerificationMemoryTotalMiB = 0,
            VerificationMemoryMinMiB = null,
            VerificationMemoryMaxMiB = null,
            VerificationLastMemorySnapshotUtc = null,
            CandidateMemorySamples = 0,
            CandidateAvailableRamMiB = null,
            CandidateMemoryMinMiB = null,
            CandidateMemoryMaxMiB = null,
            DriverObservationLastUtc = null,
            FeatureFirstObservedUtc = null,
            FeatureLastObservedUtc = null,
            FeatureObservationSamples = 0,
            FeatureObservedRunning = null,
            FeatureObservedUsage = AiOptionalFeatureUsage.Unknown,
            FeatureLastConfirmedUseUtc = null,
            FeatureLastUsageProbeUtc = null
        }).ToArray();
}
