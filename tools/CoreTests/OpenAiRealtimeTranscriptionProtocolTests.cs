using SpankyhoOptimizer.Services;

public sealed class OpenAiRealtimeTranscriptionProtocolTests
{
    [Fact]
    public void Session_update_uses_live_transcribe_low_delay_and_24khz_pcm()
    {
        string json = OpenAiRealtimeTranscriptionProtocol.CreateSessionUpdate(
            VoiceTranscriptionContext.Prompt,
            VoiceTranscriptionContext.Keywords);

        Assert.Contains("\"model\":\"gpt-live-transcribe\"", json);
        Assert.Contains("\"rate\":24000", json);
        Assert.Contains("\"delay\":\"low\"", json);
        Assert.Contains("\"turn_detection\":null", json);
    }

    [Fact]
    public void Parses_session_ack_and_error_events()
    {
        Assert.Equal("session.updated", OpenAiRealtimeTranscriptionProtocol.GetEventType(
            "{\"type\":\"session.updated\"}"));
        Assert.Equal("bad config", OpenAiRealtimeTranscriptionProtocol.TryParseErrorMessage(
            "{\"type\":\"error\",\"error\":{\"message\":\"bad config\"}}"));
    }

    [Fact]
    public void Parses_delta_and_completed_transcripts()
    {
        Assert.Equal("Ahoj", OpenAiRealtimeTranscriptionProtocol.TryParseTranscript(
            "{\"type\":\"conversation.item.input_audio_transcription.delta\",\"delta\":\"Ahoj\"}"));
        string completed = "{\"type\":\"conversation.item.input_audio_transcription.completed\",\"transcript\":\"Ahoj světe\"}";
        Assert.Equal("Ahoj světe", OpenAiRealtimeTranscriptionProtocol.TryParseTranscript(completed));
        Assert.True(OpenAiRealtimeTranscriptionProtocol.IsCompletedTranscriptEvent(completed));
    }
}
