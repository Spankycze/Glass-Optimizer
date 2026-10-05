namespace SpankyhoOptimizer.Services;

public enum VoiceSttRoute
{
    OpenAiCloud,
    GeminiCloud,
    Unavailable
}

public static class VoiceSttRoutingPolicy
{
    public static VoiceSttRoute Select(bool hasOpenAi, bool hasGemini) =>
        hasOpenAi
            ? VoiceSttRoute.OpenAiCloud
            : hasGemini
                ? VoiceSttRoute.GeminiCloud
                : VoiceSttRoute.Unavailable;
}
