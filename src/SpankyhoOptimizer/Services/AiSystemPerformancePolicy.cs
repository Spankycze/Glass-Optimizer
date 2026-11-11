using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record AiSystemPerformanceSample(DateTime Utc, double CpuPercent, double UsedMemoryMb,
    double SchedulerDelayMs, double FixedWorkMs, string WorkloadKey, double? TemperatureC,
    bool Valid, string Reason, string ActiveComponents = "", double WorkloadCpuPercent = double.NaN);

public sealed record AiPerformanceVerdict(bool Accepted, string Reason, double WorkImprovementPercent);

public static class AiSystemPerformancePolicy
{
    public const int SamplesPerWindow = 6;

    public static bool IsMeasuredCandidate(TweakDefinition tweak) => tweak.Id is
        "perf.synthetic" or
        "sys.inventory" or "sys.ait" or
        "ui.thumbcache" or "ui.nonetworkthumbsdb" or "ui.syncpromo" or "privacy.explorerpromo" or
        "ui.searchfeed" or "ui.searchhistory" or "ui.searchmsa" or "ui.searchaad" or
        "ui.searchlocation" or "privacy.websearch" or "privacy.searchhl" or "privacy.cloudsearch" or
        "ui.startrecommend" or "ui.startaccount" or "privacy.consumer" or "privacy.tips" or
        "privacy.tips2" or "privacy.tips3" or "privacy.spotlightstart" or "privacy.welcome";

    public static string CandidateDeferredReason(TweakDefinition tweak, string components)
    {
        if (tweak.Id is "sys.gamedvr" or "sys.capture")
            return "Excluded by user: Game DVR/App Capture stay manual";
        if (!IsMeasuredCandidate(tweak))
            return "Protected/manual or no applicable measured performance candidate";
        if (tweak.Id.Equals("perf.synthetic", StringComparison.OrdinalIgnoreCase))
            return string.Empty;


        if (tweak.Id is "sys.inventory" or "sys.ait")
            return ContainsObservedComponent(components, "CompatTelRunner")
                ? "" : "Performance candidate waits for observable AppCompat workload";

        if (tweak.Id is "ui.thumbcache" or "ui.nonetworkthumbsdb" or "ui.syncpromo" or
            "privacy.explorerpromo")
            return ContainsObservedComponent(components, "explorer")
                ? "" : "Explorer candidate waits for measurable Explorer activity";

        if (tweak.Id is "ui.searchfeed" or "ui.searchhistory" or "ui.searchmsa" or
            "ui.searchaad" or "ui.searchlocation" or "privacy.websearch" or
            "privacy.searchhl" or "privacy.cloudsearch")
            return ContainsObservedComponent(components, "SearchHost", "SearchApp")
                ? "" : "Search candidate waits for measurable Search activity";

        if (tweak.Id is "ui.startrecommend" or "ui.startaccount" or "privacy.consumer" or
            "privacy.tips" or "privacy.tips2" or "privacy.tips3" or
            "privacy.spotlightstart" or "privacy.welcome")
            return ContainsObservedComponent(components, "StartMenuExperienceHost", "ShellExperienceHost")
                ? "" : "Shell candidate waits for measurable Start/Shell activity";

        return "Safe reversible policy; no applicable live CPU/productivity metric (manual preference)";
    }
    // Avoid params arrays, LINQ enumerators and a capturing local function in
    // repeated candidate checks. Keep the previous substring/case behavior.
    internal static bool ContainsObservedComponent(string components, string first, string? second = null) =>
        components.Contains(first, StringComparison.OrdinalIgnoreCase) ||
        (second != null && components.Contains(second, StringComparison.OrdinalIgnoreCase));

    public static string SampleBlocker(AiSystemPerformanceSample s)
    {
        if (!s.Valid) return "Missing/unreliable measurements: " + s.Reason;
        if (string.IsNullOrEmpty(s.WorkloadKey)) return "No comparable active workload";
        if (!double.IsFinite(s.CpuPercent) || !double.IsFinite(s.UsedMemoryMb) ||
            !double.IsFinite(s.SchedulerDelayMs) || !double.IsFinite(s.FixedWorkMs) ||
            !double.IsFinite(s.WorkloadCpuPercent) || s.WorkloadCpuPercent < 0.1 ||
            s.FixedWorkMs <= 0 || s.UsedMemoryMb <= 0 || s.SchedulerDelayMs < 0)
            return "Invalid measurement values";
        if (s.CpuPercent < 0.5 || s.CpuPercent > 35) return "Idle/high-load measurements not comparable";
        if (!s.TemperatureC.HasValue ||
            !AiPowerTelemetryEvidencePolicy.HasUsableCpuTemperature(s.TemperatureC.Value))
            return "Temperature unavailable or implausible";
        if (s.TemperatureC >= 75) return "Thermal guard";
        return "";
    }
    public static string WindowBlocker(IReadOnlyList<AiSystemPerformanceSample> window)
    {
        if (window.Count < SamplesPerWindow || window[^1].Utc - window[0].Utc < TimeSpan.FromSeconds(25))
            return "Insufficient observation window";
        for (int i = 0; i < window.Count; i++)
            if (SampleBlocker(window[i]).Length != 0)
                return "Missing/unsafe measurements";
        for (int i = 1; i < window.Count; i++)
            if (window[i].Utc - window[i - 1].Utc < TimeSpan.FromSeconds(5) ||
                window[i].Utc - window[i - 1].Utc > TimeSpan.FromSeconds(45))
                return "Duplicate/out-of-order/gapped measurements";
        for (int i = 1; i < window.Count; i++)
            if (!string.Equals(window[0].WorkloadKey, window[i].WorkloadKey, StringComparison.Ordinal))
                return "Workload changed within window";
        if (WindowNoiseTooHigh(window))
            return "Measurements too noisy";
        return "";
    }

    // A/B/A measurements have six samples in their normal window. Analyze
    // them on the stack rather than allocating several LINQ arrays/iterators
    // and repeatedly sorting the same values for median and MAD.
    internal static bool WindowNoiseTooHigh(IReadOnlyList<AiSystemPerformanceSample> window)
    {
        Span<double> fixedWork = window.Count <= 32
            ? stackalloc double[window.Count] : new double[window.Count];
        Span<double> foregroundCpu = window.Count <= 32
            ? stackalloc double[window.Count] : new double[window.Count];
        for (int i = 0; i < window.Count; i++)
        {
            fixedWork[i] = window[i].FixedWorkMs;
            foregroundCpu[i] = window[i].WorkloadCpuPercent;
        }
        var (fixedMedian, fixedMad) = MedianAndMad(fixedWork);
        var (cpuMedian, cpuMad) = MedianAndMad(foregroundCpu);
        return fixedMad > fixedMedian * 0.08 ||
               cpuMad > Math.Max(0.5, cpuMedian * 0.15);
    }

    private static (double Median, double Mad) MedianAndMad(Span<double> values)
    {
        values.Sort();
        int count = values.Length;
        double median = count % 2 == 0
            ? (values[count / 2 - 1] + values[count / 2]) / 2
            : values[count / 2];
        for (int i = 0; i < count; i++)
            values[i] = Math.Abs(values[i] - median);
        values.Sort();
        double mad = count % 2 == 0
            ? (values[count / 2 - 1] + values[count / 2]) / 2
            : values[count / 2];
        return (median, mad);
    }

    public static AiPerformanceVerdict Compare(IReadOnlyList<AiSystemPerformanceSample> baseline,
        IReadOnlyList<AiSystemPerformanceSample> candidate, IReadOnlyList<AiSystemPerformanceSample> reference)
    {
        AiPerformanceVerdict Reject(string reason) => new(false, reason, 0);
        string issue = WindowBlocker(baseline);
        if (issue.Length != 0) return Reject(issue);
        issue = WindowBlocker(candidate);
        if (issue.Length != 0) return Reject(issue);
        issue = WindowBlocker(reference);
        if (issue.Length != 0) return Reject(issue);
        if (baseline[^1].Utc >= candidate[0].Utc || candidate[^1].Utc >= reference[0].Utc)
            return Reject("Overlapping/out-of-order A/B/A measurements");
        if (baseline[0].WorkloadKey != candidate[0].WorkloadKey || baseline[0].WorkloadKey != reference[0].WorkloadKey)
            return Reject("Workload changed; reduced activity is not a gain");
        double a = WindowMedian(baseline, WindowMetric.FixedWork), b = WindowMedian(candidate, WindowMetric.FixedWork);
        double r = WindowMedian(reference, WindowMetric.FixedWork);
        double cpuA = WindowMedian(baseline, WindowMetric.Cpu), cpuB = WindowMedian(candidate, WindowMetric.Cpu);
        double cpuR = WindowMedian(reference, WindowMetric.Cpu);
        double memoryA = WindowMedian(baseline, WindowMetric.Memory);
        double memoryB = WindowMedian(candidate, WindowMetric.Memory);
        double memoryR = WindowMedian(reference, WindowMetric.Memory);
        double foregroundA = WindowMedian(baseline, WindowMetric.Foreground);
        double foregroundB = WindowMedian(candidate, WindowMetric.Foreground);
        double foregroundR = WindowMedian(reference, WindowMetric.Foreground);
        if (Math.Abs(foregroundB - foregroundA) > Math.Max(1, foregroundA * 0.2) ||
            Math.Abs(foregroundR - foregroundA) > Math.Max(1, foregroundA * 0.2))
            return Reject("Foreground workload changed; lower activity is not proof of productivity");
        // Separate an actual candidate CPU regression from a changed A2
        // reference. The latter makes the A/B/A trial inconclusive, not a
        // measured loss by candidate EPP 12; neither case may be approved.
        if (cpuB > cpuA + Math.Max(2, cpuA * 0.15))
            return Reject("CPU regression");
        if (Math.Abs(cpuR - cpuA) > Math.Max(2, cpuA * 0.2))
            return Reject("Reference CPU/workload changed");
        if (memoryB > memoryA + Math.Max(128, memoryA * 0.03))
            return Reject("Memory regression");
        if (Math.Abs(memoryR - memoryA) > Math.Max(128, memoryA * 0.03))
            return Reject("Reference memory/workload changed");
        if (Math.Abs(r - a) > a * 0.07)
            return Reject("Original-state reference did not recover baseline; inconclusive");
        double tempA = WindowMedian(baseline, WindowMetric.Temperature);
        double tempB = WindowMedian(candidate, WindowMetric.Temperature);
        double tempR = WindowMedian(reference, WindowMetric.Temperature);
        if (Math.Abs(tempR - tempA) > 3 || Math.Abs(tempB - tempA) > 5)
            return Reject("Temperature changed between A/B/A windows; inconclusive");
        double delayA = WindowMedian(baseline, WindowMetric.Delay);
        double delayB = WindowMedian(candidate, WindowMetric.Delay);
        double delayR = WindowMedian(reference, WindowMetric.Delay);
        if (delayB > delayA + Math.Max(1, delayA * 0.15)) return Reject("Scheduler-delay regression");
        if (Math.Abs(delayR - delayA) > Math.Max(1, delayA * 0.20))
            return Reject("Reference scheduler-delay changed; inconclusive");
        // A/B/A confidence must include candidate-window dispersion too.
        // A noisy candidate can appear faster by median while offering no
        // repeatable benefit under otherwise stable reference windows.
        double noise = Math.Max(
            Math.Max(WindowMad(baseline, WindowMetric.FixedWork), WindowMad(candidate, WindowMetric.FixedWork)),
            WindowMad(reference, WindowMetric.FixedWork));
        double improvement = (Math.Min(a, r) - b) / Math.Min(a, r) * 100;
        if (improvement < 10 || Math.Min(a, r) - b < Math.Max(0.25, noise * 4))
            return Reject("No significant repeatable fixed-work improvement");
        return new(true, $"A/B/A fixed-work improvement {improvement:0.0}% with stable workload; memory/scheduler guards passed", improvement);
    }
    internal enum WindowMetric { FixedWork, Cpu, Memory, Foreground, Temperature, Delay }

    // These six-sample A/B/A windows are measured repeatedly. Use stack memory
    // instead of LINQ projection, iterator, and sorted-array allocations.
    internal static double WindowMedian(IReadOnlyList<AiSystemPerformanceSample> window, WindowMetric metric)
    {
        if (window.Count == 0) return double.NaN;
        Span<double> values = window.Count <= 32
            ? stackalloc double[window.Count] : new double[window.Count];
        FillWindowValues(window, metric, values);
        values.Sort();
        int mid = values.Length / 2;
        return values.Length % 2 == 0 ? (values[mid - 1] + values[mid]) / 2 : values[mid];
    }

    private static double WindowMad(IReadOnlyList<AiSystemPerformanceSample> window, WindowMetric metric)
    {
        if (window.Count == 0) return double.NaN;
        Span<double> values = window.Count <= 32
            ? stackalloc double[window.Count] : new double[window.Count];
        FillWindowValues(window, metric, values);
        return MedianAndMad(values).Mad;
    }

    private static void FillWindowValues(IReadOnlyList<AiSystemPerformanceSample> window,
        WindowMetric metric, Span<double> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            var sample = window[i];
            destination[i] = metric switch
            {
                WindowMetric.FixedWork => sample.FixedWorkMs,
                WindowMetric.Cpu => sample.CpuPercent,
                WindowMetric.Memory => sample.UsedMemoryMb,
                WindowMetric.Foreground => sample.WorkloadCpuPercent,
                WindowMetric.Temperature => sample.TemperatureC!.Value,
                WindowMetric.Delay => sample.SchedulerDelayMs,
                _ => throw new ArgumentOutOfRangeException(nameof(metric))
            };
        }
    }

    public static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        return v.Length == 0 ? double.NaN : v.Length % 2 == 0 ? (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2 : v[v.Length / 2];
    }
    private static double Mad(IEnumerable<double> values)
    {
        var v = values.ToArray(); double median = Median(v);
        return Median(v.Select(x => Math.Abs(x - median)));
    }
}

public sealed class AiSystemPerformanceProbe
{
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    private readonly ProcessSamplingHistory _cpuHistory = new();
    private readonly PerformanceProcessAccumulator _processAccumulator = new();
    // Reuse the conservative, PID/name-verified denied-native-CPU policy of
    // adaptive power sampling. Never cache successful reads or skip foreground.
    private readonly AdaptiveCpuAccessDeniedCache _deniedCpuReads = new();
    private ulong _idle, _total;
    private DateTime _at;
    private bool _lastNativeCpuReadable;
    private static uint _workSink;
    private bool _fixedWorkWarmed;
    private static readonly bool ProbeTimingEnabled =
        Environment.GetCommandLineArgs().Contains("--ai-power-evidence-smoke", StringComparer.Ordinal);
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    // At most one running and one queued probe; discard stale bursts safely.
    private readonly SemaphoreSlim _pendingSlots = new(2, 2);

    private static bool IsObservedComponent(string name) =>
        name.Contains("CompatTelRunner", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SearchApp", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Widgets", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("WidgetService", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RuntimeBroker", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("dwm", StringComparison.OrdinalIgnoreCase);

    // Process.GetProcesses and per-process CPU reads are synchronous native calls.
    // Keep that work off any WinUI/caller dispatcher, including preflight ticks.
    public async Task<AiSystemPerformanceSample> SampleAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!await _pendingSlots.WaitAsync(0, token).ConfigureAwait(false))
            return new AiSystemPerformanceSample(DateTime.UtcNow, double.NaN, double.NaN,
                double.NaN, double.NaN, string.Empty, null, false,
                "Native process sampler busy; stale queued read skipped");
        try
        {
            // A queued observation expires rather than becoming stale evidence
            // if native sampling stalls. Never interrupt the active native read.
            if (!await _sampleGate.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false))
                return new AiSystemPerformanceSample(DateTime.UtcNow, double.NaN, double.NaN,
                    double.NaN, double.NaN, string.Empty, null, false,
                    "Native process sampler busy; stale queued read skipped");
            try
            {
                return await Task.Run(() => SampleCoreAsync(token), token).ConfigureAwait(false);
            }
            finally
            {
                _sampleGate.Release();
            }
        }
        finally
        {
            _pendingSlots.Release();
        }
    }

    private async Task<AiSystemPerformanceSample> SampleCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long sampleStart = Stopwatch.GetTimestamp();
        DateTime now = DateTime.UtcNow;
        string reason = "";
        bool nativeCpuReadable = GetSystemTimes(out var idle, out var kernel, out var user);
        ulong total = kernel.Value + user.Value;
        bool warmed = nativeCpuReadable && AiNativeSamplingValidity.CanCompareCpu(
            _lastNativeCpuReadable, _at, now, _total, total, _idle, idle.Value);
        double elapsed = Math.Max(0.1, (now - _at).TotalSeconds);
        double cpu = warmed ? Math.Clamp((1 - (idle.Value - _idle) / (double)(total - _total)) * 100, 0, 100) : double.NaN;
        _idle = idle.Value; _total = total; _at = now;
        _lastNativeCpuReadable = nativeCpuReadable;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        bool nativeMemoryReadable = GlobalMemoryStatusEx(ref memory);
        bool memoryValuesValid = AiNativeSamplingValidity.TryUsedMemoryMb(
            memory.TotalPhys, memory.AvailPhys, out double usedMb);
        bool valid = nativeCpuReadable && nativeMemoryReadable && memoryValuesValid;
        _processAccumulator.Reset();
        _cpuHistory.Begin();
        _deniedCpuReads.BeginCycle(now);
        var foregroundBefore = ForegroundProcessProbe.GetWindowIdentity();
        bool foregroundFound = false;
        string foregroundName = string.Empty;
        double foregroundAppCpu = 0;
        int processorCount = Environment.ProcessorCount;
        Process[] processes = Process.GetProcesses();
        try
        {
            // Resolve one foreground PID from ALREADY acquired Process[] wrappers
            // before accumulating the workload CPU for its executable family.
            // No extra process enumeration or separate OpenProcess is needed.
            if (foregroundBefore.ProcessId > 0)
            {
                foreach (Process p in processes)
                {
                    try
                    {
                        if (p.Id != foregroundBefore.ProcessId) continue;
                        foregroundName = p.ProcessName;
                        foregroundFound = !string.IsNullOrWhiteSpace(foregroundName);
                        break;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or
                        System.ComponentModel.Win32Exception or NotSupportedException) { }
                }
            }
            foreach (Process p in processes)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    int pid = p.Id;
                    string name = p.ProcessName;
                    if (_deniedCpuReads.ShouldSkip(pid, name, foregroundBefore.ProcessId))
                        continue;
                    TimeSpan t;
                    try
                    {
                        t = p.TotalProcessorTime;
                    }
                    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                    {
                        _deniedCpuReads.RecordDenied(pid, name, foregroundBefore.ProcessId);
                        continue; // Unreadable evidence must remain UNKNOWN, not zero.
                    }
                    _cpuHistory.Record(pid, name, t);
                    if (_cpuHistory.TryGet(pid, name, out var before) && t > before)
                    {
                        double processCpu = (t - before).TotalSeconds / elapsed / processorCount * 100;
                        _processAccumulator.Add(pid, name, processCpu, IsObservedComponent(name));
                        if (foregroundFound &&
                            string.Equals(name, foregroundName, StringComparison.OrdinalIgnoreCase))
                            foregroundAppCpu += processCpu;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        finally
        {
            // Release ALL handles returned upfront, including after cancellation.
            ProcessSnapshotCleanup.DisposeAll(processes);
        }
        _cpuHistory.Commit();
        var foregroundAfterEnumeration = ForegroundProcessProbe.GetWindowIdentity();
        string observedComponents = _processAccumulator.GetObservedComponents();
        if (!warmed) { valid = false; reason = "Native CPU counters require a fresh comparable sample"; }
        if (!nativeMemoryReadable || !memoryValuesValid)
        { valid = false; reason = "Physical memory counters unavailable or inconsistent"; }
        if (warmed && cpu > 1 && _processAccumulator.TotalCpu < cpu * 0.4)
        { valid = false; reason = "Insufficient readable workload coverage"; }
        // Invalid native evidence can never enter an A/B/A window. Avoid the
        // extra scheduler delay and synthetic CPU workload on these samples;
        // retain process-history commit above for the next valid reading.
        if (!valid)
            return new(now, cpu, usedMb, double.NaN, double.NaN, string.Empty,
                null, false, reason, observedComponents, double.NaN);
        double enumerationMs = Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds;
        long scheduleStart = Stopwatch.GetTimestamp();
        await Task.Delay(20, token).ConfigureAwait(false);
        double scheduledMs = Stopwatch.GetElapsedTime(scheduleStart).TotalMilliseconds;
        double delay = Math.Max(0, scheduledMs - 20);
        token.ThrowIfCancellationRequested();
        long workStart = Stopwatch.GetTimestamp();
        uint x = 0x5f3759df;
        for (int i = 0; i < 500000; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; }
        Volatile.Write(ref _workSink, x);
        double workMs = Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;
        // The first fixed-work execution can include tier-0 JIT compilation.
        // Do not treat its wall time as a comparable EPP performance metric.
        if (!_fixedWorkWarmed)
        {
            _fixedWorkWarmed = true;
            valid = false;
            reason = "Fixed-work measurement requires warm-up";
        }
        if (ProbeTimingEnabled)
            LogService.Info("power", $"AI_PROBE_PHASE_TIMING enumerationMs={enumerationMs:0.000} " +
                $"scheduleMs={scheduledMs:0.000} fixedWorkMs={workMs:0.000}");
        // A foreground switch while the 20ms scheduler/fixed-work probe
        // executes invalidates the sample, including same-PID HWND changes.
        var foregroundAfterProbe = ForegroundProcessProbe.GetWindowIdentity();
        bool foregroundComparable = AiEppForegroundEvidencePolicy.IsComparable(
            foregroundBefore, foregroundAfterEnumeration, foregroundFound,
            foregroundName, foregroundAppCpu) &&
            AiEppForegroundEvidencePolicy.IsComparable(
                foregroundBefore, foregroundAfterProbe, foregroundFound,
                foregroundName, foregroundAppCpu);
        if (!foregroundComparable)
        {
            valid = false;
            reason = "Foreground process/window or CPU evidence changed";
        }
        string key = foregroundComparable
            ? AiEppForegroundEvidencePolicy.WorkloadKey(foregroundBefore, foregroundName)
            : string.Empty;
        var telemetry = AdaptivePowerRuntimeService.ReadStatus();
        double? temperature = telemetry != null && now - telemetry.UpdatedUtc < TimeSpan.FromSeconds(30) ? telemetry.TemperatureC : null;
        return new(now, cpu, usedMb, delay, workMs, key, temperature, valid, reason,
            observedComponents, foregroundComparable ? foregroundAppCpu : double.NaN);
    }
}
