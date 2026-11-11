using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static AppThemePreference _themePreference = AppThemePreference.Dark;
    private static bool _darkTheme = true;
    private static AppThemePalette _themePalette = AppThemeEngine.Resolve(AppThemePreference.Dark, true);
    private static string _themeSection = "overview";
    private static readonly Dictionary<int, SolidColorBrush> BrushCache = new();
    private static readonly Dictionary<uint, SolidColorBrush> AlphaBrushCache = new();

    private static SolidColorBrush PaletteBrush(ThemeRgb color) => ThemeColor(color.R, color.G, color.B);
    private static SolidColorBrush TextPrimaryBrush() => PaletteBrush(SectionPalette.TextPrimary);
    // Dark acrylic cards need readable secondary labels regardless of the selected palette.
    // Reuse the cached solid brush; no per-row brush allocations or extra layout passes.
    private static SolidColorBrush TextSecondaryBrush() => _darkTheme
        ? ThemeColor(222, 234, 246)
        : PaletteBrush(SectionPalette.TextSecondary);
    private static SolidColorBrush SurfaceBrush() => IsSolidDarkTheme ? ThemeColor(224, 9, 18, 29) : UseGlassChrome ? ThemeColor(16,12,25,38) : PaletteBrush(SectionPalette.Surface);
    private static SolidColorBrush SurfaceElevatedBrush() => IsSolidDarkTheme ? ThemeColor(232, 16, 28, 42) : UseGlassChrome ? ThemeColor(26,34,48,63) : PaletteBrush(SectionPalette.SurfaceElevated);
    private static SolidColorBrush SurfaceAltBrush() => IsSolidDarkTheme
        ? ThemeColor(216, 13, 26, 40)
        : UseGlassChrome ? ThemeColor(12, 13, 26, 40) : PaletteBrush(SectionPalette.SurfaceAlt);
    private static SolidColorBrush BorderThemeBrush() => PaletteBrush(SectionPalette.Border);
    private static SolidColorBrush ThemeColor(byte r, byte g, byte b)
    {
        int key = (r << 16) | (g << 8) | b;
        if (BrushCache.TryGetValue(key, out var cached)) return cached;
        var brush = new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
        BrushCache[key] = brush;
        return brush;
    }

    private static SolidColorBrush ThemeColor(byte a, byte r, byte g, byte b)
    {
        if (a == 255) return ThemeColor(r, g, b);
        uint key = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        if (AlphaBrushCache.TryGetValue(key, out var cached)) return cached;
        var brush = new SolidColorBrush(ColorHelper.FromArgb(a, r, g, b));
        AlphaBrushCache[key] = brush;
        return brush;
    }

    private static (byte R, byte G, byte B) AccentRgb() => (_themePalette.Accent.R, _themePalette.Accent.G, _themePalette.Accent.B);
    private static SolidColorBrush ButtonVioletBrush() => PaletteBrush(SectionPalette.PrimaryButton);
    private static SolidColorBrush ButtonVioletSoftBrush() => UseGlassChrome ? GlassAccentTintBrush() : PaletteBrush(SectionPalette.PrimaryButtonSoft);
    private static SolidColorBrush ButtonVioletBorderBrush() => PaletteBrush(SectionPalette.PrimaryButtonBorder);
    private static SolidColorBrush AccentBrush() => PaletteBrush(SectionPalette.Accent);
    private static SolidColorBrush AccentSoftBrush() => UseGlassChrome ? GlassAccentTintBrush() : PaletteBrush(SectionPalette.AccentSoft);
    private static SolidColorBrush SuccessBrush() => PaletteBrush(_themePalette.Success);
    private static SolidColorBrush WarningBrush() => PaletteBrush(_themePalette.Warning);
    private static SolidColorBrush InfoBrush() => PaletteBrush(SectionPalette.Info);
    private static SolidColorBrush ProgressBrush() => PaletteBrush(SectionPalette.Info);
    private static SolidColorBrush OnAccentTextBrush() => ThemeColor(255, 255, 255);
    private static SolidColorBrush ControlKnobBrush() => _darkTheme ? ThemeColor(248, 248, 252) : SurfaceElevatedBrush();
    private static SolidColorBrush DialogOverlayBrush(byte alpha = 118) =>
        _darkTheme
            ? ThemeColor((byte)Math.Min(104, (int)alpha), 8, 28, 44)
            : ThemeColor((byte)Math.Min(108, (int)alpha), 16, 24, 40);
    private static SolidColorBrush NetworkAccentBrush() => UseNeutralSectionChrome ? ThemeColor(192,192,192) : _themePreference switch
    {
        AppThemePreference.RedPerformance => ThemeColor(190, 108, 112),
        AppThemePreference.PurpleAccent => ThemeColor(126, 112, 190),
        AppThemePreference.Light => ThemeColor(2, 132, 199),
        _ => ThemeColor(36, 210, 255)
    };

    // Semantic telemetry colors live in the theme layer so Overview/graphs never own raw RGB values.
    // Graphite Dark preserves the current visual exactly; other themes get restrained theme-aware variants.
    private static SolidColorBrush CpuMetricBrush() => _darkTheme ? ThemeColor(92,183,255) : ThemeColor(55,65,81);
    private static SolidColorBrush GpuMetricBrush() => _darkTheme ? ThemeColor(95,220,142) : ThemeColor(35,115,82);
    private static SolidColorBrush GpuHardwareBrush() => GpuMetricBrush();
    private static SolidColorBrush RamMetricBrush() => _darkTheme ? ThemeColor(191,153,255) : ThemeColor(141,104,41);
    private static SolidColorBrush DiskMetricBrush() => _darkTheme ? ThemeColor(255,184,94) : ThemeColor(59,102,138);
    private static SolidColorBrush DiskHardwareBrush() => DiskMetricBrush();
    private static void ResetThemeBrushCaches()
    {
        GlassGradientCache.Clear();
        ReferenceSurfaceCache.Clear();
        ReferenceIconChipCache.Clear();
        _referenceRimBrush = null;
        _sectionDarkCardBackground = null;
        _sectionDarkBorder = null;
        _sectionDarkHeaderBackground = null;
        _overviewCardBackground = null;
        _overviewNeonBorder = null;
        _sidebarActiveBackground = null;
        _sidebarInactiveBackground = null;
        _sidebarHoverBackground = null;
        _sidebarActiveBorder = null;
        _sidebarInactiveBorder = null;
        _glassPanel = null;
        _glassEdge = null;
        _glassBlue = null;
        _glassBlueEdge = null;
        _sidebarGlass = null;
        _glassNavigation = null;
        _dialogModalCardBrush = null;
        _dialogModalBodyBrush = null;
    }

    private static void ApplyThemeResources()
    {
        if (Application.Current?.Resources is not ResourceDictionary resources) return;
        ApplyToggleOnThemeResources(resources);
        ApplyToggleOffThemeResources(resources);
        ApplySelectionThemeResources(resources);
        ApplyAccentThemeResources(resources);
    }

    // One visual language for all native WinUI toggles and the NIC custom track.
    // The screenshot reference is a vivid sky-blue 44x22 capsule with a white knob;
    // the ON/OFF text remains managed by each existing section.
    private static Brush? _unifiedToggleOnTrack, _unifiedToggleOnHover,
        _unifiedToggleOnPressed, _unifiedToggleOffTrack;

    private static Brush UnifiedToggleOnTrack() => _unifiedToggleOnTrack ??= GlassGradient(
        Windows.UI.Color.FromArgb(244, 102, 207, 249),
        Windows.UI.Color.FromArgb(237, 37, 156, 229));

    private static Brush UnifiedToggleOnHover() => _unifiedToggleOnHover ??= GlassGradient(
        Windows.UI.Color.FromArgb(250, 124, 219, 252),
        Windows.UI.Color.FromArgb(244, 49, 173, 238));

    private static Brush UnifiedToggleOnPressed() => _unifiedToggleOnPressed ??= GlassGradient(
        Windows.UI.Color.FromArgb(245, 70, 183, 237),
        Windows.UI.Color.FromArgb(238, 23, 138, 214));

    private static Brush UnifiedToggleOffTrack() => _unifiedToggleOffTrack ??= GlassGradient(
        Windows.UI.Color.FromArgb(67, 91, 127, 160),
        Windows.UI.Color.FromArgb(48, 35, 71, 108));

    private static void ApplyToggleOnThemeResources(ResourceDictionary resources)
    {
        // Native WinUI ToggleSwitch style requires SolidColorBrush values for
        // its theme resources; assigning LinearGradientBrush crashes XAML's
        // visual-size smoke with STATUS_STOWED_EXCEPTION (0xC000027B).
        // The custom NIC switch may still use the gradient from the screenshot.
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOn", ThemeColor(62, 183, 237));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOnPointerOver", ThemeColor(82, 199, 247));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOnPressed", ThemeColor(38, 160, 224));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOnDisabled", ThemeColor(112, 85, 168, 207));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOn", ThemeColor(158, 116, 207, 250));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOnPointerOver", ThemeColor(190, 150, 225, 253));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOnPressed", ThemeColor(168, 94, 184, 244));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOnDisabled", ThemeColor(75, 112, 188, 225));
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOn", ThemeColor(255, 255, 255));
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOnPointerOver", ThemeColor(255, 255, 255));
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOnPressed", ThemeColor(255, 255, 255));
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOnDisabled", ThemeColor(211, 227, 239));
    }

    private static void ApplyToggleOffThemeResources(ResourceDictionary resources)
    {
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOff", ThemeColor(64, 66, 102, 137));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOffPointerOver", ThemeColor(83, 82, 123, 157));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOffPressed", ThemeColor(97, 72, 112, 152));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOff", ThemeColor(96, 130, 171, 209));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOffPointerOver", ThemeColor(135, 145, 196, 228));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOffPressed", ThemeColor(115, 122, 180, 214));
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOff", ThemeColor(255, 255, 255));
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOffPointerOver", ThemeColor(255, 255, 255));
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOffPressed", ThemeColor(255, 255, 255));
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOffDisabled", ThemeColor(211, 227, 239));
    }

    private static void ApplySelectionThemeResources(ResourceDictionary resources)
    {
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundFillChecked", ButtonVioletBrush());
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundFillCheckedPointerOver", AccentBrush());
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundFillCheckedPressed", ButtonVioletBorderBrush());
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundStrokeChecked", ButtonVioletBorderBrush());
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundStrokeCheckedPointerOver", AccentBrush());
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundStrokeCheckedPressed", ButtonVioletBorderBrush());
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedFill", ButtonVioletBrush());
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedFillPointerOver", AccentBrush());
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedFillPressed", ButtonVioletBorderBrush());
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedStroke", ButtonVioletBorderBrush());
    }

    private static void ApplyAccentThemeResources(ResourceDictionary resources)
    {
        resources["AccentFillColorDefault"] = Windows.UI.Color.FromArgb(255, _themePalette.PrimaryButton.R, _themePalette.PrimaryButton.G, _themePalette.PrimaryButton.B);
        resources["AccentFillColorSecondary"] = Windows.UI.Color.FromArgb(255, _themePalette.Accent.R, _themePalette.Accent.G, _themePalette.Accent.B);
        resources["AccentFillColorTertiary"] = Windows.UI.Color.FromArgb(255, _themePalette.PrimaryButtonBorder.R, _themePalette.PrimaryButtonBorder.G, _themePalette.PrimaryButtonBorder.B);
        resources["AccentFillColorDisabled"] = Windows.UI.Color.FromArgb(102, _themePalette.PrimaryButton.R, _themePalette.PrimaryButton.G, _themePalette.PrimaryButton.B);
        ApplyNeutralControlResources(resources);
    }

    private async Task ApplyStartupThemeResourcesAfterFirstRenderAsync()
    {
        System.Threading.CancellationToken token = _windowLifetimeCts.Token;
        try
        {
            await Task.Delay(850, token).ConfigureAwait(false);

            long idleWaitMs = 0;
            for (int i = 0; i < 4; i++)
            {
                token.ThrowIfCancellationRequested();
                TimeSpan sinceNavigation = DateTime.UtcNow - _lastNavigationUtc;
                if (sinceNavigation >= TimeSpan.FromMilliseconds(300)) break;
                int delay = Math.Clamp(320 - (int)sinceNavigation.TotalMilliseconds, 50, 320);
                await Task.Delay(delay, token).ConfigureAwait(false);
                idleWaitMs += delay;
            }

            if (!await RunStartupThemeResourceStageAsync("toggle-on", ApplyToggleOnThemeResources, token).ConfigureAwait(false)) return;
            if (!await RunStartupThemeResourceStageAsync("toggle-off", ApplyToggleOffThemeResources, token).ConfigureAwait(false)) return;
            if (!await RunStartupThemeResourceStageAsync("selection", ApplySelectionThemeResources, token).ConfigureAwait(false)) return;
            if (!await RunStartupThemeResourceStageAsync("accent", ApplyAccentThemeResources, token).ConfigureAwait(false)) return;
            LogService.Info("performance", $"THEME_STARTUP_RESOURCES_DEFERRED_OK stages=4 idleWaitMs={idleWaitMs}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal window shutdown.
        }
    }

    private Task<bool> RunStartupThemeResourceStageAsync(string stage, Action<ResourceDictionary> apply, System.Threading.CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested)
            {
                tcs.TrySetResult(false);
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (Application.Current?.Resources is ResourceDictionary resources)
                    apply(resources);
                LogService.Info("performance", $"THEME_STARTUP_RESOURCE_STAGE stage={stage} ms={sw.ElapsedMilliseconds}");
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                LogService.Error("theme", $"Startup theme resource stage failed: {stage}", ex);
                tcs.TrySetException(ex);
            }
        }))
            tcs.TrySetResult(false);
        return tcs.Task;
    }

    // Shared section chrome follows the Overview visual language in dark mode.
    // Cache the immutable gradient brushes because these helpers are hit by nearly every section row/card.
    private static Brush? _sectionDarkCardBackground;
    private static Brush? _sectionDarkBorder;
    private static Brush? _sectionDarkHeaderBackground;

    private static Brush SectionCardBackgroundBrush()
        => UseGlassChrome ? GlassSurfaceBrush() : UseNeutralSectionChrome ? NeutralCardSurface() : (_darkTheme ? (_sectionDarkCardBackground ??= OverviewCardBackgroundBrush()) : SurfaceElevatedBrush());

    private static Brush SectionBorderBrush()
        => UseGlassChrome ? GlassBorderBrush() : UseNeutralSectionChrome ? NeutralCardEdge() : (_darkTheme ? (_sectionDarkBorder ??= OverviewNeonBorderBrush()) : BorderThemeBrush());

    private static Brush SectionHeaderBackgroundBrush()
    {
        if (UseGlassChrome) return GlassSurfaceLowBrush();
        if (UseNeutralSectionChrome) return GlassSurfaceLowBrush();
        if (!_darkTheme) return SurfaceElevatedBrush();
        if (_sectionDarkHeaderBackground != null) return _sectionDarkHeaderBackground;

        _sectionDarkHeaderBackground = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, _themePalette.SectionHeaderStart.R, _themePalette.SectionHeaderStart.G, _themePalette.SectionHeaderStart.B), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, _themePalette.SectionHeaderMid.R, _themePalette.SectionHeaderMid.G, _themePalette.SectionHeaderMid.B), Offset = .72 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, _themePalette.SectionHeaderEnd.R, _themePalette.SectionHeaderEnd.G, _themePalette.SectionHeaderEnd.B), Offset = 1 }
            }
        };
        return _sectionDarkHeaderBackground;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        if (!_darkTheme) return ThemeColor(r, g, b);
        int max = System.Math.Max(r, System.Math.Max(g, b));
        int min = System.Math.Min(r, System.Math.Min(g, b));
        int avg = (r + g + b) / 3;
        if (max - min >= 36) return ThemeColor((byte)System.Math.Min(255, r + 8), (byte)System.Math.Min(255, g + 8), (byte)System.Math.Min(255, b + 8));
        if (avg >= 245) return ThemeColor(16, 20, 28);
        if (avg >= 232) return ThemeColor(22, 28, 38);
        if (avg >= 210) return ThemeColor(37, 46, 60);
        if (avg <= 55) return ThemeColor(242, 245, 250);
        if (avg <= 105) return ThemeColor(201, 208, 220);
        if (avg <= 165) return ThemeColor(137, 149, 169);
        return ThemeColor(90, 102, 121);
    }
    internal async Task RunThemeRenderSmokeAsync()
    {
        var original = _themePreference;
        string? originalSub = _sub;
        try
        {
            // This checks an actual eight-second inactive-cache deadline.
            // Record it separately so release profiling never attributes the
            // intentional memory-lifetime wait to slow theme switching.
            var cacheLifetimeSmokeSw = System.Diagnostics.Stopwatch.StartNew();
            await RunThemesPageCacheSmokeAsync();
            LogService.Info("release-ui-smoke", $"THEMES_CACHE_LIFETIME_SMOKE_OK ms={cacheLifetimeSmokeSw.ElapsedMilliseconds} ttlMs={ThemesPageCacheTtl.TotalMilliseconds:0}");
            var themeVariantSmokeSw = System.Diagnostics.Stopwatch.StartNew();
            AppThemePreference[] themes =
            {
                AppThemePreference.Dark,
                AppThemePreference.SolidDark
            };

            foreach (var theme in themes)
            {
                await ChangeThemeAsync(theme);
                if (theme == AppThemePreference.SolidDark)
                {
                    if (_startupTitlebarNativeApplied || UseGlassChrome ||
                        !UseDarkAcrylicBackdrop || !MainWindow.IsSolidDarkTheme ||
                        AppTitleBar.Visibility != Visibility.Visible ||
                        AppTitleBar.Children.Count == 0 || _titleBarLogoImage == null ||
                        SolidDarkTintOpacity <= GlassTintOpacity ||
                        SolidDarkLuminosityOpacity <= GlassLuminosityOpacity ||
                        SurfaceAltBrush().Color.A <= 12 || SurfaceAltBrush().Color.A >= 255)
                        throw new InvalidOperationException("SolidDark theme did not preserve its translucent custom titlebar or weaker Glass backdrop.");
                }
                else
                {
                    if (_startupTitlebarNativeApplied)
                        throw new InvalidOperationException($"{theme} theme unexpectedly restored the opaque native DWM titlebar path.");
                    if (!_customTitleBarPrimed || AppTitleBar.Visibility != Visibility.Visible ||
                        AppTitleBar.Children.Count == 0 || _titleBarLogoImage == null)
                        throw new InvalidOperationException($"{theme} theme did not restore the custom transparent titlebar shell.");
                }

                // Reselecting an already active user palette is a no-op, not a
                // second expensive BuildShell/cache invalidation. Check the
                // exact titlebar child reference to catch accidental rebuilds.
                int titlebarChildCountBeforeReselect = AppTitleBar.Children.Count;
                var titlebarBeforeReselect = titlebarChildCountBeforeReselect > 0 ? AppTitleBar.Children[0] : null;
                int generationBeforeReselect = _navigationGeneration;
                await SelectThemePreviewAsync(theme);
                bool titlebarReferenceStable = titlebarBeforeReselect == null ||
                    (AppTitleBar.Children.Count > 0 && ReferenceEquals(AppTitleBar.Children[0], titlebarBeforeReselect));
                if (_themePreference != theme ||
                    _navigationGeneration != generationBeforeReselect ||
                    AppTitleBar.Children.Count == 0 || !titlebarReferenceStable)
                    throw new InvalidOperationException($"{theme} active theme selection rebuilt the shell or navigated. preference={_themePreference} expected={theme} generation={_navigationGeneration} before={generationBeforeReselect} visibility={AppTitleBar.Visibility} children={AppTitleBar.Children.Count} beforeChildren={titlebarChildCountBeforeReselect} referenceStable={titlebarReferenceStable}.");
                LogService.Info("release-ui-smoke", $"THEME_PREVIEW_RESELECT_NOOP_OK theme={theme}");

                // Native shape can be checked only after the replacement tree is connected/measured.
                for (int frame = 0; frame < 40 && (RootGrid.XamlRoot == null || RootGrid.ActualWidth < 50); frame++)
                    await Task.Delay(25);
                if (theme != AppThemePreference.SolidDark)
                    VerifyGlassBackdropLifecycle();
                ValidateNeutralUiRegressionSmoke();
                await ShowSectionAsync("overview");
                await ShowSectionAsync("settings");
                await ShowSectionAsync("network");
                await ShowSectionAsync("storage");
                await ShowSectionAsync("startup");
                await ShowSectionAsync("gpu");
                LogService.Info("theme", $"THEME_VARIANT_SMOKE_OK theme={theme} dark={_darkTheme}");
            }

            await ShowSectionAsync("update");
            await ShowSectionAsync("fixer");
            await ShowSectionAsync("power");
            await ShowSectionAsync("defender");
            await ShowSectionAsync("restore");
            _sub = "Služby"; await ShowSectionAsync("components");
            _sub = "Task Scheduler"; await ShowSectionAsync("components");
            await ShowSectionAsync("debloat");
            await RunNeutralStartupTitlebarSmokeAsync();
            LogService.Info("release-ui-smoke", $"THEME_VARIANTS_AND_NAVIGATION_OK ms={themeVariantSmokeSw.ElapsedMilliseconds} themes={themes.Length}");
            LogService.Info("THEME_RENDER_SMOKE_OK");
        }
        finally
        {
            _sub = originalSub;
            await ChangeThemeAsync(original);
        }
    }

    private async Task ChangeThemeAsync(AppThemePreference preference)
    {
        InvalidateOverviewPageCache(forceRelease: true);
        InvalidateSettingsPageCache();
        InvalidateThemesPageCache();
        InvalidateContextMenuUiCache();
        InvalidateAppsPageCache();
        InvalidateAiPageCache();
        InvalidateDebloatPageCache();
        InvalidateGpuPageCache(includeStaticShell: true);
        InvalidateNetworkPageCache();
        InvalidateStoragePageCache(includeStaticShell: true);
        InvalidateMemoryRecommendedPanel();
        InvalidateDefenderPageCache(includeStaticShell: true);
        InvalidatePowerPageCache();
        InvalidateRestorePageCache();
        InvalidateComponentsPageCache();
        InvalidateStartupPageCache();
        InvalidateUpdatePageCache();
        InvalidateSpicetifyPageCache();
        InvalidateSystemPageCache();
        InvalidatePrivacyPageCache();
        InvalidateUiPageCache();
        InvalidateFixerStaticCache();
        _themePreference = preference;
        _darkTheme = AppThemeService.ResolveDark(preference);
        _themePalette = AppThemeEngine.Resolve(preference, _darkTheme);
        ResetThemeBrushCaches();
        ApplyThemeResources();
        // Palette transitions are kept for internal visual regression only.
        // The production UI uses fixed Dark Glass; never persist test palettes
        // or rewrite the user's old theme setting during a QA run.
        string current = _section;
        _subSelectorCache.Clear();

        // BuildShell creates a brand-new titlebar tree. Never carry primed/content references
        // from the previous theme into the replacement shell.
        _customTitleBarPrimed = false;
        System.Threading.Volatile.Write(ref _startupTitlebarThemeCompleted, false);
        _titleBarLogoImage = null;

        RunWithOverviewPalette(() =>
        {
            BuildShell();
            CompleteDeferredMainTabs();
            ActivateCustomTitleBarNow();
            // Finish the replacement header before navigation restores the section palette.
            CompleteDeferredTitleBarVisuals();
        });
        UpdateDirtyState();
        try { ApplyWindowTitleBarTheme(); }
        catch (System.Exception ex) { LogService.Error("theme", "Titlebar refresh after theme change", ex); }
        await ShowSectionAsync(current);
    }
}
