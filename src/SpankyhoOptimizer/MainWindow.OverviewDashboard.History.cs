using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static double OverviewHistoryTimeX(
        DateTime sampleUtc, DateTime cutoff, DateTime rightEdge)
    {
        double duration = (rightEdge - cutoff).TotalSeconds;
        if (duration <= 0) return 512;
        return 38 + 474 * Math.Clamp(
            (sampleUtc - cutoff).TotalSeconds / duration, 0, 1);
    }

    // A per-metric 96-column envelope retains real short peaks and minima,
    // unlike taking every Nth history sample. Missing native counters and
    // >8s pauses terminate independent native path figures. At steady load
    // there are at most two extrema/column (~192 points/metric) rather than
    // rebuilding thousands of native points on each 4-second UI refresh.
    private static int RenderOverviewMetricHistory(
        System.Collections.Generic.IReadOnlyList<OverviewPerfSample> history,
        int first, DateTime cutoff, DateTime rightEdge,
        OverviewMetricKind metric, OverviewSegmentedChart chart)
    {
        chart.Reset();
        const int columns = 96;
        int bucket = -1, lowIndex = -1, highIndex = -1;
        int lastIndex = -1, emittedIndex = -1;
        double low = 0, high = 0;
        DateTime previousValidUtc = default;
        int rendered = 0;

        void Emit(int index)
        {
            if (index < 0 || index == emittedIndex) return;
            OverviewPerfSample item = history[index];
            chart.Add(OverviewMetricValue(in item, metric),
                OverviewHistoryTimeX(item.Utc, cutoff, rightEdge));
            emittedIndex = index;
            rendered++;
        }

        void Flush(bool includeLast)
        {
            if (lowIndex < 0) return;
            if (lowIndex < highIndex)
            {
                Emit(lowIndex); Emit(highIndex);
            }
            else
            {
                Emit(highIndex); Emit(lowIndex);
            }
            if (includeLast) Emit(lastIndex);
            lowIndex = highIndex = lastIndex = -1;
        }

        for (int i = Math.Max(first, 0); i < history.Count; i++)
        {
            OverviewPerfSample sample = history[i];
            double? metricValue = OverviewMetricValue(in sample, metric);
            bool valid = metricValue is { } v && double.IsFinite(v);
            bool gap = previousValidUtc != default &&
                (sample.Utc <= previousValidUtc ||
                 sample.Utc - previousValidUtc > TimeSpan.FromSeconds(8));
            if (!valid || gap)
            {
                Flush(true);
                chart.FinishRun();
                bucket = -1;
                emittedIndex = -1;
                previousValidUtc = default;
            }
            if (!valid) continue;
            double value = metricValue!.Value;
            double x = OverviewHistoryTimeX(sample.Utc, cutoff, rightEdge);
            int nextBucket = Math.Clamp(
                (int)((x - 38) * columns / 474), 0, columns - 1);
            if (bucket != nextBucket)
            {
                Flush(false);
                bucket = nextBucket;
                lowIndex = highIndex = lastIndex = i;
                low = high = value;
                if (emittedIndex < 0) Emit(i);
            }
            else
            {
                if (value < low) { low = value; lowIndex = i; }
                if (value > high) { high = value; highIndex = i; }
                lastIndex = i;
            }
            previousValidUtc = sample.Utc;
        }
        Flush(true);
        chart.FinishRun();
        return rendered;
    }

    // Reusable per-metric cursor. Unlike the legacy five-pass traversal,
    // sample time, x and bucket are computed ONCE per history item.
    // RAM fill shares RAM's extrema instead of rescanning all samples.
    private sealed class OverviewEnvelopeCursor
    {
        private System.Collections.Generic.IReadOnlyList<OverviewPerfSample>? _history;
        private OverviewSegmentedChart? _line;
        private OverviewSegmentedChart? _fill;
        private OverviewMetricKind _metric;
        private DateTime _cutoff, _rightEdge, _previousValidUtc;
        private int _bucket, _lowIndex, _highIndex, _lastIndex, _emittedIndex;
        private double _low, _high;

        public void Begin(
            System.Collections.Generic.IReadOnlyList<OverviewPerfSample> history,
            DateTime cutoff, DateTime rightEdge, OverviewMetricKind metric,
            OverviewSegmentedChart line, OverviewSegmentedChart? fill = null)
        {
            _history = history;
            _metric = metric;
            _line = line;
            _fill = fill;
            _cutoff = cutoff;
            _rightEdge = rightEdge;
            _previousValidUtc = default;
            _bucket = _lowIndex = _highIndex = _lastIndex = _emittedIndex = -1;
            _low = _high = 0;
            line.Reset();
            fill?.Reset();
        }

        private void Emit(int index)
        {
            if (index < 0 || index == _emittedIndex || _history == null)
                return;
            OverviewPerfSample item = _history[index];
            double x = OverviewHistoryTimeX(item.Utc, _cutoff, _rightEdge);
            double? value = OverviewMetricValue(in item, _metric);
            _line?.Add(value, x);
            _fill?.Add(value, x);
            _emittedIndex = index;
        }

        private void Flush(bool includeLast)
        {
            if (_lowIndex < 0) return;
            if (_lowIndex < _highIndex)
            {
                Emit(_lowIndex); Emit(_highIndex);
            }
            else
            {
                Emit(_highIndex); Emit(_lowIndex);
            }
            if (includeLast) Emit(_lastIndex);
            _lowIndex = _highIndex = _lastIndex = -1;
        }

        public void Push(int index, in OverviewPerfSample sample,
            int nextBucket)
        {
            double? metricValue = OverviewMetricValue(in sample, _metric);
            bool valid = metricValue is { } v && double.IsFinite(v);
            bool gap = _previousValidUtc != default &&
                (sample.Utc <= _previousValidUtc ||
                 sample.Utc - _previousValidUtc > TimeSpan.FromSeconds(8));
            if (!valid || gap)
            {
                Flush(true);
                _line?.FinishRun();
                _fill?.FinishRun();
                _bucket = -1;
                _emittedIndex = -1;
                _previousValidUtc = default;
            }
            if (!valid) return;

            double value = metricValue!.Value;
            if (_bucket != nextBucket)
            {
                Flush(false);
                _bucket = nextBucket;
                _lowIndex = _highIndex = _lastIndex = index;
                _low = _high = value;
                if (_emittedIndex < 0) Emit(index);
            }
            else
            {
                if (value < _low) { _low = value; _lowIndex = index; }
                if (value > _high) { _high = value; _highIndex = index; }
                _lastIndex = index;
            }
            _previousValidUtc = sample.Utc;
        }

        public void End()
        {
            Flush(true);
            _line?.FinishRun();
            _fill?.FinishRun();
            // Do not retain a navigated-away UI tree through long-lived cursors.
            _line = null;
            _fill = null;
            _history = null;
        }
    }

    private readonly OverviewEnvelopeCursor[] _overviewEnvelopeCursors =
    {
        new(), new(), new(), new()
    };

    // One chronological history traversal for four series + RAM area.
    // Cursor state is pooled for the life of MainWindow; no per-refresh
    // LINQ enumerators, delegate captures, arrays or native figure churn.
    private static void RenderOverviewHistoryBatch(
        System.Collections.Generic.IReadOnlyList<OverviewPerfSample> history,
        int first, DateTime cutoff, DateTime rightEdge,
        OverviewEnvelopeCursor[] cursors,
        OverviewSegmentedChart cpu, OverviewSegmentedChart gpu,
        OverviewSegmentedChart ram, OverviewSegmentedChart disk,
        OverviewSegmentedChart ramFill)
    {
        const int columns = 96;
        cursors[0].Begin(history, cutoff, rightEdge, OverviewMetricKind.Cpu, cpu);
        cursors[1].Begin(history, cutoff, rightEdge, OverviewMetricKind.Gpu, gpu);
        cursors[2].Begin(history, cutoff, rightEdge, OverviewMetricKind.Ram, ram, ramFill);
        cursors[3].Begin(history, cutoff, rightEdge, OverviewMetricKind.Disk, disk);
        try
        {
            for (int i = Math.Max(first, 0); i < history.Count; i++)
            {
                OverviewPerfSample sample = history[i];
                double x = OverviewHistoryTimeX(sample.Utc, cutoff, rightEdge);
                int bucket = Math.Clamp(
                    (int)((x - 38) * columns / 474), 0, columns - 1);
                cursors[0].Push(i, in sample, bucket);
                cursors[1].Push(i, in sample, bucket);
                cursors[2].Push(i, in sample, bucket);
                cursors[3].Push(i, in sample, bucket);
            }
        }
        finally
        {
            for (int i = 0; i < cursors.Length; i++)
                cursors[i].End();
        }
    }


    internal static string RunOverviewHistoryBatchParitySmoke()
    {
        DateTime start = new(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc);
        DateTime end = start.AddMinutes(60);
        var history = new System.Collections.Generic.List<OverviewPerfSample>(1800);
        for (int i = 0; i < 1800; i++)
            history.Add(new OverviewPerfSample(start.AddSeconds(i * 2),
                i == 1041 ? 100 : i == 300 ? null : i == 1300 ? 0 : 25 + i % 10,
                i % 239 == 0 ? null : i % 70,
                i % 501 == 0 ? null : 40 + i % 15,
                i % 311 == 0 ? null : i % 90));

        var color = new SolidColorBrush(Microsoft.UI.Colors.Blue);
        var before = new OverviewSegmentedChart[5];
        var after = new OverviewSegmentedChart[5];
        for (int i = 0; i < 5; i++)
        {
            before[i] = new OverviewSegmentedChart(i == 4 ? null : color,
                i == 4 ? color : null);
            after[i] = new OverviewSegmentedChart(i == 4 ? null : color,
                i == 4 ? color : null);
        }
        var cursors = new[]
        {
            new OverviewEnvelopeCursor(), new OverviewEnvelopeCursor(),
            new OverviewEnvelopeCursor(), new OverviewEnvelopeCursor()
        };

        void Legacy()
        {
            RenderOverviewMetricHistory(history, 0, start, end, OverviewMetricKind.Cpu, before[0]);
            RenderOverviewMetricHistory(history, 0, start, end, OverviewMetricKind.Gpu, before[1]);
            RenderOverviewMetricHistory(history, 0, start, end, OverviewMetricKind.Ram, before[2]);
            RenderOverviewMetricHistory(history, 0, start, end, OverviewMetricKind.Disk, before[3]);
            RenderOverviewMetricHistory(history, 0, start, end, OverviewMetricKind.Ram, before[4]);
        }

        void Batch() => RenderOverviewHistoryBatch(history, 0, start, end, cursors,
            after[0], after[1], after[2], after[3], after[4]);

        Legacy();
        Batch();
        for (int i = 0; i < 5; i++)
        {
            if (before[i].SegmentCount != after[i].SegmentCount)
                throw new InvalidOperationException($"History batch segment count mismatch metric={i}.");
            for (int s = 0; s < before[i].SegmentCount; s++)
            {
                var a = before[i].GetSegment(s)!;
                var b = after[i].GetSegment(s)!;
                if (a.StartPoint.X != b.StartPoint.X || a.StartPoint.Y != b.StartPoint.Y ||
                    a.IsClosed != b.IsClosed ||
                    a.Segments.Count != b.Segments.Count)
                    throw new InvalidOperationException($"History batch figure mismatch metric={i}.");
                var aLine = (PolyLineSegment)a.Segments[0];
                var bLine = (PolyLineSegment)b.Segments[0];
                if (aLine.Points.Count != bLine.Points.Count)
                    throw new InvalidOperationException($"History batch points mismatch metric={i}.");
                for (int k = 0; k < aLine.Points.Count; k++)
                    if (aLine.Points[k].X != bLine.Points[k].X ||
                        aLine.Points[k].Y != bLine.Points[k].Y)
                        throw new InvalidOperationException(
                            $"History batch path coordinates differ metric={i} segment={s} point={k}.");
            }
        }

        for (int i = 0; i < 3; i++) { Legacy(); Batch(); }
        const int frames = 24;
        long oldAlloc = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < frames; i++) Legacy();
        double oldMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        oldAlloc = GC.GetAllocatedBytesForCurrentThread() - oldAlloc;

        long newAlloc = GC.GetAllocatedBytesForCurrentThread();
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < frames; i++) Batch();
        double newMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        newAlloc = GC.GetAllocatedBytesForCurrentThread() - newAlloc;
        // Native WinUI PointCollection writes dominate both variants and
        // allocation samples vary with UI dispatcher/GC state. Guard only
        // meaningful regressions (>20% plus 64 KiB), not <2% sampling noise.
        if (newAlloc > oldAlloc * 1.20 + 65_536)
            throw new InvalidOperationException(
                $"One-pass chart allocation regression oldAlloc={oldAlloc} newAlloc={newAlloc}.");
        return $"OVERVIEW_HISTORY_BATCH_PARITY_OK source=1800 metrics=5 pathsEqual=true " +
            $"oldMs={oldMs:F2} newMs={newMs:F2} oldAllocB={oldAlloc} newAllocB={newAlloc} " +
            "frames=24 oneHistoryScan=true RAMfillShared=true writes=0";
    }

    internal static string RunOverviewHistoryEnvelopeSmoke()
    {
        DateTime start = new(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc);
        DateTime right = start.AddMinutes(60);
        var chart = new OverviewSegmentedChart(
            new SolidColorBrush(Microsoft.UI.Colors.Blue));
        var history = new System.Collections.Generic.List<OverviewPerfSample>(1800);
        for (int i = 0; i < 1800; i++)
        {
            double? load = i == 1001 ? 100 : i == 1300 ? null : i == 1500 ? 0 : 30;
            history.Add(new OverviewPerfSample(start.AddSeconds(i * 2), load, 10, 40, 5));
        }
        int firstCount = RenderOverviewMetricHistory(
            history, 0, start, right, OverviewMetricKind.Cpu, chart);
        if (chart.SegmentCount != 2 || firstCount > 225 ||
            firstCount < 5 || chart.AllocatedSegments != 2)
            throw new InvalidOperationException("Long-range graph not bounded or gap skipped.");

        bool peakFound = false, trueZeroFound = false;
        for (int s = 0; s < chart.SegmentCount; s++)
        {
            var figure = chart.GetSegment(s)!;
            if (figure.StartPoint.Y <= 10.01) peakFound = true;
            if (figure.StartPoint.Y >= 77.99) trueZeroFound = true;
            foreach (var segment in figure.Segments)
            {
                if (segment is not PolyLineSegment line) continue;
                foreach (var point in line.Points)
                {
                    if (point.Y <= 10.01) peakFound = true;
                    if (point.Y >= 77.99) trueZeroFound = true;
                }
            }
        }
        if (!peakFound || !trueZeroFound)
            throw new InvalidOperationException("Sparse bucket downsampling lost spike or real 0%.");

        DateTime shortStart = right.AddSeconds(-4);
        if (OverviewHistoryTimeX(shortStart, start, right) < 511 ||
            Math.Abs(OverviewHistoryTimeX(start, start, right) - 38) > .001 ||
            Math.Abs(OverviewHistoryTimeX(right, start, right) - 512) > .001)
            throw new InvalidOperationException("Partial history stretched across selected time range.");

        int initialPool = chart.AllocatedSegments;
        for (int i = 0; i < 40; i++)
            RenderOverviewMetricHistory(
                history, 0, start, right, OverviewMetricKind.Cpu, chart);
        if (chart.AllocatedSegments != initialPool)
            throw new InvalidOperationException("History envelope reallocates native figures.");
        return "OVERVIEW_HISTORY_ENVELOPE_OK source=1800 bins=96 " +
            "realSpike=true trueZero=true missingGap=true timeAnchored=true " +
            "frames=40 figurePoolStable=true writes=0";
    }

    // A rare driver/PDH failure can alternate valid/missing readings for
    // hours. PathFigures beyond the bounded cache stay visible THIS frame
    // but are never retained in the pool afterward. Once the sensor recovers,
    // shrink old cached native figures after a grace interval.
    internal static string RunOverviewNativeFigureRetentionSmoke()
    {
        var chart = new OverviewSegmentedChart(
            new SolidColorBrush(Microsoft.UI.Colors.Blue));
        const int values = 600;
        for (int frame = 0; frame < 3; frame++)
        {
            chart.Reset();
            for (int i = 0; i < values; i++)
                chart.Add(i % 2 == 0 ? i % 100 : null, 38 + i * .1);
            chart.FinishRun();
            if (chart.SegmentCount != values / 2 ||
                chart.AllocatedSegments != 96 ||
                chart.GetSegment(values / 2 - 1) == null)
                throw new InvalidOperationException(
                    "Overview prolonged native sensor failure retained unbounded figures.");
        }
        for (int frame = 0; frame < 16; frame++)
        {
            chart.Reset();
            chart.Add(40, 38);
            chart.Add(41, 48);
            chart.FinishRun();
            if (chart.SegmentCount != 1 ||
                chart.GetSegment(0)?.StartPoint.X != 38)
                throw new InvalidOperationException("Overview figure pool shrank a live line.");
        }
        if (chart.AllocatedSegments != 24)
            throw new InvalidOperationException(
                $"Overview recovered native figure pool did not shrink: {chart.AllocatedSegments}.");
        chart.Reset();
        if (chart.SegmentCount != 0)
            throw new InvalidOperationException("Overview stale figures survived history reset.");
        return "OVERVIEW_NATIVE_FIGURE_RETENTION_OK stormRuns=300 " +
            "retainedMax=96 recoveredRetained=24 sparseFrames=16 " +
            "livePathPreserved=true emptyCleared=true writes=0";
    }


    internal static string RunOverviewSegmentedChartSmoke()
    {
        var blue = new SolidColorBrush(Microsoft.UI.Colors.Blue);
        var cpu = new OverviewSegmentedChart(blue);
        var fill = new OverviewSegmentedChart(null, blue);
        double?[] readings = { null, 25, 50, null, 0, 75, null, null, 100 };
        for (int i = 0; i < readings.Length; i++)
        {
            cpu.Add(readings[i], 38 + i * 10);
            fill.Add(readings[i], 38 + i * 10);
        }
        cpu.FinishRun();
        fill.FinishRun();
        if (cpu.SegmentCount != 3 || fill.SegmentCount != 3 ||
            cpu.AllocatedSegments != 3 || fill.AllocatedSegments != 3 ||
            cpu.GetSegment(0)?.StartPoint.X != 48 ||
            cpu.GetSegment(1)?.StartPoint.X != 78 ||
            cpu.GetSegment(2)?.StartPoint.X != 118 ||
            fill.GetSegment(0)?.StartPoint.Y != 78 ||
            fill.GetSegment(1)?.StartPoint.Y != 78 ||
            fill.GetSegment(2)?.IsClosed != true)
            throw new InvalidOperationException("Missing telemetry bridged a graphical gap.");
        var first = cpu.GetSegment(0);
        var firstFill = fill.GetSegment(0);
        for (int frame = 0; frame < 120; frame++)
        {
            cpu.Reset();
            fill.Reset();
            for (int i = 0; i < readings.Length; i++)
            {
                double? value = frame % 2 == 0 ? readings[i] : i % 3 == 0 ? null : i * 9d;
                cpu.Add(value, 38 + i * 10);
                fill.Add(value, 38 + i * 10);
            }
            cpu.FinishRun();
            fill.FinishRun();
        }
        if (cpu.SegmentCount != 3 || fill.SegmentCount != 3 ||
            cpu.AllocatedSegments != 3 || fill.AllocatedSegments != 3 ||
            !ReferenceEquals(first, cpu.GetSegment(0)) ||
            !ReferenceEquals(firstFill, fill.GetSegment(0)))
            throw new InvalidOperationException("Segmented chart leaked/recreated native run figures.");
        cpu.Reset();
        fill.Reset();
        if (cpu.SegmentCount != 0 || fill.SegmentCount != 0)
            throw new InvalidOperationException("Expired chart retained stale plotted lines.");
        return "OVERVIEW_SEGMENTED_CHART_OK gaps=3 fillIsolated=true " +
            "frames=120 poolStable=3 singleton=true expiredCleared=true writes=0";
    }

    private static bool AppendOverviewOptionalMetricPoint(
        PointCollection points, double? percent, double x)
    {
        if (percent is not { } value || !double.IsFinite(value))
            return false;
        points.Add(new Windows.Foundation.Point(
            x, 78 - Math.Clamp(value, 0, 100) * .68));
        return true;
    }

    // Verifies that missing native samples remain missing through history and
    // actual detached WinUI chart points. No hardware probes or OS writes.
    internal static string RunOverviewOptionalTelemetrySmoke()
    {
        var cpu = new PointCollection();
        var gpu = new PointCollection();
        var disk = new PointCollection();
        DateTime now = new(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc);
        var unavailable = new OverviewPerfSample(now, null, null, 50, null);
        var idle = new OverviewPerfSample(now.AddSeconds(2), 0, 0, 50, 0);
        var loaded = new OverviewPerfSample(now.AddSeconds(4), 50, 75, 50, 40);
        if (AppendOverviewOptionalMetricPoint(cpu, unavailable.Cpu, 38) ||
            AppendOverviewOptionalMetricPoint(gpu, unavailable.Gpu, 38) ||
            AppendOverviewOptionalMetricPoint(disk, unavailable.Disk, 38) ||
            cpu.Count != 0 || gpu.Count != 0 || disk.Count != 0 ||
            !AppendOverviewOptionalMetricPoint(cpu, idle.Cpu, 138) ||
            !AppendOverviewOptionalMetricPoint(gpu, idle.Gpu, 138) ||
            !AppendOverviewOptionalMetricPoint(disk, idle.Disk, 138) ||
            !AppendOverviewOptionalMetricPoint(cpu, loaded.Cpu, 238) ||
            !AppendOverviewOptionalMetricPoint(gpu, loaded.Gpu, 238) ||
            !AppendOverviewOptionalMetricPoint(disk, loaded.Disk, 238) ||
            cpu.Count != 2 || gpu.Count != 2 || disk.Count != 2 ||
            Math.Abs(cpu[0].Y - 78) > .001 ||
            Math.Abs(gpu[1].Y - 27) > .001 ||
            Math.Abs(disk[1].Y - 50.8) > .001 ||
            AppendOverviewOptionalMetricPoint(gpu, double.NaN, 338) ||
            AppendOverviewOptionalMetricPoint(disk, double.PositiveInfinity, 338) ||
            gpu.Count != 2 || disk.Count != 2)
            throw new InvalidOperationException(
                "Overview missing telemetry rendered as a false 0% graph dip.");

        // In kernel32 GetSystemTimes, kernelDelta includes idleDelta.
        if (ComputeOverviewCpuPercent(500, 750, 250) != 50 ||
            ComputeOverviewCpuPercent(0, 500, 500) != 100 ||
            ComputeOverviewCpuPercent(1000, 1000, 0) != 0 ||
            ComputeOverviewCpuPercent(1100, 1000, 0) != null ||
            ComputeOverviewCpuPercent(-1, 500, 500) != null ||
            ComputeOverviewCpuPercent(100, -1, 500) != null ||
            ComputeOverviewCpuPercent(100, 500, -1) != null ||
            ComputeOverviewCpuPercent(0, 0, 0) != null ||
            ComputeOverviewCpuPercent(0, long.MaxValue, 1) != null)
            throw new InvalidOperationException("CPU idle/native counter validity regression.");
        return "OVERVIEW_OPTIONAL_TELEMETRY_OK missingCpuGpuDisk=true " +
            "trueIdleZero=true recovery=true invalidFinite=true " +
            "counterRollback=true counterOverflow=true writes=0";
    }

    // Read-only synthetic native-memory edge cases, detached WinUI data.
    // Never allocates physical RAM, changes Windows settings or forces GC.
}
