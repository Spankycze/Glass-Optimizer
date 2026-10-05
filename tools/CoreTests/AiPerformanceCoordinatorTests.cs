using SpankyhoOptimizer.Services;
namespace CoreTests;
[Collection("AI performance experiments")]
public sealed class AiPerformanceCoordinatorTests
{
    [Fact]
    public void TrialExcludesAnotherTunerAndMaintenanceUntilOwnerReleases()
    {
        Assert.True(AiPerformanceExperimentCoordinator.TryAcquire("system-tweaks"));
        try
        {
            Assert.True(AiPerformanceExperimentCoordinator.IsActive);
            Assert.False(AiPerformanceExperimentCoordinator.TryAcquire("network"));
            Assert.False(AiPerformanceExperimentCoordinator.TryAcquire("maintenance"));
            AiPerformanceExperimentCoordinator.Release("network");
            Assert.True(AiPerformanceExperimentCoordinator.IsActive);
            Assert.True(AiPerformanceExperimentCoordinator.TryAcquire("system-tweaks"));
        }
        finally { AiPerformanceExperimentCoordinator.Release("system-tweaks"); }
        Assert.False(AiPerformanceExperimentCoordinator.IsActive);
        Assert.True(AiPerformanceExperimentCoordinator.TryAcquire("maintenance"));
        try { Assert.False(AiPerformanceExperimentCoordinator.TryAcquire("network")); }
        finally { AiPerformanceExperimentCoordinator.Release("maintenance"); }
        Assert.True(AiPerformanceExperimentCoordinator.TryAcquire("network"));
        AiPerformanceExperimentCoordinator.Release("network");
        Assert.False(AiPerformanceExperimentCoordinator.IsActive);
    }

    [Fact]
    public void AbandonedLeaseEventuallyExpires()
    {
        DateTime lastTouch = new(2026, 10, 3, 20, 0, 0, DateTimeKind.Utc);
        Assert.False(AiPerformanceExperimentCoordinator.IsLeaseExpired(
            lastTouch,
            lastTouch.AddMinutes(10)));
        Assert.True(AiPerformanceExperimentCoordinator.IsLeaseExpired(
            lastTouch,
            lastTouch.Add(AiPerformanceExperimentCoordinator.LeaseTimeout).AddSeconds(1)));
    }
}
