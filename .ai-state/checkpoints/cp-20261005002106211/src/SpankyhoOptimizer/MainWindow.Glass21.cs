using System;
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
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        if (!DesktopAcrylicController.IsSupported()) return;
        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
            TintColor = Windows.UI.Color.FromArgb(255, 8, 17, 27),
            TintOpacity = .07f,
            LuminosityOpacity = .10f,
            FallbackColor = Windows.UI.Color.FromArgb(255, 7, 12, 19)
        };
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, root));
        _controller.AddSystemBackdropTarget(target);
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        if (_controller != null)
        {
            _controller.RemoveSystemBackdropTarget(target);
            _controller.Dispose();
            _controller = null;
        }
        base.OnTargetDisconnected(target);
    }
}

public sealed partial class MainWindow
{
    private static bool UseGlassChrome => _darkTheme;
    private static Brush? _glassPanel, _glassEdge, _glassBlue, _glassBlueEdge, _sidebarGlass;
    private static Brush GlassGradient(Windows.UI.Color top, Windows.UI.Color bottom)
    {
        var b = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0,0), EndPoint = new Windows.Foundation.Point(0,1) };
        b.GradientStops.Add(new GradientStop { Color = top, Offset = 0 });
        b.GradientStops.Add(new GradientStop { Color = bottom, Offset = 1 });
        return b;
    }
    private static Brush GlassPanelBrush() => _glassPanel ??= GlassGradient(
        Windows.UI.Color.FromArgb(36, 91, 127, 154), Windows.UI.Color.FromArgb(22, 14, 30, 44));
    private static Brush GlassEdgeBrush() => _glassEdge ??= GlassGradient(
        Windows.UI.Color.FromArgb(100, 220, 239, 250), Windows.UI.Color.FromArgb(42, 104, 150, 178));
    private static Brush GlassBlueBrush() => _glassBlue ??= GlassGradient(
        Windows.UI.Color.FromArgb(255, 139, 222, 255), Windows.UI.Color.FromArgb(255, 54, 168, 243));
    private static Brush GlassBlueEdgeBrush() => _glassBlueEdge ??= GlassGradient(
        Windows.UI.Color.FromArgb(185, 178, 222, 255), Windows.UI.Color.FromArgb(60, 95, 164, 248));

    private static Brush SidebarGlassBrush()
    {
        if (_sidebarGlass != null) return _sidebarGlass;
        var b = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(14, 15, 35, 50), Offset = 0 });
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(16, 17, 37, 53), Offset = .82 });
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(10, 83, 144, 196), Offset = 1 });
        _sidebarGlass = b;
        return b;
    }

    // A single restrained selection material; action buttons retain their accent.
    private static Brush? _glassNavigation;
    private static Brush GlassNavigationBrush() => _glassNavigation ??= GlassGradient(
        Windows.UI.Color.FromArgb(116, 72, 119, 157),
        Windows.UI.Color.FromArgb(76, 16, 39, 62));

    private Grid? _glassShellAppliedRoot;
    private bool _glassShellAppliedDarkTheme;

    private void UpdateGlassShell()
    {
        bool glass = _darkTheme;
        if (ReferenceEquals(_glassShellAppliedRoot, RootGrid) && _glassShellAppliedDarkTheme == glass)
            return;

        AppTitleBar.Background = glass ? ThemeColor(0,0,0,0) : BuildCustomTitleBarBackground();
        AppTitleBar.Height = glass ? 50 : 36;
        if (AppTitleBar.Children.Count > 0 && AppTitleBar.Children[0] is StackPanel brand)
        {
            foreach (var child in brand.Children)
            {
                if (child is Image logo) { logo.Width = glass ? 24 : 20; logo.Height = glass ? 24 : 20; }
                if (child is TextBlock text && text.Text == "Spankyho Optimizer") text.FontSize = glass ? 15.5 : 12.8;
            }
        }
        AppTitleBar.Padding = new Thickness(glass ? 24 : 12, 0, 142, 0);
        RootGrid.Background = glass ? ThemeColor(0,0,0,0) : SurfaceAltBrush();
        _pageScroll.Background = ThemeColor(0,0,0,0);
        PageHost.Background = ThemeColor(0,0,0,0);
        if (_mainTabsShell != null)
        {
            _mainTabsShell.Background = glass ? SidebarGlassBrush() : SurfaceAltBrush();
            _mainTabsShell.Width = ShellSidebarWidth;
            _mainTabsShell.Margin = new Thickness(0, glass ? 50 : 38, 0, 0);
            _mainTabsShell.Padding = new Thickness(glass ? 8 : 10, glass ? 14 : 16, glass ? 8 : 10, 14);
            CategoryBar.Spacing = glass ? 4 : 4;
            foreach (var button in _categoryButtons.Values) ApplyGlassNavigationGeometry(button, glass);
            _mainTabsShell.BorderBrush = glass ? ThemeColor(42, 193, 225, 244) : ThemeColor(0,0,0,0);
            _mainTabsShell.BorderThickness = new Thickness(0,0,1,0);
        }

        _glassShellAppliedRoot = RootGrid;
        _glassShellAppliedDarkTheme = glass;
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

    private static System.Collections.Generic.List<SpankyhoOptimizer.Models.TweakDefinition> GlassSystemTweaks(System.Collections.Generic.List<SpankyhoOptimizer.Models.TweakDefinition> system)
    {
        var result = new System.Collections.Generic.List<SpankyhoOptimizer.Models.TweakDefinition>(system);
        foreach (var t in SpankyhoOptimizer.Services.CatalogService.GetGlassSystemSupplementTweaks())
            result.Add(t);
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
        grid.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (grid.Children[1] is FrameworkElement right)
        {
            Grid.SetRow(right, wide ? 0 : 1);
            Grid.SetColumn(right, wide ? 1 : 0);
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