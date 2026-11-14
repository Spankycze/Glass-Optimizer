using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task RunLiveMetricsLoopAsync(CancellationToken token)
    {
        int tick = 0;
        double? gpu = null;
        double? disk = null;
        while (!token.IsCancellationRequested)
        {
            bool onOverview = _section == "overview";
            bool activeOverview = onOverview && _windowTelemetryActive;
            if (!activeOverview)
            {
                // Restart the sampling schedule after returning: do not burn an
                // expensive GPU/disk PDH sample immediately merely because many
                // inactive wake cycles have elapsed.
                tick = 0;
                // Invalidate old readings: the first frame after activation
                // must not present a GPU/disk value from minutes ago, or use
                // a CPU delta accumulated throughout the inactive period.
                gpu = null;
                disk = null;
                _lastCpuKernel = 0;
                // Fully idle when Overview is hidden or the window is inactive. No hardware
                // probes/UI work is needed here; wake only occasionally to notice reactivation.
                // A ten-second timer only applies while actually idle. A UI
                // activation/navigation signal wakes immediately with no polling.
                await _liveMetricsWake.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                continue;
            }

            long sampleStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            // Record the time of observation on the sensor worker, not when
            // the WinUI dispatcher happens to consume a queued sample.
            DateTime sampleUtc = DateTime.UtcNow;
            double? cpu = ReadCpuUsage();
            var ramSample = ReadRamSnapshot();
            double? ram = ramSample.Percent;
            var networkRates = ReadNetworkRates();
            // Initialize native counters on the worker after first paint (~2 s).
            // Never run the measured cold PDH initialization on the UI thread.
            // Native GPU/disk performance counters are more expensive than CPU/RAM samples.
            // Poll them every other active iteration (4 s); CPU/RAM remain at 2 s.
            bool firstActiveSample = tick == 0;
            bool slowMetricsTick = tick >= 1 && tick % 2 == 1 && !_liveMetricsDiagnosticSuppressSlow;
            bool gpuMetricsSample = slowMetricsTick;
            bool diskMetricsSample = slowMetricsTick;
            if (diskMetricsSample)
            {
                disk = ReadDiskActivity();
                System.Threading.Interlocked.Increment(ref _latestDiskSensorGeneration);
            }
            if (gpuMetricsSample)
            {
                gpu = ReadGpuUsage();
                System.Threading.Interlocked.Increment(ref _latestGpuSensorGeneration);
            }
            // Sample on first active Overview frame, retry at 4 and 8 seconds
            // in case an elevated bridge is still starting, then every 16s.
            // No extra worker process or sampler is scheduled.
            if (OverviewTemperatureReadingPolicy.ShouldRead(tick))
            {
                var sensor = HardwareTelemetryBridgeService.TryReadFresh(TimeSpan.FromSeconds(15));
                FanAgentHealth? fan = FanProfileService.ReadHealthStatus();
                if (fan != null && (!fan.Healthy ||
                    !OverviewTemperatureReadingPolicy.IsFresh(
                        fan.UpdatedUtc, sampleUtc, TimeSpan.FromSeconds(15))))
                    fan = null;
                _referenceSensorCpu = OverviewTemperatureReadingPolicy.SelectCpu(
                    sensor?.CpuTempC, fan?.CpuTempC);
                _referenceSensorBoard = OverviewTemperatureReadingPolicy.ValidTemperature(
                    sensor?.MotherboardTempC);
                _referenceSensorFanRpm = fan?.HidOk == true
                    ? OverviewTemperatureReadingPolicy.AverageActiveFanRpm(fan.Rpms)
                    : null;
                _referenceCpuClockMhz = sensor?.EffectiveClockMHz ??
                    fan?.EffectiveClockMHz;
                try
                {
                    // A broken NVML result such as 0/200C must not mask a
                    // valid LibreHardwareMonitor GPU fallback.
                    _referenceSensorGpu = OverviewTemperatureReadingPolicy.ValidTemperature(
                        GpuNvapiUsageService.TryReadTemperature()) ??
                        OverviewTemperatureReadingPolicy.ValidTemperature(
                            GpuNvmlUsageService.TryReadTemperature()) ??
                        OverviewTemperatureReadingPolicy.ValidTemperature(
                            CpuTemperatureService.ReadGpuTelemetry().Celsius);
                }
                catch { _referenceSensorGpu = null; }
            }
            // The dispatcher may drop the odd (slow-sensor) sample and keep
            // only the following fast CPU sample. Carry the latest slow
            // generation with BOTH UI frames until actually painted.
            long gpuSensorGeneration = System.Threading.Volatile.Read(ref _latestGpuSensorGeneration);
            long diskSensorGeneration = System.Threading.Volatile.Read(ref _latestDiskSensorGeneration);
            // GPU/disk locals above are reused across worker iterations. Capture
            // the matching values per queued UI frame, not a mutable future
            // reading whose generation might belong to another frame.
            double? sampledGpu = gpu;
            double? sampledDisk = disk;
            bool refreshFastGauges = true;
            bool refreshOverviewChart = tick % 2 == 0;
            tick++;

            if (token.IsCancellationRequested) break;
            if (_windowTelemetryActive && !_liveMetricsDiagnosticSuppressUi &&
                (activeOverview || refreshFastGauges || gpuMetricsSample || diskMetricsSample))
            {
                QueueLiveMetricsUiUpdate(() =>
                {
                    long uiPhaseStart = OverviewUiPhaseAudit
                        ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    if (refreshFastGauges)
                    {
                        AnimateGauge(_cpuGauge, cpu, refreshOverviewChart);
                        AnimateGauge(_ramGauge, ram, refreshOverviewChart);
                        _latestOverviewRamSample = ramSample;
                        UpdateOverviewRamSummary(ramSample);
                    }
                    // On resume first show the explicit "loading" state
                    // rather than retaining stale GPU/disk gauge readings.
                    // Normal PDH cadence remains every fourth second.
                    // GPU and disk are SAMPLED only on odd worker ticks
                    // (each 4 seconds). The shared CPU/RAM chart repaint
                    // flag is true on even ticks, so using that flag here
                    // starves subsequent GPU/disk sparklines of every fresh
                    // sensor reading. Their own sensor cadence already caps
                    // native geometry writes at once per 4 seconds.
                    bool gpuNeedsPaint = ShouldPaintSlowSensorSample(
                        gpuSensorGeneration, _paintedGpuSensorGeneration,
                        firstActiveSample || (_gpuGauge?.Samples.Count == 0 &&
                            _gpuGauge?.MissingSamples == 0));
                    if (_gpuGauge != null && gpuNeedsPaint)
                    {
                        AnimateGauge(_gpuGauge, sampledGpu,
                            OverviewGaugeSparkDue(gpuNeedsPaint, refreshOverviewChart));
                        _paintedGpuSensorGeneration = gpuSensorGeneration;
                    }
                    bool diskNeedsPaint = ShouldPaintSlowSensorSample(
                        diskSensorGeneration, _paintedDiskSensorGeneration,
                        firstActiveSample || (_diskGauge?.Samples.Count == 0 &&
                            _diskGauge?.MissingSamples == 0));
                    if (_diskGauge != null && diskNeedsPaint)
                    {
                        AnimateGauge(_diskGauge, sampledDisk,
                            OverviewGaugeSparkDue(diskNeedsPaint, refreshOverviewChart));
                        _paintedDiskSensorGeneration = diskSensorGeneration;
                    }
                    long afterGauges = OverviewUiPhaseAudit
                        ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

                    if (activeOverview)
                    {
                        // The lower-bound search assumes chronological samples.
                        // Wall-clock correction after sleep/NTP can move backwards;
                        // discard the invalid time axis rather than showing stale
                        // future points or silently misplacing the cutoff.
                        if (_overviewPerfHistory.Count > 0 && sampleUtc < _overviewPerfHistory[^1].Utc)
                        {
                            _overviewPerfHistory.Clear();
                            _overviewNextHistoryPruneUtc = DateTime.MinValue;
                            _overviewChartLastRenderedSampleUtc = default;
                            _overviewChartLastRenderedRightEdgeUtc = default;
                            _overviewChartLastRenderedFirst = -1;
                        }
                        _overviewPerfHistory.Add(new OverviewPerfSample(sampleUtc, cpu, sampledGpu, ram, sampledDisk));
                        if (sampleUtc >= _overviewNextHistoryPruneUtc)
                        {
                            DateTime keepAfter = sampleUtc.AddMinutes(-65);
                            int expired = FindFirstOverviewSampleAtOrAfter(_overviewPerfHistory, keepAfter);
                            if (expired > 0) _overviewPerfHistory.RemoveRange(0, expired);
                            _overviewNextHistoryPruneUtc = sampleUtc.AddMinutes(1);
                        }
                        UpdateReferenceHeader(cpu, ram, sampledGpu, sampledDisk, refreshOverviewChart);
                        // The active reference Overview has its own live 60-second
                        // chart. Skip the detached legacy chart path entirely;
                        // if a legacy host is reintroduced, retain its cadence.
                        if (refreshOverviewChart && _overviewCpuChartLine != null)
                            UpdateOverviewPerformanceChart();
                        long afterCharts = OverviewUiPhaseAudit
                            ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

                        UpdateOverviewFooterReadouts(cpu, sampledGpu, ram, sampledDisk);
                        UpdateOverviewUptimeReadout();
                        if (networkRates.BaselineReset)
                            ResetOverviewNetworkHistory();
                        // The current Overview uses the reference network chart.
                        // Legacy strip/plot elements are null on that page;
                        // avoid filling unused rolling queues every four seconds.
                        if (_overviewNetworkStatusText != null ||
                            _overviewNetworkSpeedText != null ||
                            _overviewNetDownLine != null ||
                            _overviewNetUpLine != null)
                            UpdateOverviewNetworkVisuals(networkRates.DownMbps,
                                networkRates.UpMbps, networkRates.Online);
                        UpdateReferenceNetwork(networkRates.DownMbps, networkRates.UpMbps,
                            networkRates.Online, refreshOverviewChart, networkRates.AdapterLabel);
                        long afterNetwork = OverviewUiPhaseAudit
                            ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                        UpdateReferenceTemperature(_referenceSensorCpu, _referenceSensorGpu,
                            _referenceSensorBoard, _referenceSensorFanRpm);
                        if (OverviewUiPhaseAudit)
                        {
                            long afterTemperature = System.Diagnostics.Stopwatch.GetTimestamp();
                            double Ms(long a, long b) =>
                                System.Diagnostics.Stopwatch.GetElapsedTime(a, b).TotalMilliseconds;
                            LogService.Info("performance",
                                $"OVERVIEW_UI_PHASE gaugesMs={Ms(uiPhaseStart, afterGauges):F2} " +
                                $"chartsMs={Ms(afterGauges, afterCharts):F2} " +
                                $"networkMs={Ms(afterCharts, afterNetwork):F2} " +
                                $"temperatureMs={Ms(afterNetwork, afterTemperature):F2} " +
                                $"totalMs={Ms(uiPhaseStart, afterTemperature):F2}");
                        }
                    }
                }, token, sampleStartedTicks);
            }
            // Full telemetry stays responsive only while Overview is active. When the app loses
            // focus, keep a lightweight CPU/RAM snapshot at the relaxed cadence and avoid GPU,
            // disk, network and chart work until the window becomes active again.
            await Task.Delay(activeOverview ? 2000 : 10000, token).ConfigureAwait(false);
        }
    }

    // A blocked dispatcher or quick hide/show can leave stale samples queued.
    // Discard data whose measurement is older than the network/CPU sampling
    // freshness horizon rather than letting it repaint a recently reopened UI.
    internal static bool IsOverviewLiveSampleFresh(long sampleTicks, long nowTicks) =>
        sampleTicks > 0 && nowTicks >= sampleTicks &&
        System.Diagnostics.Stopwatch.GetElapsedTime(sampleTicks, nowTicks) <= TimeSpan.FromSeconds(8);

    // At most ONE queued dispatcher callback for Overview telemetry. A busy
    // WinUI thread must show the latest sample rather than replaying every
    // outdated 2-second reading when it becomes responsive again.
    private readonly object _liveMetricsUiGate = new();
    private readonly record struct QueuedLiveMetricsUiSample(
        Action Update, CancellationToken Token, long StartedTicks,
        int NavigationGeneration, int ActivationEpoch);
    private QueuedLiveMetricsUiSample? _latestLiveMetricsUiSample;
    private bool _liveMetricsUiEnqueued;
    private int _liveMetricsActivationEpoch;
    // Monotonic sample generations survive coalescing and worker restarts.
    // Updated only by the sensor worker, acknowledged only on the UI thread.
    private long _latestGpuSensorGeneration;
    private long _latestDiskSensorGeneration;
    private long _paintedGpuSensorGeneration;
    private long _paintedDiskSensorGeneration;

    internal static bool ShouldPaintSlowSensorSample(
        long sampledGeneration, long paintedGeneration, bool firstActiveFrame) =>
        firstActiveFrame || sampledGeneration != paintedGeneration;

    private void QueueLiveMetricsUiUpdate(
        Action update, CancellationToken token, long sampleStartedTicks)
    {
        if (token.IsCancellationRequested || _windowLifetimeCts.IsCancellationRequested)
            return;
        var sample = new QueuedLiveMetricsUiSample(
            update, token, sampleStartedTicks,
            System.Threading.Volatile.Read(ref _navigationGeneration),
            System.Threading.Volatile.Read(ref _liveMetricsActivationEpoch));
        lock (_liveMetricsUiGate)
        {
            _latestLiveMetricsUiSample = sample;
            if (_liveMetricsUiEnqueued) return;
            _liveMetricsUiEnqueued = true;
        }

        if (DispatcherQueue.TryEnqueue(DrainLatestLiveMetricsUiUpdate)) return;
        // Dispatcher disposed during shutdown: do not retain the last UI tree.
        lock (_liveMetricsUiGate)
        {
            _latestLiveMetricsUiSample = null;
            _liveMetricsUiEnqueued = false;
        }
    }

    private void DrainLatestLiveMetricsUiUpdate()
    {
        QueuedLiveMetricsUiSample? sample;
        lock (_liveMetricsUiGate)
        {
            sample = _latestLiveMetricsUiSample;
            _latestLiveMetricsUiSample = null;
            _liveMetricsUiEnqueued = false;
        }
        if (sample is not { } latest ||
            latest.Token.IsCancellationRequested ||
            _windowLifetimeCts.IsCancellationRequested ||
            !_windowTelemetryActive || _section != "overview" ||
            latest.NavigationGeneration != _navigationGeneration ||
            latest.ActivationEpoch != _liveMetricsActivationEpoch ||
            !IsOverviewLiveSampleFresh(
                latest.StartedTicks, System.Diagnostics.Stopwatch.GetTimestamp()))
            return;
        latest.Update();
    }

    private double? ReadCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            _lastCpuKernel = 0; // Do not compare with counters from a prior failed read.
            return null;
        }
        long i = idle.Value, k = kernel.Value, u = user.Value;
        if (_lastCpuKernel == 0)
        {
            _lastCpuIdle = i; _lastCpuKernel = k; _lastCpuUser = u;
            return null; // One counter snapshot is not a CPU utilization sample.
        }
        long idleDelta = i - _lastCpuIdle;
        long kernelDelta = k - _lastCpuKernel;
        long userDelta = u - _lastCpuUser;
        _lastCpuIdle = i; _lastCpuKernel = k; _lastCpuUser = u;
        return ComputeOverviewCpuPercent(idleDelta, kernelDelta, userDelta);
    }

    internal static double? ComputeOverviewCpuPercent(
        long idleDelta, long kernelDelta, long userDelta)
    {
        if (idleDelta < 0 || kernelDelta < 0 || userDelta < 0 ||
            kernelDelta > long.MaxValue - userDelta)
            return null;
        long totalDelta = kernelDelta + userDelta;
        if (totalDelta <= 0 || idleDelta > totalDelta)
            return null; // Rollback/reset/invalid native counters, not 0% load.
        return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private static (double? Percent, double UsedGb, double TotalGb) ReadRamSnapshot()
    {
        var status = new LiveMemoryStatus { Length = (uint)Marshal.SizeOf<LiveMemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
            return (null, 0, 0);
        return ComputeOverviewRamSnapshot(
            status.TotalPhys, status.AvailPhys, status.MemoryLoad);
    }

    internal static (double? Percent, double UsedGb, double TotalGb)
        ComputeOverviewRamSnapshot(ulong totalBytes, ulong availableBytes, uint memoryLoad)
    {
        if (totalBytes == 0 || availableBytes > totalBytes || memoryLoad > 100)
            return (null, 0, 0);
        const double gb = 1024d * 1024d * 1024d;
        return (memoryLoad, (totalBytes - availableBytes) / gb, totalBytes / gb);
    }

    private static double? ReadDiskActivity() => DiskPdhUsageService.TryRead();

    private static double? ReadGpuUsage() => GpuPdhUsageService.TryRead();

    [StructLayout(LayoutKind.Sequential)]
    private struct LiveFileTime
    {
        public uint Low;
        public uint High;
        public long Value => ((long)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LiveMemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out LiveFileTime idle, out LiveFileTime kernel, out LiveFileTime user);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref LiveMemoryStatus status);
    private FrameworkElement OverviewGlowIcon(string tag, double iconSize, SolidColorBrush accent)
    { return SectionIcon(tag, iconSize); }
    private static Brush? _overviewCardBackground;
    private static Brush? _overviewNeonBorder;

    private static Brush OverviewCardBackgroundBrush()
    { return _darkTheme ? (_overviewCardBackground ??= GlassPanelBrush()) : SurfaceElevatedBrush(); }
    private static Brush OverviewNeonBorderBrush()
    { return _darkTheme ? (_overviewNeonBorder ??= GlassEdgeBrush()) : BorderThemeBrush(); }

    private Border BuildOverviewPerformanceChart()
    {
        _overviewChartLastRenderedSampleUtc = default;
        _overviewChartLastRenderedRightEdgeUtc = default;
        _overviewChartLastRenderedFirst = -1;
        var root = new Grid { MinHeight = 132, Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var item in new[]
        {
            ("CPU", CpuMetricBrush()),
            ("GPU", GpuMetricBrush()),
            ("RAM", RamMetricBrush()),
            ("Disk", DiskMetricBrush())
        })
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            entry.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = item.Item2, VerticalAlignment = VerticalAlignment.Center });
            entry.Children.Add(new TextBlock { Text = item.Item1, FontSize = 10.5, Foreground = TextSecondaryBrush() });
            legend.Children.Add(entry);
        }
        root.Children.Add(legend);

        var canvas = new Canvas
        {
            Height = 96,
            Margin = new Thickness(0, 8, 0, 0),
            Background = UseGlassChrome ? GlassSurfaceLowBrush() : ThemeColor(35, 9, 12, 24)
        };
        string[] yLabels = { "100%", "75%", "50%", "25%", "0%" };
        for (int i = 0; i < yLabels.Length; i++)
        {
            double y = 10 + i * 17d;
            canvas.Children.Add(new Line
            {
                X1 = 38,
                X2 = 520,
                Y1 = y,
                Y2 = y,
                Stroke = ThemeColor(43, 52, 74),
                StrokeThickness = 1,
                Opacity = .65
            });
            var label = new TextBlock { Text = yLabels[i], FontSize = 10.5, Foreground = TextSecondaryBrush() };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 7);
            canvas.Children.Add(label);
        }

        string[] xLabels = { "60 s", "45 s", "30 s", "15 s", "0 s" };
        _overviewChartXLabels = new TextBlock[xLabels.Length];
        for (int i = 0; i < xLabels.Length; i++)
        {
            var label = new TextBlock { Text = xLabels[i], FontSize = 10.5, Foreground = TextSecondaryBrush() };
            _overviewChartXLabels[i] = label;
            Canvas.SetLeft(label, 38 + i * 118d);
            Canvas.SetTop(label, 80);
            canvas.Children.Add(label);
        }

        _overviewRamChartFill = new OverviewSegmentedChart(null,
            ThemeColor(28, RamMetricBrush().Color.R, RamMetricBrush().Color.G, RamMetricBrush().Color.B));
        canvas.Children.Add(_overviewRamChartFill.Visual);

        _overviewRamChartLine = new OverviewSegmentedChart(RamMetricBrush());
        _overviewCpuChartLine = new OverviewSegmentedChart(CpuMetricBrush());
        _overviewGpuChartLine = new OverviewSegmentedChart(GpuMetricBrush());
        _overviewDiskChartLine = new OverviewSegmentedChart(DiskMetricBrush());
        canvas.Children.Add(_overviewRamChartLine.Visual);
        canvas.Children.Add(_overviewCpuChartLine.Visual);
        canvas.Children.Add(_overviewGpuChartLine.Visual);
        canvas.Children.Add(_overviewDiskChartLine.Visual);
        UpdateOverviewPerformanceChart();

        Grid.SetRow(canvas, 1);
        root.Children.Add(canvas);

        return new Border
        {
            Background = UseGlassChrome ? OverviewLiquidSurface() : SurfaceAltBrush(),
            BorderBrush = UseGlassChrome ? OverviewLiquidEdge() : OverviewNeonBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = root
        };
    }

    private void SetOverviewRange(int minutes)
    {
        _overviewRangeMinutes = Math.Clamp(minutes, 1, 60);
        foreach (var item in _overviewRangePills)
        {
            bool active = item.Minutes == _overviewRangeMinutes;
            item.Pill.Background = active
                ? (UseGlassChrome ? GlassAccentTintBrush() : ButtonVioletBrush())
                : (UseGlassChrome ? OverviewLiquidInset() : SurfaceAltBrush());
            item.Pill.BorderBrush = active ? ButtonVioletBorderBrush() : (UseGlassChrome ? OverviewLiquidEdge() : BorderThemeBrush());
            if (item.Pill.Child is TextBlock text)
                text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
        }

        if (_overviewChartXLabels is { Length: 5 })
        {
            string[] labels = _overviewRangeMinutes switch
            {
                <= 1 => OverviewRangeLabels1m,
                5 => OverviewRangeLabels5m,
                15 => OverviewRangeLabels15m,
                _ => OverviewRangeLabels60m
            };
            for (int i = 0; i < labels.Length; i++) SetOverviewTextIfChanged(_overviewChartXLabels[i], labels[i]);
        }
        UpdateOverviewPerformanceChart();
    }

    private void UpdateOverviewPerformanceChart()
    {
        if (_overviewCpuChartLine == null || _overviewGpuChartLine == null ||
            _overviewRamChartLine == null || _overviewDiskChartLine == null || _overviewRamChartFill == null)
            return;

        DateTime rightEdge = DateTime.UtcNow;
        DateTime cutoff = rightEdge.AddMinutes(-_overviewRangeMinutes);
        int first = FindFirstOverviewSampleAtOrAfter(_overviewPerfHistory, cutoff);
        int sourceCount = _overviewPerfHistory.Count - first;
        if (sourceCount <= 0)
        {
            _overviewCpuChartLine.Reset();
            _overviewGpuChartLine.Reset();
            _overviewRamChartLine.Reset();
            _overviewDiskChartLine.Reset();
            _overviewRamChartFill.Reset();
            _overviewChartLastRenderedSampleUtc = default;
            _overviewChartLastRenderedRightEdgeUtc = default;
            return;
        }

        DateTime latestSample = _overviewPerfHistory[^1].Utc;
        if (IsOverviewChartRenderRedundant(latestSample, _overviewRangeMinutes, first, sourceCount,
                rightEdge, _overviewChartLastRenderedSampleUtc, _overviewChartLastRenderedRangeMinutes,
                _overviewChartLastRenderedFirst, _overviewChartLastRenderedSourceCount,
                _overviewChartLastRenderedRightEdgeUtc))
            return;

        // Use sample timestamps and preserve each bucket's extrema: a short
        // history must not stretch across 60 min or hide brief real spikes.
        RenderOverviewHistoryBatch(_overviewPerfHistory, first, cutoff, rightEdge,
            _overviewEnvelopeCursors, _overviewCpuChartLine, _overviewGpuChartLine,
            _overviewRamChartLine, _overviewDiskChartLine, _overviewRamChartFill);
        _overviewChartLastRenderedSampleUtc = latestSample;
        _overviewChartLastRenderedRightEdgeUtc = rightEdge;
        _overviewChartLastRenderedRangeMinutes = _overviewRangeMinutes;
        _overviewChartLastRenderedFirst = first;
        _overviewChartLastRenderedSourceCount = sourceCount;
    }

    private enum OverviewMetricKind { Cpu, Gpu, Ram, Disk }

    private static double? OverviewMetricValue(
        in OverviewPerfSample sample, OverviewMetricKind metric) => metric switch
    {
        OverviewMetricKind.Cpu => sample.Cpu,
        OverviewMetricKind.Gpu => sample.Gpu,
        OverviewMetricKind.Ram => sample.Ram,
        _ => sample.Disk
    };
}
