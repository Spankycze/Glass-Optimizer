using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Border BlackCard(UIElement child, double minHeight = 0)
    {
        var card = new Border
        {
            Background = SectionCardBackgroundBrush(), BorderBrush = SectionBorderBrush(),
            BorderThickness = UseGlassChrome ? new Thickness(1) : new Thickness(0, 1, 0, 1), CornerRadius = new CornerRadius(UseGlassChrome ? 22 : 4),
            Padding = new Thickness(16), MinHeight = minHeight, Child = child
        };
        return ApplySurfaceDepth(card);
    }

    // Elevate only major surfaces; keep dense tweak rows free of per-row shadows.
    private static Border ApplySurfaceDepth(Border card)
    {
        if (_darkTheme)
        {
            card.Shadow = new ThemeShadow();
            card.Translation = new System.Numerics.Vector3(0, 0, 12);
        }
        return card;
    }

    private Border BuildBlackOverviewHeader()
    {
        _heroBackgroundImage = null;
        HeroTextPanel = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        PageTitle = new TextBlock { Text = "Přehled", FontSize = UseGlassChrome ? 48 : 30, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.Bold, Foreground = TextPrimaryBrush() };
        PageSubtitle = new TextBlock { Text = "Hardware, živé využití a stav systému", FontSize = UseGlassChrome ? 21 : 13, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap };
        HeroTextPanel.Children.Add(PageTitle);
        HeroTextPanel.Children.Add(PageSubtitle);
        var body = new Grid { Margin = new Thickness(24, _darkTheme ? 60 : 38, 12, 4), ColumnSpacing = 18 };
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(HeroTextPanel);
        var mark = new Image { Source = SidebarIconSource("system"), Width = 48, Height = 48, Opacity = .65, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(mark, 1); body.Children.Add(mark);
        var frame = new Border { Background = SurfaceAltBrush(), Child = body, IsHitTestVisible = false };
        _heroImageFrame = frame;
        var root = new Grid(); root.Children.Add(frame);
        _heroActionButtonReady = true;
        return new Border { Background = SurfaceAltBrush(), Child = root };
    }

    private Border BuildBlackTelemetryCard(string label, string subtitle, SolidColorBrush accent, out GaugeRefs refs)
    {
        var layout = new Grid { ColumnSpacing = 18 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        var visual = BlackHardwareVisual(label == "CPU" ? "cpu" : label == "GPU" ? "gpu" : label == "RAM" ? "ram" : "nvme",
            label == "CPU" ? 0 : label == "GPU" ? 1 : label == "RAM" ? 2 : 3);
        visual.Height = 128;
        visual.VerticalAlignment = VerticalAlignment.Center;
        layout.Children.Add(visual);

        var body = new StackPanel { Spacing = 7 };
        Grid.SetColumn(body, 1); layout.Children.Add(body);
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = label, FontSize = 10, CharacterSpacing = 160, FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondaryBrush(), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(name);
        var status = new TextBlock { Text = "NA\u010c\u00cdT\u00c1M", FontSize = 9, Foreground = TextSecondaryBrush() };
        var badge = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(6,2,6,2),
            Background = SurfaceAltBrush(), Child = status };
        Grid.SetColumn(badge, 1); header.Children.Add(badge); body.Children.Add(header);

        var secondary = _overviewTextRefs is { } inventory
            ? label switch { "CPU" => inventory.Cpu, "GPU" => inventory.Gpu, "RAM" => inventory.Ram, _ => inventory.Disk }
            : new TextBlock { Text = subtitle };
        secondary.FontSize = 13; secondary.FontWeight = FontWeights.SemiBold;
        secondary.Foreground = TextPrimaryBrush(); secondary.TextWrapping = TextWrapping.Wrap;
        secondary.MinHeight = 32; body.Children.Add(secondary);

        var readings = new Grid { ColumnSpacing = 14 };
        readings.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        readings.ColumnDefinitions.Add(new ColumnDefinition());
        var value = new TextBlock { Text = "\u2014", Width = 84, FontSize = 34, FontWeight = FontWeights.Medium,
            Foreground = TextPrimaryBrush(), VerticalAlignment = VerticalAlignment.Center };
        readings.Children.Add(value);
        var sparkHost = new Canvas { Height = 38, MinWidth = 72, Background = SurfaceAltBrush(), IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center };
        var spark = new Polyline { Stroke = accent, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
        var dot = new Ellipse { Width = 4, Height = 4, Fill = accent, Visibility = Visibility.Collapsed };
        sparkHost.Children.Add(spark); sparkHost.Children.Add(dot);
        sparkHost.SizeChanged += (_, e) => sparkHost.Clip = new RectangleGeometry
        { Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height)) };
        Grid.SetColumn(sparkHost, 1); readings.Children.Add(sparkHost); body.Children.Add(readings);

        var fill = new Border { Width = 0, Height = 2, HorizontalAlignment = HorizontalAlignment.Left,
            Background = accent, CornerRadius = new CornerRadius(1) };
        var track = new Border { Height = 2, MinWidth = 72, Background = _darkTheme ? ThemeColor(18,18,18) : BorderThemeBrush(),
            CornerRadius = new CornerRadius(1), Child = fill };
        body.Children.Add(track);
        TextBlock Stat(string text) => new() { Text = text, FontSize = 8.5, Foreground = TextSecondaryBrush() };
        var min = Stat("MIN \u2014"); var avg = Stat("AVG \u2014"); var max = Stat("MAX \u2014");
        var stats = new Grid { ColumnSpacing = 6 };
        for (int i=0; i<3; i++) stats.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(avg,1); Grid.SetColumn(max,2);
        stats.Children.Add(min); stats.Children.Add(avg); stats.Children.Add(max); body.Children.Add(stats);
        refs = new GaugeRefs { ValueText=value, SecondaryText=secondary, MinText=min, AvgText=avg, PeakText=max,
            StatusText=status, StatusBadge=badge, Sparkline=spark, EndDot=dot, SparkHost=sparkHost, Fill=fill,
            TrackHost=track, VerticalLevel=false };
        return BlackCard(layout, 182);
    }

    private readonly System.Collections.Generic.Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.BitmapImage> _blackHardwareSources = new();

    private Image BlackHardwareVisual(string asset, int index)
    {
        if (!_blackHardwareSources.TryGetValue(asset, out var source))
        {
            source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
            {
                DecodePixelWidth = 480,
                UriSource = new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Hardware", asset + ".png"))
            };
            _blackHardwareSources[asset] = source;
        }
        return new Image
        {
            Name = "OverviewHardwareVisual" + index, Source = source, Height = 108,
            Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false
        };
    }

    // Retain the inventory readiness host without building duplicate visual cards.
    // Inventory TextBlocks are now attached directly to the live telemetry cards.
    private Border BuildBlackHardwareStrip(OverviewTextRefs refs, bool compact)
        => new() { Visibility = Visibility.Collapsed };
    private StackPanel BlackInfoHeading(string title, string icon)
    {
        var row = new StackPanel { Orientation=Orientation.Horizontal, Spacing=8 };
        row.Children.Add(SectionIcon(icon,18));
        row.Children.Add(new TextBlock { Text=title, FontSize=13, FontWeight=FontWeights.SemiBold, Foreground=TextPrimaryBrush(), VerticalAlignment=VerticalAlignment.Center });
        return row;
    }

    private Border BuildBlackSystemCard(TextBlock os, TextBlock board)
    {
        var body = new StackPanel { Spacing=10 };
        body.Children.Add(BlackInfoHeading("Systém", "system"));
        os.FontSize=12; os.TextWrapping=TextWrapping.Wrap; os.MaxLines=4;
        board.FontSize=11; board.TextWrapping=TextWrapping.Wrap; board.MaxLines=3;
        body.Children.Add(os); body.Children.Add(board);
        _overviewSystemStatusText = new TextBlock { Text="Načítám systém…", FontSize=10, Foreground=TextSecondaryBrush() };
        _overviewDriverStatusText = new TextBlock { Text="Načítám ovladač…", FontSize=10, Foreground=TextSecondaryBrush() };
        body.Children.Add(_overviewSystemStatusText); body.Children.Add(_overviewDriverStatusText);
        return BlackCard(body, 198);
    }

    private Border BuildBlackUptimeCard(TextBlock uptime)
    {
        var body = new StackPanel { Spacing=12 };
        body.Children.Add(BlackInfoHeading("Doba provozu","power"));
        uptime.FontSize=23; uptime.Foreground=TextPrimaryBrush(); uptime.TextWrapping=TextWrapping.Wrap;
        body.Children.Add(uptime);
        body.Children.Add(new TextBlock { Text="Od posledního spuštění Windows", FontSize=11, Foreground=TextSecondaryBrush(), TextWrapping=TextWrapping.Wrap });
        return BlackCard(body, 198);
    }
}