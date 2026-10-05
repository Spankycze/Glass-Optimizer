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
            TintOpacity = .56f,
            LuminosityOpacity = .38f,
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
    private static bool UseGlassChrome => _darkTheme && _themeSection != "overview";
    private static Brush? _glassPanel, _glassEdge, _glassBlue, _glassBlueEdge;
    private static Brush GlassGradient(Windows.UI.Color top, Windows.UI.Color bottom)
    {
        var b = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0,0), EndPoint = new Windows.Foundation.Point(0,1) };
        b.GradientStops.Add(new GradientStop { Color = top, Offset = 0 });
        b.GradientStops.Add(new GradientStop { Color = bottom, Offset = 1 });
        return b;
    }
    private static Brush GlassPanelBrush() => _glassPanel ??= GlassGradient(
        Windows.UI.Color.FromArgb(92, 75, 87, 103), Windows.UI.Color.FromArgb(100, 18, 27, 41));
    private static Brush GlassEdgeBrush() => _glassEdge ??= GlassGradient(
        Windows.UI.Color.FromArgb(85, 221, 234, 250), Windows.UI.Color.FromArgb(26, 174, 198, 232));
    private static Brush GlassBlueBrush() => _glassBlue ??= GlassGradient(
        Windows.UI.Color.FromArgb(230, 54, 151, 248), Windows.UI.Color.FromArgb(220, 17, 93, 189));
    private static Brush GlassBlueEdgeBrush() => _glassBlueEdge ??= GlassGradient(
        Windows.UI.Color.FromArgb(185, 178, 222, 255), Windows.UI.Color.FromArgb(60, 95, 164, 248));

    private void UpdateGlassShell()
    {
        bool glass = _darkTheme && _section != "overview";
        RootGrid.Background = glass ? ThemeColor(0,0,0,0) : SurfaceAltBrush();
        _pageScroll.Background = ThemeColor(0,0,0,0);
        PageHost.Background = ThemeColor(0,0,0,0);
        if (_mainTabsShell != null)
        {
            _mainTabsShell.Background = _darkTheme ? ThemeColor(48, 12, 20, 32) : SurfaceAltBrush();
            _mainTabsShell.BorderBrush = _darkTheme ? GlassEdgeBrush() : BorderThemeBrush();
        }
    }

    private static Grid CreateGlassGroupGrid()
    {
        var grid = new Grid { Tag = "glass-groups", ColumnSpacing = 18, RowSpacing = 18 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new StackPanel { Spacing = 18 });
        grid.Children.Add(new StackPanel { Spacing = 18 });
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
