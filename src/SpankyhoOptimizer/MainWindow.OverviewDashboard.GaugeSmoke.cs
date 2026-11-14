using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    internal async Task RunOverviewGaugeHotpathSmokeAsync(bool includeNativeBenchmark = false)
    {
        // This isolated test can overlap initial startup navigation. Its
        // first deferred gauge factory may be canceled by the later Overview
        // activation; make one bounded fresh attempt before reporting failure.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await ShowSectionAsync("overview");
            for (int i = 0; i < 40 &&
                (_cpuGauge == null || _ramGauge == null ||
                 _gpuGauge == null || _diskGauge == null ||
                 _referenceNetworkValue == null || _referenceChartCpu == null); i++)
                await Task.Delay(25);
            if (_cpuGauge != null && _ramGauge != null &&
                _gpuGauge != null && _diskGauge != null &&
                _referenceNetworkValue != null && _referenceChartCpu != null)
                break;
            if (attempt == 0)
                InvalidateOverviewPageCache(forceRelease: true);
        }
        if (_cpuGauge == null || _ramGauge == null || _gpuGauge == null || _diskGauge == null ||
            _referenceNetworkValue == null || _referenceChartCpu == null)
            throw new InvalidOperationException("Overview gauges or reference cards were not materialized.");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        void Frame()
        {
            AnimateGauge(_cpuGauge, 31);
            AnimateGauge(_ramGauge, 52);
            AnimateGauge(_gpuGauge, 40);
            AnimateGauge(_diskGauge, 12);
        }
        for (int i = 0; i < 35; i++) Frame(); // warmed sample windows
        int initialRedraws = _cpuGauge.SparklineRedrawCount + _ramGauge.SparklineRedrawCount +
            _gpuGauge.SparklineRedrawCount + _diskGauge.SparklineRedrawCount;
        // Before measuring, ensure the native fill matches all four curves.
        foreach (var gauge in new[] { _cpuGauge, _ramGauge, _gpuGauge, _diskGauge })
            if (gauge.SparkFill == null || gauge.SparkFill.Points.Count != gauge.Sparkline.Points.Count + 2)
                throw new InvalidOperationException("Overview fill geometry not aligned with its sparkline.");
        long before = GC.GetAllocatedBytesForCurrentThread();
        process.Refresh();
        TimeSpan cpuStart = process.TotalProcessorTime;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 120; i++) Frame();
        sw.Stop();
        process.Refresh();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        int finalRedraws = _cpuGauge.SparklineRedrawCount + _ramGauge.SparklineRedrawCount +
            _gpuGauge.SparklineRedrawCount + _diskGauge.SparklineRedrawCount;
        if (finalRedraws != initialRedraws)
            throw new InvalidOperationException($"Steady gauges were unnecessarily redrawn: {finalRedraws - initialRedraws}.");
        LogService.Info("performance",
            $"OVERVIEW_GAUGE_HOTPATH_SMOKE_OK frames=120 gauges=4" +
            $" wallMs={sw.Elapsed.TotalMilliseconds:F2}" +
            $" cpuMs={(process.TotalProcessorTime - cpuStart).TotalMilliseconds:F2}" +
            $" allocatedBytes={allocated} wsMB={process.WorkingSet64 / 1048576d:F2}" +
            $" redraws={finalRedraws - initialRedraws} fillWritesSkipped={120 * 4 * 33}");

        // The steady fast path must not mask an independently invalidated
        // native Polygon fill. A rebuilt fill and a new sample both re-enter
        // the full path, then subsequent equal frames remain allocation-free.
        _cpuGauge.SparkFill!.Points.Clear();
        int fillRecoveryBefore = _cpuGauge.SparklineRedrawCount;
        AnimateGauge(_cpuGauge, 31);
        if (_cpuGauge.SparklineRedrawCount != fillRecoveryBefore + 1 ||
            _cpuGauge.SparkFill.Points.Count != _cpuGauge.Sparkline.Points.Count + 2)
            throw new InvalidOperationException("Steady gauge failed to restore lost native fill.");
        int recoveredRedraws = _cpuGauge.SparklineRedrawCount;
        AnimateGauge(_cpuGauge, 31);
        if (_cpuGauge.SparklineRedrawCount != recoveredRedraws)
            throw new InvalidOperationException("Restored gauge redrew an identical second sample.");

        // Verify changing data too: retaining WinUI point slots must preserve
        // the same line/fill polygon geometry under a rolling 30-sample window,
        // not just on the identical-value fast path above.
        int rollingRedrawsBefore = _cpuGauge.SparklineRedrawCount +
            _ramGauge.SparklineRedrawCount + _gpuGauge.SparklineRedrawCount +
            _diskGauge.SparklineRedrawCount;
        long rollingAllocatedStart = GC.GetAllocatedBytesForCurrentThread();
        var rollingWatch = System.Diagnostics.Stopwatch.StartNew();
        for (int frame = 0; frame < 90; frame++)
        {
            AnimateGauge(_cpuGauge, (frame * 7) % 100);
            AnimateGauge(_ramGauge, (frame * 3 + 18) % 100);
            AnimateGauge(_gpuGauge, (frame * 11) % 100);
            AnimateGauge(_diskGauge, (frame * 5 + 2) % 100);
            foreach (var gauge in new[] { _cpuGauge, _ramGauge, _gpuGauge, _diskGauge })
            {
                if (gauge.Sparkline.Points.Count != gauge.Samples.Count ||
                    gauge.SparkFill?.Points.Count != gauge.Sparkline.Points.Count + 2 ||
                    gauge.Sparkline.Points.Any(point =>
                        !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                    throw new InvalidOperationException(
                        "Rolling gauge geometry lost a point or an aligned fill.");
            }
        }
        rollingWatch.Stop();
        long rollingAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - rollingAllocatedStart;
        LogService.Info("performance",
            $"OVERVIEW_GAUGE_ROLLING_BENCH_OK frames=90 gauges=4 wallMs={rollingWatch.Elapsed.TotalMilliseconds:F3} allocatedB={rollingAllocatedBytes}");
        int rollingRedrawsAfter = _cpuGauge.SparklineRedrawCount +
            _ramGauge.SparklineRedrawCount + _gpuGauge.SparklineRedrawCount +
            _diskGauge.SparklineRedrawCount;
        if (rollingRedrawsAfter <= rollingRedrawsBefore)
            throw new InvalidOperationException(
                "Changing gauge samples failed to redraw native geometry.");

        // Validate the final value of each real, visible 22-segment grid, not
        // merely the counters/underlying sparklines. The optimized update
        // range must light and dim exactly the same blocks as before.
        static void VerifySegmentLevel(GaugeRefs refs, double value, string phase)
        {
            if (refs.TrackHost is not Grid grid || grid.Children.Count != 22)
                throw new InvalidOperationException("Overview segment grid missing: " + phase);
            int lit = Math.Clamp((int)Math.Round(value * grid.Children.Count / 100), 0, grid.Children.Count);
            for (int i = 0; i < grid.Children.Count; i++)
            {
                if (grid.Children[i] is not Border block ||
                    Math.Abs(block.Opacity - (i < lit ? 1d : .27d)) > .001)
                    throw new InvalidOperationException($"Segment opacity mismatch {phase}: index={i} lit={lit}.");
            }
        }
        VerifySegmentLevel(_cpuGauge, 23, "cpu-rolling");
        VerifySegmentLevel(_ramGauge, 85, "ram-rolling");
        VerifySegmentLevel(_gpuGauge, 79, "gpu-rolling");
        VerifySegmentLevel(_diskGauge, 47, "disk-rolling");

        AnimateGauge(_cpuGauge, 97);
        VerifySegmentLevel(_cpuGauge, 97, "cpu-high");
        if (_cpuGauge.LastAppearance != 2 || _cpuGauge.StatusText.Text != "HIGH")
            throw new InvalidOperationException("High telemetry state did not update.");
        // An existing segment must retain its WinUI animation across rapid
        // HIGH -> NORMAL changes rather than creating a new Storyboard.
        if (_cpuGauge.TrackHost is not Grid cpuTrack ||
            cpuTrack.Children[15] is not Border cpuSegment ||
            !_referenceSegmentAnimations.TryGetValue(cpuSegment, out var cachedAnimation))
            throw new InvalidOperationException("Reference segment animation cache not primed.");
        AnimateGauge(_cpuGauge, 31);
        if (!_referenceSegmentAnimations.TryGetValue(cpuSegment, out var retainedAnimation) ||
            !ReferenceEquals(cachedAnimation, retainedAnimation))
            throw new InvalidOperationException("Reference segment animation was allocated again.");
        if (_cpuGauge.LastAppearance != 0 || _cpuGauge.StatusText.Text != "NORMAL")
            throw new InvalidOperationException("Normal telemetry state did not recover.");
        _cpuGauge.Sparkline.Points.Clear(); // simulate invalidated visual after layout
        int priorRecoveryRedraws = _cpuGauge.SparklineRedrawCount;
        AnimateGauge(_cpuGauge, 31);
        if (_cpuGauge.Sparkline.Points.Count != _cpuGauge.Samples.Count ||
            _cpuGauge.SparkFill?.Points.Count != _cpuGauge.Sparkline.Points.Count + 2 ||
            _cpuGauge.SparklineRedrawCount != priorRecoveryRedraws + 1)
            throw new InvalidOperationException("Gauge line/fill geometry was not restored after invalidation.");
        // Invalid non-finite samples must be treated as unavailable, never
        // passed to segment geometry or left rendered as live sensor values.
        AnimateGauge(_gpuGauge, double.NaN);
        AnimateGauge(_gpuGauge, null);
        AnimateGauge(_gpuGauge, double.PositiveInfinity);
        if (_gpuGauge.LastAppearance != 4 || _gpuGauge.StatusText.Text != "NEDOSTUPNÉ")
            throw new InvalidOperationException("Missing telemetry state was not shown.");
        if (_gpuGauge.Samples.Count != 0 || _gpuGauge.Sparkline.Points.Count != 0 ||
            _gpuGauge.SparkFill?.Points.Count != 0 || _gpuGauge.ValueText.Text != "N/A")
            throw new InvalidOperationException("Unavailable GPU left stale samples or visible sparkline.");
        VerifySegmentLevel(_gpuGauge, 0, "gpu-unavailable");
        AnimateGauge(_gpuGauge, 40);
        VerifySegmentLevel(_gpuGauge, 40, "gpu-recovered");
        if (_gpuGauge.LastAppearance != 0 || _gpuGauge.StatusText.Text != "NORMAL" ||
            _gpuGauge.Samples.Count != 1 ||
            _gpuGauge.SparkFill?.Points.Count != _gpuGauge.Sparkline.Points.Count + 2)
            throw new InvalidOperationException("Recovered telemetry state/first sample was not restored.");
        // Verify that the real 22-segment cards use one shared immutable
        // border brush, not a separate native brush for every segment.
        static void VerifyGaugeResources(GaugeRefs gauge)
        {
            if (gauge.TrackHost is not Grid track ||
                track.Children.Count != 22 ||
                track.Children[0] is not Border first ||
                first.BorderBrush is not SolidColorBrush brush ||
                gauge.SparkHost.Clip is not RectangleGeometry)
                throw new InvalidOperationException("Overview gauge resources missing.");
            for (int i = 1; i < track.Children.Count; i++)
                if (track.Children[i] is not Border segment ||
                    !ReferenceEquals(brush, segment.BorderBrush))
                    throw new InvalidOperationException("Overview gauge replicated native segment border brushes.");
        }
        VerifyGaugeResources(_cpuGauge);
        VerifyGaugeResources(_ramGauge);
        VerifyGaugeResources(_gpuGauge);
        VerifyGaugeResources(_diskGauge);
        if (_referenceNetworkBlocks is not { Length: 22 } networkSegments ||
            networkSegments[0].BorderBrush is not SolidColorBrush networkBrush ||
            networkSegments.Any(b => !ReferenceEquals(b.BorderBrush, networkBrush)))
            throw new InvalidOperationException("Overview network meter replicated its native segment brushes.");
        // A skipped native frame must keep the rolling 2-second samples,
        // numeric readout, trend and 22 LEDs live, then repaint BOTH line
        // and fill on the existing 4-second cadence. No new runtime probe.
        foreach (GaugeRefs gauge in new[] { _cpuGauge, _ramGauge, _gpuGauge, _diskGauge })
        {
            for (int i = 0; i < 33; i++)
                AnimateGauge(gauge, 35 + i % 7);
            if (gauge.Sparkline.Points.Count != 30 ||
                gauge.SparkFill?.Points.Count != 32)
                throw new InvalidOperationException("Gauge cadence smoke requires warmed line/fill.");
            var beforePoints = gauge.Sparkline.Points.ToArray();
            int beforePaints = gauge.SparklineRedrawCount;
            AnimateGauge(gauge, 92, refreshSpark: false);
            if (gauge.SparklineRedrawCount != beforePaints ||
                gauge.CurrentValue != 92 || gauge.Samples.Count != 30 ||
                gauge.ValueText.Text != "92%" ||
                gauge.Sparkline.Points.Count != beforePoints.Length)
                throw new InvalidOperationException("Gauge off-cadence telemetry or native paint mutated.");
            for (int i = 0; i < beforePoints.Length; i++)
                if (gauge.Sparkline.Points[i] != beforePoints[i])
                    throw new InvalidOperationException("Off-cadence gauge wrote native curve points.");
            VerifySegmentLevel(gauge, 92, "cadence-live");
            AnimateGauge(gauge, 73, refreshSpark: true);
            if (gauge.SparklineRedrawCount != beforePaints + 1 ||
                gauge.SparkFill?.Points.Count != gauge.Sparkline.Points.Count + 2 ||
                gauge.Sparkline.Points.Any(point =>
                    !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                throw new InvalidOperationException("On-cadence gauge failed to repaint curve/fill.");
        }
        // Regression: tick 1/3/5 is the slow GPU/disk sample, while the
        // historical chart's native redraw tick is 0/2/4. If a future change
        // applies only the latter to all gauges, slow sensor charts freeze.
        for (int cycle = 0; cycle < 6; cycle++)
        {
            bool slowDue = cycle > 0 && cycle % 2 == 1;
            bool historyDue = cycle % 2 == 0;
            if (OverviewGaugeSparkDue(slowDue, historyDue) !=
                (slowDue || historyDue))
                throw new InvalidOperationException("Gauge slow-sensor/chart phase policy is incorrect.");
            if (slowDue && !OverviewGaugeSparkDue(true, false))
                throw new InvalidOperationException("GPU/disk spark was skipped on a fresh 4-second sensor tick.");
        }
        int gpuBeforeSlow = _gpuGauge.SparklineRedrawCount;
        int diskBeforeSlow = _diskGauge.SparklineRedrawCount;
        AnimateGauge(_gpuGauge, 51, OverviewGaugeSparkDue(true, false));
        AnimateGauge(_diskGauge, 29, OverviewGaugeSparkDue(true, false));
        AnimateGauge(_gpuGauge, 64, OverviewGaugeSparkDue(true, false));
        AnimateGauge(_diskGauge, 39, OverviewGaugeSparkDue(true, false));
        if (_gpuGauge.SparklineRedrawCount != gpuBeforeSlow + 2 ||
            _diskGauge.SparklineRedrawCount != diskBeforeSlow + 2)
            throw new InvalidOperationException("Two consecutive slow PDH ticks did not repaint GPU/disk sparklines.");
        LogService.Info("performance", "OVERVIEW_GPU_DISK_SPARK_PHASE_SMOKE_OK slowTicks=2 nativePaints=2 cpuChartPhase=opposite");
        // Detailed performance benchmarks belong ONLY to the dedicated
        // --overview-gauge-hotpath-smoke command. The full release UI smoke
        // keeps all behavioral assertions but must not inject 240 artificial
        // changing-value frames on four WinUI cards and distort A/B CPU.
        if (includeNativeBenchmark)
        {
            (int paints, double wallMs, long allocatedB) ProbeGaugeGeometryCadence(bool half)
            {
                GaugeRefs[] gauges = { _cpuGauge, _ramGauge, _gpuGauge, _diskGauge };
                int previousPaints = gauges.Sum(g => g.SparklineRedrawCount);
                long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                for (int frame = 0; frame < 120; frame++)
                    for (int g = 0; g < gauges.Length; g++)
                        AnimateGauge(gauges[g], (frame * (7 + g * 4) + g * 13 + 3) % 100,
                            !half || frame % 2 == 0);
                timer.Stop();
                return (gauges.Sum(g => g.SparklineRedrawCount) - previousPaints,
                    timer.Elapsed.TotalMilliseconds,
                    GC.GetAllocatedBytesForCurrentThread() - beforeBytes);
            }
            var everyTick = ProbeGaugeGeometryCadence(half: false);
            var everyOtherTick = ProbeGaugeGeometryCadence(half: true);
            if (everyTick.paints != 480 || everyOtherTick.paints != 240)
                throw new InvalidOperationException(
                    $"Gauge cadence expected 480 vs 240 native curve+fill frames: {everyTick.paints}/{everyOtherTick.paints}.");
            LogService.Info("performance",
                $"OVERVIEW_GAUGE_4S_BENCH_OK samples=120 gauges=4 fullPaints={everyTick.paints} halfPaints={everyOtherTick.paints} " +
                $"fullMs={everyTick.wallMs:F2} halfMs={everyOtherTick.wallMs:F2} " +
                $"fullAllocatedB={everyTick.allocatedB} halfAllocatedB={everyOtherTick.allocatedB}");
        }
        LogService.Info("performance",
            "OVERVIEW_GAUGE_4S_CADENCE_SMOKE_OK telemetry2s=true lineFill4s=true history30=true nativeWritesSkipped=true");
        LogService.Info("performance",
            "OVERVIEW_GAUGE_RENDER_GUARD_OK steady=suppressed high=true normal=true unavailable=true recovered=true geometry=true sharedBrushes=true");

        // Smoke-only steady-rate network comparison, using the normal
        // visible reference card without any extra production polling.
        if (_referenceNetworkValue == null || _referenceNetworkDetail == null)
            throw new InvalidOperationException("Overview network card not materialized.");
        for (int i = 0; i < 35; i++)
            UpdateReferenceNetwork(18.25, 4.75, online: true);
        long networkBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var networkWatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 180; i++)
            UpdateReferenceNetwork(18.25, 4.75, online: true);
        networkWatch.Stop();
        LogService.Info("performance",
            $"OVERVIEW_NETWORK_STEADY_BENCH_OK frames=180 wallMs={networkWatch.Elapsed.TotalMilliseconds:F3} allocatedB={GC.GetAllocatedBytesForCurrentThread() - networkBytesBefore}");
        if (_referenceNetworkValue.Text != FormatRate(23) ||
            _referenceNetworkDetail.Text != $"↓ {FormatRate(18.25)}  |  ↑ {FormatRate(4.75)}" ||
            _referenceNetworkHistory.Count != 30)
            throw new InvalidOperationException("Steady network labels/history incorrect.");
        UpdateReferenceNetwork(18.25, 4.75, online: false);
        if (_referenceNetworkValue.Text != "—" ||
            _referenceNetworkDetail.Text != "Síť odpojena" ||
            _referenceNetworkFoot?.Text != "Síť odpojena")
            throw new InvalidOperationException("Offline network labels were not refreshed.");
        UpdateReferenceNetwork(18.25, 4.75, online: true);
        if (_referenceNetworkValue.Text != FormatRate(23) ||
            _referenceNetworkDetail.Text != $"↓ {FormatRate(18.25)}  |  ↑ {FormatRate(4.75)}" ||
            _referenceNetworkHistory.Count != 30)
            throw new InvalidOperationException("Reconnected network labels/history were not restored.");
        UpdateReferenceNetwork(18.25, 4.75, online: true, adapterName: "Ethernet 2");
        if (_referenceNetworkFoot?.Text != "Ethernet 2")
            throw new InvalidOperationException("Measured NIC label was not displayed.");
        UpdateReferenceNetwork(18.25, 4.75, online: true, adapterName: "2 aktivní rozhraní");
        if (_referenceNetworkFoot?.Text != "2 aktivní rozhraní")
            throw new InvalidOperationException("Aggregated NIC traffic was incorrectly attributed to one adapter.");
        LogService.Info("performance",
            "OVERVIEW_NETWORK_TEXT_GUARD_OK steady=true offline=true reconnect=true multiNicLabel=true historyPreserved=true");

        // The 30-value rolling peak must expire on the exact frame it leaves
        // the history. Otherwise the 22 native segments stay nearly empty
        // long after an old high-traffic sample disappeared.
        UpdateReferenceNetwork(799, 1, online: true);
        for (int i = 0; i < 29; i++)
            UpdateReferenceNetwork(9, 1, online: true);
        if (_referenceNetworkLastLit != 0 || _referenceNetworkHistory.Count != 30)
            throw new InvalidOperationException("Network meter prematurely lost its rolling peak.");
        UpdateReferenceNetwork(9, 1, online: true);
        if (_referenceNetworkLastLit != 22 || _referenceNetworkHistory.Count != 30)
            throw new InvalidOperationException("Network meter retained a peak past the 30-sample window.");
        LogService.Info("performance", "OVERVIEW_NETWORK_PEAK_EXPIRY_SMOKE_OK window=30 stalePeak=false nativeSegments=22");

        // On an off-cadence sample, values/history/segments must update while
        // the existing native PointCollection is left untouched. The next
        // chart cadence must catch up with the latest rolling samples.
        if (_referenceNetworkSpark?.Parent is Canvas actualNetworkChart &&
            actualNetworkChart.ActualWidth >= 10)
        {
            var priorPoints = _referenceNetworkSpark.Points.ToArray();
            UpdateReferenceNetwork(77, 1, online: true, refreshSpark: false);
            if (_referenceNetworkHistory.Count != 30 ||
                _referenceNetworkValue?.Text != FormatRate(78) ||
                _referenceNetworkSpark.Points.Count != priorPoints.Length)
                throw new InvalidOperationException("Off-cadence network tick lost live telemetry.");
            for (int i = 0; i < priorPoints.Length; i++)
                if (_referenceNetworkSpark.Points[i] != priorPoints[i])
                    throw new InvalidOperationException("Off-cadence network tick mutated native geometry.");
            UpdateReferenceNetwork(77, 1, online: true, refreshSpark: true);
            if (_referenceNetworkSpark.Points.Count != _referenceNetworkHistory.Count ||
                _referenceNetworkLastSparkWidth != actualNetworkChart.ActualWidth)
                throw new InvalidOperationException("Network chart failed to catch up at its 4-second cadence.");
        }
        LogService.Info("performance",
            "OVERVIEW_NETWORK_CADENCE_SMOKE_OK live2s=true geometry4s=true history30=true");

        // Smoke-only measurement of the legacy network status strip. Use
        // detached native labels to avoid interfering with the active Overview.
        var originalStatus = _overviewNetworkStatusText;
        var originalSpeed = _overviewNetworkSpeedText;
        var originalNextChartUtc = _overviewNextNetworkChartUtc;
        try
        {
            _overviewNetworkStatusText = new TextBlock();
            _overviewNetworkSpeedText = new TextBlock();
            _overviewNextNetworkChartUtc = DateTime.MaxValue;
            for (int i = 0; i < 15; i++)
                UpdateOverviewNetworkVisuals(12.5, 3.75, online: true);
            long stripBefore = GC.GetAllocatedBytesForCurrentThread();
            var stripWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 360; i++)
                UpdateOverviewNetworkVisuals(12.5, 3.75, online: true);
            stripWatch.Stop();
            LogService.Info("performance",
                $"OVERVIEW_NETWORK_STATUS_STEADY_BENCH_OK frames=360 wallMs={stripWatch.Elapsed.TotalMilliseconds:F3} allocatedB={GC.GetAllocatedBytesForCurrentThread() - stripBefore}");
            if (_overviewNetworkStatusText.Text != "Aktivní připojení" ||
                _overviewNetworkSpeedText.Text != $"↓ {FormatRate(12.5)}      ↑ {FormatRate(3.75)}")
                throw new InvalidOperationException("Legacy Overview network strip lost steady data.");
            UpdateOverviewNetworkVisuals(0, 0, online: false);
            if (_overviewNetworkStatusText.Text != "Síť odpojena")
                throw new InvalidOperationException("Legacy Overview network strip failed offline state.");
            UpdateOverviewNetworkVisuals(18.5, 2.25, online: true);
            if (_overviewNetworkStatusText.Text != "Aktivní připojení" ||
                _overviewNetworkSpeedText.Text != $"↓ {FormatRate(18.5)}      ↑ {FormatRate(2.25)}")
                throw new InvalidOperationException("Legacy Overview network strip failed reconnect.");
            // A recreated UI tree must not inherit the old label memo.
            _overviewNetworkStatusText = new TextBlock();
            _overviewNetworkSpeedText = new TextBlock();
            UpdateOverviewNetworkVisuals(18.5, 2.25, online: true);
            if (_overviewNetworkStatusText.Text != "Aktivní připojení" ||
                _overviewNetworkSpeedText.Text != $"↓ {FormatRate(18.5)}      ↑ {FormatRate(2.25)}")
                throw new InvalidOperationException("Legacy network strip memo masked rebuilt native labels.");
        }
        finally
        {
            _overviewNetworkStatusText = originalStatus;
            _overviewNetworkSpeedText = originalSpeed;
            _overviewNextNetworkChartUtc = originalNextChartUtc;
        }

        // Detached native readouts only. Measure the same actual four-footer
        // and uptime paths used by Overview without touching visible state.
        var priorCpuFooter = _overviewCpuFooterText;
        var priorGpuFooter = _overviewGpuFooterText;
        var priorRamFooter = _overviewRamFooterText;
        var priorDiskFooter = _overviewDiskFooterText;
        var priorCpuFill = _overviewCpuFooterFill;
        var priorGpuFill = _overviewGpuFooterFill;
        var priorRamFill = _overviewRamFooterFill;
        var priorDiskFill = _overviewDiskFooterFill;
        var priorUptime = _overviewUptimeText;
        var priorFooterKey = _overviewLastFooterTextKey;
        var priorUptimeTarget = _overviewLastUptimeTextTarget;
        var priorUptimeMinute = _overviewLastUptimeMinute;
        try
        {
            _overviewCpuFooterText = new TextBlock();
            _overviewGpuFooterText = new TextBlock();
            _overviewRamFooterText = new TextBlock();
            _overviewDiskFooterText = new TextBlock();
            _overviewCpuFooterFill = new Border { Width = 0 };
            _overviewGpuFooterFill = new Border { Width = 0 };
            _overviewRamFooterFill = new Border { Width = 0 };
            _overviewDiskFooterFill = new Border { Width = 0 };
            _overviewUptimeText = new TextBlock();
            for (int i = 0; i < 12; i++)
            {
                UpdateOverviewFooterReadouts(28.25, 34.5, 53.75, 12.25);
                UpdateOverviewUptimeReadout();
            }
            long beforeFooterBytes = GC.GetAllocatedBytesForCurrentThread();
            var footerSw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 360; i++)
                UpdateOverviewFooterReadouts(28.25, 34.5, 53.75, 12.25);
            footerSw.Stop();
            LogService.Info("performance",
                $"OVERVIEW_FOOTER_STEADY_BENCH_OK frames=360 wallMs={footerSw.Elapsed.TotalMilliseconds:F3} allocatedB={GC.GetAllocatedBytesForCurrentThread() - beforeFooterBytes}");
            long beforeUptimeBytes = GC.GetAllocatedBytesForCurrentThread();
            var uptimeSw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 360; i++)
                UpdateOverviewUptimeReadout();
            uptimeSw.Stop();
            LogService.Info("performance",
                $"OVERVIEW_UPTIME_STEADY_BENCH_OK frames=360 wallMs={uptimeSw.Elapsed.TotalMilliseconds:F3} allocatedB={GC.GetAllocatedBytesForCurrentThread() - beforeUptimeBytes}");
            if (_overviewCpuFooterText.Text != $"{28.25:0}% využití" ||
                _overviewGpuFooterText.Text != $"{34.5:0}% využití" ||
                _overviewRamFooterText.Text != $"{53.75:0}% využití" ||
                _overviewDiskFooterText.Text != $"{12.25:0}% aktivita" ||
                string.IsNullOrWhiteSpace(_overviewUptimeText.Text))
                throw new InvalidOperationException("Overview footer/uptime labels lost steady live values.");
            UpdateOverviewFooterReadouts(double.NaN, double.PositiveInfinity,
                double.NegativeInfinity, null);
            if (_overviewCpuFooterText.Text != "Telemetrie CPU čeká…" ||
                _overviewGpuFooterText.Text != "Telemetrie GPU čeká…" ||
                _overviewRamFooterText.Text != "Telemetrie RAM čeká…" ||
                _overviewDiskFooterText.Text != "Telemetrie disku čeká…" ||
                _overviewCpuFooterFill.Width != 0 ||
                _overviewGpuFooterFill.Width != 0 ||
                _overviewRamFooterFill.Width != 0 ||
                _overviewDiskFooterFill.Width != 0)
                throw new InvalidOperationException("Overview nonfinite/missing footers retained stale text or width.");
            UpdateOverviewFooterReadouts(64, 31, 52, 9);
            if (_overviewCpuFooterText.Text != "64% využití" ||
                _overviewGpuFooterText.Text != "31% využití" ||
                _overviewRamFooterText.Text != "52% využití" ||
                _overviewDiskFooterText.Text != "9% aktivita" ||
                Math.Abs(_overviewCpuFooterFill.Width - 30.72) > .01)
                throw new InvalidOperationException("Overview footer recovery lost live text or bar width.");
            // Recreated native elements must receive values despite a matching
            // last-input cache; uptime also rebinds within the same minute.
            _overviewCpuFooterText = new TextBlock();
            _overviewGpuFooterText = new TextBlock();
            _overviewRamFooterText = new TextBlock();
            _overviewDiskFooterText = new TextBlock();
            _overviewUptimeText = new TextBlock();
            UpdateOverviewFooterReadouts(64, 31, 52, 9);
            UpdateOverviewUptimeReadout();
            if (_overviewCpuFooterText.Text != "64% využití" ||
                _overviewGpuFooterText.Text != "31% využití" ||
                _overviewRamFooterText.Text != "52% využití" ||
                _overviewDiskFooterText.Text != "9% aktivita" ||
                string.IsNullOrWhiteSpace(_overviewUptimeText.Text))
                throw new InvalidOperationException("Overview footer/uptime native rebuild cache failed.");
        }
        finally
        {
            _overviewCpuFooterText = priorCpuFooter;
            _overviewGpuFooterText = priorGpuFooter;
            _overviewRamFooterText = priorRamFooter;
            _overviewDiskFooterText = priorDiskFooter;
            _overviewCpuFooterFill = priorCpuFill;
            _overviewGpuFooterFill = priorGpuFill;
            _overviewRamFooterFill = priorRamFill;
            _overviewDiskFooterFill = priorDiskFill;
            _overviewUptimeText = priorUptime;
            _overviewLastFooterTextKey = priorFooterKey;
            _overviewLastUptimeTextTarget = priorUptimeTarget;
            _overviewLastUptimeMinute = priorUptimeMinute;
        }

        // Existing UI smoke only: quantify repeat header work without
        // changing live telemetry frequency or adding runtime diagnostics.
        for (int i = 0; i < 15; i++)
            UpdateReferenceHeader(31.25, 52.5, 40.75, 12.5);
        long headerBefore = GC.GetAllocatedBytesForCurrentThread();
        var headerWatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 360; i++)
            UpdateReferenceHeader(31.25, 52.5, 40.75, 12.5);
        headerWatch.Stop();
        LogService.Info("performance",
            $"OVERVIEW_REFERENCE_HEADER_STEADY_BENCH_OK frames=360 wallMs={headerWatch.Elapsed.TotalMilliseconds:F3} allocatedB={GC.GetAllocatedBytesForCurrentThread() - headerBefore}");
        if (_referenceChartCpu?.Text != $"{31.25:0} %" ||
            _referenceChartRam?.Text != $"{52.5:0} %" ||
            _referenceChartGpu?.Text != $"{40.75:0} %" ||
            _referenceChartDisk?.Text != $"{12.5:0} %" ||
            _referenceLeftUsage?.Text != $"{31.25:0} %")
            throw new InvalidOperationException(
                $"Reference header stable labels mismatch cpu={_referenceChartCpu?.Text} ram={_referenceChartRam?.Text} gpu={_referenceChartGpu?.Text} disk={_referenceChartDisk?.Text} left={_referenceLeftUsage?.Text}.");
        UpdateReferenceHeader(double.NaN, double.PositiveInfinity, double.NaN, double.NegativeInfinity);
        if (_referenceChartCpu?.Text != "—" || _referenceChartRam?.Text != "—" ||
            _referenceChartGpu?.Text != "—" || _referenceChartDisk?.Text != "—" ||
            _referenceLeftUsage?.Text != "—")
            throw new InvalidOperationException("Nonfinite reference header telemetry rendered as live values.");
        UpdateReferenceHeader(80, 79, 78, 77);
        if (_referenceChartCpu?.Text != "80 %" || _referenceChartRam?.Text != "79 %" ||
            _referenceChartGpu?.Text != "78 %" || _referenceChartDisk?.Text != "77 %" ||
            _referenceLeftUsage?.Text != "80 %")
            throw new InvalidOperationException("Reference header failed to recover after sensor loss.");
        LogService.Info("performance",
            "OVERVIEW_REFERENCE_HEADER_GUARD_OK steady=true missingNonfinite=true recovered=true chartPreserved=true");
    }

    // Slow GPU/disk sensors sample on the opposite 4-second phase from the
    // fast CPU/RAM history timer. A fresh slow sensor MUST repaint its own
    // native curve even when the fast chart cadence is not due.
    internal static bool OverviewGaugeSparkDue(bool freshSlowSample, bool historyChartTick) =>
        freshSlowSample || historyChartTick;
}
