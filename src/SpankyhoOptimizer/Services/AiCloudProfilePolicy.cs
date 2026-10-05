using System.Collections.Generic;
using System.Linq;

namespace SpankyhoOptimizer.Services;

public static class AiCloudProfilePolicy
{
    public static bool ShouldBackgroundSync(AiWorkloadClass workload) =>
        AiSharedWorkloadPolicy.AllowsBackgroundAutomation(workload);

    public static AiSystemRuntimeConfig SanitizeConfig(AiSystemRuntimeConfig config) =>
        AiRuntimeScopePolicy.Sanitize(config);

    public static AiSystemRuntimeItemState[] SanitizeItems(
        IEnumerable<AiSystemRuntimeItemState> items) =>
        items.Select(x => x with
        {
            Managed = false,
            Sleeping = false,
            ObservationSinceUtc = null,
            CooldownUntilUtc = null,
            OriginalServiceStart = null,
            OriginalTaskEnabled = null,
            WasRunningBefore = false,
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
            CandidateLoadMax = null
        }).ToArray();
}
