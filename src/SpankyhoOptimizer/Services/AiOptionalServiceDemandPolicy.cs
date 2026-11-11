using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Fail-closed admission veto for any optional service which has persisted
/// evidence of real device demand. Never admits a Windows mutation; normal
/// ownership, dependencies, smoothness and measured A/B/A still apply.
/// </summary>
internal static class AiOptionalServiceDemandPolicy
{
    internal static bool HasRecentDemand(AiSystemRuntimeItemState state, DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
            return true;
        // Corrupt or future timestamps cannot be treated as proof of idleness.
        if (state.FeatureLastConfirmedUseUtc is DateTime used &&
            (used.Kind != DateTimeKind.Utc || used > nowUtc ||
             nowUtc - used <= AiOptionalFeatureRecentUsePolicy.RecentUseWindow))
            return true;
        // A stale confirmed-use timestamp cannot mask newer live demand
        // if this entry was subsequently classified as InUse.
        if (state.FeatureObservedUsage != AiOptionalFeatureUsage.InUse)
            return false;
        // Old journals may contain classified demand without a separate
        // last-confirmed timestamp. Honor it while its scan remains recent.
        if (state.FeatureLastObservedUtc is not DateTime observed)
            return true;
        return observed.Kind != DateTimeKind.Utc || observed > nowUtc ||
            nowUtc - observed <= AiOptionalFeatureRecentUsePolicy.RecentUseWindow;
    }
}
