using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiMaintenanceFeedbackPolicyTests
{
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
            AiMaintenanceFeedbackPolicy.Evaluate(1.5, 3, 3.3); // avg 1.1
        Assert.True(good.Ready);
        Assert.True(good.Accepted);
        Assert.True(good.ImprovementPercent >= 10);

        AiMaintenanceFeedbackVerdict noise =
            AiMaintenanceFeedbackPolicy.Evaluate(1.5, 3, 4.35); // avg 1.45
        Assert.True(noise.Ready);
        Assert.False(noise.Accepted);
    }

    [Fact]
    public void RejectsWeakBaseline()
    {
        AiMaintenanceFeedbackVerdict verdict =
            AiMaintenanceFeedbackPolicy.Evaluate(0.4, 3, 0.6);
        Assert.False(verdict.Ready);
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
                referenceLoadTotal: 4.5);

        Assert.True(verdict.Ready);
        Assert.True(verdict.Accepted);
        Assert.Contains("A/B/A confirmed", verdict.Reason);
    }

    [Fact]
    public void AbaRejectsWhenReferenceDoesNotRecover()
    {
        AiMaintenanceFeedbackVerdict verdict =
            AiMaintenanceFeedbackPolicy.EvaluateAba(
                baselineLoadPercent: 1.5,
                candidateLoadPercent: 1.1,
                referenceSamples: 3,
                referenceLoadTotal: 2.7);

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
                referenceLoadTotal: 3.9);

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
