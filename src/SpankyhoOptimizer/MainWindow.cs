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
}
