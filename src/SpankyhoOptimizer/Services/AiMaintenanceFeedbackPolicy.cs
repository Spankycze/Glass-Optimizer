using System;

namespace SpankyhoOptimizer.Services;

public sealed record AiMaintenanceFeedbackVerdict(
    bool Ready,
    bool Accepted,
    string Reason,
    double ImprovementPercent,
    double ImprovementAbsolute);

public static class AiMaintenanceFeedbackPolicy
{
    public const int RequiredCandidateSamples = 3;
    public const double MinimumImprovementPercent = 10.0;
    public const double MinimumAbsoluteImprovement = 0.15;
    public const double MinimumSpreadTolerance = 0.25;
    public const double MaximumRelativeSpread = 0.45;
    public static readonly TimeSpan MaximumVerificationDuration = TimeSpan.FromMinutes(30);

    public static AiMaintenanceFeedbackVerdict Evaluate(
        double baselineLoadPercent,
        int candidateSamples,
        double candidateLoadTotal,
        double? candidateMin = null,
        double? candidateMax = null)
    {
        if (!double.IsFinite(baselineLoadPercent) || baselineLoadPercent < 0.75)
            return new(false, false, "Baseline background pressure is not measurable", 0, 0);

        if (candidateSamples < RequiredCandidateSamples)
            return new(false, false, $"Collecting candidate feedback {candidateSamples}/{RequiredCandidateSamples}", 0, 0);

        double candidate = candidateLoadTotal / candidateSamples;
        if (!double.IsFinite(candidate) || candidate < 0 || candidate > AiBackgroundPerformancePolicy.MaximumQuietLoadPercent)
            return new(true, false, "Candidate feedback is invalid/noisy", 0, 0);
        if (!IsStableWindow(candidate, candidateMin, candidateMax, out string candidateSpreadReason))
            return new(true, false, "Candidate feedback is noisy: " + candidateSpreadReason, 0, 0);

        return CompareCandidate(baselineLoadPercent, candidate);
    }

    public static AiMaintenanceFeedbackVerdict EvaluateAba(
        double baselineLoadPercent,
        double candidateLoadPercent,
        int referenceSamples,
        double referenceLoadTotal,
        double? candidateMin = null,
        double? candidateMax = null,
        double? referenceMin = null,
        double? referenceMax = null)
    {
        if (!double.IsFinite(baselineLoadPercent) || baselineLoadPercent < 0.75 ||
            !double.IsFinite(candidateLoadPercent) || candidateLoadPercent < 0)
            return new(false, false, "A/B/A inputs are not measurable", 0, 0);

        if (referenceSamples < RequiredCandidateSamples)
            return new(false, false, $"Collecting reference feedback {referenceSamples}/{RequiredCandidateSamples}", 0, 0);

        double reference = referenceLoadTotal / referenceSamples;
        if (!double.IsFinite(reference) || reference < 0 ||
            reference > AiBackgroundPerformancePolicy.MaximumQuietLoadPercent)
            return new(true, false, "Reference feedback is invalid/noisy", 0, 0);
        if (!IsStableWindow(candidateLoadPercent, candidateMin, candidateMax, out string candidateSpreadReason))
            return new(true, false, "Candidate feedback is noisy: " + candidateSpreadReason, 0, 0);
        if (!IsStableWindow(reference, referenceMin, referenceMax, out string referenceSpreadReason))
            return new(true, false, "Reference feedback is noisy: " + referenceSpreadReason, 0, 0);

        double recoveryTolerance = Math.Max(0.20, baselineLoadPercent * 0.18);
        if (Math.Abs(reference - baselineLoadPercent) > recoveryTolerance)
            return new(true, false,
                $"Reference A did not recover ({baselineLoadPercent:0.00}% -> {reference:0.00}%)",
                0, 0);

        AiMaintenanceFeedbackVerdict vsBaseline = CompareCandidate(baselineLoadPercent, candidateLoadPercent);
        if (!vsBaseline.Accepted)
            return vsBaseline;

        double absoluteVsReference = reference - candidateLoadPercent;
        double percentVsReference = absoluteVsReference / reference * 100d;
        bool accepted = absoluteVsReference >= MinimumAbsoluteImprovement &&
                        percentVsReference >= MinimumImprovementPercent;

        return accepted
            ? new(true, true,
                $"A/B/A confirmed {baselineLoadPercent:0.00}% -> {candidateLoadPercent:0.00}% -> {reference:0.00}% ({percentVsReference:0.0}% vs reference)",
                percentVsReference, absoluteVsReference)
            : new(true, false,
                $"Candidate did not beat restored reference ({candidateLoadPercent:0.00}% vs {reference:0.00}%)",
                percentVsReference, absoluteVsReference);
    }

    private static bool IsStableWindow(
        double mean,
        double? minimum,
        double? maximum,
        out string reason)
    {
        reason = string.Empty;
        if (minimum is not double min || maximum is not double max)
            return true;
        if (!double.IsFinite(min) || !double.IsFinite(max) || min < 0 || max < min)
        {
            reason = "invalid sample range";
            return false;
        }

        double spread = max - min;
        double allowed = Math.Max(MinimumSpreadTolerance, mean * MaximumRelativeSpread);
        if (spread <= allowed)
            return true;

        reason = $"spread {spread:0.00} > {allowed:0.00}";
        return false;
    }

    private static AiMaintenanceFeedbackVerdict CompareCandidate(
        double baselineLoadPercent,
        double candidateLoadPercent)
    {
        double absolute = baselineLoadPercent - candidateLoadPercent;
        double percent = absolute / baselineLoadPercent * 100d;
        bool accepted = absolute >= MinimumAbsoluteImprovement &&
                        percent >= MinimumImprovementPercent;

        return accepted
            ? new(true, true,
                $"Measured background load improved {baselineLoadPercent:0.00}% -> {candidateLoadPercent:0.00}% ({percent:0.0}%)",
                percent, absolute)
            : new(true, false,
                $"No repeatable background-load benefit ({baselineLoadPercent:0.00}% -> {candidateLoadPercent:0.00}%)",
                percent, absolute);
    }
}
