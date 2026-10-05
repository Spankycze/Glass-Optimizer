using SpankyhoOptimizer.Services;

public sealed class RealtimeVoiceVadPolicyTests
{
    [Theory]
    [InlineData(8, 5)]
    [InlineData(24, 5)]
    [InlineData(25, 6)]
    [InlineData(72, 6)]
    [InlineData(73, 7)]
    [InlineData(180, 7)]
    public void Natural_pause_budget_protects_czech_sentences(int speechChunks, int expected)
    {
        Assert.Equal(expected, RealtimeVoiceVadPolicy.RequiredEndSilenceChunks(speechChunks));
    }

    [Theory]
    [InlineData(4, false, 5)]
    [InlineData(4, true, 0)]
    public void Clear_speech_resets_previous_pause(int currentSilence, bool speechResumed, int expected)
    {
        Assert.Equal(expected, RealtimeVoiceVadPolicy.NextSilenceCount(currentSilence, speechResumed));
    }
}
