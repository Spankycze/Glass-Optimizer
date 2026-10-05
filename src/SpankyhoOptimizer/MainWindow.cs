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
    private readonly TweakEngine _engine = new TweakEngine();
    private readonly ConcurrentDictionary<string, List<TweakDefinition>> _tweakCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _tweakCacheTime = new();

    private List<TweakDefinition> _tweaks = new List<TweakDefinition>();

    private string _section = "overview";
    private TweakViewMode _tweakViewMode = TweakViewMode.Manual;

    private string? _sub;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _idleCacheTrimCts;
    private CancellationTokenSource? _upstreamUpdateCts;
    private int _startupUpstreamCheckStarted;
    private DateTime _lastNavigationUtc = DateTime.UtcNow;
    private string? _lastNavigationErrorId;
    private int _navigationFailureCount;
    internal int NavigationFailureCount => Volatile.Read(ref _navigationFailureCount);

    private int _navigationGeneration;
    private int _titlebarThemeGeneration;
    private int _dirtyRefreshQueued;
    private bool _deferredWindowSetupCompleted;
    private bool _startupTitlebarThemeCompleted;
    private bool _startupTitlebarNativeApplied;
    private IntPtr _windowHandle;

    private readonly Dictionary<string, string[]> _subOptions = new Dictionary<string, string[]>
    {
        ["browsers"] = new string[7] { "Firefox", "Chrome", "Edge", "Brave", "Opera", "Opera GX", "Vivaldi" }
    };
    private readonly Dictionary<string, Button[]> _subSelectorCache = new(StringComparer.OrdinalIgnoreCase);

    private Grid RootGrid;

    private Grid AppTitleBar;

    private StackPanel PageHost;

    private ScrollViewer _pageScroll;

    private StackPanel TopActions;

    private StackPanel ContentStack;
    private Grid _contentLayerHost;
    private readonly Queue<StackPanel> _detachedNavigationHosts = new();
    private readonly Dictionary<UIElement, StackPanel> _detachedNavigationOwners = new();
    private bool _detachedNavigationCleanupQueued;

    private Border ActionBar;

    private TextBlock DirtyText;

    private StackPanel SubSelectorHost;

    private TextBlock PageTitle;

    private TextBlock PageSubtitle;

    private TextBlock AdminBadge;
    private bool _initialSectionNavigationCompleted;

    private async Task CleanupPendingExostenzaAfterFirstRenderAsync()
    {
        await Task.Delay(250).ConfigureAwait(false);
        string? state = await GpuDriverService.ReconcilePendingExostenzaAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(state))
            LogService.Info("drivers", "EXOSTENZA_STARTUP_RECONCILE " + state);
    }

    public MainWindow()
    {
        var ctorPhase = Stopwatch.StartNew();
        Title = "Spankyho Optimizer";
        long themeStartMs = ctorPhase.ElapsedMilliseconds;
        _themePreference = AppThemeService.LoadPreference();
        long themePreferenceMs = ctorPhase.ElapsedMilliseconds - themeStartMs;
        _darkTheme = AppThemeService.ResolveDark(_themePreference);
        long themeDarkMs = ctorPhase.ElapsedMilliseconds - themeStartMs - themePreferenceMs;
        _themePalette = AppThemeEngine.Resolve(_themePreference, _darkTheme);
        long themePaletteMs = ctorPhase.ElapsedMilliseconds - themeStartMs - themePreferenceMs - themeDarkMs;
        ResetThemeBrushCaches();
        long themeResetMs = ctorPhase.ElapsedMilliseconds - themeStartMs - themePreferenceMs - themeDarkMs - themePaletteMs;
        long themeResourcesMs = 0;
        long themeMs = ctorPhase.ElapsedMilliseconds;
        StartupDiagnostics.BufferInfo($"THEME_STARTUP_PHASE preferenceMs={themePreferenceMs} darkMs={themeDarkMs} paletteMs={themePaletteMs} resetMs={themeResetMs} resourcesMs={themeResourcesMs} totalMs={themeMs-themeStartMs} deferredResources=true");
        // Normálně startujeme na Přehledu. Tray agent může požádat o přímé otevření konkrétní sekce.
        string? openSectionArg = Array.Find(
            Environment.GetCommandLineArgs(),
            x => x.StartsWith("--open-section=", StringComparison.OrdinalIgnoreCase));
        _section = openSectionArg != null
            ? openSectionArg.Substring("--open-section=".Length).Trim()
            : "overview";
        if (string.IsNullOrWhiteSpace(_section))
            _section = "overview";
        _sub = null;
        // Never delete large Exostenza payloads on the cold-start UI path.
        // Pending cleanup is scheduled after first render and runs on a worker thread.
        BuildShell();
        long shellMs = ctorPhase.ElapsedMilliseconds;
        ConfigureWindow();
        long windowMs = ctorPhase.ElapsedMilliseconds;
        StartupDiagnostics.BufferInfo($"MAINWINDOW_CTOR themeMs={themeMs} shellMs={shellMs - themeMs} windowMs={windowMs - shellMs} totalMs={windowMs}");
Closed += (_, _) => { UiStateService.Save(new UiState(_section, _sub, "Manual")); CleanupWindowResources(); };
        Activated += (_, e) =>
        {
            _windowTelemetryActive = e.WindowActivationState != WindowActivationState.Deactivated;
        };
        RootGrid.Loaded += (_, _) =>
        {
            // Finalize rowless geometry with the real loaded size. The first SizeChanged can
            // arrive before ActualHeight is committed, which previously left Overview at 0 px.
            UpdateRowlessShellGeometry(RootGrid.ActualWidth, null, RootGrid.ActualHeight);
            // Theme resource mutation is intentionally deferred until after first paint.
            SafeTaskService.ForgetDeferred(ApplyStartupThemeResourcesAfterFirstRenderAsync(), "performance", "Deferred startup theme resources");
            // Warm cheap backend metadata/probes away from the first Overview snapshot.
            PersistentHardwareCacheService.QueueJsonMetadataWarmup();
            SystemInfoService.QueueNetworkSummaryWarmup();
            // Non-Overview section factories/catalogs are intentionally on-demand. Warming them at
            // startup wasted CPU/RAM and competed with the first usable frame.
            // Custom titlebar geometry is reserved before first paint in ConfigureWindow to avoid a visible header jump.
            // Child content is attached here after activation so WinUI cold-init does not block startup.
            // Caption colors are applied by ActivateCustomTitleBarNow; do not schedule a second
            // AppWindow.TitleBar pass a few hundred milliseconds later.
            // Do not build custom titlebar children inside Window.Activate()/Loaded.
            // Geometry is already reserved, so a next-tick attach cannot move the header.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, ActivateCustomTitleBarNow);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                CompleteDeferredShellBackdrop();
                CompleteDeferredHero();
                SafeTaskService.Forget(ScheduleDeferredSidebarAfterFirstRenderAsync(), "performance", "Deferred sidebar");
            });
            SafeTaskService.ForgetDeferred(ScheduleDeferredWindowSetupAfterFirstRenderAsync(), "performance", "Deferred window setup");
            SafeTaskService.Forget(ShowSectionAsync(_section), "navigation", "Initial section load");
            // Start latency-critical prewarm only after Loaded/Window.Activate returns. Some async
            // methods do useful work before their first await, so starting them inline regresses startup.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                SafeTaskService.ForgetDeferred(
                    RunStagedPostRenderPrewarmAsync(),
                    "performance",
                    "Staged post-render prewarm");
            });
            SafeTaskService.ForgetDeferred(StartLiveMetricsAfterFirstRenderAsync(), "performance", "Deferred live metrics");
            SafeTaskService.ForgetDeferred(CheckUpstreamUpdatesOnStartupAsync(), "upstream", "Startup GitHub update check");
            SafeTaskService.ForgetDeferred(RunCursorStartupWatchdogAsync(), "cursor", "Cursor startup watchdog");
            if (GpuDriverService.HasPendingExostenzaCleanup)
                SafeTaskService.ForgetDeferred(CleanupPendingExostenzaAfterFirstRenderAsync(), "drivers", "Deferred Exostenza cleanup");
            // Keep startup lean: section-specific UI/backend warmups now happen when that section opens.
            // Only the active Overview telemetry and the idle cache trimmer stay alive after first paint.
            SafeTaskService.ForgetDeferred(RunIdleCacheTrimLoopAsync(), "performance", "Idle UI cache trim");
        };
    }

    private void ConfigureWindow()
    {
        var phase = Stopwatch.StartNew();
        _windowHandle = WindowNative.GetWindowHandle(this);
        AppWindow fromWindowId = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        long getWindowMs = phase.ElapsedMilliseconds;
        fromWindowId.Resize(new SizeInt32(1320, 860));
        long resizeOnlyMs = phase.ElapsedMilliseconds;
        long presenterGetMs = resizeOnlyMs;
        long presenterMinimumMs = resizeOnlyMs;
        long presenterFlagsMs = resizeOnlyMs;
        if (fromWindowId.Presenter is OverlappedPresenter startupPresenter)
        {
            presenterGetMs = phase.ElapsedMilliseconds;
            presenterMinimumMs = presenterGetMs;
            startupPresenter.IsMinimizable = true;
            startupPresenter.IsMaximizable = true;
            startupPresenter.IsResizable = true;
            presenterFlagsMs = phase.ElapsedMilliseconds;
        }
        long resizeMs = phase.ElapsedMilliseconds;

        // Keep startup layout native/stable and attach the custom drag region immediately after
        // the first render. Calling SetTitleBar before activation can break the initial content layout.
        var startupTitleBrush = BuildCustomTitleBarBackground();
        long titleBrushBuildMs = phase.ElapsedMilliseconds - resizeMs;
        AppTitleBar.Background = startupTitleBrush;
        long titleBrushSetMs = phase.ElapsedMilliseconds - resizeMs - titleBrushBuildMs;

        // PERF/UI INVARIANT: visible titlebar content is first-frame UI and must never be deferred.
        // Performance audits may defer DWM/caption styling, but NEVER the brand/title children.
        PopulateCustomTitleBarContent();
        _titleContentBuiltSynchronously = AppTitleBar.Children.Count > 0;
        long titleContentMs = phase.ElapsedMilliseconds - resizeMs - titleBrushBuildMs - titleBrushSetMs;

        // Keep native caption extension deferred until after Window.Activate. The custom
        // 36px titlebar geometry is already reserved so the shell itself does not relayout.

        // Keep the 36px titlebar geometry visible from the first frame.
        // SetTitleBar/caption colors can wait for activation; visible title children cannot.
        AppTitleBar.Visibility = Visibility.Visible;
        HeaderCard.Margin = new Thickness(0);

        // First-frame invariant: visible custom titlebar content and its 36px geometry are
        // already present. The expensive native drag/caption attachment is intentionally queued
        // from RootGrid.Loaded, after Window.Activate returns, so WinUI cold initialization does
        // not block first activation and the header geometry still cannot jump.
        long titleTailMs = phase.ElapsedMilliseconds - resizeMs - titleBrushBuildMs - titleBrushSetMs;
        long titlebarMs = phase.ElapsedMilliseconds;

        StartupDiagnostics.BufferInfo(
            $"CONFIGURE_WINDOW getMs={getWindowMs} resizeOnlyMs={resizeOnlyMs - getWindowMs} presenterGetMs={presenterGetMs - resizeOnlyMs} presenterMinimumMs={presenterMinimumMs - presenterGetMs} presenterFlagsMs={presenterFlagsMs - presenterMinimumMs} resizeMs={resizeMs - getWindowMs} titleBrushBuildMs={titleBrushBuildMs} titleBrushSetMs={titleBrushSetMs} titleContentMs={titleContentMs} titleTailMs={titleTailMs} titlebarMs={titlebarMs - resizeMs} deferredWindowExtras=true totalMs={titlebarMs}");
    }

    private async Task ScheduleDeferredWindowSetupAfterFirstRenderAsync()
    {
        await Task.Delay(650).ConfigureAwait(false);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CompleteDeferredWindowSetup);
    }

    private void CompleteDeferredWindowSetup()
    {
        if (_deferredWindowSetupCompleted) return;
        _deferredWindowSetupCompleted = true;
        var phase = Stopwatch.StartNew();
        try
        {
            AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
            long getMs = phase.ElapsedMilliseconds;
            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
            long iconMs = phase.ElapsedMilliseconds;
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = 980;
                presenter.PreferredMinimumHeight = 680;
            }
            long presenterMs = phase.ElapsedMilliseconds;
            LogService.Info("performance", $"WINDOW_SETUP_DEFERRED getMs={getMs} iconMs={iconMs - getMs} presenterMs={presenterMs - iconMs} totalMs={presenterMs}");
        }
        catch (Exception ex)
        {
            _deferredWindowSetupCompleted = false;
            LogService.Error("window", "Deferred window setup failed", ex);
        }
    }

    internal async Task RunDeferredWindowSetupSmokeAsync()
    {
        for (int i = 0; i < 20 && !_deferredWindowSetupCompleted; i++)
            await Task.Delay(25);
        if (!_deferredWindowSetupCompleted)
            throw new InvalidOperationException("Deferred window setup did not complete.");

        AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
        if (appWindow.Presenter is not OverlappedPresenter presenter ||
            presenter.PreferredMinimumWidth != 980 || presenter.PreferredMinimumHeight != 680 ||
            !presenter.IsMinimizable || !presenter.IsMaximizable || !presenter.IsResizable)
            throw new InvalidOperationException("Deferred window presenter state mismatch.");

        LogService.Info("WINDOW_SETUP_DEFERRED_SMOKE_OK");
    }

    private async Task ScheduleStartupTitleBarThemeAsync()
    {
        await Task.Delay(300).ConfigureAwait(false);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ApplyWindowTitleBarTheme();
            Volatile.Write(ref _startupTitlebarThemeCompleted, true);
            LogService.Info("performance", "TITLEBAR_CUSTOM_THEME_STAGE transparentCaption=true");
        });
    }

    internal async Task RunDeferredTitlebarSmokeAsync()
    {
        // PERF/UI REGRESSION GUARD: brand/title content must exist before deferred caption work.
        if (!_titleContentBuiltSynchronously || AppTitleBar.Children.Count == 0)
            throw new InvalidOperationException("Titlebar visible content was deferred; first-frame title invariant broken.");

        for (int i = 0; i < 40 && !_startupTitlebarThemeCompleted; i++)
            await Task.Delay(25);
        if (!_startupTitlebarThemeCompleted)
            throw new InvalidOperationException("Startup titlebar theme did not complete.");
        if (_startupTitlebarNativeApplied)
            throw new InvalidOperationException("Opaque native DWM titlebar path unexpectedly replaced the custom caption.");
        if (!_customTitleBarPrimed)
            throw new InvalidOperationException("Custom titlebar was not primed after activation.");
        if (AppTitleBar.Visibility != Visibility.Visible || AppTitleBar.Children.Count == 0)
            throw new InvalidOperationException("Custom titlebar content is missing.");

        double headerHeight = HeaderCard.Height;
        double pageTop = _pageScroll?.Margin.Top ?? -1;
        double rootWidth = RootGrid.ActualWidth;
        double rootHeight = RootGrid.ActualHeight;
        await Task.Delay(120);
        double afterHeaderHeight = HeaderCard.Height;
        double afterPageTop = _pageScroll?.Margin.Top ?? -1;
        double afterRootWidth = RootGrid.ActualWidth;
        double afterRootHeight = RootGrid.ActualHeight;
        if (Math.Abs(afterHeaderHeight - headerHeight) > 0.5 ||
            Math.Abs(afterPageTop - pageTop) > 0.5)
        {
            LogService.Info("performance",
                $"TITLEBAR_GEOMETRY_DRIFT header={headerHeight:0.##}->{afterHeaderHeight:0.##} pageTop={pageTop:0.##}->{afterPageTop:0.##} root={rootWidth:0.##}x{rootHeight:0.##}->{afterRootWidth:0.##}x{afterRootHeight:0.##}");
            throw new InvalidOperationException("Titlebar/header geometry changed after first render.");
        }

        if (_heroImageFrame == null ||
            Math.Abs(_heroImageFrame.Margin.Top) > 0.1 ||
            Math.Abs(_heroImageFrame.Height - HeaderCard.Height) > 0.5)
            throw new InvalidOperationException(
                $"Hero/titlebar seam invariant broken: heroTop={_heroImageFrame?.Margin.Top ?? -1:0.##} heroH={_heroImageFrame?.Height ?? -1:0.##} headerH={HeaderCard.Height:0.##}.");

        LogService.Info("TITLEBAR_STABLE_SMOKE_OK customTransparent=true syncActivation=true stableGeometry=true heroUnderTitlebar=true");
    }

    private void ApplyWindowTitleBarTheme()
    {
        _ = Interlocked.Increment(ref _titlebarThemeGeneration);
        var sw = Stopwatch.StartNew();
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            var tb = appWindow.TitleBar;
            var transparent = ColorHelper.FromArgb(0, 0, 0, 0);
            var titleFg = ColorHelper.FromArgb(255, _themePalette.TextPrimary.R, _themePalette.TextPrimary.G, _themePalette.TextPrimary.B);
            var inactiveFg = ColorHelper.FromArgb(255, _themePalette.TextSecondary.R, _themePalette.TextSecondary.G, _themePalette.TextSecondary.B);
            var hoverBg = ColorHelper.FromArgb(48, _themePalette.SurfaceElevated.R, _themePalette.SurfaceElevated.G, _themePalette.SurfaceElevated.B);
            var pressedBg = ColorHelper.FromArgb(76, _themePalette.Border.R, _themePalette.Border.G, _themePalette.Border.B);

            tb.ExtendsContentIntoTitleBar = true;
            tb.BackgroundColor = transparent;
            tb.ForegroundColor = titleFg;
            tb.ButtonBackgroundColor = transparent;
            tb.ButtonInactiveBackgroundColor = transparent;
            tb.ButtonForegroundColor = titleFg;
            tb.ButtonInactiveForegroundColor = inactiveFg;
            tb.ButtonHoverBackgroundColor = hoverBg;
            tb.ButtonHoverForegroundColor = titleFg;
            tb.ButtonPressedBackgroundColor = pressedBg;
            tb.ButtonPressedForegroundColor = titleFg;
            tb.InactiveBackgroundColor = transparent;
            tb.InactiveForegroundColor = inactiveFg;

            ApplyNativeGlassWindowShape();
            _startupTitlebarNativeApplied = false;
            _startupTitlebarThemeCompleted = true;
            LogService.Info("performance", $"TITLEBAR_CUSTOM_THEME_APPLY transparent=true totalMs={sw.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            LogService.Error("theme", "Custom titlebar styling failed", ex);
        }
    }


    private Task NavigateMainSectionFromUiAsync(string section)
    {
        if (string.Equals(_section, section, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        return ShowSectionAsync(section);
    }

    private Task NavigateSubSectionFromUiAsync(string section, string sub)
    {
        if (string.Equals(_section, section, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_sub, sub, StringComparison.Ordinal))
            return Task.CompletedTask;

        _sub = sub;
        return ShowSectionAsync(section);
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

    private async Task ShowSectionAsync(string key)
    {
        if (string.Equals(key, "components", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "services", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "drivers", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "tasks", StringComparison.OrdinalIgnoreCase))
        {
            key = "ai-runtime";
        }

        if (TrySkipRedundantSameSectionNavigation(key))
        {
            _lastNavigationErrorId = null;
            _lastNavigationUtc = DateTime.UtcNow;
            SchedulePageScrollPolicyRefresh();
            return;
        }

        int generation = Interlocked.Increment(ref _navigationGeneration);
        var sw = Stopwatch.StartNew();

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
                EnsureVisibleSectionLayout(key);

                // Visual polish is intentionally deferred one low-priority dispatcher turn.
                // Composition animation + the first scroll-policy timer must not extend the
                // latency-critical navigation await or cold-render benchmark.
                int visualGeneration = generation;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (visualGeneration != _navigationGeneration) return;
                    AnimateSectionIn(ContentStack);
                    SchedulePageScrollPolicyRefresh();
                });

                long totalMs = sw.ElapsedMilliseconds;
                if (totalMs >= 20)
                    LogService.Info("navigation", $"{key} ready ms={totalMs} buildMs={buildMs} visualTail=deferred");
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
            await navigationCts.CancelAsync();

            if (generation != _navigationGeneration)
            {
                LogService.Info("navigation",
                    $"{key} stale failure ignored gen={generation} currentGen={_navigationGeneration} type={ex.GetType().Name}");
                return;
            }

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


    private void EnsureVisibleSectionLayout(string section)
    {
        if (_pageScroll == null || _contentLayerHost == null || RootGrid == null) return;

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

            if (pageReady && layerReady && contentReady)
                return;
        }

        throw new InvalidOperationException(
            $"{section}: layout se ani po čekání nenačetl. " +
            $"page={_pageScroll.ActualWidth:0.#}x{_pageScroll.ActualHeight:0.#}/op={_pageScroll.Opacity:0.##}, " +
            $"layer={_contentLayerHost.ActualWidth:0.#}x{_contentLayerHost.ActualHeight:0.#}/op={_contentLayerHost.Opacity:0.##}, " +
            $"content={ContentStack.ActualWidth:0.#}x{ContentStack.ActualHeight:0.#}/loaded={ContentStack.IsLoaded}.");
    }

    private int _pageScrollPolicyQueued;
    private int _pageScrollPolicyDirty;
    private DispatcherTimer? _pageScrollSettleTimer;

    private void SchedulePageScrollPolicyRefresh()
    {
        if (_pageScroll == null || PageHost == null) return;

        Interlocked.Exchange(ref _pageScrollPolicyDirty, 1);
        if (Interlocked.Exchange(ref _pageScrollPolicyQueued, 1) == 0)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                Interlocked.Exchange(ref _pageScrollPolicyDirty, 0);
                ApplyPageScrollPolicyNow();

                Interlocked.Exchange(ref _pageScrollPolicyQueued, 0);
                if (Volatile.Read(ref _pageScrollPolicyDirty) != 0)
                    SchedulePageScrollPolicyRefresh();
            });
        }

        _pageScrollSettleTimer ??= CreatePageScrollSettleTimer();
        _pageScrollSettleTimer.Stop();
        _pageScrollSettleTimer.Start();
    }

    private DispatcherTimer CreatePageScrollSettleTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplyPageScrollPolicyNow();
        };
        return timer;
    }

    private void ApplyPageScrollPolicyNow()
    {
        if (_pageScroll == null || PageHost == null) return;

        double viewport = _pageScroll.ViewportHeight;
        double contentHeight = PageHost.ActualHeight;
        if (viewport <= 0 || contentHeight <= 0) return;

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

        // System and browser pages already own live, refreshable page caches. Re-entering
        // the exact same section/subsection must be a true no-op; rebuilding/reattaching
        // the same WinUI tree repeatedly grows native wrapper high-water memory.
        if (key == "system" && _systemPageCache is { Count: > 0 } &&
            _systemVirtualRowsCache != null && _systemVirtualMapCache != null)
            return true;

        if (key == "browsers")
        {
            string browserSub = _sub ?? "Firefox";
            if (_browserPageCachesStore != null &&
                _browserPageCachesStore.ContainsKey(browserSub) &&
                _browserVirtualRowsCacheStore != null &&
                _browserVirtualRowsCacheStore.ContainsKey(browserSub) &&
                _browserVirtualMapCacheStore != null &&
                _browserVirtualMapCacheStore.ContainsKey(browserSub))
                return true;
        }

        if (key == "ui" &&
            _uiPageCache is { Count: > 0 } &&
            _uiVirtualRowsCache is { Length: 86 } &&
            _uiVirtualMapCache != null &&
            DateTime.UtcNow - _uiPageCacheUsedAt <= TimeSpan.FromMinutes(3))
            return true;

        return false;
    }
    private void ThrowIfNavigationStale(int generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration)
            throw new OperationCanceledException(token);
    }

    private void AttachCachedElement(UIElement element)
    {
        DependencyObject? currentParent = VisualTreeHelper.GetParent(element);
        if (ReferenceEquals(currentParent, ContentStack))
            return;

        if (currentParent is Panel parent)
        {
            parent.Children.Remove(element);
        }
        else
        {
            // Detached cached elements are indexed by their previous host so reattach stays O(1).
            if (_detachedNavigationOwners.TryGetValue(element, out var detachedHost))
            {
                detachedHost.Children.Remove(element);
                _detachedNavigationOwners.Remove(element);
            }
            else
            {
                // Fallback for legacy/unindexed hosts.
                foreach (var legacyHost in _detachedNavigationHosts)
                {
                    if (!legacyHost.Children.Contains(element))
                        continue;

                    legacyHost.Children.Remove(element);
                    break;
                }
            }
        }

        ContentStack.Children.Add(element);
    }

    private void QueueDetachedNavigationHostCleanup(StackPanel host)
    {
        if (host.Children.Count == 0)
            return;

        foreach (UIElement child in host.Children)
            _detachedNavigationOwners[child] = host;

        _detachedNavigationHosts.Enqueue(host);
        ScheduleDetachedNavigationHostCleanup();
    }

    private void ScheduleDetachedNavigationHostCleanup()
    {
        if (_detachedNavigationCleanupQueued || _detachedNavigationHosts.Count == 0)
            return;

        _detachedNavigationCleanupQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _detachedNavigationCleanupQueued = false;
            if (_detachedNavigationHosts.Count == 0)
                return;

            var host = _detachedNavigationHosts.Peek();
            if (host.Children.Count > 0)
            {
                var child = host.Children[host.Children.Count - 1];
                _detachedNavigationOwners.Remove(child);
                host.Children.RemoveAt(host.Children.Count - 1);
            }

            if (host.Children.Count == 0)
            {
                _detachedNavigationHosts.Dequeue();
                if (_contentLayerHost.Children.Contains(host))
                    _contentLayerHost.Children.Remove(host);
            }

            ScheduleDetachedNavigationHostCleanup();
        });
    }

    private void ClearNavigationHostsFast()
    {
        if (ContentStack.Children.Count > 0)
        {
            var oldContent = ContentStack;
            oldContent.IsHitTestVisible = false;
            oldContent.Opacity = 0;

            var replacement = new StackPanel { Spacing = 0 };
            _contentLayerHost.Children.Add(replacement);
            ContentStack = replacement;
            QueueDetachedNavigationHostCleanup(oldContent);
        }

        if (TopActions.Children.Count > 0)
            TopActions.Children.Clear();
        if (SubSelectorHost.Children.Count > 0)
            SubSelectorHost.Children.Clear();
        if (SubSelectorHost.Visibility != Visibility.Collapsed)
            SubSelectorHost.Visibility = Visibility.Collapsed;
    }

    private async Task ShowSectionCoreAsync(string key, int generation, CancellationToken token)
    {
        var navPhase = Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration) throw new OperationCanceledException(token);
        CleanupForNavigation(key);
        long cleanupMs = navPhase.ElapsedMilliseconds;
        _section = key;
        _themeSection = key;
        UpdateGlassShell();
        bool overviewNoScroll = string.Equals(key, "overview", StringComparison.OrdinalIgnoreCase) &&
            (RootGrid.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth) >= 1200 &&
            (RootGrid.ActualHeight > 1 ? RootGrid.ActualHeight : 860) >= 790;
        if (!overviewNoScroll && _pageScroll.VerticalScrollMode != ScrollMode.Enabled)
            _pageScroll.VerticalScrollMode = ScrollMode.Enabled;
        if (overviewNoScroll)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (generation != _navigationGeneration || _section != "overview") return;
                if (Math.Abs(_pageScroll.VerticalOffset) > 0.1)
                    _pageScroll.ChangeView(null, 0, null, true);
                if (_pageScroll.VerticalScrollMode != ScrollMode.Disabled)
                    _pageScroll.VerticalScrollMode = ScrollMode.Disabled;
            });
        }
        if (ActionBar.Visibility != Visibility.Collapsed)
            ActionBar.Visibility = Visibility.Collapsed;
        _powerPageCacheAttached = false;
        long stateMs = navPhase.ElapsedMilliseconds;
        ClearNavigationHostsFast();
        long clearMs = navPhase.ElapsedMilliseconds;
        SectionDefinition sectionDefinition = CatalogService.GetSection(key);
        long catalogMs = navPhase.ElapsedMilliseconds;
        UpdateVisualSelection(key, sectionDefinition.IconGlyph);
        long selectionMs = navPhase.ElapsedMilliseconds;
        Visibility targetHeroVisibility = key == "overview" ? Visibility.Visible : Visibility.Collapsed;
        if (_heroDeferredCompleted && HeroTextPanel.Visibility != targetHeroVisibility)
            HeroTextPanel.Visibility = targetHeroVisibility;
        long heroMs = navPhase.ElapsedMilliseconds;
        double targetHeaderHeight = CalculateSectionHeroHeight(
            key,
            RootGrid.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth);
        if (Math.Abs(HeaderCard.Height - targetHeaderHeight) > 0.1)
            UpdateRowlessShellGeometry(heroHeightOverride: targetHeaderHeight);
        long headerSizeMs = navPhase.ElapsedMilliseconds;
        string heroTitle = key == "overview" ? "P\u0159ehled" : sectionDefinition.Title;
        string heroSubtitle = key == "overview"
            ? "Hardware, \u017eiv\u00e9 vyu\u017eit\u00ed a stav syst\u00e9mu"
            : sectionDefinition.Subtitle;
        if (_heroDeferredCompleted)
        {
            if (!string.Equals(PageTitle.Text, heroTitle, StringComparison.Ordinal))
                PageTitle.Text = heroTitle;
            if (!string.Equals(PageSubtitle.Text, heroSubtitle, StringComparison.Ordinal))
                PageSubtitle.Text = heroSubtitle;
        }
        long shellMs = navPhase.ElapsedMilliseconds;
        long shellOnlyMs = shellMs - cleanupMs;
        // Keep detailed shell telemetry for genuinely slow navigation only.
        // System used to log every navigation (including 0 ms hot-path soaks), creating
        // avoidable queue/string/disk pressure exactly while measuring navigation.
        if (cleanupMs >= 15 || shellOnlyMs >= 15)
        {
            LogService.Info("performance", $"NAV_PHASE section={key} cleanupMs={cleanupMs} shellMs={shellOnlyMs}");
            LogService.Info("performance",
                $"NAV_SHELL_PHASE section={key} stateMs={stateMs - cleanupMs} clearMs={clearMs - stateMs} catalogMs={catalogMs - clearMs} selectionMs={selectionMs - catalogMs} heroMs={heroMs - selectionMs} headerSizeMs={headerSizeMs - heroMs} textMs={shellMs - headerSizeMs}");
        }
        if (CrashLoopGuardService.SafeMode && !IsSafeModeAllowedSection(key))
        {
            BuildCrashSafeModePage();
            return;
        }
        long subSelectorStartMs = navPhase.ElapsedMilliseconds;
        if (_subOptions.TryGetValue(key, out string[] value))
        {
            if (_sub == null)
            {
                _sub = value[0];
            }
            if (!value.Contains<string>(_sub))
            {
                _sub = value[0];
            }
            BuildSubSelector(key, value);
        }
        else
        {
            _sub = null;
        }
        long subSelectorMs = navPhase.ElapsedMilliseconds - subSelectorStartMs;
        if (subSelectorMs >= 5)
            LogService.Info("performance", $"NAV_SUBSELECTOR_PHASE section={key} sub={_sub ?? "-"} ms={subSelectorMs}");
        if (key is "overview" or "components" or "gpu" or "network" or "storage" or "startup" or "defender")
        {
            long preludeTotalMs = navPhase.ElapsedMilliseconds;
            if (preludeTotalMs >= 5)
                LogService.Info("performance",
                    $"NAV_PRELUDE_PHASE section={key} cleanupMs={cleanupMs} stateMs={stateMs - cleanupMs} clearMs={clearMs - stateMs} catalogMs={catalogMs - clearMs} selectionMs={selectionMs - catalogMs} heroMs={heroMs - selectionMs} headerMs={headerSizeMs - heroMs} textMs={shellMs - headerSizeMs} subMs={subSelectorMs} totalMs={preludeTotalMs}");
        }
        if (key == "overview")
        {
            await BuildOverviewDashboardAsync(token);
            return;
        }
        if (key == "power")
        {
            await BuildPowerPlanAsync(token);
            return;
        }
        if (key == "debloat")
        {
            if (TryAttachDebloatPageCache()) return;
            await BuildDebloatPageAsync(token);
            ThrowIfNavigationStale(generation, token);
            CaptureDebloatPageCache();
            return;
        }
        if (key == "apps")
        {
            var appsForeground = Stopwatch.StartNew();
            await BuildAppsPageAsync(token);
            LogService.Info("performance", $"APPS_NAV_PHASE totalMs={appsForeground.ElapsedMilliseconds}");
            return;
        }

        if (key == "fixer")
        {
            await BuildFixerPageAsync(token);
            return;
        }
        if (key == "restore")
        {
            await BuildRestorePageAsync(token);
            return;
        }
        if (key == "logs")
        {
            BuildLogsPageAsync(token);
            return;
        }
        if (key == "extensions")
        {
            await BuildExtensionsPageAsync(token);
            return;
        }
        if (key == "spicetify")
        {
            await BuildSpicetifyPageAsync(token);
            return;
        }
        if (key == "powershell")
        {
            await BuildPowerShellTweakerPageAsync(token);
            return;
        }
        if (key == "cursor")
        {
            BuildCursorPage();
            return;
        }
        if (key == "settings")
        {
            BuildSettingsPage();
            return;
        }
        if (key == "startup")
        {
            var startupNavPerf = Stopwatch.StartNew();
            if (TryAttachStartupPageCache())
            {
                LogService.Info("performance", $"STARTUP_NAV_PHASE cacheHit=true totalMs={startupNavPerf.ElapsedMilliseconds}");
                return;
            }
            long cacheMs = startupNavPerf.ElapsedMilliseconds;
            await BuildStartupManagerAsync(token);
            ThrowIfNavigationStale(generation, token);
            long managerMs = startupNavPerf.ElapsedMilliseconds - cacheMs;
            int startupGeneration = generation;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || startupGeneration != _navigationGeneration || _section != "startup") return;
                BuildWindowsModernizerStartupPanel(token);
                CaptureStartupPageCache();
            });
            long modernizerMs = startupNavPerf.ElapsedMilliseconds - cacheMs - managerMs;
            CaptureStartupPageCache();
            long captureMs = startupNavPerf.ElapsedMilliseconds - cacheMs - managerMs - modernizerMs;
            LogService.Info("performance", $"STARTUP_NAV_PHASE cacheHit=false cacheMs={cacheMs} managerMs={managerMs} modernizerMs={modernizerMs} captureMs={captureMs} totalMs={startupNavPerf.ElapsedMilliseconds}");
            return;
        }
        if (key == "usb")
        {
            await BuildUsbToolsAsync(token);
            return;
        }
        if (key == "lianli")
        {
            await BuildLianLiPageAsync(token);
            return;
        }
        if (key == "activity")
        {
            BuildActivityPage();
            return;
        }
        if (key == "components")
        {
            var componentPerf = Stopwatch.StartNew();
            string componentSub = _sub ?? "Služby";
            string componentCacheKey = "components|" + componentSub;
            if (!_tweakCache.TryGetValue(componentCacheKey, out _tweaks))
                _tweakCache[componentCacheKey] = _tweaks = CatalogService.GetTweaks("components", componentSub);
            long catalogPhaseMs = componentPerf.ElapsedMilliseconds;

            if (TryAttachComponentsPageCache(componentSub, token))
            {
                LogService.Info("performance", $"COMPONENT_NAV_PHASE sub={componentSub} cacheHit=true catalogMs={catalogPhaseMs} totalMs={componentPerf.ElapsedMilliseconds}");
                return;
            }
            long cachePhaseMs = componentPerf.ElapsedMilliseconds - catalogPhaseMs;
            BuildComponentsContextActions();
            long contextPhaseMs = componentPerf.ElapsedMilliseconds - catalogPhaseMs - cachePhaseMs;
            long buildStart = componentPerf.ElapsedMilliseconds;
            if (componentSub == "Ovladače")
                await BuildDriverManagerPageAsync(token);
            else if (componentSub == "Task Scheduler")
                await BuildTaskManagerPageAsync(token);
            else
            {
                await BuildServiceManagerPageAsync(token);
                ThrowIfNavigationStale(generation, token);
                BuildWindowsModernizerVendorPanel(token);
            }
            ThrowIfNavigationStale(generation, token);
            long buildPhaseMs = componentPerf.ElapsedMilliseconds - buildStart;

            long captureStart = componentPerf.ElapsedMilliseconds;
            CaptureComponentsPageCache(componentSub);
            long capturePhaseMs = componentPerf.ElapsedMilliseconds - captureStart;
            LogService.Info("performance",
                $"COMPONENT_NAV_PHASE sub={componentSub} cacheHit=false catalogMs={catalogPhaseMs} cacheMs={cachePhaseMs} contextMs={contextPhaseMs} buildMs={buildPhaseMs} captureMs={capturePhaseMs} totalMs={componentPerf.ElapsedMilliseconds}");
            return;
        }
        if (key == "ai") { await BuildAiPageAsync(token); return; }
        if (key == "ai-runtime")
        {
            if (TryAttachAiRuntimePageCache()) return;
            await BuildAiSystemRuntimePageAsync(token);
            CaptureAiRuntimePageCache();
            return;
        }
        if (key == "defender") { await BuildDefenderPageAsync(token); return; }
        Stopwatch? systemForeground = key == "system" ? Stopwatch.StartNew() : null;
        Stopwatch? storageCatalogPhase = key == "storage" ? Stopwatch.StartNew() : null;
        string tweakCacheKey = key + "|" + (_sub ?? "");
        if (!_tweakCache.TryGetValue(tweakCacheKey, out var cachedTweaks))
        {
            cachedTweaks = CatalogService.GetTweaks(key, _sub);
            _tweakCache[tweakCacheKey] = cachedTweaks;
        }
        _tweaks = key == "system" ? GlassSystemTweaks(cachedTweaks) : cachedTweaks;
        long systemCatalogMs = systemForeground?.ElapsedMilliseconds ?? 0;
        long storageCatalogMs = storageCatalogPhase?.ElapsedMilliseconds ?? 0;
        if (key == "system" && TryAttachSystemPageCache(token))
        {
            bool systemStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var systemDetectedAt) ||
                DateTime.UtcNow - systemDetectedAt > TimeSpan.FromSeconds(90);
            if (systemStale)
                ScheduleRenderFirstBackground("system", tweakCacheKey, _tweaks.ToArray(), true, token);
            return;
        }
        if (key == "privacy" && TryAttachPrivacyPageCache())
        {
            bool privacyStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var privacyDetectedAt) ||
                DateTime.UtcNow - privacyDetectedAt > TimeSpan.FromSeconds(90);
            if (privacyStale)
                ScheduleRenderFirstBackground("privacy", tweakCacheKey, _tweaks.ToArray(), true, token);
            return;
        }
        if (key == "privacy") BuildPrivacyActions();
        if (key == "ui" && TryAttachUiPageCache())
        {
            BuildWinhanceUiActions();
            bool uiStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var uiDetectedAt) ||
                DateTime.UtcNow - uiDetectedAt > TimeSpan.FromSeconds(90);
            if (uiStale)
                ScheduleRenderFirstBackground("ui", tweakCacheKey, _tweaks.ToArray(), true, token);
            return;
        }
        if (key == "browsers")
        {
            string browserSub = _sub ?? "Firefox";
            bool browserStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var browserDetectedAt) || DateTime.UtcNow - browserDetectedAt > TimeSpan.FromSeconds(90);
            if (TryAttachBrowserPageCache(browserSub))
            {
                if (browserStale)
                    ScheduleRenderFirstBackground("browsers", tweakCacheKey, _tweaks.ToArray(), true, token);
                return;
            }
        }
        if (key == "memory")
        {
            BuildMemoryActions();
        }
        if (key == "storage")
        {
            long storagePreMs = navPhase.ElapsedMilliseconds;
            var storageForeground = Stopwatch.StartNew();
            bool storageStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var storageDetectedAt) || DateTime.UtcNow - storageDetectedAt > TimeSpan.FromSeconds(90);
            if (!storageStale && TryAttachStoragePageCache(token))
                return;
            if (storageStale) InvalidateStoragePageCache();

            AttachStorageStaticShell(token, out long storageHeroMs, out long storageMetricsMs);

            BuildStorageActions(token);
            ScheduleStorageOptimizationAdvisor(token);
            long storageActionsMs = storageForeground.ElapsedMilliseconds - storageHeroMs - storageMetricsMs;
            ScheduleStorageTools(token);
            long storageScheduleMs = storageForeground.ElapsedMilliseconds - storageHeroMs - storageMetricsMs - storageActionsMs;
            LogService.Info("performance",
                $"STORAGE_FOREGROUND_PHASE preStorageMs={storagePreMs} catalogMs={storageCatalogMs} heroMs={storageHeroMs} metricsMs={storageMetricsMs} actionsMs={storageActionsMs} scheduleMs={storageScheduleMs} totalMs={storageForeground.ElapsedMilliseconds}");
        }
        if (key == "gpu")
        {
            var gpuBuild = Stopwatch.StartNew();
            SynchronizeGpuDetectionGeneration();
            bool gpuStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var gpuDetectedAt) || DateTime.UtcNow - gpuDetectedAt > TimeSpan.FromSeconds(90);
            if (!gpuStale && TryAttachGpuPageCache(token))
                return;
            if (gpuStale) InvalidateGpuPageCache();
            long setupMs = gpuBuild.ElapsedMilliseconds;
            BuildGpuDriverCenter(token);
            ThrowIfNavigationStale(generation, token);
            long driverCenterMs = gpuBuild.ElapsedMilliseconds - setupMs;
            ScheduleGpuActions(token);
            long actionsMs = gpuBuild.ElapsedMilliseconds - setupMs - driverCenterMs;
            if (gpuBuild.ElapsedMilliseconds >= 5)
                LogService.Info("performance",
                    $"GPU_BUILD_PHASE setupMs={setupMs} driverCenterMs={driverCenterMs} actionsMs={actionsMs} totalMs={gpuBuild.ElapsedMilliseconds}");
        }
        if (key == "network")
        {
            if (TryAttachNetworkPageCache(token, out bool networkPanelsRefreshed))
            {
                bool networkStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var networkDetectedAt) || DateTime.UtcNow - networkDetectedAt > TimeSpan.FromSeconds(90);
                if (networkStale)
                {
                    int networkGeneration = generation;
                    var networkTweaks = _tweaks.ToArray();
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        if (token.IsCancellationRequested || networkGeneration != _navigationGeneration || _section != "network") return;
                        SafeTaskService.Forget(
                            DetectSectionTweaksAndRefreshAsync("network", tweakCacheKey, networkTweaks, token, networkGeneration),
                            "tweaks",
                            "Cached network detect");
                    });
                }
                return;
            }
            var networkBuild = Stopwatch.StartNew();
            BuildNetworkPerformancePanel(token);
            long networkPerfMs = networkBuild.ElapsedMilliseconds;
            await BuildNetworkTelemetryAsync(token);
            ThrowIfNavigationStale(generation, token);
            long networkShellMs = networkBuild.ElapsedMilliseconds - networkPerfMs;
            BuildNetworkDashboardBody(token);
            long networkDashboardMs = networkBuild.ElapsedMilliseconds - networkPerfMs - networkShellMs;
            await BuildDriverActionsAsync(token);
            ThrowIfNavigationStale(generation, token);
            long networkDriverMs = networkBuild.ElapsedMilliseconds - networkShellMs - networkPerfMs - networkDashboardMs;
            ScheduleNetworkTools(token);
            long networkToolsMs = networkBuild.ElapsedMilliseconds - networkShellMs - networkPerfMs - networkDashboardMs - networkDriverMs;
            CaptureNetworkPageCache();
            LogService.Info("performance", $"NETWORK_BUILD shellMs={networkShellMs} perfMs={networkPerfMs} dashboardMs={networkDashboardMs} driverMs={networkDriverMs} toolsMs={networkToolsMs} totalMs={networkBuild.ElapsedMilliseconds}");
        }
        if (key == "storage")
        {
            var storageBuild = Stopwatch.StartNew();
            int storageGeneration = generation;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || storageGeneration != _navigationGeneration || _section != "storage") return;
                SafeTaskService.Forget(BuildStorageHealthAsync(token), "storage", "Storage health");
                SafeTaskService.Forget(BuildStorageAdvancedAsync(token), "storage", "Storage advanced");
            });
            long kickoffMs = storageBuild.ElapsedMilliseconds;
            BuildStorageSnapshot(token);
            BuildWindowsModernizerStoragePanel(token);
            long snapshotMs = storageBuild.ElapsedMilliseconds - kickoffMs;
            LogService.Info("performance", $"STORAGE_BUILD kickoffMs={kickoffMs} snapshotMs={snapshotMs} toolsMs=0 totalMs={storageBuild.ElapsedMilliseconds}");
        }
        if (key == "update")
        {
            if (TryAttachUpdatePageCache(token))
                return;
            await BuildWindowsUpdateCenterAsync(token);
            ThrowIfNavigationStale(generation, token);
        }
        if (key == "browsers")
        {
            string browser = _sub ?? "Firefox";
            var browserBuild = Stopwatch.StartNew();

            BuildBrowserDashboard(browser, token);
            long dashboardMs = browserBuild.ElapsedMilliseconds;

            BuildBrowserCachePanel(browser, token);
            long cachePanelMs = browserBuild.ElapsedMilliseconds - dashboardMs;

            long updaterMs = 0;
            long advancedMs = 0;
            long modernizerMs = 0;
            if (browser == "Firefox")
            {
                long phaseStart = browserBuild.ElapsedMilliseconds;
                BuildFirefoxUpdaterPanel(token);
                updaterMs = browserBuild.ElapsedMilliseconds - phaseStart;

                phaseStart = browserBuild.ElapsedMilliseconds;
                BuildFirefoxAdvancedPanel(token);
                advancedMs = browserBuild.ElapsedMilliseconds - phaseStart;

                phaseStart = browserBuild.ElapsedMilliseconds;
                BuildWindowsModernizerFirefoxPanel(token);
                modernizerMs = browserBuild.ElapsedMilliseconds - phaseStart;
            }

            LogService.Info("performance",
                $"BROWSER_BUILD_PHASE browser={browser} dashboardMs={dashboardMs} cachePanelMs={cachePanelMs} updaterMs={updaterMs} advancedMs={advancedMs} modernizerMs={modernizerMs} totalMs={browserBuild.ElapsedMilliseconds}");
        }
        if (key == "ui")
        {
            var uiBuild = Stopwatch.StartNew();
            BuildContextMenuTweaker();
            long contextMenuMs = uiBuild.ElapsedMilliseconds;

            BuildWindowsModernizerUiPanel(token);
            long modernizerUiMs = uiBuild.ElapsedMilliseconds - contextMenuMs;

            BuildWinhanceUiActions();
            long winhanceUiMs = uiBuild.ElapsedMilliseconds - contextMenuMs - modernizerUiMs;

            LogService.Info("performance",
                $"UI_BUILD_PHASE contextMenuMs={contextMenuMs} modernizerMs={modernizerUiMs} winhanceMs={winhanceUiMs} totalMs={uiBuild.ElapsedMilliseconds}");
        }
        var renderTailPerf = Stopwatch.StartNew();
        TweakDefinition[] lazyTweaks = _tweaks.Where(x => x.LazyDetect).ToArray();
        TweakDefinition[] immediateTweaks = _tweaks.Where(x => !x.LazyDetect).ToArray();
        long splitTweaksMs = renderTailPerf.ElapsedMilliseconds;
        bool staleDetection = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var detectedAt) || DateTime.UtcNow - detectedAt > TimeSpan.FromSeconds(90);
        bool renderFirst = key is "system" or "privacy" or "network" or "gpu" or "memory" or "ui" or "browsers" or "storage" or "update";
        if (renderFirst)
        {
            var phase = Stopwatch.StartNew();
            if (key == "network")
            {
                _networkTweakHost = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
                ContentStack.Children.Add(_networkTweakHost);
                ScheduleNetworkTweakCards(token);
            }
            else if (key == "gpu")
            {
                _gpuTweakHost = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
                ContentStack.Children.Add(_gpuTweakHost);
                ScheduleGpuTweakCards(token);
            }
            else if (key == "storage")
            {
                _storageTweakHost = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
                ContentStack.Children.Add(_storageTweakHost);
                ScheduleStorageTweakCards(token);
            }
            else if (key == "system")
            {
                _systemVirtualHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                ContentStack.Children.Add(_systemVirtualHost);
                ScheduleSystemVirtualCards(token);
            }
            else
            {
                BuildTweakCards();
            }
            long cardsMs = phase.ElapsedMilliseconds;
            if (key == "system" && systemForeground != null)
                LogService.Info("performance", $"SYSTEM_FOREGROUND_PHASE catalogMs={systemCatalogMs} preRenderMs={systemForeground.ElapsedMilliseconds - systemCatalogMs - cardsMs} cardsMs={cardsMs} totalMs={systemForeground.ElapsedMilliseconds}");
            if (key == "network") CaptureNetworkPageCache();
            if (key == "browsers") CaptureBrowserPageCache(_sub ?? "Firefox");
            if (key == "update") CaptureUpdatePageCache();
            if (key == "system" && _systemVirtualRowsCache != null) CaptureSystemPageCache();
            if (key == "privacy") CapturePrivacyPageCache();
            if (key == "ui") CaptureUiPageCache();
            if (key is "network" or "storage" or "gpu")
                LogService.Info("performance", $"RENDER_TAIL_PHASE section={key} splitMs={splitTweaksMs} cardsMs={cardsMs} totalMs={renderTailPerf.ElapsedMilliseconds}");
            LogService.Info("performance", $"TWEAK_UI_PHASE section={key} count={_tweaks.Count} cardsMs={cardsMs}");
            if (key == "gpu")
            {
                int gpuDeferredGeneration = generation;
                TweakDefinition[] gpuDeferredTweaks = _tweaks.ToArray();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (token.IsCancellationRequested || gpuDeferredGeneration != _navigationGeneration || _section != "gpu") return;
                    SafeTaskService.Forget(RunGpuDeferredStateAfterSettleAsync(tweakCacheKey, gpuDeferredTweaks, staleDetection, token, gpuDeferredGeneration), "gpu", "Deferred GPU state");
                });
            }
            else if (key == "storage")
            {
                int storageDeferredGeneration = generation;
                TweakDefinition[] storageDeferredTweaks = _tweaks.ToArray();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (token.IsCancellationRequested || storageDeferredGeneration != _navigationGeneration || _section != "storage") return;
                    SafeTaskService.Forget(RunStorageDeferredStateAfterSettleAsync(tweakCacheKey, storageDeferredTweaks, staleDetection, token, storageDeferredGeneration), "storage", "Deferred storage state");
                });
            }
            else
            {
                ScheduleRenderFirstBackground(key, tweakCacheKey, _tweaks.ToArray(), staleDetection, token);
            }
            return;
        }
        if (staleDetection)
        {
            await _engine.DetectAsync(immediateTweaks, token);
            ThrowIfNavigationStale(generation, token);
            _tweakCacheTime[tweakCacheKey] = DateTime.UtcNow;
        }
        await BuildHardwareRecommendedPanelAsync(key, _tweaks, token);
        ThrowIfNavigationStale(generation, token);
        BuildTweakCards();
        if (lazyTweaks.Length > 0) SafeTaskService.Forget(DetectLazyTweaksAsync(key, lazyTweaks, token), "tweaks", "Lazy detect " + key);
    }

    private void ScheduleRenderFirstBackground(string sectionKey, string cacheKey, TweakDefinition[] tweaks, bool staleDetection, CancellationToken token)
    {
        int generation = _navigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != sectionKey) return;

            var immediate = tweaks.Where(x => !x.LazyDetect).ToArray();
            var lazy = tweaks.Where(x => x.LazyDetect).ToArray();

            if (staleDetection && immediate.Length > 0)
                SafeTaskService.Forget(
                    DetectSectionTweaksAndRefreshAsync(sectionKey, cacheKey, immediate, token, generation),
                    "tweaks",
                    "Background immediate detect " + sectionKey);

            if (lazy.Length > 0)
                SafeTaskService.Forget(
                    DetectLazyTweaksAfterSettleAsync(sectionKey, lazy, token, generation),
                    "tweaks",
                    "Background lazy detect " + sectionKey);

            if (sectionKey is "network" or "memory")
                SafeTaskService.Forget(BuildHardwareRecommendedPanelAsync(sectionKey, tweaks, token), sectionKey, "Lazy hardware recommended");
        });
    }

    private object? _sectionDetectGateStore;
    private HashSet<string>? _sectionDetectInFlightStore;
    private object _sectionDetectGate => _sectionDetectGateStore ??= new object();
    private HashSet<string> _sectionDetectInFlight =>
        _sectionDetectInFlightStore ??= new(StringComparer.OrdinalIgnoreCase);

    private async Task DetectSectionTweaksAndRefreshAsync(string sectionKey, string cacheKey, TweakDefinition[] tweaks, CancellationToken token, int generation = -1)
    {
        if (generation < 0) generation = _navigationGeneration;
        lock (_sectionDetectGate)
        {
            if (!_sectionDetectInFlight.Add(cacheKey)) return;
        }
        try
        {
            // Read-only detection is shared work: finish it even when navigation leaves the section.
            await _engine.DetectAsync(tweaks, CancellationToken.None);
            _tweakCacheTime[cacheKey] = DateTime.UtcNow;
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != sectionKey) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration || _section != sectionKey) return;
                RefreshCards();
                SyncAttachedClassicCacheRevisionStamp(sectionKey, cacheKey);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Error("Background tweak detect failed: " + sectionKey, ex);
            if (!token.IsCancellationRequested &&
                generation == _navigationGeneration &&
                string.Equals(_section, sectionKey, StringComparison.OrdinalIgnoreCase))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested ||
                        generation != _navigationGeneration ||
                        !string.Equals(_section, sectionKey, StringComparison.OrdinalIgnoreCase))
                        return;

                    ShowActionFeedback(
                        sectionKey.Equals("privacy", StringComparison.OrdinalIgnoreCase) ? "Privacy & Security" : sectionKey,
                        ActionFeedbackKind.Failed,
                        "Nepodařilo se ověřit skutečný stav některých přepínačů: " + ex.Message);
                });
            }
        }
        finally
        {
            lock (_sectionDetectGate) _sectionDetectInFlight.Remove(cacheKey);
        }
    }
    private async Task DetectLazyTweaksAfterSettleAsync(string sectionKey, TweakDefinition[] tweaks, CancellationToken token, int generation)
    {
        try
        {
            await Task.Delay(250, token);
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != sectionKey) return;
            await DetectLazyTweaksAsync(sectionKey, tweaks, token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task DetectLazyTweaksAsync(string sectionKey, TweakDefinition[] tweaks, CancellationToken token)
    {
        int generation = _navigationGeneration;
        try
        {
            await _engine.DetectAsync(tweaks, token);
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != sectionKey) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested && generation == _navigationGeneration && _section == sectionKey)
                    RefreshCards();
            });
        }
        catch (OperationCanceledException) { }
    }

    private void RenderSectionError(string key, string id, Exception ex, int generation)
    {
        if (generation != _navigationGeneration || !string.Equals(_section, key, StringComparison.OrdinalIgnoreCase))
        {
            LogService.Info("navigation",
                $"STALE_ERROR_SURFACE_BLOCKED section={key} gen={generation} currentGen={_navigationGeneration} currentSection={_section}");
            return;
        }

        ContentStack.Children.Clear(); TopActions.Children.Clear(); ActionBar.Visibility = Visibility.Collapsed;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Sekci se nepodařilo načíst", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        panel.Children.Add(new TextBlock { Text = ex.Message + "  [ID " + id + "]", TextWrapping = TextWrapping.Wrap, Foreground = TextSecondaryBrush() });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var retry = new Button { Content = "Zkusit znovu" };
        ApplyPillButtonAppearance(retry);
        retry.Click += (_, _) =>
        {
            if (generation != _navigationGeneration ||
                !string.Equals(_section, key, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_lastNavigationErrorId, id, StringComparison.Ordinal))
                return;

            SafeTaskService.Forget(ShowSectionAsync(key), "navigation", "Retry " + key);
        };
        var copy = new Button { Content = "Kopírovat chybu" };
        ApplyPillButtonAppearance(copy);
        copy.Click += (_, _) =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText($"Spankyho Optimizer\nSekce: {key}\nID: {id}\nTyp: {ex.GetType().FullName}\nChyba: {ex.Message}\nStage: {StartupDiagnostics.LastStage}");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        };
        actions.Children.Add(retry); actions.Children.Add(copy); panel.Children.Add(actions);
        ContentStack.Children.Add(new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.2 : 1), CornerRadius = new CornerRadius(16), Padding = new Thickness(20), Child = panel });
    }

    public async Task RunSectionSmokeTestAsync()
    {
        foreach (string key in CatalogService.Sections.Select(x => x.Key))
        {
            if (key == "components")
            {
                LogService.Info("section-smoke", "SKIP components (replaced by ai-runtime)");
                continue;
            }
            LogService.Info("section-smoke", "ENTER " + key); await ShowSectionAsync(key); await AssertSectionLayoutHealthyAsync(key); EnsureNavigationHealthy(key); LogService.Info("section-smoke", "OK " + key); await Task.Delay(15);
        }
        var wmRead = Stopwatch.StartNew();
        WindowsModernizerSnapshot wm = WindowsModernizerService.ReadSnapshot();
        wmRead.Stop();
        LogService.Info("WINDOWS_MODERNIZER_READONLY_SMOKE_OK ms=" + wmRead.ElapsedMilliseconds +
            " ui=" + wm.UiBaselineConfigured + " search=" + wm.SearchCleanupConfigured +
            " mtu=" + (wm.ActiveMtu?.ToString() ?? "n/a") + " firefoxRam=" + wm.FirefoxDynamicRamConfigured +
            " vendor=" + wm.VendorServicesTuned);
        if (wmRead.ElapsedMilliseconds > 100)
            throw new InvalidOperationException("WindowsModernizer snapshot hot-path exceeded 100 ms: " + wmRead.ElapsedMilliseconds + " ms");
        var adaptiveSw = Stopwatch.StartNew();
        AdaptiveScanReport adaptive = await WindowsModernizerService.ScanAdaptiveSystemAsync();
        adaptiveSw.Stop();
        if (adaptive.Total <= 0)
            throw new InvalidOperationException("Adaptive Windows scan returned no findings.");
        LogService.Info("WINDOWS_MODERNIZER_ADAPTIVE_SCAN_SMOKE_OK ms=" + adaptiveSw.ElapsedMilliseconds +
            " total=" + adaptive.Total + " needed=" + adaptive.Needed + " ok=" + adaptive.AlreadyOptimal +
            " manual=" + adaptive.ManualReview + " na=" + adaptive.NotApplicable);
        if (adaptiveSw.ElapsedMilliseconds > 5000)
            throw new InvalidOperationException("Adaptive Windows scan exceeded 5000 ms: " + adaptiveSw.ElapsedMilliseconds + " ms");
        LogService.Info("SECTION_SMOKE_OK");
    }


    private void BuildSubSelector(string section, string[] options)
    {
        SubSelectorHost.Visibility = Visibility.Visible;
        if (!_subSelectorCache.TryGetValue(section, out var buttons))
        {
            buttons = options.Select(opt =>
            {
                var button = new Button { Content = opt, Tag = opt };
                ApplyPillButtonAppearance(button);
                button.Click += delegate
                {
                    SafeTaskService.Forget(NavigateSubSectionFromUiAsync(section, opt), "navigation", "Open sub-section " + opt);
                };
                return button;
            }).ToArray();
            _subSelectorCache[section] = buttons;
        }

        foreach (var button in buttons)
        {
            bool active = string.Equals(button.Tag as string, _sub, StringComparison.Ordinal);
            if (active)
            {
                button.Background = UiBrush("SpankyAccentBrush");
                button.Foreground = OnAccentTextBrush();
            }
            else
            {
                button.Background = ButtonVioletSoftBrush();
                button.Foreground = TextPrimaryBrush();
            }
            SubSelectorHost.Children.Add(button);
        }
    }



    private void BuildTweakCards()
    {
        if (UseVirtualTweakRows())
        {
            BuildVirtualTweakCards();
            return;
        }
        ClearVirtualTweakRows();
        BuildClassicTweakCards(ContentStack);
    }

    private void BuildClassicTweakCards(Panel host)
    {
        foreach (IGrouping<string, TweakDefinition> group in _tweaks.GroupBy(t => t.Group))
        {
            host.Children.Add(CreateClassicTweakGroupHeader(group.Key, group.Count()));
            foreach (var tweak in group)
                host.Children.Add(CreateClassicTweakCard(tweak));
        }
    }

    private Border CreateClassicTweakGroupHeader(string groupName, int count)
    {
        var headRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        headRow.Children.Add(new Border
        {
            Width = 4,
            Height = 20,
            CornerRadius = new CornerRadius(2),
            Background = ButtonVioletBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });
        headRow.Children.Add(new TextBlock
        {
            Text = $"{groupName.ToUpperInvariant()}   •   {count} TWEAKŮ",
            FontSize = 13.5,
            CharacterSpacing = 10,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });
        return new Border
        {
            Margin = new Thickness(0, 10, 0, 6),
            Padding = new Thickness(11, 8, 11, 8),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(_darkTheme ? 1.2 : 1),
            BorderBrush = SectionBorderBrush(),
            Background = SectionHeaderBackgroundBrush(),
            Child = headRow
        };
    }

    private Border CreateClassicTweakCard(TweakDefinition tweak)
    {
        tweak.PropertyChanged -= Tweak_PropertyChanged;
        tweak.PropertyChanged += Tweak_PropertyChanged;
        return CreateTweakCard(tweak);
    }


    private static int CountClassicTweakCards(Panel? host) =>
        host?.Children.OfType<Border>().Count(x => x.Child is Grid) ?? 0;

    private void Tweak_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Pending" or "Current" or "IsLoaded") QueueDirtyStateRefresh();
    }
    private void QueueDirtyStateRefresh()
    {
        if (Interlocked.Exchange(ref _dirtyRefreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _dirtyRefreshQueued, 0);
            UpdateDirtyState();
        }))
            Interlocked.Exchange(ref _dirtyRefreshQueued, 0);
    }


    private sealed record ClassicTweakCardRefs(
        ToggleSwitch Toggle,
        TextBlock ToggleState,
        TextBlock Meta,
        Border StateBadge,
        TextBlock StateText,
        Border VerdictBadge,
        TextBlock VerdictText,
        bool NeutralChrome = false);

    private Border CreateTweakCard(TweakDefinition t)
    {
        bool neutralChrome = UseNeutralSectionChrome;
        var policy = TweakPolicyService.Evaluate(t, _tweakViewMode);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconBox = new Border { Width = UseCompactSectionChrome ? 34 : 42, Height = UseCompactSectionChrome ? 34 : 42, CornerRadius = new CornerRadius(8), Background = SurfaceAltBrush(), BorderThickness = new Thickness(0), Child = SectionIcon(t.Section, UseCompactSectionChrome ? 25 : 31), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(iconBox);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        text.Children.Add(new TextBlock { Text = t.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var uiState = TweakUiState(t);
        var stateText = uiState.Text;
        var stateBrush = uiState.Resource;
        var stateBadge = Badge(stateText, stateBrush);
        titleRow.Children.Add(stateBadge);
        var verdict = Badge(policy.Verdict, policy.Verdict == "Recommended" ? "SpankySuccessBrush" : policy.Verdict == "Advanced" || policy.Verdict == "Needs attention" ? "SpankyWarningBrush" : "SpankyInfoBrush");
        verdict.Tag = "policyVerdict"; titleRow.Children.Add(verdict);
        if (t.RestartRequired) titleRow.Children.Add(Badge("Restart", "SpankyInfoBrush"));
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock { Text = t.Description, FontSize = UseCompactSectionChrome ? 12.5 : 12, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap, MaxLines = UseCompactSectionChrome ? 0 : 2, TextTrimming = UseCompactSectionChrome ? TextTrimming.None : TextTrimming.CharacterEllipsis, MaxWidth = 1100 });
        var meta = new TextBlock { Text = _tweakViewMode == TweakViewMode.Diagnostics || !t.IsLoaded ? policy.Detail : TweakVerificationMeta(t), FontSize = 10.8, Foreground = TextSecondaryBrush(), Tag = "meta", TextWrapping = TextWrapping.Wrap };
        text.Children.Add(meta);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var toggleState = new TextBlock { Text = t.Pending ? "ON" : "OFF", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ClassicToggleStateBrush(t.Pending, neutralChrome), VerticalAlignment = VerticalAlignment.Center, Tag = "toggleState", Margin = new Thickness(0, 0, 3, 0) };
        Grid.SetColumn(toggleState, 2); grid.Children.Add(toggleState);
        var toggle = new ToggleSwitch { IsOn = t.Pending, Tag = t, IsEnabled = policy.CanEdit, VerticalAlignment = VerticalAlignment.Center, OffContent = "", OnContent = "", MinWidth = 50 };
        AutomationProperties.SetName(toggle, t.Name);
        toggle.Toggled += delegate { toggleState.Text = toggle.IsOn ? "ON" : "OFF"; toggleState.Foreground = ClassicToggleStateBrush(toggle.IsOn, neutralChrome); var live = TweakPolicyService.Evaluate(t, _tweakViewMode); if (live.CanEdit && toggle.Tag is TweakDefinition { IsLoaded: not false } td) { td.Pending = toggle.IsOn; QueueDirtyStateRefresh(); } };
        Grid.SetColumn(toggle, 3); grid.Children.Add(toggle);
        grid.Tag = new ClassicTweakCardRefs(
            toggle,
            toggleState,
            meta,
            stateBadge,
            (TextBlock)stateBadge.Child,
            verdict,
            (TextBlock)verdict.Child,
            neutralChrome);
        var row = new Border { Tag = t, Background = UseCompactSectionChrome ? SurfaceAltBrush() : SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = UseCompactSectionChrome ? new Thickness(0,0,0,1) : new Thickness(1), CornerRadius = new CornerRadius(0), Margin = new Thickness(0, 0, 0, UseCompactSectionChrome ? 0 : 7), Padding = UseCompactSectionChrome ? new Thickness(12,16,12,16) : new Thickness(14, 10, 14, 10), Child = grid };
        if (!UseNeutralSectionChrome) AttachHoverMotion(row, 1.002f); ToolTipService.SetToolTip(row, policy.Detail);
        return row;
    }


    private static Brush ClassicToggleStateBrush(bool on, bool neutral) =>
        neutral ? ThemeColor(on ? (byte)224 : (byte)152, on ? (byte)224 : (byte)152, on ? (byte)224 : (byte)152)
                : on ? SuccessBrush() : TextSecondaryBrush();

    private static void ApplyBadgeAppearance(Border badge, string resource, bool neutral)
    {
        Brush semantic = neutral ? PaletteBrush(resource switch
        {
            "SpankySuccessBrush" => NeutralBlackPalette.Success,
            "SpankyWarningBrush" => NeutralBlackPalette.Warning,
            "SpankyInfoBrush" => NeutralBlackPalette.Info,
            _ => NeutralBlackPalette.Accent
        }) : UiBrush(resource);
        badge.Background = neutral ? ThemeColor(10,10,10) : semantic;
        if (badge.Child is TextBlock label)
            label.Foreground = neutral ? semantic : OnAccentTextBrush();
    }

    private static Border Badge(string text, string resource)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(7.0), Opacity = 0.88,
            Padding = new Thickness(7.0, 3.0, 7.0, 3.0),
            Child = new TextBlock { Text = text, FontSize = 10.0 }
        };
        ApplyBadgeAppearance(badge, resource, UseNeutralSectionChrome);
        return badge;
    }
    private long CurrentTweakRevisionStamp()
    {
        long stamp = _tweaks.Count;
        foreach (var tweak in _tweaks)
            stamp += tweak.Revision;
        return stamp;
    }

    private bool RefreshCardsIfTweakRevisionChanged(ref long cachedStamp)
    {
        long currentStamp = CurrentTweakRevisionStamp();
        if (currentStamp == cachedStamp)
            return false;

        RefreshCards();
        cachedStamp = currentStamp;
        return true;
    }

    private void SyncAttachedClassicCacheRevisionStamp(string sectionKey, string cacheKey)
    {
        long stamp = CurrentTweakRevisionStamp();
        if (sectionKey == "network" && _networkPageCache != null)
        {
            _networkPageRevisionStamp = stamp;
            return;
        }

        if (sectionKey == "browsers")
        {
            const string prefix = "browsers|";
            string sub = cacheKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? cacheKey[prefix.Length..]
                : (_sub ?? "Firefox");
            if (_browserPageCaches.ContainsKey(sub))
                _browserPageRevisionStamp[sub] = stamp;
        }
    }

    private void RefreshClassicTweakCard(Border item)
    {
        if (item.Tag is not TweakDefinition tweakDefinition || item.Child is not Grid grid) return;
        var policy = TweakPolicyService.Evaluate(tweakDefinition, _tweakViewMode);

        if (grid.Tag is not ClassicTweakCardRefs refs)
        {
            // Compatibility fallback for any cached card created by an older layout path.
            ToggleSwitch? toggle = grid.Children.OfType<ToggleSwitch>().FirstOrDefault();
            StackPanel? stack = grid.Children.OfType<StackPanel>().FirstOrDefault();
            TextBlock? meta = stack?.Children.OfType<TextBlock>().FirstOrDefault(x => Equals(x.Tag, "meta"));
            StackPanel? title = stack?.Children.OfType<StackPanel>().FirstOrDefault(x => x.Orientation == Orientation.Horizontal);
            Border? verdict = title?.Children.OfType<Border>().FirstOrDefault(x => Equals(x.Tag, "policyVerdict"));
            Border? state = title?.Children.OfType<Border>().FirstOrDefault(x => !Equals(x.Tag, "policyVerdict"));
            TextBlock? toggleState = grid.Children.OfType<TextBlock>().FirstOrDefault(x => Equals(x.Tag, "toggleState"));
            if (toggle == null || meta == null || verdict?.Child is not TextBlock verdictText || state?.Child is not TextBlock stateTextBlock)
                return;
            toggleState ??= new TextBlock { Text = toggle.IsOn ? "ON" : "OFF", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = toggle.IsOn ? ThemeColor(224,224,224) : ThemeColor(152,152,152), VerticalAlignment = VerticalAlignment.Center, Tag = "toggleState" };
            refs = new ClassicTweakCardRefs(toggle, toggleState, meta, state, stateTextBlock, verdict, verdictText, UseNeutralSectionChrome);
            grid.Tag = refs;
        }

        bool enabled = policy.CanEdit && !tweakDefinition.IsBusy;
        if (refs.Toggle.IsEnabled != enabled) refs.Toggle.IsEnabled = enabled;
        if (refs.Toggle.IsOn != tweakDefinition.Pending) refs.Toggle.IsOn = tweakDefinition.Pending;
        string toggleText = tweakDefinition.Pending ? "ON" : "OFF";
        if (!string.Equals(refs.ToggleState.Text, toggleText, StringComparison.Ordinal)) refs.ToggleState.Text = toggleText;
        refs.ToggleState.Foreground = ClassicToggleStateBrush(tweakDefinition.Pending, refs.NeutralChrome);

        string metaText = _tweakViewMode == TweakViewMode.Diagnostics || !tweakDefinition.IsLoaded
            ? policy.Detail
            : TweakVerificationMeta(tweakDefinition);
        if (!string.Equals(refs.Meta.Text, metaText, StringComparison.Ordinal)) refs.Meta.Text = metaText;

        var uiState = TweakUiState(tweakDefinition);
        string stateText = uiState.Text;
        if (!string.Equals(refs.StateText.Text, stateText, StringComparison.Ordinal))
        {
            refs.StateText.Text = stateText;
            ApplyBadgeAppearance(refs.StateBadge, uiState.Resource, refs.NeutralChrome);
        }

        if (!string.Equals(refs.VerdictText.Text, policy.Verdict, StringComparison.Ordinal))
        {
            refs.VerdictText.Text = policy.Verdict;
            ApplyBadgeAppearance(refs.VerdictBadge, policy.Verdict == "Recommended"
                ? "SpankySuccessBrush"
                : policy.Verdict == "Advanced" || policy.Verdict == "Needs attention"
                    ? "SpankyWarningBrush"
                    : "SpankyInfoBrush", refs.NeutralChrome);
        }
        // Tooltip is initialized when the card is created. Rebuilding it during every
        // state refresh causes expensive WinUI tooltip materialization on the UI thread.
    }

    private void RefreshCards(bool force = false)
    {
        if (_virtualTweakRows != null)
            RefreshVirtualTweakRows(force);
        RefreshVirtualSectionDashboardMetrics(_section);

        foreach (Border item in ContentStack.Children.OfType<Border>())
            RefreshClassicTweakCard(item);
        if (_networkTweakHost != null)
        {
            foreach (Border item in _networkTweakHost.Children.OfType<Border>())
                RefreshClassicTweakCard(item);
        }
        if (_gpuTweakHost != null)
        {
            foreach (Border item in _gpuTweakHost.Children.OfType<Border>())
                RefreshClassicTweakCard(item);
        }
        if (_storageTweakHost != null)
        {
            foreach (Border item in _storageTweakHost.Children.OfType<Border>())
                RefreshClassicTweakCard(item);
        }
    }

    private void UpdateDirtyState()
    {
        int num = _tweaks.Count((TweakDefinition x) => x.IsDirty);
        if (num <= 0)
        {
            if (_actionBarInitialized)
            {
                ActionBar.Visibility = Visibility.Collapsed;
                UpdateRowlessShellGeometry();
            }
            return;
        }

        EnsureActionBarInitialized();
        ActionBar.Visibility = Visibility.Visible;
        UpdateRowlessShellGeometry();
        DirtyText.Text = num == 1 ? "1 nevyřízená změna" : $"{num} nevyřízených změn";
    }

    private void Apply_Click(object sender, RoutedEventArgs e) =>
        SafeTaskService.Forget(ApplyClickCoreAsync(), "tweaks", "Apply selected tweaks");

    private async Task ApplyClickCoreAsync()
    {
        List<TweakDefinition> dirty = _tweaks.Where((TweakDefinition x) => x.IsDirty).ToList();
        if (dirty.Count == 0) return;
        if (!await ShowTweakApplyConfirmationAsync(dirty)) return;
        if (dirty.Any((TweakDefinition x) => x.RestartRequired || x.Risk == TweakRisk.Advanced))
        {
            await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - tweak batch");
        }
        var progressResult = await RunCancellableWithProgressAsync(
            "Aplikuji tweaky",
            $"Provádím a ověřuji {dirty.Count} vybraných změn…",
            progressToken => _engine.ApplyAsync(dirty, progressToken),
            result => result.Count == 0,
            result => result.Count == 0 ? string.Empty : string.Join(" • ", result.Take(2)));
        if (!progressResult.Completed)
        {
            LogService.Info("tweaks", $"Global apply cancelled by user. requested={dirty.Count}");
            await ReconcileCancelledApplyAsync(dirty, "global");
            RefreshCards(force: true);
            QueueDirtyStateRefresh();
            return;
        }
        IReadOnlyList<string> readOnlyList = progressResult.Result;

        try
        {
            // Final source-of-truth pass. Never let the UI keep an optimistic Pending state
            // after Apply; command/API tweaks are detected again from Windows immediately.
            var immediateReadBack = dirty.Where(x => !x.RestartRequired).ToArray();
            if (immediateReadBack.Length > 0)
                await _engine.DetectAsync(immediateReadBack);
        }
        catch (Exception ex)
        {
            LogService.Error("tweaks", "Post-apply final detect failed", ex);
        }

        if (dirty.Any(x => x.Section == "storage")) InvalidateStoragePageCache();
        if (dirty.Any(x => x.Section == "memory")) InvalidateMemoryRecommendedPanel();
        if (dirty.Any(x => x.Section == "defender")) InvalidateDefenderPageCache();
        if (dirty.Any(x => x.Section == "components")) InvalidateComponentsPageCache();
        if (dirty.Any(x => x.Section == "browsers")) InvalidateBrowserPageCache();
        if (dirty.Any(x => x.Section is "memory" or "system")) MemoryStateService.Invalidate();
        RefreshCards(force: true);
        QueueDirtyStateRefresh();
        if (readOnlyList.Count > 0)
        {
            ShowActionFeedback("Tweaky", ActionFeedbackKind.Failed, string.Join(" • ", readOnlyList.Take(3)));
            await ShowMessageAsync("Některé změny selhaly", string.Join(Environment.NewLine, readOnlyList.Take(8)));
            return;
        }
        bool restartRequired = dirty.Any((TweakDefinition x) => x.RestartRequired);
        ShowActionFeedback("Tweaky", restartRequired ? ActionFeedbackKind.RestartRequired : ActionFeedbackKind.Verified, $"{dirty.Count} změn bylo ověřeno.");
        await ShowMessageAsync("Hotovo", restartRequired ? "Změny byly aplikovány a ověřeny. Některé vyžadují restart Windows." : "Změny byly aplikovány a ověřeny.");
    }

    private async Task ReconcileCancelledApplyAsync(IEnumerable<TweakDefinition> requested, string area)
    {
        var immediateReadBack = requested.Where(x => !x.RestartRequired).Distinct().ToArray();
        if (immediateReadBack.Length == 0) return;

        using var reconcileCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await _engine.DetectAsync(immediateReadBack, reconcileCts.Token);
            LogService.Info("tweaks", $"CANCEL_RECONCILE_OK area={area} count={immediateReadBack.Length}");
        }
        catch (OperationCanceledException)
        {
            LogService.Info("tweaks", $"CANCEL_RECONCILE_TIMEOUT area={area} count={immediateReadBack.Length}");
        }
        catch (Exception ex)
        {
            LogService.Error("tweaks", $"Cancelled apply reconcile failed area={area}", ex);
        }
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        foreach (TweakDefinition tweak in _tweaks)
        {
            tweak.Pending = tweak.Current;
        }
        RefreshCards(force: true);
        QueueDirtyStateRefresh();
    }

    private async Task ShowMessageAsync(string title, string text)
    {
        await ShowStyledInfoAsync(title, text, "Zavřít");
    }

    private Button QuickButton(string text, Func<Task> action)
    {
        Button b = new Button { Content = text };
        ApplyPillButtonAppearance(b);
        b.MinHeight = 35;
        b.Padding = new Thickness(13, 6, 13, 6);
        b.FontSize = 12.5;
        if (!UseNeutralSectionChrome) AttachHoverMotion(b, 1.012f);
        int actionBusy = 0;
        b.Click += async delegate
        {
            if (Interlocked.Exchange(ref actionBusy, 1) != 0)
            {
                LogService.Info("ui", $"QUICK_ACTION_DUPLICATE_BLOCKED text={text}");
                return;
            }

            object normalContent = b.Content;
            b.IsEnabled = false;
            b.Content = "Pracuji\u2026";
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                LogService.Error($"Quick action failed: {text} | HRESULT=0x{ex.HResult:X8}", ex);
                ShowActionFeedback(text, ActionFeedbackKind.Failed, ex.Message);
                await ShowMessageAsync("Chyba", ex.Message);
            }
            finally
            {
                b.Content = normalContent;
                b.IsEnabled = true;
                Interlocked.Exchange(ref actionBusy, 0);
            }
        };
        return b;
    }




    private void BuildMemoryActions()
    {
        BuildMemoryState();
        var defaults = new StackPanel { Spacing = 8 };
        defaults.Children.Add(new TextBlock { Text = "Plánování aktivních aplikací — výchozí nastavení Windows", Foreground = TextPrimaryBrush(), FontWeight = FontWeights.SemiBold });
        defaults.Children.Add(new TextBlock { Text = "Systémová cache — výchozí nastavení Windows", Foreground = TextPrimaryBrush(), FontWeight = FontWeights.SemiBold });
        defaults.Children.Add(new TextBlock { Text = "Stránkovací soubor — spravuje Windows", Foreground = TextPrimaryBrush(), FontWeight = FontWeights.SemiBold });
        defaults.Children.Add(new TextBlock { Text = "Doporučené nastavení ponechává plánování, systémovou cache a stránkovací soubor na bezpečných výchozích hodnotách Windows.", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        ContentStack.Children.Add(VisualPanel("\uE950", "Paměť a plánování", "Doporučené = bezpečné výchozí hodnoty Windows", defaults));
        var memoryTools = new StackPanel { Spacing = 8 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.0 };
        row.Children.Add(QuickButton("Spustit údržbu paměti", async delegate
        {
            await WindowsActions.RunIdleMaintenanceAsync();
        }));
        row.Children.Add(QuickButton("Uvolnit nepoužívanou paměť", async delegate
        {
            await WindowsActions.TrimWorkingSetsAsync();
        }));
        row.Children.Add(QuickButton("Komprese paměti", async delegate
        {
            var state = await WindowsActions.GetMemoryCompressionAsync();
            var result = await ShowStyledChoiceAsync("Komprese paměti", state + "\n\nVe Windows je doporučené ponechat ji zapnutou.", "Zapnout", "Vypnout");
            if (result == 0) return;
            bool enable = result == 1;
            await WindowsActions.SetMemoryCompressionAsync(enable);
            InvalidateMemoryRecommendedPanel();
            var readback = await WindowsActions.GetMemoryCompressionAsync();
            bool verified = readback.Contains(enable.ToString(), StringComparison.OrdinalIgnoreCase);
            ShowActionFeedback("Komprese paměti", verified ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed, readback);
            if (_section == "memory") await ShowSectionAsync("memory");
        }));
        memoryTools.Children.Add(row);
        memoryTools.Children.Add(BuildGTweakMemoryAdvancedActions());
        ContentStack.Children.Add(BuildDashboardExpander(
            "Paměťové nástroje",
            "Údržba paměti • uvolnění nepoužívané paměti • komprese • pokročilé čištění",
            memoryTools));
    }

    private Border? _storageToolsCard;
    private StackPanel? _storageActionsHost;
    private StackPanel? _storageActionsQueuedHost;
    private int _storageActionsQueuedGeneration;

    private Border EnsureStorageActionsCard()
    {
        if (_storageToolsCard != null)
            return _storageToolsCard;

        var root = new StackPanel { Spacing = 8 };
        _storageToolsCard = BuildDashboardExpander(
            "Storage nástroje",
            "Optimize Drives • NTFS • Windows.old • cache",
            root);

        if (_storageToolsCard.Child is Expander expander)
        {
            bool materialized = false;
            expander.Expanding += (_, _) =>
            {
                if (materialized) return;
                materialized = true;

                var main = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                main.Children.Add(QuickButton("Otevřít Optimize Drives", delegate
                {
                    ProcessRunner.Open("dfrgui.exe");
                    return Task.CompletedTask;
                }));
                root.Children.Add(main);
                root.Children.Add(BuildGTweakStorageActions());
            };
        }

        return _storageToolsCard;
    }

    private void PrewarmStorageActionsCard()
    {
        _ = EnsureStorageActionsCard();
    }

    private void BuildStorageActions(CancellationToken token)
    {
        _storageActionsHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        ContentStack.Children.Add(_storageActionsHost);
        ScheduleStorageActions(_storageActionsHost, token);
    }

    private void ScheduleStorageActions(StackPanel host, CancellationToken token)
    {
        int generation = _navigationGeneration;
        if (host.Children.Count > 0 ||
            (ReferenceEquals(_storageActionsQueuedHost, host) &&
             _storageActionsQueuedGeneration == generation)) return;
        _storageActionsQueuedHost = host;
        _storageActionsQueuedGeneration = generation;

        void ReleaseQueueTicket()
        {
            if (ReferenceEquals(_storageActionsQueuedHost, host) &&
                _storageActionsQueuedGeneration == generation)
            {
                _storageActionsQueuedHost = null;
                _storageActionsQueuedGeneration = 0;
            }
        }

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ReleaseQueueTicket();
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "storage" || !ReferenceEquals(_storageActionsHost, host) ||
                host.Children.Count > 0) return;

            var sw = Stopwatch.StartNew();
            host.Children.Add(EnsureStorageActionsCard());
            SchedulePageScrollPolicyRefresh();
            LogService.Info("performance", $"STORAGE_ACTIONS_DEFERRED ms={sw.ElapsedMilliseconds} generation={generation}");
        }))
            ReleaseQueueTicket();
    }

    private Border? _gpuToolsCard;
    private bool _gpuToolsQueued;

    private void ScheduleGpuActions(CancellationToken token)
    {
        if (_gpuToolsCard != null)
        {
            ContentStack.Children.Add(_gpuToolsCard);
            return;
        }
        if (_gpuToolsQueued) return;
        _gpuToolsQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _gpuToolsQueued = false;
            if (token.IsCancellationRequested || _section != "gpu" || _gpuToolsCard != null) return;
            var sw = Stopwatch.StartNew();
            BuildGpuActions();
            LogService.Info("performance", $"GPU_TOOLS_DEFERRED ms={sw.ElapsedMilliseconds}");
        });
    }

    private void BuildGpuActions()
    {
        if (_gpuToolsCard != null)
        {
            ContentStack.Children.Add(_gpuToolsCard);
            return;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(QuickButton("Grafické nastavení Windows", delegate
        {
            ProcessRunner.Open("ms-settings:display-advancedgraphics");
            return Task.CompletedTask;
        }));
        row.Children.Add(QuickButton("Vyčistit shader cache", async () =>
        {
            try
            {
                long before = await StorageMaintenanceService.GetShaderCacheSizeAsync();
                if (before <= 0)
                {
                    ShowActionFeedback("GPU shader cache", ActionFeedbackKind.Info, "Shader cache je už prázdná.");
                    await ShowMessageAsync("GPU", "Shader cache je už prázdná.");
                    return;
                }

                if (!await ShowStyledConfirmAsync(
                    "Vyčistit GPU shader cache?",
                    $"Shader cache mají přibližně {before / 1048576d:0.0} MB. Použij jen při konkrétním problému; první start hry po vyčištění může znovu kompilovat shadery a krátce stutterovat.",
                    "Vyčistit",
                    "Zrušit",
                    "GPU / Display")) return;

                await StorageMaintenanceService.ClearShaderCachesAsync();
                long after = await StorageMaintenanceService.GetShaderCacheSizeAsync();
                long freed = Math.Max(0, before - after);
                string detail = $"Uvolněno {freed / 1048576d:0.0} MB • zbývá {after / 1048576d:0.0} MB.";
                ShowActionFeedback(
                    "GPU shader cache",
                    after == 0 ? ActionFeedbackKind.Verified : ActionFeedbackKind.Info,
                    detail);
                await ShowMessageAsync("GPU", detail);
            }
            catch (Exception ex)
            {
                LogService.Error("gpu", "Shader cache cleanup failed", ex);
                ShowActionFeedback("GPU shader cache", ActionFeedbackKind.Failed, ex.Message);
                await ShowMessageAsync("GPU", "Vyčištění shader cache selhalo: " + ex.Message);
            }
        }));

        _gpuToolsCard = BuildDashboardExpander(
            "Grafické nástroje",
            "Windows Graphics Settings • shader cache",
            row);
        ContentStack.Children.Add(_gpuToolsCard);
    }

    private Task BuildDriverActionsAsync(CancellationToken token)
    {
        _networkDriverActionToken = token;
        if (_networkDriverCard != null && _networkRealtekSummary != null)
        {
            ContentStack.Children.Add(_networkDriverCard);
            var cachedTarget = _networkRealtekSummary;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!token.IsCancellationRequested && _section == "network")
                    SafeTaskService.Forget(LoadDriverSummaryAsync(cachedTarget, token), "network", "Lazy Realtek summary");
            });
            return Task.CompletedTask;
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "network") return;

            var summary = new TextBlock { Text = "Načítám stav Realtek adaptéru…", TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            _networkRealtekSummary = summary;
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(summary);
            stack.Children.Add(QuickButton("Zkontrolovat Windows Update", async delegate { await WindowsActions.RealtekScanAsync(_networkDriverActionToken); }));
            _networkDriverCard = BuildDashboardExpander(
                "Driver síťové karty",
                "Realtek • Windows Update • stav aktivního adaptéru",
                stack);
            ContentStack.Children.Add(_networkDriverCard);
            SafeTaskService.Forget(LoadDriverSummaryAsync(summary, token), "network", "Lazy Realtek summary");
        });
        return Task.CompletedTask;
    }

    private async Task LoadDriverSummaryAsync(TextBlock target, CancellationToken token)
    {
        string text;
        Exception? error = null;
        try
        {
            text = await WindowsActions.GetRealtekSummaryAsync(token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            error = ex;
            text = "Realtek kontrolu se nepodařilo načíst: " + ex.Message;
        }

        if (token.IsCancellationRequested || _section != "network") return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested || _section != "network") return;
            target.Text = text;
            target.Foreground = error == null ? TextSecondaryBrush() : WarningBrush();
            if (error != null)
                ShowActionFeedback("Realtek driver", ActionFeedbackKind.Failed, error.Message);
        });
    }

    private Border ActionCard(string title, string desc, string button, Func<Task> action)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string tag = string.IsNullOrWhiteSpace(_themeSection) ? "system" : _themeSection;
        var icon = new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(11), Background = SurfaceAltBrush(), BorderThickness = new Thickness(0), Child = SectionIcon(tag, 36), VerticalAlignment = VerticalAlignment.Center };
        if (!UseNeutralSectionChrome) AttachHoverMotion(icon, 1.025f); grid.Children.Add(icon);
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 17.5, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = desc, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = TextSecondaryBrush(), MaxWidth = 840 });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var actionButton = QuickButton(button, action); Grid.SetColumn(actionButton, 2); grid.Children.Add(actionButton);
        var card = new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.25 : 1), CornerRadius = new CornerRadius(13), Padding = new Thickness(15, 13, 15, 13), Margin = new Thickness(0, 0, 0, 10), Child = grid };
        if (!UseNeutralSectionChrome) AttachHoverMotion(card, 1.006f);
        return card;
    }


    private static Brush UiBrush(string name)
    {
        return name switch
        {
            "SpankySuccessBrush" => SuccessBrush(),
            "SpankyWarningBrush" => WarningBrush(),
            "SpankyAccentBrush" => AccentBrush(),
            "SpankyAccentSoftBrush" => AccentSoftBrush(),
            _ => AccentBrush()
        };
    }

    private static void ApplyPillButtonAppearance(Button button)
    {
        button.CornerRadius = new CornerRadius(4);
        button.Padding = new Thickness(13, 6, 13, 6);
        button.MinHeight = 34d;
        button.FontSize = 12d;
        button.Foreground = TextPrimaryBrush();
        // Shared material buttons across every page.
        ApplyMaterialButtonAppearance(button);
        button.BorderThickness = new Thickness(1);
        EnsureAccessibleButton(button);
    }

}