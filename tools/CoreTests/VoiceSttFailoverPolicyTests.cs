using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class VoiceSttFailoverPolicyTests
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    public void RealtimeRequiresKeyRequestAndNoCooldown(
        bool hasKey,
        bool requested,
        bool cooling,
        bool expected)
    {
        Assert.Equal(expected, VoiceSttFailoverPolicy.ShouldTryOpenAiRealtime(
            hasKey, requested, cooling));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void SegmentSkipsCooldownWithoutThrowing(
        bool hasKey,
        bool cooling,
        bool expected)
    {
        Assert.Equal(expected, VoiceSttFailoverPolicy.ShouldTryOpenAiSegment(
            hasKey, cooling));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, false)]
    public void GeminiOnlyStopsWhenBothRoutesAreCooling(
        bool hasKey,
        bool primaryCooling,
        bool fallbackCooling,
        bool expected)
    {
        Assert.Equal(expected, VoiceSttFailoverPolicy.ShouldTryGemini(
            hasKey, primaryCooling, fallbackCooling));
    }
}
