using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static (byte R, byte G, byte B) FeatureAccentColor(string tag) => tag switch
    {
        "power" => (255, 92, 53),
        "gpu" => (49, 203, 153),
        "memory" => (43, 183, 229),
        "network" => (35, 170, 220),
        "storage" => (113, 132, 255),
        "system" => (171, 117, 255),
        "components" => (66, 201, 146),
        "usb" => (255, 158, 61),
        "privacy" => (230, 173, 53),
        "ai" => (184, 105, 255),
        "defender" => (239, 84, 84),
        "debloat" => (242, 113, 143),
        _ => (255, 92, 53)
    };
    private static SolidColorBrush FeatureAccentBrush(string tag) { var c = FeatureAccentColor(tag); return ThemeColor(c.R, c.G, c.B); }
    private Border BuildFeatureTile(string tag, string title, string value, string detail)
    {
        var root = new Grid { ColumnSpacing = 12 }; root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = FeatureAccentBrush(tag), Margin = new Thickness(0, 2, 0, 2) });
        var stack = new StackPanel { Spacing = 2 }; stack.Children.Add(new TextBlock { Text = title, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = FeatureAccentBrush(tag), CharacterSpacing = 80 });
        stack.Children.Add(new TextBlock { Text = value, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap, MaxLines = 2 }); Grid.SetColumn(stack, 1); root.Children.Add(stack);
        var tile = new Border { MinHeight = 86, Background = UseGlassChrome ? GlassPanelBrush() : SectionCardBackgroundBrush(), BorderBrush = UseGlassChrome ? GlassEdgeBrush() : SectionBorderBrush(), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(UseGlassChrome ? 18 : 8), Padding = new Thickness(13, 11, 13, 11), Child = root };
        AttachHoverMotion(tile, 1.006f);
        return tile;
    }
}

