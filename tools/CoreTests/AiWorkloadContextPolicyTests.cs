using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiWorkloadContextPolicyTests
{
    [Fact]
    public void GameAlwaysWins()
    {
        Assert.Equal(AiWorkloadClass.Game,
            AiWorkloadContextPolicy.Classify(
                true, false, "explorer", "svchost", 3, 8));
    }

    [Fact]
    public void ForegroundCpuIsInteractive()
    {
        Assert.Equal(AiWorkloadClass.Interactive,
            AiWorkloadContextPolicy.Classify(
                false, false, "firefox", "firefox", 18, 30));
    }

    [Fact]
    public void DifferentDominantCpuProcessIsBackground()
    {
        Assert.Equal(AiWorkloadClass.Background,
            AiWorkloadContextPolicy.Classify(
                false, false, "firefox", "MsMpEng", 20, 35));
    }

    [Fact]
    public void RealIdleOverridesForegroundWindow()
    {
        Assert.Equal(AiWorkloadClass.Idle,
            AiWorkloadContextPolicy.Classify(
                false, true, "explorer", "explorer", 1, 2));
    }
}
