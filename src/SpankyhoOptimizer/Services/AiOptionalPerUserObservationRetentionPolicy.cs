using System;
using System.Collections.Generic;
using System.ServiceProcess;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Retire only abandoned read-only observations for dynamic per-user services.
/// This MUST NOT remove ownership, recovery intent or measured experiment data.
/// </summary>
internal static class AiOptionalPerUserObservationRetentionPolicy
{
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    internal static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(30);

    internal static bool ShouldPrune(
        AiSystemRuntimeItemState row, bool serviceStillInstalled, DateTime nowUtc)
    {
        if (serviceStillInstalled || nowUtc.Kind != DateTimeKind.Utc ||
            !AiOptionalPerUserWindowsFeatureCatalog.TryResolve(row.Id, out _) ||
            !AiOptionalFeatureSummaryPolicy.IsPassiveObservation(row) ||
            !string.IsNullOrEmpty(row.PendingMutation) || row.PerformanceVerified ||
            row.FailedBenefitTrials > 0 || row.LastBenefitVerifiedUtc.HasValue ||
            row.OriginalServiceStart.HasValue || row.CooldownUntilUtc.HasValue ||
            row.FeatureLastObservedUtc is not DateTime last ||
            last.Kind != DateTimeKind.Utc || last > nowUtc ||
            nowUtc - last < Retention)
            return false;

        DateTime? lastUse = row.FeatureLastConfirmedUseUtc;
        return lastUse is null ||
            (lastUse.Value.Kind == DateTimeKind.Utc &&
             lastUse.Value <= nowUtc && nowUtc - lastUse.Value >= Retention);
    }

    internal static int Prune(
        IDictionary<string, AiSystemRuntimeItemState> items,
        IReadOnlyDictionary<string, ServiceControllerStatus> installed,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(installed);
        List<string>? expired = null;
        foreach (var pair in items)
        {
            if (ShouldPrune(pair.Value, installed.ContainsKey(pair.Value.Id), nowUtc))
                (expired ??= new()).Add(pair.Key);
        }
        if (expired is null) return 0;
        foreach (string key in expired) items.Remove(key);
        return expired.Count;
    }
}
