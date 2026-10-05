namespace SpankyhoOptimizer.Services;

public static class VoiceConversationWarmupPolicy
{
    public static bool ShouldWarmGemini(bool chatGptConnected, bool hasGeminiKey) =>
        !chatGptConnected && hasGeminiKey;
}
