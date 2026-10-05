using SpankyhoOptimizer.Services;

public sealed class FanAgentVersionPolicyTests
{
    [Theory]
    [InlineData("15.4.595", "15.4.595", false)]
    [InlineData("15.4.595", "15.4.594", true)]
    [InlineData("15.4.594", "15.4.595", false)]
    [InlineData("15.4.595", "", true)]
    [InlineData("15.4.595", "broken", true)]
    public void Update_policy_never_downgrades_newer_runtime(
        string current,
        string running,
        bool expected)
    {
        Assert.Equal(
            expected,
            FanAgentVersionPolicy.ShouldRequestUpdate(current, running));
    }
}
