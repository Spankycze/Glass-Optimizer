using SpankyhoOptimizer.Services;

public sealed class RuntimeHostStartupPolicyLoopTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Voice_runtime_log_is_only_for_real_agent(
        bool watchdog,
        bool watchdogLoop,
        bool expected)
    {
        Assert.Equal(
            expected,
            RuntimeHostStartupPolicy.ShouldLogVoiceRuntime(watchdog, watchdogLoop));
    }
}
