using SpankyhoOptimizer.Services;

public sealed class VoiceOpenAiModelTests
{
    [Fact]
    public void Uses_configured_transcription_model()
    {
        var settings = new OpenAiVoiceSettings("OpenAI", "gpt-6-sol", "custom-transcribe", "", true);
        Assert.Equal("custom-transcribe", OpenAiSegmentTranscriptionService.ResolveTranscribeModel(settings));
    }

    [Fact]
    public void Live_model_falls_back_to_file_transcribe_for_segment_requests()
    {
        var settings = new OpenAiVoiceSettings("OpenAI", "gpt-6-sol", "gpt-live-transcribe", "", true);
        Assert.Equal("gpt-transcribe", OpenAiSegmentTranscriptionService.ResolveTranscribeModel(settings));
    }
}
