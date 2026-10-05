using System;
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
            TintColor = Windows.UI.Color.FromArgb(255, 8, 13, 22),
            TintOpacity = .12f,
            LuminosityOpacity = .08f,
            FallbackColor = Windows.UI.Color.FromArgb(255, 15, 20, 29)
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
    private static Brush? _glassPanel, _glassEdge, _glassBlue, _glassBlueEdge;
    private static Brush GlassGradient(Windows.UI.Color top, Windows.UI.Color bottom)
    {
        var b = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0,0), EndPoint = new Windows.Foundation.Point(0,1) };
        b.GradientStops.Add(new GradientStop { Color = top, Offset = 0 });
        b.GradientStops.Add(new GradientStop { Color = bottom, Offset = 1 });
        return b;
    }
    private static Brush GlassPanelBrush() => _glassPanel ??= GlassGradient(
        Windows.UI.Color.FromArgb(45, 122, 156, 187), Windows.UI.Color.FromArgb(62, 16, 36, 53));
    private static Brush GlassEdgeBrush() => _glassEdge ??= GlassGradient(
        Windows.UI.Color.FromArgb(174, 230, 244, 255), Windows.UI.Color.FromArgb(75, 202, 228, 246));
    private static Brush GlassBlueBrush() => _glassBlue ??= GlassGradient(
        Windows.UI.Color.FromArgb(255, 139, 222, 255), Windows.UI.Color.FromArgb(255, 54, 168, 243));
    private static Brush GlassBlueEdgeBrush() => _glassBlueEdge ??= GlassGradient(
        Windows.UI.Color.FromArgb(185, 178, 222, 255), Windows.UI.Color.FromArgb(60, 95, 164, 248));

    private void UpdateGlassShell()
    {
        bool glass = _darkTheme;
        AppTitleBar.Background = glass ? GlassPanelBrush() : BuildCustomTitleBarBackground();
        AppTitleBar.Height = glass ? 50 : 36;
        if (AppTitleBar.Children.Count > 0 && AppTitleBar.Children[0] is StackPanel brand)
        {
            foreach (var child in brand.Children)
            {
                if (child is Image logo) { logo.Width = glass ? 26 : 20; logo.Height = glass ? 26 : 20; }
                if (child is TextBlock text && text.Text == "Spankyho Optimizer") text.FontSize = glass ? 18 : 12.8;
            }
        }
        AppTitleBar.Padding = new Thickness(glass ? 24 : 12, 0, 142, 0);
        RootGrid.Background = glass ? ThemeColor(0,0,0,0) : SurfaceAltBrush();
        _pageScroll.Background = ThemeColor(0,0,0,0);
        PageHost.Background = ThemeColor(0,0,0,0);
        if (_mainTabsShell != null)
        {
            _mainTabsShell.Background = glass ? ThemeColor(32, 8, 28, 45) : SurfaceAltBrush();
            _mainTabsShell.Width = ShellSidebarWidth;
            _mainTabsShell.Margin = new Thickness(0, glass ? 50 : 38, 0, 0);
            _mainTabsShell.Padding = new Thickness(glass ? 8 : 10, glass ? 28 : 16, glass ? 8 : 10, 16);
            CategoryBar.Spacing = glass ? 6 : 4;
            foreach (var button in _categoryButtons.Values) ApplyGlassNavigationGeometry(button, glass);
            _mainTabsShell.BorderBrush = _darkTheme ? GlassEdgeBrush() : BorderThemeBrush();
        }
    }

    private static void ApplyGlassNavigationGeometry(Button button, bool glass)
    {
        button.MinHeight = glass ? 62 : 36;
        button.Padding = glass ? new Thickness(16,10,12,10) : new Thickness(8,3,8,3);
        button.CornerRadius = new CornerRadius(glass ? 20 : 16);
        if (button.Content is StackPanel row && row.Children.Count > 1)
        {
            row.Spacing = glass ? 14 : 9;
            if (row.Children[0] is Grid icon) {
                icon.Width = glass ? 34 : 22; icon.Height = glass ? 34 : 22;
                foreach (var visual in icon.Children) if (visual is Image image) { image.Width = glass ? 34 : 22; image.Height = glass ? 34 : 22; }
            }
            if (row.Children[1] is TextBlock text) { text.FontSize = glass ? 18 : 13; text.MaxWidth = glass ? 184 : 112; }
        }
        if (glass && Equals(button.Tag, _themeSection)) button.Background = GlassGradient(
            Windows.UI.Color.FromArgb(120,69,166,240), Windows.UI.Color.FromArgb(40,15,64,108));
    }

    private static System.Collections.Generic.List<SpankyhoOptimizer.Models.TweakDefinition> GlassSystemTweaks(System.Collections.Generic.List<SpankyhoOptimizer.Models.TweakDefinition> system)
    {
        var result = new System.Collections.Generic.List<SpankyhoOptimizer.Models.TweakDefinition>(system);
        foreach (var t in SpankyhoOptimizer.Services.CatalogService.GetTweaks("ui"))
            if (t.Id is "ui.taskanim" or "ui.transparency") result.Add(t);
        foreach (var t in SpankyhoOptimizer.Services.CatalogService.GetTweaks("privacy"))
            if (t.Id is "privacy.telemetry" or "privacy.adid" or "privacy.tailored" or "privacy.activity") result.Add(t);
        return result;
    }
    private static string GlassSystemGroup(SpankyhoOptimizer.Models.TweakDefinition t) => t.Id switch
    {
        "ui.taskanim" or "ui.transparency" or "sys.gamemode" or "sys.gamedvr" => "Windows",
        "privacy.telemetry" or "privacy.adid" or "privacy.tailored" or "privacy.activity" => "Soukromí",
        _ => t.Group
    };
    private static int GlassSystemOrder(SpankyhoOptimizer.Models.TweakDefinition t) => t.Id switch
    {
        "ui.taskanim" => 0, "ui.transparency" => 1, "sys.gamemode" => 2, "sys.gamedvr" => 3,
        "privacy.telemetry" => 4, "privacy.adid" => 5, "privacy.tailored" => 6, "privacy.activity" => 7, _ => 8
    };

    private static Grid CreateGlassGroupGrid()
    {
        var grid = new Grid { Tag = "glass-groups", ColumnSpacing = 24, RowSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new StackPanel { Spacing = 24 });
        grid.Children.Add(new StackPanel { Spacing = 24 });
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