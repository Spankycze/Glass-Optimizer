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

        bool Has(params string[] names) => names.Any(name =>
            components.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (tweak.Id is "sys.inventory" or "sys.ait")
            return Has("CompatTelRunner")
                ? "" : "Performance candidate waits for observable AppCompat workload";

        if (tweak.Id is "ui.thumbcache" or "ui.nonetworkthumbsdb" or "ui.syncpromo" or
            "privacy.explorerpromo")
            return Has("explorer")
                ? "" : "Explorer candidate waits for measurable Explorer activity";

        if (tweak.Id is "ui.searchfeed" or "ui.searchhistory" or "ui.searchmsa" or
            "ui.searchaad" or "ui.searchlocation" or "privacy.websearch" or
            "privacy.searchhl" or "privacy.cloudsearch")
            return Has("SearchHost", "SearchApp")
                ? "" : "Search candidate waits for measurable Search activity";

        if (tweak.Id is "ui.startrecommend" or "ui.startaccount" or "privacy.consumer" or
            "privacy.tips" or "privacy.tips2" or "privacy.tips3" or
            "privacy.spotlightstart" or "privacy.welcome")
            return Has("StartMenuExperienceHost", "ShellExperienceHost")
                ? "" : "Shell candidate waits for measurable Start/Shell activity";

        return "Safe reversible policy; no applicable live CPU/productivity metric (manual preference)";
    }
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
        if (!s.TemperatureC.HasValue || !double.IsFinite(s.TemperatureC.Value)) return "Temperature unavailable";
        if (s.TemperatureC >= 75) return "Thermal guard";
        return "";
    }
    public static string WindowBlocker(IReadOnlyList<AiSystemPerformanceSample> window)
    {
        if (window.Count < SamplesPerWindow || window[^1].Utc - window[0].Utc < TimeSpan.FromSeconds(25))
            return "Insufficient observation window";
        if (window.Any(s => SampleBlocker(s).Length != 0)) return "Missing/unsafe measurements";
        for (int i = 1; i < window.Count; i++)
            if (window[i].Utc - window[i - 1].Utc < TimeSpan.FromSeconds(5) ||
                window[i].Utc - window[i - 1].Utc > TimeSpan.FromSeconds(45))
                return "Duplicate/out-of-order/gapped measurements";
        if (window.Select(s => s.WorkloadKey).Distinct(StringComparer.Ordinal).Count() != 1)
            return "Workload changed within window";
        if (Mad(window.Select(s => s.FixedWorkMs)) > Median(window.Select(s => s.FixedWorkMs)) * 0.08 ||
            Mad(window.Select(s => s.WorkloadCpuPercent)) > Math.Max(0.5, Median(window.Select(s => s.WorkloadCpuPercent)) * 0.15))
            return "Measurements too noisy";
        return "";
    }
    public static AiPerformanceVerdict Compare(IReadOnlyList<AiSystemPerformanceSample> baseline,
        IReadOnlyList<AiSystemPerformanceSample> candidate, IReadOnlyList<AiSystemPerformanceSample> reference)
    {
        AiPerformanceVerdict Reject(string reason) => new(false, reason, 0);
        foreach (var window in new[] { baseline, candidate, reference })
        {
            string issue = WindowBlocker(window);
            if (issue.Length != 0) return Reject(issue);
        }
        if (baseline[^1].Utc >= candidate[0].Utc || candidate[^1].Utc >= reference[0].Utc)
            return Reject("Overlapping/out-of-order A/B/A measurements");
        if (baseline[0].WorkloadKey != candidate[0].WorkloadKey || baseline[0].WorkloadKey != reference[0].WorkloadKey)
            return Reject("Workload changed; reduced activity is not a gain");
        double a = Median(baseline.Select(s => s.FixedWorkMs)), b = Median(candidate.Select(s => s.FixedWorkMs));
        double r = Median(reference.Select(s => s.FixedWorkMs));
        double cpuA = Median(baseline.Select(s => s.CpuPercent)), cpuB = Median(candidate.Select(s => s.CpuPercent));
        double cpuR = Median(reference.Select(s => s.CpuPercent));
        double memoryA = Median(baseline.Select(s => s.UsedMemoryMb));
        double memoryB = Median(candidate.Select(s => s.UsedMemoryMb));
        double memoryR = Median(reference.Select(s => s.UsedMemoryMb));
        double foregroundA = Median(baseline.Select(s => s.WorkloadCpuPercent));
        double foregroundB = Median(candidate.Select(s => s.WorkloadCpuPercent));
        double foregroundR = Median(reference.Select(s => s.WorkloadCpuPercent));
        if (Math.Abs(foregroundB - foregroundA) > Math.Max(1, foregroundA * 0.2) ||
            Math.Abs(foregroundR - foregroundA) > Math.Max(1, foregroundA * 0.2))
            return Reject("Foreground workload changed; lower activity is not proof of productivity");
        if (cpuB > cpuA + Math.Max(2, cpuA * 0.15) || Math.Abs(cpuR - cpuA) > Math.Max(2, cpuA * 0.2))
            return Reject("CPU regression or reference workload changed");
        if (memoryB > memoryA + Math.Max(128, memoryA * 0.03))
            return Reject("Memory regression");
        if (Math.Abs(memoryR - memoryA) > Math.Max(128, memoryA * 0.03))
            return Reject("Reference memory/workload changed");
        if (Math.Abs(r - a) > a * 0.07)
            return Reject("Original-state reference did not recover baseline; inconclusive");
        if (Math.Abs(Median(reference.Select(s => s.TemperatureC!.Value)) -
            Median(baseline.Select(s => s.TemperatureC!.Value))) > 3)
            return Reject("Temperature changed; inconclusive");
        double delayA = Median(baseline.Select(s => s.SchedulerDelayMs));
        double delayB = Median(candidate.Select(s => s.SchedulerDelayMs));
        if (delayB > delayA + Math.Max(1, delayA * 0.15)) return Reject("Scheduler-delay regression");
        double noise = Math.Max(Mad(baseline.Select(s => s.FixedWorkMs)), Mad(reference.Select(s => s.FixedWorkMs)));
        double improvement = (Math.Min(a, r) - b) / Math.Min(a, r) * 100;
        if (improvement < 10 || Math.Min(a, r) - b < Math.Max(0.25, noise * 4))
            return Reject("No significant repeatable fixed-work improvement");
        return new(true, $"A/B/A fixed-work improvement {improvement:0.0}% with stable workload; memory/scheduler guards passed", improvement);
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
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    private readonly Dictionary<int, TimeSpan> _previousCpu = new();
    private ulong _idle, _total;
    private DateTime _at;
    private static uint _workSink;

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

    public async Task<AiSystemPerformanceSample> SampleAsync(CancellationToken token)
    {
        DateTime now = DateTime.UtcNow;
        string reason = "";
        bool valid = GetSystemTimes(out var idle, out var kernel, out var user);
        ulong total = kernel.Value + user.Value;
        double elapsed = Math.Max(0.1, (now - _at).TotalSeconds);
        bool warmed = _at != default && total > _total && idle.Value >= _idle;
        double cpu = warmed ? Math.Clamp((1 - (idle.Value - _idle) / (double)(total - _total)) * 100, 0, 100) : double.NaN;
        _idle = idle.Value; _total = total; _at = now;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        valid &= GlobalMemoryStatusEx(ref memory);
        double usedMb = (memory.TotalPhys - memory.AvailPhys) / 1048576d;
        var loads = new List<(int Id, string Name, double Cpu)>();
        var next = new Dictionary<int, TimeSpan>();
        var components = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var t = p.TotalProcessorTime; string name = p.ProcessName;
                    next[p.Id] = t;
                    if (_previousCpu.TryGetValue(p.Id, out var before) && t > before)
                    {
                        double processCpu = (t - before).TotalSeconds / elapsed / Environment.ProcessorCount * 100;
                        loads.Add((p.Id, name, processCpu));
                        if (processCpu >= 0.05 && IsObservedComponent(name))
                            components.Add(name);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        _previousCpu.Clear();
        foreach (var pair in next) _previousCpu[pair.Key] = pair.Value;
        var foregroundWork = loads.Where(x => !x.Name.Contains("Spanky", StringComparison.OrdinalIgnoreCase) &&
            x.Name != "Idle" && !components.Contains(x.Name, StringComparer.OrdinalIgnoreCase) && x.Cpu >= 0.1).OrderByDescending(x => x.Cpu).Take(2).ToArray();
        string key = string.Join("|", foregroundWork.OrderBy(x => x.Id).Select(x => x.Id + ":" + x.Name));
        if (!warmed) { valid = false; reason = "First native sample establishes counters"; }
        if (warmed && cpu > 1 && loads.Sum(x => x.Cpu) < cpu * 0.4)
        { valid = false; reason = "Insufficient readable workload coverage"; }
        var schedule = Stopwatch.StartNew();
        await Task.Delay(20, token).ConfigureAwait(false);
        double delay = Math.Max(0, schedule.Elapsed.TotalMilliseconds - 20);
        token.ThrowIfCancellationRequested();
        var work = Stopwatch.StartNew();
        uint x = 0x5f3759df;
        for (int i = 0; i < 500000; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; }
        Volatile.Write(ref _workSink, x);
        double workMs = work.Elapsed.TotalMilliseconds;
        var telemetry = AdaptivePowerRuntimeService.ReadStatus();
        double? temperature = telemetry != null && now - telemetry.UpdatedUtc < TimeSpan.FromSeconds(30) ? telemetry.TemperatureC : null;
        return new(now, cpu, usedMb, delay, workMs, key, temperature, valid, reason, string.Join("|", components.OrderBy(x => x)), foregroundWork.Sum(x => x.Cpu));
    }
}
