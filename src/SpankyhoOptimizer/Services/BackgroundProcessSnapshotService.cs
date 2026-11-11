using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpankyhoOptimizer.Services;

internal readonly record struct BackgroundProcessStats(int Count, long WorkingSetBytes, double CpuSeconds);

// Query the Windows process table once for the complete Background Activity page.
// The old implementation enumerated the entire table for each of 16 process names.
internal static class BackgroundProcessSnapshotService
{
    // AI only needs presence/absence; never fetch expensive native
    // WorkingSet/TotalProcessorTime when rendering is not involved.
    internal static bool ShouldReadResourceCounters(bool includeDormant) =>
        !includeDormant;

    internal static Dictionary<string, BackgroundProcessStats> Capture(
        IReadOnlyCollection<string> names, CancellationToken token,
        bool readResourceCounters = true)
    {
        // The Background Activity page passes an immutable, prebuilt name set.
        // Reuse it instead of rehashing every name on each refresh.
        IReadOnlySet<string> wanted = PrepareNames(names);
        token.ThrowIfCancellationRequested();
        if (wanted.Count == 0)
            return new Dictionary<string, BackgroundProcessStats>(StringComparer.OrdinalIgnoreCase);
        var metrics = new Dictionary<string, BackgroundProcessStats>(wanted.Count, StringComparer.OrdinalIgnoreCase);
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string name = process.ProcessName;
                    if (!wanted.Contains(name)) continue;
                    long ws = 0;
                    double cpuSeconds = 0;
                    if (readResourceCounters)
                    {
                        try
                        {
                            ws = Math.Max(0, process.WorkingSet64);
                            cpuSeconds = Math.Max(0, process.TotalProcessorTime.TotalSeconds);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                        {
                            // Still count a matching process if its protected counters cannot be read.
                        }
                    }
                    Add(metrics, name, ws, cpuSeconds);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Processes may exit between enumeration and reading their names.
                }
            }
        }
        finally
        {
            // A single native Dispose failure must not prevent releasing the
            // remaining process handles acquired in the same snapshot.
            ProcessSnapshotCleanup.DisposeAll(processes);
        }
        return metrics;
    }

    internal static IReadOnlySet<string> PrepareNames(IReadOnlyCollection<string> names) =>
        names is HashSet<string> ready && ReferenceEquals(ready.Comparer, StringComparer.OrdinalIgnoreCase)
            ? ready
            : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    internal static void Add(Dictionary<string, BackgroundProcessStats> metrics,
        string name, long workingSetBytes, double cpuSeconds)
    {
        // A matching process previously hashed its name twice (lookup then
        // indexed replacement). The per-capture dictionary is exclusively
        // owned here; one mutable value-ref avoids the redundant lookup.
        ref BackgroundProcessStats slot = ref CollectionsMarshal.GetValueRefOrAddDefault(
            metrics, name, out _);
        slot = new BackgroundProcessStats(
            slot.Count + 1,
            slot.WorkingSetBytes + Math.Max(0, workingSetBytes),
            slot.CpuSeconds + Math.Max(0, cpuSeconds));
    }
}
