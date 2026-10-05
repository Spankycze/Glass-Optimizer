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
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly long _startMem = GC.GetTotalMemory(false);

        public Scope(string area, string operation)
        {
            _area = area;
            _operation = operation;
        }

        public void Dispose()
        {
            _sw.Stop();
            long deltaKb = (GC.GetTotalMemory(false) - _startMem) / 1024;
            if (_sw.ElapsedMilliseconds >= 100 || Math.Abs(deltaKb) >= 1024)
                LogService.Info("perf", $"OP area={_area} op={_operation} ms={_sw.ElapsedMilliseconds} managedDelta={deltaKb}KB");
        }
    }
}
