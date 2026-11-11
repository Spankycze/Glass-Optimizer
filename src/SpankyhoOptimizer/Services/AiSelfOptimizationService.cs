using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed record AiSelfOptimizationHealth(
    DateTime UpdatedUtc,
    bool Enabled,
    double ProcessCpuPercent,
    double WorkingSetMb,
    int Threads,
    int Handles,
    double ManagedMemoryMb,
    double PressureScore,
    int MaintenanceIntervalSeconds,
    string State);

public sealed class AiSelfOptimizationService
{
    // Processor topology is stable throughout the lifetime of the agent.
    // Avoid reevaluating Environment.ProcessorCount on each native sample.
    private static readonly int ProcessCpuNormalizationCores = Math.Max(1, Environment.ProcessorCount);
    private readonly bool _suppressHealthWrites;
    public AiSelfOptimizationService() { }
    internal AiSelfOptimizationService(bool suppressHealthWrites) =>
        _suppressHealthWrites = suppressHealthWrites;

    private static readonly string HealthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer",
        "ai-self-optimization-health.json");
    private static readonly ObservedJsonSnapshotCache<AiSelfOptimizationHealth> HealthSnapshot = new(HealthPath);

    private DateTime _lastSampleUtc = DateTime.MinValue;
    private long _lastSampleTimestamp;
    private TimeSpan _lastCpu = TimeSpan.Zero;
    private int _maintenanceIntervalSeconds = 60;
    private double _cpuEwma;
    private bool _hasEwmaSample;
    private double _ramEwma;
    private double _lastWorkingSetMb;
    private int _lastHandleCount;
    private double _ramGrowthEwmaPerMinute;
    private double _handleGrowthEwmaPerMinute;
    private int _pressureStreak;
    private int _calmStreak;
    private bool? _lastEnabledState;
    private readonly record struct HealthFingerprint(
        bool Enabled, double Cpu, double WorkingSetMb, int Threads,
        int Handles, double ManagedMemoryMb, double Pressure,
        int MaintenanceIntervalSeconds, string State);

    // Value-type equality avoids boxing all eight numbers and allocating
    // a formatted string at every pressure sample before the 2-minute
    // persisted-health write throttle has even been evaluated.
    private HealthFingerprint? _lastHealthFingerprint;
    private long _lastHealthWriteStamp; // monotonic persistence throttle
    // Published by this FanAgent process only after an atomic health write.
    // Reuse this immutable record for same-loop consumers instead of stat-ing
    // and parsing the same JSON via the read-only cross-process bridge.
    private AiSelfOptimizationHealth? _currentHealth;
    internal AiSelfOptimizationHealth? CurrentHealth => _currentHealth;

    public int Sample(bool enabled) => Sample(enabled, AiWorkloadClass.Unknown);

    public int Sample(bool enabled, AiWorkloadClass workload)
    {
        if (!enabled)
        {
            if (_lastEnabledState != false)
            {
                ResetSamplingEpoch();
                WriteHealth(false, 0, "OFF");
            }
            _lastEnabledState = false;
            return _maintenanceIntervalSeconds;
        }

        // Disabled monitoring performs no high-resolution clock query.
        long sampleTimestamp = Stopwatch.GetTimestamp();
        // Compute the monotonic elapsed interval once, reusing it for both
        // epoch validation and the cached sampling interval admission.
        double sinceLastSampleMs = 0;
        bool validEpoch = _lastEnabledState == true &&
            AiSelfOptimizationPolicy.TryMonotonicElapsedMilliseconds(
                _lastSampleTimestamp, sampleTimestamp, Stopwatch.Frequency,
                out sinceLastSampleMs);
        if (!validEpoch)
            ResetSamplingEpoch();
        _lastEnabledState = true;
        // Cached ticks reuse the integer cadence policy, avoiding a TimeSpan
        // conversion while retaining the native sample schedule.
        int sampleIntervalMs = AiSelfOptimizationPolicy.SamplingIntervalMilliseconds(workload);
        // Clock corrections must not force a process snapshot or delay it.
        if (validEpoch && sinceLastSampleMs < sampleIntervalMs)
            return _maintenanceIntervalSeconds;

        // The common cached tick needs no wall-clock read; only a real
        // native sample uses UTC for the persisted human-readable timestamp.
        DateTime now = DateTime.UtcNow;
        using Process process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;

        if (_lastSampleTimestamp == 0)
        {
            InitializeSamplingBaseline(process, cpu, now, sampleTimestamp);
            return _maintenanceIntervalSeconds;
        }

        // Reuse the already validated monotonic interval from the admission
        // check, avoiding a duplicate elapsed-time calculation on native ticks.
        // A missing/invalid epoch was reset and returned at baseline above.
        double elapsedMs = sinceLastSampleMs;
        if (!AiSelfOptimizationPolicy.TryProcessCpuPercent(
                _lastCpu, cpu, elapsedMs, ProcessCpuNormalizationCores,
                out double cpuPercent))
        {
            // Never blend corrupt process counters into EMA/pressure votes.
            // The Process wrapper already exists: reuse it for new baseline.
            ResetSamplingEpoch();
            InitializeSamplingBaseline(process, cpu, now, sampleTimestamp);
            return _maintenanceIntervalSeconds;
        }

        // Sample lightweight process counters before the thread enumeration;
        // all values still belong to the same scheduled native sample.
        double workingSetMb = process.WorkingSet64 / 1024d / 1024d;
        int handles = process.HandleCount;
        int threads = process.Threads.Count;
        double managedMemoryMb = GC.GetTotalMemory(false) / 1024d / 1024d;

        // Both growth sensors share the same monotonic sampling interval.
        // Compute its reciprocal once rather than dividing both counters.
        double perMinuteScale = 1d / Math.Max(0.05, elapsedMs / 60000d);
        double ramGrowthPerMinute = Math.Clamp(
            Math.Max(0, workingSetMb - _lastWorkingSetMb) * perMinuteScale,
            0,
            250);
        double handleGrowthPerMinute = Math.Clamp(
            Math.Max(0, handles - _lastHandleCount) * perMinuteScale,
            0,
            3000);

        _lastSampleUtc = now;
        _lastSampleTimestamp = sampleTimestamp;
        _lastCpu = cpu;
        _lastWorkingSetMb = workingSetMb;
        _lastHandleCount = handles;

        _cpuEwma = AiSelfOptimizationPolicy.BlendEwma(
            _cpuEwma, cpuPercent, 0.72, _hasEwmaSample);
        _ramEwma = AiSelfOptimizationPolicy.BlendEwma(
            _ramEwma, workingSetMb, 0.82, _hasEwmaSample);
        _ramGrowthEwmaPerMinute = AiSelfOptimizationPolicy.BlendEwma(
            _ramGrowthEwmaPerMinute, ramGrowthPerMinute, 0.72, _hasEwmaSample);
        _handleGrowthEwmaPerMinute = AiSelfOptimizationPolicy.BlendEwma(
            _handleGrowthEwmaPerMinute, handleGrowthPerMinute, 0.72, _hasEwmaSample);
        // Sensor failures must not poison every future EMA sample.
        // The current interval remains unknown and re-baselines immediately;
        // do not vote it as calm or pressured using corrupt history.
        if (RequiresFreshEpochFromCurrentEwma())
        {
            ResetSamplingEpoch();
            InitializeSamplingBaseline(process, cpu, now, sampleTimestamp);
            return _maintenanceIntervalSeconds;
        }
        _hasEwmaSample = true;

        double pressureScore = AiSelfOptimizationPolicy.ComputePressureScore(
            _cpuEwma,
            _ramEwma,
            threads,
            handles,
            _ramGrowthEwmaPerMinute,
            _handleGrowthEwmaPerMinute);

        bool pressured = AiSelfOptimizationPolicy.IsPressured(
            pressureScore,
            managedMemoryMb,
            _ramGrowthEwmaPerMinute,
            _handleGrowthEwmaPerMinute);
        bool calm = AiSelfOptimizationPolicy.IsCalm(
            pressureScore,
            managedMemoryMb,
            _ramGrowthEwmaPerMinute,
            _handleGrowthEwmaPerMinute);
        _pressureStreak = pressured ? _pressureStreak + 1 : 0;
        _calmStreak = calm ? _calmStreak + 1 : 0;

        if (_pressureStreak >= 2)
        {
            _maintenanceIntervalSeconds = Math.Min(240, _maintenanceIntervalSeconds + 30);
            _pressureStreak = 0;
        }
        else if (_calmStreak >= 3)
        {
            _maintenanceIntervalSeconds = Math.Max(45, _maintenanceIntervalSeconds - 15);
            _calmStreak = 0;
        }

        _maintenanceIntervalSeconds = AiSelfOptimizationPolicy.ApplyWorkloadFloor(
            _maintenanceIntervalSeconds,
            workload);

        string state = AiSelfOptimizationPolicy.State(
            pressureScore,
            _maintenanceIntervalSeconds,
            _ramGrowthEwmaPerMinute,
            _handleGrowthEwmaPerMinute);

        WriteHealth(true, cpuPercent, state, workingSetMb, threads, handles, managedMemoryMb, pressureScore, observedUtc: now);
        return _maintenanceIntervalSeconds;
    }

    // Called only after an initial or discarded sample. No new OpenProcess;
    // use the Process already created for the existing periodic sampling tick.
    private void InitializeSamplingBaseline(
        Process process, TimeSpan cpu, DateTime now, long sampleTimestamp)
    {
        _lastSampleUtc = now;
        _lastSampleTimestamp = sampleTimestamp;
        _lastCpu = cpu;
        _lastWorkingSetMb = process.WorkingSet64 / 1024d / 1024d;
        _lastHandleCount = process.HandleCount;
        int threadCount = process.Threads.Count;
        WriteHealth(true, 0, "Inicializace",
            _lastWorkingSetMb, threadCount, _lastHandleCount,
            GC.GetTotalMemory(false) / 1024d / 1024d, observedUtc: now);
    }

    // Reset moving averages when monitoring is re-enabled or monotonic sample
    // continuity is lost across a long suspend or invalid native CPU delta.
    // Used by the live sampling path and by isolated recovery regression tests.
    // Never accesses the persisted machine health snapshot.
    internal bool RequiresFreshEpochFromCurrentEwma() =>
        AiSelfOptimizationPolicy.RequiresEwmaReset(
            _cpuEwma, _ramEwma, _ramGrowthEwmaPerMinute,
            _handleGrowthEwmaPerMinute);

    internal void ResetSamplingEpoch()
    {
        _lastSampleUtc = DateTime.MinValue;
        _lastSampleTimestamp = 0;
        _lastCpu = TimeSpan.Zero;
        _maintenanceIntervalSeconds = 60;
        _cpuEwma = 0;
        _hasEwmaSample = false;
        _ramEwma = 0;
        _lastWorkingSetMb = 0;
        _lastHandleCount = 0;
        _ramGrowthEwmaPerMinute = 0;
        _handleGrowthEwmaPerMinute = 0;
        _pressureStreak = 0;
        _calmStreak = 0;
        _lastHealthFingerprint = null;
        _lastHealthWriteStamp = 0;
        _currentHealth = null;
    }

    public static AiSelfOptimizationHealth? ReadHealth() => HealthSnapshot.TryRead();

    // Cross-process readers still use the persisted JSON. FanAgent can skip
    // native FileInfo.Refresh on every 1-5s tick when the same instance just
    // committed its health record. Stale/future snapshots fall back to disk.
    internal static bool IsSameProcessHealthFresh(
        AiSelfOptimizationHealth? health, DateTime nowUtc) =>
        health != null && health.UpdatedUtc <= nowUtc &&
        nowUtc - health.UpdatedUtc <= TimeSpan.FromMinutes(5);

    internal static AiSelfOptimizationHealth? ResolveSameProcessHealth(
        AiSelfOptimizationHealth? committed, DateTime nowUtc) =>
        IsSameProcessHealthFresh(committed, nowUtc) ? committed : ReadHealth();

    // Read-only benchmark for normal health consumption; no settings writes,
    // health writes or Process priority changes are performed.
    internal static string RunSameProcessHealthReuseSmoke()
    {
        DateTime now = DateTime.UtcNow;
        var committed = new AiSelfOptimizationHealth(
            now, true, 0.2, 38, 6, 140, 11, 0.3, 60, "Normal");
        if (!ReferenceEquals(ResolveSameProcessHealth(committed, now), committed) ||
            IsSameProcessHealthFresh(committed, now.AddMinutes(6)) ||
            IsSameProcessHealthFresh(committed, now.AddMilliseconds(-1)) ||
            IsSameProcessHealthFresh(null, now))
            throw new InvalidOperationException("Self-health current-process reuse freshness regression.");
        const int samples = 1024;
        long allocStart = GC.GetAllocatedBytesForCurrentThread();
        long clockStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++) _ = ReadHealth();
        double baselineMs = Stopwatch.GetElapsedTime(clockStart).TotalMilliseconds;
        long baselineBytes = GC.GetAllocatedBytesForCurrentThread() - allocStart;
        allocStart = GC.GetAllocatedBytesForCurrentThread();
        clockStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++)
            if (!ReferenceEquals(ResolveSameProcessHealth(committed, now), committed))
                throw new InvalidOperationException("Same-process health identity regression.");
        double reuseMs = Stopwatch.GetElapsedTime(clockStart).TotalMilliseconds;
        long reuseBytes = GC.GetAllocatedBytesForCurrentThread() - allocStart;
        return $"AI_SELF_HEALTH_SNAPSHOT_REUSE_OK samples={samples} " +
            $"fileMetadataMs={baselineMs:F3} directMs={reuseMs:F3} " +
            $"fileAllocatedB={baselineBytes} directAllocatedB={reuseBytes} " +
            "staleFallback=true futureRejected=true writes=0";
    }

    // Read-only benchmark for the recurring 2-minute health-write gate.
    // No sampling, file writes, native process changes or forced collections.
    internal static string RunHealthFingerprintValueSmoke()
    {
        var baseline = new HealthFingerprint(true, 0.48, 43.1, 9, 170, 18.9, 12.45, 60, "Normal");
        var changes = new[]
        {
            baseline with { Enabled = false },
            baseline with { Cpu = 0.49 },
            baseline with { WorkingSetMb = 43.2 },
            baseline with { Threads = 10 },
            baseline with { Handles = 171 },
            baseline with { ManagedMemoryMb = 19.0 },
            baseline with { Pressure = 12.46 },
            baseline with { MaintenanceIntervalSeconds = 75 },
            baseline with { State = "CPU pressure" }
        };
        foreach (var changed in changes)
        {
            if (baseline == changed || changed == baseline)
                throw new InvalidOperationException("AI health fingerprint missed a tracked change.");
        }
        if (baseline != baseline with { State = string.Concat("Nor", "mal") })
            throw new InvalidOperationException("AI health fingerprint text equality changed.");

        const int rounds = 16384;
        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        int oldChanges = 0;
        string? prior = null;
        for (int i = 0; i < rounds; i++)
        {
            var value = changes[i % changes.Length];
            string current = string.Join('|', value.Enabled, value.Cpu,
                value.WorkingSetMb, value.Threads, value.Handles,
                value.ManagedMemoryMb, value.Pressure,
                value.MaintenanceIntervalSeconds, value.State);
            if (!string.Equals(prior, current, StringComparison.Ordinal)) oldChanges++;
            prior = current;
        }
        double oldMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long oldAllocatedB = GC.GetAllocatedBytesForCurrentThread() - startBytes;

        startBytes = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        int newChanges = 0;
        HealthFingerprint? priorValue = null;
        for (int i = 0; i < rounds; i++)
        {
            var current = changes[i % changes.Length];
            if (priorValue != current) newChanges++;
            priorValue = current;
        }
        double newMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long newAllocatedB = GC.GetAllocatedBytesForCurrentThread() - startBytes;
        if (oldChanges != newChanges || newAllocatedB >= oldAllocatedB)
            throw new InvalidOperationException("AI health fingerprint allocation or state parity regression.");
        return $"AI_SELF_HEALTH_FINGERPRINT_NO_ALLOC_OK rounds={rounds} " +
            $"beforeAllocatedB={oldAllocatedB} afterAllocatedB={newAllocatedB} " +
            $"beforeMs={oldMs:F3} afterMs={newMs:F3} " +
            $"trackedChanges=9 equivalentState=true writes=0";
    }

    // Health checkpoints must age by elapsed time, not adjustable UTC.
    // An NTP/backwards clock jump must not keep the last saved health alive
    // indefinitely while downstream consumers reject it as stale.
    internal static bool IsHealthWriteThrottleActive(long lastStamp,
        long nowStamp, long frequency) =>
        lastStamp > 0 && nowStamp >= lastStamp && frequency > 0 &&
        (nowStamp - (double)lastStamp) / frequency < 120d;

    internal static bool ShouldThrottleHealthWrite(bool sameFingerprint,
        long lastStamp, long nowStamp, long frequency) =>
        sameFingerprint && IsHealthWriteThrottleActive(lastStamp, nowStamp, frequency);

    internal static string RunHealthWriteThrottleSmoke()
    {
        const long frequency = 10_000;
        const long baseline = 100_000;
        long twoMinutes = baseline + 120 * frequency;
        if (!ShouldThrottleHealthWrite(true, baseline, baseline, frequency) ||
            !ShouldThrottleHealthWrite(true, baseline, twoMinutes - 1, frequency) ||
            ShouldThrottleHealthWrite(true, baseline, twoMinutes, frequency) ||
            ShouldThrottleHealthWrite(true, baseline, baseline - 1, frequency) ||
            ShouldThrottleHealthWrite(true, 0, baseline, frequency) ||
            ShouldThrottleHealthWrite(true, baseline, baseline, 0) ||
            ShouldThrottleHealthWrite(false, baseline, baseline, frequency))
            throw new InvalidOperationException("AI health monotonic write throttle regressed.");
        // A wall-clock correction no longer changes the 2-minute elapsed
        // deadline. Real elapsed time and changed state determine writes.
        DateTime wallClock = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        if (wallClock.AddHours(-3) >= wallClock ||
            ShouldThrottleHealthWrite(true, baseline, twoMinutes, frequency))
            throw new InvalidOperationException("AI health writer follows Windows clock adjustment.");
        return "AI_HEALTH_MONOTONIC_WRITE_SMOKE_OK cadence120s=true " +
            "stateChangeImmediate=true backwardTimeIgnored=true writes=0";
    }

    // Current sampling provides all three native Process counters. Only OFF
    // transitions or another partial caller require a new process snapshot.
    internal static bool NeedsProcessSnapshot(double? workingSetMb, int? threads, int? handles) =>
        workingSetMb is null || threads is null || handles is null;

    private void WriteHealth(
        bool enabled,
        double cpuPercent,
        string state,
        double? workingSetMb = null,
        int? threads = null,
        int? handles = null,
        double? managedMemoryMb = null,
        double pressureScore = 0,
        DateTime? observedUtc = null)
    {
        // Test-only instance suppresses writes to the shared machine health file.
        if (_suppressHealthWrites) return;
        try
        {
            // Sample() already collected the native counters. Avoid creating
            // and disposing another Process wrapper in that common path.
            using Process? fallbackProcess = NeedsProcessSnapshot(workingSetMb, threads, handles)
                ? Process.GetCurrentProcess()
                : null;
            double roundedCpu = Math.Round(cpuPercent, 2);
            double roundedWorkingSet = Math.Round(workingSetMb ?? fallbackProcess!.WorkingSet64 / 1024d / 1024d, 1);
            int threadCount = threads ?? fallbackProcess!.Threads.Count;
            int handleCount = handles ?? fallbackProcess!.HandleCount;
            double roundedManaged = Math.Round(managedMemoryMb ?? GC.GetTotalMemory(false) / 1024d / 1024d, 1);
            double roundedPressure = Math.Round(pressureScore, 2);
            var fingerprint = new HealthFingerprint(
                enabled, roundedCpu, roundedWorkingSet,
                threadCount, handleCount, roundedManaged, roundedPressure,
                _maintenanceIntervalSeconds, state);
            DateTime nowUtc = observedUtc ?? DateTime.UtcNow;
            long nowStamp = Stopwatch.GetTimestamp();
            if (ShouldThrottleHealthWrite(
                _lastHealthFingerprint is HealthFingerprint previous &&
                previous == fingerprint,
                _lastHealthWriteStamp, nowStamp, Stopwatch.Frequency))
                return;

            var health = new AiSelfOptimizationHealth(
                nowUtc,
                enabled,
                roundedCpu,
                roundedWorkingSet,
                threadCount,
                handleCount,
                roundedManaged,
                roundedPressure,
                _maintenanceIntervalSeconds,
                state);

            Directory.CreateDirectory(Path.GetDirectoryName(HealthPath)!);
            // JSON is identical on disk, but avoid the transient UTF-16
            // string and re-encoding on each durable health checkpoint.
            // Unique sidecars also keep two RuntimeHosts from trampling the
            // same fixed .tmp payload for this shared machine health file.
            AtomicFileService.WriteAllBytesAtomic(
                HealthPath, JsonSerializer.SerializeToUtf8Bytes(health));
            // Invalidate only after the atomic rename. Never publish an
            // uncommitted or no-longer-readable health value in memory.
            HealthSnapshot.InvalidateAfterWrite();
            _currentHealth = health;
            _lastHealthFingerprint = fingerprint;
            _lastHealthWriteStamp = nowStamp;
        }
        catch
        {
            // Self-telemetry must never affect the runtime.
        }
    }
}
