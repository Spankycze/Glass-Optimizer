using SpankyhoOptimizer.Services;

public sealed class HardwareTelemetryBridgeTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Registration_gate_requires_elevated_trusted_host(bool elevated, bool validHost, bool allowed)
    {
        Assert.Equal(allowed, HardwareTelemetryBridgeService.ShouldClaimRegistrationGate(elevated, validHost));
    }

    [Fact]
    public async Task Bridge_registration_parallel_start_allows_exactly_one_owner()
    {
        int accepted = 0;
        using var ready = new CountdownEvent(8);
        using var release = new ManualResetEventSlim(false);
        using var start = new ManualResetEventSlim(false);
        Task[] contenders = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            start.Wait();
            if (HardwareTelemetryBridgeService.TryEnterRegistration())
            {
                Interlocked.Increment(ref accepted);
                release.Wait();
                HardwareTelemetryBridgeService.ExitRegistration();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
            start.Set();
            SpinWait.SpinUntil(() => Volatile.Read(ref accepted) != 0, TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref accepted));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(contenders);
        }
        Assert.True(HardwareTelemetryBridgeService.TryEnterRegistration());
        HardwareTelemetryBridgeService.ExitRegistration();
    }

    [Fact]
    public void Bridge_registration_gate_releases_after_exception()
    {
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            Assert.True(HardwareTelemetryBridgeService.TryEnterRegistration());
            try { throw new InvalidOperationException("injected registration failure"); }
            finally { HardwareTelemetryBridgeService.ExitRegistration(); }
        }));
        Assert.True(HardwareTelemetryBridgeService.TryEnterRegistration());
        HardwareTelemetryBridgeService.ExitRegistration();
    }

    [Fact]
    public void Bridge_registration_single_flight_rejects_parallel_contenders()
    {
        Assert.True(HardwareTelemetryBridgeService.TryEnterRegistration());
        try
        {
            int accepted = 0;
            Parallel.For(0, 32, _ =>
            {
                if (HardwareTelemetryBridgeService.TryEnterRegistration())
                    Interlocked.Increment(ref accepted);
            });
            Assert.Equal(0, accepted);
        }
        finally { HardwareTelemetryBridgeService.ExitRegistration(); }
        Assert.True(HardwareTelemetryBridgeService.TryEnterRegistration());
        HardwareTelemetryBridgeService.ExitRegistration();
    }

    [Fact]
    public void Bridge_registration_single_flight_rejects_reentry_and_releases()
    {
        Assert.True(HardwareTelemetryBridgeService.TryEnterRegistration());
        try { Assert.False(HardwareTelemetryBridgeService.TryEnterRegistration()); }
        finally { HardwareTelemetryBridgeService.ExitRegistration(); }
        Assert.True(HardwareTelemetryBridgeService.TryEnterRegistration());
        HardwareTelemetryBridgeService.ExitRegistration();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Protected_bridge_task_start_requires_successful_read_only_query(
        bool querySucceeded, bool expected)
    {
        Assert.Equal(expected,
            HardwareTelemetryBridgeService.ShouldStartVerifiedTask(querySucceeded));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Protected_bridge_task_replacement_requires_verified_query_and_stop(
        bool querySucceeded, bool stopSucceeded, bool expected)
    {
        Assert.Equal(expected,
            HardwareTelemetryBridgeService.ShouldReplaceVerifiedTask(querySucceeded, stopSucceeded));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Query_policy_matches_registration_state(bool created, bool shouldQuery)
    {
        Assert.Equal(shouldQuery, HardwareTelemetryBridgeService.ShouldQueryBeforeBridgeStart(created));
    }

    [Theory]
    [InlineData(null, "version", false)]
    [InlineData("", "version", true)]
    [InlineData("previous", "version", true)]
    [InlineData("version", "version", false)]
    [InlineData("VERSION", "version", false)]
    public void Registration_replacement_policy_distinguishes_unreadable_missing_and_matching_stamp(
        string? current, string expected, bool shouldReplace)
    {
        Assert.Equal(shouldReplace,
            HardwareTelemetryBridgeService.ShouldAttemptRegistrationReplacement(current, expected));
    }

    [Fact]
    public void Registration_stamp_read_trims_value()
    {
        Assert.Equal("version", HardwareTelemetryBridgeService.TryReadRegistrationStamp(() => "  version  "));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registration_stamp_read_error_falls_back_to_empty(bool denied)
    {
        string? actual = HardwareTelemetryBridgeService.TryReadRegistrationStamp(() =>
        {
            if (denied) throw new UnauthorizedAccessException("test denied");
            throw new IOException("test io");
        });
        Assert.Null(actual);
    }

    [Fact]
    public void Registration_stamp_write_success_returns_true()
    {
        bool invoked = false;
        Assert.True(HardwareTelemetryBridgeService.TryWriteRegistrationStamp(() => invoked = true));
        Assert.True(invoked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registration_stamp_write_io_or_access_failure_does_not_throw(bool denied)
    {
        bool result = HardwareTelemetryBridgeService.TryWriteRegistrationStamp(() =>
        {
            if (denied) throw new UnauthorizedAccessException("test denied");
            throw new IOException("test disk error");
        });
        Assert.False(result);
    }

    [Fact]
    public void Task_xml_runs_as_system_highest_without_time_limit()
    {
        string xml = HardwareTelemetryBridgeService.BuildTaskXml(
            @"C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.RuntimeHost.exe");

        Assert.Contains("encoding=\"UTF-16\"", xml, StringComparison.Ordinal);
        Assert.Contains("S-1-5-18", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceAccount", xml, StringComparison.Ordinal);
        Assert.Contains("HighestAvailable", xml, StringComparison.Ordinal);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml, StringComparison.Ordinal);
        Assert.Contains("--hardware-telemetry-bridge", xml, StringComparison.Ordinal);
        Assert.Contains("MultipleInstancesPolicy>StopExisting", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Appdata_runtime_is_not_a_trusted_elevated_source()
    {
        string userRuntime = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpankyhoOptimizer",
            "RuntimeHost",
            "15.4.999",
            "SpankyhoOptimizer.RuntimeHost.exe");

        Assert.False(HardwareTelemetryBridgeService.IsTrustedSourcePath(userRuntime));
    }

    [Fact]
    public void Program_files_runtime_is_a_trusted_elevated_source()
    {
        string protectedRuntime = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Spankyho Optimizer",
            "SpankyhoOptimizer.RuntimeHost.exe");

        Assert.True(HardwareTelemetryBridgeService.IsTrustedSourcePath(protectedRuntime));
    }

    [Fact]
    public void Snapshot_is_stored_under_protected_program_files()
    {
        string programFiles = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string snapshot = Path.GetFullPath(HardwareTelemetryBridgeService.SnapshotPath);

        Assert.StartsWith(programFiles, snapshot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compatible_old_snapshot_is_not_current_version_readiness()
    {
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow, 1, "15.4.595", true, 45, "Core", "Ryzen",
            10, 2500, 45, HardwareTelemetryBridgeService.CurrentSchemaVersion);

        Assert.True(HardwareTelemetryBridgeService.IsSnapshotCompatible(
            snapshot, "15.4.596"));
        Assert.False(HardwareTelemetryBridgeService.IsCurrentVersionSnapshot(
            snapshot, "15.4.596"));
    }

    [Fact]
    public void Current_snapshot_satisfies_current_version_readiness()
    {
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow, 1, "15.4.596", true, 45, "Core", "Ryzen",
            10, 2500, 45, HardwareTelemetryBridgeService.CurrentSchemaVersion);

        Assert.True(HardwareTelemetryBridgeService.IsCurrentVersionSnapshot(
            snapshot, "15.4.596"));
    }

    [Fact]
    public void Legacy_594_snapshot_is_accepted_by_595()
    {
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow, 1, "15.4.594", true, 45, "Core", "Ryzen",
            10, 2500, 45, null);

        Assert.True(HardwareTelemetryBridgeService.IsSnapshotCompatible(
            snapshot, "15.4.595"));
    }

    [Fact]
    public void Legacy_snapshot_before_594_is_rejected()
    {
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow, 1, "15.4.593", true, 45, "Core", "Ryzen",
            10, 2500, 45, null);

        Assert.False(HardwareTelemetryBridgeService.IsSnapshotCompatible(
            snapshot, "15.4.595"));
    }

    [Fact]
    public void Legacy_594_snapshot_is_rejected_after_two_patch_gap()
    {
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow, 1, "15.4.594", true, 45, "Core", "Ryzen",
            10, 2500, 45, null);

        Assert.False(HardwareTelemetryBridgeService.IsSnapshotCompatible(
            snapshot, "15.4.597"));
    }

    [Fact]
    public void Schema_v1_snapshot_is_patch_version_independent()
    {
        var snapshot = new HardwareTelemetryBridgeSnapshot(
            DateTime.UtcNow, 1, "15.4.594", true, 45, "Core", "Ryzen",
            10, 2500, 45, HardwareTelemetryBridgeService.CurrentSchemaVersion);

        Assert.True(HardwareTelemetryBridgeService.IsSnapshotCompatible(
            snapshot, "15.4.999"));
    }

    [Fact]
    public void Task_xml_rejects_untrusted_command_path_even_if_it_needs_escaping()
    {
        Assert.Throws<ArgumentException>(() => HardwareTelemetryBridgeService.BuildTaskXml(
            @"C:\A&B\SpankyhoOptimizer.RuntimeHost.exe"));
    }
}
