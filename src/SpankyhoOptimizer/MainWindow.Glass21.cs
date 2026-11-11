using System;
using System.Collections.Generic;
using System.Linq;
using SpankyhoOptimizer.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SpankyhoOptimizer;

internal sealed class OptimizerGlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private IDisposable? _elementTarget;
    private SystemBackdropConfiguration? _defaultConfiguration;
    private SystemBackdropConfiguration? _configuration;
    internal bool IsControllerReady => _controller != null;
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _elementTarget = target as IDisposable;
        if (!DesktopAcrylicController.IsSupported()) return;
        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
            // Cool light-blue glass instead of the old purple/near-black cast. Keep the tint
            // deliberately weak so the real desktop acrylic remains visible through the shell.
            TintColor = Windows.UI.Color.FromArgb(255, 78, 166, 218),
            TintOpacity = MainWindow.IsSolidDarkTheme ? MainWindow.SolidDarkTintOpacity : MainWindow.GlassTintOpacity,
            LuminosityOpacity = MainWindow.IsSolidDarkTheme ? MainWindow.SolidDarkLuminosityOpacity : MainWindow.GlassLuminosityOpacity,
            // Never flash an opaque fallback surface during activation/deactivation.
            FallbackColor = Windows.UI.Color.FromArgb(0, 0, 0, 0)
        };
        _defaultConfiguration = GetDefaultSystemBackdropConfiguration(target, root);
        // WinUI owns and updates its default object on every activation transition.
        // Never pass that mutable object to a controller with an always-active material policy.
        var configuration = _configuration = new SystemBackdropConfiguration
        {
            Theme = _defaultConfiguration.Theme,
            IsHighContrast = _defaultConfiguration.IsHighContrast
        };
        // Keep acrylic visually active while a lightweight updater/toast window owns focus.
        // Otherwise Windows switches the main Optimizer backdrop to the opaque fallback color.
        configuration.IsInputActive = true;
        _controller.SetSystemBackdropConfiguration(configuration);
        _controller.AddSystemBackdropTarget(target);
    }
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, root);
        if (_configuration == null) return;
        var defaults = GetDefaultSystemBackdropConfiguration(target, root);
        _defaultConfiguration = defaults;
        // Focus changes notify every backdrop (including each Overview card).
        // The independent active-material policy means most notifications do
        // not change any pixel. Avoid three redundant WinRT/compositor writes
        // per native Acrylic controller unless configuration actually changed.
        if (_configuration.Theme != defaults.Theme)
            _configuration.Theme = defaults.Theme;
        if (_configuration.IsHighContrast != defaults.IsHighContrast)
            _configuration.IsHighContrast = defaults.IsHighContrast;
        if (!_configuration.IsInputActive)
            _configuration.IsInputActive = true;
    }

    internal void ReleaseDetachedElementTarget()
    {
        // SystemBackdropElement drops its C++ reference on unload. A managed projection
        // can survive until the CLR finalizer: explicitly close the detached link here
        // so its thread-affine InputObject resources are released on the XAML thread.
        var target = _elementTarget;
        _elementTarget = null;
        target?.Dispose();
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        if (_controller != null)
        {
            _controller.RemoveSystemBackdropTarget(target);
            _controller.Dispose();
            _controller = null;
        }
        _defaultConfiguration = null;
        _configuration = null;
        base.OnTargetDisconnected(target);
    }
}

internal sealed class UpdatePopupGlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private IDisposable? _elementTarget;
    private SystemBackdropConfiguration? _defaultConfiguration;
    private SystemBackdropConfiguration? _configuration;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _elementTarget = target as IDisposable;
        if (!DesktopAcrylicController.IsSupported()) return;

        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
            // Lighter smoked-glass toast: subdued neutral charcoal tint
            // lets more actual desktop acrylic show through. No blue bars.
            TintColor = Windows.UI.Color.FromArgb(255, 23, 28, 37),
            TintOpacity = .045f,
            LuminosityOpacity = .11f,
            FallbackColor = Windows.UI.Color.FromArgb(0, 0, 0, 0)
        };
        _defaultConfiguration = GetDefaultSystemBackdropConfiguration(target, root);
        // WinUI owns and updates its default object on every activation transition.
        // Never pass that mutable object to a controller with an always-active material policy.
        var configuration = _configuration = new SystemBackdropConfiguration
        {
            Theme = _defaultConfiguration.Theme,
            IsHighContrast = _defaultConfiguration.IsHighContrast
        };
        configuration.IsInputActive = true;
        _controller.SetSystemBackdropConfiguration(configuration);
        _controller.AddSystemBackdropTarget(target);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, root);
        if (_configuration == null) return;
        var defaults = GetDefaultSystemBackdropConfiguration(target, root);
        _defaultConfiguration = defaults;
        // Focus changes notify every backdrop (including each Overview card).
        // The independent active-material policy means most notifications do
        // not change any pixel. Avoid three redundant WinRT/compositor writes
        // per native Acrylic controller unless configuration actually changed.
        if (_configuration.Theme != defaults.Theme)
            _configuration.Theme = defaults.Theme;
        if (_configuration.IsHighContrast != defaults.IsHighContrast)
            _configuration.IsHighContrast = defaults.IsHighContrast;
        if (!_configuration.IsInputActive)
            _configuration.IsInputActive = true;
    }

    internal void ReleaseDetachedElementTarget()
    {
        var target = _elementTarget;
        _elementTarget = null;
        target?.Dispose();
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        if (_controller != null)
        {
            _controller.RemoveSystemBackdropTarget(target);
            _controller.Dispose();
            _controller = null;
        }
        _defaultConfiguration = null;
        _configuration = null;
        base.OnTargetDisconnected(target);
    }
}

public sealed partial class MainWindow
{
    private static bool UseGlassChrome => _darkTheme && !IsSolidDarkTheme;
    private static bool UseDarkAcrylicBackdrop => _darkTheme;

    internal static bool IsSolidDarkTheme => _themePreference == AppThemePreference.SolidDark;
    internal const float GlassTintOpacity = .055f;
    internal const float SolidDarkTintOpacity = .16f;
    internal const float GlassLuminosityOpacity = .16f;
    internal const float SolidDarkLuminosityOpacity = .35f;
    private static Brush? _glassPanel, _glassEdge, _glassBlue, _glassBlueEdge, _sidebarGlass;
    // Native WinUI gradient objects are immutable after construction and may be shared
    // by multiple detached cards. Keep this UI-thread-only cache bounded per theme.
    private static readonly Dictionary<ulong, Brush> GlassGradientCache = new(32);
    private static Brush GlassGradient(Windows.UI.Color top, Windows.UI.Color bottom)
    {
        static uint Argb(Windows.UI.Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        ulong key = ((ulong)Argb(top) << 32) | Argb(bottom);
        if (GlassGradientCache.TryGetValue(key, out var existing)) return existing;
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0,0), EndPoint = new Windows.Foundation.Point(0,1) };
        brush.GradientStops.Add(new GradientStop { Color = top, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = bottom, Offset = 1 });
        if (GlassGradientCache.Count >= 128) GlassGradientCache.Clear();
        GlassGradientCache[key] = brush;
        return brush;
    }
    // Preserve the native gradient surface contract with identical low-alpha stops:
    // visually uniform glass without the old bright-to-dark banding.
    private static Brush GlassPanelBrush() => _glassPanel ??= GlassGradient(
        Windows.UI.Color.FromArgb(20, 82, 132, 164), Windows.UI.Color.FromArgb(20, 82, 132, 164));

    // Central semantic glass tokens. Keep opacity low enough that the native acrylic backdrop
    // remains visible; individual pages should not invent their own near-black surfaces.
    private static SystemBackdropElement CreateNativeGlassElement(CornerRadius radius)
    {
        var element = new SystemBackdropElement
        {
            CornerRadius = radius,
            SystemBackdrop = new OptimizerGlassBackdrop(),
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        // Native backdrop/input ownership must end on the UI thread, not in the CLR
        // finalizer. Cached cards get a fresh material when attached again.
        element.Unloaded += static (sender, _) =>
        {
            var unloaded = (SystemBackdropElement)sender;
            // WinUI may deliver a queued Unloaded after the element was reattached.
            if (unloaded.IsLoaded) return;
            var previous = unloaded.SystemBackdrop as OptimizerGlassBackdrop;
            unloaded.SystemBackdrop = null;
            previous?.ReleaseDetachedElementTarget();
        };
        element.Loaded += static (sender, _) =>
        {
            var loaded = (SystemBackdropElement)sender;
            loaded.SystemBackdrop ??= new OptimizerGlassBackdrop();
        };
        return element;
    }

    private static Brush GlassSurfaceLowBrush() => ThemeColor(14, 58, 112, 148);
    private static Brush GlassSurfaceBrush() => GlassPanelBrush();
    private static Brush GlassSurfaceElevatedBrush() => GlassGradient(
        Windows.UI.Color.FromArgb(33, 150, 216, 246), Windows.UI.Color.FromArgb(16, 58, 112, 146));
    private static Brush GlassHeroSurfaceBrush() => GlassGradient(
        Windows.UI.Color.FromArgb(30, 160, 224, 250), Windows.UI.Color.FromArgb(15, 54, 108, 148));
    private static Brush GlassDialogInnerSurfaceBrush() => ThemeColor(24, 66, 122, 158);
    private static SolidColorBrush GlassOverlayBrush(byte alpha = 64) => ThemeColor(
        (byte)Math.Clamp((int)alpha, 48, 82), 12, 42, 66);
    private static SolidColorBrush GlassTrackBrush() => ThemeColor(30, 74, 142, 184);
    private static Brush GlassBorderBrush() => GlassEdgeBrush();
    private static SolidColorBrush GlassBorderStrongBrush() => ThemeColor(86, 218, 226, 244);
    private static SolidColorBrush GlassHoverBrush() => ThemeColor(34, 88, 164, 214);
    private static SolidColorBrush GlassPressedBrush() => ThemeColor(46, 70, 140, 194);
    private static SolidColorBrush GlassAccentTintBrush() => ThemeColor(38, 92, 186, 236);

    private static Brush GlassEdgeBrush() => _glassEdge ??= GlassGradient(
        Windows.UI.Color.FromArgb(88, 220, 239, 250), Windows.UI.Color.FromArgb(36, 104, 150, 178));
    private static Brush GlassBlueBrush() => _glassBlue ??= GlassGradient(
        Windows.UI.Color.FromArgb(94, 143, 218, 252), Windows.UI.Color.FromArgb(64, 57, 153, 236));
    private static Brush GlassBlueEdgeBrush() => _glassBlueEdge ??= GlassGradient(
        Windows.UI.Color.FromArgb(138, 181, 229, 255), Windows.UI.Color.FromArgb(70, 98, 166, 241));

    private static Brush SidebarGlassBrush()
    {
        if (_sidebarGlass != null) return _sidebarGlass;
        var b = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(14, 14, 40, 58), Offset = 0 });
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(16, 18, 50, 72), Offset = .82 });
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(12, 92, 178, 224), Offset = 1 });
        _sidebarGlass = b;
        return b;
    }

    // A single restrained selection material; action buttons retain their accent.
    private static Brush? _glassNavigation;
    private static Brush GlassNavigationBrush() => _glassNavigation ??= GlassGradient(
        Windows.UI.Color.FromArgb(110, 74, 184, 242),
        Windows.UI.Color.FromArgb(62, 18, 64, 102));

    private Grid? _glassShellAppliedRoot;
    private bool _glassShellAppliedGlassChrome;

    private void UpdateGlassShell()
    {
        bool glass = UseGlassChrome;
        if (ReferenceEquals(_glassShellAppliedRoot, RootGrid) && _glassShellAppliedGlassChrome == glass)
            return;

        AppTitleBar.Background = glass ? ThemeColor(0,0,0,0) : BuildCustomTitleBarBackground();
        AppTitleBar.Height = glass ? 44 : 36;
        if (AppTitleBar.Children.Count > 0 && AppTitleBar.Children[0] is StackPanel brand)
        {
            foreach (var child in brand.Children)
            {
                if (child is Image logo) { logo.Width = glass ? 24 : 20; logo.Height = glass ? 24 : 20; }
                if (child is TextBlock text && text.Text.StartsWith("Glass Optimizer", StringComparison.Ordinal)) text.FontSize = glass ? 15.5 : 12.8;
            }
        }
        AppTitleBar.Padding = new Thickness(glass ? 20 : 12, 0, 142, 0);
        RootGrid.Background = glass ? ThemeColor(0,0,0,0) : SurfaceAltBrush();
        _pageScroll.Background = ThemeColor(0,0,0,0);
        PageHost.Background = ThemeColor(0,0,0,0);
        if (_mainTabsShell != null)
        {
            _mainTabsShell.Background = glass ? SidebarGlassBrush() : SurfaceAltBrush();
            _mainTabsShell.Width = ShellSidebarWidth;
            _mainTabsShell.Margin = new Thickness(0, glass ? ShellTitleBarHeight : 38, 0, 0);
            _mainTabsShell.Padding = new Thickness(glass ? 8 : 10, glass ? 14 : 16, glass ? 8 : 10, 14);
            CategoryBar.Spacing = glass ? 4 : 4;
            foreach (var button in _categoryButtons.Values) ApplyGlassNavigationGeometry(button, glass);
            _mainTabsShell.BorderBrush = glass ? ThemeColor(0,0,0,0) : BorderThemeBrush();
            _mainTabsShell.BorderThickness = glass ? new Thickness(0) : new Thickness(0,0,1,0);
        }

        _glassShellAppliedRoot = RootGrid;
        _glassShellAppliedGlassChrome = glass;
    }

    private int _glassActivationRepairQueued;

    private void QueueGlassActivationRepair()
    {
        if (_windowLifetimeCts.IsCancellationRequested || !UseGlassChrome) return;

        // Activated already applies DWM/titlebar policy once. Reapplying the
        // native window region twice more on the same focus event synchronously
        // blocks the WinUI input thread and delays dragging the main window.
        // XAML shell visuals do not change merely because focus changes:
        // retain cached brushes and geometry unless theme/root actually changed.
        UpdateGlassShell();

        if (System.Threading.Interlocked.Exchange(ref _glassActivationRepairQueued, 1) != 0)
            return;

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                // Closed may run after this repair was queued; never touch its disposed native window.
                if (_windowLifetimeCts.IsCancellationRequested) return;
                bool rebound = false;
                if (SystemBackdrop is not OptimizerGlassBackdrop backdrop ||
                    (DesktopAcrylicController.IsSupported() && !backdrop.IsControllerReady))
                {
                    SystemBackdrop = null;
                    SystemBackdrop = new OptimizerGlassBackdrop();
                    rebound = true;
                }

                // Only a genuinely recreated backdrop requires an extra native
                // region restoration; ordinary activation was already repaired
                // synchronously by ApplyWindowTitleBarTheme.
                if (rebound)
                {
                    UpdateGlassShell();
                    ApplyNativeGlassWindowShape();
                    LogService.Info("window", "GLASS_ACTIVATION_REPAIR_OK rebound=true");
                }
            }
            catch (Exception ex)
            {
                LogService.Error("window", "Glass activation repair failed", ex);
            }
            finally
            {
                System.Threading.Volatile.Write(ref _glassActivationRepairQueued, 0);
            }
        }))
        {
            System.Threading.Volatile.Write(ref _glassActivationRepairQueued, 0);
        }
    }

    private static void VerifyGlassSemanticSurfaceContract()
    {
        if (!UseGlassChrome) return;

        static void CheckSolid(Brush brush, string name, byte maxAlpha)
        {
            if (brush is not SolidColorBrush solid)
                throw new InvalidOperationException($"Glass semantic surface {name} must be a SolidColorBrush.");
            if (solid.Color.A > maxAlpha)
                throw new InvalidOperationException($"Glass semantic surface {name} is too opaque: alpha={solid.Color.A}.");
            if (solid.Color.A > 0 && ((solid.Color.R + solid.Color.G + solid.Color.B) / 3) < 24)
                throw new InvalidOperationException($"Glass semantic surface {name} drifted to near-black.");
        }

        static void CheckGradient(Brush brush, string name, byte maxAlpha)
        {
            if (brush is not LinearGradientBrush gradient || gradient.GradientStops.Count == 0)
                throw new InvalidOperationException($"Glass semantic surface {name} must be a gradient.");
            foreach (var stop in gradient.GradientStops)
            {
                if (stop.Color.A > maxAlpha)
                    throw new InvalidOperationException($"Glass semantic surface {name} is too opaque: alpha={stop.Color.A}.");
                if (stop.Color.A > 0 && ((stop.Color.R + stop.Color.G + stop.Color.B) / 3) < 24)
                    throw new InvalidOperationException($"Glass semantic surface {name} drifted to near-black.");
            }
        }

        CheckSolid(GlassSurfaceLowBrush(), "surface-low", 64);
        CheckGradient(GlassSurfaceBrush(), "surface", 72);
        CheckGradient(GlassSurfaceElevatedBrush(), "surface-elevated", 80);
        CheckGradient(GlassHeroSurfaceBrush(), "hero", 80);
        CheckSolid(GlassDialogInnerSurfaceBrush(), "dialog-inner", 72);
        CheckSolid(GlassOverlayBrush(), "overlay", 96);
        CheckSolid(GlassTrackBrush(), "track", 72);
        CheckSolid(GlassHoverBrush(), "hover", 72);
        CheckSolid(GlassPressedBrush(), "pressed", 80);
        CheckGradient(GlassBlueBrush(), "primary-action", 96);
        CheckGradient(GlassBlueEdgeBrush(), "primary-action-edge", 140);

        if (DialogModalCardBrush() is not AcrylicBrush acrylic ||
            acrylic.TintOpacity > 0.24 ||
            acrylic.TintLuminosityOpacity > 0.28)
            throw new InvalidOperationException("Dialog acrylic contract regressed.");

        LogService.Info("visual-qa", "GLASS_SEMANTIC_SURFACE_CONTRACT_OK");
    }

    private void VerifyGlassBackdropLifecycle()
    {
        if (!UseGlassChrome) return;
        static void Transparent(Brush? brush, string name)
        {
            if (brush is not SolidColorBrush { Color.A: 0 })
                throw new InvalidOperationException($"Glass backdrop blocked by {name}: {brush?.GetType().Name}.");
        }
        if (SystemBackdrop is not OptimizerGlassBackdrop)
            throw new InvalidOperationException("Native glass backdrop missing.");
        VerifyRoundedGlassWindow();
        VerifyGlassSemanticSurfaceContract();
        Transparent(BuildShellBackdrop(), "initial/rebuilt shell");
        UpdateGlassShell();
        bool completed = _shellFirstPaintCompleted;
        try
        {
            _shellFirstPaintCompleted = false;
            CompleteDeferredShellBackdrop();
            Transparent(RootGrid.Background, "root after deferred paint");
            Transparent(_pageScroll.Background, "scroll after deferred paint");
            Transparent(PageHost.Background, "page after deferred paint");
            if (_mainTabsShell?.Background is not LinearGradientBrush sidebar ||
                sidebar.GradientStops.Any(stop => stop.Color.A == 255))
                throw new InvalidOperationException("Sidebar blocks native glass.");
        }
        finally { _shellFirstPaintCompleted = completed; }
        LogService.Info("performance", "GLASS_BACKDROP_LIFECYCLE_SMOKE_OK native=true transparent=true deferred=true");
    }

    private static void ApplyGlassNavigationGeometry(Button button, bool glass)
    {
        button.MinHeight = glass ? 48 : 36;
        button.Padding = glass ? new Thickness(8,6,8,6) : new Thickness(8,3,8,3);
        button.CornerRadius = new CornerRadius(glass ? 20 : 16);
        if (button.Content is StackPanel row && row.Children.Count > 1)
        {
            row.Spacing = glass ? 11 : 9;
            if (row.Children[0] is Grid icon) {
                icon.Width = 30; icon.Height = 30;
                foreach (var visual in icon.Children)
                    if (visual is Image image) { image.Width = 28; image.Height = 28; image.Opacity = 1; }
            }
            if (row.Children[1] is TextBlock text) { text.Tag = "UiNavigationSize"; text.FontSize = UiFontSize("UiNavigationSize"); text.MaxWidth = glass ? 122 : 112; }
        }
    }

    // Catalog lists remain stable until the cache entry itself is replaced.
    // Reuse the combined 56-item System list across navigation and XAML-cache
    // reattachment; avoid rebuilding it before every foreground navigation.
    private System.Collections.Generic.List<SpankyhoOptimizer.Models.TweakDefinition>? _cachedGlassSystemSource;
    private System.Collections.Generic.List<SpankyhoOptimizer.Models.TweakDefinition>? _cachedGlassSystemMerged;

    private System.Collections.Generic.List<SpankyhoOptimizer.Models.TweakDefinition> GlassSystemTweaks(System.Collections.Generic.List<SpankyhoOptimizer.Models.TweakDefinition> system)
    {
        if (ReferenceEquals(_cachedGlassSystemSource, system) && _cachedGlassSystemMerged != null)
            return _cachedGlassSystemMerged;
        var supplements = SpankyhoOptimizer.Services.CatalogService.GetGlassSystemSupplementTweaks();
        var result = new System.Collections.Generic.List<SpankyhoOptimizer.Models.TweakDefinition>(system.Count + supplements.Count);
        result.AddRange(system);
        foreach (var tweak in supplements)
            result.Add(tweak);
        _cachedGlassSystemSource = system;
        _cachedGlassSystemMerged = result;
        return result;
    }
    private static string GlassSystemGroup(SpankyhoOptimizer.Models.TweakDefinition t) => t.Id switch
    {
        "ui.taskanim" or "ui.transparency" or "sys.gamemode" or "sys.gamedvr" => "Windows",
        "privacy.telemetry" or "privacy.adid" or "privacy.tailored" or "privacy.activity" => "Soukromí",
        "sys.wpbt" or "sys.hibernation" or "ui.endtask" or "ui.copydetails" or "ui.mergeconflicts" or
        "ui.persistfolders" or "ui.mostused" or "privacy.languageweb" or "privacy.scoobe" => "Windows 26H2",
        _ => t.Group == "Windows" ? "Další nastavení Windows" : t.Group
    };
    private static int GlassSystemOrder(SpankyhoOptimizer.Models.TweakDefinition t) => t.Id switch
    {
        "ui.taskanim" => 0, "ui.transparency" => 1, "sys.gamemode" => 2, "sys.gamedvr" => 3,
        "privacy.telemetry" => 4, "privacy.adid" => 5, "privacy.tailored" => 6, "privacy.activity" => 7, _ => 8
    };

    private static Grid CreateGlassGroupGrid()
    {
        var grid = new Grid { Tag = "glass-groups", ColumnSpacing = 14, RowSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new StackPanel { Spacing = 14 });
        grid.Children.Add(new StackPanel { Spacing = 14 });
        grid.SizeChanged += (_, _) => ArrangeGlassGroups(grid);
        return grid;
    }
    private static void ArrangeGlassGroups(Grid grid)
    {
        bool wide = grid.ActualWidth >= 760;
        // No native GridLength/attached-property writes until the layout
        // actually crosses its breakpoint. Unconditional setters during
        // SizeChanged can cause redundant WinUI measure/arrange cycles.
        var targetWidth = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (!grid.ColumnDefinitions[1].Width.Equals(targetWidth))
            grid.ColumnDefinitions[1].Width = targetWidth;
        if (grid.Children[1] is FrameworkElement right)
        {
            int row = wide ? 0 : 1;
            int column = wide ? 1 : 0;
            if (Grid.GetRow(right) != row) Grid.SetRow(right, row);
            if (Grid.GetColumn(right) != column) Grid.SetColumn(right, column);
        }
    }
    private static void AttachVirtualGroup(Panel target, UIElement group)
    {
        if (target is Grid { Tag: "glass-groups" } grid)
        {
            var left = (StackPanel)grid.Children[0];
            var right = (StackPanel)grid.Children[1];
            (left.Children.Count <= right.Children.Count ? left : right).Children.Add(group);
        }
        else target.Children.Add(group);
    }
}
