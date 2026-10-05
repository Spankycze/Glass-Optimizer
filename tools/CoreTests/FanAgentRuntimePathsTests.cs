using SpankyhoOptimizer.Services;

public sealed class FanAgentRuntimePathsTests
{
    [Fact]
    public void Runtime_state_is_scoped_to_local_app_data()
    {
        var paths = FanAgentRuntimePaths.Create(@"C:\Users\test\AppData\Local");

        Assert.Equal(
            @"C:\Users\test\AppData\Local\SpankyhoOptimizer\FanAgent\fan-agent-heartbeat.txt",
            paths.HeartbeatPath);
        Assert.Equal(
            @"C:\Users\test\AppData\Local\SpankyhoOptimizer\FanAgent\fan-agent-version.txt",
            paths.VersionPath);
        Assert.Equal(
            @"C:\Users\test\AppData\Local\SpankyhoOptimizer\FanAgent\fan-agent-restart.request",
            paths.RestartRequestPath);
        Assert.Equal(
            @"C:\Users\test\AppData\Local\SpankyhoOptimizer\FanAgent\fan-agent-health.json",
            paths.HealthPath);
    }
}
