using System;

namespace SpankyhoOptimizer.Services;

public sealed partial class AiSystemResourceManagerService
{
    // A Candidate B trial keeps identical bookkeeping for services and scheduled
    // tasks; only the ownership columns differ before this reset.
    private static AiSystemRuntimeItemState MarkTrialStarted(
        AiSystemRuntimeItemState intent,
        int actionCooldownSeconds,
        double baselineLoad,
        double? baselineAvailableRam) =>
        intent with
        {
            ObservationSinceUtc = null,
            CooldownUntilUtc = DateTime.UtcNow.AddSeconds(actionCooldownSeconds),
            LastAction = "candidate trial: waiting for measured feedback",
            LastChangedUtc = DateTime.UtcNow,
            BaselineLoadPercent = baselineLoad,
            BaselineAvailableRamMiB = baselineAvailableRam,
            VerificationMemorySamples = 0,
            VerificationMemoryTotalMiB = 0,
            VerificationMemoryMinMiB = null,
            VerificationMemoryMaxMiB = null,
            VerificationLastMemorySnapshotUtc = null,
            CandidateMemorySamples = 0,
            CandidateAvailableRamMiB = null,
            CandidateMemoryMinMiB = null,
            CandidateMemoryMaxMiB = null,
            VerificationStartedUtc = DateTime.UtcNow,
            VerificationLastSampleUtc = null,
            VerificationSamples = 0,
            VerificationLoadTotal = 0,
            PerformanceVerified = false,
            VerificationPhase = "CandidateB",
            CandidateLoadPercent = null,
            VerificationLoadMin = null,
            VerificationLoadMax = null,
            CandidateLoadMin = null,
            CandidateLoadMax = null
        };

    // Yielding to an external change discards every verified-trial column.
    private static AiSystemRuntimeItemState ClearVerifiedTrial(AiSystemRuntimeItemState state) =>
        state with
        {
            BaselineLoadPercent = null,
            VerificationStartedUtc = null,
            VerificationLastSampleUtc = null,
            VerificationSamples = 0,
            VerificationLoadTotal = 0,
            PerformanceVerified = false,
            VerificationPhase = string.Empty,
            CandidateLoadPercent = null,
            VerificationLoadMin = null,
            VerificationLoadMax = null,
            CandidateLoadMin = null,
            CandidateLoadMax = null
        };
}
