namespace SpankyhoOptimizer.Services;

// Presence is not usage. A disconnected adapter or an installed spooler is
// evidence for observation, never by itself permission to disable a feature.
public enum AiOptionalFeatureUsage { Unknown, InUse, CandidateForObservation }

internal static class AiOptionalFeatureUsagePolicy
{
    internal static AiOptionalFeatureUsage Classify(
        bool installed, bool usageSignalKnown, bool currentlyInUse,
        bool hasPendingWork, bool backgroundDependencyPresent)
    {
        if (!installed || !usageSignalKnown)
            return AiOptionalFeatureUsage.Unknown;
        if (currentlyInUse || hasPendingWork || backgroundDependencyPresent)
            return AiOptionalFeatureUsage.InUse;
        return AiOptionalFeatureUsage.CandidateForObservation;
    }
}
