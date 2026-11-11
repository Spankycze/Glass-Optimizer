using System;

namespace SpankyhoOptimizer.Services;

public readonly record struct AiMemoryWindow(int Count, double TotalPhysicalMiB,
    double AvailableTotalMiB, double MinAvailableMiB, double MaxAvailableMiB,
    DateTime FirstUtc, DateTime LastUtc);

public sealed record AiMemoryTrialEvidence(string Outcome, double? AvailableDeltaMiB,
    double? BaselineAvailableMiB, double? CandidateAvailableMiB,
    double? ReferenceAvailableMiB, string Reason);

internal static class AiMaintenanceMemoryPolicy
{
    internal static AiMemoryWindow Add(AiMemoryWindow window,
        AiSystemSmoothnessHealth? health, DateTime nowUtc) => default;

    internal static AiMemoryTrialEvidence Evaluate(AiMemoryWindow baseline,
        AiMemoryWindow candidate, AiMemoryWindow reference) =>
        new("Unavailable", null, null, null, null, "RAM evidence unavailable");
}
