using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// A stored "blocked" reason is not a pending optimization. Missing optional
/// Windows components are inventory facts, while real safety vetoes require a
/// different UI category. Do not change any actual admission/rollback rules.
/// </summary>
internal static class AiRuntimeBlockedSummaryPolicy
{
    internal enum Category { None, Unavailable, Unreadable, Protected, Other }

    internal readonly record struct Summary(
        int Protected, int Unavailable, int Other,
        int Observed = 0, int RecentlyUsed = 0, int ObservationCandidates = 0,
        int Unreadable = 0)
    {
        internal int Total => Protected + Unavailable + Other + Unreadable;
        internal int ActiveSafetyVetoes => Protected + Other;
        internal int UnknownObserved => Observed - RecentlyUsed - ObservationCandidates;
    }

    // A blocked AI-owned restore must never disappear behind ordinary
    // inventory or be reported as a routine stopped service/driver.
    // Both localized persisted reasons and earlier English journals exist.
    internal static bool RequiresManualRecovery(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) &&
        (reason.Contains("ruční obnova nutná", StringComparison.OrdinalIgnoreCase) ||
         reason.Contains("manual recovery required", StringComparison.OrdinalIgnoreCase));

    internal static Category Classify(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Category.None;
        // Imported/legacy journal entries can contain surrounding whitespace.
        // Slice the existing string without allocating another trimmed string.
        ReadOnlySpan<char> normalized = reason.AsSpan().Trim();
        if (normalized.Equals("not installed", StringComparison.OrdinalIgnoreCase))
            return Category.Unavailable;
        // Older journals conflated two possibilities: treat ambiguous entries
        // as unverified rather than alleging the component is absent.
        if (normalized.Equals("not installed/unreadable", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("task status unreadable", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("service status unreadable", StringComparison.OrdinalIgnoreCase))
            return Category.Unreadable;
        ReadOnlySpan<char> learnedPrefix = "AI learned protection";
        bool learnedProtection = normalized.StartsWith(learnedPrefix, StringComparison.OrdinalIgnoreCase) &&
            (normalized.Length == learnedPrefix.Length ||
             !char.IsLetterOrDigit(normalized[learnedPrefix.Length]));
        if (RequiresManualRecovery(reason) ||
            normalized.Equals("critical denylist", StringComparison.OrdinalIgnoreCase) ||
            learnedProtection ||
            normalized.Contains("opakovaně obnoveno systémem", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("running dependent service", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("service nelze bezpečně zastavit", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("confirmed recent device use; service trial deferred", StringComparison.OrdinalIgnoreCase) ||
            (normalized.StartsWith("task is active or scheduled:", StringComparison.OrdinalIgnoreCase) &&
             !normalized["task is active or scheduled:".Length..].Trim().IsEmpty))
            return Category.Protected;
        return Category.Other;
    }

    internal static Summary Count(IReadOnlyList<AiSystemRuntimeItemState> items)
    {
        int protectedItems = 0, unavailable = 0, other = 0, unreadable = 0;
        int observed = 0, recentlyUsed = 0, candidates = 0;
        // ReadItems returns an array. Indexing IReadOnlyList avoids boxing
        // the array enumerator on each GUI refresh / benchmark invocation.
        for (int i = 0; i < items.Count; i++)
        {
            AiSystemRuntimeItemState item = items[i];
            if (AiOptionalFeatureSummaryPolicy.IsPassiveObservation(item))
            {
                observed++;
                if (item.FeatureObservedUsage == AiOptionalFeatureUsage.InUse)
                    recentlyUsed++;
                else if (item.FeatureObservedUsage == AiOptionalFeatureUsage.CandidateForObservation)
                    candidates++;
                continue;
            }
            switch (Classify(item.BlockedReason))
            {
                case Category.Protected: protectedItems++; break;
                case Category.Unavailable: unavailable++; break;
                case Category.Unreadable: unreadable++; break;
                case Category.Other: other++; break;
            }
        }
        return new(protectedItems, unavailable, other, observed, recentlyUsed, candidates, unreadable);
    }
}
