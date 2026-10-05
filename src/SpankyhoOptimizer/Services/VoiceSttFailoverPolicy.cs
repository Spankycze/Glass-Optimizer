namespace SpankyhoOptimizer.Services;

public static class VoiceSttFailoverPolicy
{
    public static bool ShouldTryOpenAiRealtime(
        bool hasOpenAiKey,
        bool realtimeRequested,
        bool realtimeCooling) =>
        hasOpenAiKey && realtimeRequested && !realtimeCooling;

    public static bool ShouldTryOpenAiSegment(
        bool hasOpenAiKey,
        bool segmentCooling) =>
        hasOpenAiKey && !segmentCooling;

    public static bool ShouldTryGemini(
        bool hasGeminiKey,
        bool primaryCooling,
        bool fallbackCooling) =>
        hasGeminiKey && !(primaryCooling && fallbackCooling);
}
