using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{


    private Border DiagnosticLine(string name, string value)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
        row.Children.Add(new TextBlock { Text = name, Foreground = TextSecondaryBrush(), FontSize = 12 });
        var v = new TextBlock { Text = value, Foreground = TextPrimaryBrush(), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        Grid.SetColumn(v, 1); row.Children.Add(v);
        return new Border { Padding = new Microsoft.UI.Xaml.Thickness(10, 7, 10, 7), Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(), BorderThickness = new Microsoft.UI.Xaml.Thickness(_darkTheme ? 1.1 : 1), CornerRadius = new Microsoft.UI.Xaml.CornerRadius(9), Child = row };
    }
}

