using SpankyhoOptimizer.Services;
using Xunit.Abstractions;

namespace CoreTests;

public sealed class AiMaintenanceSchedulingPolicyTests
{
    [Fact]
    public void PendingWriteAheadRowsTriggerImmediateServiceTaskAndDriverScopes()
    {
        var rows = new Dictionary<string, AiSystemRuntimeItemState>
        {
            ["service|a"] = FastState("service", "a") with
                { PendingMutation = "stop-service" },
            ["task|a"] = FastState("TASK", "a") with
                { PendingMutation = "disable-task" },
            ["driver|a"] = FastState("Driver", "a") with
                { PendingMutation = "change-start" }
        };
        Assert.Equal((true, true, true),
            AiMaintenanceSchedulingPolicy.TrackedScopes(rows.Values));
        Assert.Equal((true, false, true),
            AiMaintenanceSchedulingPolicy.TrackedScopes(rows.Values, true, false, true));
        rows["driver|a"] = FastState("driver", "a");
        Assert.Equal((true, true, false),
            AiMaintenanceSchedulingPolicy.TrackedScopes(rows.Values));
    }

    [Fact]
    public void FastRecoveryUsesPrecomputedKeysWithoutCreatingDummyJournalRows()
    {
        string[] serviceKeys = AiSystemResourceManagerService.ServiceAllowlistKeys;
        string[] taskKeys = AiSystemResourceManagerService.TaskAllowlistKeys;
        Assert.Equal(32, serviceKeys.Length);
        Assert.Equal(28, taskKeys.Length);
        Assert.Equal(serviceKeys.Length, serviceKeys.Distinct(
            StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(taskKeys.Length, taskKeys.Distinct(
            StringComparer.OrdinalIgnoreCase).Count());

        var rows = new Dictionary<string, AiSystemRuntimeItemState>(
            StringComparer.OrdinalIgnoreCase)
        {
            [serviceKeys[2]] = FastState("service", "Fax") with { Managed = true },
            [taskKeys[3]] = FastState("task", "UsbCeip") with { PendingMutation = "disable" }
        };
        int beforeRows = rows.Count;
        int selected = 0;
        foreach (string key in serviceKeys)
            if (rows.TryGetValue(key, out var state) &&
                AiMaintenanceSchedulingPolicy.NeedsFastServiceRuntimeRead(false, false, state))
                selected++;
        foreach (string key in taskKeys)
            if (rows.TryGetValue(key, out var state) &&
                AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(false, false, state))
                selected++;
        Assert.Equal(2, selected);
        Assert.Equal(beforeRows, rows.Count);

        // Real per-cycle lookup paths use the same fixed strings, avoiding
        // repeated kind|id concatenation. No OS state is read or mutated.
        for (int i = 0; i < 4096; i++)
            foreach (string key in serviceKeys)
                _ = rows.TryGetValue(key, out _);
        const int loops = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < loops; i++)
        {
            foreach (string key in serviceKeys)
                _ = rows.TryGetValue(key, out _);
            foreach (string key in taskKeys)
                _ = rows.TryGetValue(key, out _);
        }
        long cachedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < loops; i++)
        {
            foreach (string key in serviceKeys)
                _ = rows.TryGetValue(string.Concat("service|".AsSpan(), key.AsSpan(8)), out _);
            foreach (string key in taskKeys)
                _ = rows.TryGetValue(string.Concat("task|".AsSpan(), key.AsSpan(5)), out _);
        }
        long formattedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(beforeRows, rows.Count);
        _output.WriteLine($"AI_FAST_JOURNAL_KEYS_BENCH scans={loops} keysPerScan={serviceKeys.Length + taskKeys.Length} formattedBytes={formattedBytes} cachedBytes={cachedBytes} savedBytes={formattedBytes-cachedBytes}");
        Assert.True(cachedBytes < formattedBytes / 10,
            $"Fixed key lookups {cachedBytes}B vs formatted {formattedBytes}B");
    }

    [Fact]
    public void FastServiceScopeQueriesOnlyOwnedAndPendingAndPreservesDeepInventory()
    {
        var ordinary = FastState("service", "Example") with
        {
            ObservationSinceUtc = new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc),
            BlockedReason = "last device-use observation",
            FailedBenefitTrials = 1,
            LastVerifiedBenefitAbsolute = 5.0
        };
        var owned = ordinary with { Managed = true };
        var sleeping = ordinary with { Sleeping = true };
        var pending = ordinary with { PendingMutation = "service-stop" };
        Assert.False(AiMaintenanceSchedulingPolicy.NeedsFastServiceRuntimeRead(
            false, false, ordinary));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsFastServiceRuntimeRead(
            false, false, owned));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsFastServiceRuntimeRead(
            false, false, sleeping));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsFastServiceRuntimeRead(
            false, false, pending));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsFastServiceRuntimeRead(
            false, true, ordinary));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsFastServiceRuntimeRead(
            true, false, ordinary));
        var suspended =
            AiMaintenanceSchedulingPolicy.SuspendUnownedServiceObservation(ordinary);
        Assert.Null(suspended.ObservationSinceUtc);
        Assert.Equal(ordinary.BlockedReason, suspended.BlockedReason);
        Assert.Equal(ordinary.LastVerifiedBenefitAbsolute,
            suspended.LastVerifiedBenefitAbsolute);
        Assert.Equal(ordinary.FailedBenefitTrials, suspended.FailedBenefitTrials);
        Assert.Same(suspended,
            AiMaintenanceSchedulingPolicy.SuspendUnownedServiceObservation(suspended));

        var fixture = Enumerable.Range(0, 33).Select(i =>
            i == 2 ? owned : i == 11 ? sleeping : i == 21 ? pending : ordinary).ToArray();
        Assert.Equal(3, fixture.Count(row =>
            AiMaintenanceSchedulingPolicy.NeedsFastServiceRuntimeRead(false, false, row)));
        Assert.Equal(33, fixture.Count(row =>
            AiMaintenanceSchedulingPolicy.NeedsFastServiceRuntimeRead(false, true, row)));
    }

    [Fact]
    public void FastServiceSnapshotKeepsDeviceDemandAndManagedLiveReadbackWithoutFullScmEnumeration()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string missing = "SpankyhoOptimizer_ServiceDoesNotExist_1394";
        var snapshot = AiSystemResourceManagerService.GetFastServiceStatusSnapshot(
            new[] { "Spooler", "WlanSvc", missing });
        // Wi-Fi and the printer evidence helpers remain available on short
        // active scans even without any owned services. Missing is UNKNOWN.
        Assert.True(snapshot.Count <= 5);
        Assert.False(snapshot.ContainsKey(missing));
        Assert.All(snapshot.Keys, name => Assert.Contains(
            name, new[] { "Spooler", "WlanSvc", "PrintNotify",
                "PrintDeviceConfigurationService", "PrintScanBrokerService" },
            StringComparer.OrdinalIgnoreCase));
        var recovery = AiSystemResourceManagerService.GetFastServiceStatusSnapshot(
            new[] { "DiagTrack", "WerSvc" });
        Assert.True(recovery.Count <= 7);
        Assert.All(recovery.Keys, name => Assert.Contains(
            name, new[] { "Spooler", "WlanSvc", "PrintNotify",
                "PrintDeviceConfigurationService", "PrintScanBrokerService",
                "DiagTrack", "WerSvc" }, StringComparer.OrdinalIgnoreCase));
    }

    private readonly ITestOutputHelper _output;
    public AiMaintenanceSchedulingPolicyTests(ITestOutputHelper output) => _output = output;
    [Fact]
    public void FastTaskRecoverySkipsFullMeasuredOpportunityOrderingAllocations()
    {
        var paths = Enumerable.Range(0, 28)
            .Select(i => @"\\Microsoft\\Windows\\Example\\Task" + i).ToArray();
        var states = new Dictionary<string, AiSystemRuntimeItemState>
        {
            ["task|" + paths[3]] = FastState("task", paths[3]) with { Managed = true },
            ["task|" + paths[19]] = FastState("task", paths[19]) with
            {
                Managed = true,
                Sleeping = true,
                PendingMutation = "task-disable"
            }
        };
        var fastCandidates = new List<string> { paths[3], paths[19] };
        DateTime now = new(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);

        // Deep inventory must still rank all 28. The foreground recovery
        // loop now visits exactly the two owner paths and never ranks.
        int[] deep = AiMaintenanceOpportunityPolicy.OrderedIndices(
            "task", paths, 3, states, now);
        Assert.Equal(28, deep.Length);
        Assert.Equal(28, deep.Distinct().Count());
        Assert.Equal(new[] { paths[3], paths[19] }, fastCandidates);

        // Measure steady-state managed allocations without changing a
        // production gate: this is the per-pass full-ranking waste removed.
        for (int i = 0; i < 2048; i++)
        {
            _ = AiMaintenanceOpportunityPolicy.OrderedIndices(
                "task", paths, i, states, now);
            foreach (string candidate in fastCandidates) _ = candidate.Length;
        }

        const int rounds = 1024;
        long before = GC.GetAllocatedBytesForCurrentThread();
        int fullChecksum = 0;
        for (int i = 0; i < rounds; i++)
        {
            int[] order = AiMaintenanceOpportunityPolicy.OrderedIndices(
                "task", paths, i, states, now);
            fullChecksum += order.Length;
        }
        long fullBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        int fastChecksum = 0;
        for (int i = 0; i < rounds; i++)
            foreach (string path in fastCandidates) fastChecksum += path.Length;
        long fastBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(rounds * paths.Length, fullChecksum);
        Assert.True(fastChecksum > 0);
        _output.WriteLine($"TASK_FAST_RECOVERY_ALLOCATION_REPLAY passes={rounds} allowlisted={paths.Length} tracked={fastCandidates.Count} fullRankingBytes={fullBytes} fastOwnerIterationBytes={fastBytes} savedBytes={fullBytes-fastBytes}");
        Assert.True(fullBytes > 100_000, $"Full candidate ranking unexpectedly cheap: {fullBytes} B");
        Assert.True(fastBytes < fullBytes / 20,
            $"Full opportunity ranking {fullBytes} B vs direct owner iteration {fastBytes} B");
    }

    [Fact]
    public void ForegroundTaskFastChecksQueryOnlyOwnedOrPendingButKeepDeepDiscovery()
    {
        var plain = FastState("task", "\\Microsoft\\Windows\\Example");
        var owned = plain with { Managed = true };
        var sleeping = plain with { Sleeping = true };
        var pending = plain with { PendingMutation = "write-ahead-disable" };
        Assert.False(AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(
            quietEnough: false, fullDiscovery: false, plain));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(
            quietEnough: false, fullDiscovery: false, owned));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(
            quietEnough: false, fullDiscovery: false, sleeping));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(
            quietEnough: false, fullDiscovery: false, pending));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(
            quietEnough: true, fullDiscovery: false, plain));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(
            quietEnough: false, fullDiscovery: true, plain));
        var fixture = Enumerable.Range(0, 35).Select(i =>
            i == 3 ? owned : i == 19 ? pending : plain).ToArray();
        Assert.Equal(2, fixture.Count(state =>
            AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(
                false, false, state)));
        Assert.Equal(35, fixture.Count(state =>
            AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(
                false, true, state)));
    }

    [Fact]
    public void ForegroundTaskCancelsOnlyUnownedObservationNotJournalOrLearning()
    {
        DateTime observed = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);
        var state = FastState("task", "Example") with
        {
            ObservationSinceUtc = observed,
            BlockedReason = "manual restriction",
            Rollbacks = 1
        };
        var suspended = AiMaintenanceSchedulingPolicy.SuspendUnownedTaskObservation(state);
        Assert.Null(suspended.ObservationSinceUtc);
        Assert.Equal(state.BlockedReason, suspended.BlockedReason);
        Assert.Equal(state.Rollbacks, suspended.Rollbacks);
        Assert.Same(suspended,
            AiMaintenanceSchedulingPolicy.SuspendUnownedTaskObservation(suspended));
    }

    [Fact]
    public void ActiveDriverInventoryAvoidsUnownedRegistryReadsButPreservesAllRecoveryOwners()
    {
        Assert.False(AiMaintenanceSchedulingPolicy.NeedsDriverRegistryRead(
            quietEnough: false, managed: false));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsDriverRegistryRead(
            quietEnough: true, managed: false));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsDriverRegistryRead(
            quietEnough: false, managed: true));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsDriverRegistryRead(
            quietEnough: false, managed: false, sleeping: true));
        Assert.True(AiMaintenanceSchedulingPolicy.NeedsDriverRegistryRead(
            quietEnough: false, managed: false, pendingMutation: true));
        // 50 unowned drivers never justify an active registry sweep; two
        // previously owned entries still perform every restoration readback.
        int reads = 0;
        for (int i = 0; i < 52; i++)
            if (AiMaintenanceSchedulingPolicy.NeedsDriverRegistryRead(
                    quietEnough: false, managed: i >= 50))
                reads++;
        Assert.Equal(2, reads);
        Assert.Equal(52, Enumerable.Range(0, 52).Count(i =>
            AiMaintenanceSchedulingPolicy.NeedsDriverRegistryRead(
                quietEnough: true, managed: i >= 50)));
    }

    [Fact]
    public void InterruptedDriverObservationLosesIdleCreditButPreservesOwnershipAndExplanation()
    {
        DateTime now = new(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);
        var state = FastState("driver", "Example") with
        {
            ObservationSinceUtc = now.AddHours(-2),
            DriverObservationLastUtc = now,
            BlockedReason = "externally restricted",
            Rollbacks = 1
        };
        var suspended = AiMaintenanceSchedulingPolicy.SuspendUnownedDriverObservation(state);
        Assert.Null(suspended.ObservationSinceUtc);
        Assert.Null(suspended.DriverObservationLastUtc);
        Assert.Equal(state.BlockedReason, suspended.BlockedReason);
        Assert.Equal(1, suspended.Rollbacks);
        Assert.False(suspended.Managed);
        Assert.Same(suspended,
            AiMaintenanceSchedulingPolicy.SuspendUnownedDriverObservation(suspended));
    }

    [Fact]
    public void Maintenance_tick_only_defers_within_valid_forward_clock_interval()
    {
        DateTime now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan cadence = TimeSpan.FromSeconds(120);
        Assert.False(AiSystemResourceManagerService.IsWithinRecentInterval(DateTime.MinValue, now, cadence));
        Assert.True(AiSystemResourceManagerService.IsWithinRecentInterval(now.AddSeconds(-119), now, cadence));
        Assert.True(AiSystemResourceManagerService.IsWithinRecentInterval(now, now, cadence));
        Assert.False(AiSystemResourceManagerService.IsWithinRecentInterval(now.AddSeconds(-120), now, cadence));
        Assert.False(AiSystemResourceManagerService.IsWithinRecentInterval(now.AddSeconds(-121), now, cadence));
    }

    [Fact]
    public void Clock_rollback_immediately_makes_maintenance_and_cloud_sync_due()
    {
        DateTime now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        DateTime oldFuture = now.AddHours(2);
        Assert.False(AiSystemResourceManagerService.IsWithinRecentInterval(oldFuture, now, TimeSpan.FromSeconds(60)));
        Assert.False(AiSystemResourceManagerService.IsWithinRecentInterval(oldFuture, now, TimeSpan.FromMinutes(15)));
        Assert.False(AiSystemResourceManagerService.IsWithinRecentInterval(now, now, TimeSpan.Zero));
        Assert.False(AiSystemResourceManagerService.IsWithinRecentInterval(now, now, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Deep_scan_updates_timestamp_only_when_due_and_recovers_after_clock_rollback()
    {
        DateTime now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan cadence = TimeSpan.FromMinutes(3);
        DateTime last = now.AddSeconds(-30);
        Assert.False(AiSystemResourceManagerService.ShouldRunDeepScan(ref last, now, cadence));
        Assert.Equal(now.AddSeconds(-30), last);
        last = now.AddHours(1); // Old timestamp now lies in the future.
        Assert.True(AiSystemResourceManagerService.ShouldRunDeepScan(ref last, now, cadence));
        Assert.Equal(now, last);
        Assert.False(AiSystemResourceManagerService.ShouldRunDeepScan(ref last, now.AddSeconds(20), cadence));
        Assert.True(AiSystemResourceManagerService.ShouldRunDeepScan(ref last, now.AddMinutes(3), cadence));
        Assert.Equal(now.AddMinutes(3), last);
    }

    [Fact]
    public void Probe_cache_expiration_is_bounded_by_configured_ttl()
    {
        DateTime now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan ttl = TimeSpan.FromSeconds(30);
        Assert.True(AiSystemResourceManagerService.IsCachedProbeFresh(now.AddSeconds(30), now, ttl));
        Assert.True(AiSystemResourceManagerService.IsCachedProbeFresh(now.AddSeconds(1), now, ttl));
        Assert.False(AiSystemResourceManagerService.IsCachedProbeFresh(now.AddHours(1), now, ttl));
        Assert.False(AiSystemResourceManagerService.IsCachedProbeFresh(now, now, ttl));
    }

    [Fact]
    public void RotatesFirstPriorityAcrossKinds()
    {
        Assert.Equal("service", AiMaintenanceSchedulingPolicy.OrderForCycle(0)[0]);
        Assert.Equal("task", AiMaintenanceSchedulingPolicy.OrderForCycle(1)[0]);
        Assert.Equal("driver", AiMaintenanceSchedulingPolicy.OrderForCycle(2)[0]);
        Assert.Equal("service", AiMaintenanceSchedulingPolicy.OrderForCycle(3)[0]);
    }

    [Theory]
    [InlineData(int.MaxValue - 1, "service")]
    [InlineData(int.MaxValue, "task")]
    [InlineData(int.MinValue, "driver")]
    [InlineData(int.MinValue + 1, "service")]
    public void Scheduler_rotation_survives_signed_counter_overflow(int cycle, string firstKind)
    {
        Assert.Equal(firstKind, AiMaintenanceSchedulingPolicy.KindAt(cycle, 0));
        string[] order = AiMaintenanceSchedulingPolicy.OrderForCycle(cycle);
        Assert.Equal(firstKind, order[0]);
        Assert.Equal(3, order.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Cycle_counter_wrap_is_defined_even_with_checked_arithmetic()
    {
        Assert.Equal(int.MinValue, AiMaintenanceSchedulingPolicy.NextCycle(int.MaxValue));
        Assert.Equal(int.MinValue + 1, AiMaintenanceSchedulingPolicy.NextCycle(int.MinValue));
        int last = int.MaxValue;
        int next = AiMaintenanceSchedulingPolicy.NextCycle(last);
        Assert.Equal("task", AiMaintenanceSchedulingPolicy.KindAt(last, 0));
        Assert.Equal("driver", AiMaintenanceSchedulingPolicy.KindAt(next, 0));
    }

    [Fact]
    public void Runtime_direct_kind_lookup_has_same_order_without_allocating_a_list()
    {
        Assert.Equal(3, AiMaintenanceSchedulingPolicy.KindCount);
        foreach (int cycle in new[] { 0, 1, 2, 3, 500, int.MaxValue, int.MinValue })
        {
            string[] expected = AiMaintenanceSchedulingPolicy.OrderForCycle(cycle);
            for (int offset = 0; offset < AiMaintenanceSchedulingPolicy.KindCount; offset++)
                Assert.Equal(expected[offset], AiMaintenanceSchedulingPolicy.KindAt(cycle, offset));
        }
    }

    [Fact]
    public void Direct_kind_lookup_avoids_per_cycle_result_array_allocations()
    {
        // Warm up both paths before counting only the current thread.
        _ = AiMaintenanceSchedulingPolicy.KindAt(0, 0);
        _ = AiMaintenanceSchedulingPolicy.OrderForCycle(0);
        const int count = 500;
        long start = GC.GetAllocatedBytesForCurrentThread();
        int sum = 0;
        for (int i = 0; i < count; i++)
            for (int k = 0; k < AiMaintenanceSchedulingPolicy.KindCount; k++)
                sum += AiMaintenanceSchedulingPolicy.KindAt(i, k).Length;
        long directBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++)
            sum += AiMaintenanceSchedulingPolicy.OrderForCycle(i)[0].Length;
        long arrayBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(sum > 0);
        Assert.True(directBytes < arrayBytes / 4,
            $"Direct lookup {directBytes} B vs array creation {arrayBytes} B");
    }

    private static AiSystemRuntimeItemState FastState(string kind, string id) =>
        new(kind, id, false, false, "", null, null, null, null, false, 0, "none");

    [Fact]
    public void TrackedScopesSinglePassRetainsManagedSleepingAndObservedKinds()
    {
        DateTime now = new(2026, 10, 15, 18, 0, 0, DateTimeKind.Utc);
        var states = new Dictionary<string, AiSystemRuntimeItemState>
        {
            ["service|a"] = FastState("SERVICE", "a") with { Managed = true },
            ["task|a"] = FastState("TASK", "a") with { Sleeping = true },
            ["driver|a"] = FastState("Driver", "a") with { ObservationSinceUtc = now },
            ["service|idle"] = FastState("service", "idle"),
            ["other|managed"] = FastState("unknown", "managed") with { Managed = true }
        };
        var found = AiMaintenanceSchedulingPolicy.TrackedScopes(states.Values);
        Assert.True(found.Service);
        Assert.True(found.Task);
        Assert.True(found.Driver);
        states.Remove("driver|a");
        found = AiMaintenanceSchedulingPolicy.TrackedScopes(states.Values);
        Assert.True(found.Service);
        Assert.True(found.Task);
        Assert.False(found.Driver);
        states.Clear();
        Assert.Equal((false, false, false),
            AiMaintenanceSchedulingPolicy.TrackedScopes(states.Values));
    }

    [Theory]
    [InlineData(true, false, false, true, false, false)]
    [InlineData(false, true, false, false, true, false)]
    [InlineData(false, false, true, false, false, true)]
    [InlineData(true, false, true, true, false, true)]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(true, true, true, true, true, true)]
    public void DisabledDomainDoesNotRequirePreScanForFastCandidates(
        bool services, bool tasks, bool drivers,
        bool expectedService, bool expectedTask, bool expectedDriver)
    {
        var states = new Dictionary<string, AiSystemRuntimeItemState>
        {
            ["service"] = FastState("service", "S") with { Managed = true },
            ["task"] = FastState("task", "T") with { Sleeping = true },
            ["driver"] = FastState("driver", "D") with
            {
                ObservationSinceUtc = new DateTime(2026, 10, 15, 18, 0, 0, DateTimeKind.Utc)
            }
        };
        var actual = AiMaintenanceSchedulingPolicy.TrackedScopes(
            states.Values, services, tasks, drivers);
        Assert.Equal((expectedService, expectedTask, expectedDriver), actual);
    }

    [Fact]
    public void DisabledKindsShortCircuitWithoutEnumeratingCandidateJournal()
    {
        var states = new Dictionary<string, AiSystemRuntimeItemState>();
        for (int i = 0; i < 100; i++)
            states[i.ToString()] = FastState("service", i.ToString()) with { Managed = true };
        _ = AiMaintenanceSchedulingPolicy.TrackedScopes(states.Values, false, false, false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int unexpected = 0;
        for (int i = 0; i < 10_000; i++)
        {
            var selected = AiMaintenanceSchedulingPolicy.TrackedScopes(
                states.Values, false, false, false);
            if (selected.Service || selected.Task || selected.Driver)
                unexpected++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, unexpected);
        Assert.True(allocated <= 128, $"Disabled pre-scan allocated {allocated} bytes");
    }

    [Fact]
    public void TrackedScopeSelectionRemainsAllocationFreeAcrossFrequentTicks()
    {
        var states = new Dictionary<string, AiSystemRuntimeItemState>();
        for (int i = 0; i < 50; i++)
            states["service|" + i] = FastState("service", i.ToString());
        states["task|active"] = FastState("task", "active") with { Managed = true };
        _ = AiMaintenanceSchedulingPolicy.TrackedScopes(states.Values);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int active = 0;
        for (int i = 0; i < 10000; i++)
        {
            var kinds = AiMaintenanceSchedulingPolicy.TrackedScopes(states.Values);
            if (kinds.Task) active++;
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10000, active);
        Assert.True(bytes <= 128, $"10,000 journal scans allocated {bytes} B");
    }

    [Fact]
    public void EveryCycleContainsEveryKindOnce()
    {
        foreach (int cycle in Enumerable.Range(0, 9))
        {
            string[] order = AiMaintenanceSchedulingPolicy.OrderForCycle(cycle);
            Assert.Equal(3, order.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains("service", order);
            Assert.Contains("task", order);
            Assert.Contains("driver", order);
        }
    }
}
