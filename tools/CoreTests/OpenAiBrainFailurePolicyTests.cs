using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class OpenAiBrainFailurePolicyTests
{
    [Theory]
    [InlineData("HTTP 429 too many requests")]
    [InlineData("insufficient_quota")]
    [InlineData("rate limit exceeded")]
    public void RateLimitGetsLongCooldown(string error)
    {
        Assert.Equal(TimeSpan.FromMinutes(5),
            OpenAiBrainFailurePolicy.ResolveCooldown(error));
    }

    [Fact]
    public void InvalidKeyGetsLongerCooldown()
    {
        Assert.Equal(TimeSpan.FromMinutes(15),
            OpenAiBrainFailurePolicy.ResolveCooldown("401 Unauthorized invalid API key"));
    }

    [Fact]
    public void GenericTransientFailureGetsShortCooldown()
    {
        Assert.Equal(TimeSpan.FromSeconds(20),
            OpenAiBrainFailurePolicy.ResolveCooldown("connection reset"));
    }

    [Fact]
    public void PersistentBrainHealthIsScopedToApiKeyAndExpiry()
    {
        DateTime now = new(2026, 10, 4, 6, 0, 0, DateTimeKind.Utc);
        var state = new OpenAiBrainHealthState(
            now.AddMinutes(5),
            VoiceCloudRateLimitStateService.Fingerprint("key-a"),
            "HTTP 429");

        Assert.True(OpenAiBrainHealthStateService.IsActiveForKey(state, "key-a", now));
        Assert.False(OpenAiBrainHealthStateService.IsActiveForKey(state, "key-b", now));
        Assert.False(OpenAiBrainHealthStateService.IsActiveForKey(
            state with { UntilUtc = now.AddSeconds(-1) }, "key-a", now));
    }

    [Fact]
    public void PersistentBrainHealthLivesInCurrentUserVoiceFolder()
    {
        string local = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(OpenAiBrainHealthStateService.StatePath);

        Assert.StartsWith(local, path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            Path.Combine("SpankyhoOptimizer", "Voice"),
            path,
            StringComparison.OrdinalIgnoreCase);
    }
}
