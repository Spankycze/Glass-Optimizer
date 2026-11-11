using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Border BuildModernSectionHero(
        string iconKey,
        string title,
        string subtitle,
        string visualTitle,
        string visualDetail,
        UIElement? actions = null)
    {
        if (UseCompactSectionChrome) return BuildNeutralSectionIntro(title, subtitle, actions);
        var grid = new Grid { ColumnSpacing = 22, MinHeight = 150 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });

        var left = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        left.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        if (actions != null) left.Children.Add(actions);
        grid.Children.Add(left);

        var visualStack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        visualStack.Children.Add(OverviewGlowIcon(iconKey, 28, PaletteBrush(_themePalette.HeroAccent)));
        visualStack.Children.Add(new TextBlock
        {
            Text = visualTitle,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Center
        });        visualStack.Children.Add(new TextBlock
        {
            Text = visualDetail,
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 220
        });

        var visual = new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.35 : 1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18),
            Child = visualStack
        };
        Grid.SetColumn(visual, 1);
        grid.Children.Add(visual);
        // The fixed 270px decorative panel must not squeeze action buttons
        // and descriptions when the window is narrow. Hide only this visual;
        // keep every real control and status in the primary column.
        bool compactHero = false;
        grid.SizeChanged += (_, args) =>
        {
            if (args.NewSize.Width <= 0) return;
            bool narrow = args.NewSize.Width < 640;
            if (compactHero == narrow) return;
            compactHero = narrow;
            visual.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            grid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 270);
            grid.ColumnSpacing = narrow ? 0 : 22;
            grid.MinHeight = narrow ? 100 : 150;
        };
        return BuildDashboardCard(grid, new Thickness(20, 16, 20, 16));
    }
    private FrameworkElement BuildDashboardMetric(
        string label,
        string value,
        string detail,
        string brushKey = "SpankyInfoBrush")
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 70,
            Foreground = TextSecondaryBrush()
        });
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiBrush(brushKey),
            TextWrapping = TextWrapping.Wrap
        });        stack.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.2 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7, 10, 7),
            Child = stack
        };
    }

    private Grid BuildDashboardMetricGrid(params FrameworkElement[] tiles)
    {
        var grid = new Grid { ColumnSpacing = 7, RowSpacing = 7 };
        foreach (FrameworkElement tile in tiles) grid.Children.Add(tile);

        // A fixed four-column layout clipped labels in the compact window.
        // Reflow existing tiles on resize instead of reconstructing cards,
        // preserving live bindings and keeping UI allocation costs low.
        int currentColumns = -1;
        void Arrange(int columns)
        {
            columns = Math.Min(Math.Max(1, tiles.Length), columns);
            if (columns == currentColumns) return;
            currentColumns = columns;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (int i = 0; i < columns; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition());
            int rows = (tiles.Length + columns - 1) / columns;
            for (int i = 0; i < rows; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < tiles.Length; i++)
            {
                Grid.SetColumn(tiles[i], i % columns);
                Grid.SetRow(tiles[i], i / columns);
            }
        }

        Arrange(Math.Min(4, Math.Max(1, tiles.Length)));
        grid.SizeChanged += (_, args) =>
        {
            double width = args.NewSize.Width;
            if (width <= 0) return;
            int columns = width < 480 ? 2 : width < 760 ? 3 : 4;
            // Four equal cards look better as two balanced rows than 3 + 1.
            if (tiles.Length == 4 && columns == 3) columns = 2;
            Arrange(columns);
        };
        return grid;
    }

    private Border BuildFlatSectionHeader(string title, string subtitle)
    {
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimaryBrush()
        });
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = UseCompactSectionChrome ? 12 : 10.5,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        // A single quiet divider and a clear two-line hierarchy instead of
        // the old colored rail and nested decorative heading surface.
        return new Border
        {
            Tag = "flat-section-heading",
            Margin = new Thickness(0, 8, 0, 5),
            Padding = new Thickness(4, 6, 4, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = SectionBorderBrush(),
            Background = null,
            Child = text
        };
    }

    private Border BuildDashboardExpander(string title, string subtitle, UIElement content, bool expanded = false)
    {
        var header = new StackPanel { Spacing = 3 };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14.5,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimaryBrush()
        });
        header.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Foreground = _darkTheme ? ThemeColor(184, 192, 205) : TextSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        var expander = new Expander
        {
            Header = header,
            IsExpanded = expanded,
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (UseGlassChrome)
        {
            expander.Resources["ExpanderHeaderBackground"] = GlassSurfaceLowBrush();
            expander.Resources["ExpanderHeaderBackgroundPointerOver"] = GlassHoverBrush();
            expander.Resources["ExpanderHeaderBackgroundPressed"] = GlassPressedBrush();
            expander.Resources["ExpanderHeaderBorderBrush"] = GlassBorderBrush();
        }
        return BuildDashboardCard(expander, new Thickness(15, 10, 15, 10));
    }
}
