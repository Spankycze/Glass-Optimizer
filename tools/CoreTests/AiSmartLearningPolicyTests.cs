using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiSmartLearningPolicyTests
{
    [Theory]
    [InlineData(AiWorkloadClass.Game, 5)]
    [InlineData(AiWorkloadClass.Interactive, 5)]
    [InlineData(AiWorkloadClass.Background, 8)]
    [InlineData(AiWorkloadClass.Idle, 10)]
    [InlineData(AiWorkloadClass.Unknown, 10)]
    public void LearningSamplesAreTimeSeparated(AiWorkloadClass workload, int seconds)
    {
        DateTime now = new(2026, 10, 4, 5, 0, 0, DateTimeKind.Utc);
        DateTime last = now.AddSeconds(-(seconds - 1));

        Assert.False(AiSmartLearningPolicy.IsSampleDue(last, workload, workload, now));
        Assert.True(AiSmartLearningPolicy.IsSampleDue(
            now.AddSeconds(-seconds), workload, workload, now));
    }

    [Fact]
    public void WorkloadChangeAllowsImmediateFreshSample()
    {
        DateTime now = DateTime.UtcNow;
        Assert.True(AiSmartLearningPolicy.IsSampleDue(
            now.AddSeconds(-1),
            AiWorkloadClass.Idle,
            AiWorkloadClass.Interactive,
            now));
    }

    [Fact]
    public void PersistenceRequiresDirtyModelAndUsesWorkloadCadence()
    {
        DateTime now = new(2026, 10, 4, 5, 0, 0, DateTimeKind.Utc);

        Assert.False(AiSmartLearningPolicy.IsPersistenceDue(
            false, DateTime.MinValue, AiWorkloadClass.Game, now));
        Assert.True(AiSmartLearningPolicy.IsPersistenceDue(
            true, DateTime.MinValue, AiWorkloadClass.Game, now));

        Assert.False(AiSmartLearningPolicy.IsPersistenceDue(
            true, now.AddMinutes(-4), AiWorkloadClass.Game, now));
        Assert.True(AiSmartLearningPolicy.IsPersistenceDue(
            true, now.AddMinutes(-5), AiWorkloadClass.Game, now));

        Assert.False(AiSmartLearningPolicy.IsPersistenceDue(
            true, now.AddSeconds(-59), AiWorkloadClass.Idle, now));
        Assert.True(AiSmartLearningPolicy.IsPersistenceDue(
            true, now.AddMinutes(-1), AiWorkloadClass.Idle, now));
    }
}
