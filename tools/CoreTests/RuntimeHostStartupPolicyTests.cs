using SpankyhoOptimizer.Services;

public sealed class RuntimeHostStartupPolicyTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Voice_runtime_log_is_only_for_agent_mode(bool watchdog, bool expected)
    {
        Assert.Equal(expected, RuntimeHostStartupPolicy.ShouldLogVoiceRuntime(watchdog));
    }

    [Fact]
    public void Hardware_bridge_does_not_log_as_voice_runtime()
    {
        Assert.False(RuntimeHostStartupPolicy.ShouldLogVoiceRuntime(
            watchdog: false,
            watchdogLoop: false,
            hardwareBridge: true));
    }

    [Theory]
    [InlineData(0, false, false, false, false)]
    [InlineData(0, true, false, false, false)]
    [InlineData(0, false, true, false, false)]
    [InlineData(0, false, false, true, true)]
    [InlineData(1, false, false, false, true)]
    [InlineData(1, true, false, false, true)]
    [InlineData(1, false, true, false, true)]
    public void Fan_runtime_is_user_session_only_but_bridge_may_run_in_session_zero(
        int sessionId,
        bool watchdog,
        bool watchdogLoop,
        bool hardwareBridge,
        bool expected)
    {
        Assert.Equal(
            expected,
            RuntimeHostStartupPolicy.ShouldRunInSession(
                sessionId,
                watchdog,
                watchdogLoop,
                hardwareBridge));
    }
}
