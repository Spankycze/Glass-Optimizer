using System;
using System.Diagnostics;

namespace SpankyhoOptimizer.Services;

public sealed record PerformanceSnapshot(long WorkingSetMb, long PrivateMb, long ManagedMb, int Handles, int Gen0, int Gen1, int Gen2);

public static class PerformanceTelemetryService
{
    public static IDisposable Measure(string area, string operation) => new Scope(area, operation);

    public static PerformanceSnapshot GetSnapshot()
    {
        using var p = Process.GetCurrentProcess();
        return new PerformanceSnapshot(
            p.WorkingSet64 / 1024 / 1024,
            p.PrivateMemorySize64 / 1024 / 1024,
            GC.GetTotalMemory(false) / 1024 / 1024,
            p.HandleCount,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    public static void SnapshotMemory(string area)
    {
        try
        {
            var s = GetSnapshot();
            LogService.Info("perf", $"MEM area={area} ws={s.WorkingSetMb}MB private={s.PrivateMb}MB managed={s.ManagedMb}MB handles={s.Handles} gc={s.Gen0}/{s.Gen1}/{s.Gen2}");
        }
        catch { }
    }
    private sealed class Scope : IDisposable
    {
        private readonly string _area;
        private readonly string _operation;
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private readonly int _startedThread = Environment.CurrentManagedThreadId;
        private readonly long _startedAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        private int _disposed;

        public Scope(string area, string operation)
        {
            _area = area;
            _operation = operation;
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            double elapsedMs = Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
            bool sameThread = Environment.CurrentManagedThreadId == _startedThread;
            long allocatedKb = sameThread
                ? Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - _startedAllocatedBytes) / 1024
                : -1;
            if (elapsedMs >= 100 || allocatedKb >= 1024)
                LogService.Info("perf", $"OP area={_area} op={_operation} ms={elapsedMs:F2} allocatedKB={(sameThread ? allocatedKb.ToString() : "NA")} crossThread={!sameThread}");
        }
    }
}
