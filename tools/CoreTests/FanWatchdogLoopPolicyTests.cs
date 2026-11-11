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
    public void Heartbeat_freshness_rejects_future_and_stale_timestamps()
    {
        var now = new System.DateTime(2026, 10, 18, 9, 0, 0, System.DateTimeKind.Utc);
        var ttl = System.TimeSpan.FromSeconds(30);
        Assert.True(FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(now, now, ttl));
        Assert.True(FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(now.AddSeconds(-30), now, ttl));
        Assert.False(FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(now.AddSeconds(-31), now, ttl));
        Assert.True(FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(now.AddSeconds(2), now, ttl));
        Assert.False(FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(now.AddSeconds(3), now, ttl));
        Assert.False(FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(default, now, ttl));
        Assert.False(FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(now, now, System.TimeSpan.Zero));
    }

    [Fact]
    public void Heartbeat_process_identity_rejects_reused_pid()
    {
        var stamp = new System.DateTime(2026, 10, 18, 9, 0, 0, System.DateTimeKind.Utc);
        Assert.True(FanWatchdogLoopPolicy.MatchesHeartbeatProcessStart(stamp.AddHours(-1), stamp));
        Assert.True(FanWatchdogLoopPolicy.MatchesHeartbeatProcessStart(stamp.AddSeconds(2), stamp));
        Assert.False(FanWatchdogLoopPolicy.MatchesHeartbeatProcessStart(stamp.AddSeconds(3), stamp));
        Assert.False(FanWatchdogLoopPolicy.MatchesHeartbeatProcessStart(default, stamp));
        Assert.False(FanWatchdogLoopPolicy.MatchesHeartbeatProcessStart(stamp, default));
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
