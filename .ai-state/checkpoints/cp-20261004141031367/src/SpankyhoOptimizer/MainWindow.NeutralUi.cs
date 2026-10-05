using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // One neutral design system covers every page, including Overview.
    private static bool UseCompactSectionChrome =>
        true;
    private static bool UseNeutralSectionChrome => _darkTheme && UseCompactSectionChrome;

    private static readonly AppThemePalette NeutralBlackPalette = new(
        new(241,241,241), new(176,176,176),
        new(0,0,0), new(4,4,4), new(0,0,0), new(24,24,24),
        new(99,161,212), new(8,17,24), new(99,161,212), new(99,161,212),
        new(7,7,7), new(2,2,2), new(54,92,122),
        new(112,184,140), new(221,176,88), new(99,161,212),
        new(8,8,8), new(5,5,5), new(0,0,0));

    private static AppThemePalette SectionPalette => UseNeutralSectionChrome ? NeutralBlackPalette : _themePalette;

    private static void RunWithOverviewPalette(Action factory)
    {
        string original = _themeSection;
        try { _themeSection = "overview"; factory(); }
        finally { _themeSection = original; }
    }

    private static void RunWithNeutralPrewarmPalette(Action factory)
    {
        string original = _themeSection;
        try
        {
            // Factories are synchronous on the UI thread; restore before any navigation.
            _themeSection = "section-prewarm";
            factory();
        }
        finally { _themeSection = original; }
    }
    private static Brush? _neutralCardSurface;
    private static Brush? _neutralCardEdge;
    private static Brush? _neutralPrimarySurface;
    private static Brush? _neutralWorkspaceSurface;

    private static Brush NeutralCardSurface() => _neutralCardSurface ??= NeutralMaterialBrush(5, 1, 0);
    private static Brush NeutralCardEdge() => _neutralCardEdge ??= NeutralMaterialBrush(42, 20, 6);
    private static Brush NeutralPrimarySurface() => _neutralPrimarySurface ??= NeutralMaterialBrush(10, 4, 0);

    private static Brush NeutralMaterialBrush(byte top, byte body, byte bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, top, top, top), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, body, body, body), Offset = .20 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, bottom, bottom, bottom), Offset = 1 });
        return brush;
    }

    private static Brush NeutralDepthBrush(byte top, byte bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, top, top, top), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, bottom, bottom, bottom), Offset = 1 });
        return brush;
    }

    // Cached brushes belong to visual properties. Each resource key owns its own
    // brush so theme replacement cannot re-parent an object from another dictionary.
    private static void SetOwnedThemeBrush(ResourceDictionary resources, string key, SolidColorBrush source)
        => resources[key] = new SolidColorBrush(source.Color);

    private static void ApplyNeutralControlResources(ResourceDictionary resources)
    {
        // Reset these shared row resources on theme round-trips as well.
        SetOwnedThemeBrush(resources, "SpankyRowSurfaceBrush", _darkTheme ? ThemeColor(0,0,0) : ThemeColor(255,255,255));
        SetOwnedThemeBrush(resources, "SpankyRowBorderBrush", _darkTheme ? ThemeColor(28,28,28) : ThemeColor(218,222,232));
        SetOwnedThemeBrush(resources, "SpankyRowAccentSurfaceBrush", _darkTheme ? ThemeColor(2,2,2) : ThemeColor(244,246,250));
        SetOwnedThemeBrush(resources, "SpankyRowBadgeSurfaceBrush", _darkTheme ? ThemeColor(10,10,10) : ThemeColor(234,241,253));
        SetOwnedThemeBrush(resources, "SpankyControlOnBrush", _darkTheme ? ThemeColor(44,107,154) : PaletteBrush(_themePalette.PrimaryButton));
        resources["ButtonBackground"] = _darkTheme ? NeutralDepthBrush(26,13) : ThemeColor(245,246,248);
        resources["ButtonBackgroundPointerOver"] = _darkTheme ? NeutralDepthBrush(36,21) : ThemeColor(232,235,240);
        resources["ButtonBackgroundPressed"] = _darkTheme ? NeutralDepthBrush(14,9) : ThemeColor(220,225,232);
        SetOwnedThemeBrush(resources, "ButtonBorderBrush", _darkTheme ? ThemeColor(50,50,50) : ThemeColor(200,205,213));
        SetOwnedThemeBrush(resources, "ButtonBorderBrushPointerOver", _darkTheme ? ThemeColor(86,86,86) : ThemeColor(160,170,182));
        SetOwnedThemeBrush(resources, "ButtonBorderBrushPressed", _darkTheme ? ThemeColor(38,38,38) : ThemeColor(145,155,167));
        SetOwnedThemeBrush(resources, "ButtonForegroundPointerOver", TextPrimaryBrush());
        SetOwnedThemeBrush(resources, "ButtonForegroundPressed", TextPrimaryBrush());
        // Disabled/busy labels remain readable without looking actionable.
        SetOwnedThemeBrush(resources, "ButtonForegroundDisabled", _darkTheme ? ThemeColor(152,152,152) : ThemeColor(95,95,95));
        SetOwnedThemeBrush(resources, "AccentButtonForegroundDisabled", _darkTheme ? ThemeColor(152,152,152) : ThemeColor(95,95,95));
        SetOwnedThemeBrush(resources, "ButtonBackgroundDisabled", _darkTheme ? ThemeColor(7,7,7) : ThemeColor(240,240,240));
        SetOwnedThemeBrush(resources, "AccentButtonBackgroundDisabled", _darkTheme ? ThemeColor(7,7,7) : ThemeColor(240,240,240));
        SetOwnedThemeBrush(resources, "ButtonBorderBrushDisabled", _darkTheme ? ThemeColor(28,28,28) : ThemeColor(200,200,200));
        if (!_darkTheme) return;
        resources["AccentFillColorDefault"] = Windows.UI.Color.FromArgb(255,44,107,154);
        resources["AccentFillColorSecondary"] = Windows.UI.Color.FromArgb(255,61,129,177);
        resources["AccentFillColorTertiary"] = Windows.UI.Color.FromArgb(255,31,83,124);
        resources["AccentFillColorDisabled"] = Windows.UI.Color.FromArgb(102,44,107,154);
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOnDisabled", ThemeColor(102,44,107,154));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOnPointerOver", ThemeColor(125,183,223));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOnPressed", ThemeColor(49,115,159));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOnDisabled", ThemeColor(102,99,161,212));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOffPressed", ThemeColor(13,13,13));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOffPointerOver", ThemeColor(96,96,96));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOffPressed", ThemeColor(28,28,28));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundStrokeCheckedPointerOver", ThemeColor(125,183,223));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundStrokeCheckedPressed", ThemeColor(49,115,159));
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedFillPressed", ThemeColor(31,83,124));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOn", ThemeColor(44,107,154));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOnPointerOver", ThemeColor(61,129,177));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOnPressed", ThemeColor(31,83,124));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOn", ThemeColor(99,161,212));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOff", ThemeColor(6,6,6));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOffPointerOver", ThemeColor(14,14,14));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOff", ThemeColor(40,40,40));
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOn", ThemeColor(250,250,250));
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOff", ThemeColor(207,207,207));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundFillChecked", ThemeColor(44,107,154));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundFillCheckedPointerOver", ThemeColor(61,129,177));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundFillCheckedPressed", ThemeColor(31,83,124));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundStrokeChecked", ThemeColor(99,161,212));
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedFill", ThemeColor(44,107,154));
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedFillPointerOver", ThemeColor(61,129,177));
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedStroke", ThemeColor(99,161,212));
    }

    private Border BuildNeutralSectionIntro(string title, string subtitle, UIElement? actions = null)
    {
        var grid = new Grid { ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 30, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
            stack.Children.Add(new TextBlock
            {
                Text = subtitle, FontSize = 13, Foreground = TextSecondaryBrush(),
                TextWrapping = TextWrapping.Wrap
            });
        grid.Children.Add(stack);
        if (actions != null)
        {
            if (actions is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center;
            if (actions is FrameworkElement actionElement) Grid.SetColumn(actionElement, 1);
            grid.Children.Add(actions);
        }
        return new Border { Tag = "workspace-intro", Padding = new Thickness(0, 12, 0, 24), Child = grid };
    }

    private Border BuildWorkspaceSurface(string title, string subtitle, UIElement content)
    {
        var heading = new StackPanel { Spacing = 8 };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(subtitle))
            heading.Children.Add(new TextBlock { Text = subtitle, FontSize = 12.5, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        UIElement body;
        if ((RootGrid?.ActualWidth ?? StartupClientWidth) >= 1180)
        {
            var columns = new Grid { ColumnSpacing = 28 };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            var contentHost = new Border { Child = content };
            columns.Children.Add(heading); Grid.SetColumn(contentHost, 1); columns.Children.Add(contentHost);
            body = columns;
        }
        else
        {
            var stack = new StackPanel { Spacing = 20 };
            stack.Children.Add(heading); stack.Children.Add(content); body = stack;
        }
        return new Border { Background = _darkTheme ? (_neutralWorkspaceSurface ??= NeutralMaterialBrush(10, 3, 0)) : SurfaceAltBrush(), BorderBrush = _darkTheme ? NeutralCardEdge() : SectionBorderBrush(),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16,20,16,20), Margin = new Thickness(0,0,0,10), Child = body };
    }
}

