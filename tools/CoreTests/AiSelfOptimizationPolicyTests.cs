using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiSelfOptimizationPolicyTests
{
    [Fact]
    public void StableLowOverheadStaysCalm()
    {
        double score = AiSelfOptimizationPolicy.ComputePressureScore(
            cpuEwmaPercent: 0.25,
            workingSetEwmaMb: 45,
            threads: 18,
            handles: 350,
            ramGrowthMbPerMinute: 0.5,
            handleGrowthPerMinute: 2);

        Assert.True(AiSelfOptimizationPolicy.IsCalm(score, 30, 0.5, 2));
        Assert.False(AiSelfOptimizationPolicy.IsPressured(score, 30, 0.5, 2));
    }

    [Fact]
    public void SustainedRamGrowthTriggersPressureBeforeAbsoluteRamIsHuge()
    {
        double score = AiSelfOptimizationPolicy.ComputePressureScore(
            0.3, 55, 20, 400, 15, 5);

        Assert.True(AiSelfOptimizationPolicy.IsPressured(score, 35, 15, 5));
        Assert.Equal("Rostoucí režie", AiSelfOptimizationPolicy.State(score, 60, 15, 5));
    }

    [Fact]
    public void HandleGrowthTriggersPressure()
    {
        double score = AiSelfOptimizationPolicy.ComputePressureScore(
            0.2, 50, 20, 450, 1, 150);

        Assert.True(AiSelfOptimizationPolicy.IsPressured(score, 30, 1, 150));
        Assert.Equal("Rostoucí režie", AiSelfOptimizationPolicy.State(score, 60, 1, 150));
    }

    [Theory]
    [InlineData(AiWorkloadClass.Game, 60)]
    [InlineData(AiWorkloadClass.Interactive, 45)]
    [InlineData(AiWorkloadClass.Background, 30)]
    [InlineData(AiWorkloadClass.Idle, 30)]
    [InlineData(AiWorkloadClass.Unknown, 60)]
    public void SelfMonitoringCadenceFollowsWorkload(AiWorkloadClass workload, int seconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(seconds),
            AiSelfOptimizationPolicy.SamplingInterval(workload));
    }

    [Fact]
    public void WorkloadAppliesConservativeMaintenanceFloor()
    {
        Assert.Equal(180, AiSelfOptimizationPolicy.ApplyWorkloadFloor(45, AiWorkloadClass.Game));
        Assert.Equal(120, AiSelfOptimizationPolicy.ApplyWorkloadFloor(45, AiWorkloadClass.Interactive));
        Assert.Equal(60, AiSelfOptimizationPolicy.ApplyWorkloadFloor(45, AiWorkloadClass.Background));
        Assert.Equal(120, AiSelfOptimizationPolicy.ApplyWorkloadFloor(45, AiWorkloadClass.Unknown));
        Assert.Equal(45, AiSelfOptimizationPolicy.ApplyWorkloadFloor(45, AiWorkloadClass.Idle));
        Assert.Equal(240, AiSelfOptimizationPolicy.ApplyWorkloadFloor(240, AiWorkloadClass.Game));
    }
}
