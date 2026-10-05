using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiSharedWorkloadPolicyTests
{
    [Fact]
    public void FreshSharedStatusWins()
    {
        DateTime now = new(2026, 10, 4, 5, 0, 0, DateTimeKind.Utc);
        var status = new AdaptivePowerRuntimeStatus(
            now.AddSeconds(-5), "Balanced", 25, "", 55, 3400, 55,
            false, false, 20, 4, "Interactive");

        Assert.Equal(
            AiWorkloadClass.Interactive,
            AiSharedWorkloadPolicy.Resolve(status, false, 25, now));
    }

    [Fact]
    public void StaleStatusFallsBackOnlyToProvenIdle()
    {
        DateTime now = new(2026, 10, 4, 5, 0, 0, DateTimeKind.Utc);
        var stale = new AdaptivePowerRuntimeStatus(
            now.AddMinutes(-2), "Performance", 80, "", 70, 4200, 90,
            false, true, 12, 4, "Game");

        Assert.Equal(
            AiWorkloadClass.Unknown,
            AiSharedWorkloadPolicy.Resolve(stale, false, 30, now));
        Assert.Equal(
            AiWorkloadClass.Idle,
            AiSharedWorkloadPolicy.Resolve(stale, true, 2, now));
    }

    [Fact]
    public void OnlyIdleAndBackgroundAllowBackgroundAutomation()
    {
        Assert.True(AiSharedWorkloadPolicy.AllowsBackgroundAutomation(AiWorkloadClass.Idle));
        Assert.True(AiSharedWorkloadPolicy.AllowsBackgroundAutomation(AiWorkloadClass.Background));
        Assert.False(AiSharedWorkloadPolicy.AllowsBackgroundAutomation(AiWorkloadClass.Interactive));
        Assert.False(AiSharedWorkloadPolicy.AllowsBackgroundAutomation(AiWorkloadClass.Game));
        Assert.False(AiSharedWorkloadPolicy.AllowsBackgroundAutomation(AiWorkloadClass.Unknown));
    }
}
