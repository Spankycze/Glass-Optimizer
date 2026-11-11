using System;
using System.Diagnostics;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Read-only comparison using solely the current test process. Native
/// Process.Refresh() prevents confusing cached Process properties with
/// real native WorkingSet64 retrieval. No mutations of Windows/app state.
/// </summary>
internal static class AiWorkingSetNativeCostSmoke
{
    internal static string RunFunctionalSmoke()
    {
        const int rounds = 256;
        const int stride = 4; // Representative sparse refresh (8s / 2s CPU).
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long ramBefore = process.WorkingSet64;
        int handlesBefore = process.HandleCount;
        int threadsBefore = process.Threads.Count;
        TimeSpan cpuBefore = process.TotalProcessorTime;

        static (double ElapsedMs, long AllocatedB, int Reads, long LastBytes)
            Measure(Process process, int rounds, int stride)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            int reads = 0;
            long currentBytes = 0;
            for (int i = 0; i < rounds; i++)
            {
                if ((i % stride) == 0)
                {
                    process.Refresh();
                    currentBytes = process.WorkingSet64;
                    reads++;
                }
                if (currentBytes <= 0)
                    throw new InvalidOperationException("Owned process RAM sample unavailable.");
            }
            return (Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocated,
                reads, currentBytes);
        }

        // Alternate the order of runs to reduce warm-up and CPU cache bias.
        var before = Measure(process, rounds, 1);
        var after = Measure(process, rounds, stride);
        var afterAgain = Measure(process, rounds, stride);
        var beforeAgain = Measure(process, rounds, 1);
        process.Refresh();
        double cpuDeltaMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        long ramDeltaKiB = (process.WorkingSet64 - ramBefore) / 1024;
        int handlesDelta = process.HandleCount - handlesBefore;
        int threadsDelta = process.Threads.Count - threadsBefore;
        if (before.Reads != rounds || after.Reads != rounds / stride ||
            afterAgain.Reads != rounds / stride || beforeAgain.Reads != rounds ||
            before.LastBytes <= 0 || after.LastBytes <= 0 ||
            afterAgain.LastBytes <= 0 || beforeAgain.LastBytes <= 0)
            throw new InvalidOperationException("Native RAM benchmark violated scan cadence.");

        return $"AI_WORKING_SET_NATIVE_COST_SMOKE_OK baselineNativeReads={before.Reads} " +
            $"sparseNativeReads={after.Reads} reductionPct={100 * (1 - (double)after.Reads / before.Reads):0.##} " +
            $"baselineMs={before.ElapsedMs:0.###} sparseMs={after.ElapsedMs:0.###} " +
            $"sparseSecondMs={afterAgain.ElapsedMs:0.###} baselineSecondMs={beforeAgain.ElapsedMs:0.###} " +
            $"baselineAllocB={before.AllocatedB} sparseAllocB={after.AllocatedB} " +
            $"selfCpuMs={cpuDeltaMs:0.###} selfRamDeltaKiB={ramDeltaKiB} " +
            $"selfHandleDelta={handlesDelta} selfThreadDelta={threadsDelta} " +
            "onlyOwnedTestProcess=true nativeReadsOnly=true writes=0";
    }
}
