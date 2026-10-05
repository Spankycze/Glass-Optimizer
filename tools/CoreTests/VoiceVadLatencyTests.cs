using SpankyhoOptimizer.Services;

public sealed class VoiceVadLatencyTests
{
    [Theory]
    [InlineData(8, 3)]
    [InlineData(24, 3)]
    [InlineData(25, 4)]
    [InlineData(72, 4)]
    [InlineData(73, 5)]
    [InlineData(180, 5)]
    public void End_silence_budget_is_latency_optimized(int speechChunks, int expected)
    {
        Assert.Equal(expected, VoiceVadPolicy.RequiredEndSilenceChunks(speechChunks));
    }
}
