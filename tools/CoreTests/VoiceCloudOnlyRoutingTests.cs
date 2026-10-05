using SpankyhoOptimizer.Services;

public sealed class VoiceCloudOnlyRoutingTests
{
    [Theory]
    [InlineData(true, true, VoiceSttRoute.OpenAiCloud)]
    [InlineData(true, false, VoiceSttRoute.OpenAiCloud)]
    [InlineData(false, true, VoiceSttRoute.GeminiCloud)]
    [InlineData(false, false, VoiceSttRoute.Unavailable)]
    public void Cloud_only_routing_never_selects_local_whisper(
        bool hasOpenAi,
        bool hasGemini,
        VoiceSttRoute expected)
    {
        Assert.Equal(expected, VoiceSttRoutingPolicy.Select(hasOpenAi, hasGemini));
    }
}
