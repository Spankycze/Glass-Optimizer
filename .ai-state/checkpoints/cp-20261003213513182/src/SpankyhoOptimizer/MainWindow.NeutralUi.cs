using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // Overview keeps its original palette; all other pages use compact workspace chrome.
    private static bool UseCompactSectionChrome =>
        !string.Equals(_themeSection, "overview", StringComparison.OrdinalIgnoreCase);
    private static bool UseNeutralSectionChrome => _darkTheme && UseCompactSectionChrome;

    private static readonly AppThemePalette NeutralBlackPalette = new(
        new(241,241,241), new(176,176,176),
        new(16,16,16), new(24,24,24), new(8,8,8), new(48,48,48),
        new(211,211,211), new(32,32,32), new(160,160,160), new(112,112,112),
        new(53,53,53), new(29,29,29), new(78,78,78),
        new(112,184,140), new(221,176,88), new(192,192,192),
        new(16,16,16), new(16,16,16), new(16,16,16));

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

    private static Brush NeutralCardSurface() => _neutralCardSurface ??= NeutralDepthBrush(24, 18);
    private static Brush NeutralCardEdge() => _neutralCardEdge ??= NeutralDepthBrush(53, 35);
    private static Brush NeutralPrimarySurface() => _neutralPrimarySurface ??= NeutralDepthBrush(58, 40);

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
        SetOwnedThemeBrush(resources, "SpankyRowSurfaceBrush", _darkTheme ? ThemeColor(22,22,22) : ThemeColor(255,255,255));
        SetOwnedThemeBrush(resources, "SpankyRowBorderBrush", _darkTheme ? ThemeColor(48,48,48) : ThemeColor(218,222,232));
        SetOwnedThemeBrush(resources, "SpankyRowAccentSurfaceBrush", _darkTheme ? ThemeColor(30,30,30) : ThemeColor(244,246,250));
        SetOwnedThemeBrush(resources, "SpankyRowBadgeSurfaceBrush", _darkTheme ? ThemeColor(38,38,38) : ThemeColor(234,241,253));
        SetOwnedThemeBrush(resources, "SpankyControlOnBrush", _darkTheme ? ThemeColor(82,82,82) : PaletteBrush(_themePalette.PrimaryButton));
        if (!_darkTheme) return;
        resources["AccentFillColorDefault"] = Windows.UI.Color.FromArgb(255,82,82,82);
        resources["AccentFillColorSecondary"] = Windows.UI.Color.FromArgb(255,104,104,104);
        resources["AccentFillColorTertiary"] = Windows.UI.Color.FromArgb(255,65,65,65);
        resources["AccentFillColorDisabled"] = Windows.UI.Color.FromArgb(102,82,82,82);
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOnDisabled", ThemeColor(102,82,82,82));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOnPointerOver", ThemeColor(139,139,139));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOnPressed", ThemeColor(79,79,79));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOnDisabled", ThemeColor(102,117,117,117));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOffPressed", ThemeColor(13,13,13));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOffPointerOver", ThemeColor(96,96,96));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOffPressed", ThemeColor(48,48,48));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundStrokeCheckedPointerOver", ThemeColor(139,139,139));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundStrokeCheckedPressed", ThemeColor(79,79,79));
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedFillPressed", ThemeColor(65,65,65));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOn", ThemeColor(82,82,82));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOnPointerOver", ThemeColor(104,104,104));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOnPressed", ThemeColor(65,65,65));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOn", ThemeColor(117,117,117));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOff", ThemeColor(20,20,20));
        SetOwnedThemeBrush(resources, "ToggleSwitchFillOffPointerOver", ThemeColor(31,31,31));
        SetOwnedThemeBrush(resources, "ToggleSwitchStrokeOff", ThemeColor(62,62,62));
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOn", ThemeColor(250,250,250));
        SetOwnedThemeBrush(resources, "ToggleSwitchKnobFillOff", ThemeColor(207,207,207));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundFillChecked", ThemeColor(82,82,82));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundFillCheckedPointerOver", ThemeColor(104,104,104));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundFillCheckedPressed", ThemeColor(65,65,65));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundStrokeChecked", ThemeColor(117,117,117));
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedFill", ThemeColor(82,82,82));
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedFillPointerOver", ThemeColor(104,104,104));
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedStroke", ThemeColor(117,117,117));
    }

    private Border BuildNeutralSectionIntro(string title, string subtitle, UIElement? actions = null)
    {
        var grid = new Grid { ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stack = new StackPanel { Spacing = 7 };
        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
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
        return new Border { Tag = "workspace-intro", Padding = new Thickness(0, 12, 0, 22), Child = grid };
    }

    private Border BuildWorkspaceSurface(string title, string subtitle, UIElement content)
    {
        var stack = new StackPanel { Spacing = 14 };
        var heading = new StackPanel { Spacing = 5 };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 15.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(subtitle))
            heading.Children.Add(new TextBlock { Text = subtitle, FontSize = 12.5, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(heading);
        stack.Children.Add(new Border { Height = 1, Background = _darkTheme ? ThemeColor(39,39,39) : BorderThemeBrush(), IsHitTestVisible = false });
        stack.Children.Add(content);
        return new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(18,16,18,16), Margin = new Thickness(0,0,0,12), Child = stack };
    }
}

