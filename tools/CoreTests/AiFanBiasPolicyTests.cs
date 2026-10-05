using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiFanBiasPolicyTests
{
    [Fact]
    public void LowConfidenceNeverSuppressesDirectThermalSafetyBias()
    {
        int lowConfidence = AiFanBiasPolicy.Compute(
            temp: 79,
            predictedTemp10s: 82,
            power: 40,
            load: 10,
            trend: 1.1,
            anomaly: 10,
            gamePhase: "None",
            confidence: 10,
            workload: AiWorkloadClass.Background);

        int highConfidence = AiFanBiasPolicy.Compute(
            79, 82, 40, 10, 1.1, 10, "None", 100, AiWorkloadClass.Background);

        Assert.True(lowConfidence >= 400);
        Assert.Equal(highConfidence, lowConfidence);
    }

    [Fact]
    public void BackgroundLoadBiasIsLowerThanInteractiveLoadBias()
    {
        int background = AiFanBiasPolicy.Compute(
            55, 55, 45, 75, 0, 0, "None", 100, AiWorkloadClass.Background);
        int interactive = AiFanBiasPolicy.Compute(
            55, 55, 45, 75, 0, 0, "None", 100, AiWorkloadClass.Interactive);

        Assert.True(background < interactive);
    }

    [Fact]
    public void IdleLoadDoesNotCreateLoadFanBias()
    {
        int idle = AiFanBiasPolicy.Compute(
            55, 55, 45, 80, 0, 0, "None", 100, AiWorkloadClass.Idle);

        Assert.Equal(0, idle);
    }
}
