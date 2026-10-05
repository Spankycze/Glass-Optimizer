using SpankyhoOptimizer.Services;

public sealed class ChatGptModelCachePolicyTests
{
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(9, true, false)]
    [InlineData(10, true, true)]
    [InlineData(60, true, true)]
    public void Cached_model_is_returned_immediately_and_refreshes_when_stale(
        int ageMinutes,
        bool expectedUseCached,
        bool expectedRefresh)
    {
        var decision = ChatGptModelCachePolicy.Decide(
            hasCachedModel: true,
            TimeSpan.FromMinutes(ageMinutes));

        Assert.Equal(expectedUseCached, decision.UseCachedImmediately);
        Assert.Equal(expectedRefresh, decision.RefreshInBackground);
    }

    [Fact]
    public void Missing_cache_requires_blocking_fetch()
    {
        var decision = ChatGptModelCachePolicy.Decide(false, TimeSpan.Zero);
        Assert.False(decision.UseCachedImmediately);
        Assert.True(decision.FetchNow);
    }
}
