using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiPowerTransitionPolicyTests
{
    [Fact]
    public void GameAndUrgentMeasuredLoadEnterImmediately()
    {
        Assert.Equal(3, AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Game, 10, 10, 30));
        Assert.Equal(3, AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Interactive, 20, 80, 60));
        Assert.Equal(3, AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Background, 20, 40, 110));
    }

    [Fact]
    public void NormalInteractiveDemandNeedsConfirmation()
    {
        Assert.Equal(2, AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Interactive, 80, 45, 60));
        Assert.Equal(1, AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Interactive, 30, 45, 60));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidCpuMeasurementCannotDelaySignaledPerformanceDemand(double load)
    {
        Assert.Equal(3, AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Background, 80, load, null));
        Assert.Equal(0, AiPowerTransitionPolicy.PerformanceVoteGain(
            false, AiWorkloadClass.Background, 80, load, null));
    }

    [Fact]
    public void NonFiniteConfidenceDoesNotGrantInteractiveBoost()
    {
        Assert.Equal(1, AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Interactive, double.NaN, 45, null));
    }

    [Fact]
    public void NonInteractiveDemandAddsOnlyOneVote()
    {
        Assert.Equal(1, AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Background, 90, 70, 80));
        Assert.Equal(1, AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Unknown, 90, 50, 80));
        Assert.Equal(0, AiPowerTransitionPolicy.PerformanceVoteGain(
            false, AiWorkloadClass.Interactive, 100, 90, 120));
    }
}
