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
    private Task<List<TweakDefinition>>? _systemCatalogWarmTask;

    private List<TweakDefinition> _tweaks = new List<TweakDefinition>();

    private string _section = "overview";
    private TweakViewMode _tweakViewMode = TweakViewMode.Manual;

    private string? _sub;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _idleCacheTrimCts;
    private DateTime _lastNavigationUtc = DateTime.UtcNow;
    private string? _lastNavigationErrorId;

    private int _navigationGeneration;
    private int _titlebarThemeGeneration;
    private int _dirtyRefreshQueued;
    private bool _deferredWindowSetupCompleted;
    private bool _startupTitlebarThemeCompleted;
    private bool _startupTitlebarNativeApplied;
    private IntPtr _windowHandle;

    private readonly Dictionary<string, string[]> _subOptions = new Dictionary<string, string[]>
    {
        ["browsers"] = new string[7] { "Firefox", "Chrome", "Edge", "Brave", "Opera", "Opera GX", "Vivaldi" },
        ["components"] = new string[3] { "Služby", "Ovladače", "Task Scheduler" }
    };
    private readonly Dictionary<string, Button[]> _subSelectorCache = new(StringComparer.OrdinalIgnoreCase);

    private Grid RootGrid;

    private Grid AppTitleBar;

    private Grid PageHost;

    private ScrollViewer _pageScroll;

    private StackPanel TopActions;

    private StackPanel ContentStack;

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
        LogService.Info("performance", $"THEME_STARTUP_PHASE preferenceMs={themePreferenceMs} darkMs={themeDarkMs} paletteMs={themePaletteMs} resetMs={themeResetMs} resourcesMs={themeResourcesMs} totalMs={themeMs-themeStartMs} deferredResources=true");
        // Vždy startujeme na Přehledu. UI-state se před prvním renderem zbytečně nenačítá.
        _section = "overview";
        _sub = null;
        // Never delete large Exostenza payloads on the cold-start UI path.
        // Pending cleanup is scheduled after first render and runs on a worker thread.
        BuildShell();
        long shellMs = ctorPhase.ElapsedMilliseconds;
        ConfigureWindow();
        long windowMs = ctorPhase.ElapsedMilliseconds;
        LogService.Info("performance", $"MAINWINDOW_CTOR themeMs={themeMs} shellMs={shellMs - themeMs} windowMs={windowMs - shellMs} totalMs={windowMs}");

        // Start pure managed System catalog materialization after the synchronous window shell is ready.
        // First System navigation awaits this exact task, so ConcurrentDictionary.GetOrAdd never races a
        // second SystemTweaks() factory invocation.
        _systemCatalogWarmTask = Task.Run(async () =>
        {
            // Avoid competing with the immediate activation/render burst while still finishing
            // well before the earliest measured System navigation.
            await Task.Delay(12).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            var tweaks = CatalogService.GetTweaks("system");
            LogService.Info("performance", $"SYSTEM_CATALOG_EARLY_WARM ms={sw.ElapsedMilliseconds} count={tweaks.Count}");
            return tweaks;
        });

        Closed += (_, _) => { UiStateService.Save(new UiState(_section, _sub, "Manual")); CleanupWindowResources(); };
        RootGrid.Loaded += (_, _) =>
        {
            // Theme resource mutation is intentionally deferred until after first paint.
            SafeTaskService.ForgetDeferred(ApplyStartupThemeResourcesAfterFirstRenderAsync(), "performance", "Deferred startup theme resources");
            // Warm cheap backend metadata/probes away from the first Overview snapshot.
            PersistentHardwareCacheService.QueueJsonMetadataWarmup();
            SystemInfoService.QueueNetworkSummaryWarmup();
            // ExtendsContentIntoTitleBar costs ~70-80 ms on the synchronous Loaded path.
            // Let Overview paint first, then activate the custom chrome at low priority.
            SafeTaskService.ForgetDeferred(ActivateCustomTitleBarAfterFirstRenderAsync(), "performance", "Deferred titlebar activation");
            SafeTaskService.ForgetDeferred(ScheduleStartupTitleBarThemeAsync(), "performance", "Startup titlebar theme");
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                CompleteDeferredHero();
                SafeTaskService.Forget(ScheduleDeferredSidebarAfterFirstRenderAsync(), "performance", "Deferred sidebar");
            });
            SafeTaskService.ForgetDeferred(ScheduleDeferredWindowSetupAfterFirstRenderAsync(), "performance", "Deferred window setup");
            SafeTaskService.Forget(ShowSectionAsync(_section), "navigation", "Initial section load");
            SafeTaskService.ForgetDeferred(StartLiveMetricsAfterFirstRenderAsync(), "performance", "Deferred live metrics");
            if (GpuDriverService.HasPendingExostenzaCleanup)
                SafeTaskService.ForgetDeferred(CleanupPendingExostenzaAfterFirstRenderAsync(), "drivers", "Deferred Exostenza cleanup");
            SafeTaskService.Forget(PrewarmVirtualTweakPipelineAsync(), "performance", "Virtual tweak prewarm pipeline");
            SafeTaskService.ForgetDeferred(PrewarmPowerCardFactoryAfterFirstRenderAsync(), "performance", "Power card factory prewarm");
            SafeTaskService.ForgetDeferred(PrewarmStorageActionsEarlyAfterFirstRenderAsync(), "performance", "Early Storage actions prewarm");
            SafeTaskService.ForgetDeferred(PrewarmCommonControlFactoriesAfterFirstRenderAsync(), "performance", "Common control factory prewarm");
            SafeTaskService.ForgetDeferred(PrewarmActionBarAfterFirstRenderAsync(), "performance", "ActionBar factory prewarm");
            SafeTaskService.ForgetDeferred(PrewarmServiceUiAfterFirstRenderAsync(), "performance", "Service UI prewarm");
            SafeTaskService.ForgetDeferred(PrewarmFixerStaticAfterFirstRenderAsync(), "performance", "Fixer static prewarm");
            SafeTaskService.ForgetDeferred(PrewarmBcdStateAfterFirstRenderAsync(), "performance", "BCD state prewarm");
            SafeTaskService.ForgetDeferred(PrewarmWindowsFeaturesAfterFirstRenderAsync(), "performance", "Windows Features prewarm");
            SafeTaskService.ForgetDeferred(PrewarmGpuDetectionAfterFirstRenderAsync(), "performance", "GPU detection prewarm");
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
        long resizeMs = phase.ElapsedMilliseconds;
        LogService.Info("performance", $"CONFIGURE_WINDOW getMs={getWindowMs} resizeMs={resizeMs - getWindowMs} deferred=true totalMs={resizeMs}");
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
            if (appWindow.Presenter is OverlappedPresenter overlappedPresenter)
            {
                if (overlappedPresenter.PreferredMinimumWidth != 980)
                    overlappedPresenter.PreferredMinimumWidth = 980;
                if (overlappedPresenter.PreferredMinimumHeight != 680)
                    overlappedPresenter.PreferredMinimumHeight = 680;
                if (!overlappedPresenter.IsMinimizable)
                    overlappedPresenter.IsMinimizable = true;
                if (!overlappedPresenter.IsMaximizable)
                    overlappedPresenter.IsMaximizable = true;
                if (!overlappedPresenter.IsResizable)
                    overlappedPresenter.IsResizable = true;
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
        var sw = Stopwatch.StartNew();
        IntPtr hwnd = _windowHandle;
        int nativeError = -1;
        if (hwnd != IntPtr.Zero && NativeTitleBarService.TryApply(hwnd, _darkTheme, out nativeError))
        {
            Volatile.Write(ref _startupTitlebarNativeApplied, true);
            Volatile.Write(ref _startupTitlebarThemeCompleted, true);
            LogService.Info("performance", $"TITLEBAR_NATIVE_BG_STAGE totalMs={sw.ElapsedMilliseconds}");
            return;
        }

        LogService.Info("performance", $"TITLEBAR_NATIVE_BG_FALLBACK error={nativeError} ms={sw.ElapsedMilliseconds}");
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyWindowTitleBarTheme);
    }

    internal async Task RunDeferredTitlebarSmokeAsync()
    {
        for (int i = 0; i < 40 && !_startupTitlebarThemeCompleted; i++)
            await Task.Delay(25);
        if (!_startupTitlebarThemeCompleted)
            throw new InvalidOperationException("Deferred startup titlebar theme did not complete.");
        if (!_startupTitlebarNativeApplied)
            throw new InvalidOperationException("Native DWM titlebar path was not used.");
        for (int i = 0; i < 40 && (AppTitleBar.Visibility != Visibility.Visible || AppTitleBar.Children.Count == 0); i++)
            await Task.Delay(25);
        if (!_customTitleBarPrimed)
            throw new InvalidOperationException("Deferred custom titlebar activation did not complete.");
        if (AppTitleBar.Visibility != Visibility.Visible || AppTitleBar.Children.Count == 0)
            throw new InvalidOperationException("Deferred custom titlebar content did not complete.");
        LogService.Info("TITLEBAR_DEFERRED_SMOKE_OK native=true deferredActivation=true");
    }

    private void ApplyWindowTitleBarTheme()
    {
        int generation = Interlocked.Increment(ref _titlebarThemeGeneration);
        var sw = Stopwatch.StartNew();
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            if (NativeTitleBarService.TryApply(hwnd, _darkTheme, out int nativeError))
            {
                _startupTitlebarNativeApplied = true;
                _startupTitlebarThemeCompleted = true;
                LogService.Info("performance", $"TITLEBAR_NATIVE_STAGE totalMs={sw.ElapsedMilliseconds}");
                return;
            }
            _startupTitlebarNativeApplied = false;
            LogService.Info("performance", $"TITLEBAR_NATIVE_FALLBACK error={nativeError} ms={sw.ElapsedMilliseconds}");

            AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            var tb = appWindow.TitleBar;
            long getMs = sw.ElapsedMilliseconds;
            var titleBg = ColorHelper.FromArgb(255, _themePalette.Surface.R, _themePalette.Surface.G, _themePalette.Surface.B);
            var titleFg = ColorHelper.FromArgb(255, _themePalette.TextPrimary.R, _themePalette.TextPrimary.G, _themePalette.TextPrimary.B);
            var hoverBg = ColorHelper.FromArgb(255, _themePalette.SurfaceElevated.R, _themePalette.SurfaceElevated.G, _themePalette.SurfaceElevated.B);
            var pressedBg = ColorHelper.FromArgb(255, _themePalette.Border.R, _themePalette.Border.G, _themePalette.Border.B);
            var inactiveFg = ColorHelper.FromArgb(255, _themePalette.TextSecondary.R, _themePalette.TextSecondary.G, _themePalette.TextSecondary.B);

            long titleStart = sw.ElapsedMilliseconds;
            tb.BackgroundColor = titleBg;
            long backgroundMs = sw.ElapsedMilliseconds - titleStart;
            long foregroundStart = sw.ElapsedMilliseconds;
            tb.ForegroundColor = titleFg;
            _startupTitlebarThemeCompleted = true;
            long foregroundMs = sw.ElapsedMilliseconds - foregroundStart;
            long titleMs = sw.ElapsedMilliseconds - getMs;
            LogService.Info("performance", $"TITLEBAR_STAGE stage=title getMs={getMs} bgMs={backgroundMs} fgMs={foregroundMs} workMs={titleMs} totalMs={sw.ElapsedMilliseconds}");

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (generation != Volatile.Read(ref _titlebarThemeGeneration)) return;
                var stage = Stopwatch.StartNew();
                try
                {
                    tb.ButtonBackgroundColor = titleBg;
                    tb.ButtonForegroundColor = titleFg;
                }
                catch (Exception ex) { LogService.Error("theme", "Deferred titlebar button base styling failed", ex); }
                LogService.Info("performance", $"TITLEBAR_STAGE stage=buttons ms={stage.ElapsedMilliseconds}");

                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (generation != Volatile.Read(ref _titlebarThemeGeneration)) return;
                    var interaction = Stopwatch.StartNew();
                    try
                    {
                        tb.ButtonHoverBackgroundColor = hoverBg;
                        tb.ButtonHoverForegroundColor = titleFg;
                        tb.ButtonPressedBackgroundColor = pressedBg;
                        tb.ButtonPressedForegroundColor = titleFg;
                        tb.InactiveBackgroundColor = titleBg;
                        tb.InactiveForegroundColor = inactiveFg;
                    }
                    catch (Exception ex) { LogService.Error("theme", "Deferred titlebar interaction styling failed", ex); }
                    LogService.Info("performance", $"TITLEBAR_STAGE stage=interaction ms={interaction.ElapsedMilliseconds}");
                });
            });
        }
        catch (Exception ex) { LogService.Error("theme", "Deferred titlebar styling failed", ex); }
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

    private async Task ShowSectionAsync(string key)
    {
        if (TrySkipRedundantSameSectionNavigation(key))
        {
            _lastNavigationErrorId = null;
            _lastNavigationUtc = DateTime.UtcNow;
            return;
        }

        int generation = Interlocked.Increment(ref _navigationGeneration);
        var sw = Stopwatch.StartNew();

        CancelActiveProgressOperationForNavigation();

        // One CTS owns both superseded-navigation cancellation and the 20s timeout.
        // This replaces the former watchdog CTS + linked section CTS pair on every click.
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var navigationCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _loadCts = navigationCts;
        CancellationToken token = navigationCts.Token;

        try
        {
            long navSetupMs = sw.ElapsedMilliseconds;
            await ShowSectionCoreAsync(key, generation, token);
            long buildMs = sw.ElapsedMilliseconds;
            if (key is "network" or "storage" or "gpu" or "startup" or "components" or "defender")
                LogService.Info("performance", $"NAV_WRAPPER_PHASE section={key} setupMs={navSetupMs} coreMs={buildMs - navSetupMs} buildMs={buildMs}");
            // The 20s timeout guards the foreground navigation build only. Deferred work for the
            // active section should stay alive until the next navigation cancels this CTS.
            navigationCts.CancelAfter(Timeout.InfiniteTimeSpan);
            if (generation == _navigationGeneration)
            {
                long animateMs = 0;
                if (_initialSectionNavigationCompleted)
                {
                    long animateStartMs = sw.ElapsedMilliseconds;
                    AnimatePageIn();
                    animateMs = sw.ElapsedMilliseconds - animateStartMs;
                }
                else
                {
                    _initialSectionNavigationCompleted = true;
                }
                _lastNavigationErrorId = null; _lastNavigationUtc = DateTime.UtcNow;
                long totalMs = sw.ElapsedMilliseconds;
                if (totalMs >= 20) LogService.Info("navigation", $"{key} ready ms={totalMs} buildMs={buildMs} animateMs={animateMs}");
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
            LogService.Error("navigation", $"{key} failed id={id} gen={generation}", timeout);
            RenderSectionError(key, id, timeout);
        }
        catch (Exception ex)
        {
            navigationCts.Cancel(); CrashFingerprintService.Record(ex, "navigation", key);
            string id = ErrorService.NewCorrelationId(); SectionFailureService.Fail(key, id); _lastNavigationErrorId = id; _lastNavigationUtc = DateTime.UtcNow;
            LogService.Error("navigation", $"{key} failed id={id} gen={generation}", ex);
            if (generation == _navigationGeneration) RenderSectionError(key, id, ex);
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
    private async Task ShowSectionCoreAsync(string key, int generation, CancellationToken token)
    {
        var navPhase = Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration) throw new OperationCanceledException(token);
        CleanupForNavigation(key);
        long cleanupMs = navPhase.ElapsedMilliseconds;
        _section = key;
        _themeSection = key;
        bool overviewNoScroll = string.Equals(key, "overview", StringComparison.OrdinalIgnoreCase);
        _pageScroll.VerticalScrollMode = overviewNoScroll ? ScrollMode.Disabled : ScrollMode.Enabled;
        if (overviewNoScroll && Math.Abs(_pageScroll.VerticalOffset) > 0.1)
            _pageScroll.ChangeView(null, 0, null, true);
        if (ActionBar.Visibility != Visibility.Collapsed)
            ActionBar.Visibility = Visibility.Collapsed;
        _powerPageCacheAttached = false;
        long stateMs = navPhase.ElapsedMilliseconds;
        ContentStack.Children.Clear();
        TopActions.Children.Clear();
        SubSelectorHost.Children.Clear();
        if (SubSelectorHost.Visibility != Visibility.Collapsed)
            SubSelectorHost.Visibility = Visibility.Collapsed;
        long clearMs = navPhase.ElapsedMilliseconds;
        SectionDefinition sectionDefinition = CatalogService.GetSection(key);
        long catalogMs = navPhase.ElapsedMilliseconds;
        UpdateVisualSelection(key, sectionDefinition.IconGlyph);
        long selectionMs = navPhase.ElapsedMilliseconds;
        const Visibility targetHeroVisibility = Visibility.Collapsed;
        if (HeroTextPanel.Visibility != targetHeroVisibility)
            HeroTextPanel.Visibility = targetHeroVisibility;
        long heroMs = navPhase.ElapsedMilliseconds;
        double targetHeaderHeight = RootGrid.ActualWidth > 1
            ? Math.Clamp(((RootGrid.ActualWidth - 274d) * 216d) / 1256d + 70d, 250d, 286d)
            : 286d;
        if (RootGrid.RowDefinitions[0].Height.GridUnitType != GridUnitType.Pixel ||
            Math.Abs(RootGrid.RowDefinitions[0].Height.Value - targetHeaderHeight) > 0.1)
            RootGrid.RowDefinitions[0].Height = new GridLength(targetHeaderHeight);
        long headerSizeMs = navPhase.ElapsedMilliseconds;
        string heroTitle = key == "overview" ? "Spankyho Optimizer" : sectionDefinition.Title;
        string heroSubtitle = key == "overview"
            ? "Rychlejší systém. Lepší výkon. Vše na jednom místě."
            : sectionDefinition.Subtitle;
        if (!string.Equals(PageTitle.Text, heroTitle, StringComparison.Ordinal))
            PageTitle.Text = heroTitle;
        if (!string.Equals(PageSubtitle.Text, heroSubtitle, StringComparison.Ordinal))
            PageSubtitle.Text = heroSubtitle;
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
        if (key is "components" or "gpu" or "network" or "storage" or "startup" or "defender")
        {
            LogService.Info("performance",
                $"NAV_PRELUDE_PHASE section={key} cleanupMs={cleanupMs} stateMs={stateMs - cleanupMs} clearMs={clearMs - stateMs} catalogMs={catalogMs - clearMs} selectionMs={selectionMs - catalogMs} heroMs={heroMs - selectionMs} headerMs={headerSizeMs - heroMs} textMs={shellMs - headerSizeMs} subMs={subSelectorMs} totalMs={navPhase.ElapsedMilliseconds}");
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
            CaptureDebloatPageCache();
            return;
        }
        if (key == "apps")
        {
            await BuildAppsPageAsync(token);
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
            long managerMs = startupNavPerf.ElapsedMilliseconds - cacheMs;
            BuildWindowsModernizerStartupPanel(token);
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
                BuildWindowsModernizerVendorPanel(token);
            }
            long buildPhaseMs = componentPerf.ElapsedMilliseconds - buildStart;

            long captureStart = componentPerf.ElapsedMilliseconds;
            CaptureComponentsPageCache(componentSub);
            long capturePhaseMs = componentPerf.ElapsedMilliseconds - captureStart;
            LogService.Info("performance",
                $"COMPONENT_NAV_PHASE sub={componentSub} cacheHit=false catalogMs={catalogPhaseMs} cacheMs={cachePhaseMs} contextMs={contextPhaseMs} buildMs={buildPhaseMs} captureMs={capturePhaseMs} totalMs={componentPerf.ElapsedMilliseconds}");
            return;
        }
        if (key == "ai") { await BuildAiPageAsync(token); return; }
        if (key == "defender") { await BuildDefenderPageAsync(token); return; }
        Stopwatch? systemForeground = key == "system" ? Stopwatch.StartNew() : null;
        Stopwatch? storageCatalogPhase = key == "storage" ? Stopwatch.StartNew() : null;
        string tweakCacheKey = key + "|" + (_sub ?? "");
        if (!_tweakCache.TryGetValue(tweakCacheKey, out var cachedTweaks))
        {
            if (key == "system" && _systemCatalogWarmTask != null)
                cachedTweaks = await _systemCatalogWarmTask;
            else
                cachedTweaks = CatalogService.GetTweaks(key, _sub);
            _tweakCache[tweakCacheKey] = cachedTweaks;
        }
        _tweaks = cachedTweaks;
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

            AttachStorageStaticShell(out long storageHeroMs, out long storageMetricsMs);

            BuildStorageActions();
            ScheduleStorageOptimizationAdvisor(token);
            long storageActionsMs = storageForeground.ElapsedMilliseconds - storageHeroMs - storageMetricsMs;
            ScheduleStorageTools(token);
            long storageScheduleMs = storageForeground.ElapsedMilliseconds - storageHeroMs - storageMetricsMs - storageActionsMs;
            LogService.Info("performance",
                $"STORAGE_FOREGROUND_PHASE preStorageMs={storagePreMs} catalogMs={storageCatalogMs} heroMs={storageHeroMs} metricsMs={storageMetricsMs} actionsMs={storageActionsMs} scheduleMs={storageScheduleMs} totalMs={storageForeground.ElapsedMilliseconds}");
        }
        if (key == "gpu")
        {
            SynchronizeGpuDetectionGeneration();
            bool gpuStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var gpuDetectedAt) || DateTime.UtcNow - gpuDetectedAt > TimeSpan.FromSeconds(90);
            if (!gpuStale && TryAttachGpuPageCache(token))
                return;
            if (gpuStale) InvalidateGpuPageCache();
            await BuildGpuDriverCenterAsync(token);
            BuildGpuActions();
        }
        if (key == "network")
        {
            if (TryAttachNetworkPageCache(token, out bool networkPanelsRefreshed))
            {
                bool networkStale = !_tweakCacheTime.TryGetValue(tweakCacheKey, out var networkDetectedAt) || DateTime.UtcNow - networkDetectedAt > TimeSpan.FromSeconds(90);
                if (networkStale) SafeTaskService.Forget(DetectSectionTweaksAndRefreshAsync("network", tweakCacheKey, _tweaks.ToArray(), token), "tweaks", "Cached network detect");
                return;
            }
            var networkBuild = Stopwatch.StartNew();
            BuildNetworkPerformancePanel(token);
            long networkPerfMs = networkBuild.ElapsedMilliseconds;
            await BuildNetworkTelemetryAsync(token);
            long networkShellMs = networkBuild.ElapsedMilliseconds - networkPerfMs;
            BuildNetworkDashboardBody(token);
            long networkDashboardMs = networkBuild.ElapsedMilliseconds - networkPerfMs - networkShellMs;
            await BuildDriverActionsAsync(token);
            long networkDriverMs = networkBuild.ElapsedMilliseconds - networkShellMs - networkPerfMs - networkDashboardMs;
            BuildNetworkTools();
            long networkToolsMs = networkBuild.ElapsedMilliseconds - networkShellMs - networkPerfMs - networkDashboardMs - networkDriverMs;
            CaptureNetworkPageCache();
            LogService.Info("performance", $"NETWORK_BUILD shellMs={networkShellMs} perfMs={networkPerfMs} dashboardMs={networkDashboardMs} driverMs={networkDriverMs} toolsMs={networkToolsMs} totalMs={networkBuild.ElapsedMilliseconds}");
        }
        if (key == "storage")
        {
            var storageBuild = Stopwatch.StartNew();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || _section != "storage") return;
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
        }
        if (key == "browsers")
        {
            string browser = _sub ?? "Firefox";
            BuildBrowserDashboard(browser);
            BuildBrowserCachePanel(browser, token);
            if (browser == "Firefox")
            {
                BuildFirefoxAdvancedPanel(token);
                BuildWindowsModernizerFirefoxPanel(token);
            }
        }
        if (key == "ui")
        {
            BuildContextMenuTweaker();
            BuildWindowsModernizerUiPanel();
            BuildWinhanceUiActions();
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
                SafeTaskService.Forget(BuildGpuDeferredStateAsync(tweakCacheKey, _tweaks.ToArray(), staleDetection, token), "gpu", "Deferred GPU state");
            }
            else if (key == "storage")
            {
                SafeTaskService.Forget(BuildStorageDeferredStateAsync(tweakCacheKey, _tweaks.ToArray(), staleDetection, token), "storage", "Deferred storage state");
            }
            else
            {
                ScheduleRenderFirstBackground(key, tweakCacheKey, _tweaks.ToArray(), staleDetection, token);
            }
            return;
        }
        if (staleDetection) { await _engine.DetectAsync(immediateTweaks, token); _tweakCacheTime[tweakCacheKey] = DateTime.UtcNow; }
        await BuildHardwareRecommendedPanelAsync(key, _tweaks, token);
        BuildTweakCards();
        if (lazyTweaks.Length > 0) SafeTaskService.Forget(DetectLazyTweaksAsync(key, lazyTweaks, token), "tweaks", "Lazy detect " + key);
    }

    private void ScheduleRenderFirstBackground(string sectionKey, string cacheKey, TweakDefinition[] tweaks, bool staleDetection, CancellationToken token)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != sectionKey) return;
            if (staleDetection) SafeTaskService.Forget(DetectSectionTweaksAndRefreshAsync(sectionKey, cacheKey, tweaks, token), "tweaks", "Background detect " + sectionKey);
            if (sectionKey is "network" or "memory") SafeTaskService.Forget(BuildHardwareRecommendedPanelAsync(sectionKey, tweaks, token), sectionKey, "Lazy hardware recommended");
        });
    }

    private object? _sectionDetectGateStore;
    private HashSet<string>? _sectionDetectInFlightStore;
    private object _sectionDetectGate => _sectionDetectGateStore ??= new object();
    private HashSet<string> _sectionDetectInFlight =>
        _sectionDetectInFlightStore ??= new(StringComparer.OrdinalIgnoreCase);

    private async Task DetectSectionTweaksAndRefreshAsync(string sectionKey, string cacheKey, TweakDefinition[] tweaks, CancellationToken token)
    {
        lock (_sectionDetectGate)
        {
            if (!_sectionDetectInFlight.Add(cacheKey)) return;
        }
        try
        {
            // Read-only detection is shared work: finish it even when navigation leaves the section.
            await _engine.DetectAsync(tweaks, CancellationToken.None);
            _tweakCacheTime[cacheKey] = DateTime.UtcNow;
            if (token.IsCancellationRequested || _section != sectionKey) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != sectionKey) return;
                RefreshCards();
                SyncAttachedClassicCacheRevisionStamp(sectionKey, cacheKey);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Error("Background tweak detect failed: " + sectionKey, ex); }
        finally
        {
            lock (_sectionDetectGate) _sectionDetectInFlight.Remove(cacheKey);
        }
    }
    private async Task DetectLazyTweaksAsync(string sectionKey, TweakDefinition[] tweaks, CancellationToken token)
    {
        try
        {
            await _engine.DetectAsync(tweaks, token);
            if (token.IsCancellationRequested || _section != sectionKey) return;
            DispatcherQueue.TryEnqueue(() => { if (_section == sectionKey) RefreshCards(); });
        }
        catch (OperationCanceledException) { }
    }

    private void RenderSectionError(string key, string id, Exception ex)
    {
        ContentStack.Children.Clear(); TopActions.Children.Clear(); ActionBar.Visibility = Visibility.Collapsed;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Sekci se nepodařilo načíst", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        panel.Children.Add(new TextBlock { Text = ex.Message + "  [ID " + id + "]", TextWrapping = TextWrapping.Wrap, Foreground = TextSecondaryBrush() });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var retry = new Button { Content = "Zkusit znovu" };
        ApplyPillButtonAppearance(retry);
        retry.Click += (_, _) => SafeTaskService.Forget(ShowSectionAsync(key), "navigation", "Retry " + key);
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
                foreach (string sub in _subOptions["components"]) { LogService.Info("section-smoke", "ENTER components/" + sub); _sub = sub; await ShowSectionAsync("components"); EnsureNavigationHealthy("components/" + sub); LogService.Info("section-smoke", "OK components/" + sub); await Task.Delay(15); }
                continue;
            }
            LogService.Info("section-smoke", "ENTER " + key); await ShowSectionAsync(key); EnsureNavigationHealthy(key); LogService.Info("section-smoke", "OK " + key); await Task.Delay(15);
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
        TextBlock VerdictText);

    private Border CreateTweakCard(TweakDefinition t)
    {
        var policy = TweakPolicyService.Evaluate(t, _tweakViewMode);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconBox = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(10), Background = SurfaceAltBrush(), BorderThickness = new Thickness(0), Child = V13Icon(t.Section, 31), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(iconBox);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleRow.Children.Add(new TextBlock { Text = t.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), VerticalAlignment = VerticalAlignment.Center });
        var stateText = !t.IsLoaded ? "NEDOSTUPNÉ" : t.Pending != t.Current ? "ČEKÁ NA POUŽITÍ" : t.Current ? "AKTIVNÍ" : "NEAKTIVNÍ";
        var stateBrush = !t.IsLoaded ? "SpankyWarningBrush" : t.Pending != t.Current ? "SpankyAccentBrush" : t.Current ? "SpankySuccessBrush" : "SpankyInfoBrush";
        var stateBadge = Badge(stateText, stateBrush);
        titleRow.Children.Add(stateBadge);
        var verdict = Badge(policy.Verdict, policy.Verdict == "Recommended" ? "SpankySuccessBrush" : policy.Verdict == "Advanced" || policy.Verdict == "Needs attention" ? "SpankyWarningBrush" : "SpankyInfoBrush");
        verdict.Tag = "policyVerdict"; titleRow.Children.Add(verdict);
        if (t.RestartRequired) titleRow.Children.Add(Badge("Restart", "SpankyInfoBrush"));
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock { Text = t.Description, FontSize = 12, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 1100 });
        var meta = new TextBlock { Text = _tweakViewMode == TweakViewMode.Diagnostics || !t.IsLoaded ? policy.Detail : "Dopad: " + t.Impact + "   •   " + t.Status, FontSize = 10.8, Foreground = TextSecondaryBrush(), Tag = "meta", TextWrapping = TextWrapping.Wrap };
        text.Children.Add(meta);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var toggleState = new TextBlock { Text = t.Pending ? "ON" : "OFF", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = t.Pending ? ThemeColor(196, 206, 255) : ThemeColor(132, 139, 156), VerticalAlignment = VerticalAlignment.Center, Tag = "toggleState", Margin = new Thickness(0, 0, 3, 0) };
        Grid.SetColumn(toggleState, 2); grid.Children.Add(toggleState);
        var toggle = new ToggleSwitch { IsOn = t.Pending, Tag = t, IsEnabled = policy.CanEdit, VerticalAlignment = VerticalAlignment.Center, OffContent = "", OnContent = "", MinWidth = 50 };
        toggle.Toggled += delegate { toggleState.Text = toggle.IsOn ? "ON" : "OFF"; toggleState.Foreground = toggle.IsOn ? ThemeColor(196, 206, 255) : ThemeColor(132, 139, 156); var live = TweakPolicyService.Evaluate(t, _tweakViewMode); if (live.CanEdit && toggle.Tag is TweakDefinition { IsLoaded: not false } td) { td.Pending = toggle.IsOn; QueueDirtyStateRefresh(); } };
        Grid.SetColumn(toggle, 3); grid.Children.Add(toggle);
        grid.Tag = new ClassicTweakCardRefs(
            toggle,
            toggleState,
            meta,
            stateBadge,
            (TextBlock)stateBadge.Child,
            verdict,
            (TextBlock)verdict.Child);
        var row = new Border { Tag = t, Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.15 : 1), CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 0, 7), Padding = new Thickness(14, 10, 14, 10), Child = grid };
        AttachHoverMotion(row, 1.002f); ToolTipService.SetToolTip(row, policy.Detail);
        return row;
    }


    private static Border Badge(string text, string resource)
    {
        Brush background = UiBrush(resource);
        return new Border
        {
            CornerRadius = new CornerRadius(7.0),
            Background = background,
            Opacity = 0.88,
            Padding = new Thickness(7.0, 3.0, 7.0, 3.0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10.0,
                Foreground = OnAccentTextBrush()
            }
        };
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
            toggleState ??= new TextBlock { Text = toggle.IsOn ? "ON" : "OFF", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = toggle.IsOn ? ThemeColor(196, 206, 255) : ThemeColor(132, 139, 156), VerticalAlignment = VerticalAlignment.Center, Tag = "toggleState" };
            refs = new ClassicTweakCardRefs(toggle, toggleState, meta, state, stateTextBlock, verdict, verdictText);
            grid.Tag = refs;
        }

        bool enabled = policy.CanEdit && !tweakDefinition.IsBusy;
        if (refs.Toggle.IsEnabled != enabled) refs.Toggle.IsEnabled = enabled;
        if (refs.Toggle.IsOn != tweakDefinition.Pending) refs.Toggle.IsOn = tweakDefinition.Pending;
        string toggleText = tweakDefinition.Pending ? "ON" : "OFF";
        if (!string.Equals(refs.ToggleState.Text, toggleText, StringComparison.Ordinal)) refs.ToggleState.Text = toggleText;
        refs.ToggleState.Foreground = tweakDefinition.Pending ? ThemeColor(196, 206, 255) : ThemeColor(132, 139, 156);

        string metaText = _tweakViewMode == TweakViewMode.Diagnostics || !tweakDefinition.IsLoaded
            ? policy.Detail
            : "Dopad: " + tweakDefinition.Impact + "   •   " + tweakDefinition.Status;
        if (!string.Equals(refs.Meta.Text, metaText, StringComparison.Ordinal)) refs.Meta.Text = metaText;

        string stateText = !tweakDefinition.IsLoaded ? "NEDOSTUPNÉ"
            : tweakDefinition.Pending != tweakDefinition.Current ? "ČEKÁ NA POUŽITÍ"
            : tweakDefinition.Current ? "AKTIVNÍ" : "NEAKTIVNÍ";
        if (!string.Equals(refs.StateText.Text, stateText, StringComparison.Ordinal))
        {
            refs.StateText.Text = stateText;
            refs.StateBadge.Background = UiBrush(!tweakDefinition.IsLoaded
                ? "SpankyWarningBrush"
                : tweakDefinition.Pending != tweakDefinition.Current
                    ? "SpankyAccentBrush"
                    : tweakDefinition.Current ? "SpankySuccessBrush" : "SpankyInfoBrush");
        }

        if (!string.Equals(refs.VerdictText.Text, policy.Verdict, StringComparison.Ordinal))
        {
            refs.VerdictText.Text = policy.Verdict;
            refs.VerdictBadge.Background = UiBrush(policy.Verdict == "Recommended"
                ? "SpankySuccessBrush"
                : policy.Verdict == "Advanced" || policy.Verdict == "Needs attention"
                    ? "SpankyWarningBrush"
                    : "SpankyInfoBrush");
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
                ActionBar.Visibility = Visibility.Collapsed;
            return;
        }

        EnsureActionBarInitialized();
        ActionBar.Visibility = Visibility.Visible;
        DirtyText.Text = num == 1 ? "1 nevyřízená změna" : $"{num} nevyřízených změn";
    }

    private void Apply_Click(object sender, RoutedEventArgs e) =>
        SafeTaskService.Forget(ApplyClickCoreAsync(), "tweaks", "Apply selected tweaks");

    private async Task ApplyClickCoreAsync()
    {
        List<TweakDefinition> dirty = _tweaks.Where((TweakDefinition x) => x.IsDirty).ToList();
        if (dirty.Count == 0) return;
        var confirmStack = new StackPanel { Spacing = 7 };
        confirmStack.Children.Add(new TextBlock { Text = $"Vybráno změn: {dirty.Count}", FontWeight = FontWeights.SemiBold });
        foreach (var tweak in dirty.Take(40)) confirmStack.Children.Add(new TextBlock { Text = $"• {tweak.Name}: {(tweak.Pending ? "ZAP" : "VYP")}", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (dirty.Count > 40) confirmStack.Children.Add(new TextBlock { Text = $"…a dalších {dirty.Count - 40}", Opacity = .65 });
        if (dirty.Any(x => x.Risk == TweakRisk.Advanced)) confirmStack.Children.Insert(1, new TextBlock { Text = "Některé změny jsou Advanced a mohou snížit zabezpečení nebo funkčnost Windows.", TextWrapping = TextWrapping.Wrap, Foreground = UiBrush("SpankyWarningBrush") });
        if (!await ShowStyledContentAsync("Potvrdit vybrané tweaky", "Zkontroluj změny před aplikací.", new ScrollViewer { Content = confirmStack, MaxHeight = 470 }, "Použít vybrané")) return;
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
        ShowActionFeedback("Tweaky", restartRequired ? ActionFeedbackKind.RestartRequired : ActionFeedbackKind.Verified, $"{dirty.Count} změn ověřeno read-backem.");
        await ShowMessageAsync("Hotovo", restartRequired ? "Změny byly aplikovány a ověřeny. Některé vyžadují restart Windows." : "Změny byly aplikovány a ověřeny read-backem.");
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
        b.FontSize = 12;
        AttachHoverMotion(b, 1.012f);
        b.Click += async delegate
        {
            b.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                LogService.Error($"Quick action failed: {text} | HRESULT=0x{ex.HResult:X8}", ex);
                ShowActionFeedback(text, ActionFeedbackKind.Failed, ex.Message);
                await ShowMessageAsync("Chyba", $"{ex.Message}\n\nHRESULT: 0x{ex.HResult:X8}\nDetaily byly zapsány do logu.");
            }
            finally
            {
                b.IsEnabled = true;
            }
        };
        return b;
    }




    private void BuildMemoryActions()
    {
        BuildMemoryState();
        var defaults = new StackPanel { Spacing = 8 };
        defaults.Children.Add(new TextBlock { Text = "Foreground scheduling — Windows default", Foreground = TextPrimaryBrush(), FontWeight = FontWeights.SemiBold });
        defaults.Children.Add(new TextBlock { Text = "LargeSystemCache — Windows default", Foreground = TextPrimaryBrush(), FontWeight = FontWeights.SemiBold });
        defaults.Children.Add(new TextBlock { Text = "Pagefile — Windows / System Managed", Foreground = TextPrimaryBrush(), FontWeight = FontWeights.SemiBold });
        defaults.Children.Add(new TextBlock { Text = "Recommended ponechává scheduler a desktop cache na výchozím Windows chování a pagefile systémově spravovaný.", Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        ContentStack.Children.Add(VisualPanel("\uE950", "Windows scheduler / cache", "Recommended = výchozí hodnoty Windows", defaults));
        var memoryTools = new StackPanel { Spacing = 8 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.0 };
        row.Children.Add(QuickButton("Spustit idle maintenance", async delegate
        {
            await WindowsActions.RunIdleMaintenanceAsync();
        }));
        row.Children.Add(QuickButton("Trim working sets", async delegate
        {
            await WindowsActions.TrimWorkingSetsAsync();
        }));
        row.Children.Add(QuickButton("Memory Compression", async delegate
        {
            var state = await WindowsActions.GetMemoryCompressionAsync();
            var result = await ShowStyledChoiceAsync("Memory Compression", state + "\n\nVe Windows je doporučené ponechat ji zapnutou.", "Zapnout", "Vypnout");
            if (result == 0) return;
            bool enable = result == 1;
            await WindowsActions.SetMemoryCompressionAsync(enable);
            InvalidateMemoryRecommendedPanel();
            var readback = await WindowsActions.GetMemoryCompressionAsync();
            bool verified = readback.Contains(enable.ToString(), StringComparison.OrdinalIgnoreCase);
            ShowActionFeedback("Memory Compression", verified ? ActionFeedbackKind.Verified : ActionFeedbackKind.Failed, readback);
            if (_section == "memory") await ShowSectionAsync("memory");
        }));
        memoryTools.Children.Add(row);
        memoryTools.Children.Add(BuildGTweakMemoryAdvancedActions());
        ContentStack.Children.Add(BuildDashboardExpander(
            "Paměťové nástroje",
            "Idle maintenance • working sets • Memory Compression • Advanced purge",
            memoryTools));
    }

    private Border? _storageToolsCard;

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

    private void BuildStorageActions()
    {
        ContentStack.Children.Add(EnsureStorageActionsCard());
    }

    private Border? _gpuToolsCard;

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
            var size = await StorageMaintenanceService.GetShaderCacheSizeAsync();
            if (!await ShowStyledConfirmAsync("Vyčistit GPU shader cache?", $"Shader cache mají přibližně {size / 1048576d:0.0} MB. Použij jen při konkrétním problému; první start hry po vyčištění může znovu kompilovat shadery a krátce stutterovat.", "Vyčistit", "Zrušit", "GPU / Display")) return;
            await StorageMaintenanceService.ClearShaderCachesAsync();
            await ShowMessageAsync("GPU", "Shader cache byla vyčištěna.");
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
        try { text = await WindowsActions.GetRealtekSummaryAsync(token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { text = "Realtek kontrolu se nepodařilo načíst: " + ex.Message; }
        if (token.IsCancellationRequested || _section != "network") return;
        DispatcherQueue.TryEnqueue(() => { if (!token.IsCancellationRequested && _section == "network") target.Text = text; });
    }

    private Border ActionCard(string title, string desc, string button, Func<Task> action)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string tag = string.IsNullOrWhiteSpace(_themeSection) ? "system" : _themeSection;
        var icon = new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(11), Background = SurfaceAltBrush(), BorderThickness = new Thickness(0), Child = V13Icon(tag, 36), VerticalAlignment = VerticalAlignment.Center };
        AttachHoverMotion(icon, 1.025f); grid.Children.Add(icon);
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 17.5, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = desc, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = TextSecondaryBrush(), MaxWidth = 840 });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var actionButton = QuickButton(button, action); Grid.SetColumn(actionButton, 2); grid.Children.Add(actionButton);
        var card = new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.25 : 1), CornerRadius = new CornerRadius(13), Padding = new Thickness(15, 13, 15, 13), Margin = new Thickness(0, 0, 0, 10), Child = grid };
        AttachHoverMotion(card, 1.006f);
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
        button.CornerRadius = new CornerRadius(8);
        button.Padding = new Thickness(13, 6, 13, 6);
        button.MinHeight = 34d;
        button.FontSize = 12d;
        button.Foreground = TextPrimaryBrush();
        // Compact violet-tinted secondary buttons; never inherit the Windows accent.
        button.Background = ButtonVioletSoftBrush();
        button.BorderBrush = ButtonVioletBorderBrush();
        button.BorderThickness = new Thickness(1);
    }

}
