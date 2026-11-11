using System;
using System.ComponentModel;
using System.Diagnostics;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Native CPU access benchmark against READ-ONLY Process wrappers. Compares
/// repeated denied GetProcessTimes attempts to the bounded negative cache.
/// Never changes priorities, services, windows, NIC or process state.
/// </summary>
internal static class AdaptiveCpuDeniedNativeCostSmoke
{
    private readonly record struct Trial(
        int Snapshots, int Processes, int CpuAttempts, int Successes,
        int NativeDenied, int SuppressedDenied, double WallMs,
        long AllocatedB);

    private static Trial Measure(bool useCache, int rounds)
    {
        var denied = new AdaptiveCpuAccessDeniedCache();
        int processes = 0, cpuAttempts = 0, successes = 0, deniedCount = 0;
        int suppressed = 0;
        int foregroundPid = ForegroundProcessProbe.GetProcessId();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int round = 0; round < rounds; round++)
        {
            denied.BeginCycle(DateTime.UtcNow);
            Process[] snapshot = Process.GetProcesses();
            try
            {
                processes += snapshot.Length;
                foreach (Process process in snapshot)
                {
                    try
                    {
                        int pid = process.Id;
                        // Native names remain observable even where CPU time is
                        // protected (genuine Windows session/system processes).
                        string name = process.ProcessName;
                        if (useCache && denied.ShouldSkip(pid, name, foregroundPid))
                            continue;
                        cpuAttempts++;
                        try
                        {
                            _ = process.TotalProcessorTime;
                            successes++;
                        }
                        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
                        {
                            deniedCount++;
                            if (useCache)
                                denied.RecordDenied(pid, name, foregroundPid);
                        }
                    }
                    catch (Exception ex) when (ex is Win32Exception or
                        InvalidOperationException or NotSupportedException or
                        ArgumentException)
                    {
                        // Process exit/denied access yields unknown, not zero.
                    }
                }
            }
            finally
            {
                ProcessSnapshotCleanup.DisposeAll(snapshot);
            }
            suppressed += denied.SuppressedThisCycle;
        }
        return new(rounds, processes, cpuAttempts, successes,
            deniedCount, suppressed,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    internal static string RunFunctionalSmoke()
    {
        if (!OperatingSystem.IsWindows())
            return "ADAPTIVE_CPU_DENIED_NATIVE_COST_SKIP nonWindows=true";
        const int rounds = 5;
        using Process self = Process.GetCurrentProcess();
        self.Refresh();
        TimeSpan cpuBefore = self.TotalProcessorTime;
        long ramBefore = self.WorkingSet64;
        int handlesBefore = self.HandleCount;
        int threadsBefore = self.Threads.Count;
        // A-B-B-A helps expose machine load/warmup changes.
        var baseline = Measure(false, rounds);
        var optimized = Measure(true, rounds);
        var optimized2 = Measure(true, rounds);
        var baseline2 = Measure(false, rounds);
        if (baseline.Snapshots != rounds ||
            optimized.Snapshots != rounds ||
            optimized.CpuAttempts + optimized.SuppressedDenied >
                optimized.Processes ||
            optimized2.CpuAttempts + optimized2.SuppressedDenied >
                optimized2.Processes ||
            optimized.Successes <= 0 || baseline.Successes <= 0 ||
            optimized.NativeDenied < 0 || optimized2.NativeDenied < 0)
            throw new InvalidOperationException(
                "CPU negative-cache benchmark double-counted or lost all native measurements.");

        // Process sets can vary between runs; no guaranteed time/speedup and
        // no fabricated result if protected processes are absent.
        self.Refresh();
        double selfCpuMs = (self.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        long selfRamDeltaKiB = (self.WorkingSet64 - ramBefore) / 1024;
        int handleDelta = self.HandleCount - handlesBefore;
        int threadDelta = self.Threads.Count - threadsBefore;
        double savedReadsPct = optimized.Processes <= 0
            ? 0 : optimized.SuppressedDenied * 100d / optimized.Processes;
        return "ADAPTIVE_CPU_DENIED_NATIVE_COST_SMOKE_OK " +
            $"baselineSnapshots={baseline.Snapshots} " +
            $"baselineProcesses={baseline.Processes} baselineCpuAttempts={baseline.CpuAttempts} " +
            $"baselineDenied={baseline.NativeDenied} baselineMs={baseline.WallMs:0.###} " +
            $"baselineAllocB={baseline.AllocatedB} " +
            $"cachedProcesses={optimized.Processes} cachedCpuAttempts={optimized.CpuAttempts} " +
            $"cachedDenied={optimized.NativeDenied} cachedSkipped={optimized.SuppressedDenied} " +
            $"cachedSkippedPctOfProcesses={savedReadsPct:0.##} " +
            $"cachedMs={optimized.WallMs:0.###} cachedAllocB={optimized.AllocatedB} " +
            $"cached2Ms={optimized2.WallMs:0.###} cached2AllocB={optimized2.AllocatedB} " +
            $"baseline2Ms={baseline2.WallMs:0.###} baseline2AllocB={baseline2.AllocatedB} " +
            $"selfCpuMs={selfCpuMs:0.###} selfRamDeltaKiB={selfRamDeltaKiB} " +
            $"selfHandleDelta={handleDelta} selfThreadDelta={threadDelta} " +
            "readOnly=true noSystemChanges=true noPriorityWrites=true";
    }
}
