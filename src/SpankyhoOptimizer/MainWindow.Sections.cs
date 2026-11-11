using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Border BuildSectionHero(string tag, string kicker, string title, string subtitle, FrameworkElement? actions = null)
    {
        if (UseCompactSectionChrome) return BuildNeutralSectionIntro(title, subtitle, actions);
        var grid = new Grid { ColumnSpacing = 14 }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(OverviewGlowIcon(tag, 26, PaletteBrush(_themePalette.HeroAccent)));
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center }; text.Children.Add(new TextBlock { Text = kicker.ToUpperInvariant(), FontSize = 10.5, CharacterSpacing = 105, FontWeight = FontWeights.SemiBold, Foreground = AccentBrush() }); text.Children.Add(new TextBlock { Text = title, FontSize = 19.5, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap }); text.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap, MaxWidth = 860 }); Grid.SetColumn(text, 1); grid.Children.Add(text); if (actions != null) { Grid.SetColumn(actions, 2); grid.Children.Add(actions); }
        return new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(_darkTheme ? 1.3 : 1), CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 13), Child = grid };
    }
    private Border BuildSectionSurface(string tag, string title, string subtitle, UIElement content)
    {
        if (UseCompactSectionChrome) return BuildWorkspaceSurface(title, subtitle, content);
        var root = new StackPanel { Spacing = 8 }; var head = new Grid { ColumnSpacing = 9 }; head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition()); head.Children.Add(new Border { Width = 3, Height = 26, Background = PaletteBrush(SectionPalette.SectionMarker), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Top });
        var text = new StackPanel { Spacing = 2 }; text.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap }); text.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis }); Grid.SetColumn(text, 1); head.Children.Add(text); root.Children.Add(head); root.Children.Add(content);
        return new Border { Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(13, 11, 13, 11), Margin = new Thickness(0, 0, 0, 9), Child = root };
    }
}
