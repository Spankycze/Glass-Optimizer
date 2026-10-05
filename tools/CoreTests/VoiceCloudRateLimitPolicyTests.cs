using System.Net;
using System.Net.Http.Headers;
using SpankyhoOptimizer.Services;

public sealed class VoiceCloudRateLimitPolicyTests
{
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void Only_transient_model_specific_errors_allow_failover(
        HttpStatusCode status,
        bool expected)
    {
        Assert.Equal(expected, VoiceCloudRateLimitPolicy.ShouldTryNextModel(status));
    }

    [Fact]
    public void Quota_response_uses_longer_cooldown()
    {
        TimeSpan cooldown = VoiceCloudRateLimitPolicy.ResolveCooldown(
            """{"error":{"status":"RESOURCE_EXHAUSTED","message":"quota exceeded"}}""",
            null);

        Assert.Equal(TimeSpan.FromMinutes(10), cooldown);
    }

    [Theory]
    [InlineData("42s", 42)]
    [InlineData("42.5s", 42.5)]
    [InlineData("3600s", 3600)]
    public void GoogleRetryInfoHonorsServerDelay(string delay, double expectedSeconds)
    {
        string body = """{"error":{"status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"DELAY"}]}}""".Replace("DELAY", delay, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), VoiceCloudRateLimitPolicy.ResolveCooldown(body, null));
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("-1s")]
    [InlineData("0s")]
    [InlineData("NaNs")]
    [InlineData("1e4s")]
    public void InvalidGoogleRetryInfoKeepsQuotaCooldown(string delay)
    {
        string body = """{"error":{"status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"DELAY"}]}}""".Replace("DELAY", delay, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(10), VoiceCloudRateLimitPolicy.ResolveCooldown(body, null));
    }

    [Fact]
    public void LongerHeaderAndGoogleDelayAreBothRespected()
    {
        const string body = """{"error":{"details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"90s"}]}}""";
        Assert.Equal(TimeSpan.FromSeconds(90), VoiceCloudRateLimitPolicy.ResolveCooldown(body, TimeSpan.FromSeconds(45)));
        Assert.Equal(TimeSpan.FromSeconds(120), VoiceCloudRateLimitPolicy.ResolveCooldown(body, TimeSpan.FromSeconds(120)));
    }

    [Fact]
    public void UnrelatedRetryDelayDoesNotAlterQuotaCooldown()
    {
        const string body = """{"error":{"status":"RESOURCE_EXHAUSTED","retryDelay":"42s","details":[{"@type":"other.type","retryDelay":"42s"}]}}""";
        Assert.Equal(TimeSpan.FromMinutes(10), VoiceCloudRateLimitPolicy.ResolveCooldown(body, null));
    }

    [Fact]
    public void MalformedErrorDoesNotBreakCooldownResolution()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), VoiceCloudRateLimitPolicy.ResolveCooldown("{", null));
    }

    [Fact]
    public void Generic_429_uses_default_cooldown()
    {
        TimeSpan cooldown = VoiceCloudRateLimitPolicy.ResolveCooldown(
            """{"error":{"message":"try later"}}""",
            null);

        Assert.Equal(TimeSpan.FromMinutes(2), cooldown);
    }

    [Theory]
    [InlineData(2, 15)]
    [InlineData(90, 90)]
    [InlineData(7200, 7200)]
    public void Retry_after_is_honored_without_retrying_early(
        int requestedSeconds,
        int expectedSeconds)
    {
        TimeSpan cooldown = VoiceCloudRateLimitPolicy.ResolveCooldown(
            null,
            TimeSpan.FromSeconds(requestedSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), cooldown);
    }

    [Fact]
    public void Cooldown_state_is_stored_in_current_user_profile()
    {
        string local = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string state = Path.GetFullPath(VoiceCloudRateLimitStateService.StatePath);

        Assert.StartsWith(local, state, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stt_cooldown_state_is_stored_in_current_user_profile()
    {
        string local = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string state = Path.GetFullPath(GeminiSttRateLimitStateService.StatePath);

        Assert.StartsWith(local, state, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Api_key_fingerprint_is_stable_but_key_specific()
    {
        string first = VoiceCloudRateLimitStateService.Fingerprint("key-a");
        string again = VoiceCloudRateLimitStateService.Fingerprint("key-a");
        string second = VoiceCloudRateLimitStateService.Fingerprint("key-b");

        Assert.Equal(first, again);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("key-a", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_after_header_delta_is_resolved()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(75));

        TimeSpan? retry = SpankyConversationService.ResolveRetryAfter(
            response,
            DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.FromSeconds(75), retry);
    }
}
