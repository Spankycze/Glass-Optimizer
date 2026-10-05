using SpankyhoOptimizer.Services;

public sealed class GeminiSpeechStartPolicyTests
{
    [Theory]
    [InlineData(0, true, 1, false)]
    [InlineData(1, true, 2, true)]
    [InlineData(1, false, 0, false)]
    [InlineData(2, false, 0, false)]
    public void Speech_requires_two_consecutive_evidence_chunks(
        int currentEvidence,
        bool aboveThreshold,
        int expectedEvidence,
        bool expectedStart)
    {
        var result = GeminiSpeechStartPolicy.Next(currentEvidence, aboveThreshold);
        Assert.Equal(expectedEvidence, result.EvidenceChunks);
        Assert.Equal(expectedStart, result.ShouldStart);
    }

    [Fact]
    public void Custom_evidence_budget_supports_tts_barge_in_guard()
    {
        Assert.False(GeminiSpeechStartPolicy.Next(1, true, 3).ShouldStart);
        Assert.True(GeminiSpeechStartPolicy.Next(2, true, 3).ShouldStart);
    }
}
