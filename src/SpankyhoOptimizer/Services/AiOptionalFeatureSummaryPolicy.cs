using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Read-only device and per-user observation rows are inventory, not blocked
/// mutation attempts. Preserve all genuine rollback and safety blocks.
/// </summary>
internal static class AiOptionalFeatureSummaryPolicy
{
    internal static bool IsPassiveObservation(AiSystemRuntimeItemState item) =>
        // Most runtime rows have no feature observation. Check the numeric
        // guard before comparing the service kind and localized action text.
        item.FeatureObservationSamples > 0 && !item.Managed && !item.Sleeping &&
        item.Rollbacks == 0 && item.VerificationSamples == 0 &&
        string.Equals(item.Kind, "service", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(item.LastAction, "none", StringComparison.OrdinalIgnoreCase) &&
        item.BlockedReason?.StartsWith("Sledování [", StringComparison.Ordinal) == true;

    internal static bool CountsAsBlocked(AiSystemRuntimeItemState item)
    {
        // Common hot path: an empty/whitespace reason is never an actual
        // veto, even for an AI-owned item. Skip all further categorization
        // and string comparisons without changing safety classifications.
        if (string.IsNullOrWhiteSpace(item.BlockedReason))
            return false;
        if (IsPassiveObservation(item))
            return false;
        return AiRuntimeBlockedSummaryPolicy.Classify(item.BlockedReason) is
            AiRuntimeBlockedSummaryPolicy.Category.Protected or
            AiRuntimeBlockedSummaryPolicy.Category.Other ||
            (item.Managed || item.Sleeping);
    }
}
