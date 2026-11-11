using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Automatically gathers bounded, READ-ONLY Windows scheduling evidence when the
/// FanAgent already observes relevant CPU pressure. It never changes CPU Sets,
/// affinity, timer resolution, process priorities, RAM, or OS settings.
/// The autonomous priority engine can defer a NEW trial when severe wake jitter
/// was confirmed. No resident thread, polling service or new process.
/// </summary>
internal static class AiSchedulerAutoEvidenceService
{
    internal readonly record struct Evidence(
        DateTime CapturedUtc, int CpuSetCount, int ProcessorGroups,
        int EfficiencyClasses, double WakeJitterP95Ms, int WakeSamples,
        bool HighResolutionTimerAvailable);

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(15);
    // Maximum two early retries, then full 15-minute cadence on persistent failure.
    private static readonly TimeSpan FailedProbeRetryInterval = TimeSpan.FromMinutes(1);
    private const int MaximumQuickRetries = 2;
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(20);
    private const int WakeSampleCount = 16;
    private const double RequestedWakeMs = 5;
    private const double SevereWakeJitterMs = 20;
    private const double WakeProbeBudgetMs = 250;
    private const double MaxQueuedProbeDelaySeconds = 2;

    private static long _lastProbeTimestamp;
    private static int _running;
    private static Evidence _last;
    private static readonly object EvidenceLock = new();
    private static bool _admissionOpen;
    private static int _admissionGeneration;
    private static int _consecutiveIncompleteProbes;

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(
        [Out] byte[]? information, uint bufferLength, out uint returnedLength,
        nint process, uint flags);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerEx(
        nint securityAttributes, string? name, uint flags, uint desiredAccess);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        nint timer, ref long dueTime, int period, nint completionRoutine,
        nint completionArg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    internal static bool IsEligible(bool enabled, double cpuLoad, bool gameActive) =>
        enabled && !gameActive && double.IsFinite(cpuLoad) &&
        cpuLoad is >= 25 and <= 100;

    // A queued ThreadPool work item is not a licence to probe minutes later,
    // when the workload may have changed. Clock remains monotonic.
    internal static bool IsFreshQueuedProbe(
        long queuedAt, long startedAt, long frequency) =>
        queuedAt > 0 && startedAt >= queuedAt && frequency > 0 &&
        (double)(startedAt - queuedAt) / frequency <= MaxQueuedProbeDelaySeconds;

    internal static bool ShouldProbe(
        double cpuLoad, bool gameActive, long lastTimestamp, long currentTimestamp,
        long frequency, int consecutiveIncompleteProbes = 0)
    {
        if (!IsEligible(true, cpuLoad, gameActive) ||
            currentTimestamp <= 0 || frequency <= 0)
            return false;
        if (lastTimestamp <= 0 || currentTimestamp < lastTimestamp)
            return true;
        // Retry unavailable/partial native evidence promptly, without creating
        // an unbounded polling loop when Windows does not support the API.
        TimeSpan cadence = consecutiveIncompleteProbes is > 0 and <= MaximumQuickRetries
            ? FailedProbeRetryInterval : ProbeInterval;
        return (double)(currentTimestamp - lastTimestamp) / frequency >=
            cadence.TotalSeconds;
    }

    internal static bool SevereJitter(Evidence evidence, DateTime nowUtc)
    {
        return evidence.CapturedUtc != default &&
            nowUtc >= evidence.CapturedUtc &&
            nowUtc - evidence.CapturedUtc <= EvidenceLifetime &&
            HasCompleteNativeEvidence(evidence) &&
            evidence.WakeJitterP95Ms >= SevereWakeJitterMs;
    }

    // Any failed/partial probe must not publish stale or misleading evidence.
    // Unsupported native APIs also count as incomplete and are rate-limited.
    internal static bool HasCompleteNativeEvidence(Evidence evidence) =>
        evidence.CapturedUtc != default && evidence.CpuSetCount > 0 &&
        evidence.ProcessorGroups > 0 && evidence.EfficiencyClasses > 0 &&
        evidence.HighResolutionTimerAvailable && evidence.WakeSamples == WakeSampleCount &&
        double.IsFinite(evidence.WakeJitterP95Ms) && evidence.WakeJitterP95Ms >= 0;

    // B and A2 already under way always retain their original journal/rollback
    // path. Only a NEW B-phase candidate may be deferred by severe jitter.
    internal static bool ShouldDeferNewPriorityTrial(
        bool ownedOrRestoredReference, Evidence evidence, DateTime nowUtc) =>
        !ownedOrRestoredReference && SevereJitter(evidence, nowUtc);

    // Fail open on unknown evidence, unsupported APIs, or a clock anomaly.
    internal static bool AllowsNewPriorityTrial(DateTime nowUtc)
    {
        Evidence evidence;
        lock (EvidenceLock) evidence = _last;
        return !ShouldDeferNewPriorityTrial(false, evidence, nowUtc);
    }

    internal static Evidence Latest
    {
        get
        {
            lock (EvidenceLock) return _last;
        }
    }

    // Generation changes on every admission transition. A queued probe
    // never borrows permission from a later game/AI/CPU workload.
    private static bool IsCurrentAdmission(int generation)
    {
        lock (EvidenceLock)
            return _admissionOpen && generation == _admissionGeneration;
    }

    // Called from the existing FanAgent tick even when AI is OFF, so an
    // in-flight sample can be invalidated without a new timer or observer.
    internal static void QueueIfDue(bool enabled, double cpuLoad, bool gameActive)
    {
        bool admitted = IsEligible(enabled, cpuLoad, gameActive);
        int generation;
        lock (EvidenceLock)
        {
            if (_admissionOpen != admitted)
            {
                _admissionOpen = admitted;
                _admissionGeneration++;
                if (!admitted)
                    _last = default;
            }
            generation = _admissionGeneration;
        }
        if (!admitted)
            return;
        long now = Stopwatch.GetTimestamp();
        if (!ShouldProbe(cpuLoad, gameActive,
                Interlocked.Read(ref _lastProbeTimestamp), now, Stopwatch.Frequency,
                Volatile.Read(ref _consecutiveIncompleteProbes)) ||
            Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;
        Interlocked.Exchange(ref _lastProbeTimestamp, now);
        try
        {
            _ = Task.Run(() =>
            {
                Evidence result = default;
                try
                {
                    if (IsFreshQueuedProbe(now, Stopwatch.GetTimestamp(),
                            Stopwatch.Frequency) && IsCurrentAdmission(generation))
                        result = Probe(() => IsCurrentAdmission(generation));
                }
                catch (Exception)
                {
                    // Unsupported API or transient failure is optional.
                    // Do not add per-tick production diagnostic overhead.
                }
                finally
                {
                    lock (EvidenceLock)
                    {
                        if (_admissionOpen && generation == _admissionGeneration)
                        {
                            if (HasCompleteNativeEvidence(result))
                            {
                                _last = result;
                                _consecutiveIncompleteProbes = 0;
                            }
                            else
                            {
                                // Never keep an old severe-jitter veto after a
                                // more recent probe could not substantiate it.
                                _last = default;
                                _consecutiveIncompleteProbes = Math.Min(
                                    MaximumQuickRetries + 1,
                                    _consecutiveIncompleteProbes + 1);
                            }
                        }
                    }
                    Volatile.Write(ref _running, 0);
                }
            });
        }
        catch (Exception)
        {
            Volatile.Write(ref _running, 0);
        }
    }

    internal static Evidence Probe(Func<bool>? mayContinue = null)
    {
        if (mayContinue is not null && !mayContinue())
            return default;
        (int count, int groups, int efficiencies) = ReadCpuSets();
        if (mayContinue is not null && !mayContinue())
            return default;
        (double p95, int samples, bool highRes) = MeasureWakeLatency(mayContinue);
        if (mayContinue is not null && !mayContinue())
            return default;
        return new Evidence(DateTime.UtcNow, count, groups, efficiencies,
            p95, samples, highRes);
    }

    private static (int Count, int Groups, int Efficiencies) ReadCpuSets()
    {
        if (!GetSystemCpuSetInformation(null, 0, out uint length, 0, 0) &&
            Marshal.GetLastWin32Error() != 122)
            return (0, 0, 0);
        if (length < 32 || length > 256 * 1024)
            return (0, 0, 0);
        var bytes = new byte[length];
        if (!GetSystemCpuSetInformation(bytes, (uint)bytes.Length,
                out uint actual, 0, 0) || actual > bytes.Length)
            return (0, 0, 0);
        return ParseCpuSets(bytes.AsSpan(0, (int)actual));
    }

    // SYSTEM_CPU_SET_INFORMATION: variable Size; Type=CpuSet(0) uses
    // Id at offset8, Group at12, EfficiencyClass at18. Never use raw IDs
    // as affinity masks and never assume only one processor group.
    internal static (int Count, int Groups, int Efficiencies) ParseCpuSets(
        ReadOnlySpan<byte> buffer)
    {
        var groups = new HashSet<ushort>();
        var efficiencies = new HashSet<byte>();
        int count = 0;
        int offset = 0;
        while (offset <= buffer.Length - 8)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + 4));
            if (size < 8 || size > buffer.Length - offset)
                return (0, 0, 0);
            if (type == 0)
            {
                if (size < 24)
                    return (0, 0, 0);
                groups.Add(BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 12)));
                efficiencies.Add(buffer[offset + 18]);
                count++;
            }
            offset += (int)size;
        }
        return offset == buffer.Length ? (count, groups.Count, efficiencies.Count)
            : (0, 0, 0);
    }

    // Linear-interpolated sample p95 (R7): with 16 samples the old
    // nearest-rank method selected MAX and treated one isolated outlier as
    // sustained severe jitter. Two large tail samples remain significant.
    internal static double Percentile95Sorted(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
            return double.NaN;
        double index = (sorted.Count - 1) * 0.95;
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        double left = sorted[lower];
        double right = sorted[upper];
        if (!double.IsFinite(left) || !double.IsFinite(right) || right < left)
            return double.NaN;
        return left + (right - left) * (index - lower);
    }

    internal static bool WithinWakeProbeBudget(double elapsedMs) =>
        double.IsFinite(elapsedMs) && elapsedMs >= 0 && elapsedMs < WakeProbeBudgetMs;

    internal static bool HasCompleteBoundedWakeEvidence(int samples, double elapsedMs) =>
        samples == WakeSampleCount && WithinWakeProbeBudget(elapsedMs);

    private static (double P95Ms, int Samples, bool HighResolution) MeasureWakeLatency(
        Func<bool>? mayContinue)
    {
        // TIMER_MODIFY_STATE | SYNCHRONIZE = 0x00100002: least required access.
        // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2; no timer resolution tweak.
        nint timer = CreateWaitableTimerEx(0, null, 0x2, 0x00100002);
        if (timer == 0)
            return (0, 0, false);
        try
        {
            var jitter = new List<double>(WakeSampleCount);
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < WakeSampleCount; i++)
            {
                // Bound total native wait time during abnormal OS scheduling
                // pressure; incomplete evidence can never block AI mutations.
                if (!WithinWakeProbeBudget(Stopwatch.GetElapsedTime(started).TotalMilliseconds) ||
                    mayContinue is not null && !mayContinue())
                    break;
                // Negative due time is relative, in 100ns ticks.
                long due = -50_000;
                long before = Stopwatch.GetTimestamp();
                if (!SetWaitableTimer(timer, ref due, 0, 0, 0, false))
                    break;
                if (WaitForSingleObject(timer, 60) != 0)
                    break;
                if (mayContinue is not null && !mayContinue())
                    break;
                double elapsedMs = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                if (!double.IsFinite(elapsedMs) || elapsedMs < 0 || elapsedMs >= 60)
                    break;
                jitter.Add(Math.Max(0, elapsedMs - RequestedWakeMs));
            }
            if (!HasCompleteBoundedWakeEvidence(jitter.Count,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds))
                // A completed but over-budget sample is NOT 16 valid samples.
                return (0, jitter.Count == WakeSampleCount ? 0 : jitter.Count, true);
            jitter.Sort();
            return (Percentile95Sorted(jitter), jitter.Count, true);
        }
        finally
        {
            _ = CloseHandle(timer);
        }
    }
}
