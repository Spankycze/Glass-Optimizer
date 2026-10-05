using SpankyhoOptimizer.Services;

public sealed class GeminiLatestUtterancePolicyTests
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(5, 5, true)]
    public void Only_latest_generation_may_dispatch(int generation, int latestGeneration, bool expected)
    {
        Assert.Equal(expected, GeminiLatestUtterancePolicy.ShouldDispatch(generation, latestGeneration));
    }
}
