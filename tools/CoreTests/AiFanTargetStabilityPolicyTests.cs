using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiFanTargetStabilityPolicyTests
{
    [Fact]
    public void NormalIncreaseIsRateLimitedByConfidence()
    {
        int low = AiFanTargetStabilityPolicy.Stabilize(
            1700, 2200, 60, 64, 20, 30, AiWorkloadClass.Interactive);
        int high = AiFanTargetStabilityPolicy.Stabilize(
            1700, 2200, 60, 64, 20, 90, AiWorkloadClass.Interactive);

        Assert.Equal(1800, low);
        Assert.Equal(1940, high);
    }

    [Fact]
    public void EmergencyCoolingIncreaseBypassesRateLimit()
    {
        int target = AiFanTargetStabilityPolicy.Stabilize(
            1700, 2400, 83, 85, 92, 20, AiWorkloadClass.Game);

        Assert.Equal(LianLiSlInfinityService.MaximumRpm, target);
    }

    [Fact]
    public void WarmSystemDropsRpmSlowly()
    {
        int target = AiFanTargetStabilityPolicy.Stabilize(
            2200, 1620, 72, 75, 65, 90, AiWorkloadClass.Background);

        Assert.Equal(2025, target);
    }

    [Fact]
    public void IdleCoolSystemCanStepDownFaster()
    {
        int target = AiFanTargetStabilityPolicy.Stabilize(
            2200, 1620, 50, 54, 10, 90, AiWorkloadClass.Idle);

        Assert.Equal(1910, target);
    }
}
