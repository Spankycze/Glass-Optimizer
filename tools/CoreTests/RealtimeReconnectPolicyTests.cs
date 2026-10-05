using SpankyhoOptimizer.Services;

public sealed class RealtimeReconnectPolicyTests
{
    [Theory]
    [InlineData(0, 250)]
    [InlineData(1, 500)]
    [InlineData(2, 1000)]
    [InlineData(3, 2000)]
    [InlineData(4, 2000)]
    public void Backoff_is_bounded(int attempt, int expectedMs)
    {
        Assert.Equal(expectedMs, RealtimeReconnectPolicy.GetDelayMs(attempt));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void Retries_are_limited(int attempt, bool expected)
    {
        Assert.Equal(expected, RealtimeReconnectPolicy.ShouldRetry(attempt));
    }
}
