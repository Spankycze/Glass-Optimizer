using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiMaintenanceFeedbackPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CorruptCompletedCandidateEndsPartialReferenceImmediately(int referenceCount)
    {
        var result = AiMaintenanceFeedbackPolicy.EvaluateAba(
            1.5, 1.1, referenceCount, referenceCount * 1.5,
            candidateMin: 0.1, candidateMax: 2.1);
        Assert.True(result.Ready);
        Assert.False(result.Accepted);
        Assert.Contains("Candidate feedback", result.Reason);
    }

    [Theory]
    [InlineData(0.0, null)]
    [InlineData(null, 0.0)]
    [InlineData(0.0, 0.0)]
    public void ZeroSampleExtremaCannotSupportCandidateOrReference(double? min, double? max)
    {
        var candidate = AiMaintenanceFeedbackPolicy.Evaluate(1.5, 0, 0, min, max);
        var reference = AiMaintenanceFeedbackPolicy.EvaluateAba(1.5, 1.1, 0, 0,
            referenceMin: min, referenceMax: max);
        Assert.True(candidate.Ready);
        Assert.False(candidate.Accepted);
        Assert.True(reference.Ready);
        Assert.False(reference.Accepted);
    }

    [Theory]
    [InlineData(1, 1.0, 1.0, null)]
    [InlineData(1, 1.0, null, 1.0)]
    [InlineData(2, 2.0, 0.8, null)]
    [InlineData(2, 2.0, null, 1.2)]
    [InlineData(2, 2.0, 0.8, 1.2)]
    public void ValidPartialLegacyRangeStillCollectsCandidateAndReference(
        int count, double total, double? minimum, double? maximum)
    {
        var candidate = AiMaintenanceFeedbackPolicy.Evaluate(1.5, count, total, minimum, maximum);
        var reference = AiMaintenanceFeedbackPolicy.EvaluateAba(1.5, 1.1, count, total,
            candidateMin: 1.0, candidateMax: 1.2,
            referenceMin: minimum, referenceMax: maximum);
        Assert.False(candidate.Ready);
        Assert.False(candidate.Accepted);
        Assert.False(reference.Ready);
        Assert.False(reference.Accepted);
    }

    [Theory]
    [InlineData(1, 1.0, 0.8, null)]
    [InlineData(1, 1.0, null, 1.2)]
    [InlineData(2, 2.0, 1.5, null)]
    [InlineData(2, 2.0, null, 0.5)]
    public void OneSidedImpossiblePartialExtremaCloseBothPhases(
        int count, double total, double? minimum, double? maximum)
    {
        var candidate = AiMaintenanceFeedbackPolicy.Evaluate(1.5, count, total, minimum, maximum);
        var reference = AiMaintenanceFeedbackPolicy.EvaluateAba(1.5, 1.1, count, total,
            candidateMin: 1.0, candidateMax: 1.2,
            referenceMin: minimum, referenceMax: maximum);
        Assert.True(candidate.Ready);
        Assert.False(candidate.Accepted);
        Assert.True(reference.Ready);
        Assert.False(reference.Accepted);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void InvalidCandidateCountClosesTrial(int count)
    {
        var verdict = AiMaintenanceFeedbackPolicy.Evaluate(1.5, count, 0);
        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void InvalidReferenceCountClosesTrial(int count)
    {
        var verdict = AiMaintenanceFeedbackPolicy.EvaluateAba(1.5, 1.1, count, 0);
        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
    }

    [Theory]
    [InlineData(0, 2.0)]
    [InlineData(1, double.NaN)]
    [InlineData(2, double.PositiveInfinity)]
    [InlineData(2, -0.5)]
    public void CorruptPartialCandidateTotalEndsTrial(int samples, double total)
    {
        var verdict = AiMaintenanceFeedbackPolicy.Evaluate(1.5, samples, total);
        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
    }

    [Theory]
    [InlineData(0, 2.0)]
    [InlineData(1, double.NaN)]
    [InlineData(2, double.NegativeInfinity)]
    [InlineData(2, -0.5)]
    public void CorruptPartialReferenceTotalEndsTrial(int samples, double total)
    {
        var verdict = AiMaintenanceFeedbackPolicy.EvaluateAba(1.5, 1.1, samples, total);
        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
    }

    [Theory]
    [InlineData(1, 1000.0)]
    [InlineData(2, 1000.0)]
    public void ImpossiblePartialCandidateCpuTotalClosesTrial(int samples, double total)
    {
        var result = AiMaintenanceFeedbackPolicy.Evaluate(1.5, samples, total);
        Assert.True(result.Ready);
        Assert.False(result.Accepted);
    }

    [Theory]
    [InlineData(1, 1000.0)]
    [InlineData(2, 1000.0)]
    public void ImpossiblePartialReferenceCpuTotalClosesTrial(int samples, double total)
    {
        var result = AiMaintenanceFeedbackPolicy.EvaluateAba(1.5, 1.1, samples, total);
        Assert.True(result.Ready);
        Assert.False(result.Accepted);
    }

    [Theory]
    [InlineData(1, 0.8, 0.8, 0.9)]
    [InlineData(2, 2.2, 1.0, 1.1)]
    [InlineData(2, 1.9, -0.1, 2.0)]
    public void ImpossiblePartialCandidateRangeClosesTrial(int count, double total, double min, double max)
    {
        var verdict = AiMaintenanceFeedbackPolicy.Evaluate(1.5, count, total, min, max);
        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
    }

    [Theory]
    [InlineData(0, null, 0.0)]
    [InlineData(0, 0.0, null)]
    [InlineData(0, 0.0, 0.0)]
    public void ZeroSampleExtremaCannotRemainPending(int count, double? min, double? max)
    {
        var candidate = AiMaintenanceFeedbackPolicy.Evaluate(1.5, count, 0, min, max);
        var reference = AiMaintenanceFeedbackPolicy.EvaluateAba(
            1.5, 1.1, count, 0, candidateMin: 1.0, candidateMax: 1.2,
            referenceMin: min, referenceMax: max);
        Assert.True(candidate.Ready);
        Assert.False(candidate.Accepted);
        Assert.True(reference.Ready);
        Assert.False(reference.Accepted);
    }

    [Theory]
    [InlineData(1, 1.2, 0.8, null)]
    [InlineData(1, 1.2, null, 1.1)]
    [InlineData(2, 1.0, 0.8, null)]
    [InlineData(2, 3.0, null, 1.0)]
    [InlineData(2, 1.4, double.NaN, null)]
    public void InvalidSingleBoundEndsCandidateAndReferenceEarly(int count, double total, double? min, double? max)
    {
        var candidate = AiMaintenanceFeedbackPolicy.Evaluate(1.5, count, total, min, max);
        var reference = AiMaintenanceFeedbackPolicy.EvaluateAba(1.5, 1.1, count, total, referenceMin: min, referenceMax: max);
        Assert.True(candidate.Ready);
        Assert.False(candidate.Accepted);
        Assert.True(reference.Ready);
        Assert.False(reference.Accepted);
    }

    [Theory]
    [InlineData(1, 0.8, 0.8, 0.9)]
    [InlineData(2, 2.2, 1.0, 1.1)]
    [InlineData(2, 1.9, -0.1, 2.0)]
    public void ImpossiblePartialReferenceRangeClosesTrial(int count, double total, double min, double max)
    {
        var verdict = AiMaintenanceFeedbackPolicy.EvaluateAba(1.5, 1.1, count, total, referenceMin: min, referenceMax: max);
        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.4)]
    public void InvalidBaselineClosesPartialCandidateTrial(double baseline)
    {
        var result = AiMaintenanceFeedbackPolicy.Evaluate(baseline, 1, 0.9);
        Assert.True(result.Ready);
        Assert.False(result.Accepted);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.4)]
    public void InvalidBaselineClosesPartialReferenceTrial(double baseline)
    {
        var result = AiMaintenanceFeedbackPolicy.EvaluateAba(baseline, 1.1, 1, 1.5);
        Assert.True(result.Ready);
        Assert.False(result.Accepted);
    }

    [Fact]
    public void ZeroReferenceCannotProduceNonfiniteImprovement()
    {
        var verdict = AiMaintenanceFeedbackPolicy.EvaluateAba(
            0.8, 0, 3, 0, 0, 0, 0, 0);
        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
        Assert.True(double.IsFinite(verdict.ImprovementPercent));
    }

    [Fact]
    public void RequiresEnoughCandidateSamples()
    {
        AiMaintenanceFeedbackVerdict verdict =
            AiMaintenanceFeedbackPolicy.Evaluate(1.5, 2, 2.0);
        Assert.False(verdict.Ready);
    }

    [Fact]
    public void KeepsOnlyMeaningfulRepeatableGain()
    {
        AiMaintenanceFeedbackVerdict good =
            AiMaintenanceFeedbackPolicy.Evaluate(1.5, 3, 3.3, 1.0, 1.2); // avg 1.1
        Assert.True(good.Ready);
        Assert.True(good.Accepted);
        Assert.True(good.ImprovementPercent >= 10);

        AiMaintenanceFeedbackVerdict noise =
            AiMaintenanceFeedbackPolicy.Evaluate(1.5, 3, 4.35, 1.4, 1.5); // avg 1.45
        Assert.True(noise.Ready);
        Assert.False(noise.Accepted);
    }

    [Fact]
    public void RejectsWeakBaseline()
    {
        AiMaintenanceFeedbackVerdict verdict =
            AiMaintenanceFeedbackPolicy.Evaluate(0.4, 3, 0.6, 0.1, 0.3);
        // Invalid baseline closes a finished trial as inconclusive, not pending.
        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
    }

    [Fact]
    public void AbaAcceptsOnlyWhenReferenceRecoversAndCandidateStillWins()
    {
        AiMaintenanceFeedbackVerdict verdict =
            AiMaintenanceFeedbackPolicy.EvaluateAba(
                baselineLoadPercent: 1.5,
                candidateLoadPercent: 1.1,
                referenceSamples: 3,
                referenceLoadTotal: 4.5,
                candidateMin: 1.0, candidateMax: 1.2,
                referenceMin: 1.4, referenceMax: 1.6);

        Assert.True(verdict.Ready);
        Assert.True(verdict.Accepted);
        Assert.Contains("A/B/A confirmed", verdict.Reason);
    }

    [Fact]
    public void AbaRejectsApparentMeanWinWhenMedianShowsNoRepeatableGain()
    {
        // The lower Candidate B outlier improves the mean, but the typical
        // observed CPU load does not beat restored Reference A by 10%.
        var verdict = AiMaintenanceFeedbackPolicy.EvaluateAba(
            baselineLoadPercent: 1.5,
            candidateLoadPercent: 1.3,
            referenceSamples: 3,
            referenceLoadTotal: 4.5,
            candidateMin: 1.0, candidateMax: 1.45,
            referenceMin: 1.4, referenceMax: 1.6);
        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
        Assert.Contains("repeatably", verdict.Reason);
    }

    [Fact]
    public void AbaAcceptsRepeatableMedianGainDespiteCandidateHighOutlier()
    {
        // A modest high Candidate B outlier must not suppress a verified
        // repeatable win when mean and median both meet the same threshold.
        var verdict = AiMaintenanceFeedbackPolicy.EvaluateAba(
            baselineLoadPercent: 1.5,
            candidateLoadPercent: 1.1,
            referenceSamples: 3,
            referenceLoadTotal: 4.5,
            candidateMin: 1.0, candidateMax: 1.25,
            referenceMin: 1.4, referenceMax: 1.6);
        Assert.True(verdict.Ready);
        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void AbaRejectsMeanGainWhenReferenceHighOutlierMasksWeakTypicalGain()
    {
        // A high Reference A outlier raises the reference mean enough to
        // appear beneficial, but the middle samples do not show the
        // required repeatable 0.15pp improvement.
        var verdict = AiMaintenanceFeedbackPolicy.EvaluateAba(
            baselineLoadPercent: 1.5,
            candidateLoadPercent: 1.29,
            referenceSamples: 3,
            referenceLoadTotal: 4.5,
            candidateMin: 1.2, candidateMax: 1.39,
            referenceMin: 1.3, referenceMax: 1.8);
        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
        Assert.Contains("repeatably", verdict.Reason);
    }

    [Theory]
    [InlineData(1.00, 1.50)]
    [InlineData(1.05, 1.45)]
    [InlineData(0.98, 1.51)]
    public void AbaRejectsMeanWinWithoutRequiredMedianBenefit(
        double candidateMin, double candidateMax)
    {
        // Reference samples 1.40/1.50/1.60; candidate mean 1.30
        // passes the average absolute/relative comparison, but the
        // reconstructed candidate median (1.40-1.41) improves by just
        // 0.09-0.10pp, below the required 0.15pp repeatable threshold.
        var verdict = AiMaintenanceFeedbackPolicy.EvaluateAba(
            baselineLoadPercent: 1.5,
            candidateLoadPercent: 1.30,
            referenceSamples: 3,
            referenceLoadTotal: 4.5,
            candidateMin: candidateMin,
            candidateMax: candidateMax,
            referenceMin: 1.4,
            referenceMax: 1.6);
        Assert.True(verdict.Ready);
        // These valid three-sample ranges produce an insufficient
        // typical-sample benefit even though the mean appears improved.
        Assert.False(verdict.Accepted);
    }

    [Fact]
    public void AbaRejectsWhenReferenceDoesNotRecover()
    {
        AiMaintenanceFeedbackVerdict verdict =
            AiMaintenanceFeedbackPolicy.EvaluateAba(
                baselineLoadPercent: 1.5,
                candidateLoadPercent: 1.1,
                referenceSamples: 3,
                referenceLoadTotal: 2.7,
                candidateMin: 1.0, candidateMax: 1.2,
                referenceMin: 0.8, referenceMax: 1.0);

        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
        Assert.Contains("did not recover", verdict.Reason);
    }

    [Fact]
    public void AbaRejectsWhenCandidateDoesNotBeatRestoredReferenceEnough()
    {
        AiMaintenanceFeedbackVerdict verdict =
            AiMaintenanceFeedbackPolicy.EvaluateAba(
                baselineLoadPercent: 1.5,
                candidateLoadPercent: 1.2,
                referenceSamples: 3,
                referenceLoadTotal: 3.9,
                candidateMin: 1.1, candidateMax: 1.3,
                referenceMin: 1.2, referenceMax: 1.4);

        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
        Assert.Contains("did not beat", verdict.Reason);
    }

    [Fact]
    public void CandidateWithGoodAverageButWideSpreadIsRejected()
    {
        AiMaintenanceFeedbackVerdict verdict = AiMaintenanceFeedbackPolicy.Evaluate(
            baselineLoadPercent: 1.5,
            candidateSamples: 3,
            candidateLoadTotal: 3.0,
            candidateMin: 0.3,
            candidateMax: 1.7);

        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
        Assert.Contains("noisy", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AbaRejectsNoisyReferenceEvenWhenAverageRecovers()
    {
        AiMaintenanceFeedbackVerdict verdict = AiMaintenanceFeedbackPolicy.EvaluateAba(
            baselineLoadPercent: 1.5,
            candidateLoadPercent: 1.0,
            referenceSamples: 3,
            referenceLoadTotal: 4.5,
            candidateMin: 0.9,
            candidateMax: 1.1,
            referenceMin: 0.5,
            referenceMax: 2.5);

        Assert.True(verdict.Ready);
        Assert.False(verdict.Accepted);
        Assert.Contains("Reference feedback is noisy", verdict.Reason);
    }
}
