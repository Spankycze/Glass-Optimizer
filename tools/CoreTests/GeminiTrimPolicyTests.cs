using SpankyhoOptimizer.Services;

public sealed class GeminiTrimPolicyTests
{
    [Theory]
    [InlineData(8, 3, 7)]
    [InlineData(25, 4, 23)]
    [InlineData(73, 5, 70)]
    public void Retains_speech_and_quiet_consonant_tail(int total, int trailing, int expected)
    {
        Assert.Equal(expected, GeminiTrimPolicy.GetRetainedChunkCount(total, trailing));
    }
}
