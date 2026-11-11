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

    private static readonly List<TweakDefinition> EmptyComponentTweaks = new(0);
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
    // ConfigureWindow resolves AppWindow once. Reuse its native projection
    // for startup caption/Glass work instead of resolving HWND twice more.
    private AppWindow? _cachedCaptionAppWindow;
    private AppWindowTitleBar? _cachedCaptionTitleBar;
    // Native inactive/active caption colors are independent of window focus.
    // DWM switches between the two palettes itself; rewriting unchanged
    // WinRT colors on every activation costs 18–26ms on this machine.
    private (Windows.UI.Color Foreground, Windows.UI.Color Inactive,
        Windows.UI.Color Hover, Windows.UI.Color Pressed)? _appliedNativeCaptionColors;
    private int _nativeCaptionColorApplyCount;
    private int _nativeCaptionColorSkipCount;

    private readonly Dictionary<string, string[]> _subOptions = new Dictionary<string, string[]>();
    private readonly Dictionary<string, Button[]> _subSelectorCache = new(StringComparer.OrdinalIgnoreCase);

    private Grid RootGrid;

    private Grid AppTitleBar;

    private StackPanel PageHost;

    private ScrollViewer _pageScroll;

    private StackPanel TopActions;

    private StackPanel ContentStack;
    private Grid _contentLayerHost;
    private readonly Queue<StackPanel> _detachedNavigationHosts = new();
    // Reuse one fully cleaned WinUI host instead of allocating a native
    // panel/composition backing object on each large-page navigation.
    private StackPanel? _recycledNavigationHost;
    // Keep the fully rendered System Tweaks tree attached to its own collapsed
    // host between section visits. Unlike child-by-child reparenting, this
    // preserves native XAML composition/virtualization state while cached.
    private StackPanel? _persistentSystemHost;
    private StackPanel? _persistentPowerHost;
    private int _powerHostIdleDetachQueued;
    private static readonly TimeSpan PowerHostIdleDetachDelay = TimeSpan.FromMilliseconds(1400);
    private int _inPlaceNavigationClears;
    private int _reusedNavigationHosts;
    private readonly Dictionary<UIElement, StackPanel> _detachedNavigationOwners = new();
    // Debounce resumes on a worker thread while cleanup runs on the UI dispatcher.
    // Atomic reservation ensures neither thread observes a stale queue flag.
    private int _detachedNavigationCleanupQueued;
    private bool TryReserveDetachedNavigationCleanup() =>
        System.Threading.Interlocked.CompareExchange(ref _detachedNavigationCleanupQueued, 1, 0) == 0;
    private void ReleaseDetachedNavigationCleanup() =>
        System.Threading.Volatile.Write(ref _detachedNavigationCleanupQueued, 0);

    private Border ActionBar;

    private TextBlock DirtyText;

    private StackPanel SubSelectorHost;

    private TextBlock PageTitle;

    private TextBlock PageSubtitle;

    private TextBlock AdminBadge;
    private bool _initialSectionNavigationCompleted;

    private async Task CleanupPendingExostenzaAfterFirstRenderAsync()
    {
        CancellationToken token = _windowLifetimeCts.Token;
        try
        {
            await Task.Delay(250, token).ConfigureAwait(false);
            string? state = await GpuDriverService.ReconcilePendingExostenzaAsync(token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(state))
                LogService.Info("drivers", "EXOSTENZA_STARTUP_RECONCILE " + state);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal window shutdown: never keep driver reconciliation alive past UI lifetime.
        }
    }

    public MainWindow()
    {
        var ctorPhase = Stopwatch.StartNew();
        Title = "Glass Optimizer";
        // Do not instantiate UISettings on the constructor hot path. The first Activated event
        // refreshes the OS animation preference immediately after the window becomes visible.
        long themeStartMs = ctorPhase.ElapsedMilliseconds;
        // Optimizer has ONE user-facing appearance: dark Glass. The legacy
        // palette system remains internal for contrast and visual regression
        // tests; old saved user theme choices must not change startup UI.
        // Read-only: do not rewrite or delete the user's old registry setting.
        _themePreference = AppThemePreference.Dark;
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
Closed += (_, _) =>
        {
            // Window cleanup must run even if persisting the last UI position fails.
            try { UiStateService.Save(new UiState(_section, _sub, "Manual")); }
            finally { CleanupWindowResources(); }
        };
        Activated += (_, e) =>
        {
            if (_windowLifetimeCts.IsCancellationRequested) return;
            bool nextTelemetryActive = e.WindowActivationState != WindowActivationState.Deactivated;
            bool activationChanged = _windowTelemetryActive != nextTelemetryActive;
            if (activationChanged)
                Interlocked.Increment(ref _liveMetricsActivationEpoch);
            _windowTelemetryActive = nextTelemetryActive;
            if (_windowTelemetryActive)
            {
                if (_section == "overview") SignalLiveMetricsWake();
                UiMotionSettingsService.RefreshSystemPreference();
            }

            // A desktop updater popup is a separate top-level window. The main Optimizer must keep
            // exactly the same glass/titlebar surfaces when it becomes inactive; otherwise DWM can
            // briefly restore an opaque inactive caption and WinUI can expose fallback card colors.
            // Repeated CodeActivated/PointerActivated events with unchanged focus
            // must not rewrite DWM/titlebar properties or rebuild 21 sidebar visuals.
            // The initial activation still primes native glass before first paint.
            if (UseGlassChrome && (activationChanged || !_customTitleBarPrimed))
            {
                ApplyWindowTitleBarTheme();
                QueueGlassActivationRepair();
            }
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
            // A usable drag region outranks sidebar/data warmup. High-priority
            // registration after Loaded keeps Window.Activate safe while allowing
            // the first mouse drag before background UI jobs consume the queue.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, ActivateCustomTitleBarNow);
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                CompleteDeferredShellBackdrop);
            SafeTaskService.Forget(
                ScheduleDeferredSidebarAfterFirstRenderAsync(),
                "performance",
                "Deferred sidebar");
            SafeTaskService.ForgetDeferred(ScheduleDeferredWindowSetupAfterFirstRenderAsync(), "performance", "Deferred window setup");
            SafeTaskService.Forget(ShowSectionAsync(_section), "navigation", "Initial section load");
            // Section-specific catalogs and control factories now warm strictly on demand.
            // Overview startup only keeps its live telemetry and genuinely required background jobs.
            SafeTaskService.ForgetDeferred(StartLiveMetricsAfterFirstRenderAsync(), "performance", "Deferred live metrics");
            SafeTaskService.ForgetDeferred(CheckUpstreamUpdatesOnStartupAsync(), "upstream", "Startup GitHub update check");
            SafeTaskService.ForgetDeferred(CheckDesktopUpdateNotificationsAsync(), "update", "Desktop glass update notifications");
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
        _cachedCaptionAppWindow = fromWindowId;
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
        // glass titlebar geometry is already reserved so the shell itself does not relayout.

        // Keep the final glass titlebar geometry visible from the first frame.
        // SetTitleBar/caption colors can wait for activation; visible title children cannot.
        AppTitleBar.Visibility = Visibility.Visible;
        HeaderCard.Margin = new Thickness(0);

        // First-frame invariant: visible custom titlebar content and final glass geometry are
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
        // First paint is already complete when this is scheduled. Yield once and let the
        // dispatcher run icon/min-size setup at low priority instead of adding a visible 650 ms lag.
        await Task.Yield();
        if (_windowLifetimeCts.IsCancellationRequested) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_windowLifetimeCts.IsCancellationRequested) return;
            CompleteDeferredWindowSetup();
            QueueGpuIntroPrewarm();
        });
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
        if (_darkTheme &&
            (Math.Abs(AppTitleBar.Height - ShellTitleBarHeight) > 0.1 ||
             Math.Abs(AppTitleBar.Padding.Left - 20d) > 0.1))
            throw new InvalidOperationException(
                $"Glass titlebar first-frame geometry mismatch: height={AppTitleBar.Height:0.##}/{ShellTitleBarHeight:0.##}, leftPadding={AppTitleBar.Padding.Left:0.##}/20.");

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
                $"Hero/titlebar seam invariant broken: heroTop={_heroImageFrame?.Margin.Top ?? -1:0.##} heroH={_heroImageFrame?.Height:0.##}/{HeaderCard.Height:0.##}.");
        // Native hit-testing must cover the hero without stealing sidebar
        // navigation or Windows caption buttons after titlebar registration.
        VerifyHeroDragRegions();

        LogService.Info("TITLEBAR_STABLE_SMOKE_OK customTransparent=true syncActivation=true stableGeometry=true heroUnderTitlebar=true");
    }

    private void ApplyWindowTitleBarTheme()
    {
        _ = Interlocked.Increment(ref _titlebarThemeGeneration);
        var sw = Stopwatch.StartNew();
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            AppWindow appWindow = _cachedCaptionAppWindow ??
                AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _cachedCaptionAppWindow = appWindow;
            var tb = _cachedCaptionTitleBar ?? appWindow.TitleBar;
            _cachedCaptionTitleBar = tb;
            var transparent = ColorHelper.FromArgb(0, 0, 0, 0);
            var titleFg = ColorHelper.FromArgb(255, _themePalette.TextPrimary.R, _themePalette.TextPrimary.G, _themePalette.TextPrimary.B);
            var inactiveFg = ColorHelper.FromArgb(255, _themePalette.TextSecondary.R, _themePalette.TextSecondary.G, _themePalette.TextSecondary.B);
            var hoverBg = ColorHelper.FromArgb(48, _themePalette.SurfaceElevated.R, _themePalette.SurfaceElevated.G, _themePalette.SurfaceElevated.B);
            var pressedBg = ColorHelper.FromArgb(76, _themePalette.Border.R, _themePalette.Border.G, _themePalette.Border.B);

            bool extensionChanged = !tb.ExtendsContentIntoTitleBar;
            if (extensionChanged)
                tb.ExtendsContentIntoTitleBar = true;

            var signature = (Foreground: titleFg, Inactive: inactiveFg,
                Hover: hoverBg, Pressed: pressedBg);
            bool colorsChanged = extensionChanged ||
                _appliedNativeCaptionColors is not { } previous ||
                !previous.Equals(signature);
            if (colorsChanged)
            {
                // DWM handles active/inactive titlebar transitions using the
                // values already set below; activation must not resend the same
                // 12 expensive native WinRT writes for an unchanged palette.
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
                _appliedNativeCaptionColors = signature;
                _nativeCaptionColorApplyCount++;
            }
            else
            {
                _nativeCaptionColorSkipCount++;
            }

            // Keep native glass/rounded-corner policy repair on a real
            // activation, even when colors are already correct.
            ApplyNativeGlassWindowShape();
            _startupTitlebarNativeApplied = false;
            _startupTitlebarThemeCompleted = true;
            LogService.Info("performance",
                $"TITLEBAR_CUSTOM_THEME_APPLY colorsChanged={colorsChanged} extended={extensionChanged} totalMs={sw.ElapsedMilliseconds}");
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
    private void ThrowIfNavigationStale(int generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration)
            throw new OperationCanceledException(token);
    }

    private void AttachCachedElement(UIElement element, bool deferHostInvalidation = false)
    {
        DependencyObject? currentParent = VisualTreeHelper.GetParent(element);
        if (ReferenceEquals(currentParent, ContentStack))
            return;

        if (currentParent is Panel parent)
        {
            // A direct parent already owns the child; avoid scanning detached hosts.
            parent.Children.Remove(element);
            _detachedNavigationOwners.Remove(element);
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

        // Cached WinUI elements can retain their previous measure slot after they are detached
        // from a taller page. Force a fresh measure so a short page (notably Overview) cannot
        // inherit the previous section's height and falsely enable scrolling.
        element.InvalidateMeasure();
        element.InvalidateArrange();
        if (!deferHostInvalidation)
        {
            ContentStack.InvalidateMeasure();
            ContentStack.InvalidateArrange();
            _contentLayerHost?.InvalidateMeasure();
            PageHost?.InvalidateMeasure();
        }
    }

    private void QueueDetachedNavigationHostCleanup(StackPanel host)
    {
        if (host.Children.Count == 0)
            return;

        // Hide old, non-interactive content immediately so a hidden heavy page
        // cannot participate in layout/composition while low-priority cleanup
        // incrementally removes its children. Cached roots still reparent safely.
        if (host.Visibility != Visibility.Collapsed)
            host.Visibility = Visibility.Collapsed;
        host.IsHitTestVisible = false;
        // Keep detach O(1): cached elements can reattach through the existing queue fallback.
        // Eagerly indexing every child here made large Startup pages charge their teardown cost
        // to the next section's foreground navigation.
        _detachedNavigationHosts.Enqueue(host);
        ScheduleDetachedNavigationHostCleanup(delayFirstFrame: true);
    }

    // Do not let a continuous stream of navigation events starve native
    // WinUI wrapper cleanup indefinitely. Always keep teardown on the
    // low-priority dispatcher; only bound the initial debounce.
    internal static bool ShouldBeginDetachedHostCleanup(TimeSpan idle, TimeSpan queuedFor, int pendingHosts)
        => idle >= TimeSpan.FromMilliseconds(140)
            || queuedFor >= TimeSpan.FromMilliseconds(420)
            || (pendingHosts >= 4 && queuedFor >= TimeSpan.FromMilliseconds(120));

    private async Task DelayDetachedNavigationHostCleanupAsync(int pendingHostsAtSchedule)
    {
        try
        {
            var queuedAt = System.Diagnostics.Stopwatch.StartNew();
            while (!_windowLifetimeCts.IsCancellationRequested)
            {
                TimeSpan idleFor = DateTime.UtcNow - _lastNavigationUtc;
                if (ShouldBeginDetachedHostCleanup(idleFor, queuedAt.Elapsed, pendingHostsAtSchedule))
                    break;

                TimeSpan remainingQuiet = TimeSpan.FromMilliseconds(140) - idleFor;
                TimeSpan remainingDeadline = TimeSpan.FromMilliseconds(420) - queuedAt.Elapsed;
                TimeSpan wait = remainingQuiet < remainingDeadline ? remainingQuiet : remainingDeadline;
                if (wait < TimeSpan.FromMilliseconds(20))
                    wait = TimeSpan.FromMilliseconds(20);
                await Task.Delay(wait, _windowLifetimeCts.Token).ConfigureAwait(false);
            }
            if (_windowLifetimeCts.IsCancellationRequested) return;

            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                ReleaseDetachedNavigationCleanup();
                // A queued WinUI callback may run after Window.Closed has invalidated native XAML peers.
                if (_windowLifetimeCts.IsCancellationRequested)
                    return;
                ScheduleDetachedNavigationHostCleanup();
            }))
                ReleaseDetachedNavigationCleanup();
        }
        catch (OperationCanceledException)
        {
            ReleaseDetachedNavigationCleanup();
        }
    }

    private void ScheduleDetachedNavigationHostCleanup(bool delayFirstFrame = false)
    {
        if (_windowLifetimeCts.IsCancellationRequested)
            return;
        if (_detachedNavigationHosts.Count == 0 || !TryReserveDetachedNavigationCleanup())
            return;
        if (delayFirstFrame)
        {
            SafeTaskService.Forget(DelayDetachedNavigationHostCleanupAsync(_detachedNavigationHosts.Count), "navigation", "Deferred detached host cleanup");
            return;
        }

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ReleaseDetachedNavigationCleanup();
            // Never dereference native Children after CleanupWindowResources cancels the window lifetime.
            if (_windowLifetimeCts.IsCancellationRequested)
                return;
            if (_detachedNavigationHosts.Count == 0)
                return;

            var host = _detachedNavigationHosts.Peek();
            int removeCount = DetachedNavigationCleanupPolicy.RemoveCount(
                host.Children.Count, _detachedNavigationHosts.Count);
            for (int i = 0; i < removeCount; i++)
            {
                int index = host.Children.Count - 1;
                var child = host.Children[index];
                _detachedNavigationOwners.Remove(child);
                host.Children.RemoveAt(index);
            }

            if (host.Children.Count == 0)
            {
                _detachedNavigationHosts.Dequeue();
                if (_contentLayerHost.Children.Contains(host))
                    _contentLayerHost.Children.Remove(host);
                if (_recycledNavigationHost == null && !ReferenceEquals(host, ContentStack))
                    _recycledNavigationHost = host;
            }

            ScheduleDetachedNavigationHostCleanup();
        }))
            ReleaseDetachedNavigationCleanup();
    }

    private void SchedulePinnedPowerHostIdleDetach()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _powerHostIdleDetachQueued, 1, 0) != 0)
            return;
        SafeTaskService.Forget(DetachPinnedPowerHostAfterQuietAsync(), "navigation", "Power host idle detach");
    }

    private async Task DetachPinnedPowerHostAfterQuietAsync()
    {
        try
        {
            await Task.Delay(PowerHostIdleDetachDelay, _windowLifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            System.Threading.Interlocked.Exchange(ref _powerHostIdleDetachQueued, 0);
            return;
        }

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            System.Threading.Interlocked.Exchange(ref _powerHostIdleDetachQueued, 0);
            if (_windowLifetimeCts.IsCancellationRequested) return;
            var pinned = _persistentPowerHost;
            if (pinned == null || ReferenceEquals(pinned, ContentStack) ||
                pinned.Visibility != Visibility.Collapsed) return;
            if (DateTime.UtcNow - _lastNavigationUtc < PowerHostIdleDetachDelay)
            {
                SchedulePinnedPowerHostIdleDetach();
                return;
            }
            if (_contentLayerHost.Children.Contains(pinned))
                _contentLayerHost.Children.Remove(pinned);
        }))
            System.Threading.Interlocked.Exchange(ref _powerHostIdleDetachQueued, 0);
    }

    // A recycled host that is still attached or re-adopted as a pinned/current host
    // would make Children.Add throw COMException 0x800F1000 (element already has a parent).
    private StackPanel TakeRecycledNavigationHost(out bool recycled)
    {
        var host = _recycledNavigationHost;
        _recycledNavigationHost = null;
        recycled = host != null &&
            host.Parent == null &&
            host.Children.Count == 0 &&
            !ReferenceEquals(host, ContentStack) &&
            !ReferenceEquals(host, _persistentPowerHost) &&
            !ReferenceEquals(host, _persistentSystemHost);
        return recycled ? host! : new StackPanel { Spacing = 0 };
    }

    // Reusing a host formerly collapsed for native cleanup must explicitly
    // restore Visibility. Opacity=1 alone does not make WinUI layout it.
    internal static void ActivateRecycledNavigationHost(StackPanel host)
    {
        if (host.Children.Count != 0)
            throw new InvalidOperationException("Recycled navigation host was not empty.");
        host.Visibility = Visibility.Visible;
        host.IsHitTestVisible = true;
        host.Opacity = 1;
    }

    private void ClearNavigationHostsFast(string previousSection, string nextSection)
    {
        // Power's cache TTL is shorter than the periodic global trim. Drop an
        // expired pinned native tree before any new Power host can replace it.
        if (_persistentPowerHost != null && !IsPowerPageCacheFresh())
            InvalidatePowerPageCache();

        if (string.Equals(previousSection, "system", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(nextSection, "system", StringComparison.OrdinalIgnoreCase) &&
            _systemPageCache is { Count: 1 } && _systemVirtualRowsCache != null &&
            ReferenceEquals(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(_systemPageCache[0]), ContentStack) &&
            ContentStack.Children.Count == 1)
        {
            // Keep the existing cached WinUI tree in the visual layer, but
            // collapsed and not hit-testable. No element detachment takes place.
            var systemHost = ContentStack;
            systemHost.IsHitTestVisible = false;
            systemHost.Visibility = Visibility.Collapsed;
            _persistentSystemHost = systemHost;
            ContentStack = TakeRecycledNavigationHost(out bool recycled);
            if (ContentStack.Children.Count != 0)
                throw new InvalidOperationException("Recycled navigation host was not empty.");
            ContentStack.Visibility = Visibility.Visible;
            ContentStack.IsHitTestVisible = true;
            _contentLayerHost.Children.Add(ContentStack);
            if (recycled) _reusedNavigationHosts++;
        }
        else if (string.Equals(previousSection, "power", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(nextSection, "power", StringComparison.OrdinalIgnoreCase) &&
            CanParkPowerPageCache())
        {
            var powerHost = ContentStack;
            powerHost.IsHitTestVisible = false;
            powerHost.Visibility = Visibility.Collapsed;
            _persistentPowerHost = powerHost;
            // Keep the loaded tree during rapid navigation; only detach the
            // complete host when the user has been idle long enough.
            SchedulePinnedPowerHostIdleDetach();
            ContentStack = TakeRecycledNavigationHost(out bool recycled);
            if (ContentStack.Children.Count != 0)
                throw new InvalidOperationException("Recycled navigation host was not empty.");
            ContentStack.Visibility = Visibility.Visible;
            ContentStack.IsHitTestVisible = true;
            _contentLayerHost.Children.Add(ContentStack);
            if (recycled) _reusedNavigationHosts++;
        }
        else if (ContentStack.Children.Count > 0)
        {
            // Most compact sections have few/lightweight roots, so clear
            // them in place. GPU is the exception: its three/four direct
            // roots contain a deep native stats + driver visual tree, and
            // clearing only those few roots synchronously was observed at
            // 61ms during GPU cancellation navigation. Reuse the existing
            // deferred host-cleanup path for GPU regardless of root count.
            if (ContentStack.Children.Count <= 8 &&
                !string.Equals(previousSection, "gpu", StringComparison.OrdinalIgnoreCase))
            {
                ContentStack.Children.Clear();
                _inPlaceNavigationClears++;
            }
            else
            {
                // Heavy pages keep the deferred first-frame teardown so
                // navigation remains responsive. Reuse a previously cleaned
                // host when possible, never a host that still owns children.
                var oldContent = ContentStack;
                oldContent.IsHitTestVisible = false;
                oldContent.Opacity = 0;

                var replacement = TakeRecycledNavigationHost(out bool recycled);
                ActivateRecycledNavigationHost(replacement);
                _contentLayerHost.Children.Add(replacement);
                ContentStack = replacement;
                if (recycled) _reusedNavigationHosts++;
                QueueDetachedNavigationHostCleanup(oldContent);
            }
        }

        if (TopActions.Children.Count > 0)
            TopActions.Children.Clear();
        if (SubSelectorHost.Children.Count > 0)
            SubSelectorHost.Children.Clear();
        if (SubSelectorHost.Visibility != Visibility.Collapsed)
            SubSelectorHost.Visibility = Visibility.Collapsed;

        if (string.Equals(nextSection, "system", StringComparison.OrdinalIgnoreCase) &&
            _persistentSystemHost != null && _systemPageCache is { Count: 1 } &&
            _systemVirtualRowsCache != null && _systemVirtualMapCache != null)
        {
            // The ordinary navigation path has already emptied or replaced
            // ContentStack. Recycle that empty host, then reveal the existing
            // system visual tree without removing or reattaching its children.
            var unusedHost = ContentStack;
            if (unusedHost.Children.Count != 0)
                throw new InvalidOperationException("Navigation host not empty before pinned system reuse.");
            if (_contentLayerHost.Children.Contains(unusedHost))
                _contentLayerHost.Children.Remove(unusedHost);
            if (_recycledNavigationHost == null)
                _recycledNavigationHost = unusedHost;
            ContentStack = _persistentSystemHost;
            ContentStack.Visibility = Visibility.Visible;
            ContentStack.IsHitTestVisible = true;
        }
        else if (string.Equals(nextSection, "power", StringComparison.OrdinalIgnoreCase) &&
            _persistentPowerHost != null && IsPowerPageCacheFresh())
        {
            var unusedHost = ContentStack;
            if (unusedHost.Children.Count != 0)
                throw new InvalidOperationException("Navigation host not empty before pinned power reuse.");
            // The pinned host itself may be the current (in-place cleared) host; never recycle it.
            if (!ReferenceEquals(unusedHost, _persistentPowerHost))
            {
                if (_contentLayerHost.Children.Contains(unusedHost))
                    _contentLayerHost.Children.Remove(unusedHost);
                if (_recycledNavigationHost == null)
                    _recycledNavigationHost = unusedHost;
            }
            ContentStack = _persistentPowerHost;
            if (!_contentLayerHost.Children.Contains(ContentStack))
                _contentLayerHost.Children.Add(ContentStack);
            ContentStack.Visibility = Visibility.Visible;
            ContentStack.IsHitTestVisible = true;
        }
    }

    // Overview is the first visible section. Reading the full tweak catalog
    // merely to obtain its immutable label/icon can initialize unrelated
    // source indexes on the WinUI dispatcher. Keep the Overview metadata
    // locally until another section actually needs CatalogService.
    private static readonly SectionDefinition StartupOverviewSection = new(
        "overview", "Přehled", "Stav systému a rychlé profily", "\uE80F");
    // System's immutable metadata is needed before its tweak catalog. Using
    // this value-equal record avoids making the UI wait for CatalogService's
    // first static initialization during the navigation shell phase.
    private static readonly SectionDefinition StartupSystemSection = new(
        "system", "Systémové tweaky", "Windows, boot a virtualizace", "\uE770");

    private async Task ShowSectionCoreAsync(string key, int generation, CancellationToken token)
    {
        var navPhase = Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration) throw new OperationCanceledException(token);
        string previousSection = _section;
        CleanupForNavigation(key);
        long cleanupMs = navPhase.ElapsedMilliseconds;
        _section = key;
        if (key == "overview" && _windowTelemetryActive)
            SignalLiveMetricsWake();
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
        ClearNavigationHostsFast(previousSection, key);
        long clearMs = navPhase.ElapsedMilliseconds;
        SectionDefinition sectionDefinition = key == "overview"
            ? StartupOverviewSection
            : key == "system"
                ? StartupSystemSection
            : key == "components"
                ? new SectionDefinition(
                    "components",
                    _sub ?? "Služby",
                    _sub switch
                    {
                        "Ovladače" => "Správa ovladačů a kompatibility zařízení",
                        "Task Scheduler" => "Plánované úlohy a doporučené nastavení",
                        _ => "Bezpečné doporučené nastavení služeb"
                    },
                    "\uE713")
                : CatalogService.GetSection(key);
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
        // The navigation selection changed SubNav visibility. Apply the final layout once,
        // including the target hero height; the previous flow could do two full passes.
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
            if (Array.IndexOf(value, _sub) < 0)
            {
                _sub = value[0];
            }
            bool deferComponentsServiceSelector =
                key == "components" && string.Equals(_sub, "Služby", StringComparison.Ordinal);
            if (!deferComponentsServiceSelector)
            {
                BuildSubSelector(key, value);
            }
            else
            {
                int selectorGeneration = generation;
                string[] selectorOptions = value;
                SafeTaskService.Forget(
                    CompleteDeferredComponentsSelectorAsync(selectorOptions, selectorGeneration, token),
                    "components",
                    "Deferred Components selector");
            }
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
            long beforeOverviewMs = navPhase.ElapsedMilliseconds;
            await BuildOverviewDashboardAsync(token);
            long overviewMs = navPhase.ElapsedMilliseconds - beforeOverviewMs;
            if (LogService.PerformanceDiagnosticsEnabled)
                LogService.Info("performance",
                    $"NAV_OVERVIEW_BUILD preludeMs={beforeOverviewMs} overviewMs={overviewMs} coreMs={navPhase.ElapsedMilliseconds} cache={_overviewPageCache?.Count ?? 0}");
            return;
        }
        if (key == "power")
        {
            await BuildPowerPlanAsync(token);
            return;
        }
        if (key == "debloat")
        {
            _debloatActionToken = token;
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
        if (key == "themes")
        {
            if (!TryAttachThemesPageCache())
            {
                BuildThemesPage();
                CaptureThemesPageCache();
            }
            return;
        }
        if (key == "powershell")
        {
            await BuildPowerShellPageAsync(token);
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
                if (startupNavPerf.ElapsedMilliseconds >= 8)
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
            if (startupNavPerf.ElapsedMilliseconds >= 10)
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
            string componentSub = _sub ?? "Služby";
            if (string.Equals(componentSub, "Služby", StringComparison.OrdinalIgnoreCase))
            {
                _tweaks = EmptyComponentTweaks;
                if (_componentsPageCacheStore != null && TryAttachComponentsPageCache("Služby", token))
                    return;

                await BuildServiceManagerPageAsync(token);
                ThrowIfNavigationStale(generation, token);
                SafeTaskService.Forget(
                    CompleteDeferredServiceChromeAsync(generation, token),
                    "components",
                    "Deferred service chrome/cache");
                return;
            }

            var componentPerf = Stopwatch.StartNew();
            string componentCacheKey = "components|" + componentSub;
            if (!_tweakCache.TryGetValue(componentCacheKey, out _tweaks))
                _tweakCache[componentCacheKey] = _tweaks = CatalogService.GetTweaks("components", componentSub);
            long catalogPhaseMs = componentPerf.ElapsedMilliseconds;

            if (TryAttachComponentsPageCache(componentSub, token))
            {
                if (componentPerf.ElapsedMilliseconds >= 8)
                    LogService.Info("performance", $"COMPONENT_NAV_PHASE sub={componentSub} cacheHit=true catalogMs={catalogPhaseMs} totalMs={componentPerf.ElapsedMilliseconds}");
                return;
            }
            long cachePhaseMs = componentPerf.ElapsedMilliseconds - catalogPhaseMs;
            BuildComponentsContextActions();
            long contextPhaseMs = componentPerf.ElapsedMilliseconds - catalogPhaseMs - cachePhaseMs;
            long buildStart = componentPerf.ElapsedMilliseconds;
            if (componentSub == "Ovladače")
                await BuildDriverManagerPageAsync(token);
            else
                await BuildTaskManagerPageAsync(token);
            ThrowIfNavigationStale(generation, token);
            long buildPhaseMs = componentPerf.ElapsedMilliseconds - buildStart;

            long captureStart = componentPerf.ElapsedMilliseconds;
            CaptureComponentsPageCache(componentSub);
            long capturePhaseMs = componentPerf.ElapsedMilliseconds - captureStart;
            if (componentPerf.ElapsedMilliseconds >= 10)
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
        if (key == "gpu")
        {
            long gpuStartMs = navPhase.ElapsedMilliseconds;
            const string gpuCacheKey = "gpu|";
            SynchronizeGpuDetectionGeneration();
            bool gpuStale = !_tweakCacheTime.TryGetValue(gpuCacheKey, out var gpuDetectedAt) ||
                DateTime.UtcNow - gpuDetectedAt > TimeSpan.FromSeconds(90);

            if (_tweakCache.TryGetValue(gpuCacheKey, out var cachedGpuTweaks))
                _tweaks = cachedGpuTweaks;
            else
                _tweaks = EmptyComponentTweaks;

            if (TryAttachGpuPageCache(token))
            {
                // Expired tweak detection is independent of the cached GPU page.
                // Keep its WinUI tree, and re-read states in the background.
                if (gpuStale)
                    SafeTaskService.Forget(
                        RunGpuDeferredStateAfterSettleAsync(gpuCacheKey, _tweaks.ToArray(), true, token, generation),
                        "gpu", "Refresh stale GPU tweaks without rebuilding page");
                return;
            }
            if (gpuStale) InvalidateGpuPageCache();
            long gpuPrepareMs = navPhase.ElapsedMilliseconds;

            BuildGpuDriverCenter(token);
            long gpuBuildMs = navPhase.ElapsedMilliseconds;
            ThrowIfNavigationStale(generation, token);
            ScheduleGpuActions(token);
            SafeTaskService.Forget(
                CompleteGpuTweaksAfterFirstFrameAsync(gpuCacheKey, gpuStale, token, generation),
                "gpu",
                "Deferred GPU catalog + tweak UI");
            long gpuEndMs = navPhase.ElapsedMilliseconds;
            if (gpuEndMs - gpuStartMs >= 15 || gpuEndMs >= 35)
                LogService.Info("performance", $"GPU_NAV_DIAG preludeMs={gpuStartMs} prepareMs={gpuPrepareMs - gpuStartMs} buildMs={gpuBuildMs - gpuPrepareMs} deferredMs={gpuEndMs - gpuBuildMs} totalMs={gpuEndMs} stale={gpuStale}");
            return;
        }
        Stopwatch? systemForeground = key == "system" ? Stopwatch.StartNew() : null;
        Stopwatch? storageCatalogPhase = key == "storage" ? Stopwatch.StartNew() : null;
        string tweakCacheKey = key + "|" + (_sub ?? "");
        // First System navigation needs only the empty virtual host. Construct
        // catalog definitions in the existing LOW-priority root callback after
        // foreground has returned, not synchronously on WinUI and not in a
        // racing worker whose scheduling can delay the 35ms first-paint gate.
        bool deferSystemCatalog = key == "system" && _systemPageCache == null &&
            !_tweakCache.ContainsKey("system|");
        if (deferSystemCatalog)
        {
            _tweaks = EmptyComponentTweaks;
        }
        else
        {
            if (!_tweakCache.TryGetValue(tweakCacheKey, out var cachedTweaks))
            {
                cachedTweaks = CatalogService.GetTweaks(key, _sub);
                _tweakCache[tweakCacheKey] = cachedTweaks;
            }
            _tweaks = key == "system" ? GlassSystemTweaks(cachedTweaks) : cachedTweaks;
        }
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
            bool uiStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var uiDetectedAt) ||
                DateTime.UtcNow - uiDetectedAt > TimeSpan.FromSeconds(90);
            if (uiStale)
                ScheduleRenderFirstBackground("ui", tweakCacheKey, _tweaks.ToArray(), true, token);
            return;
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
            if (TryAttachStoragePageCache(token))
            {
                // Refresh expired tweak states without discarding a healthy
                // storage UI. The cache attach already refreshes stale disk
                // health/advanced panels and resumes deferred content.
                if (storageStale)
                    SafeTaskService.Forget(
                        RunStorageDeferredStateAfterSettleAsync(tweakCacheKey, _tweaks.ToArray(), true, token, generation),
                        "storage", "Refresh stale storage tweaks without rebuilding page");
                return;
            }
            if (storageStale) InvalidateStoragePageCache();

            AttachStorageStaticShell(token, out long storageHeroMs, out long storageMetricsMs);

            BuildStorageActions(token);
            ScheduleStorageOptimizationAdvisor(token);
            long storageActionsMs = storageForeground.ElapsedMilliseconds - storageHeroMs - storageMetricsMs;
            ScheduleStorageTools(token);
            long storageScheduleMs = storageForeground.ElapsedMilliseconds - storageHeroMs - storageMetricsMs - storageActionsMs;
            // Repeated cached navigation commonly takes 0ms. Retain actionable
            // slow-path diagnostics without flooding performance-smoke output.
            if (storageForeground.ElapsedMilliseconds >= 5)
                LogService.Info("performance",
                    $"STORAGE_FOREGROUND_PHASE preStorageMs={storagePreMs} catalogMs={storageCatalogMs} heroMs={storageHeroMs} metricsMs={storageMetricsMs} actionsMs={storageActionsMs} scheduleMs={storageScheduleMs} totalMs={storageForeground.ElapsedMilliseconds}");
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
            if (networkBuild.ElapsedMilliseconds >= 10)
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
            if (storageBuild.ElapsedMilliseconds >= 5)
                LogService.Info("performance", $"STORAGE_BUILD kickoffMs={kickoffMs} snapshotMs={snapshotMs} toolsMs=0 totalMs={storageBuild.ElapsedMilliseconds}");
        }
        if (key == "update")
        {
            if (TryAttachUpdatePageCache(token))
                return;
            await BuildWindowsUpdateCenterAsync(token);
            ThrowIfNavigationStale(generation, token);
        }
        if (key == "ui")
        {
            var uiBuild = Stopwatch.StartNew();
            BuildContextMenuTweaker();
            if (uiBuild.ElapsedMilliseconds >= 10)
                LogService.Info("performance",
                    $"UI_BUILD_PHASE contextMenuMs={uiBuild.ElapsedMilliseconds} legacyExtras=false totalMs={uiBuild.ElapsedMilliseconds}");
        }
        var renderTailPerf = Stopwatch.StartNew();
        bool renderFirst = key is "system" or "privacy" or "network" or "memory" or "ui" or "storage" or "update";
        // All render-first sections defer tweak detection and its immediate/lazy
        // partition to the low-priority dispatcher. Avoid two useless LINQ
        // enumerations + arrays on the cold foreground navigation hot path.
        TweakDefinition[] lazyTweaks = renderFirst ? Array.Empty<TweakDefinition>() : _tweaks.Where(x => x.LazyDetect).ToArray();
        TweakDefinition[] immediateTweaks = renderFirst ? Array.Empty<TweakDefinition>() : _tweaks.Where(x => !x.LazyDetect).ToArray();
        long splitTweaksMs = renderTailPerf.ElapsedMilliseconds;
        bool staleDetection = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var detectedAt) || DateTime.UtcNow - detectedAt > TimeSpan.FromSeconds(90);
        if (renderFirst)
        {
            var phase = Stopwatch.StartNew();
            if (key == "network")
            {
                _networkTweakHost = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
                ContentStack.Children.Add(_networkTweakHost);
                ScheduleNetworkTweakCards(token);
            }
            else if (key == "storage")
            {
                _storageTweakHost = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
                ContentStack.Children.Add(_storageTweakHost);
                ScheduleStorageTweakCards(token);
            }
            else if (key == "system")
            {
                // Only an empty host exists before the first deferred System
                // render. Attach it in that same Low-priority callback so a
                // cold native visual-tree insertion cannot block foreground
                // navigation during a rapid multi-section smoke/user tour.
                _systemVirtualHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                ScheduleSystemVirtualCards(token);
            }
            else
            {
                BuildTweakCards();
            }
            long cardsMs = phase.ElapsedMilliseconds;
            if (key == "system" && systemForeground != null && systemForeground.ElapsedMilliseconds >= 12)
                LogService.Info("performance", $"SYSTEM_FOREGROUND_PHASE catalogMs={systemCatalogMs} preRenderMs={systemForeground.ElapsedMilliseconds - systemCatalogMs - cardsMs} cardsMs={cardsMs} totalMs={systemForeground.ElapsedMilliseconds}");
            if (key == "network") CaptureNetworkPageCache();
            if (key == "update") CaptureUpdatePageCache();
            if (key == "system" && _systemVirtualRowsCache != null) CaptureSystemPageCache();
            if (key == "privacy") CapturePrivacyPageCache();
            if (key == "ui") CaptureUiPageCache();
            // Zero-ms navigation must not queue two performance.log writes.
            // Slow paths still retain both diagnostics for investigation.
            long tailMs = renderTailPerf.ElapsedMilliseconds;
            if (cardsMs >= 5 || tailMs >= 12)
            {
                if (key is "network" or "storage" or "gpu")
                    LogService.Info("performance", $"RENDER_TAIL_PHASE section={key} splitMs={splitTweaksMs} cardsMs={cardsMs} totalMs={tailMs}");
                LogService.Info("performance", $"TWEAK_UI_PHASE section={key} count={_tweaks.Count} cardsMs={cardsMs}");
            }
            if (key == "storage")
            {
                int storageDeferredGeneration = generation;
                TweakDefinition[] storageDeferredTweaks = _tweaks.ToArray();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (token.IsCancellationRequested || storageDeferredGeneration != _navigationGeneration || _section != "storage") return;
                    SafeTaskService.Forget(RunStorageDeferredStateAfterSettleAsync(tweakCacheKey, storageDeferredTweaks, staleDetection, token, storageDeferredGeneration), "storage", "Deferred storage state");
                });
            }
            else if (!deferSystemCatalog)
            {
                // A deferred first-System catalog schedules its own state
                // detection after the root has its real 57 model definitions.
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
            // Read-only detection is shared work: finish it even when navigation leaves the section,
            // but never outlive the application window.
            await _engine.DetectAsync(tweaks, _windowLifetimeCts.Token);
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
            " mtu=" + (wm.ActiveMtu?.ToString() ?? "n/a") +
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


    private static List<UIElement> SnapshotChildren(StackPanel source)
    {
        int count = source.Children.Count;
        var snapshot = new List<UIElement>(count);
        for (int i = 0; i < count; i++)
            snapshot.Add(source.Children[i]);
        return snapshot;
    }

    private async Task CompleteDeferredServiceChromeAsync(int generation, CancellationToken token)
    {
        await Task.Delay(180, token).ConfigureAwait(false);
        if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "components" || _sub != "Služby")
            return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "components" || _sub != "Služby") return;
            BuildComponentsContextActions();
            BuildWindowsModernizerVendorPanel(token);
            CaptureComponentsPageCache("Služby");
        });
    }

    private async Task CompleteDeferredComponentsSelectorAsync(string[] options, int generation, CancellationToken token)
    {
        await Task.Delay(60, token).ConfigureAwait(false);
        if (token.IsCancellationRequested || generation != _navigationGeneration) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "components" || _sub != "Služby") return;
            BuildSubSelector("components", options);
        });
    }

    private void BuildSubSelector(string section, string[] options)
    {
        SubSelectorHost.Visibility = Visibility.Visible;
        if (!_subSelectorCache.TryGetValue(section, out var buttons))
        {
            buttons = new Button[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                string opt = options[i];
                var button = new Button { Content = opt, Tag = opt };
                ApplyPillButtonAppearance(button);
                button.Click += delegate
                {
                    SafeTaskService.Forget(NavigateSubSectionFromUiAsync(section, opt), "navigation", "Open sub-section " + opt);
                };
                buttons[i] = button;
            }
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
        var groups = new Dictionary<string, List<TweakDefinition>>();
        var order = new List<string>();
        foreach (TweakDefinition tweak in _tweaks)
        {
            if (!groups.TryGetValue(tweak.Group, out List<TweakDefinition>? items))
            {
                items = new List<TweakDefinition>();
                groups[tweak.Group] = items;
                order.Add(tweak.Group);
            }
            items.Add(tweak);
        }

        foreach (string groupName in order)
        {
            List<TweakDefinition> items = groups[groupName];
            host.Children.Add(CreateClassicTweakGroupHeader(groupName, items.Count));
            foreach (TweakDefinition tweak in items)
                host.Children.Add(CreateClassicTweakCard(tweak));
        }
    }

    private Border CreateClassicTweakGroupHeader(string groupName, int count)
    {
        // Group name and count are separated for scanning; no legacy violet rail,
        // uppercase wall of text or extra bordered card around each group.
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = groupName,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var counter = new TextBlock
        {
            Text = $"{count} nastavení",
            FontSize = 11,
            Foreground = TextSecondaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(counter, 1);
        row.Children.Add(counter);
        return new Border
        {
            Tag = "tweak-group-heading",
            Margin = new Thickness(0, 9, 0, 4),
            Padding = new Thickness(5, 6, 5, 7),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = SectionBorderBrush(),
            Background = null,
            Child = row
        };
    }

    private Border CreateClassicTweakCard(TweakDefinition tweak)
    {
        tweak.PropertyChanged -= Tweak_PropertyChanged;
        tweak.PropertyChanged += Tweak_PropertyChanged;
        return CreateTweakCard(tweak);
    }


    // Headings may also be grid-backed after the UI rework. Count only
    // actual tweak models, never arbitrary visual shapes or header containers.
    private static int CountClassicTweakCards(Panel? host) =>
        host?.Children.OfType<Border>().Count(x => x.Tag is TweakDefinition) ?? 0;

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
        bool NeutralChrome = false)
    {
        // Counts this card's actual user-toggle writebacks; unlike the global
        // dirty-refresh flag, unrelated tweak detection cannot change it.
        public int UserWritebacks { get; set; }
    }

    private Border CreateTweakCard(TweakDefinition t)
    {
        bool neutralChrome = UseNeutralSectionChrome;
        var policy = TweakPolicyService.Evaluate(t, _tweakViewMode);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconBox = new Border { Width = UseCompactSectionChrome ? 34 : 42, Height = UseCompactSectionChrome ? 34 : 42, CornerRadius = new CornerRadius(8), Background = _darkTheme ? GlassSurfaceLowBrush() : SurfaceAltBrush(), BorderThickness = new Thickness(0), Child = SectionIcon(t.Section, UseCompactSectionChrome ? 25 : 31), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(iconBox);
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        // Keep status, risk and restart markers aligned without pushing the
        // whole tweak name off-screen on a narrower section viewport.
        var titleRow = new Grid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = t.Name, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap,
            MaxLines = 2, VerticalAlignment = VerticalAlignment.Center
        });
        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        var uiState = TweakUiState(t);
        var stateText = uiState.Text;
        var stateBrush = uiState.Resource;
        var stateBadge = Badge(stateText, stateBrush);
        badges.Children.Add(stateBadge);
        var verdict = Badge(policy.Verdict, policy.Verdict == "Recommended" ? "SpankySuccessBrush" : policy.Verdict == "Advanced" || policy.Verdict == "Needs attention" ? "SpankyWarningBrush" : "SpankyInfoBrush");
        verdict.Tag = "policyVerdict"; badges.Children.Add(verdict);
        if (t.RestartRequired) badges.Children.Add(Badge("Restart", "SpankyInfoBrush"));
        Grid.SetColumn(badges, 1); titleRow.Children.Add(badges);
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock { Text = t.Description, FontSize = UseCompactSectionChrome ? 12.5 : 12, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap, MaxLines = UseCompactSectionChrome ? 0 : 2, TextTrimming = UseCompactSectionChrome ? TextTrimming.None : TextTrimming.CharacterEllipsis, MaxWidth = 1100 });
        var meta = new TextBlock { Text = _tweakViewMode == TweakViewMode.Diagnostics || !t.IsLoaded ? policy.Detail : TweakVerificationMeta(t), FontSize = 10.8, Foreground = TextSecondaryBrush(), Tag = "meta", TextWrapping = TextWrapping.Wrap };
        text.Children.Add(meta);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var toggleState = new TextBlock { Text = t.Pending ? "ON" : "OFF", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ClassicToggleStateBrush(t.Pending, neutralChrome), VerticalAlignment = VerticalAlignment.Center, Tag = "toggleState", Margin = new Thickness(0, 0, 3, 0) };
        Grid.SetColumn(toggleState, 2); grid.Children.Add(toggleState);
        var toggle = new ToggleSwitch { IsOn = t.Pending, Tag = t, IsEnabled = policy.CanEdit && !t.IsBusy, VerticalAlignment = VerticalAlignment.Center, OffContent = "", OnContent = "", MinWidth = 50 };
        NormalizeToggleAppearance(toggle);
        AutomationProperties.SetName(toggle, t.Name);
        toggle.Toggled += delegate
        {
            var live = TweakPolicyService.Evaluate(t, _tweakViewMode);
            bool editable = live.CanEdit && t.IsLoaded && !t.IsBusy;
            if (!editable && toggle.IsOn != t.Pending)
            {
                // Do not let a denied or in-flight edit leave a false visual
                // ON/OFF state. The second Toggled callback sees equality
                // and does not write to the model.
                toggle.IsOn = t.Pending;
            }
            else if (editable && t.Pending != toggle.IsOn)
            {
                t.Pending = toggle.IsOn;
                if (grid.Tag is ClassicTweakCardRefs refs)
                    refs.UserWritebacks++;
                QueueDirtyStateRefresh();
            }

            string actual = t.Pending ? "ON" : "OFF";
            if (!string.Equals(toggleState.Text, actual, StringComparison.Ordinal))
                toggleState.Text = actual;
            toggleState.Foreground = ClassicToggleStateBrush(t.Pending, neutralChrome);
        };
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
        var row = new Border { Tag = t, Background = UseCompactSectionChrome ? (_darkTheme ? GlassSurfaceLowBrush() : SurfaceAltBrush()) : SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = UseCompactSectionChrome ? new Thickness(0,0,0,1) : new Thickness(1), CornerRadius = new CornerRadius(0), Margin = new Thickness(0, 0, 0, UseCompactSectionChrome ? 0 : 7), Padding = UseCompactSectionChrome ? new Thickness(12,16,12,16) : new Thickness(14, 10, 14, 10), Child = grid };
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
        badge.Background = neutral ? (_darkTheme ? GlassSurfaceLowBrush() : ThemeColor(245,245,245)) : semantic;
        if (badge.Child is TextBlock label)
            label.Foreground = neutral ? semantic : OnAccentTextBrush();
    }

    private static Border Badge(string text, string resource)
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(7.0), Opacity = 0.88,
            Padding = new Thickness(7.0, 3.0, 7.0, 3.0),
            Child = new TextBlock { Text = text, FontSize = 10.5 }
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
        // Detached cached pages are not visible, and touching their WinUI
        // controls makes every System/Privacy toggle also refresh unrelated
        // NIC, GPU and storage cards. Those sections refresh on cache attach
        // via RefreshCardsIfTweakRevisionChanged.
        if (_section == "network" && _networkTweakHost is { } networkHost &&
            ContentStack.Children.Contains(networkHost))
        {
            foreach (Border item in networkHost.Children.OfType<Border>())
                RefreshClassicTweakCard(item);
        }
        if (_section == "gpu" && _gpuTweakHost is { } gpuHost &&
            ContentStack.Children.Contains(gpuHost))
        {
            foreach (Border item in gpuHost.Children.OfType<Border>())
                RefreshClassicTweakCard(item);
        }
        if (_section == "storage" && _storageTweakHost is { } storageHost &&
            ContentStack.Children.Contains(storageHost))
        {
            foreach (Border item in storageHost.Children.OfType<Border>())
                RefreshClassicTweakCard(item);
        }
    }

    private void UpdateDirtyState()
    {
        int num = _tweaks.Count((TweakDefinition x) => x.IsDirty);
        if (num <= 0)
        {
            if (_actionBarInitialized && ActionBar.Visibility != Visibility.Collapsed)
            {
                ActionBar.Visibility = Visibility.Collapsed;
                UpdateRowlessShellGeometry();
            }
            return;
        }

        EnsureActionBarInitialized();
        // Most toggle edits leave the action bar visible. Recalculating the
        // entire WinUI shell geometry on every row change causes unnecessary
        // layout work; only a true visibility transition needs that update.
        if (ActionBar.Visibility != Visibility.Visible)
        {
            ActionBar.Visibility = Visibility.Visible;
            UpdateRowlessShellGeometry();
        }
        string countText = num == 1
            ? "1 nevyřízená změna" : $"{num} nevyřízených změn";
        if (!string.Equals(DirtyText.Text, countText, StringComparison.Ordinal))
            DirtyText.Text = countText;
    }

    // Guard the confirmation AND the actual Windows apply as one operation.
    // The progress-dialog gate alone begins too late: two quick clicks
    // could otherwise open duplicate confirmations for the same batch.
    private int _tweakApplyInProgress;

    private void Apply_Click(object sender, RoutedEventArgs e) =>
        SafeTaskService.Forget(ApplyClickCoreAsync(), "tweaks", "Apply selected tweaks");

    private async Task ApplyClickCoreAsync()
    {
        if (Interlocked.Exchange(ref _tweakApplyInProgress, 1) != 0)
            return;
        try
        {
        List<TweakDefinition> selectedCatalog = _tweaks;
        int confirmationGeneration = _navigationGeneration;
        var dirty = selectedCatalog.Where(x => x.IsDirty).ToList();
        if (dirty.Count == 0) return;
        var originalSelection = dirty
            .Select(x => (Tweak: x, x.Pending, x.Current)).ToArray();

        // A confirmation may be open while a background Windows refresh or
        // navigation changes the models. Never apply a DIFFERENT batch from
        // the one the user reviewed.
        bool SelectionStillCurrent() =>
            confirmationGeneration == _navigationGeneration &&
            ReferenceEquals(_tweaks, selectedCatalog) &&
            selectedCatalog.Count(x => x.IsDirty) == originalSelection.Length &&
            originalSelection.All(x =>
                x.Tweak.IsLoaded && !x.Tweak.IsBusy && x.Tweak.IsDirty &&
                x.Tweak.Pending == x.Pending && x.Tweak.Current == x.Current);

        if (!await ShowTweakApplyConfirmationAsync(dirty)) return;
        if (!SelectionStillCurrent())
        {
            ShowActionFeedback("Tweaky", ActionFeedbackKind.Failed,
                "Výběr změn se mezitím změnil. Zkontroluj jej a potvrď znovu.");
            return;
        }
        if (dirty.Any(x => x.RestartRequired || x.Risk == TweakRisk.Advanced))
        {
            await WindowsActions.EnsureRecentRestorePointAsync("Spankyho Optimizer - tweak batch");
            if (!SelectionStillCurrent())
            {
                ShowActionFeedback("Tweaky", ActionFeedbackKind.Failed,
                    "Stav změn se mezitím změnil. Potvrď aktuální výběr znovu.");
                return;
            }
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
            bool stateRechecked = await ReconcileCancelledApplyAsync(dirty, "global");
            RefreshCards(force: true);
            QueueDirtyStateRefresh();
            if (!stateRechecked && confirmationGeneration == _navigationGeneration &&
                ReferenceEquals(_tweaks, selectedCatalog))
                ShowActionFeedback("Tweaky", ActionFeedbackKind.Failed,
                    "Dávka byla přerušena, ale skutečný stav některých položek se nepodařilo ověřit.");
            return;
        }
        IReadOnlyList<string> readOnlyList = progressResult.Result;
        Exception? finalReadBackError = null;
        try
        {
            // Read the real Windows state after Apply, without discarding a
            // NEW Pending choice staged while the native batch was in flight.
            // DetectAsync reconciles an unchanged Pending with actual state;
            // a newer dirty choice stays staged for the user's next Apply.
            var immediateReadBack = dirty.Where(x => !x.RestartRequired).ToArray();
            if (immediateReadBack.Length > 0)
            {
                await _engine.DetectAsync(immediateReadBack);
                if (immediateReadBack.Any(x => !x.IsLoaded))
                    finalReadBackError = new InvalidOperationException(
                        "U některých změn nelze načíst skutečný stav Windows.");
                else
                {
                    // Re-detection can succeed but still find that Windows
                    // reverted a value after the engine's own verification.
                    // Compare with the USER-CONFIRMED expected values, not
                    // Pending (which a detect pass intentionally refreshes).
                    var mismatches = originalSelection
                        .Where(x => !x.Tweak.RestartRequired &&
                                    x.Tweak.Current != x.Pending)
                        .Select(x => x.Tweak.Name).Take(3).ToArray();
                    if (mismatches.Length != 0)
                        finalReadBackError = new InvalidOperationException(
                            "Windows nepotvrdil požadované hodnoty: " +
                            string.Join(", ", mismatches));
                }
            }
        }
        catch (Exception ex)
        {
            finalReadBackError = ex;
            LogService.Error("tweaks", "Post-apply final detect failed", ex);
        }

        if (dirty.Any(x => x.Section == "storage")) InvalidateStoragePageCache();
        if (dirty.Any(x => x.Section == "memory")) InvalidateMemoryRecommendedPanel();
        if (dirty.Any(x => x.Section == "defender")) InvalidateDefenderPageCache();
        if (dirty.Any(x => x.Section == "components")) InvalidateComponentsPageCache();
        if (dirty.Any(x => x.Section is "memory" or "system")) MemoryStateService.Invalidate();
        RefreshCards(force: true);
        QueueDirtyStateRefresh();
        if (readOnlyList.Count > 0)
        {
            ShowActionFeedback("Tweaky", ActionFeedbackKind.Failed, "Některé změny Windows po aplikaci nepotvrdil.");
            await ShowTweakApplyFailureAsync(readOnlyList);
            return;
        }
        if (finalReadBackError != null)
        {
            // A backend returning no errors is NOT proof of success when the
            // independent native readback could not confirm actual state.
            ShowActionFeedback("Tweaky", ActionFeedbackKind.Failed,
                "Změny mohly proběhnout, ale jejich konečný stav nebyl ověřen.");
            await ShowMessageAsync("Nelze potvrdit stav Windows",
                "Kontrola po použití změn se nepodařila. " +
                "Neopakuj aplikaci naslepo; nejprve znovu načti skutečný stav. " +
                finalReadBackError.GetBaseException().Message);
            return;
        }
        bool restartRequired = dirty.Any((TweakDefinition x) => x.RestartRequired);
        ShowActionFeedback("Tweaky", restartRequired ? ActionFeedbackKind.RestartRequired : ActionFeedbackKind.Verified, $"{dirty.Count} změn bylo ověřeno.");
        await ShowMessageAsync("Hotovo", restartRequired ? "Změny byly aplikovány a ověřeny. Některé vyžadují restart Windows." : "Změny byly aplikovány a ověřeny.");
        }
        finally
        {
            Interlocked.Exchange(ref _tweakApplyInProgress, 0);
        }
    }

    private async Task<bool> ReconcileCancelledApplyAsync(IEnumerable<TweakDefinition> requested, string area)
    {
        var immediateReadBack = requested.Where(x => !x.RestartRequired).Distinct().ToArray();
        if (immediateReadBack.Length == 0) return true;

        using var reconcileCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            // A cancelled batch needs factual Windows readback, not an
            // implicit "Discard". Preserve any user-selected target still
            // pending after the engine's best-effort rollback.
            await _engine.DetectAsync(immediateReadBack, reconcileCts.Token);
            bool verified = immediateReadBack.All(x => x.IsLoaded);
            if (verified)
                LogService.Info("tweaks",
                    $"CANCEL_RECONCILE_OK area={area} count={immediateReadBack.Length}");
            else
                LogService.Info("tweaks",
                    $"CANCEL_RECONCILE_INCOMPLETE area={area} count={immediateReadBack.Length}");
            return verified;
        }
        catch (OperationCanceledException)
        {
            LogService.Info("tweaks", $"CANCEL_RECONCILE_TIMEOUT area={area} count={immediateReadBack.Length}");
            return false;
        }
        catch (Exception ex)
        {
            LogService.Error("tweaks", $"Cancelled apply reconcile failed area={area}", ex);
            return false;
        }
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        // Do not reset Pending while a confirmed batch is being applied or
        // post-apply native readback is reconciling those same definitions.
        if (Volatile.Read(ref _tweakApplyInProgress) != 0) return;
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

    private Button QuickButton(string text, Func<Task> action, Func<bool>? canExecute = null)
    {
        Button b = new Button { Content = text };
        ApplyPillButtonAppearance(b);
        b.MinHeight = UseGlassChrome ? 36 : 35;
        b.CornerRadius = new CornerRadius(UseGlassChrome ? 14 : 7);
        b.Padding = new Thickness(UseGlassChrome ? 15 : 13, 6, UseGlassChrome ? 15 : 13, 6);
        b.FontSize = 12.5;
        if (!UseNeutralSectionChrome) AttachHoverMotion(b, 1.012f);
        int actionBusy = 0;
        b.Click += async delegate
        {
            try
            {
                if (Interlocked.Exchange(ref actionBusy, 1) != 0)
                {
                    LogService.Info("ui", $"QUICK_ACTION_DUPLICATE_BLOCKED text={text}");
                    return;
                }

                object normalContent = b.Content;
                // Keep focus while guarded async work runs; actionBusy blocks duplicate activation.
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
                    b.IsEnabled = canExecute?.Invoke() ?? true;
                    Interlocked.Exchange(ref actionBusy, 0);
                }
            }
            catch (Exception ex)
            {
                // An asynchronous event handler must never let an exception reach
                // the WinUI dispatcher; a failed feedback dialog would otherwise crash.
                Interlocked.Exchange(ref actionBusy, 0);
                LogService.Error($"Quick action handler failed: {text} | HRESULT=0x{ex.HResult:X8}", ex);
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
        // A rejected low-priority enqueue must release the pending bit, or the
        // GPU tools card could never be scheduled again in this session.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _gpuToolsQueued = false;
            if (token.IsCancellationRequested || _section != "gpu" || _gpuToolsCard != null) return;
            var sw = Stopwatch.StartNew();
            BuildGpuActions();
            LogService.Info("performance", $"GPU_TOOLS_DEFERRED ms={sw.ElapsedMilliseconds}");
        })) _gpuToolsQueued = false;
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
            if (!ContentStack.Children.Contains(_networkDriverCard))
                AttachCachedElement(_networkDriverCard);
            RefreshNetworkDriverSummaryIfExpanded();
            return Task.CompletedTask;
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "network") return;

            var summary = new TextBlock { Text = "Rozbal pro načtení stavu ovladače síťové karty.", TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            _networkRealtekSummary = summary;
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(summary);
            stack.Children.Add(QuickButton("Obnovit stav", () =>
            {
                _networkDriverSummaryAt = default;
                RefreshNetworkDriverSummaryIfExpanded();
                return Task.CompletedTask;
            }));
            stack.Children.Add(QuickButton("Zkontrolovat Windows Update", async delegate { await WindowsActions.RealtekScanAsync(_networkDriverActionToken); }));
            _networkDriverCard = BuildDashboardExpander(
                "Driver síťové karty",
                "Realtek • Windows Update • stav aktivního adaptéru",
                stack);
            ContentStack.Children.Add(_networkDriverCard);
            if (_networkPageCache != null && !_networkPageCache.Contains(_networkDriverCard))
                _networkPageCache.Add(_networkDriverCard);
            if (_networkDriverCard.Child is Expander expander)
                expander.Expanding += (_, _) =>
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        RefreshNetworkDriverSummaryIfExpanded);
        });
        return Task.CompletedTask;
    }

    // A collapsed driver card should not probe Windows Update / Realtek on every
    // navigation. An expanded cached card refreshes at most once per 20 seconds,
    // and a manual refresh remains available for immediate read-back.
    private void RefreshNetworkDriverSummaryIfExpanded()
    {
        if (_section != "network" || _networkDriverCard?.Child is not Expander { IsExpanded: true } ||
            _networkRealtekSummary is not { } target) return;
        CancellationToken token = _loadCts?.Token ?? _windowLifetimeCts.Token;
        if (token.IsCancellationRequested) return;
        int generation = _navigationGeneration;
        if (_networkDriverSummaryLoadingGeneration == generation ||
            DateTime.UtcNow - _networkDriverSummaryAt < TimeSpan.FromSeconds(20)) return;

        _networkDriverSummaryLoadingGeneration = generation;
        SafeTaskService.Forget(RunReadbackAsync(), "network", "On-demand network driver readback");
        async Task RunReadbackAsync()
        {
            try
            {
                await LoadDriverSummaryAsync(target, token);
                if (!token.IsCancellationRequested && generation == _navigationGeneration &&
                    _section == "network" && ReferenceEquals(_networkRealtekSummary, target))
                    _networkDriverSummaryAt = DateTime.UtcNow;
            }
            finally
            {
                if (_networkDriverSummaryLoadingGeneration == generation)
                    _networkDriverSummaryLoadingGeneration = -1;
            }
        }
    }

    private async Task LoadDriverSummaryAsync(TextBlock target, CancellationToken token)
    {
        int generation = _navigationGeneration;
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

        if (token.IsCancellationRequested ||
            generation != _navigationGeneration ||
            _section != "network" ||
            !ReferenceEquals(_networkRealtekSummary, target)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "network" ||
                !ReferenceEquals(_networkRealtekSummary, target)) return;
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
        var icon = new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(11), Background = _darkTheme ? GlassSurfaceLowBrush() : SurfaceAltBrush(), BorderThickness = new Thickness(0), Child = SectionIcon(tag, 36), VerticalAlignment = VerticalAlignment.Center };
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
