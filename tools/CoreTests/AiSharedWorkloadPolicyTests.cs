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
    public void FreshGameSignalOverridesTemporaryUnknownClassWithoutDisablingGameProtection()
    {
        DateTime now = new(2026, 10, 15, 14, 40, 0, DateTimeKind.Utc);
        var status = new AdaptivePowerRuntimeStatus(
            now.AddSeconds(-2), "Performance", 62.5, "", 65, 4300, 80,
            false, true, 12, 4, "Unknown", ForegroundProcess: "firefox",
            ForegroundProcessId: 100, ProcessCpuObservedUtc: now.AddSeconds(-2),
            ForegroundWindowHandle: 1234);
        Assert.Equal(AiWorkloadClass.Game,
            AiSharedWorkloadPolicy.Resolve(status, false, 62.5, now));
        Assert.False(AiSharedWorkloadPolicy.AllowsBackgroundAutomation(
            AiSharedWorkloadPolicy.Resolve(status, false, 62.5, now)));
    }

    [Fact]
    public void VerifiedSameSnapshotForegroundCanResolveUnknownToInteractive()
    {
        DateTime now = new(2026, 10, 15, 14, 40, 0, DateTimeKind.Utc);
        var status = new AdaptivePowerRuntimeStatus(
            now.AddSeconds(-2), "Balanced", 22, "", 55, 3500, 55,
            false, false, 20, 4, "Unknown", TopCpuProcessPercent: 6,
            ForegroundProcess: "firefox", ForegroundProcessId: 100,
            ProcessCpuObservedUtc: now.AddSeconds(-3),
            ForegroundWindowHandle: 1234);
        Assert.Equal(AiWorkloadClass.Interactive,
            AiSharedWorkloadPolicy.Resolve(status, false, 22, now));
        Assert.Equal(AiWorkloadClass.Unknown,
            AiSharedWorkloadPolicy.Resolve(status with
            { ProcessCpuObservedUtc = now.AddSeconds(-20) }, false, 22, now));
        Assert.Equal(AiWorkloadClass.Unknown,
            AiSharedWorkloadPolicy.Resolve(status with
            { ForegroundProcessId = 0 }, false, 22, now));
        Assert.Equal(AiWorkloadClass.Unknown,
            AiSharedWorkloadPolicy.Resolve(status with
            { ForegroundWindowHandle = 0 }, false, 22, now));
        Assert.Equal(AiWorkloadClass.Unknown,
            AiSharedWorkloadPolicy.Resolve(status with
            { TopCpuProcessPercent = double.NaN }, false, 22, now));
        Assert.Equal(AiWorkloadClass.Unknown,
            AiSharedWorkloadPolicy.Resolve(status with
            { ProcessCpuObservedUtc = now.AddSeconds(1) }, false, 22, now));
        Assert.Equal(AiWorkloadClass.Unknown,
            AiSharedWorkloadPolicy.Resolve(status, false, double.NaN, now));
    }

    [Fact]
    public void UnknownCannotBecomeGameFromStaleGameFlag()
    {
        DateTime now = new(2026, 10, 15, 14, 40, 0, DateTimeKind.Utc);
        var stale = new AdaptivePowerRuntimeStatus(
            now.AddMinutes(-3), "Performance", 22, "", 55, 3500, 55,
            false, true, 12, 4, "Unknown");
        Assert.Equal(AiWorkloadClass.Unknown,
            AiSharedWorkloadPolicy.Resolve(stale, false, 22, now));
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
