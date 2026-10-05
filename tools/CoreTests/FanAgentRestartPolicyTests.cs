using SpankyhoOptimizer.Services;

public sealed class FanAgentRestartPolicyTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("15.4.562", true)]
    [InlineData("15.4.567|2026-10-03T08:00:00Z", true)]
    [InlineData("restart", true)]
    public void Any_non_empty_restart_request_is_accepted(string payload, bool expected)
    {
        Assert.Equal(expected, FanAgentRestartPolicy.ShouldRestart(payload));
    }
}
