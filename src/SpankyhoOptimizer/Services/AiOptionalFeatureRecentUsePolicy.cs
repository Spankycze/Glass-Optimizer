using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Conservative recent-use memory. A momentarily empty printer queue or
/// disconnected Wi-Fi adapter does not erase recently confirmed demand.
/// This policy cannot authorize disabling any Windows component.
/// </summary>
internal static class AiOptionalFeatureRecentUsePolicy
{
    internal static readonly TimeSpan RecentUseWindow = TimeSpan.FromHours(24);

    internal static DateTime? LastConfirmedUse(
        DateTime? priorUtc, AiOptionalFeatureUsage current, DateTime nowUtc)
    {
        // Do not erase earlier confirmed demand after a clock rollback or
        // corrupt/non-UTC journal. The admission gate treats uncertain use
        // as a one-way veto until its timestamp can be evaluated safely.
        if (nowUtc.Kind != DateTimeKind.Utc)
            return priorUtc;
        if (current == AiOptionalFeatureUsage.InUse)
            return nowUtc;
        if (priorUtc is not DateTime last)
            return null;
        if (last.Kind != DateTimeKind.Utc || last > nowUtc)
            return last;
        return nowUtc - last <= RecentUseWindow ? last : null;
    }

    internal static AiOptionalFeatureUsage Effective(
        AiOptionalFeatureUsage current, DateTime? lastUsedUtc, DateTime nowUtc) =>
        LastConfirmedUse(lastUsedUtc, current, nowUtc).HasValue
            ? AiOptionalFeatureUsage.InUse
            : current;
}
