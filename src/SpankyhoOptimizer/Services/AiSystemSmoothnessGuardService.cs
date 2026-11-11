using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace SpankyhoOptimizer.Services;

// Reuses FanAgent's existing tick. Windows-wide memory/CPU pressure, not a fake FPS metric.
public sealed record AiSystemSmoothnessHealth(
    DateTime UpdatedUtc, double CpuLoadPercent, double UsedRamPercent,
    double FreeRamGiB, string State, string Detail, bool GameActive,
    double? DiskBusyPercent = null, double? DiskLatencyMs = null,
    int RecoveryWaitSeconds = 0, double? HottestLogicalProcessorPercent = null,
    string HottestLogicalProcessor = "", int LogicalCorePressureSamples = 0);

public static class AiSystemSmoothnessGuardService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private static readonly string StatusPath = Path.Combine(LogService.Root, "ai-system-smoothness.json");
    private static readonly ObservedJsonSnapshotCache<AiSystemSmoothnessHealth> DiskSnapshot = new(StatusPath);
    private static AiSystemSmoothnessHealth? _latest;
    private static DateTime _lastSampleUtc, _lastWrittenUtc;
    private static string _lastState = "";
    private static int _sustainedLogicalCoreSamples;
    private static int _sustainedDiskBusySamples;
    private static int _sustainedDiskLatencySamples;
    private static int _stableNormalSamples;
    private static DateTime _lastPressureUtc = DateTime.MinValue;
    // Don't restart competing A/B/A experiments immediately after a spike.
    // FanAgent samples every 30 seconds: require >=3 quiet readings and
    // 2 minutes of stability after CPU/RAM/disk pressure.
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(2);

    public static AiSystemSmoothnessHealth? ReadCurrent()
    {
        var value = _latest;
        if (value != null && IsFresh(value.UpdatedUtc, DateTime.UtcNow, TimeSpan.FromMinutes(2)))
            return value;
        try
        {
            value = DiskSnapshot.Read();
            // GUI is a different process: a 6-minute-old snapshot must not
            // masquerade as LIVE CPU/RAM/I/O status. Keep at most 2 minutes.
            return value != null && IsFresh(value.UpdatedUtc, DateTime.UtcNow, TimeSpan.FromMinutes(2))
                ? value : null;
        }
        catch { return null; }
    }

    // Reject future-dated snapshots too: sleep, resume or clock correction must
    // never make an old or invalid reading look indefinitely fresh.
    internal static bool IsFresh(DateTime updatedUtc, DateTime nowUtc, TimeSpan limit) =>
        updatedUtc.Kind == DateTimeKind.Utc &&
        nowUtc.Kind == DateTimeKind.Utc &&
        updatedUtc != default && limit >= TimeSpan.Zero &&
        nowUtc >= updatedUtc && nowUtc - updatedUtc <= limit;

    // FanAgent already owns the immutable in-memory sample. Disk snapshots
    // serve GUI/other processes only. Never stat/decode JSON on every agent
    // tick when the current process has no valid sample.
    internal static AiSystemSmoothnessHealth? PreferAgentSnapshot(
        AiSystemSmoothnessHealth? candidate, DateTime utc, TimeSpan maxAge) =>
        candidate != null && IsFresh(candidate.UpdatedUtc, utc, maxAge)
            ? candidate : null;

    internal static AiSystemSmoothnessHealth? ReadAgentSnapshot(DateTime utc) =>
        PreferAgentSnapshot(Volatile.Read(ref _latest), utc,
            TimeSpan.FromMinutes(2));

    // A single FanAgent tick must use the SAME immutable CPU/RAM/disk
    // snapshot for its quiet-window decision and maintenance safety gates.
    // Missing, future-dated or stale evidence always denies new mutations.
    internal static bool TrustedQuietExperiment(
        AiSystemSmoothnessHealth? state, DateTime utc,
        int stableNormalSamples, DateTime lastPressureUtc) =>
        state is { State: "Normal", GameActive: false } &&
        // "Normal" is not sufficient if a provider publishes contradictory
        // raw CPU/RAM/disk measurements. Match owned-trial fail-closed guards.
        !AiAutomaticPriorityTrialPolicy.DangerousAfterApplying(state, utc) &&
        RecoveryAllowsExperiment(stableNormalSamples, lastPressureUtc, utc);

    internal static bool AllowsBackgroundExperiment(
        AiSystemSmoothnessHealth? currentAgentState, DateTime utc) =>
        TrustedQuietExperiment(currentAgentState, utc,
            _stableNormalSamples, _lastPressureUtc);

    public static bool AllowsBackgroundExperiment()
    {
        DateTime utc = DateTime.UtcNow;
        return AllowsBackgroundExperiment(ReadAgentSnapshot(utc), utc);
    }

    // Samples are nominally 30 seconds apart. Do not accumulate three "stable"
    // samples across long scheduler pauses or clock discontinuities.
    internal static bool IsSampleGapResetRequired(TimeSpan gap) =>
        gap < TimeSpan.Zero || gap > TimeSpan.FromSeconds(75);

    // Release the PDH query and native buffer when the Agent exits cleanly.
    public static void StopSampling() => AiLogicalCorePdhSampler.Dispose();

    public static void SampleIfDue(double cpuLoadPercent, bool gameActive)
    {
        DateTime utc = DateTime.UtcNow;
        // A total CPU telemetry outage must immediately invalidate stale
        // Normal evidence, even between the existing 30s PDH polls.
        if (!AiCpuLoadSelectionPolicy.IsValid(cpuLoadPercent))
        {
            // Schedule one immediate real sample when valid CPU evidence
            // returns; no extra polls while the provider remains unavailable.
            _lastSampleUtc = default;
            _latest = null;
            _stableNormalSamples = 0;
            _sustainedDiskBusySamples = 0;
            _sustainedDiskLatencySamples = 0;
            _sustainedLogicalCoreSamples = 0;
            _lastPressureUtc = utc;
            if (_lastState != "Unknown" || utc < _lastWrittenUtc ||
                utc - _lastWrittenUtc >= TimeSpan.FromSeconds(90))
                PublishUnavailable(utc, gameActive);
            return;
        }
        if (_lastSampleUtc != default)
        {
            TimeSpan gap = utc - _lastSampleUtc;
            if (gap >= TimeSpan.Zero && gap < TimeSpan.FromSeconds(30)) return;
            // A sleep/resume or clock correction breaks the continuity of the
            // 30-second samples. Reset quiet and sustained-pressure sequences.
            if (IsSampleGapResetRequired(gap))
            {
                _latest = null;
                _lastState = "";
                _stableNormalSamples = 0;
                _sustainedDiskBusySamples = 0;
                _sustainedDiskLatencySamples = 0;
                _sustainedLogicalCoreSamples = 0;
                _lastPressureUtc = utc;
            }
        }
        _lastSampleUtc = utc;
        try
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory) || memory.TotalPhys == 0 ||
                memory.AvailPhys > memory.TotalPhys)
            {
                // A provider failure must invalidate the previous quiet streak.
                _latest = null;
                _stableNormalSamples = 0;
                _sustainedDiskBusySamples = 0;
                _sustainedDiskLatencySamples = 0;
                _sustainedLogicalCoreSamples = 0;
                _lastPressureUtc = utc;
                PublishUnavailable(utc, gameActive);
                return;
            }
            var logicalCore = AiLogicalCorePdhSampler.TryRead();
            _sustainedLogicalCoreSamples = AiLogicalCorePressurePolicy.NextStreak(
                _sustainedLogicalCoreSamples, logicalCore?.Percent, cpuLoadPercent);
            double freeGiB = memory.AvailPhys / 1073741824d;
            double usedPercent = 100d * (1 - memory.AvailPhys / (double)memory.TotalPhys);
            // Reuse the existing PDH disk utilization counter, sampled only every
            // 30s from FanAgent. Three consecutive high readings avoid reacting to
            // momentary I/O bursts and fake 'SSD speedup' claims.
            double? diskBusy = null;
            double? diskLatencyMs = null;
            try
            {
                var disk = DiskPdhUsageService.TryReadWithLatency();
                diskBusy = disk.BusyPercent;
                diskLatencyMs = disk.LatencyMs;
            }
            catch { /* Disk counters are optional; CPU/RAM guard still works. */ }
            _sustainedDiskBusySamples = diskBusy is >= 80 and <= 100 && !gameActive
                ? Math.Min(3, _sustainedDiskBusySamples + 1) : 0;
            // 50+ ms for three samples indicates sustained I/O latency; a
            // single storage access spike is normal and not a reason to tune.
            // A disk reporting high average latency without meaningful I/O is
            // not evidence of a real user-facing regression. Require an
            // accompanying busy signal and never count missing PDH values.
            _sustainedDiskLatencySamples = ValidDiskLatencyPressure(
                diskBusy, diskLatencyMs, gameActive)
                ? Math.Min(3, _sustainedDiskLatencySamples + 1) : 0;
            string state = Evaluate(usedPercent, freeGiB, cpuLoadPercent,
                gameActive, diskBusy, _sustainedDiskBusySamples,
                diskLatencyMs, _sustainedDiskLatencySamples,
                _sustainedLogicalCoreSamples);
            UpdateRecoveryState(state, utc);
            string detail = state switch
            {
                "Memory pressure" => "Nízká volná RAM: odkládám nové výkonové experimenty.",
                "CPU pressure" => "Vysoké vytížení CPU: odkládám ladění a chráním odezvu.",
                "Single-core pressure" => $"Opakovaně přetížený logický procesor (aktuálně {logicalCore?.LogicalProcessor}: {logicalCore?.Percent:0}%): odkládám nové experimenty a chráním odezvu.",
                "Disk pressure" => "Dlouhodobě vytížené úložiště: odkládám experimenty pro zachování odezvy.",
                "Disk latency" => "Opakovaná vysoká latence SSD/HDD: odkládám výkonové experimenty.",
                "Gaming" => "Hra aktivní: bez experimentů služeb/úloh/CPU.",
                _ => "Bez zjištěného výrazného tlaku na CPU/RAM."
            };
            _latest = new(utc, Math.Round(cpuLoadPercent, 1),
                Math.Round(usedPercent, 1), Math.Round(freeGiB, 2), state, detail,
                gameActive, diskBusy.HasValue ? Math.Round(diskBusy.Value, 1) : null,
                diskLatencyMs.HasValue ? Math.Round(diskLatencyMs.Value, 1) : null,
                RecoveryRemainingSeconds(utc), logicalCore?.Percent,
                logicalCore?.LogicalProcessor ?? "", _sustainedLogicalCoreSamples);
            // Persist at most once / 90s when state is unchanged so the GUI's
            // separate process gets a current snapshot without writing per tick.
            // Commit the write timestamp only AFTER atomic persistence succeeds.
            if (state == _lastState &&
                utc >= _lastWrittenUtc &&
                utc - _lastWrittenUtc < TimeSpan.FromSeconds(90))
                return;
            Directory.CreateDirectory(LogService.Root);
            AtomicFileService.WriteText(StatusPath, JsonSerializer.Serialize(_latest));
            _lastState = state;
            _lastWrittenUtc = utc;
        }
        catch (Exception ex)
        {
            // A failed provider or persistence step must not leave the previous
            // quiet sample eligible for another autonomous experiment.
            _latest = null;
            _stableNormalSamples = 0;
            _sustainedDiskBusySamples = 0;
            _sustainedDiskLatencySamples = 0;
            _sustainedLogicalCoreSamples = 0;
            _lastPressureUtc = utc;
            PublishUnavailable(utc, gameActive);
            LogService.Error("ai-runtime", "CPU/RAM pressure sample failed", ex);
        }
    }

    // Publish an explicit unavailable state so a separate GUI cannot keep
    // presenting the previously persisted Normal snapshot after a failure.
    // Failure path only; normal sampling remains on the existing 90s cadence.
    private static void PublishUnavailable(DateTime utc, bool gameActive)
    {
        var unavailable = new AiSystemSmoothnessHealth(
            utc, 0, 0, 0, "Unknown",
            "Měření výkonu není dostupné. AI ladění je dočasně pozastaveno.",
            gameActive);
        _latest = unavailable;
        try
        {
            Directory.CreateDirectory(LogService.Root);
            AtomicFileService.WriteText(StatusPath, JsonSerializer.Serialize(unavailable));
            _lastState = "Unknown";
            _lastWrittenUtc = utc;
        }
        catch { /* Never replace the original failure with a persistence error. */ }
    }

    internal static bool ValidDiskLatencyPressure(double? busy, double? latencyMs, bool game) =>
        !game && busy is >= 5 and <= 100 &&
        latencyMs is >= 50 and <= 5000 &&
        double.IsFinite(busy.Value) && double.IsFinite(latencyMs.Value);

    private static int RecoveryRemainingSeconds(DateTime now)
    {
        if (_stableNormalSamples < 3) return 90;
        if (_lastPressureUtc == DateTime.MinValue) return 0;
        return Math.Max(0, (int)Math.Ceiling((RecoveryCooldown - (now - _lastPressureUtc)).TotalSeconds));
    }

    private static void UpdateRecoveryState(string state, DateTime utc)
    {
        if (state == "Normal")
        {
            _stableNormalSamples = Math.Min(3, _stableNormalSamples + 1);
            return;
        }
        _stableNormalSamples = 0;
        // Unreliable samples are a pause too. Require a fresh quiet window after
        // telemetry failure rather than treating missing data as recovery.
        _lastPressureUtc = utc;
    }

    internal static bool RecoveryAllowsExperiment(int quietSamples, DateTime lastPressure,
        DateTime now) =>
        quietSamples >= 3 && now >= lastPressure &&
        (lastPressure == DateTime.MinValue ||
         now - lastPressure >= RecoveryCooldown);

    // Deliberate thresholds: low free RAM OR very high occupancy prevents experiment,
    // independently from CPU load. A game is not an acceptable A/B/A test environment.
    internal static string Evaluate(double usedRamPercent, double freeGiB, double cpu,
        bool game, double? diskBusy = null, int sustainedDiskBusySamples = 0,
        double? diskLatencyMs = null, int sustainedDiskLatencySamples = 0,
        int sustainedLogicalCoreSamples = 0)
    {
        // GameActive is independent telemetry, not permission to conceal
        // real memory/CPU pressure. Previously "Gaming" masked even 96.4%
        // RAM / 0.57 GiB free and reset no safety/recovery streak. Preserve
        // Gaming for a healthy game; disk heuristics still avoid game spikes.
        if (!double.IsFinite(usedRamPercent) || !double.IsFinite(freeGiB) ||
            !double.IsFinite(cpu) || usedRamPercent < 0 || usedRamPercent > 100 ||
            freeGiB < 0 || cpu < 0 || cpu > 100)
            return "Unknown";
        if (freeGiB < 1.5 || (usedRamPercent >= 92 && freeGiB < 4))
            return "Memory pressure";
        if (cpu >= 75) return "CPU pressure";
        if (AiLogicalCorePressurePolicy.IsSustained(sustainedLogicalCoreSamples))
            return "Single-core pressure";
        if (game) return "Gaming";
        if (diskBusy is >= 80 and <= 100 && sustainedDiskBusySamples >= 3)
            return "Disk pressure";
        if (ValidDiskLatencyPressure(diskBusy, diskLatencyMs, game) &&
            sustainedDiskLatencySamples >= 3)
            return "Disk latency";
        return "Normal";
    }

    internal static string RunOfflineSmoke()
    {
        if (Evaluate(40, 10, 15, false) != "Normal" ||
            Evaluate(94, 0.8, 15, false) != "Memory pressure" ||
            Evaluate(70, 8, 80, false) != "CPU pressure" ||
            Evaluate(30, 14, 10, true) != "Gaming" ||
            Evaluate(96.4, 0.57, 33.4, true) != "Memory pressure" ||
            Evaluate(70, 8, 80, true) != "CPU pressure" ||
            Evaluate(double.NaN, 1, 10, true) != "Unknown" ||
            Evaluate(30, 14, 10, true, 98, 3, 65, 3) != "Gaming" ||
            Evaluate(double.NaN, 1, 10, false) != "Unknown" ||
            Evaluate(40, 10, 15, false, 95, 1) != "Normal" ||
            Evaluate(40, 10, 15, false, 95, 3) != "Disk pressure" ||
            Evaluate(40, 10, 15, false, 101, 3) != "Normal" ||
            Evaluate(40, 10, 15, false, 10, 0, 85, 1) != "Normal" ||
            Evaluate(40, 10, 15, false, 10, 0, 85, 3) != "Disk latency" ||
            Evaluate(40, 10, 15, false, 0, 0, 85, 3) != "Normal" ||
            Evaluate(40, 10, 15, false, null, 0, 85, 3) != "Normal" ||
            ValidDiskLatencyPressure(0, 85, false) ||
            ValidDiskLatencyPressure(null, 85, false) ||
            !ValidDiskLatencyPressure(10, 85, false) ||
            RecoveryAllowsExperiment(1, DateTime.MinValue, DateTime.UtcNow) ||
            RecoveryAllowsExperiment(3, DateTime.UtcNow, DateTime.UtcNow) ||
            !RecoveryAllowsExperiment(3, DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow) ||
            RecoveryAllowsExperiment(3, DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow) ||
            IsFresh(DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow, TimeSpan.FromMinutes(6)) ||
            IsFresh(DateTime.UtcNow.AddMinutes(-7), DateTime.UtcNow, TimeSpan.FromMinutes(2)) ||
            IsFresh(DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow, TimeSpan.FromMinutes(2)) ||
            !IsSampleGapResetRequired(TimeSpan.FromMinutes(3)) ||
            IsSampleGapResetRequired(TimeSpan.FromSeconds(30)) ||
            !IsSampleGapResetRequired(TimeSpan.FromSeconds(76)) ||
            IsSampleGapResetRequired(TimeSpan.FromSeconds(75)))
            throw new InvalidOperationException("AI smoothness pressure policy regression.");
        return "AI_SMOOTHNESS_POLICY_OK normal=1 memory=1 cpu=1 gaming=1 unknown=1 disk=stable-only";
    }
}
