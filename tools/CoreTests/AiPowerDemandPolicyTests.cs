using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiPowerDemandPolicyTests
{
    [Fact]
    public void ThermalAnomalyAloneDoesNotRequestPerformance()
    {
        Assert.False(AiPowerDemandPolicy.HasPerformanceDemand(
            gameActive: false,
            loadPercent: 18,
            packagePowerW: 80,
            clockMHz: 3800,
            anomalyScore: 90));
    }

    [Fact]
    public void RealWorkOrGameRequestsPerformance()
    {
        Assert.True(AiPowerDemandPolicy.HasPerformanceDemand(false, 52, 45, 3200, 10));
        Assert.True(AiPowerDemandPolicy.HasPerformanceDemand(true, 8, 30, 2200, 0));
        Assert.True(AiPowerDemandPolicy.HasPerformanceDemand(false, 30, 80, 3600, 10));
    }

    [Fact]
    public void BackgroundWorkUsesHigherPerformanceThreshold()
    {
        Assert.False(AiPowerDemandPolicy.HasPerformanceDemand(
            false, 55, 70, 3700, 20, AiWorkloadClass.Background));
        Assert.True(AiPowerDemandPolicy.HasPerformanceDemand(
            false, 42, 60, 3400, 20, AiWorkloadClass.Interactive));
        Assert.True(AiPowerDemandPolicy.HasPerformanceDemand(
            true, 8, 30, 2200, 0, AiWorkloadClass.Game));
    }

    [Fact]
    public void LowModelConfidenceNeverBlocksMeasuredHighLoad()
    {
        Assert.True(AiPowerDemandPolicy.HasPerformanceDemand(
            false, 80, 40, 3000, 0, AiWorkloadClass.Interactive, confidence: 5));
        Assert.True(AiPowerDemandPolicy.HasPerformanceDemand(
            false, 45, 80, 3600, 0, AiWorkloadClass.Interactive, confidence: 5));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    [InlineData(101.0)]
    public void InvalidCpuTelemetryNeverSelectsEconomyPowerPlan(double load)
    {
        Assert.True(AiPowerDemandPolicy.HasPerformanceDemand(
            false, load, null, null, 0, AiWorkloadClass.Background));
    }

    [Fact]
    public void InvalidThermalEvidenceCannotTriggerGuard()
    {
        Assert.False(AiPowerDemandPolicy.ShouldThermalGuard(false, 80, double.NaN, 90));
        Assert.False(AiPowerDemandPolicy.ShouldThermalGuard(false, 80, 40, double.NaN));
    }

    [Fact]
    public void ThermalGuardNeedsConfidenceAndNonGameModerateLoad()
    {
        Assert.True(AiPowerDemandPolicy.ShouldThermalGuard(false, 80, 40, 80));
        Assert.False(AiPowerDemandPolicy.ShouldThermalGuard(true, 80, 40, 80));
        Assert.False(AiPowerDemandPolicy.ShouldThermalGuard(false, 80, 70, 80));
        Assert.False(AiPowerDemandPolicy.ShouldThermalGuard(false, 80, 40, 40));
    }
}
