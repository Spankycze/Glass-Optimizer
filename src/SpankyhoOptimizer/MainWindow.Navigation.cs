using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Windows.Graphics;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow : Window
{
    private Task NavigateMainSectionFromUiAsync(string section)
    {
        if (string.Equals(_section, section, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        return ShowSectionAsync(section);
    }

    private Task NavigateSubSectionFromUiAsync(string section, string sub)
    {
        bool sameSection = string.Equals(_section, section, StringComparison.OrdinalIgnoreCase);
        if (sameSection && string.Equals(_sub, sub, StringComparison.Ordinal))
            return Task.CompletedTask;

        _sub = sub;
        // A changed sub-tab within the same section must not be mistaken for
        // a redundant sidebar click. Keep the fast skip for unchanged tabs.
        return ShowSectionAsync(section, forceNavigation: sameSection);
    }

    private static async Task DisposeNavigationCancellationAsync(
        CancellationTokenSource cts,
        Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    public Task NavigateExternalAsync(string key) => ShowSectionAsync(key);

    private async Task ShowSectionAsync(string key, bool forceNavigation = false)
    {
        if (string.Equals(key, "services", StringComparison.OrdinalIgnoreCase))
        {
            key = "components";
            _sub = "Služby";
        }
        else if (string.Equals(key, "drivers", StringComparison.OrdinalIgnoreCase))
        {
            key = "components";
            _sub = "Ovladače";
        }
        else if (string.Equals(key, "tasks", StringComparison.OrdinalIgnoreCase))
        {
            key = "components";
            _sub = "Task Scheduler";
        }

        if (string.Equals(key, "lianli", StringComparison.OrdinalIgnoreCase))
        {
            var supported = await Task.Run(LianLiSlInfinityService.Detect).ConfigureAwait(true);
            if (!supported.Present)
                return;
        }

        if (!forceNavigation && TrySkipRedundantSameSectionNavigation(key))
        {
            _lastNavigationErrorId = null;
            _lastNavigationUtc = DateTime.UtcNow;
            SchedulePageScrollPolicyRefresh();
            return;
        }

        int generation = Interlocked.Increment(ref _navigationGeneration);
        var sw = Stopwatch.StartNew();

        // The persistent content host may be swapped while attaching a cached
        // section. Remember whether this exact loaded host was already reset
        // here so its native animations are not stopped twice at navigation end.
        FrameworkElement originalNavigationHost = ContentStack;
        bool originalNavigationHostReset = originalNavigationHost.IsLoaded;
        AnimateSectionOut(ContentStack);
        CancelActiveProgressOperationForNavigation();

        // One CTS owns both superseded-navigation cancellation and the 20s timeout.
        // This replaces the former watchdog CTS + linked section CTS pair on every click.
        long cancelStartMs = sw.ElapsedMilliseconds;
        var previousNavigationCts = _loadCts;
        _loadCts = null;
        if (previousNavigationCts != null)
        {
            Task cancellation = previousNavigationCts.CancelAsync();
            if (cancellation.IsCompleted)
            {
                previousNavigationCts.Dispose();
            }
            else
            {
                SafeTaskService.Forget(
                    DisposeNavigationCancellationAsync(previousNavigationCts, cancellation),
                    "navigation",
                    "Previous navigation cancellation cleanup");
            }
        }
        long cancelMs = sw.ElapsedMilliseconds - cancelStartMs;
        var navigationCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _loadCts = navigationCts;
        CancellationToken token = navigationCts.Token;

        try
        {
            long navSetupMs = sw.ElapsedMilliseconds;
            await ShowSectionCoreAsync(key, generation, token);
            long buildMs = sw.ElapsedMilliseconds;
            if (buildMs >= 5)
                LogService.Info("performance", $"NAV_WRAPPER_PHASE section={key} cancelMs={cancelMs} setupMs={navSetupMs} coreMs={buildMs - navSetupMs} buildMs={buildMs}");
            // The 20s timeout guards the foreground navigation build only. Deferred work for the
            // active section should stay alive until the next navigation cancels this CTS.
            navigationCts.CancelAfter(Timeout.InfiniteTimeSpan);
            if (generation == _navigationGeneration)
            {
                _initialSectionNavigationCompleted = true;
                _lastNavigationErrorId = null;
                _lastNavigationUtc = DateTime.UtcNow;
                long layoutStartMs = sw.ElapsedMilliseconds;
                EnsureVisibleSectionLayout(key,
                    originalNavigationHostReset && ReferenceEquals(originalNavigationHost, ContentStack));
                // Recover a missed first-paint GPU intro prewarm after returning to Overview.
                // Deferred window setup owns the initial attempt; never block first activation.
                if (key == "overview" && _deferredWindowSetupCompleted && _gpuStaticHero == null)
                    QueueGpuIntroPrewarm();
                long layoutMs = sw.ElapsedMilliseconds - layoutStartMs;

                // EnsureVisibleSectionLayout already restores the persistent ContentStack
                // composition state. A second queued visual reset is duplicate native work;
                // schedule only the coalesced scroll-policy pass after navigation completes.
                SchedulePageScrollPolicyRefresh();

                long totalMs = sw.ElapsedMilliseconds;
                // Diagnose the occasional 25ms strict System foreground gate
                // without sampling process counters or changing the hot path.
                // The known 29ms outlier had wrapper setup=13 and core=16ms,
                // coincident with unrelated Driver factory prewarm.
                if (key == "system" && totalMs >= 25)
                    LogService.Info("performance",
                        $"SYSTEM_NAV_SLOW totalMs={totalMs} setupMs={navSetupMs} cancelMs={cancelMs} coreMs={buildMs - navSetupMs} postCoreMs={layoutStartMs - buildMs} layoutMs={layoutMs}");
                if (key == "gpu" && totalMs >= 35)
                    LogService.Info("performance", $"GPU_NAV_WRAPPER_DIAG totalMs={totalMs} setupMs={navSetupMs} coreMs={buildMs - navSetupMs} postCoreMs={layoutStartMs - buildMs} layoutMs={layoutMs} tailMs={totalMs - layoutStartMs - layoutMs}");
                if (totalMs >= 20 && LogService.PerformanceDiagnosticsEnabled)
                {
                    // Reading working set, handles and GC statistics here used
                    // to inflate the actual foreground navigation time (including
                    // cold System render). Keep exact navigation timing and sample
                    // process counters on the LOW priority dispatcher after the
                    // first frame, never synchronously in the navigation path.
                    long measuredMs = totalMs;
                    long measuredCoreMs = buildMs - navSetupMs;
                    long measuredCancelMs = cancelMs;
                    long measuredLayoutMs = layoutMs;
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        if (generation != _navigationGeneration) return;
                        var memory = PerformanceTelemetryService.GetSnapshot();
                        LogService.Info("navigation", $"{key} ready ms={measuredMs} coreMs={measuredCoreMs} cancelMs={measuredCancelMs} layoutMs={measuredLayoutMs} wsMB={memory.WorkingSetMb} privateMB={memory.PrivateMb} managedMB={memory.ManagedMb} handles={memory.Handles} gc0={memory.Gen0} scrollPolicy=coalesced telemetry=deferred");
                    });
                }
            }
        }
        catch (OperationCanceledException) when (generation != _navigationGeneration)
        {
            if (sw.ElapsedMilliseconds >= 100)
                LogService.Info("navigation", $"{key} cancelled gen={generation} ms={sw.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException ex) when (generation == _navigationGeneration && token.IsCancellationRequested)
        {
            var timeout = new TimeoutException($"section:{key} překročil timeout 20s.", ex);
            LogService.Error("watchdog", $"section:{key} timed out", timeout);
            CrashFingerprintService.Record(timeout, "navigation", key);
            string id = ErrorService.NewCorrelationId(); SectionFailureService.Fail(key, id); _lastNavigationErrorId = id; _lastNavigationUtc = DateTime.UtcNow;
            Interlocked.Increment(ref _navigationFailureCount);
            LogService.Error("navigation", $"{key} failed id={id} gen={generation}", timeout);
            RenderSectionError(key, id, timeout, generation);
        }
        catch (Exception ex)
        {
            if (generation != _navigationGeneration)
            {
                LogService.Info("navigation",
                    $"{key} stale failure ignored gen={generation} currentGen={_navigationGeneration} type={ex.GetType().Name}");
                return;
            }

            await navigationCts.CancelAsync();

            CrashFingerprintService.Record(ex, "navigation", key);
            string id = ErrorService.NewCorrelationId();
            SectionFailureService.Fail(key, id);
            _lastNavigationErrorId = id;
            _lastNavigationUtc = DateTime.UtcNow;
            Interlocked.Increment(ref _navigationFailureCount);
            LogService.Error("navigation", $"{key} failed id={id} gen={generation}", ex);
            RenderSectionError(key, id, ex, generation);
        }
    }


    private void EnsureVisibleSectionLayout(string section, bool contentHostAlreadyReset = false)
    {
        if (_pageScroll == null || _contentLayerHost == null || RootGrid == null) return;

        // Persistent UI must never retain handoff-compositor opacity/scale state.
        // The start of this navigation already reset its original host. Only
        // skip a second reset if the same loaded host is still attached here.
        // Independent layout-repair callers use the default full reset.
        if (!contentHostAlreadyReset && ContentStack?.IsLoaded == true)
            ResetMotionVisual(ContentStack);
        if (_mainTabsShell?.IsLoaded == true) ResetMotionVisual(_mainTabsShell);
        if (HeaderCard?.IsLoaded == true) ResetMotionVisual(HeaderCard);
        // Selected/previous menu buttons have already been reset by UpdateCategorySelection.
        // All remaining persistent sidebar buttons never animate. Avoid 21 unnecessary
        // compositor StopAnimation/Scale/Opacity calls for each navigation.
        bool needsGeometryRepair =
            double.IsNaN(_pageScroll.Height) || _pageScroll.Height <= 1 ||
            _pageScroll.Visibility != Visibility.Visible ||
            _pageScroll.Opacity < 0.99 ||
            _contentLayerHost.Visibility != Visibility.Visible ||
            _contentLayerHost.Opacity < 0.99;

        if (!needsGeometryRepair) return;

        _pageScroll.Visibility = Visibility.Visible;
        _pageScroll.Opacity = 1;
        _contentLayerHost.Visibility = Visibility.Visible;
        _contentLayerHost.Opacity = 1;
        UpdateRowlessShellGeometry(RootGrid.ActualWidth, null, RootGrid.ActualHeight);
        LogService.Info("navigation",
            $"SECTION_LAYOUT_SELF_HEAL section={section} pageHeight={_pageScroll.Height:0.#} root={RootGrid.ActualWidth:0.#}x{RootGrid.ActualHeight:0.#}");
    }

    private async Task AssertSectionLayoutHealthyAsync(string section)
    {
        if (_pageScroll == null || _contentLayerHost == null || ContentStack == null)
            throw new InvalidOperationException($"{section}: chybí content host.");

        // Some pages (notably Startup/installed-app inventories) attach their first rows
        // asynchronously after navigation. Give the real content tree a short bounded window
        // to materialize, but still fail hard if it stays collapsed/0px.
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(i == 0 ? 35 : 25);
            EnsureVisibleSectionLayout(section);
            await Task.Yield();

            bool pageReady =
                _pageScroll.Visibility == Visibility.Visible && _pageScroll.Opacity >= 0.99 &&
                _pageScroll.ActualWidth >= 100 && _pageScroll.ActualHeight >= 100;
            bool layerReady =
                _contentLayerHost.Visibility == Visibility.Visible && _contentLayerHost.Opacity >= 0.99 &&
                _contentLayerHost.ActualWidth >= 100 && _contentLayerHost.ActualHeight >= 100;
            bool contentReady =
                ContentStack.IsLoaded && ContentStack.ActualWidth >= 100 && ContentStack.ActualHeight >= 1;
            bool sidebarReady =
                _mainTabsShell != null &&
                _mainTabsShell.Visibility == Visibility.Visible &&
                _mainTabsShell.Opacity >= 0.99 &&
                _mainTabsShell.ActualWidth >= 100 &&
                CategoryBar.Children.Count == MainSectionItems.Length &&
                _categoryButtons.Count == MainSectionItems.Length;

            if (pageReady && layerReady && contentReady && sidebarReady)
                return;
        }

        throw new InvalidOperationException(
            $"{section}: layout se ani po čekání nenačetl. " +
            $"page={_pageScroll.ActualWidth:0.#}x{_pageScroll.ActualHeight:0.#}/op={_pageScroll.Opacity:0.##}, " +
            $"layer={_contentLayerHost.ActualWidth:0.#}x{_contentLayerHost.ActualHeight:0.#}/op={_contentLayerHost.Opacity:0.##}, " +
            $"content={ContentStack.ActualWidth:0.#}x{ContentStack.ActualHeight:0.#}/loaded={ContentStack.IsLoaded}.");
    }

    private DispatcherTimer? _pageScrollSettleTimer;
    private EventHandler<object>? _pageScrollSettleTimerTickHandler;
    private long _lastPageScrollResizeTicks;
    private const int PageScrollResizeQuietMilliseconds = 110;

    private void SchedulePageScrollPolicyRefresh(bool fromResize = false)
    {
        if (_pageScroll == null || PageHost == null ||
            _windowLifetimeCts.IsCancellationRequested) return;

        // Both resize and page hydration can request a potentially expensive
        // three-pass Overview layout. During a real window drag, an explicit
        // navigation/hydration request must NOT bypass the resize quiet period
        // or block pointer input by synchronously invoking UpdateLayout.
        // Without a preceding resize, normal navigation runs on the next tick.
        if (fromResize)
            _lastPageScrollResizeTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        _pageScrollSettleTimer ??= CreatePageScrollSettleTimer();
        if (!_pageScrollSettleTimer.IsEnabled)
            _pageScrollSettleTimer.Start();
    }

    internal static bool PageScrollResizeRefreshDue(TimeSpan sinceLastResize) =>
        sinceLastResize.TotalMilliseconds >= PageScrollResizeQuietMilliseconds;

    private DispatcherTimer CreatePageScrollSettleTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _pageScrollSettleTimerTickHandler = (_, _) =>
        {
            if (_windowLifetimeCts.IsCancellationRequested)
            {
                timer.Stop();
                return;
            }
            TimeSpan sinceResize = _lastPageScrollResizeTicks == 0
                ? TimeSpan.MaxValue
                : System.Diagnostics.Stopwatch.GetElapsedTime(_lastPageScrollResizeTicks);
            if (!PageScrollResizeRefreshDue(sinceResize))
                return;
            timer.Stop();
            _lastPageScrollResizeTicks = 0;
            ApplyPageScrollPolicyNow();
        };
        timer.Tick += _pageScrollSettleTimerTickHandler;
        return timer;
    }

    private double MeasurePageContentExtentHeight()
    {
        if (ContentStack == null || PageHost == null) return 0;

        static double VisibleExtent(Panel panel)
        {
            double total = 0;
            foreach (UIElement child in panel.Children)
            {
                if (child is not FrameworkElement fe || fe.Visibility == Visibility.Collapsed)
                    continue;

                // ActualHeight is stable after layout; DesiredSize is the safer fallback during
                // deferred page hydration. Never use the parent StackPanel's stale stretched slot.
                double h = fe.ActualHeight > 0 ? fe.ActualHeight : fe.DesiredSize.Height;
                total += Math.Max(0, h);
                if (fe.Margin.Top > 0) total += fe.Margin.Top;
                if (fe.Margin.Bottom > 0) total += fe.Margin.Bottom;
            }
            return total;
        }

        double content = VisibleExtent(ContentStack);
        double actions = TopActions is { Visibility: not Visibility.Collapsed } ? VisibleExtent(TopActions) : 0;
        return PageHost.Padding.Top + PageHost.Padding.Bottom + content + actions;
    }

    private void ApplyPageScrollPolicyNow()
    {
        if (_pageScroll == null || PageHost == null) return;

        double viewport = _pageScroll.ViewportHeight;
        double contentHeight = MeasurePageContentExtentHeight();
        if (viewport <= 0 || contentHeight <= 0) return;

        // Overview must use the entire available page, not stop at the
        // former 752px layout. Fit only after all nine cards are hydrated.
        // No extra timer: reuse the existing coalesced scroll-policy pass.
        // WinUI may consume part of a size change inside minimum-height
        // cards on the first measure. Three bounded layout passes converge
        // without a per-frame timer or continuously polling the UI tree.
        int fitPasses = 0;
        long slowestLayoutMs = 0;
        var fitTiming = System.Diagnostics.Stopwatch.StartNew();
        for (int pass = 0; pass < 3 &&
             FitReferenceOverviewToViewport(viewport, contentHeight); pass++)
        {
            long beforeLayout = fitTiming.ElapsedMilliseconds;
            PageHost.UpdateLayout();
            slowestLayoutMs = Math.Max(slowestLayoutMs, fitTiming.ElapsedMilliseconds - beforeLayout);
            contentHeight = MeasurePageContentExtentHeight();
            fitPasses++;
        }
        long fitTotalMs = fitTiming.ElapsedMilliseconds;
        if (fitTotalMs >= 8 || slowestLayoutMs >= 8)
            LogService.Info("performance",
                $"PAGE_SCROLL_FIT section={_section} passes={fitPasses} totalMs={fitTotalMs} " +
                $"maxLayoutMs={slowestLayoutMs} viewport={viewport:0.#} content={contentHeight:0.#}");

        bool overflow = contentHeight > viewport + 1.0;
        ScrollMode target = overflow ? ScrollMode.Enabled : ScrollMode.Disabled;
        if (_pageScroll.VerticalScrollMode != target)
        {
            _pageScroll.VerticalScrollMode = target;
            LogService.Info("navigation",
                $"PAGE_SCROLL_POLICY section={_section} mode={target} viewport={viewport:0.##} content={contentHeight:0.##}");
        }
    }

    private bool TrySkipRedundantSameSectionNavigation(string key)
    {
        if (!string.Equals(key, _section, StringComparison.OrdinalIgnoreCase) || ContentStack.Children.Count == 0)
            return false;

        if (key == "overview")
            return _cpuGauge != null && _ramGauge != null && _gpuGauge != null && _diskGauge != null;

        // Runtime's interactive controls are intentionally NOT retained when
        // navigating away. While this exact page is already attached, however,
        // a repeat sidebar click should not discard it and re-read all status
        // files or recreate every WinUI toggle. Explicit Refresh forces rebuild.
        if (key == "ai-runtime" && _lastNavigationErrorId == null)
            return ContentStack.Children.OfType<StackPanel>().Any(host =>
                string.Equals(host.Tag as string, "AiRuntimeBodyHost", StringComparison.Ordinal));

        // A repeated Themes click with its current page already attached has no
        // useful work to perform. The 8s absolute cache expiry still handles
        // external wallpaper changes and forces a fresh page when required.
        if (key == "themes" && _themesPageCache is { Count: > 0 } &&
            ThemesCacheLifetimePolicy.IsFresh(_themesPageCacheAtUtc, DateTime.UtcNow, ThemesPageCacheTtl))
            return true;

        if (key == "network" && _networkPageCache != null)
        {
            DateTime now = DateTime.UtcNow;
            string tweakCacheKey = "network|";
            bool tweakFresh = _tweakCacheTime.TryGetValue(tweakCacheKey, out var detectedAt) &&
                              now - detectedAt <= TimeSpan.FromSeconds(90);
            bool panelsFresh = now - _networkCachedPanelsAt < TimeSpan.FromSeconds(20);
            return tweakFresh && panelsFresh;
        }

        if (key == "power" && _powerPageCacheAttached &&
            _powerPageCache is { Count: > 0 } &&
            DateTime.UtcNow - _powerPageCacheAt <= TimeSpan.FromSeconds(12))
            return true;

        // System pages already own live, refreshable page caches. Re-entering
        // the exact same section must be a true no-op; rebuilding/reattaching
        // the same WinUI tree repeatedly grows native wrapper high-water memory.
        if (key == "system" && _systemPageCache is { Count: > 0 } &&
            _systemVirtualRowsCache != null && _systemVirtualMapCache != null)
            return true;

        if (key == "ui" &&
            _uiPageCache is { Count: > 0 } &&
            _uiVirtualRowsCache is { Length: 86 } &&
            _uiVirtualMapCache != null &&
            DateTime.UtcNow - _uiPageCacheUsedAt <= TimeSpan.FromMinutes(3))
            return true;

        return false;
    }
}
