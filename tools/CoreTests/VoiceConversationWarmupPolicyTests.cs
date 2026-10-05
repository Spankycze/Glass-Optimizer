using SpankyhoOptimizer.Services;

public sealed class VoiceConversationWarmupPolicyTests
{
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void Gemini_conversation_warmup_runs_only_without_chatgpt_plan(
        bool chatGptConnected,
        bool hasGeminiKey,
        bool expected)
    {
        Assert.Equal(
            expected,
            VoiceConversationWarmupPolicy.ShouldWarmGemini(
                chatGptConnected,
                hasGeminiKey));
    }
}
