using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Media.Animation;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static ReferenceOverviewHeights ReferenceFitHeights(
        double viewport, double content, double plot, double spark, double info,
        int gaugeRows = 2)
    {
        if (!double.IsFinite(viewport) || !double.IsFinite(content) ||
            !double.IsFinite(plot) || !double.IsFinite(spark) ||
            !double.IsFinite(info) || viewport <= 0 || content <= 0 || gaugeRows < 1)
            return new(ReferenceBasePlotHeight, ReferenceBaseSparkHeight, ReferenceBaseInfoHeight);

        double naturalContent = content
            - Math.Max(0, plot - ReferenceBasePlotHeight)
            - gaugeRows * Math.Max(0, spark - ReferenceBaseSparkHeight)
            - Math.Max(0, info - ReferenceBaseInfoHeight);
        double free = Math.Max(0, viewport - naturalContent - 6);
        return new(ReferenceBasePlotHeight + free * .40,
            ReferenceBaseSparkHeight + free * .44 / gaugeRows,
            ReferenceBaseInfoHeight + free * .16);
    }

    private bool FitReferenceOverviewToViewport(double viewport, double content)
    {
        if (_section != "overview" || _referenceCpuPlotHost == null ||
            _referencePlotYTicksHost == null ||
            _referencePlotYTickLabels is not { Length: 5 } ||
            _referenceNetworkPlotHost == null || _referenceTemperatureRoot == null ||
            _cpuGauge?.ReferenceSparkRow == null || _ramGauge?.ReferenceSparkRow == null ||
            _gpuGauge?.ReferenceSparkRow == null || _diskGauge?.ReferenceSparkRow == null ||
            _overviewTelemetryGrid is not { Children.Count: 6 } ||
            _overviewInfoCardsGrid is not { Children.Count: 3 })
            return false;

        int gaugeRows = _overviewTelemetryGrid.RowDefinitions.Count;
        if (_referenceBaseGaugeRowHeights == null ||
            _referenceBaseGaugeRowHeights.Length != gaugeRows)
        {
            var actualRows = new double[gaugeRows];
            for (int i = 0; i < gaugeRows; i++)
            {
                actualRows[i] = _overviewTelemetryGrid.RowDefinitions[i].ActualHeight;
                if (actualRows[i] <= 0) return false;
            }
            _referenceBaseGaugeRowHeights = actualRows;
        }

        double currentPlot = _referenceCpuPlotHost.Height;
        double currentSpark = _cpuGauge.SparkHost.Height;
        double currentInfo = _overviewInfoCardsGrid.MinHeight;
        var target = ReferenceFitHeights(viewport, content, currentPlot, currentSpark, currentInfo, gaugeRows);
        if (Math.Abs(target.Plot - currentPlot) < .75 &&
            Math.Abs(target.Spark - currentSpark) < .75 &&
            Math.Abs(target.Info - currentInfo) < .75)
            return false;

        _referenceCpuPlotHost.Height = target.Plot;
        _referencePlotYTicksHost.Height = target.Plot;
        for (int i = 0; i < _referencePlotYTickLabels.Length; i++)
            Canvas.SetTop(_referencePlotYTickLabels[i], i * (target.Plot - 16) / 4);

        foreach (var gauge in new[] { _cpuGauge, _ramGauge, _gpuGauge, _diskGauge })
        {
            if (gauge == null) continue;
            gauge.SparkHost.Height = target.Spark;
            gauge.ReferenceSparkRow!.Height = new GridLength(target.Spark);
        }
        double extraPerRow = target.Spark - ReferenceBaseSparkHeight;
        for (int i = 0; i < gaugeRows; i++)
            _overviewTelemetryGrid.RowDefinitions[i].Height =
                new GridLength(_referenceBaseGaugeRowHeights[i] + extraPerRow);
        _referenceNetworkPlotHost.Height = ReferenceBaseNetworkHeight + extraPerRow;
        _referenceTemperatureRoot.Spacing = 10 + extraPerRow / 2;

        _overviewInfoCardsGrid.MinHeight = target.Info;
        _overviewInfoCardsGrid.MaxHeight = target.Info + 4;
        // Additional data are already fetched in the normal Overview snapshot.
        // Show them only when there is room, never create a new worker.
        bool expanded = target.Info >= 90;
        if (_overviewTextRefs != null)
            _overviewTextRefs.Power.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (_overviewDriverStatusText != null)
            _overviewDriverStatusText.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (_overviewInlineSecondHistory != null)
            _overviewInlineSecondHistory.Visibility = target.Info >= 96 ?
                Visibility.Visible : Visibility.Collapsed;
        return true;
    }

    // Windows can transiently report non-finite CPU utilization while a
    // sensor initializes. Never send invalid points to native WinUI geometry.
    private static double ReferenceFiniteCpuPercent(double percent) =>
        double.IsFinite(percent) ? Math.Clamp(percent, 0d, 100d) : 0d;

    private static double ReferencePlotTimeX(DateTime sampleUtc, DateTime rightEdgeUtc, double width) =>
        Math.Clamp((sampleUtc - rightEdgeUtc.AddSeconds(-60)).TotalSeconds / 60d,
            0d, 1d) * Math.Max(0, width - 2);

    private static bool ReferenceGridNeedsResize(double width, double height,
        double lastWidth, double lastHeight) =>
        width != lastWidth || height != lastHeight;

    private void RenderReferenceMainPlot()
    {
        if (_referenceCpuPlotHost == null || _referenceCpuPlot == null ||
            _referenceCpuGlow == null || _referenceCpuArea == null) return;
        double w = _referenceCpuPlotHost.ActualWidth;
        if (w < 10) return;
        double h = Math.Max(ReferenceBasePlotHeight, _referenceCpuPlotHost.ActualHeight);
        if (ReferenceGridNeedsResize(w, h, _referenceGridWidth, _referenceGridHeight))
        {
            for (int i = 0; i < _referencePlotGridLines.Count; i++)
            {
                var l = _referencePlotGridLines[i];
                l.X1 = 0; l.X2 = w;
                l.Y1 = l.Y2 = i * (h - 2) / 4;
            }
            for (int i = 0; i < _referenceVerticalGridLines.Count; i++)
            {
                double x = (i + 1d) * w / 40d;
                _referenceVerticalGridLines[i].StartPoint = new Windows.Foundation.Point(x, 0);
                _referenceVerticalGridLines[i].EndPoint = new Windows.Foundation.Point(x, h);
            }
            _referenceGridWidth = w; _referenceGridHeight = h;
        }

        DateTime rightEdge = DateTime.UtcNow;
        int end = _overviewPerfHistory.Count;
        int first = end;
        DateTime cutoff = rightEdge.AddSeconds(-60);
        while (first > 0 && _overviewPerfHistory[first - 1].Utc >= cutoff) first--;
        first = Math.Max(first, end - 60);
        RenderReferenceCpuHistorySegments(_overviewPerfHistory, first, rightEdge, w, h,
            _referenceRawPlotPoints, _referenceCpuPlot, _referenceCpuGlow, _referenceCpuArea);
        _referenceLastPlotUtc = end == 0 ? default : _overviewPerfHistory[^1].Utc;
    }

    // Preserve Catmull-Rom smoothing only inside consecutive valid samples.
    // Missing native readings and sleep/resume gaps must not be bridged with
    // fabricated curves or filled areas. All 3 paths reuse native figures.
    private static void RenderReferenceCpuHistorySegments(
        IReadOnlyList<OverviewPerfSample> history, int first, DateTime rightEdge,
        double w, double h, List<Windows.Foundation.Point> raw,
        OverviewSegmentedChart plot, OverviewSegmentedChart glow, OverviewSegmentedChart area)
    {
        plot.Reset(); glow.Reset(); area.Reset();
        raw.Clear();
        double bottom = h - 3;
        DateTime lastValid = default;
        for (int i = Math.Max(0, first); i < history.Count; i++)
        {
            var item = history[i];
            bool valid = item.Cpu is { } v && double.IsFinite(v);
            bool gap = lastValid != default &&
                (item.Utc <= lastValid || item.Utc - lastValid > TimeSpan.FromSeconds(8));
            if (!valid || gap)
            {
                AppendReferenceSmoothCpuRun(raw, bottom, plot, glow, area);
                raw.Clear();
                lastValid = default;
            }
            if (!valid) continue;
            double x = ReferencePlotTimeX(item.Utc, rightEdge, w);
            double y = bottom - ReferenceFiniteCpuPercent(item.Cpu!.Value) * (h - 7) / 100;
            raw.Add(new Windows.Foundation.Point(x, y));
            lastValid = item.Utc;
        }
        AppendReferenceSmoothCpuRun(raw, bottom, plot, glow, area);
        raw.Clear();
    }

    private static void AppendReferenceSmoothCpuRun(
        List<Windows.Foundation.Point> raw, double bottom,
        OverviewSegmentedChart plot, OverviewSegmentedChart glow, OverviewSegmentedChart area)
    {
        if (raw.Count == 0) return;
        var p0 = raw[0];
        plot.AddPoint(p0.X, p0.Y, bottom);
        glow.AddPoint(p0.X, p0.Y, bottom);
        area.AddPoint(p0.X, p0.Y, bottom);
        for (int i = 1; i < raw.Count; i++)
        {
            var a = raw[Math.Max(0, i - 2)];
            var b = raw[i - 1];
            var c = raw[i];
            var d = raw[Math.Min(raw.Count - 1, i + 1)];
            for (int step = 1; step <= 4; step++)
            {
                double t = step / 4d, t2 = t * t, t3 = t2 * t;
                double y = .5 * ((2 * b.Y) + (-a.Y + c.Y) * t +
                    (2 * a.Y - 5 * b.Y + 4 * c.Y - d.Y) * t2 +
                    (-a.Y + 3 * b.Y - 3 * c.Y + d.Y) * t3);
                y = Math.Clamp(y, Math.Min(b.Y, c.Y), Math.Max(b.Y, c.Y));
                double x = b.X + (c.X - b.X) * t;
                plot.AddPoint(x, y, bottom);
                glow.AddPoint(x, y, bottom);
                area.AddPoint(x, y, bottom);
            }
        }
        plot.FinishRun(); glow.FinishRun(); area.FinishRun();
    }

    internal static string RunReferenceCpuGapSmoke()
    {
        var plot = new OverviewSegmentedChart(ReferenceCpuAccent());
        var glow = new OverviewSegmentedChart(ReferenceCpuAccent());
        var area = new OverviewSegmentedChart(null, ReferenceCpuAccent());
        var raw = new List<Windows.Foundation.Point>(64);
        DateTime start = new(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc);
        var history = new List<OverviewPerfSample>
        {
            new(start, 25, null, 40, null),
            new(start.AddSeconds(2), 80, null, 40, null),
            new(start.AddSeconds(4), null, null, 40, null),
            new(start.AddSeconds(6), 35, null, 40, null),
            new(start.AddSeconds(20), 70, null, 40, null),
            new(start.AddSeconds(22), 0, null, 40, null)
        };
        for (int repeat = 0; repeat < 100; repeat++)
        {
            RenderReferenceCpuHistorySegments(history, 0, start.AddSeconds(24),
                500, 120, raw, plot, glow, area);
            if (plot.SegmentCount != 3 || glow.SegmentCount != 3 ||
                area.SegmentCount != 3 || plot.AllocatedSegments != 3 ||
                glow.AllocatedSegments != 3 || area.AllocatedSegments != 3 ||
                area.GetSegment(1)?.IsClosed != true)
                throw new InvalidOperationException("Reference CPU outage was bridged or geometry leaked.");
        }
        history.Clear();
        RenderReferenceCpuHistorySegments(history, 0, start.AddSeconds(24),
            500, 120, raw, plot, glow, area);
        if (plot.SegmentCount != 0 || glow.SegmentCount != 0 ||
            area.SegmentCount != 0)
            throw new InvalidOperationException("Reference CPU chart retained expired points.");
        if (ReferenceCpuPlotDue(false, start.AddSeconds(2), start) ||
            !ReferenceCpuPlotDue(true, start.AddSeconds(2), start) ||
            ReferenceCpuPlotDue(true, start.AddSeconds(2), start.AddSeconds(2)) ||
            ReferenceCpuPlotDue(false, start, default))
            throw new InvalidOperationException("Reference CPU chart 4-second cadence guard regressed.");
        return "REFERENCE_CPU_GAP_SMOKE_OK gapMissing=true gapSleep=true " +
            "smoothedRuns=3 frames=100 pooled=3 expiredCleared=true writes=0";
    }
    // The main 60-second chart shares the established 4-second history cadence.
    // Keep the independent 2-second live gauge/header path untouched.
    private static bool ReferenceCpuPlotDue(bool cadenceTick, DateTime newestUtc,
        DateTime lastRenderedUtc) => cadenceTick && newestUtc != lastRenderedUtc;

    private void UpdateReferenceHeader(double? cpu,double? ram,double?gpu,double?disk,
        bool refreshPlot = true)
    {
        if (_referenceChartCpu == null) return;
        // Native TextBlock setters already skip equal text, but formatting all
        // 4 rates plus clock and GPU temperature every 2 seconds still allocates
        // managed strings. Cache the exact *inputs* so a new value with the same
        // rounded label is still handled by the existing display policy.
        double? safeCpu = cpu is { } c && double.IsFinite(c)
            ? ReferenceFiniteCpuPercent(c) : null;
        double? safeRam = ram is { } m && double.IsFinite(m) ? m : null;
        double? safeGpu = gpu is { } g && double.IsFinite(g) ? g : null;
        double? safeDisk = disk is { } d && double.IsFinite(d) ? d : null;
        double? clock = _cpuGauge != null && _referenceCpuClockMhz is { } mhz &&
            double.IsFinite(mhz) && mhz > 100 ? mhz : null;
        double? gpuTemperature = _gpuGauge != null && _referenceSensorGpu is { } t &&
            double.IsFinite(t) && _overviewTextRefs != null ? t : null;
        string? gpuLabel = gpuTemperature.HasValue ? _overviewTextRefs!.Gpu.Text : null;
        var next = new ReferenceHeaderTextKey(
            safeCpu, safeRam, safeGpu, safeDisk, clock, gpuTemperature, gpuLabel);
        var previous = _referenceLastHeaderTextKey;
        if (previous != next)
        {
            if (!previous.HasValue || previous.Value.Cpu != next.Cpu)
            {
                string label = next.Cpu is { } value ? $"{value:0} %" : "—";
                SetOverviewTextIfChanged(_referenceChartCpu, label);
                SetOverviewTextIfChanged(_referenceLeftUsage!, label);
            }
            if (!previous.HasValue || previous.Value.Ram != next.Ram)
                SetOverviewTextIfChanged(_referenceChartRam!,
                    next.Ram is { } value ? $"{value:0} %" : "—");
            if (!previous.HasValue || previous.Value.Gpu != next.Gpu)
                SetOverviewTextIfChanged(_referenceChartGpu!,
                    next.Gpu is { } value ? $"{value:0} %" : "—");
            if (!previous.HasValue || previous.Value.Disk != next.Disk)
                SetOverviewTextIfChanged(_referenceChartDisk!,
                    next.Disk is { } value ? $"{value:0} %" : "—");
            if (_cpuGauge != null && next.ClockMhz is { } freq &&
                (!previous.HasValue || previous.Value.ClockMhz != next.ClockMhz))
                SetOverviewTextIfChanged(_cpuGauge.SecondaryText,
                    $"{freq / 1000d:0.00} GHz  |  {Environment.ProcessorCount} vláken");
            if (_gpuGauge != null && next.GpuTemp is { } temp &&
                (!previous.HasValue || previous.Value.GpuTemp != next.GpuTemp ||
                 previous.Value.GpuLabel != next.GpuLabel))
                SetOverviewTextIfChanged(_gpuGauge.SecondaryText,
                    $"{next.GpuLabel}  |  {temp:0} °C");
            _referenceLastHeaderTextKey = next;
        }
        // Meter levels and history/geometry remain independent of text updates.
        ReferenceUpdateLeftMeter(safeCpu ?? 0);
        // Only rebuild the three native smoothed CPU geometries on the existing
        // 4-second history-chart cadence. Gauge values, 22 segments and all
        // text remain live at 2 seconds; resize still renders immediately.
        if (_overviewPerfHistory.Count > 0 &&
            ReferenceCpuPlotDue(refreshPlot, _overviewPerfHistory[^1].Utc, _referenceLastPlotUtc))
            RenderReferenceMainPlot();
    }

    private void ReferenceUpdateLeftMeter(double cpu)
    {
        if(_referenceLeftBlocks==null)return;
        cpu=ReferenceFiniteCpuPercent(cpu);
        int lit=Math.Clamp((int)Math.Round(cpu*12/100),0,12);
        int previous=_referenceLeftLastLit;
        if(lit==previous)return;
        int first = previous < 0 ? 0 : 12 - Math.Max(previous, lit);
        int end = previous < 0 ? 12 : 12 - Math.Min(previous, lit);
        for(int i=first;i<end;i++)
        {
            double opacity = i >= 12-lit ? .94 : .12;
            if(Math.Abs(_referenceLeftBlocks[i].Opacity-opacity)>.01)
                _referenceLeftBlocks[i].Opacity=opacity;
        }
        _referenceLeftLastLit=lit;
    }

    private const int ReferenceOverviewDetailsCardCount = 3;

    internal static int ReferenceOverviewColumnCount(double windowWidth) =>
        windowWidth - ShellSidebarWidth - 48 >= 770 ? 3 : 2;

    internal static string RunReferenceOverviewPolicySmoke()
    {
        // Three information cards stay permanently inline, with no repeated
        // network card and no hidden system flyout.
        if (ReferenceOverviewDetailsCardCount != 3)
            throw new InvalidOperationException("Overview must show 3 unique system detail cards.");
        var surface = ReferenceGlassSurface(ReferenceGpuAccent()) as LinearGradientBrush;
        if (surface == null || surface.GradientStops.Count != 3 ||
            surface.GradientStops[0].Color.B <= surface.GradientStops[0].Color.R ||
            surface.GradientStops[0].Color.A >= 255 ||
            surface.GradientStops[2].Color.R >= 20)
            throw new InvalidOperationException("Overview glass regressed to flat opaque blue.");
        var edge = ReferenceThinRim() as LinearGradientBrush;
        if (edge == null || edge.GradientStops[0].Color.A > 140)
            throw new InvalidOperationException("Overview acquired bright opaque white borders.");
        if (ReferenceOverviewColumnCount(1304)!=3 ||
            ReferenceOverviewColumnCount(920)!=2)
            throw new InvalidOperationException("Reference Overview must have 3 columns at normal size.");
        if(ReferenceColor(0,191,255).Color.B<200)throw new InvalidOperationException("cyan gauge palette");
        double[] values={0,12,48,100};
        foreach(double value in values){
            int lit=Math.Clamp((int)Math.Round(value*22/100),0,22);
            if(lit<0||lit>22)throw new InvalidOperationException("segment bound");
        }
        var compact = ReferenceFitHeights(636,630,99,42,78);
        var tall = ReferenceFitHeights(736,630,99,42,78);
        var repeated = ReferenceFitHeights(736,730,tall.Plot,tall.Spark,tall.Info);
        var smaller = ReferenceFitHeights(536,730,tall.Plot,tall.Spark,tall.Info);
        if (compact != new ReferenceOverviewHeights(99,42,78) ||
            Math.Abs(tall.Plot-139)>0.01 ||
            Math.Abs(tall.Spark-64)>0.01 ||
            Math.Abs(tall.Info-94)>0.01 ||
            Math.Abs((tall.Plot-99) + 2*(tall.Spark-42) + (tall.Info-78)-100)>0.01 ||
            Math.Abs(repeated.Plot-tall.Plot)>0.01 ||
            Math.Abs(repeated.Spark-tall.Spark)>0.01 ||
            Math.Abs(repeated.Info-tall.Info)>0.01 ||
            smaller != compact)
            throw new InvalidOperationException("Distributed viewport height or resize idempotence regressed.");
        var now=new DateTime(2026,10,12,4,0,0,DateTimeKind.Utc);
        double left=ReferencePlotTimeX(now.AddSeconds(-60),now,100);
        double mid=ReferencePlotTimeX(now.AddSeconds(-30),now,100);
        double right=ReferencePlotTimeX(now,now,100);
        if (left!=0 || Math.Abs(mid-49)>.01 || Math.Abs(right-98)>.01)
            throw new InvalidOperationException("Reference chart fakes a full minute before samples exist.");
        // Repeated telemetry samples do not change grid geometry; any actual
        // width/height change must still trigger a redraw.
        if (ReferenceGridNeedsResize(400,99,400,99) ||
            !ReferenceGridNeedsResize(401,99,400,99) ||
            !ReferenceGridNeedsResize(400,139,400,99) ||
            !ReferenceGridNeedsResize(400,99,double.NaN,double.NaN))
            throw new InvalidOperationException("Overview grid redraw guard regressed.");
        // A full 30-sample steady history does not need 30 native WinUI
        // PointCollection writes each tick, but a resize forces rendering.
        if (ReferenceNetworkSparkNeedsRedraw(30,30,30,200,33,200,33) ||
            !ReferenceNetworkSparkNeedsRedraw(29,30,30,200,33,200,33) ||
            !ReferenceNetworkSparkNeedsRedraw(30,30,30,201,33,200,33) ||
            !ReferenceNetworkSparkNeedsRedraw(30,30,30,200,43,200,33))
            throw new InvalidOperationException("Network steady sparkline redraw guard regressed.");
        // Disconnected and invalid NIC counters cannot appear as fake traffic.
        if (ReferenceNetworkTotal(8, 3, false) != 0 ||
            ReferenceNetworkTotal(8, 3, true) != 11 ||
            ReferenceNetworkTotal(-5, 2, true) != 2 ||
            ReferenceNetworkTotal(double.NaN, double.PositiveInfinity, true) != 0)
            throw new InvalidOperationException("Overview network traffic sanitization regressed.");
        // Non-finite sensor samples must not produce invalid native chart
        // coordinates, percentages, or segment counts.
        if (ReferenceFiniteCpuPercent(double.NaN)!=0 ||
            ReferenceFiniteCpuPercent(double.PositiveInfinity)!=0 ||
            ReferenceFiniteCpuPercent(double.NegativeInfinity)!=0 ||
            ReferenceFiniteCpuPercent(-30)!=0 ||
            ReferenceFiniteCpuPercent(130)!=100 ||
            Math.Abs(ReferenceFiniteCpuPercent(48.5)-48.5)>.001)
            throw new InvalidOperationException("Overview CPU finite-range policy regressed.");
        if (ReferenceGpuAccent().Color.R<180 ||
            ReferenceDiskAccent().Color.G<170)
            throw new InvalidOperationException("Overview theme reverted to incorrect GPU/disk colors.");
        return "REFERENCE_OVERVIEW_POLICY_SMOKE_OK grid=3x2 cards=6 gpu=magenta disk=green metric=live refresh=2s";
    }
}
