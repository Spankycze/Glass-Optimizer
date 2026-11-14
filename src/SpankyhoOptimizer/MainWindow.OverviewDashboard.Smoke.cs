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
    internal async Task RunStartupOverviewVisualSmokeAsync()
    {
        for (int i = 0; i < 80 &&
             (!_initialSectionNavigationCompleted || _section != "overview" ||
              _overviewPageCache == null || _overviewPageCache.Count == 0 ||
              _overviewGaugePanel == null || _overviewTelemetryGrid is not { Children.Count: 6 } ||
              _overviewMetricsHost == null ||
              _overviewLiveHost is not { Children.Count: 1 } || _overviewInfoCardsGrid is not { Children.Count: 3 } ||
              _cpuGauge == null); i++)
            await Task.Delay(25);

        if (_section != "overview")
            throw new InvalidOperationException($"Startup section is '{_section}', expected overview.");
        if (!_initialSectionNavigationCompleted)
            throw new InvalidOperationException("Initial Overview navigation did not complete.");
        if (_overviewPageCache == null || _overviewPageCache.Count == 0)
            throw new InvalidOperationException("Overview startup root is missing.");
        if (_overviewGaugePanel == null || _overviewTelemetryGrid is not { Children.Count: 6 } ||
            _overviewLiveHost is not { Children.Count: 1 } || _overviewInfoCardsGrid is not { Children.Count: 3 } ||
            _cpuGauge == null)
            throw new InvalidOperationException("Overview startup cards/gauges are missing.");
        if (_overviewMetricsHost == null || _overviewMetricsHost.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Overview legacy hardware host should stay collapsed.");
        if (_overviewSystemStatusText == null || _overviewDriverStatusText == null)
            throw new InvalidOperationException("Overview optimizer status card was not created.");
        if (_overviewInfoFlyout != null || _overviewInfoButton != null ||
            _overviewLiveHost is not { Children.Count: 1 } ||
            _overviewInfoCardsGrid is not { Children.Count: 3 } ||
            !_overviewInfoCardsGrid.IsLoaded)
            throw new InvalidOperationException(
                "Three permanent inline cards must be visible with no info Flyout.");
        if (!ContentStack.Children.Cast<UIElement>().Any(x => _overviewPageCache.Contains(x)))
            throw new InvalidOperationException("Overview startup root is not attached to ContentStack.");

        await Task.Delay(75);
        if (!ContentStack.IsLoaded || ContentStack.Opacity < 0.99 ||
            ContentStack.ActualWidth < 100 || ContentStack.ActualHeight < 100)
            throw new InvalidOperationException($"Overview ContentStack not visible/laid out: loaded={ContentStack.IsLoaded} opacity={ContentStack.Opacity:0.##} size={ContentStack.ActualWidth:0.#}x{ContentStack.ActualHeight:0.#}.");
        if (_contentLayerHost == null || !_contentLayerHost.IsLoaded || _contentLayerHost.Opacity < 0.99 ||
            _contentLayerHost.ActualWidth < 100 || _contentLayerHost.ActualHeight < 100)
            throw new InvalidOperationException($"Overview content host not visible/laid out: loaded={_contentLayerHost?.IsLoaded} opacity={_contentLayerHost?.Opacity:0.##} size={_contentLayerHost?.ActualWidth:0.#}x{_contentLayerHost?.ActualHeight:0.#}.");
        if (_pageScroll == null || !_pageScroll.IsLoaded || _pageScroll.Opacity < 0.99 ||
            _pageScroll.ActualWidth < 100 || _pageScroll.ActualHeight < 100)
            throw new InvalidOperationException($"Overview page scroll not visible/laid out: loaded={_pageScroll?.IsLoaded} opacity={_pageScroll?.Opacity:0.##} size={_pageScroll?.ActualWidth:0.#}x{_pageScroll?.ActualHeight:0.#}.");
        for (int i = 0; i < 20 && (!_heroDeferredCompleted || _heroImageFrame?.Child is not Grid); i++)
            await Task.Delay(25);
        if (!_heroDeferredCompleted || _heroImageFrame?.Child is not Grid ||
            HeaderCard.Height < 100 || _heroImageFrame is not { Height: >= 100 })
            throw new InvalidOperationException($"Startup hero not configured: deferred={_heroDeferredCompleted} source={_heroBackgroundImage?.Source != null} headerH={HeaderCard.Height:0.#} frameH={_heroImageFrame?.Height:0.#}.");

        VerifyGlassBackdropLifecycle();

        double clientWidth = RootGrid.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth;
        double expectedHeroHeight = CalculateSectionHeroHeight("overview", clientWidth);
        double expectedFrameHeight = expectedHeroHeight;
        var expectedHeroMargin = new Thickness(HeroFrameLeft, 0, HeroFrameRight, 0);
        double subNavHeight = SubNavShell.Visibility == Visibility.Visible ? Math.Max(0, SubNavShell.ActualHeight) : 0;
        double expectedPageTop = Math.Max(0, expectedHeroHeight + subNavHeight - OverviewPageLift);

        if (Math.Abs(HeaderCard.Height - expectedHeroHeight) > 1 ||
            _heroImageFrame == null || Math.Abs(_heroImageFrame.Height - expectedFrameHeight) > 1 ||
            !_heroImageFrame.Margin.Equals(expectedHeroMargin) ||
            Math.Abs((_pageScroll?.Margin.Top ?? -1) - expectedPageTop) > 1)
            throw new InvalidOperationException(
                $"Overview shell geometry invariant failed: header={HeaderCard.Height:0.#}/{expectedHeroHeight:0.#} frame={_heroImageFrame?.Height:0.#}/{expectedFrameHeight:0.#} pageTop={_pageScroll?.Margin.Top:0.#}/{expectedPageTop:0.#}.");

        if (_overviewGaugePanel == null || _overviewTelemetryGrid == null)
            throw new InvalidOperationException("Overview gauge geometry refs are missing.");
        if (_overviewGaugePanel.Child is not StackPanel gaugeRoot || !gaugeRoot.Margin.Equals(new Thickness(0)))
            throw new InvalidOperationException("Overview gauge root gained an unexpected offset.");
        if (!_overviewGaugePanel.Padding.Equals(new Thickness(0)))
            throw new InvalidOperationException($"Overview gauge panel padding changed: {_overviewGaugePanel.Padding}.");

        double hostExcess = (_overviewGaugeHost?.ActualHeight ?? 0) - _overviewGaugePanel.ActualHeight;
        if (hostExcess > 3)
            throw new InvalidOperationException(
                $"Overview gauge host whitespace regression: host={_overviewGaugeHost?.ActualHeight:0.#} panel={_overviewGaugePanel.ActualHeight:0.#} excess={hostExcess:0.#}.");

        double headerHeight = HeaderCard.Height;
        double pageTop = _pageScroll?.Margin.Top ?? -1;
        int contentCount = ContentStack.Children.Count;
        await Task.Delay(150);
        if (ContentStack.Children.Count != contentCount)
            throw new InvalidOperationException("Overview startup content tree changed after first render.");
        if (Math.Abs(HeaderCard.Height - headerHeight) > 0.5 ||
            Math.Abs((_pageScroll?.Margin.Top ?? -1) - pageTop) > 0.5)
            throw new InvalidOperationException("Overview/header geometry shifted after first render.");

        // Validate the actual native controls under a taller simulated viewport,
        // then restore the real viewport before the final no-scroll check.
        // Pure release smoke only; no Windows settings or user window changes.
        if (_referenceCpuPlotHost != null && _cpuGauge != null &&
            _overviewInfoCardsGrid != null && _referenceNetworkPlotHost != null)
        {
            double available = _pageScroll.ViewportHeight;
            double actualContent = MeasurePageContentExtentHeight();
            if (available >= 590 && actualContent > 0)
            {
                double simulated = actualContent;
                for (int pass = 0; pass < 3 &&
                     FitReferenceOverviewToViewport(available + 100, simulated); pass++)
                {
                    PageHost.UpdateLayout();
                    simulated = MeasurePageContentExtentHeight();
                }
                double expandedChart = _referenceCpuPlotHost.Height;
                double expandedGauge = _cpuGauge.SparkHost.Height;
                double expandedInfo = _overviewInfoCardsGrid.MinHeight;
                if (expandedChart < ReferenceBasePlotHeight + 30 ||
                    expandedGauge < ReferenceBaseSparkHeight + 15 ||
                    expandedInfo < ReferenceBaseInfoHeight + 10 ||
                    Math.Abs(simulated - actualContent - 100) > 10)
                    throw new InvalidOperationException(
                        $"Overview tall-window distribution failed: chart={expandedChart:0.#}, gauge={expandedGauge:0.#}, info={expandedInfo:0.#}, growth={simulated-actualContent:0.#}.");
                for (int pass = 0; pass < 3 &&
                     FitReferenceOverviewToViewport(available, simulated); pass++)
                {
                    PageHost.UpdateLayout();
                    simulated = MeasurePageContentExtentHeight();
                }
            }
        }
        ApplyPageScrollPolicyNow();
        double fitViewport = _pageScroll.ViewportHeight;
        double fitContent = MeasurePageContentExtentHeight();
        LogService.Info("performance",
            $"OVERVIEW_VIEWPORT_FIT width={RootGrid.ActualWidth:0.#} height={RootGrid.ActualHeight:0.#} viewport={fitViewport:0.#} content={fitContent:0.#} scroll={_pageScroll.VerticalScrollMode}");
        if (_overviewInfoCardsGrid is not { Children.Count: 3 } ||
            _overviewLiveHost is not { Children.Count: 1 } ||
            _overviewInfoFlyout != null ||
            _overviewInfoCardsGrid.ActualHeight < 55)
            throw new InvalidOperationException(
                "Overview Systém/Stav/Changelog are not permanently visible.");
        if (RootGrid.ActualWidth >= 1024 && RootGrid.ActualHeight >= 740 &&
            (_pageScroll.VerticalScrollMode != ScrollMode.Disabled ||
             fitContent > fitViewport + 1 || fitViewport - fitContent > 12))
            throw new InvalidOperationException(
                $"Inline details don't fit without scrolling: viewport={fitViewport:0.#} content={fitContent:0.#}");
        LogService.Info("performance",
            $"STARTUP_OVERVIEW_VISUAL_SMOKE_OK roots={_overviewPageCache.Count} metrics={_overviewMetricsHost.Children.Count} live={_overviewLiveHost.Children.Count} stable=true heroUnderTitlebar=true inlineNoScroll=true");
    }

    private async Task CompleteOverviewDashboardAsync(Task<OverviewSnapshot> snapshotTask,
        TextBlock cpu, TextBlock gpu, TextBlock ram, TextBlock disk, TextBlock os, TextBlock board,
        TextBlock power, TextBlock network, TextBlock driver, TextBlock history, TextBlock windows, CancellationToken token, int generation, Task<TweakBatchOverviewSnapshot>? injectedHistory = null)
    {
        var statusRefs = _overviewTextRefs;
        if (statusRefs == null || !ReferenceEquals(statusRefs.Os, os)) return;
        bool snapshotLoaded = false;
        try
        {
            await Task.Yield();
            if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
            SafeTaskService.Forget(
                CompleteOverviewDriverAsync(driver, token, generation),
                "overview",
                "Deferred overview GPU driver");
            // Keep UI-owned status and tweak-cache reads on the WinUI dispatcher.
            // ConfigureAwait(false) here allowed continuations to enumerate live
            // List<TweakDefinition> values on a ThreadPool thread during navigation.
            var s = await snapshotTask;
            snapshotLoaded = true;
            if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
                SetOverviewTextIfChanged(cpu, s.Cpu);
                SetOverviewTextIfChanged(gpu, s.Gpu);
                SetOverviewTextIfChanged(ram, s.Ram);
                SetOverviewTextIfChanged(disk, s.Disk);
                SetOverviewTextIfChanged(os, s.Windows.Replace(" • 26H2", "\n26H2", StringComparison.OrdinalIgnoreCase));
                SetOverviewTextIfChanged(board, s.Motherboard);
                SetOverviewTextIfChanged(power, s.PowerPlan);
                SetOverviewTextIfChanged(network, s.Network);
                SetOverviewTextIfChanged(windows, s.Windows);

                if (_cpuGauge != null) SetOverviewTextIfChanged(_cpuGauge.SecondaryText, s.Cpu);
                if (_gpuGauge != null) SetOverviewTextIfChanged(_gpuGauge.SecondaryText, s.Gpu);
                if (_latestOverviewRamSample is { } liveRam)
                    UpdateOverviewRamSummary(liveRam);
                else if (_ramGauge != null) SetOverviewTextIfChanged(_ramGauge.SecondaryText, s.Ram);
                if (_diskGauge != null) SetOverviewTextIfChanged(_diskGauge.SecondaryText, s.Disk);
                if (_overviewHardwareCpuText != null) SetOverviewTextIfChanged(_overviewHardwareCpuText, s.Cpu);
                if (_overviewHardwareGpuText != null) SetOverviewTextIfChanged(_overviewHardwareGpuText, s.Gpu);
                if (_overviewHardwareRamText != null) SetOverviewTextIfChanged(_overviewHardwareRamText, string.IsNullOrWhiteSpace(s.RamHardware) ? s.Ram : s.RamHardware);
                if (_overviewHardwareDiskText != null) SetOverviewTextIfChanged(_overviewHardwareDiskText, s.Disk);
                ApplyOverviewSystemStatus(statusRefs, "●  Systém načten");
            });

            if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
            var tweakHistory = await (injectedHistory ?? TweakBatchHistoryService.ReadOverviewSnapshotAsync(4, 120, token));
            if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
            var recent = tweakHistory.Recent;
            var expectedTweakStates = tweakHistory.ExpectedStates;
            var detectedTweakStates = GetDetectedTweakStates();
            var mergedTweakStates = MergeActiveTweakStates(expectedTweakStates, detectedTweakStates);
            int expectedActiveTweaks = expectedTweakStates.Count(x => x.Value);
            int detectedActiveTweaks = detectedTweakStates.Count(x => x.Value);
            int activeTweaks = mergedTweakStates.Count(x => x.Value);
            string activeSource = detectedTweakStates.Count > 0
                ? (expectedTweakStates.Count > 0
                    ? $"ověřeno {detectedTweakStates.Count} + historie"
                    : $"ověřeno {detectedTweakStates.Count}")
                : "historie";
            string activeLogSignature = $"{activeTweaks}|{expectedActiveTweaks}|{expectedTweakStates.Count}|{detectedActiveTweaks}|{detectedTweakStates.Count}|{mergedTweakStates.Count}";
            DateTime activeLogNow = DateTime.UtcNow;
            if (OverviewDiagnosticLogPolicy.ShouldLog(_overviewActiveTweaksLogSignature,
                    activeLogSignature, _overviewActiveTweaksLastLogUtc, activeLogNow))
            {
                _overviewActiveTweaksLogSignature = activeLogSignature;
                _overviewActiveTweaksLastLogUtc = activeLogNow;
                LogService.Info("overview",
                    $"OVERVIEW_ACTIVE_TWEAKS active={activeTweaks} expectedActive={expectedActiveTweaks} expectedKnown={expectedTweakStates.Count} detectedActive={detectedActiveTweaks} detectedKnown={detectedTweakStates.Count} mergedKnown={mergedTweakStates.Count}");
            }
            string historyText = BuildOverviewActiveTweakStatusText(
                activeTweaks,
                mergedTweakStates.Count,
                activeSource,
                recent.Length == 0 ? null : recent[0].Time);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
                SetOverviewTextIfChanged(history, historyText);
            });


        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _windowLifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("overview", "Overview data failed", ex);
            // Complete each independent card even when the other data source
            // fails: otherwise History remains at "Načítám..." indefinitely.
            // Ownership checks prevent an older navigation from repainting a
            // replacement Overview tree after cancellation or retry.
            if (snapshotLoaded)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (CanPublishOverviewStatus(statusRefs, token, generation))
                        SetOverviewTextIfChanged(history, "Historie tweaků je dočasně nedostupná");
                });
            }
            else
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (CanPublishOverviewStatus(statusRefs, token, generation))
                        ApplyOverviewSystemStatus(statusRefs, "●  Systém se nepodařilo načíst");
                });
            }
        }
    }

    private async Task CompleteOverviewDriverAsync(TextBlock driver, CancellationToken token, int generation, Task<GpuDriverInfo?>? injectedWarmCache = null)
    {
        var statusRefs = _overviewTextRefs;
        if (statusRefs == null || !ReferenceEquals(statusRefs.Driver, driver) ||
            !CanPublishOverviewStatus(statusRefs, token, generation)) return;
        try
        {
            if (_gpuDetectionGeneration == GpuDriverService.DetectionCacheGeneration && _gpuInfo != null)
            {
                string cachedVersion = _gpuInfo.DisplayVersion;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (CanPublishOverviewStatus(statusRefs, token, generation))
                    {
                        driver.Text = cachedVersion;
                        ApplyOverviewDriverStatus(statusRefs, string.IsNullOrWhiteSpace(cachedVersion) ? "●  Ovladač nezjištěn" : "●  Ovladač zjištěn");
                    }
                });
                return;
            }

            // A valid persisted GPU identity takes only a few milliseconds
            // and needs no 900ms startup deferral or registry/WMI detection.
            // Defer the *expensive* hardware probe only on a true cache miss.
            var timing = System.Diagnostics.Stopwatch.StartNew();
            SynchronizeGpuDetectionGeneration();
            long detectionGeneration = _gpuDetectionGeneration;
            var persisted = await (injectedWarmCache ?? GpuDriverService.TryWarmPersistentDetectionCacheAsync(token));
            // The cached driver and navigation ownership are WinUI-managed state.
            if (!DispatcherQueue.HasThreadAccess)
                throw new InvalidOperationException("Overview GPU warm-cache completion must resume on UI dispatcher.");
            if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
            bool cacheHit = persisted != null &&
                detectionGeneration == GpuDriverService.DetectionCacheGeneration;
            GpuDriverInfo? gpuDriver;
            if (cacheHit)
            {
                gpuDriver = persisted;
                _gpuInfo = persisted;
            }
            else
            {
                // Protect the initial UI render from expensive driver detection,
                // while keeping cached status responsive on every navigation.
                await Task.Delay(900, token);
                if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
                gpuDriver = await GetOverviewGpuDriverAsync(token);
            }
            if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
            LogService.Info("performance",
                $"OVERVIEW_GPU_DRIVER_READY cacheHit={cacheHit} ms={timing.ElapsedMilliseconds} " +
                $"fallbackDeferred={!cacheHit}");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (CanPublishOverviewStatus(statusRefs, token, generation))
                {
                    SetOverviewTextIfChanged(driver, gpuDriver == null ? "Driver nezjištěn" : gpuDriver.DisplayVersion);
                    ApplyOverviewDriverStatus(statusRefs, gpuDriver == null || string.IsNullOrWhiteSpace(gpuDriver.DisplayVersion)
                        ? "●  Ovladač nezjištěn" : "●  Ovladač zjištěn");
                }
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _windowLifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("overview", "Overview GPU driver failed", ex);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (CanPublishOverviewStatus(statusRefs, token, generation))
                    ApplyOverviewDriverStatus(statusRefs, "●  Ovladač se nepodařilo načíst");
            });
        }
    }

    private const string OverviewSystemLoading = "●  Načítám systém…";
    private const string OverviewDriverLoading = "●  Načítám ovladač…";

    private bool CanPublishOverviewStatus(OverviewTextRefs owner, CancellationToken token, int generation) =>
        !_windowLifetimeCts.IsCancellationRequested && !token.IsCancellationRequested &&
        generation == _navigationGeneration && _section == "overview" && ReferenceEquals(owner, _overviewTextRefs);

    // UI-thread publication keeps completion even while the corresponding label does not exist.
    private void ApplyOverviewSystemStatus(OverviewTextRefs owner, string status)
    {
        owner.SystemStatus = status;
        if (ReferenceEquals(owner, _overviewTextRefs) && _overviewSystemStatusText != null)
            SetOverviewTextIfChanged(_overviewSystemStatusText, status);
    }

    private void ApplyOverviewDriverStatus(OverviewTextRefs owner, string status)
    {
        owner.DriverStatus = status;
        if (ReferenceEquals(owner, _overviewTextRefs) && _overviewDriverStatusText != null)
            SetOverviewTextIfChanged(_overviewDriverStatusText, status);
    }

    private Border BuildOverviewHardwareStrip(OverviewTextRefs refs, bool compact)
    { return BuildBlackHardwareStrip(refs, compact); }
    private void ReleaseOverviewGaugeVisuals()
    {
        ReleaseGauge(ref _cpuGauge);
        ReleaseGauge(ref _ramGauge);
        ReleaseGauge(ref _gpuGauge);
        ReleaseGauge(ref _diskGauge);
    }

    private static void ReleaseGauge(ref GaugeRefs? refs) => refs = null;

    private static void SetOverviewTextIfChanged(TextBlock block, string value)
    {
        if (!string.Equals(block.Text, value, StringComparison.Ordinal))
            block.Text = value;
    }

    private static void SetOverviewWidthIfChanged(FrameworkElement element, double value)
    {
        if (Math.Abs(element.Width - value) >= 0.1)
            element.Width = value;
    }
}
