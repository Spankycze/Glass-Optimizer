using System;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private enum OptimizationAdvisorKind
    {
        Ok,
        Recommended,
        Test,
        Info
    }

    private Border BuildOptimizationAdvisorRow(
        string title,
        string current,
        string recommended,
        string reason,
        OptimizationAdvisorKind kind,
        Func<Task>? action = null,
        string actionText = "Použít")
    {
        var root = new StackPanel { Spacing = 5 };

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });

        string badgeText = kind switch
        {
            OptimizationAdvisorKind.Ok => "OK",
            OptimizationAdvisorKind.Recommended => "ZMĚNIT",
            OptimizationAdvisorKind.Test => "TEST",
            _ => "INFO"
        };
        string badgeBrush = kind switch
        {
            OptimizationAdvisorKind.Ok => "SpankySuccessBrush",
            OptimizationAdvisorKind.Recommended => "SpankyWarningBrush",
            OptimizationAdvisorKind.Test => "SpankyInfoBrush",
            _ => "SpankyInfoBrush"
        };

        var badge = new Border
        {
            Padding = new Thickness(7, 2, 7, 2),
            CornerRadius = new CornerRadius(6),
            Background = UiBrush(badgeBrush),
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock
        {
            Text = badgeText,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = OnAccentTextBrush()
        };
        Grid.SetColumn(badge, 1);
        header.Children.Add(badge);
        root.Children.Add(header);

        var values = new Grid { ColumnSpacing = 6 };
        values.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        values.ColumnDefinitions.Add(new ColumnDefinition());
        values.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        values.ColumnDefinitions.Add(new ColumnDefinition());

        var nowLabel = new TextBlock
        {
            Text = "Teď:",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        var nowValue = new TextBlock
        {
            Text = current,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var recommendedLabel = new TextBlock
        {
            Text = kind == OptimizationAdvisorKind.Test ? "Porovnat:" : "Doporučeno:",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondaryBrush(),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var recommendedValue = new TextBlock
        {
            Text = recommended,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(nowLabel, 0);
        Grid.SetColumn(nowValue, 1);
        Grid.SetColumn(recommendedLabel, 2);
        Grid.SetColumn(recommendedValue, 3);
        values.Children.Add(nowLabel);
        values.Children.Add(nowValue);
        values.Children.Add(recommendedLabel);
        values.Children.Add(recommendedValue);
        root.Children.Add(values);

        root.Children.Add(new TextBlock
        {
            Text = reason,
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (action != null)
        {
            var button = kind == OptimizationAdvisorKind.Recommended
                ? PrimaryQuickButton(actionText, action)
                : QuickButton(actionText, action);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(button);
        }

        return new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(11, 8, 11, 8),
            Child = root
        };
    }

}
