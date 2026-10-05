using SpankyhoOptimizer.Services;

public sealed class GeminiBargeInPolicyTests
{
    [Theory]
    [InlineData(false, 0.011, 0.011)]
    [InlineData(true, 0.011, 0.022)]
    [InlineData(true, 0.020, 0.040)]
    public void Tts_active_raises_start_threshold(bool ttsActive, double normal, double expected)
    {
        Assert.Equal(expected, GeminiBargeInPolicy.GetStartThreshold(normal, ttsActive), 3);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    public void Tts_active_requires_more_evidence(bool ttsActive, int expected)
    {
        Assert.Equal(expected, GeminiBargeInPolicy.RequiredEvidenceChunks(ttsActive));
    }
}
