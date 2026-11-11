using SpankyhoOptimizer.Services;
using Xunit;

namespace CoreTests;

public sealed class FanAgentRegistrationCadence1439Tests
{
    [Theory]
    [InlineData(1000L, 0L, true)]
    [InlineData(1000L, 900L, true)]
    [InlineData(1000L, 1000L, true)]
    [InlineData(1000L, 1001L, false)]
    [InlineData(300000L, 600000L, false)]
    [InlineData(600000L, 600000L, true)]
    public void RegistrationRunsOnlyAfterMonotonicDueTime(long now, long nextDue, bool expected)
    {
        Assert.Equal(expected, FanProfileService.StartupRegistrationDue(now, nextDue));
    }

    [Fact]
    public void RepeatedSaveWindowAvoidsExpensiveRegistrationWhileFailureCanRetryEarlier()
    {
        const long now = 100_000;
        long afterTaskSuccess = now + 10 * 60_000;
        long afterFallback = now + 60_000;
        // App startup just established Task Scheduler; rapid fan sliders must
        // not re-launch cleanup, COM queries or legacy schtasks.
        for (long at = now + 1; at < now + 20_000; at += 500)
            Assert.False(FanProfileService.StartupRegistrationDue(at, afterTaskSuccess));
        // A fallback can be retried after only one minute.
        Assert.True(FanProfileService.StartupRegistrationDue(afterFallback, afterFallback));
        Assert.False(FanProfileService.StartupRegistrationDue(now + 59_999, afterFallback));
        Assert.True(FanProfileService.StartupRegistrationDue(afterTaskSuccess, afterTaskSuccess));
    }
}
