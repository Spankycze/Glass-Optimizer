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
        new(244,246,250), new(196,202,212),
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


    // Main cards share one cached glass material. Small row resources below stay lightweight,
    // but the large surfaces must visibly read as translucent glass.
    private static Brush NeutralCardSurface() => _neutralCardSurface ??= (_darkTheme ? GlassPanelBrush() : NeutralMaterialBrush(5, 1, 0));
    private static Brush NeutralCardEdge() => _neutralCardEdge ??= (_darkTheme ? GlassEdgeBrush() : NeutralMaterialBrush(42, 20, 6));
    private static Brush NeutralPrimarySurface() => _neutralPrimarySurface ??= (_darkTheme ? GlassBlueBrush() : NeutralMaterialBrush(10, 4, 0));

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

    // Each resource key owns its brush. A large part of the native control palette
    // is identical across all dark theme variants: avoid reallocating/reassigning
    // an unchanged owned resource on every theme transition. Never mutate a brush
    // that a live template may still reference, and never share the source brush.
    private static void SetOwnedThemeBrush(ResourceDictionary resources, string key, SolidColorBrush source)
    {
        if (resources.ContainsKey(key) && resources[key] is SolidColorBrush current &&
            current.Color.Equals(source.Color))
            return;
        resources[key] = new SolidColorBrush(source.Color);
    }

    private static void ApplyNeutralControlResources(ResourceDictionary resources)
    {
        // Reset these shared row resources on theme round-trips as well.
        SetOwnedThemeBrush(resources, "SpankyRowSurfaceBrush", _darkTheme ? ThemeColor(16,32,66,88) : ThemeColor(255,255,255));
        SetOwnedThemeBrush(resources, "SpankyRowBorderBrush", _darkTheme ? ThemeColor(68,166,214,240) : ThemeColor(218,222,232));
        SetOwnedThemeBrush(resources, "SpankyRowAccentSurfaceBrush", _darkTheme ? ThemeColor(38,42,96,126) : ThemeColor(244,246,250));
        SetOwnedThemeBrush(resources, "SpankyRowBadgeSurfaceBrush", _darkTheme ? ThemeColor(52,48,104,134) : ThemeColor(234,241,253));
        SetOwnedThemeBrush(resources, "SpankyControlOnBrush", _darkTheme ? ThemeColor(29,127,231) : PaletteBrush(_themePalette.PrimaryButton));
        // Global fallback for legacy buttons without our common button factory.
        // Navigation, switches and color swatches keep their explicit local styles.
        resources["ButtonBackground"] = _darkTheme ? BlueActionSurface(primary: false) : ThemeColor(225,241,255);
        resources["ButtonBackgroundPointerOver"] = _darkTheme ? GlassHoverBrush() : ThemeColor(207,233,252);
        resources["ButtonBackgroundPressed"] = _darkTheme ? GlassPressedBrush() : ThemeColor(179,218,244);
        SetOwnedThemeBrush(resources, "ButtonBorderBrush", _darkTheme ? ThemeColor(87,116,191,234) : ThemeColor(146,195,230));
        SetOwnedThemeBrush(resources, "ButtonBorderBrushPointerOver", _darkTheme ? ThemeColor(130,178,224,250) : ThemeColor(61,153,215));
        SetOwnedThemeBrush(resources, "ButtonBorderBrushPressed", _darkTheme ? ThemeColor(94,127,181,224) : ThemeColor(83,159,211));
        SetOwnedThemeBrush(resources, "ProgressBarBackground", _darkTheme ? GlassTrackBrush() : ThemeColor(218,222,232));
        SetOwnedThemeBrush(resources, "ButtonForegroundPointerOver", TextPrimaryBrush());
        SetOwnedThemeBrush(resources, "ButtonForegroundPressed", TextPrimaryBrush());
        // Disabled/busy labels remain readable without looking actionable.
        SetOwnedThemeBrush(resources, "ButtonForegroundDisabled", _darkTheme ? ThemeColor(205,216,224) : ThemeColor(95,95,95));
        SetOwnedThemeBrush(resources, "AccentButtonForegroundDisabled", _darkTheme ? ThemeColor(205,216,224) : ThemeColor(95,95,95));
        SetOwnedThemeBrush(resources, "ButtonBackgroundDisabled", _darkTheme ? ThemeColor(24,42,82,104) : ThemeColor(240,240,240));
        SetOwnedThemeBrush(resources, "AccentButtonBackgroundDisabled", _darkTheme ? ThemeColor(28,48,92,116) : ThemeColor(240,240,240));
        SetOwnedThemeBrush(resources, "ButtonBorderBrushDisabled", _darkTheme ? ThemeColor(52,92,146,176) : ThemeColor(200,200,200));
        if (!_darkTheme) return;
        resources["AccentFillColorDefault"] = Windows.UI.Color.FromArgb(255,29,127,231);
        resources["AccentFillColorSecondary"] = Windows.UI.Color.FromArgb(255,61,129,177);
        resources["AccentFillColorTertiary"] = Windows.UI.Color.FromArgb(255,31,83,124);
        resources["AccentFillColorDisabled"] = Windows.UI.Color.FromArgb(102,44,107,154);
        // ToggleSwitch colors are owned ONLY by ApplyToggleOn/OffThemeResources:
        // do not overwrite their sky-blue reference style from this fallback.
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundStrokeCheckedPointerOver", ThemeColor(125,183,223));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundStrokeCheckedPressed", ThemeColor(49,115,159));
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedFillPressed", ThemeColor(31,83,124));
        // Never replace the shared ToggleSwitch track/knob while applying
        // theme-neutral button, checkbox and radio resources.
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundFillChecked", ThemeColor(29,127,231));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundFillCheckedPointerOver", ThemeColor(61,129,177));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundFillCheckedPressed", ThemeColor(31,83,124));
        SetOwnedThemeBrush(resources, "CheckBoxCheckBackgroundStrokeChecked", ThemeColor(99,161,212));
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedFill", ThemeColor(29,127,231));
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedFillPointerOver", ThemeColor(61,129,177));
        SetOwnedThemeBrush(resources, "RadioButtonOuterEllipseCheckedStroke", ThemeColor(99,161,212));
    }

    private Border BuildNeutralSectionIntro(string title, string subtitle, UIElement? actions = null)
    {
        var grid = new Grid { ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stack = new StackPanel { Spacing = 7 };
        stack.Children.Add(new TextBlock
        {
            Text = title, Tag = "UiPageTitleSize", FontSize = UiFontSize("UiPageTitleSize"), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
            stack.Children.Add(new TextBlock
            {
                Text = subtitle, Tag = "UiSubtitleSize", FontSize = UiFontSize("UiSubtitleSize"), Foreground = TextSecondaryBrush(),
                TextWrapping = TextWrapping.Wrap
            });
        grid.Children.Add(stack);
        if (actions != null)
        {
            var actionHost = new Border { Child = actions, VerticalAlignment = VerticalAlignment.Center };
                        if ((RootGrid?.ActualWidth ?? StartupClientWidth) < 1180)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(actionHost, 1);
                Grid.SetColumnSpan(actionHost, 2);
                actionHost.Margin = new Thickness(0,8,0,0);
            }
            else Grid.SetColumn(actionHost, 1);
            grid.Children.Add(actionHost);
        }
        return new Border { Tag = "workspace-intro", Padding = UseGlassChrome ? new Thickness(0,12,0,12) : new Thickness(0,7,0,9), Margin = new Thickness(0,0,0,8), BorderBrush = _darkTheme ? GlassBorderBrush() : SectionBorderBrush(), BorderThickness = new Thickness(0), Child = grid };
    }

    private Border BuildWorkspaceSurface(string title, string subtitle, UIElement content)
    {
        // Every section uses the same compact title -> one-line purpose -> action
        // hierarchy; explanatory text must not be hidden exclusively in a tooltip.
        var header = new StackPanel { Spacing = 2 };
        var heading = new TextBlock
        {
            Tag = "workspace-heading", Text = title, FontSize = 15.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap,
            MaxLines = 2
        };
        header.Children.Add(heading);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var summary = new TextBlock
            {
                Tag = "workspace-subtitle", Text = subtitle, FontSize = 11.5,
                Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.NoWrap,
                MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTipService.SetToolTip(summary, subtitle);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(summary, subtitle);
            header.Children.Add(summary);
        }
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(header);
        body.Children.Add(content);
        return new Border
        {
            Tag = "workspace-surface",
            Background = UseGlassChrome ? GlassPanelBrush() : null,
            CornerRadius = new CornerRadius(UseGlassChrome ? 16 : 0),
            BorderBrush = _darkTheme ? GlassBorderBrush() : SectionBorderBrush(),
            BorderThickness = UseGlassChrome ? new Thickness(1) : new Thickness(0,0,0,1),
            Padding = UseGlassChrome ? new Thickness(16,13,16,13) : new Thickness(0,7,0,12),
            Margin = new Thickness(0,0,0,10),
            Child = body
        };
    }
}