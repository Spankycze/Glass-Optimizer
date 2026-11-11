using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class AiSelfOptimizationPolicyTests
{
    [Fact]
    public void Fully_measured_self_health_does_not_need_another_process_handle()
    {
        Assert.False(AiSelfOptimizationService.NeedsProcessSnapshot(72.3, 20, 420));
        Assert.False(AiSelfOptimizationService.NeedsProcessSnapshot(0, 0, 0));
    }

    [Fact]
    public void Disabled_health_and_partially_available_process_metrics_still_use_fallback()
    {
        Assert.True(AiSelfOptimizationService.NeedsProcessSnapshot(null, null, null));
        Assert.True(AiSelfOptimizationService.NeedsProcessSnapshot(null, 20, 420));
        Assert.True(AiSelfOptimizationService.NeedsProcessSnapshot(72.3, null, 420));
        Assert.True(AiSelfOptimizationService.NeedsProcessSnapshot(72.3, 20, null));
    }

    [Fact]
    public void Process_sampling_can_keep_same_fallback_requirements_as_initialization()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        double workingSetMb = process.WorkingSet64 / 1024d / 1024d;
        int threads = process.Threads.Count;
        int handles = process.HandleCount;
        Assert.False(AiSelfOptimizationService.NeedsProcessSnapshot(workingSetMb, threads, handles));
        Assert.True(AiSelfOptimizationService.NeedsProcessSnapshot(null, threads, handles));
    }

    [Theory]
    [InlineData(10.000001d)]
    [InlineData(double.MaxValue)]
    public void ImpossiblePressureScoreCannotMasqueradeAsNormalMaintenance(double score)
    {
        Assert.Equal("Čekám na platnou telemetrii",
            AiSelfOptimizationPolicy.State(score, 60, 0.2, 1));
        Assert.True(AiSelfOptimizationPolicy.IsPressured(score, 30, 0, 0));
        Assert.False(AiSelfOptimizationPolicy.IsCalm(score, 30, 0, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void InvalidMaintenanceCadenceCannotBeReportedAsActiveOptimization(int seconds)
    {
        Assert.Equal("Čekám na platnou telemetrii",
            AiSelfOptimizationPolicy.State(0.25, seconds, 0.2, 1));
    }

    [Theory]
    [InlineData(0.70, false, true)]
    [InlineData(0.71, false, false)]
    [InlineData(1.14, false, false)]
    [InlineData(1.15, true, false)]
    [InlineData(10.0, true, false)]
    public void PressureHysteresisUsesExpectedAdmissionBounds(
        double score, bool pressured, bool calm)
    {
        Assert.Equal(pressured, AiSelfOptimizationPolicy.IsPressured(score, 30, 0, 0));
        Assert.Equal(calm, AiSelfOptimizationPolicy.IsCalm(score, 30, 0, 0));
    }

    [Theory]
    [InlineData(79.99, 0, 0, false)]
    [InlineData(80, 0, 0, true)]
    [InlineData(30, 11.99, 0, false)]
    [InlineData(30, 12, 0, true)]
    [InlineData(30, 0, 119.99, false)]
    [InlineData(30, 0, 120, true)]
    public void MemoryAndGrowthIndependentPressureLimits(
        double managedMb, double ramGrowthMbPerMinute, double handleGrowthPerMinute,
        bool expectedPressure)
    {
        Assert.Equal(expectedPressure, AiSelfOptimizationPolicy.IsPressured(
            0.25, managedMb, ramGrowthMbPerMinute, handleGrowthPerMinute));
    }

    [Fact]
    public void StableLowOverheadStaysCalm()
    {
        double score = AiSelfOptimizationPolicy.ComputePressureScore(
            cpuEwmaPercent: 0.25,
            workingSetEwmaMb: 45,
            threads: 18,
            handles: 350,
            ramGrowthMbPerMinute: 0.5,
            handleGrowthPerMinute: 2);

        Assert.True(AiSelfOptimizationPolicy.IsCalm(score, 30, 0.5, 2));
        Assert.False(AiSelfOptimizationPolicy.IsPressured(score, 30, 0.5, 2));
    }

    [Fact]
    public void SustainedRamGrowthTriggersPressureBeforeAbsoluteRamIsHuge()
    {
        double score = AiSelfOptimizationPolicy.ComputePressureScore(
            0.3, 55, 20, 400, 15, 5);

        Assert.True(AiSelfOptimizationPolicy.IsPressured(score, 35, 15, 5));
        Assert.Equal("Rostoucí režie", AiSelfOptimizationPolicy.State(score, 60, 15, 5));
    }

    [Fact]
    public void HandleGrowthTriggersPressure()
    {
        double score = AiSelfOptimizationPolicy.ComputePressureScore(
            0.2, 50, 20, 450, 1, 150);

        Assert.True(AiSelfOptimizationPolicy.IsPressured(score, 30, 1, 150));
        Assert.Equal("Rostoucí režie", AiSelfOptimizationPolicy.State(score, 60, 1, 150));
    }

    [Theory]
    [InlineData(AiWorkloadClass.Game, 60)]
    [InlineData(AiWorkloadClass.Interactive, 45)]
    [InlineData(AiWorkloadClass.Background, 30)]
    [InlineData(AiWorkloadClass.Idle, 30)]
    [InlineData(AiWorkloadClass.Unknown, 60)]
    public void SelfMonitoringCadenceFollowsWorkload(AiWorkloadClass workload, int seconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(seconds),
            AiSelfOptimizationPolicy.SamplingInterval(workload));
    }

    [Fact]
    public void Recent_enabled_samples_keep_the_current_epoch()
    {
        DateTime now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(AiSelfOptimizationPolicy.RequiresFreshBaseline(now.AddSeconds(-30), now, true));
        Assert.False(AiSelfOptimizationPolicy.RequiresFreshBaseline(now.AddMinutes(-10), now, true));
    }

    [Fact]
    public void Reenable_suspend_and_clock_rollback_require_a_fresh_baseline()
    {
        DateTime now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(AiSelfOptimizationPolicy.RequiresFreshBaseline(now.AddSeconds(-30), now, false));
        Assert.True(AiSelfOptimizationPolicy.RequiresFreshBaseline(now.AddSeconds(-30), now, null));
        Assert.True(AiSelfOptimizationPolicy.RequiresFreshBaseline(DateTime.MinValue, now, true));
        Assert.True(AiSelfOptimizationPolicy.RequiresFreshBaseline(now.AddMinutes(-11), now, true));
        Assert.True(AiSelfOptimizationPolicy.RequiresFreshBaseline(now.AddSeconds(1), now, true));
    }

    [Fact]
    public void NativeProcessWrapperCostIsMeasured()
    {
        const int rounds = 32;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            _ = process.TotalProcessorTime;
            _ = process.WorkingSet64;
            _ = process.HandleCount;
            _ = process.Threads.Count;
        }
        Console.WriteLine($"AI_PROCESS_WRAPPERS rounds={rounds} totalMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} allocatedB={GC.GetAllocatedBytesForCurrentThread() - before}");
    }

    [Fact]
    public void ReusedProcessWithRefreshCostIsMeasured()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        const int rounds = 32;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
        {
            process.Refresh();
            _ = process.TotalProcessorTime;
            _ = process.WorkingSet64;
            _ = process.HandleCount;
            _ = process.Threads.Count;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"AI_PROCESS_REFRESH rounds={rounds} totalMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} allocatedB={allocated}");
        Assert.True(allocated >= 0);
        Assert.True(process.Id > 0);
        TimeSpan cpuBeforeRefresh = process.TotalProcessorTime;
        process.Refresh();
        Assert.True(process.TotalProcessorTime >= cpuBeforeRefresh);
        Assert.True(process.WorkingSet64 > 0);
        Assert.True(process.HandleCount > 0);
        Assert.True(process.Threads.Count > 0);
    }

    [Fact]
    public void NativeProcessFreshVersusRefreshedPairedMeasurement()
    {
        const int rounds = 64;
        static (double Milliseconds, long Bytes) Measure(bool refreshed)
        {
            using var reused = System.Diagnostics.Process.GetCurrentProcess();
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < rounds; i++)
            {
                if (refreshed)
                {
                    reused.Refresh();
                    _ = reused.TotalProcessorTime;
                    _ = reused.WorkingSet64;
                    _ = reused.HandleCount;
                    _ = reused.Threads.Count;
                }
                else
                {
                    using var fresh = System.Diagnostics.Process.GetCurrentProcess();
                    _ = fresh.TotalProcessorTime;
                    _ = fresh.WorkingSet64;
                    _ = fresh.HandleCount;
                    _ = fresh.Threads.Count;
                }
            }
            return (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }
        // Prime native counter caches/JIT before the paired ABBA comparison.
        _ = Measure(false);
        _ = Measure(true);
        var freshFirst = Measure(false);
        var refreshedFirst = Measure(true);
        var refreshedSecond = Measure(true);
        var freshSecond = Measure(false);
        Console.WriteLine($"AI_NATIVE_PAIRED rounds={rounds} freshMs={freshFirst.Milliseconds:F3},{freshSecond.Milliseconds:F3} refreshMs={refreshedFirst.Milliseconds:F3},{refreshedSecond.Milliseconds:F3} freshBytes={freshFirst.Bytes},{freshSecond.Bytes} refreshBytes={refreshedFirst.Bytes},{refreshedSecond.Bytes}");
        Assert.True(freshFirst.Bytes > 0);
        Assert.True(refreshedFirst.Bytes > 0);
    }

    [Fact]
    public void ProcessCountersRemainFreshAfterRefresh()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan before = process.TotalProcessorTime;
        long initialMemory = process.WorkingSet64;
        int initialThreads = process.Threads.Count;
        int initialHandles = process.HandleCount;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 20)
            _ = Math.Sqrt(123456.789);
        process.Refresh();
        Assert.True(process.TotalProcessorTime >= before);
        Assert.True(process.WorkingSet64 > 0 && initialMemory > 0);
        Assert.True(process.Threads.Count > 0 && initialThreads > 0);
        Assert.True(process.HandleCount > 0 && initialHandles > 0);
    }

    [Fact]
    public void NativeProcessAcquisitionVersusCountersCost()
    {
        const int rounds = 64;
        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                _ = process.Id;
        double acquisitionMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long acquisitionBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
        startBytes = GC.GetAllocatedBytesForCurrentThread();
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            _ = process.TotalProcessorTime;
            _ = process.WorkingSet64;
            _ = process.HandleCount;
            _ = process.Threads.Count;
        }
        Console.WriteLine($"AI_NATIVE_COST_PHASES count={rounds} acquisitionMs={acquisitionMs:F3} acquisitionBytes={acquisitionBytes} fullMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} fullBytes={GC.GetAllocatedBytesForCurrentThread() - startBytes}");
        Assert.True(acquisitionBytes >= 0);
    }

    [Fact]
    public void NativeThreadEnumerationDominanceIsMeasured()
    {
        const int rounds = 48;
        static (double Ms, long Bytes) Measure(bool threads)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < rounds; i++)
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                _ = p.TotalProcessorTime;
                _ = p.WorkingSet64;
                _ = p.HandleCount;
                if (threads) _ = p.Threads.Count;
            }
            return (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }
        _ = Measure(false);
        _ = Measure(true);
        var a = Measure(false);
        var b = Measure(true);
        Console.WriteLine($"AI_THREAD_ENUM rounds={rounds} withoutMs={a.Ms:F3} withMs={b.Ms:F3} withoutBytes={a.Bytes} withBytes={b.Bytes}");
        Assert.True(a.Bytes > 0 && b.Bytes > 0);
    }

    [Fact]
    public void NativeProcessGetterAllocationBreakdown()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        const int rounds = 100;
        foreach (string metric in new[] { "cpu", "ram", "handles", "threads" })
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < rounds; i++)
            {
                switch (metric)
                {
                    case "cpu": _ = process.TotalProcessorTime; break;
                    case "ram": _ = process.WorkingSet64; break;
                    case "handles": _ = process.HandleCount; break;
                    case "threads": _ = process.Threads.Count; break;
                }
            }
            Console.WriteLine($"AI_NATIVE_GETTER name={metric} rounds={rounds} elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} allocatedB={GC.GetAllocatedBytesForCurrentThread() - before}");
        }
    }

    [Fact]
    public void NativeSamplingElapsedTimeIsMeasured()
    {
        var service = new AiSelfOptimizationService(suppressHealthWrites: true);
        _ = service.Sample(true, AiWorkloadClass.Background);
        var field = typeof(AiSelfOptimizationService).GetField("_lastSampleTimestamp",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        long ramBefore = process.WorkingSet64;
        int handlesBefore = process.HandleCount;
        int threadsBefore = process.Threads.Count;
        TimeSpan cpuBefore = process.TotalProcessorTime;
        const int repetitions = 32;
        double totalMs = 0;
        long totalBytes = 0;
        for (int i = 0; i < repetitions; i++)
        {
            field.SetValue(service, System.Diagnostics.Stopwatch.GetTimestamp() -
                System.Diagnostics.Stopwatch.Frequency * 31);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            _ = service.Sample(true, AiWorkloadClass.Background);
            totalMs += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            totalBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        process.Refresh();
        Console.WriteLine($"AI_NATIVE_SAMPLE_32 totalMs={totalMs:F3} avgMs={totalMs / repetitions:F4} allocatedB={totalBytes} cpuDeltaMs={(process.TotalProcessorTime - cpuBefore).TotalMilliseconds:F2} ramDeltaB={process.WorkingSet64 - ramBefore} handlesDelta={process.HandleCount - handlesBefore} threadsDelta={process.Threads.Count - threadsBefore}");
        Assert.Null(service.CurrentHealth);
    }

    [Fact]
    public void DisabledSamplingDoesNotAllocateOnRepeatedTicks()
    {
        var service = new AiSelfOptimizationService(suppressHealthWrites: true);
        Assert.Equal(60, service.Sample(false, AiWorkloadClass.Background));
        for (int i = 0; i < 1000; i++) _ = service.Sample(false, AiWorkloadClass.Background);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int interval = 0;
        for (int i = 0; i < 100_000; i++)
            interval = service.Sample(false, AiWorkloadClass.Background);
        Assert.Equal(60, interval);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"AI_DISABLED_100K elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} allocatedB={bytes}");
        Assert.True(bytes < 4096);
        Assert.Null(service.CurrentHealth);
    }

    [Fact]
    public void CachedSamplingTickHasBoundedManagedAllocation()
    {
        var service = new AiSelfOptimizationService(suppressHealthWrites: true);
        Assert.Equal(60, service.Sample(true, AiWorkloadClass.Background));
        for (int i = 0; i < 10_000; i++) _ = service.Sample(true, AiWorkloadClass.Background);
        const int iterations = 100_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int interval = 0;
        for (int i = 0; i < iterations; i++) interval = service.Sample(true, AiWorkloadClass.Background);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.Equal(60, interval);
        Assert.True(allocated < 4096, $"Cached self-monitor ticks allocated {allocated} B in 100k calls");
        Assert.True(elapsedMs > 0);
        Console.WriteLine($"AI_CACHED_SAMPLE_100K elapsedMs={elapsedMs:F3} allocatedB={allocated} nsPerTick={elapsedMs * 1_000_000 / iterations:F1}");
        Assert.Null(service.CurrentHealth);
    }

    [Theory]
    [InlineData("_cpuEwma")]
    [InlineData("_ramEwma")]
    [InlineData("_ramGrowthEwmaPerMinute")]
    [InlineData("_handleGrowthEwmaPerMinute")]
    public void FullSampleRecoversInvalidEwmaWithoutPersistingMachineHealth(string corruptField)
    {
        var service = new AiSelfOptimizationService(suppressHealthWrites: true);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Set(string name, object value) =>
            typeof(AiSelfOptimizationService).GetField(name, flags)!.SetValue(service, value);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Set("_lastEnabledState", true);
        Set("_lastSampleTimestamp", System.Diagnostics.Stopwatch.GetTimestamp() -
            System.Diagnostics.Stopwatch.Frequency * 61);
        Set("_lastCpu", process.TotalProcessorTime);
        Set("_lastWorkingSetMb", process.WorkingSet64 / 1024d / 1024d);
        Set("_lastHandleCount", process.HandleCount);
        Set("_hasEwmaSample", true);
        foreach (string lane in new[] { "_cpuEwma", "_ramEwma", "_ramGrowthEwmaPerMinute", "_handleGrowthEwmaPerMinute" })
            Set(lane, 0d);
        Set(corruptField, double.NaN);
        Assert.True(service.RequiresFreshEpochFromCurrentEwma());
        Assert.Equal(60, service.Sample(true, AiWorkloadClass.Background));
        Assert.False(service.RequiresFreshEpochFromCurrentEwma());
        Assert.Null(service.CurrentHealth);
    }

    [Theory]
    [InlineData("_cpuEwma")]
    [InlineData("_ramEwma")]
    [InlineData("_ramGrowthEwmaPerMinute")]
    [InlineData("_handleGrowthEwmaPerMinute")]
    public void CorruptEwmaEpochCanRecoverWithoutKeepingInvalidHistory(string corruptField)
    {
        var service = new AiSelfOptimizationService();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        string[] lanes = ["_cpuEwma", "_ramEwma", "_ramGrowthEwmaPerMinute", "_handleGrowthEwmaPerMinute"];
        foreach (string lane in lanes)
            typeof(AiSelfOptimizationService).GetField(lane, flags)!.SetValue(service, 5d);
        typeof(AiSelfOptimizationService).GetField(corruptField, flags)!.SetValue(service, double.NaN);
        double[] before = lanes.Select(lane => (double)typeof(AiSelfOptimizationService).GetField(lane, flags)!.GetValue(service)!).ToArray();
        Assert.True(AiSelfOptimizationPolicy.RequiresEwmaReset(before[0], before[1], before[2], before[3]));
        Assert.True(service.RequiresFreshEpochFromCurrentEwma());
        service.ResetSamplingEpoch();
        foreach (string lane in lanes)
            Assert.Equal(0d, (double)typeof(AiSelfOptimizationService).GetField(lane, flags)!.GetValue(service)!);
        Assert.False(service.RequiresFreshEpochFromCurrentEwma());
        Assert.False(AiSelfOptimizationPolicy.RequiresEwmaReset(0d, 0d, 0d, 0d));
        Assert.Equal(30d, AiSelfOptimizationPolicy.BlendEwma(0d, 30d, 0.72d, false));
    }

    [Fact]
    public void Starting_new_epoch_clears_stale_growth_pressure_and_fingerprint()
    {
        var service = new AiSelfOptimizationService();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        void Set(string field, object value)
        {
            var info = typeof(AiSelfOptimizationService).GetField(field, flags);
            Assert.NotNull(info);
            info.SetValue(service, value);
        }
        T Read<T>(string field)
        {
            var info = typeof(AiSelfOptimizationService).GetField(field, flags);
            Assert.NotNull(info);
            return Assert.IsType<T>(info.GetValue(service));
        }

        Set("_lastSampleUtc", new DateTime(2026, 10, 10, 11, 0, 0, DateTimeKind.Utc));
        Set("_lastCpu", TimeSpan.FromMinutes(4));
        Set("_maintenanceIntervalSeconds", 240);
        foreach (string field in new[] { "_cpuEwma", "_ramEwma", "_lastWorkingSetMb",
                     "_ramGrowthEwmaPerMinute", "_handleGrowthEwmaPerMinute" })
            Set(field, 98d);
        Set("_lastHandleCount", 900);
        Set("_pressureStreak", 2);
        Set("_calmStreak", 3);
        var fingerprintType = typeof(AiSelfOptimizationService).GetNestedType(
            "HealthFingerprint", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(fingerprintType);
        object? fingerprint = Activator.CreateInstance(fingerprintType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: new object[] { true, 1d, 2d, 3, 4, 5d, 6d, 60, "old" }, culture: null);
        Assert.NotNull(fingerprint);
        Set("_lastHealthFingerprint", fingerprint);
        Set("_lastHealthWriteUtc", DateTime.UtcNow);

        service.ResetSamplingEpoch();

        Assert.Equal(DateTime.MinValue, Read<DateTime>("_lastSampleUtc"));
        Assert.Equal(TimeSpan.Zero, Read<TimeSpan>("_lastCpu"));
        Assert.Equal(60, Read<int>("_maintenanceIntervalSeconds"));
        foreach (string field in new[] { "_cpuEwma", "_ramEwma", "_lastWorkingSetMb",
                     "_ramGrowthEwmaPerMinute", "_handleGrowthEwmaPerMinute" })
            Assert.Equal(0d, Read<double>(field));
        Assert.Equal(0, Read<int>("_lastHandleCount"));
        Assert.Equal(0, Read<int>("_pressureStreak"));
        Assert.Equal(0, Read<int>("_calmStreak"));
        Assert.Null(typeof(AiSelfOptimizationService)
            .GetField("_lastHealthFingerprint", flags)!.GetValue(service));
        Assert.Equal(DateTime.MinValue, Read<DateTime>("_lastHealthWriteUtc"));
    }

    [Fact]
    public void WorkloadAppliesConservativeMaintenanceFloor()
    {
        Assert.Equal(180, AiSelfOptimizationPolicy.ApplyWorkloadFloor(45, AiWorkloadClass.Game));
        Assert.Equal(120, AiSelfOptimizationPolicy.ApplyWorkloadFloor(45, AiWorkloadClass.Interactive));
        Assert.Equal(60, AiSelfOptimizationPolicy.ApplyWorkloadFloor(45, AiWorkloadClass.Background));
        Assert.Equal(120, AiSelfOptimizationPolicy.ApplyWorkloadFloor(45, AiWorkloadClass.Unknown));
        Assert.Equal(45, AiSelfOptimizationPolicy.ApplyWorkloadFloor(45, AiWorkloadClass.Idle));
        Assert.Equal(240, AiSelfOptimizationPolicy.ApplyWorkloadFloor(240, AiWorkloadClass.Game));
    }
}
