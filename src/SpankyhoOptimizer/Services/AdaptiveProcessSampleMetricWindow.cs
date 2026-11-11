using System;
using System.Diagnostics;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Allocation-free aggregation of all existing adaptive CPU/process scans.
/// A minute-long average and maximum are more representative than the
/// single scan which happened to occur when the logging gate opened.
/// No own polling, timer, process list or file writing.
/// </summary>
internal struct AdaptiveProcessSampleMetricWindow
{
    internal readonly record struct Snapshot(
        int Scans, double MeanMs, double MinMs, double MaxMs,
        double MeanAllocatedB, long MaxAllocatedB,
        double MeanProcesses, int MaxProcesses,
        long NativeNameReads, long ReusedNames,
        long NativeWorkingSetReads, long WorkingSetCacheHits,
        int WorkingSetRefreshes, long DuplicateCandidatesSkipped,
        long NativeCpuReadAttempts, long CpuAccessDenied,
        long SuppressedDeniedCpuReads);

    private int _scans;
    private double _sumMs, _minMs, _maxMs;
    private long _sumAllocated, _maxAllocated, _sumProcesses;
    private int _maxProcesses;
    private long _nameReads, _reusedNames, _nativeWorkingSetReads, _cacheHits;
    private int _refreshes;
    private long _duplicatesSkipped;
    private long _cpuNativeReads, _cpuAccessDenied, _cpuDeniedSkipped;

    internal readonly int Count => _scans;

    internal void Add(double wallMs, long allocatedB, int processCount,
        int nativeNameReads, int reusedNames,
        int nativeWorkingSetReads, int cacheHits, bool refreshed,
        int duplicateCandidatesSkipped = 0,
        int nativeCpuReadAttempts = 0, int cpuAccessDenied = 0,
        int suppressedDeniedCpuReads = 0)
    {
        if (!double.IsFinite(wallMs) || wallMs < 0 ||
            allocatedB < 0 || processCount < 0 ||
            nativeNameReads < 0 || reusedNames < 0 ||
            nativeWorkingSetReads < 0 || cacheHits < 0 ||
            duplicateCandidatesSkipped < 0 ||
            nativeCpuReadAttempts < 0 || cpuAccessDenied < 0 ||
            suppressedDeniedCpuReads < 0)
            return;

        _minMs = _scans == 0 ? wallMs : Math.Min(_minMs, wallMs);
        _maxMs = Math.Max(_maxMs, wallMs);
        _scans++;
        _sumMs += wallMs;
        _sumAllocated += allocatedB;
        _maxAllocated = Math.Max(_maxAllocated, allocatedB);
        _sumProcesses += processCount;
        _maxProcesses = Math.Max(_maxProcesses, processCount);
        _nameReads += nativeNameReads;
        _reusedNames += reusedNames;
        _nativeWorkingSetReads += nativeWorkingSetReads;
        _cacheHits += cacheHits;
        if (refreshed) _refreshes++;
        _duplicatesSkipped += duplicateCandidatesSkipped;
        _cpuNativeReads += nativeCpuReadAttempts;
        _cpuAccessDenied += cpuAccessDenied;
        _cpuDeniedSkipped += suppressedDeniedCpuReads;
    }

    internal readonly Snapshot Read() =>
        _scans == 0 ? default : new(
            _scans, _sumMs / _scans, _minMs, _maxMs,
            (double)_sumAllocated / _scans, _maxAllocated,
            (double)_sumProcesses / _scans, _maxProcesses,
            _nameReads, _reusedNames, _nativeWorkingSetReads,
            _cacheHits, _refreshes, _duplicatesSkipped,
            _cpuNativeReads, _cpuAccessDenied, _cpuDeniedSkipped);

    internal Snapshot TakeAndReset()
    {
        Snapshot value = Read();
        this = default;
        return value;
    }

    internal static string RunFunctionalSmoke()
    {
        var stats = new AdaptiveProcessSampleMetricWindow();
        if (stats.Read().Scans != 0)
            throw new InvalidOperationException("Nonempty telemetry window at startup.");
        stats.Add(10, 100, 100, 20, 80, 5, 0, true, 2, 90, 20, 8);
        stats.Add(20, 150, 120, 10, 110, 0, 5, false);
        stats.Add(30, 50, 90, 15, 75, 5, 0, true);
        var result = stats.TakeAndReset();
        if (result.Scans != 3 || result.MeanMs != 20 ||
            result.MinMs != 10 || result.MaxMs != 30 ||
            result.MeanAllocatedB != 100 || result.MaxAllocatedB != 150 ||
            Math.Abs(result.MeanProcesses - 103.3333333) > 0.001 ||
            result.MaxProcesses != 120 || result.NativeNameReads != 45 ||
            result.ReusedNames != 265 || result.NativeWorkingSetReads != 10 ||
            result.WorkingSetCacheHits != 5 || result.WorkingSetRefreshes != 2 ||
            result.DuplicateCandidatesSkipped != 2 ||
            result.NativeCpuReadAttempts != 90 ||
            result.CpuAccessDenied != 20 ||
            result.SuppressedDeniedCpuReads != 8 ||
            stats.Count != 0)
            throw new InvalidOperationException(
                "Adaptive process-sampling minute aggregates were inaccurate.");
        stats.Add(double.NaN, 1, 1, 1, 1, 1, 1, false);
        stats.Add(1, -1, 1, 1, 1, 1, 1, false);
        if (stats.Count != 0)
            throw new InvalidOperationException("Invalid sampling metric accepted.");
        const int rounds = 32768;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
            stats.Add(2.5, 50, 180, 45, 135, 1, 4, (i & 3) == 0,
                0, 140, 35, 90);
        var hot = stats.TakeAndReset();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (hot.Scans != rounds || hot.WorkingSetRefreshes != rounds / 4 ||
            hot.NativeWorkingSetReads != rounds || hot.WorkingSetCacheHits != rounds * 4 ||
            hot.MaxMs != 2.5 || hot.MeanMs != 2.5 ||
            hot.DuplicateCandidatesSkipped != 0 ||
            hot.NativeCpuReadAttempts != rounds * 140 ||
            hot.CpuAccessDenied != rounds * 35 ||
            hot.SuppressedDeniedCpuReads != rounds * 90 ||
            bytes > 128)
            throw new InvalidOperationException(
                "Adaptive process sampling telemetry allocated or double-counted.");
        return $"ADAPTIVE_PROCESS_METRIC_WINDOW_SMOKE_OK scans={rounds} " +
            $"elapsedMs={elapsedMs:0.###} allocBytes={bytes} " +
            "perMinuteAggregates=true cpuDeniedAttempts=true " +
            "cpuDeniedSkipped=true correctMinMeanMax=true writes=0";
    }
}
