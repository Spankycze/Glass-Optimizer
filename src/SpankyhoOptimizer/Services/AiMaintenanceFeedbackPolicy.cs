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
        // A non-measurable baseline cannot become valid by collecting more
        // Candidate-B samples. Close the unsafe trial as inconclusive now.
        if (!double.IsFinite(baselineLoadPercent) || baselineLoadPercent < 0.75 ||
            baselineLoadPercent > AiBackgroundPerformancePolicy.MaximumQuietLoadPercent)
            return new(true, false,
                "Baseline background pressure is not measurable", 0, 0);

        if (candidateSamples < 0)
            return new(true, false, "Candidate sample count is invalid", 0, 0);
        // Corrupt partial evidence must not keep an owned service or task
        // in Candidate B merely because three observations have not arrived.
        if (!double.IsFinite(candidateLoadTotal) || candidateLoadTotal < 0 ||
            (candidateSamples == 0 && candidateLoadTotal != 0) ||
            (candidateSamples > 0 && candidateSamples < RequiredCandidateSamples &&
             candidateLoadTotal > candidateSamples * AiBackgroundPerformancePolicy.MaximumQuietLoadPercent))
            return new(true, false, "Candidate accumulated feedback is invalid", 0, 0);
        if (candidateSamples < RequiredCandidateSamples)
        {
            if (HasImpossiblePartialRange(candidateSamples, candidateLoadTotal, candidateMin, candidateMax))
                return new(true, false, "Candidate partial range is inconsistent", 0, 0);
            return new(false, false, $"Collecting candidate feedback {candidateSamples}/{RequiredCandidateSamples}", 0, 0);
        }
        // The live Candidate B collector advances after exactly three
        // observations. Four or more can only come from an inconsistent
        // imported/restarted phase and must never confirm a fresh CPU win.
        if (candidateSamples != RequiredCandidateSamples)
            return new(true, false, "Candidate sample count is inconsistent", 0, 0);

        double candidate = candidateLoadTotal / candidateSamples;
        if (!double.IsFinite(candidate) || candidate < 0 || candidate > AiBackgroundPerformancePolicy.MaximumQuietLoadPercent)
            return new(true, false, "Candidate feedback is invalid/noisy", 0, 0);
        if (!IsStableWindow(candidate, candidateSamples, candidateMin, candidateMax, out string candidateSpreadReason))
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
            baselineLoadPercent > AiBackgroundPerformancePolicy.MaximumQuietLoadPercent ||
            !double.IsFinite(candidateLoadPercent) || candidateLoadPercent < 0 ||
            candidateLoadPercent > AiBackgroundPerformancePolicy.MaximumQuietLoadPercent)
            return new(true, false,
                "A/B/A inputs are not measurable", 0, 0);

        if (referenceSamples < 0)
            return new(true, false, "A/B/A reference sample count is invalid", 0, 0);
        // Treat damaged in-progress Reference A journals as terminally
        // inconclusive instead of waiting for missing samples indefinitely.
        if (!double.IsFinite(referenceLoadTotal) || referenceLoadTotal < 0 ||
            (referenceSamples == 0 && referenceLoadTotal != 0) ||
            (referenceSamples > 0 && referenceSamples < RequiredCandidateSamples &&
             referenceLoadTotal > referenceSamples * AiBackgroundPerformancePolicy.MaximumQuietLoadPercent))
            return new(true, false, "Reference accumulated feedback is invalid", 0, 0);
        if (referenceSamples < RequiredCandidateSamples)
        {
            // Candidate B has already finished before restored Reference A
            // collection starts. Invalid prior evidence cannot be repaired
            // by waiting for more reference samples: fail closed promptly.
            if (!IsStableWindow(candidateLoadPercent, RequiredCandidateSamples,
                    candidateMin, candidateMax, out string candidateReason))
                return new(true, false, "Candidate feedback is noisy: " + candidateReason, 0, 0);
            if (HasImpossiblePartialRange(referenceSamples, referenceLoadTotal, referenceMin, referenceMax))
                return new(true, false, "Reference partial range is inconsistent", 0, 0);
            return new(false, false, $"Collecting reference feedback {referenceSamples}/{RequiredCandidateSamples}", 0, 0);
        }
        if (referenceSamples != RequiredCandidateSamples)
            return new(true, false, "A/B/A reference sample count is inconsistent", 0, 0);

        double reference = referenceLoadTotal / referenceSamples;
        if (!double.IsFinite(reference) || reference < 0 ||
            reference > AiBackgroundPerformancePolicy.MaximumQuietLoadPercent)
            return new(true, false, "Reference feedback is invalid/noisy", 0, 0);
        if (!IsStableWindow(candidateLoadPercent, RequiredCandidateSamples, candidateMin, candidateMax, out string candidateSpreadReason))
            return new(true, false, "Candidate feedback is noisy: " + candidateSpreadReason, 0, 0);
        if (!IsStableWindow(reference, referenceSamples, referenceMin, referenceMax, out string referenceSpreadReason))
            return new(true, false, "Reference feedback is noisy: " + referenceSpreadReason, 0, 0);

        double recoveryTolerance = Math.Max(0.20, baselineLoadPercent * 0.18);
        if (Math.Abs(reference - baselineLoadPercent) > recoveryTolerance)
            return new(true, false,
                $"Reference A did not recover ({baselineLoadPercent:0.00}% -> {reference:0.00}%)",
                0, 0);

        AiMaintenanceFeedbackVerdict vsBaseline = CompareCandidate(baselineLoadPercent, candidateLoadPercent);
        if (!vsBaseline.Accepted)
            return vsBaseline;

        // Exactly three samples are recorded in each A/B/A phase. For
        // three observations the median can be reconstructed from the
        // already-durable total, minimum and maximum without another sample,
        // allocation or OS probe. A single low Candidate-B outlier must not
        // create a purported repeatable CPU saving when the typical reading
        // did not actually improve over restored reference A.
        double candidateMedian = 3d * candidateLoadPercent -
            candidateMin!.Value - candidateMax!.Value;
        double referenceMedian = referenceLoadTotal -
            referenceMin!.Value - referenceMax!.Value;
        double medianImprovement = referenceMedian - candidateMedian;
        bool repeatableMedianGain = referenceMedian > 0 &&
            medianImprovement >= MinimumAbsoluteImprovement &&
            medianImprovement / referenceMedian * 100d >= MinimumImprovementPercent;
        double absoluteVsReference = reference - candidateLoadPercent;
        // Zero reference CPU cannot establish a relative saving.
        double percentVsReference = reference > 0
            ? absoluteVsReference / reference * 100d : 0;
        bool accepted = referenceSamples == RequiredCandidateSamples &&
                        absoluteVsReference >= MinimumAbsoluteImprovement &&
                        percentVsReference >= MinimumImprovementPercent &&
                        repeatableMedianGain;

        return accepted
            ? new(true, true,
                $"A/B/A confirmed {baselineLoadPercent:0.00}% -> {candidateLoadPercent:0.00}% -> {reference:0.00}% ({percentVsReference:0.0}% vs reference; median gain {medianImprovement:0.00}pp)",
                percentVsReference, absoluteVsReference)
            : new(true, false,
                $"Candidate did not beat restored reference repeatably ({candidateLoadPercent:0.00}% vs {reference:0.00}%; median gap {medianImprovement:0.00}pp)",
                percentVsReference, absoluteVsReference);
    }

    private static bool HasImpossiblePartialRange(int count, double total, double? minimum, double? maximum)
    {
        // Zero observations cannot establish extrema, including numeric zero.
        // Reject an incoherent persisted phase instead of counting it as
        // pending until more samples arrive with fabricated prior evidence.
        if (count == 0)
            return minimum.HasValue || maximum.HasValue;
        // Older journals can omit either bound, but a bound that IS present
        // must still be mathematically possible for the collected samples.
        // Do not leave an owned trial running on invalid partial evidence.
        if (minimum is double knownMin &&
            (!double.IsFinite(knownMin) || knownMin < 0 ||
             knownMin > AiBackgroundPerformancePolicy.MaximumQuietLoadPercent ||
             (count == 1 && Math.Abs(total - knownMin) > 0.000000001) ||
             (count == 2 && (total < 2 * knownMin - 0.000000001 ||
                              total > knownMin + AiBackgroundPerformancePolicy.MaximumQuietLoadPercent + 0.000000001))))
            return true;
        if (maximum is double knownMax &&
            (!double.IsFinite(knownMax) || knownMax < 0 ||
             knownMax > AiBackgroundPerformancePolicy.MaximumQuietLoadPercent ||
             (count == 1 && Math.Abs(total - knownMax) > 0.000000001) ||
             (count == 2 && (total > 2 * knownMax + 0.000000001 ||
                              total < knownMax - 0.000000001))))
            return true;
        if (minimum is null || maximum is null)
            return false;
        double min = minimum.Value, max = maximum.Value;
        if (!double.IsFinite(min) || !double.IsFinite(max) || min < 0 ||
            max < min || max > AiBackgroundPerformancePolicy.MaximumQuietLoadPercent)
            return true;
        if (count == 1)
            return Math.Abs(total - min) > 0.000000001 ||
                   Math.Abs(total - max) > 0.000000001;
        return Math.Abs(total - (min + max)) > 0.000000001;
    }

    private static bool IsStableWindow(
        double mean,
        int sampleCount,
        double? minimum,
        double? maximum,
        out string reason)
    {
        reason = string.Empty;
        // Readback-compatible old snapshots may lack these two fields.
        // Missing spread evidence is NOT proof of a stable A/B/A window.
        if (minimum is not double min || maximum is not double max)
        {
            reason = "missing sample range";
            return false;
        }
        if (!double.IsFinite(mean) || !double.IsFinite(min) || !double.IsFinite(max) ||
            min < 0 || max < min ||
            max > AiBackgroundPerformancePolicy.MaximumQuietLoadPercent ||
            mean < min - 0.000000001 || mean > max + 0.000000001)
        {
            reason = "invalid sample range";
            return false;
        }

        // The reported range and mean must describe the SAME sample set.
        // With N observations including one minimum and one maximum, a
        // mean outside these tight bounds is mathematically impossible.
        // This catches mismatched or corrupt A/B/A phase journals.
        if (sampleCount < RequiredCandidateSamples ||
            mean < (max + (sampleCount - 1d) * min) / sampleCount - 0.000000001 ||
            mean > (min + (sampleCount - 1d) * max) / sampleCount + 0.000000001)
        {
            reason = "sample count, range and mean are inconsistent";
            return false;
        }

        double spread = max - min;
        double allowed = Math.Max(MinimumSpreadTolerance, mean * MaximumRelativeSpread);
        if (spread <= allowed)
            return true;

        reason = $"spread {spread:0.00} > {allowed:0.00}";
        return false;
    }

    internal static string RunFunctionalGuardSmoke()
    {
        AiMaintenanceFeedbackVerdict candidate = Evaluate(1.5, 3, 3.6, 1.1, 1.3);
        AiMaintenanceFeedbackVerdict aba = EvaluateAba(
            1.5, 1.1, 3, 4.5, 1.0, 1.2, 1.4, 1.6);
        // Exercise the median attribution gate in the release smoke too:
        // a single favorable low outlier can make the average appear faster
        // even though the middle CPU sample did not improve repeatably.
        AiMaintenanceFeedbackVerdict falseMeanWin = EvaluateAba(
            1.5, 1.30, 3, 4.5, 1.00, 1.50, 1.4, 1.6);
        AiMaintenanceFeedbackVerdict trueMedianWin = EvaluateAba(
            1.5, 1.10, 3, 4.5, 1.00, 1.25, 1.4, 1.6);
        if (!candidate.Ready || !candidate.Accepted ||
            !aba.Ready || !aba.Accepted ||
            !falseMeanWin.Ready || falseMeanWin.Accepted ||
            !trueMedianWin.Ready || !trueMedianWin.Accepted ||
            Evaluate(1.5, 3, 3.6).Accepted ||
            !Evaluate(1.5, 3, 3.6).Ready ||
            !Evaluate(double.NaN, 3, 3.6, 1.1, 1.3).Ready ||
            Evaluate(double.NaN, 3, 3.6, 1.1, 1.3).Accepted ||
            Evaluate(1.5, 3, 3.6, 0.1, 2.0).Accepted ||
            EvaluateAba(1.5, 1.1, 3, 4.5, 1.0, 1.2).Accepted ||
            !EvaluateAba(double.NaN, 1.1, 3, 4.5, 1.0, 1.2, 1.4, 1.6).Ready ||
            EvaluateAba(1.5, 1.1, 3, 9.0, 1.0, 1.2, 2.9, 3.1).Accepted)
            throw new InvalidOperationException(
                "AI maintenance feedback accepted incomplete/invalid evidence or rejected valid A/B/A.");

        return "AI_ABA_EVIDENCE_GUARD_SMOKE_OK stableCandidate=true " +
            "abaConfirmed=true missingRangeRejected=true invalidDataClosesTrial=true " +
            "referenceDriftRejected=true readOnly=true writes=0";
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
