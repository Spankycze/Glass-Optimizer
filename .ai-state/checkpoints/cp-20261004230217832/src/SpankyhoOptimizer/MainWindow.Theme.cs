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
    private static AppThemePreference _themePreference = AppThemePreference.System;
    private static bool _darkTheme;
    private static AppThemePalette _themePalette = AppThemeEngine.Resolve(AppThemePreference.System, false);
    private static string _themeSection = "overview";
    private static readonly Dictionary<int, SolidColorBrush> BrushCache = new();
    private static readonly Dictionary<uint, SolidColorBrush> AlphaBrushCache = new();

    private static SolidColorBrush PaletteBrush(ThemeRgb color) => ThemeColor(color.R, color.G, color.B);
    private static SolidColorBrush TextPrimaryBrush() => PaletteBrush(SectionPalette.TextPrimary);
    private static SolidColorBrush TextSecondaryBrush() => PaletteBrush(SectionPalette.TextSecondary);
    private static SolidColorBrush SurfaceBrush() => UseGlassChrome ? ThemeColor(34,12,20,29) : PaletteBrush(SectionPalette.Surface);
    private static SolidColorBrush SurfaceElevatedBrush() => UseGlassChrome ? ThemeColor(58,34,48,63) : PaletteBrush(SectionPalette.SurfaceElevated);
    private static SolidColorBrush SurfaceAltBrush() => UseGlassChrome ? ThemeColor(30,13,23,34) : PaletteBrush(SectionPalette.SurfaceAlt);
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
    private static SolidColorBrush ButtonVioletSoftBrush() => PaletteBrush(SectionPalette.PrimaryButtonSoft);
    private static SolidColorBrush ButtonVioletBorderBrush() => PaletteBrush(SectionPalette.PrimaryButtonBorder);
    private static SolidColorBrush AccentBrush() => PaletteBrush(SectionPalette.Accent);
    private static SolidColorBrush AccentSoftBrush() => PaletteBrush(SectionPalette.AccentSoft);
    private static SolidColorBrush SuccessBrush() => PaletteBrush(_themePalette.Success);
    private static SolidColorBrush WarningBrush() => PaletteBrush(_themePalette.Warning);
    private static SolidColorBrush InfoBrush() => PaletteBrush(SectionPalette.Info);
    private static SolidColorBrush ProgressBrush() => PaletteBrush(SectionPalette.Info);
    private static SolidColorBrush OnAccentTextBrush() => ThemeColor(255, 255, 255);
    private static SolidColorBrush ControlKnobBrush() => _darkTheme ? ThemeColor(248, 248, 252) : SurfaceElevatedBrush();
    private static SolidColorBrush DialogOverlayBrush(byte alpha = 150) =>
        _darkTheme
            ? ThemeColor((byte)Math.Min(124, (int)alpha), 4, 10, 18)
            : ThemeColor((byte)Math.Min(118, (int)alpha), 16, 24, 40);
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
    }

    private static void ApplyThemeResources()
    {
        if (Application.Current?.Resources is not ResourceDictionary resources) return;
        ApplyToggleOnThemeResources(resources);
        ApplyToggleOffThemeResources(resources);
        ApplySelectionThemeResources(resources);
        ApplyAccentThemeResources(resources);
    }

    private static void ApplyToggleOnThemeResources(ResourceDictionary resources)
    {
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOn", ButtonVioletBrush());
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOnPointerOver", AccentBrush());
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOnPressed", ButtonVioletBorderBrush());
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOnDisabled", ThemeColor(102, _themePalette.PrimaryButton.R, _themePalette.PrimaryButton.G, _themePalette.PrimaryButton.B));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOn", ButtonVioletBorderBrush());
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOnPointerOver", AccentBrush());
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOnPressed", ButtonVioletBorderBrush());
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOnDisabled", ThemeColor(102, _themePalette.PrimaryButtonBorder.R, _themePalette.PrimaryButtonBorder.G, _themePalette.PrimaryButtonBorder.B));
    }

    private static void ApplyToggleOffThemeResources(ResourceDictionary resources)
    {
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOff", SurfaceAltBrush());
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOffPointerOver", SurfaceElevatedBrush());
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOffPressed", SurfaceBrush());
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOff", BorderThemeBrush());
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOffPointerOver", TextSecondaryBrush());
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOffPressed", BorderThemeBrush());
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOff", TextPrimaryBrush());
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
        await Task.Delay(850).ConfigureAwait(false);

        long idleWaitMs = 0;
        for (int i = 0; i < 4; i++)
        {
            TimeSpan sinceNavigation = DateTime.UtcNow - _lastNavigationUtc;
            if (sinceNavigation >= TimeSpan.FromMilliseconds(300)) break;
            int delay = Math.Clamp(320 - (int)sinceNavigation.TotalMilliseconds, 50, 320);
            await Task.Delay(delay).ConfigureAwait(false);
            idleWaitMs += delay;
        }

        await RunStartupThemeResourceStageAsync("toggle-on", ApplyToggleOnThemeResources).ConfigureAwait(false);
        await RunStartupThemeResourceStageAsync("toggle-off", ApplyToggleOffThemeResources).ConfigureAwait(false);
        await RunStartupThemeResourceStageAsync("selection", ApplySelectionThemeResources).ConfigureAwait(false);
        await RunStartupThemeResourceStageAsync("accent", ApplyAccentThemeResources).ConfigureAwait(false);
        LogService.Info("performance", $"THEME_STARTUP_RESOURCES_DEFERRED_OK stages=4 idleWaitMs={idleWaitMs}");
    }

    private Task RunStartupThemeResourceStageAsync(string stage, Action<ResourceDictionary> apply)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
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
        => UseGlassChrome ? GlassPanelBrush() : UseNeutralSectionChrome ? NeutralCardSurface() : (_darkTheme ? (_sectionDarkCardBackground ??= OverviewCardBackgroundBrush()) : SurfaceElevatedBrush());

    private static Brush SectionBorderBrush()
        => UseGlassChrome ? GlassEdgeBrush() : UseNeutralSectionChrome ? NeutralCardEdge() : (_darkTheme ? (_sectionDarkBorder ??= OverviewNeonBorderBrush()) : BorderThemeBrush());

    private static Brush SectionHeaderBackgroundBrush()
    {
        if (UseGlassChrome) return ThemeColor(0,0,0,0);
        if (UseNeutralSectionChrome) return ThemeColor(8,8,8);
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
            AppThemePreference[] themes =
            {
                AppThemePreference.Light,
                AppThemePreference.Dark,
                AppThemePreference.MidnightBlue,
                AppThemePreference.PurpleAccent,
                AppThemePreference.CyanTech,
                AppThemePreference.RedPerformance
            };

            foreach (var theme in themes)
            {
                await ChangeThemeAsync(theme);
                if (_startupTitlebarNativeApplied)
                    throw new InvalidOperationException($"{theme} theme unexpectedly restored the opaque native DWM titlebar path.");
                if (!_customTitleBarPrimed || AppTitleBar.Visibility != Visibility.Visible ||
                    AppTitleBar.Children.Count == 0 || _titleBarLogoImage == null)
                    throw new InvalidOperationException($"{theme} theme did not restore the custom transparent titlebar shell.");

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

            _sub = "Firefox"; await ShowSectionAsync("browsers");
            await ShowSectionAsync("update");
            await ShowSectionAsync("fixer");
            await ShowSectionAsync("power");
            await ShowSectionAsync("defender");
            await ShowSectionAsync("restore");
            _sub = "Služby"; await ShowSectionAsync("components");
            _sub = "Task Scheduler"; await ShowSectionAsync("components");
            await ShowSectionAsync("debloat");
            await RunNeutralStartupTitlebarSmokeAsync();
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
        InvalidateBrowserPageCache();
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
        AppThemeService.SavePreference(preference);
        string current = _section;
        _navButtons.Clear();
        _subSelectorCache.Clear();

        // BuildShell creates a brand-new titlebar tree. Never carry primed/content references
        // from the previous theme into the replacement shell.
        _customTitleBarPrimed = false;
        System.Threading.Volatile.Write(ref _startupTitlebarThemeCompleted, false);
        _titleBarLogoImage = null;

        RunWithOverviewPalette(() =>
        {
            BuildShell();
            CompleteDeferredHero();
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