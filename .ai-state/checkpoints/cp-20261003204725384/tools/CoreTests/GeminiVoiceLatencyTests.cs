using SpankyhoOptimizer.Services;

public sealed class GeminiVoiceLatencyTests
{
    [Theory]
    [InlineData(8, 7)]
    [InlineData(24, 7)]
    [InlineData(25, 7)]
    [InlineData(80, 7)]
    [InlineData(81, 9)]
    public void Gemini_end_silence_preserves_sentence_pauses(int speechChunks, int expected) =>
        Assert.Equal(expected, GeminiSegmentTranscriptionService.RequiredEndSilenceChunks(speechChunks));

    [Theory]
    [InlineData(4, 18, false)]
    [InlineData(6, 8, false)]
    [InlineData(7, 8, true)]
    [InlineData(8, 81, false)]
    [InlineData(9, 81, true)]
    public void Gemini_end_detection_waits_for_complete_utterance(int silenceChunks, int speechChunks, bool expected) =>
        Assert.Equal(expected, GeminiSegmentTranscriptionService.ShouldEndSpeech(silenceChunks, speechChunks));

    [Fact]
    public void Current_chunk_is_not_appended_twice_when_preroll_already_contains_it() =>
        Assert.False(GeminiSegmentTranscriptionService.ShouldAppendCurrentAfterPreRoll(currentIncludedInPreRoll: true));
}
