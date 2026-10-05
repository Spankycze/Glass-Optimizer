using SpankyhoOptimizer.Services;

public sealed class AdaptivePowerRuntimePathPolicyTests
{
    [Fact]
    public void Runtime_status_is_written_to_current_user_profile()
    {
        string local = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string status = Path.GetFullPath(AdaptivePowerRuntimeService.StatusPath);

        Assert.StartsWith(local, status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            Path.Combine("SpankyhoOptimizer", "FanAgent"),
            status,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_status_remains_read_compatible()
    {
        Assert.Contains(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AdaptivePowerRuntimePathPolicy.LegacyPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_write_failure_has_retry_backoff()
    {
        DateTime now = DateTime.UtcNow;

        Assert.False(AdaptivePowerRuntimeService.IsStatusWriteRetryDue(
            now, now.AddSeconds(-10)));
        Assert.True(AdaptivePowerRuntimeService.IsStatusWriteRetryDue(
            now, now.AddSeconds(-31)));
    }

    [Fact]
    public void Shared_power_status_must_be_recent_and_not_from_future()
    {
        DateTime now = new(2026, 10, 4, 5, 0, 0, DateTimeKind.Utc);
        AdaptivePowerRuntimeStatus Status(DateTime updated) => new(
            updated, "Balanced", 10, "", 55, 3000, 45, false, false, 20, 4, "Interactive");

        Assert.True(AdaptivePowerRuntimeService.IsStatusFresh(Status(now.AddSeconds(-5)), now));
        Assert.True(AdaptivePowerRuntimeService.IsStatusFresh(Status(now.AddSeconds(-20)), now));
        Assert.False(AdaptivePowerRuntimeService.IsStatusFresh(Status(now.AddSeconds(-21)), now));
        Assert.False(AdaptivePowerRuntimeService.IsStatusFresh(Status(now.AddSeconds(6)), now));
        Assert.False(AdaptivePowerRuntimeService.IsStatusFresh(null, now));
    }
}
