using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class SpankyVoiceAiServiceTests
{
    [Fact]
    public void GenericDiabloQuestionDoesNotTriggerFanPerformance()
    {
        SpankyVoiceDecision decision = SpankyVoiceAiService.Understand("Spanky co vis o Diablo Immortal");
        Assert.Equal(SpankyVoiceIntent.Unknown, decision.Intent);
    }

    [Fact]
    public void GenericModeQuestionDoesNotPretendToBeFanStatus()
    {
        SpankyVoiceDecision decision = SpankyVoiceAiService.Understand("Spanky jaky rezim ma Windows");
        Assert.Equal(SpankyVoiceIntent.Unknown, decision.Intent);
    }

    [Fact]
    public void ExplicitFanPerformanceCommandStillUsesFastPath()
    {
        SpankyVoiceDecision decision = SpankyVoiceAiService.Understand("Spanky dej ventilatory na maximum");
        Assert.Equal(SpankyVoiceIntent.FanPerformance, decision.Intent);
        Assert.True(decision.Confidence >= .9);
    }

    [Fact]
    public void ExplicitFanStatusStillUsesFastPath()
    {
        SpankyVoiceDecision decision = SpankyVoiceAiService.Understand("Spanky jaky rezim maji ventilatory");
        Assert.Equal(SpankyVoiceIntent.FanStatus, decision.Intent);
    }
}
