using SpankyhoOptimizer.Services;

public sealed class FanWatchdogLoopPolicyTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Restarts_agent_only_when_heartbeat_is_stale(bool heartbeatFresh, bool expected)
    {
        Assert.Equal(expected, FanWatchdogLoopPolicy.ShouldRestartAgent(heartbeatFresh));
    }

    [Fact]
    public void Poll_interval_is_lightweight()
    {
        Assert.InRange(FanWatchdogLoopPolicy.PollInterval.TotalSeconds, 10, 30);
    }

    [Fact]
    public void Startup_grace_prevents_agent_start_race()
    {
        Assert.InRange(FanWatchdogLoopPolicy.StartupGrace.TotalSeconds, 2, 8);
    }
}
