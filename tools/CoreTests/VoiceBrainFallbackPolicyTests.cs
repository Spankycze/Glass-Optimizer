using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class VoiceBrainFallbackPolicyTests
{
    [Theory]
    [InlineData("ChatGPT plán teď není dostupný.")]
    [InlineData("Došel limit použití ChatGPT plánu pro připojené aplikace.")]
    [InlineData("OpenAI brain teď není dostupný.")]
    [InlineData("Došel bezplatný Gemini limit. Zkus to později.")]
    [InlineData("")]
    public void KnownProviderFailureRequestsFallback(string reply)
    {
        Assert.True(VoiceBrainFallbackPolicy.ShouldFallback(reply, streamedSpeech: false));
    }

    [Fact]
    public void NormalAnswerDoesNotRequestFallback()
    {
        Assert.False(VoiceBrainFallbackPolicy.ShouldFallback(
            "CPU má teď 48 stupňů.", streamedSpeech: false));
    }

    [Fact]
    public void NeverFallbackAfterSpeechAlreadyStarted()
    {
        Assert.False(VoiceBrainFallbackPolicy.ShouldFallback(
            "ChatGPT plán teď není dostupný.", streamedSpeech: true));
    }
}
