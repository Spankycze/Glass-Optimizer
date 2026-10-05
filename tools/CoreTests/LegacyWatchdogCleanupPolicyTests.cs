using SpankyhoOptimizer.Services;

public sealed class LegacyWatchdogCleanupPolicyTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Cleanup_runs_only_when_process_is_elevated(bool elevated, bool expected)
    {
        Assert.Equal(expected, LegacyWatchdogCleanupPolicy.ShouldAttempt(elevated));
    }
}
