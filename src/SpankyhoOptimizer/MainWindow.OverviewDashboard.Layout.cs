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
    private void ScheduleOverviewGaugeCards(Grid host, CancellationToken token)
    {
        int generation = _navigationGeneration;
        // A canceled visit can leave the chart shell with only some gauges.
        // Resume the missing gauges when the cached Overview is revisited;
        // do not rebuild the chart or duplicate existing native visuals.
        if (host.Children.Count > 0 && _overviewTelemetryGrid is { Children.Count: 6 }) return;
        if (ReferenceEquals(_overviewGaugeQueuedHost, host) && _overviewGaugeQueuedToken == token) return;
        _overviewGaugeQueuedHost = host;
        _overviewGaugeQueuedToken = token;
        if (host.Children.Count > 0)
        {
            if (_overviewTelemetryGrid is { Children.Count: < 6 })
                QueueReferenceOverviewCardBatch(host, token, generation,
                    _overviewTelemetryGrid.ColumnDefinitions.Count,
                    System.Diagnostics.Stopwatch.StartNew());
            else
                _overviewGaugeQueuedHost = null;
            return;
        }

        bool queued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "overview" ||
                !ReferenceEquals(_overviewGaugeQueuedHost, host) || _overviewGaugeQueuedToken != token ||
                !ReferenceEquals(_overviewGaugeHost, host) || host.Parent == null || host.Children.Count > 0)
            {
                if (ReferenceEquals(_overviewGaugeQueuedHost, host) && _overviewGaugeQueuedToken == token)
                    _overviewGaugeQueuedHost = null;
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Reference composition: wide 60-second chart followed by six compact
            // gauges in two rows of three. Reuse existing live CPU/RAM/GPU/disk
            // workers; only native WinUI shapes, no bitmap mockups.
            var shell = new StackPanel { Spacing = 8 };
            var mainChart = BuildReferenceMainPerformanceChart();
            shell.Children.Add(mainChart);
            _overviewTelemetryGrid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
            double windowWidth = RootGrid.ActualWidth > 1 ?
                RootGrid.ActualWidth : StartupClientWidth;
            int columns = ReferenceOverviewColumnCount(windowWidth);
            for (int i=0;i<columns;i++)
                _overviewTelemetryGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i=0;i<6/columns;i++)
                _overviewTelemetryGrid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            shell.Children.Add(_overviewTelemetryGrid);

            var panel = new Border {
                Background = ThemeColor(0,0,0,0), BorderThickness = new Thickness(0),
                Padding = new Thickness(0), Child = shell };
            _overviewGaugePanel = panel;
            host.Children.Add(panel);
            if (sw.ElapsedMilliseconds >= 8)
                LogService.Info("performance", $"OVERVIEW_REFERENCE_STAGE stage=shell ms={sw.ElapsedMilliseconds}");

            // Two cards per dispatcher turn avoid seven chained callbacks.
            // Keep each batch bounded so the first frame and user input remain responsive.
            QueueReferenceOverviewCardBatch(host, token, generation, columns, sw);
        });
        if (!queued && ReferenceEquals(_overviewGaugeQueuedHost, host) &&
            _overviewGaugeQueuedToken == token)
            _overviewGaugeQueuedHost = null;
    }

    private void QueueReferenceOverviewCardBatch(
        Grid host, CancellationToken token, int generation, int columns,
        System.Diagnostics.Stopwatch sw)
    {
        bool queued = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "overview" || !ReferenceEquals(_overviewGaugeQueuedHost, host) ||
                _overviewGaugeQueuedToken != token ||
                !ReferenceEquals(_overviewGaugeHost, host) || host.Parent == null ||
                _overviewTelemetryGrid is not { } grid)
                return;

            int start = grid.Children.Count;
            int end = Math.Min(start + 2, 6);
            var batchTiming = System.Diagnostics.Stopwatch.StartNew();
            for (int index = start; index < end; index++)
            {
                var card = index switch
                {
                    0 => BuildReferenceGauge("CPU", ReferenceCpuAccent(), out _cpuGauge),
                    1 => BuildReferenceGauge("RAM", ReferenceRamAccent(), out _ramGauge),
                    2 => BuildReferenceGauge("GPU", ReferenceGpuAccent(), out _gpuGauge),
                    3 => BuildReferenceGauge("Úložiště", ReferenceDiskAccent(), out _diskGauge),
                    4 => BuildReferenceNetworkCard(),
                    _ => BuildReferenceTemperatureCard()
                };
                Grid.SetColumn(card, index % columns);
                Grid.SetRow(card, index / columns);
                grid.Children.Add(card);
                if (_overviewTextRefs is { } textRefs)
                {
                    var gauge = index switch
                    {
                        0 => _cpuGauge, 1 => _ramGauge,
                        2 => _gpuGauge, 3 => _diskGauge, _ => null
                    };
                    string detail = index switch
                    {
                        0 => textRefs.Cpu.Text, 1 => textRefs.Ram.Text,
                        2 => textRefs.Gpu.Text, 3 => textRefs.Disk.Text, _ => ""
                    };
                    if (gauge != null && detail != "Načítám…")
                        SetOverviewTextIfChanged(gauge.SecondaryText, detail);
                }
            }

            // Normal two-card batches cost 3–6ms; avoid a log write for
            // each frame-sized dispatcher callback unless it exceeds 8ms.
            long batchMs = batchTiming.ElapsedMilliseconds;
            if (batchMs >= 8)
                LogService.Info("performance",
                    $"OVERVIEW_GAUGE_BATCH start={start} cards={end-start} ms={batchMs}");
            if (end < 6)
                QueueReferenceOverviewCardBatch(host, token, generation, columns, sw);
            else
            {
                _overviewGaugeQueuedHost = null;
                // A resize can happen while the three two-card batches run.
                // The final batch must honor the latest window width.
                ReflowOverviewGaugeColumns(RootGrid.ActualWidth);
                SchedulePageScrollPolicyRefresh();
                LogService.Info("performance",
                    $"OVERVIEW_REFERENCE_STAGE stage=complete cards=6 chart=true ms={sw.ElapsedMilliseconds}");
            }
        });
        if (!queued && ReferenceEquals(_overviewGaugeQueuedHost, host) &&
            _overviewGaugeQueuedToken == token)
            _overviewGaugeQueuedHost = null;
    }

    private void ScheduleOverviewCards(
        Grid metricsHost,
        Grid liveHost,
        OverviewTextRefs refs,
        bool compact,
        CancellationToken token)
    {
        int generation = _navigationGeneration;
        if ((liveHost.Children.Count > 0 || metricsHost.Children.Count > 0) ||
            (ReferenceEquals(_overviewCardsQueuedHost, metricsHost) && _overviewCardsQueuedToken == token))
            return;

        _overviewCardsQueuedHost = metricsHost;
        _overviewCardsQueuedToken = token;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "overview" ||
                !ReferenceEquals(_overviewMetricsHost, metricsHost) ||
                !ReferenceEquals(_overviewLiveHost, liveHost) || metricsHost.Parent == null || liveHost.Parent == null ||
                metricsHost.Children.Count > 0 || liveHost.Children.Count > 0)
            {
                if ((ReferenceEquals(_overviewCardsQueuedHost, metricsHost) && _overviewCardsQueuedToken == token))
                    _overviewCardsQueuedHost = null;
                return;
            }

            var metricsSw = System.Diagnostics.Stopwatch.StartNew();
            // Hardware identity is already shown directly inside the four live telemetry cards.
            // Keep this legacy host empty/collapsed so Overview remains a single-screen dashboard.
            long metricsStageMs = metricsSw.ElapsedMilliseconds;
            LogService.Info("performance", $"OVERVIEW_CARDS_STAGE stage=metrics ms={metricsStageMs} compact={compact}");

            {
                if (token.IsCancellationRequested || _section != "overview" ||
                    !ReferenceEquals(_overviewMetricsHost, metricsHost) ||
                    !ReferenceEquals(_overviewLiveHost, liveHost) ||
                    liveHost.Children.Count > 0)
                {
                    if ((ReferenceEquals(_overviewCardsQueuedHost, metricsHost) && _overviewCardsQueuedToken == token))
                        _overviewCardsQueuedHost = null;
                    return;
                }

                var liveSw = System.Diagnostics.Stopwatch.StartNew();
                var uptimeValue = new TextBlock
                {
                    Text = FormatUptime(),
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    Foreground = TextPrimaryBrush(),
                    TextWrapping = TextWrapping.NoWrap,
                    MaxLines = 1
                };
                _overviewUptimeText = uptimeValue;

                // No second network meter. System/status/changelog are now
                // real, permanently visible cards INSIDE the Overview page.
                _overviewInfoCardsGrid=BuildReferenceInlineSystemCards(refs,uptimeValue);
                liveHost.Children.Add(_overviewInfoCardsGrid);
                SchedulePageScrollPolicyRefresh();
                SafeTaskService.Forget(RefreshOverviewChangelogRowsAsync(token),
                    "overview","Overview inline changelog refresh");
                LogService.Info("visual-qa",
                    "OVERVIEW_INLINE_INFO_READY uniqueCards=3 networkDuplicate=0 flyout=false");
                if ((ReferenceEquals(_overviewCardsQueuedHost, metricsHost) && _overviewCardsQueuedToken == token))
                    _overviewCardsQueuedHost = null;
                long liveStageMs = liveSw.ElapsedMilliseconds;
                LogService.Info("performance", $"OVERVIEW_CARDS_STAGE stage=live ms={liveStageMs} compact={compact}");
                LogService.Info("performance", $"OVERVIEW_CARDS_DEFERRED staged=true metricsMs={metricsStageMs} liveMs={liveStageMs} compact={compact}");
            }
        });
    }

    private void InvalidateOverviewPageCache(bool forceRelease = false)
    {
        _overviewPageCache = null;
        _overviewTextRefs = null;
        _latestOverviewRamSample = null;
        _overviewGaugeHost = null;
        _overviewGaugeQueuedHost = null;
        _overviewGaugePanel = null;
        _overviewTelemetryGrid = null;
        _overviewMetricsHost = null;
        _overviewLiveHost = null;
        _overviewInfoFlyout = null;
        _overviewInfoCardsGrid = null;
        _overviewInfoButton = null;
        _overviewChangelogAction = null;
        _overviewCardsQueuedHost = null;
        _overviewPageCacheUsedAt = default;
        _overviewCacheCompact = false;

        _referenceCpuGlow?.Reset();
        _referenceCpuPlot?.Reset();
        _referenceCpuArea?.Reset();
        _referenceCpuGlow = null;
        _referenceVerticalGridLines.Clear();
        _referenceCpuPlot = null;
        _referenceCpuArea = null;
        _referenceCpuPlotHost = null;
        _referencePlotYTickLabels = null;
        _referencePlotYTicksHost = null;
        _referenceNetworkPlotHost = null;
        _referenceTemperatureRoot = null;
        _overviewInlineSecondHistory = null;
        _referenceBaseGaugeRowHeights = null;
        _referenceChartCpu = _referenceChartRam = _referenceChartGpu =
            _referenceChartDisk = _referenceChartNetwork = null;
        _referenceLeftUsage = null;
        _referenceNetworkValue = _referenceNetworkDetail = _referenceNetworkFoot = null;
        _referenceNetworkSpark = null;
        _referenceNetworkBlocks = null;
        _referenceNetworkLastLit = -1;
        _referenceNetworkHistory.Clear();
        _referenceTempBlocks = null;
        _referenceCpuTemperature = _referenceGpuTemperature = _referenceBoardTemperature = null;
        _referencePlotGridLines.Clear();
        _referenceLastPlotUtc = default;
        _referenceSensorCpu = _referenceSensorGpu = _referenceCpuClockMhz = _referenceSensorBoard = null;
        _referenceSensorFanRpm = null;
        _referenceLastRenderedTemperature = null;
        _referenceSensorStatusText = _referenceFanTelemetryText = null;
        _referenceLeftBlocks = null;
        _referenceLeftLastLit = -1;

        _overviewCpuChartLine?.Reset();
        _overviewGpuChartLine?.Reset();
        _overviewRamChartLine?.Reset();
        _overviewDiskChartLine?.Reset();
        _overviewRamChartFill?.Reset();
        _overviewCpuChartLine = null;
        _overviewGpuChartLine = null;
        _overviewRamChartLine = null;
        _overviewDiskChartLine = null;
        _overviewRamChartFill = null;
        _overviewChartXLabels = null;
        _overviewRangePills.Clear();
        _overviewCpuFooterText = _overviewGpuFooterText = _overviewRamFooterText = _overviewDiskFooterText = null;
        _overviewCpuFooterFill = _overviewGpuFooterFill = _overviewRamFooterFill = _overviewDiskFooterFill = null;
        _overviewUptimeText = null;
        _overviewLastFooterTextKey = null;
        _overviewLastUptimeTextTarget = null;
        _overviewLastUptimeMinute = -1;
        _overviewNetworkStatusText = null;
        _overviewNetworkSpeedText = null;
        _overviewLastNetworkStripKey = null;
        _overviewNetDownLine = null;
        _overviewNetUpLine = null;
        _overviewNetworkWasFlat = false;
        _overviewChangelogCaption = null;
        _overviewHistoryTimes = null;
        _overviewHistoryRows = null;
        _overviewSystemStatusText = null;
        _overviewDriverStatusText = null;
        _overviewHardwareCpuText = null;
        _overviewHardwareGpuText = null;
        _overviewHardwareRamText = null;
        _overviewHardwareDiskText = null;
        if (forceRelease || _section != "overview")
            ReleaseOverviewGaugeVisuals();
    }

    internal async Task RunOverviewCacheSmokeAsync()
    {
        InvalidateOverviewPageCache(forceRelease: true);
        await ShowSectionAsync("overview");
        await ShowSectionAsync("settings");
        await ShowSectionAsync("overview"); // Canceled partial gauges must resume on cached return.
        await ShowSectionAsync("overview"); // A repeated visit must never schedule duplicate gauges.
        for (int i = 0; i < 40 && (_cpuGauge == null ||
             _overviewTelemetryGrid is not { Children.Count: 6 } ||
             _overviewInfoCardsGrid is not { Children.Count: 3 }); i++)
            await Task.Delay(25);
        if (_overviewPageCache == null || _overviewPageCache.Count == 0 || _overviewTextRefs == null || _cpuGauge == null ||
            _overviewMetricsHost == null || _overviewMetricsHost.Visibility != Visibility.Collapsed ||
            _overviewLiveHost is not { Children.Count: 1 } || _overviewInfoCardsGrid is not { Children.Count: 3 })
            throw new InvalidOperationException("Overview cache/deferred cards were not captured after first render.");

        VerifyOverviewLiquidCards();
        if (_overviewTelemetryGrid is not { Children.Count: 6 } || _referenceCpuPlotHost == null)
            throw new InvalidOperationException("Reference Overview must have chart and six live cards.");
        // Both column breakpoints are inside compact mode. Reflow a *live*
        // cached grid without recreating any of the six gauge UI elements.
        var responsiveGrid = _overviewTelemetryGrid;
        var retainedGauges = responsiveGrid.Children.ToArray();
        ReflowOverviewGaugeColumns(1304);
        if (!ReflowOverviewGaugeColumns(920) ||
            responsiveGrid.ColumnDefinitions.Count != 2 || responsiveGrid.RowDefinitions.Count != 3 ||
            ReflowOverviewGaugeColumns(920))
            throw new InvalidOperationException("Overview did not enter or retain its two-column cached layout.");
        for (int i = 0; i < retainedGauges.Length; i++)
            if (!ReferenceEquals(responsiveGrid.Children[i], retainedGauges[i]) ||
                Grid.GetColumn((FrameworkElement)retainedGauges[i]) != i % 2 || Grid.GetRow((FrameworkElement)retainedGauges[i]) != i / 2)
                throw new InvalidOperationException("Two-column reflow replaced or misplaced live gauges.");
        if (!ReflowOverviewGaugeColumns(1304) ||
            responsiveGrid.ColumnDefinitions.Count != 3 || responsiveGrid.RowDefinitions.Count != 2)
            throw new InvalidOperationException("Overview did not restore its three-column cached layout.");
        for (int i = 0; i < retainedGauges.Length; i++)
            if (!ReferenceEquals(responsiveGrid.Children[i], retainedGauges[i]) ||
                Grid.GetColumn((FrameworkElement)retainedGauges[i]) != i % 3 || Grid.GetRow((FrameworkElement)retainedGauges[i]) != i / 3)
                throw new InvalidOperationException("Three-column reflow replaced or misplaced live gauges.");
        ReflowOverviewGaugeColumns(RootGrid.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth);
        SchedulePageScrollPolicyRefresh();
        LogService.Info("performance", "OVERVIEW_RESPONSIVE_CACHE_SMOKE_OK columns=2,3 retainedGauges=6 rebuilt=0");
        // The user's current compact Overview has one reference chart and one
        // network card, not duplicate detached legacy charts. Keep legacy
        // renderers available for other callers without running them live.
        if (_overviewCpuChartLine != null || _overviewNetDownLine != null ||
            _overviewNetUpLine != null || _overviewNetworkStatusText != null ||
            _overviewNetworkSpeedText != null)
            throw new InvalidOperationException(
                "Compact Overview unexpectedly materialized duplicate legacy chart/network visuals.");
        LogService.Info("performance", "OVERVIEW_LEGACY_HIDDEN_SKIP_SMOKE_OK performance=true network=true");
        // Stroke and glow must own distinct native PathGeometry/PointCollections.
        // Sharing WinUI geometry previously caused native failures.
        if (_referenceCpuPlot == null || _referenceCpuGlow == null ||
            ReferenceEquals(_referenceCpuPlot.Visual.Data, _referenceCpuGlow.Visual.Data))
            throw new InvalidOperationException("Reference plot strokes must own independent native geometries.");
        LogService.Info("regression", "REFERENCE_PLOT_INDEPENDENT_STROKES_OK");
        var firstRoot = _overviewPageCache[0];
        var firstCpuGauge = _cpuGauge;
        await ShowSectionAsync("settings");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("overview");
        long attachMs = sw.ElapsedMilliseconds;

        if (_overviewPageCache == null || _overviewTextRefs == null)
            throw new InvalidOperationException("Overview cache disappeared before reuse.");
        if (!ReferenceEquals(_overviewPageCache[0], firstRoot))
            throw new InvalidOperationException("Overview cached UI tree was rebuilt instead of reused.");
        if (!ReferenceEquals(_cpuGauge, firstCpuGauge))
            throw new InvalidOperationException("Overview gauge composition objects were rebuilt instead of reused.");
        if (!ContentStack.Children.Cast<UIElement>().Any(x => ReferenceEquals(x, firstRoot)))
            throw new InvalidOperationException("Overview cached root was not reattached.");
        if (attachMs > 25)
        {
            // A single scheduler/GC spike must not fail the release. Preserve the
            // 25 ms limit, but require the median of three measured cache hits to pass.
            long[] samples = { attachMs, 0, 0 };
            for (int sample = 1; sample < samples.Length; sample++)
            {
                await ShowSectionAsync("settings");
                sw.Restart();
                await ShowSectionAsync("overview");
                samples[sample] = sw.ElapsedMilliseconds;
            }
            Array.Sort(samples);
            LogService.Info("performance", $"OVERVIEW_CACHE_LATENCY_RECHECK medianMs={samples[1]} minMs={samples[0]} maxMs={samples[2]}");
            if (samples[1] > 25)
                throw new InvalidOperationException($"Overview cached navigation persistently slow: median {samples[1]} ms.");
        }

        VerifyOverviewInteractionSmoke();
        // Exercise the exact card the user sees, not just the reducer. These
        // values are injected only in the isolated release UI smoke process.
        UpdateReferenceTemperature(58, 46, 40, 1590);
        if (_referenceCpuTemperature?.Text != "58 °C" ||
            _referenceGpuTemperature?.Text != "46 °C" ||
            _referenceBoardTemperature?.Text != "40 °C" ||
            _referenceFanTelemetryText?.Text?.Contains("1590", StringComparison.Ordinal) != true ||
            _referenceSensorStatusText?.Text != "● Senzory: 3/3 dostupné")
            throw new InvalidOperationException("Temperature/board/fan Overview card is not populated.");
        // Same-state refresh must not replace any UI contents; a changed GPU
        // reading must update despite the retained visual-state shortcut.
        UpdateReferenceTemperature(58, 46, 40, 1590);
        UpdateReferenceTemperature(58, 47, 40, 1590);
        if (_referenceGpuTemperature?.Text != "47 °C")
            throw new InvalidOperationException("Temperature card ignored a changed GPU sensor.");
        UpdateReferenceTemperature(58, null, null, null);
        if (_referenceGpuTemperature?.Text != "—" ||
            _referenceBoardTemperature?.Text != "—" ||
            _referenceSensorStatusText?.Text != "● Senzory: 1/3 dostupné")
            throw new InvalidOperationException("Temperature card retained stale unavailable sensors.");
        UpdateReferenceTemperature(_referenceSensorCpu, _referenceSensorGpu,
            _referenceSensorBoard, _referenceSensorFanRpm);
        if (_overviewLiveHost is not { Children.Count: 1 } ||
            _overviewInfoCardsGrid is not { Children.Count: 3 } ||
            _overviewInfoFlyout != null)
            throw new InvalidOperationException(
                "System / Optimizer / Changelog must remain inside Overview, not a popup.");
        LogService.Info("performance", RunOverviewChartPointReuseSmoke());
        LogService.Info("performance", RunOverviewChartRenderPolicySmoke());
        LogService.Info("performance", RunOverviewHistoryEnvelopeSmoke());
        LogService.Info("performance", RunOverviewHistoryBatchParitySmoke());
        LogService.Info("performance", RunOverviewSegmentedChartSmoke());
        LogService.Info("performance", RunOverviewNativeFigureRetentionSmoke());
        LogService.Info("performance", RunOverviewOptionalTelemetrySmoke());
        LogService.Info("performance", RunOverviewRamValiditySmoke());
        LogService.Info("performance", RunOverviewHistoryLookupBenchmark());
        LogService.Info("performance", RunReferenceOverviewPolicySmoke());
        LogService.Info("performance", RunReferenceCpuGapSmoke());
        LogService.Info("performance", RunChangelogPagingPolicySmoke());
        LogService.Info("performance", RunOverviewPointCollectionBenchmark());
        LogService.Info("performance", RunOverviewNetworkPointReuseBenchmark());
        LogService.Info("performance", RunOverviewNetworkBaselineSmoke());
        LogService.Info("performance", RunOverviewNetworkContinuitySmoke());
        LogService.Info("performance", RunReferenceNetworkSparkReuseBenchmark());
        LogService.Info("performance", $"OVERVIEW_CACHE_SMOKE_OK attachMs={attachMs} elements={_overviewPageCache.Count}");
    }
}
