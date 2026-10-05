using SpankyhoOptimizer.Services;

public sealed class VoiceTtsLatencyPolicyTests
{
    [Fact]
    public void Generation_timeout_caps_pathological_tts_stalls()
    {
        Assert.InRange(VoiceTtsLatencyPolicy.GenerationTimeout.TotalSeconds, 4, 6);
    }
}
