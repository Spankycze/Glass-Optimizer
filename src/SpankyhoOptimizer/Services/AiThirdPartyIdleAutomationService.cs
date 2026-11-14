using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal sealed partial class AiThirdPartyIdleAutomationService
{
    // This exception is reserved exclusively for admission checks that run
    // BEFORE any Windows write. Only these failures may safely discard the
    // write-ahead owner; unknown/partial mutations must keep recovery.
    private sealed class PrewriteAdmissionException(string message)
        : InvalidOperationException(message);

    private sealed record Owner(
        string Kind, string Id, string AppKey, DateTime AppliedUtc,
        bool OriginalEnabled, bool OriginallyRunning,
        string StartupName = "", string StartupCommand = "",
        string StartupRunPath = "", int StartupView = 0, bool Pending = true,
        double? BaselineCpuPercent = null, double? BaselineRamMiB = null,
        int FeedbackSamples = 0, double SumCpuPercent = 0, double SumRamMiB = 0,
        DateTime LastFeedbackSampleUtc = default,
        double? MinCpuPercent = null, double? MaxCpuPercent = null,
        double? MinRamMiB = null, double? MaxRamMiB = null,
        int? OriginalServiceStartMode = null);
    private sealed record FeedbackOutcome(
        DateTime UpdatedUtc, string Verdict, int Samples,
        double? CpuDropPercentPoints = null, double? AvailableRamGainMiB = null,
        string AppKey = "");
    private sealed record ReferenceTrial(
        string Id, string AppKey, DateTime RestoredUtc,
        double BaselineCpu, double BaselineRamMiB,
        double CandidateCpu, double CandidateRamMiB,
        int Samples = 0, double CpuSum = 0, double RamSum = 0,
        DateTime LastSampleUtc = default,
        double? MinCpu = null, double? MaxCpu = null,
        double? MinRamMiB = null, double? MaxRamMiB = null);
    private sealed record Journal(
        Dictionary<string, Owner> Owned,
        Dictionary<string, FeedbackOutcome>? Feedback = null,
        Dictionary<string, ReferenceTrial>? References = null);
    private static readonly string JournalPath =
        Path.Combine(LogService.Root, "ai-third-party-idle-ownership.json");
    // Strict UTF-8 decoder is stateless for these one-shot GetString calls;
    // do not allocate a new Encoding on every journal load or UI visit.
    private static readonly UTF8Encoding StrictJournalUtf8 = new(false, true);
    private readonly Dictionary<string, Owner> _owned =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FeedbackOutcome> _feedback =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReferenceTrial> _references =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _absentSince =
        new(StringComparer.OrdinalIgnoreCase);
    // Only a few reusable samples from existing cached FanAgent telemetry.
    // This is local provisional evidence, never a new native polling timer.
    private readonly Queue<(DateTime AtUtc, double Cpu, double RamMiB)>
        _steamPreStopSamples = new();
    private readonly Dictionary<string, DateTime> _externallyOverriddenUntil =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _journalHealthy = true;
    private bool _learningRepairPending;
    private DateTime _lastJournalErrorUtc;
    private DateTime _lastScanUtc = DateTime.MinValue;
    // Last validated journal identity. Poll only metadata between ordinary
    // scans to discover a second RuntimeHost's newly owned resources.
    private bool _journalStampKnown;
    private bool _journalStampExists;
    private long _journalStampLength;
    private DateTime _journalStampWriteUtc;
    // Digest is only evaluated during a validated load or a real journal
    // commit, never during the periodic 10-second metadata-only probe.
    private byte[]? _journalDigest;
    private DateTime _lastJournalMetadataProbeUtc;
    private DateTime _lastUrgentProbeUtc;
    private DateTime _lastObservationUtc;
    // GUI and RuntimeHost are separate processes. Keep only the last
    // validated preference object so a changed KEEP choice can break an
    // idle window without waiting for the full Windows inventory.
    private UserFeatureProfile? _lastObservedFeatureProfile;
    private DateTime _lastFailedOffRestoreUtc;
    private bool? _lastAiEnabled;
    private bool? _lastServicesEnabled;
    private bool? _lastTasksEnabled;

    // Low-overhead operational measurements, one aggregate report every
    // 15 minutes. Never create a sampler thread or log on individual ticks.
    private readonly object _perfGate = new();
    private DateTime _perfReportAtUtc = DateTime.UtcNow;
    private long _perfTicks, _perfWallTicks, _perfMaxTicks;
    private long _perfLeaseAcquired, _perfStartupScans, _perfAppScans;
    // No extra probing: count policy decisions made by existing cycles so
    // real users can verify avoided native HKCU/Firefox enumerations.
    private long _perfStartupSkips, _perfFirefoxScans, _perfFirefoxSkips;
    private long _perfAppInventorySkips;
    // Timings are Stopwatch deltas from EXISTING native reads, not extra
    // scans or CPU sampling. Interlocked counters need no per-tick objects.
    private long _perfJournalLoadTicks, _perfJournalLoadMaxTicks;
    private long _perfStartupLoadTicks, _perfStartupLoadMaxTicks;
    private long _perfAppLoadTicks, _perfAppLoadMaxTicks;
    private double _perfLastCpuTotalSec = double.NaN;
    // Monotonic CPU interval denominator: wall-clock adjustments (time sync
    // or daylight saving) cannot fabricate CPU spikes or negative usage.
    private long _perfLastCpuClockTicks;

    // Read only the tiny persistent outcome summary on navigation to the AI
    // page; no new background poll and no Windows service/task inspection.
    internal static AiThirdPartyFeedbackView[] ReadFeedbackForUi()
    {
        try
        {
            var file = new FileInfo(JournalPath);
            if (!file.Exists || file.Length is <= 0 or > 262144)
                return Array.Empty<AiThirdPartyFeedbackView>();
            using var stream = File.OpenRead(JournalPath);
            Journal? journal = DeserializeJournalStrict(
                ReadBoundedJournalText(stream));
            return SelectTrustedFeedbackForUi(journal, DateTime.UtcNow);
        }
        catch (Exception)
        {
            // An in-progress journal swap must not break AI navigation.
            return Array.Empty<AiThirdPartyFeedbackView>();
        }
    }

    // The UI is read-only, but untrusted auxiliary feedback should not
    // appear as a valid optimization result. Reuse precisely the same
    // identity validation as durable learning recovery, without disk writes.
    private static AiThirdPartyFeedbackView[] SelectTrustedFeedbackForUi(
        Journal? journal, DateTime now)
    {
        if (!HasValidOwnedJournal(journal?.Owned) ||
            journal.Feedback is null || journal.Feedback.Count is 0 or > 256)
            return Array.Empty<AiThirdPartyFeedbackView>();

        // UI shows at most three records: a stable bounded top-3 scan
        // avoids LINQ filtering, sorting up to 256 entries, DistinctBy's
        // transient HashSet and several iterators on AI navigation.
        // Keep the old ordering: newest first, equal-time ties in original
        // journal iteration order, distinct resource identity ignoring case.
        var newest = new KeyValuePair<string, FeedbackOutcome>[3];
        int count = 0;
        foreach (var candidate in journal.Feedback)
        {
            if (!IsValidPersistedFeedback(candidate.Key, candidate.Value) ||
                candidate.Value.UpdatedUtc > now)
                continue;
            // The optional startup can remain AI-owned across future logins.
            // Do not hide its honest "measure next startup" feedback after
            // fourteen days while the exact durable owner is still active.
            // Ordinary historical outcomes and stale completed trials still
            // expire, without any extra process/registry probe on navigation.
            if (now - candidate.Value.UpdatedUtc > TimeSpan.FromDays(14) &&
                !(candidate.Value.Verdict == "deferred-until-next-startup" &&
                  journal.Owned!.TryGetValue(candidate.Key, out Owner? activeOwner) &&
                  activeOwner.Kind == "startup" && !activeOwner.Pending &&
                  string.Equals(activeOwner.AppKey, candidate.Value.AppKey,
                      StringComparison.OrdinalIgnoreCase)))
                continue;
            int duplicate = -1;
            for (int i = 0; i < count; i++)
            {
                if (candidate.Key.Equals(newest[i].Key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    duplicate = i;
                    break;
                }
            }
            if (duplicate >= 0)
            {
                // Earlier entries win an equal-time tie, like LINQ's
                // stable OrderByDescending followed by DistinctBy.
                if (candidate.Value.UpdatedUtc <= newest[duplicate].Value.UpdatedUtc)
                    continue;
                for (int i = duplicate; i + 1 < count; i++)
                    newest[i] = newest[i + 1];
                count--;
            }
            int insert = 0;
            while (insert < count &&
                   candidate.Value.UpdatedUtc <= newest[insert].Value.UpdatedUtc)
                insert++;
            if (insert >= newest.Length) continue;
            if (count < newest.Length) count++;
            for (int i = count - 1; i > insert; i--)
                newest[i] = newest[i - 1];
            newest[insert] = candidate;
        }
        if (count == 0) return Array.Empty<AiThirdPartyFeedbackView>();
        var result = new AiThirdPartyFeedbackView[count];
        for (int i = 0; i < count; i++)
        {
            var entry = newest[i];
            int split = entry.Key.IndexOf('|');
            string kind = entry.Key[..split];
            string id = entry.Key[(split + 1)..];
            var value = entry.Value;
            result[i] = new AiThirdPartyFeedbackView(
                kind, id, value.AppKey, value.UpdatedUtc, value.Verdict,
                AiThirdPartyIdlePolicy.DescribeFeedback(value.Verdict,
                    value.Samples, value.CpuDropPercentPoints,
                    value.AvailableRamGainMiB));
        }
        return result;
    }

    // Stopwatch uses monotonic ticks; no wall-clock skew or fake
    // whole-system FPS/CPU savings. The per-tick path only measures time.
    internal static double ElapsedMilliseconds(long stopwatchTicks) =>
        stopwatchTicks >= 0
            ? stopwatchTicks * 1000.0 / Stopwatch.Frequency
            : 0;

    // Monotonic timing aggregation for one phase of an already-required
    // inventory read. Safe with concurrent RuntimeHost callbacks.
    private static void RecordPhaseTicks(
        ref long totalTicks, ref long maxTicks, long elapsedTicks)
    {
        if (elapsedTicks < 0) return;
        Interlocked.Add(ref totalTicks, elapsedTicks);
        long prior = Volatile.Read(ref maxTicks);
        while (elapsedTicks > prior)
        {
            long replaced = Interlocked.CompareExchange(
                ref maxTicks, elapsedTicks, prior);
            if (replaced == prior) break;
            prior = replaced;
        }
    }

    private static (double DeltaSec, double AllCoresPct) CpuIntervalUsage(
        double previousCpuSec, double currentCpuSec, double wallSeconds,
        int logicalProcessors)
    {
        if (!double.IsFinite(previousCpuSec) ||
            !double.IsFinite(currentCpuSec) || previousCpuSec < 0 ||
            currentCpuSec < previousCpuSec ||
            !double.IsFinite(wallSeconds) || wallSeconds <= 0 ||
            logicalProcessors <= 0)
            return (double.NaN, double.NaN);
        double delta = currentCpuSec - previousCpuSec;
        return (delta, Math.Clamp(
            100.0 * delta / (wallSeconds * logicalProcessors), 0, 100));
    }

    internal static bool RunPhaseTimingSmoke()
    {
        long total = 0, maximum = 0;
        RecordPhaseTicks(ref total, ref maximum, -1);
        RecordPhaseTicks(ref total, ref maximum, 100);
        RecordPhaseTicks(ref total, ref maximum, 50);
        RecordPhaseTicks(ref total, ref maximum, 250);
        RecordPhaseTicks(ref total, ref maximum, 10);
        var usage = CpuIntervalUsage(12, 14, 100, 8);
        return total == 410 && maximum == 250 &&
            Math.Abs(ElapsedMilliseconds(Stopwatch.Frequency) - 1000) < 0.000001 &&
            Math.Abs(usage.DeltaSec - 2) < 0.000001 &&
            Math.Abs(usage.AllCoresPct - 0.25) < 0.000001 &&
            double.IsNaN(CpuIntervalUsage(double.NaN, 14, 100, 8).DeltaSec) &&
            double.IsNaN(CpuIntervalUsage(14, 12, 100, 8).AllCoresPct) &&
            double.IsNaN(CpuIntervalUsage(12, 14, 0, 8).DeltaSec) &&
            double.IsNaN(CpuIntervalUsage(12, 14, 100, 0).DeltaSec);
    }

    // Performance logs use invariant numeric tokens even when the machine
    // runs with a Czech decimal comma. One log per 15min, no hot-path cost.
    private static string PerfNumber(double value, string format = "0.000") =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string PerfNumberOrNa(double value) =>
        double.IsFinite(value) ? PerfNumber(value) : "na";

    // A phase that did NOT run in this reporting interval is unavailable,
    // not a fabricated zero-millisecond measurement.
    private static string PhaseAverageOrNa(long totalTicks, long count) =>
        count > 0 ? PerfNumber(ElapsedMilliseconds(totalTicks) / count) : "na";

    private static string PhaseMaximumOrNa(long maxTicks, long count) =>
        count > 0 ? PerfNumber(ElapsedMilliseconds(maxTicks)) : "na";

    private static double MonotonicElapsedSeconds(long previous, long current) =>
        previous > 0 && current > previous
            ? (double)(current - previous) / Stopwatch.Frequency
            : double.NaN;

    internal static bool RunTelemetryCultureSmoke() =>
        PerfNumber(1.25) == "1.250" &&
        PerfNumber(1.26, "0.0") == "1.3" &&
        PerfNumberOrNa(double.NaN) == "na" &&
        PerfNumberOrNa(double.PositiveInfinity) == "na" &&
        PhaseAverageOrNa(0, 0) == "na" &&
        PhaseMaximumOrNa(0, 0) == "na" &&
        PhaseAverageOrNa(Stopwatch.Frequency, 2) == "500.000" &&
        PhaseMaximumOrNa(Stopwatch.Frequency, 1) == "1000.000" &&
        double.IsNaN(MonotonicElapsedSeconds(0, Stopwatch.Frequency)) &&
        double.IsNaN(MonotonicElapsedSeconds(100, 99)) &&
        Math.Abs(MonotonicElapsedSeconds(
            Stopwatch.Frequency, Stopwatch.Frequency * 3) - 2) < 1e-7 &&
        double.IsFinite(ElapsedMilliseconds(Stopwatch.Frequency));

    private void RecordTickPerformance(long elapsedTicks)
    {
        // This is OBSERVABILITY, never part of a recovery transaction.
        // Even logger or process metric failures must not mask the original
        // exception/cancellation from TickCoreAsync's finally block.
        try { RecordTickPerformanceCore(elapsedTicks); }
        catch { /* Preserve the original AI outcome without noisy retries. */ }
    }

    private void RecordTickPerformanceCore(long elapsedTicks)
    {
        if (elapsedTicks < 0) return;
        long count, totalTicks, maxTicks, leases, startupScans, appScans;
        long startupSkips, firefoxScans, firefoxSkips, appInventorySkips;
        long journalTicks, journalMax, startupTicks, startupMax, appTicks, appMax;
        DateTime now = DateTime.UtcNow;
        lock (_perfGate)
        {
            _perfTicks++;
            _perfWallTicks += elapsedTicks;
            _perfMaxTicks = Math.Max(_perfMaxTicks, elapsedTicks);
            if (now - _perfReportAtUtc < TimeSpan.FromMinutes(15))
                return;
            count = _perfTicks;
            totalTicks = _perfWallTicks;
            maxTicks = _perfMaxTicks;
            _perfTicks = _perfWallTicks = _perfMaxTicks = 0;
            _perfReportAtUtc = now;
            leases = Interlocked.Exchange(ref _perfLeaseAcquired, 0);
            startupScans = Interlocked.Exchange(ref _perfStartupScans, 0);
            appScans = Interlocked.Exchange(ref _perfAppScans, 0);
            startupSkips = Interlocked.Exchange(ref _perfStartupSkips, 0);
            firefoxScans = Interlocked.Exchange(ref _perfFirefoxScans, 0);
            firefoxSkips = Interlocked.Exchange(ref _perfFirefoxSkips, 0);
            appInventorySkips = Interlocked.Exchange(ref _perfAppInventorySkips, 0);
            journalTicks = Interlocked.Exchange(ref _perfJournalLoadTicks, 0);
            journalMax = Interlocked.Exchange(ref _perfJournalLoadMaxTicks, 0);
            startupTicks = Interlocked.Exchange(ref _perfStartupLoadTicks, 0);
            startupMax = Interlocked.Exchange(ref _perfStartupLoadMaxTicks, 0);
            appTicks = Interlocked.Exchange(ref _perfAppLoadTicks, 0);
            appMax = Interlocked.Exchange(ref _perfAppLoadMaxTicks, 0);
        }

        // One native process snapshot per reporting interval, not per tick.
        // CPU is process-cumulative CPU time, NOT causal optimisation gain.
        try
        {
            using var process = Process.GetCurrentProcess();
            double cpuTotalSec = process.TotalProcessorTime.TotalSeconds;
            double cpuDeltaSec, cpuAllCoresPct;
            long cpuClock = Stopwatch.GetTimestamp();
            lock (_perfGate)
            {
                double elapsedSec = MonotonicElapsedSeconds(
                    _perfLastCpuClockTicks, cpuClock);
                var interval = CpuIntervalUsage(
                    _perfLastCpuTotalSec, cpuTotalSec, elapsedSec,
                    Environment.ProcessorCount);
                cpuDeltaSec = interval.DeltaSec;
                cpuAllCoresPct = interval.AllCoresPct;
                _perfLastCpuTotalSec = cpuTotalSec;
                _perfLastCpuClockTicks = cpuClock;
            }
            // ai-system's high-frequency module INFO is intentionally
            // disabled in normal operation. This single 15-minute summary
            // must instead use the ordinary always-on, bounded async log.
            LogService.Info(
                $"AI_APP_IDLE_RUNTIME_PERF pid={process.Id} ticks={count} " +
                $"avgMs={PerfNumber(ElapsedMilliseconds(totalTicks) / Math.Max(1, count))} " +
                $"maxMs={PerfNumber(ElapsedMilliseconds(maxTicks))} " +
                $"leaseAcquired={leases} startupScans={startupScans} " +
                $"appScans={appScans} appSkipped={appInventorySkips} startupSkipped={startupSkips} " +
                $"firefoxChecks={firefoxScans} firefoxSkipped={firefoxSkips} " +
                $"journalAvgMs={PhaseAverageOrNa(journalTicks, leases)} " +
                $"journalMaxMs={PhaseMaximumOrNa(journalMax, leases)} " +
                $"startupAvgMs={PhaseAverageOrNa(startupTicks, startupScans)} " +
                $"startupMaxMs={PhaseMaximumOrNa(startupMax, startupScans)} " +
                $"appInventoryAvgMs={PhaseAverageOrNa(appTicks, appScans)} " +
                $"appInventoryMaxMs={PhaseMaximumOrNa(appMax, appScans)} " +
                $"cpuTotalSec={PerfNumber(cpuTotalSec, "0.0")} " +
                $"cpuIntervalSec={PerfNumberOrNa(cpuDeltaSec)} " +
                $"cpuAvgAllCoresPct={PerfNumberOrNa(cpuAllCoresPct)} " +
                $"logicalProcessors={Environment.ProcessorCount} " +
                $"workingSetMiB={PerfNumber(process.WorkingSet64 / 1048576.0, "0.0")} " +
                $"privateMiB={PerfNumber(process.PrivateMemorySize64 / 1048576.0, "0.0")} " +
                $"handles={process.HandleCount} threads={process.Threads.Count} " +
                "causalBenefitUnverified=true");
        }
        catch (Exception)
        {
            try
            {
                LogService.Info(
                    $"AI_APP_IDLE_RUNTIME_PERF ticks={count} " +
                    $"avgMs={PerfNumber(ElapsedMilliseconds(totalTicks) / Math.Max(1, count))} " +
                    $"maxMs={PerfNumber(ElapsedMilliseconds(maxTicks))} " +
                    $"leaseAcquired={leases} startupScans={startupScans} " +
                    $"appScans={appScans} appSkipped={appInventorySkips} startupSkipped={startupSkips} " +
                    $"firefoxChecks={firefoxScans} firefoxSkipped={firefoxSkips} " +
                    $"journalAvgMs={PhaseAverageOrNa(journalTicks, leases)} " +
                    $"journalMaxMs={PhaseMaximumOrNa(journalMax, leases)} " +
                    $"startupAvgMs={PhaseAverageOrNa(startupTicks, startupScans)} " +
                    $"startupMaxMs={PhaseMaximumOrNa(startupMax, startupScans)} " +
                    $"appInventoryAvgMs={PhaseAverageOrNa(appTicks, appScans)} " +
                    $"appInventoryMaxMs={PhaseMaximumOrNa(appMax, appScans)} " +
                    "nativeProcessMetricsUnavailable=true");
            }
            catch { /* Measurement must never block normal AI recovery. */ }
        }
    }
}
