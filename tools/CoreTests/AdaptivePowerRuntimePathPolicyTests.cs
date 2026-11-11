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

    [Fact]
    public void Critical_status_key_tracks_exactly_six_safety_fields()
    {
        DateTime now = new(2026, 10, 15, 4, 0, 0, DateTimeKind.Utc);
        var original = new AdaptivePowerRuntimeStatus(
            now, "Balanced", 20, "busy", 52, 3600, 48, false, false, 20, 4,
            EppEvidence: "Stable");
        AdaptivePowerStatusKey key = AdaptivePowerStatusKey.From(original);
        Assert.Equal(key, AdaptivePowerStatusKey.From(original with
        {
            LoadPercent = 81, Blocker = "foreground changed", TopCpuProcess = "test"
        }));
        AdaptivePowerRuntimeStatus[] changes =
        [
            original with { Mode = "Performance" },
            original with { GameActive = true },
            original with { RealIdle = true },
            original with { Epp = 12 },
            original with { Boost = 3 },
            original with { EppEvidence = "Unstable" }
        ];
        foreach (var changed in changes)
            Assert.True(AdaptivePowerRuntimeService.ShouldWriteAdaptiveStatus(
                key, AdaptivePowerStatusKey.From(changed), now, now.AddSeconds(1)));
        Assert.True(AdaptivePowerRuntimeService.ShouldWriteAdaptiveStatus(
            null, key, now, now));
        Assert.False(AdaptivePowerRuntimeService.ShouldWriteAdaptiveStatus(
            key, key, now, now.AddSeconds(4)));
        Assert.True(AdaptivePowerRuntimeService.ShouldWriteAdaptiveStatus(
            key, key, now, now.AddSeconds(5)));
        Assert.True(AdaptivePowerRuntimeService.ShouldWriteAdaptiveStatus(
            key, key, now, now.AddSeconds(-1)));
        Assert.True(AdaptivePowerRuntimeService.IsStatusWriteRetryDue(
            now, now.AddMinutes(3)));
        Assert.False(AdaptivePowerRuntimeService.IsStatusWriteErrorLogDue(
            now, now.AddMinutes(-4)));
        Assert.True(AdaptivePowerRuntimeService.IsStatusWriteErrorLogDue(
            now, now.AddMinutes(-5)));
        Assert.True(AdaptivePowerRuntimeService.IsStatusWriteErrorLogDue(
            now, now.AddMinutes(3)));
    }

    [Fact]
    public void Critical_status_key_avoids_per_tick_string_allocations()
    {
        var status = new AdaptivePowerRuntimeStatus(
            DateTime.UtcNow, "Balanced", 20, "busy", 52, 3600, 48,
            false, false, 20, 4, EppEvidence: "Stable");
        const int iterations = 10000;
        int observed = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            string legacy = $"{status.Mode}|{status.GameActive}|{status.RealIdle}|{status.Epp}|{status.Boost}|{status.EppEvidence}";
            observed += legacy.Length;
        }
        long legacyBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        AdaptivePowerStatusKey expected = AdaptivePowerStatusKey.From(status);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            if (AdaptivePowerStatusKey.From(status) == expected) observed++;
        long typedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(observed > iterations);
        Assert.True(typedBytes <= 1024, $"Typed key allocated {typedBytes} bytes");
        Assert.True(legacyBytes >= iterations * 32,
            $"Legacy key allocated only {legacyBytes} bytes");
    }
}
