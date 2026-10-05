using SpankyhoOptimizer.Services;

public sealed class GeminiPrimarySttPolicyTests
{
    [Fact]
    public void Primary_timeout_caps_pathological_stalls()
    {
        Assert.InRange(GeminiPrimarySttPolicy.PrimaryTimeout.TotalSeconds, 4, 7);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Timeout_is_eligible_for_fallback(bool timedOut, bool expected)
    {
        Assert.Equal(expected, GeminiPrimarySttPolicy.ShouldFallbackAfterTimeout(timedOut));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, false)]
    [InlineData(System.Net.HttpStatusCode.BadRequest, false)]
    public void Only_service_unavailable_uses_primary_model_fallback(
        System.Net.HttpStatusCode status,
        bool expected)
    {
        Assert.Equal(expected, GeminiPrimarySttPolicy.ShouldFallbackAfterStatus(status));
    }

    [Fact]
    public void Fallback_does_not_add_artificial_retry_delay()
    {
        Assert.Equal(TimeSpan.Zero, GeminiPrimarySttPolicy.FallbackDelay);
    }

    [Fact]
    public void Each_fallback_model_has_bounded_timeout()
    {
        Assert.InRange(GeminiPrimarySttPolicy.FallbackModelTimeout.TotalSeconds, 8, 10);
    }
}
