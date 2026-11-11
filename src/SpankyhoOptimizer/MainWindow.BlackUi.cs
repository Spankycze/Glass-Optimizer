using System;
using SpankyhoOptimizer.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static Brush OverviewLiquidSurface() => UseGlassChrome
        ? GlassGradient(Windows.UI.Color.FromArgb(30, 213, 235, 249), Windows.UI.Color.FromArgb(9, 55, 88, 112))
        : SurfaceElevatedBrush();
    private static Brush OverviewLiquidEdge() => UseGlassChrome ? GlassEdgeBrush() : BorderThemeBrush();
    private static Brush OverviewLiquidInset() => UseGlassChrome ? ThemeColor(20, 226, 244, 255) : SurfaceBrush();
    private Border BlackCard(UIElement child, double minHeight = 0)
    {
        var radius = new CornerRadius(UseGlassChrome ? 26 : 16);
        UIElement content = child;
        Brush background = OverviewLiquidSurface();

        if (UseGlassChrome)
        {
            var layers = new Grid();
            layers.Children.Add(CreateNativeGlassElement(radius));
            // One native Border is enough for BOTH tint and padded content.
            // Its background is still painted above native acrylic and behind
            // child content, identical layer order with one less XAML visual.
            layers.Children.Add(new Border
            {
                Background = OverviewLiquidSurface(),
                CornerRadius = radius,
                Padding = new Thickness(18),
                Child = child
            });
            content = layers;
            background = ThemeColor(0, 0, 0, 0);
        }

        var card = new Border
        {
            Background = background, BorderBrush = OverviewLiquidEdge(),
            BorderThickness = UseGlassChrome ? new Thickness(1) : new Thickness(0, 1, 0, 1), CornerRadius = radius,
            Padding = UseGlassChrome ? new Thickness(0) : new Thickness(18), MinHeight = minHeight, Child = content
        };
        AttachHoverMotion(card, 1.004f);
        return ApplySurfaceDepth(card);
    }

    // Elevate only major surfaces. WinUI ThemeShadow can be shared by multiple
    // UIElements; the translation depth belongs to each Border, not the shadow.
    // Avoid one separate native ThemeShadow projection for every glass card.
    private static ThemeShadow? _sharedSurfaceShadow;
    private static Border ApplySurfaceDepth(Border card)
    {
        if (_darkTheme)
        {
            card.Shadow = _sharedSurfaceShadow ??= new ThemeShadow();
            card.Translation = new System.Numerics.Vector3(0, 0, 8);
        }
        return card;
    }

    private Border BuildBlackOverviewHeader()
    {
        _heroBackgroundImage = null;
        HeroTextPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
        PageTitle = new TextBlock
        {
            Text = "Přehled",
            FontSize = 30,
            LineHeight = 34,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimaryBrush()
        };
        PageSubtitle = new TextBlock
        {
            Text = "Výkon, hardware a stav systému na jednom místě",
            FontSize = 13,
            LineHeight = 18,
            Foreground = TextSecondaryBrush(),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        HeroTextPanel.Children.Add(PageTitle);
        HeroTextPanel.Children.Add(PageSubtitle);

        var body = new Grid { Margin = new Thickness(26, _darkTheme ? 41 : 36, 20, 15), ColumnSpacing = 20 };
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(HeroTextPanel);

        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusRow.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = ThemeColor(91, 226, 138) });
        statusRow.Children.Add(new TextBlock
        {
            Text = "Optimizer připraven",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });
        var statusPill = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 7, 14, 7),
            Background = _darkTheme
                ? GlassGradient(Windows.UI.Color.FromArgb(62, 34, 96, 138), Windows.UI.Color.FromArgb(38, 14, 46, 72))
                : OverviewLiquidInset(),
            BorderBrush = _darkTheme ? GlassBlueEdgeBrush() : OverviewLiquidEdge(),
            BorderThickness = new Thickness(1),
            Child = statusRow,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(statusPill, 1);
        body.Children.Add(statusPill);
        var frame = new Border { Background = UseGlassChrome ? ThemeColor(0,0,0,0) : SurfaceAltBrush(), Child = body, IsHitTestVisible = false };
        _heroImageFrame = frame;
        var root = new Grid(); root.Children.Add(frame);
        _heroActionButtonReady = true;
        return new Border { Background = UseGlassChrome ? ThemeColor(0,0,0,0) : SurfaceAltBrush(), Child = root };
    }

    private Border BuildBlackTelemetryCard(string label, string subtitle, SolidColorBrush accent, out GaugeRefs refs)
    {
        var body = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        var visual = BlackHardwareVisual(label == "CPU" ? "cpu" : label == "GPU" ? "gpu" : label == "RAM" ? "ram" : "nvme",
            label == "CPU" ? 0 : label == "GPU" ? 1 : label == "RAM" ? 2 : 3);
        visual.Width = 38; visual.Height = 38;
        heading.Children.Add(visual);
        heading.Children.Add(new TextBlock { Text = label switch { "CPU" => "Procesor", "GPU" => "Grafika", "RAM" => "Paměť", _ => "Úložiště" },
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(heading);
        var status = new TextBlock { Text = "NAČÍTÁM", FontSize = 10.5, Foreground = TextSecondaryBrush() };
        var badge = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(7,4,7,4),
            Background = OverviewLiquidInset(), Child = status, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(badge, 1); header.Children.Add(badge); body.Children.Add(header);

        var secondary = _overviewTextRefs is { } inventory
            ? label switch { "CPU" => inventory.Cpu, "GPU" => inventory.Gpu, "RAM" => inventory.Ram, _ => inventory.Disk }
            : new TextBlock { Text = subtitle };
        secondary.FontSize = 12; secondary.FontWeight = FontWeights.Normal;
        secondary.Foreground = TextSecondaryBrush(); secondary.TextWrapping = TextWrapping.Wrap;
        secondary.MinHeight = 32; secondary.MaxLines = 0; body.Children.Add(secondary);

        var readings = new Grid { ColumnSpacing = 10 };
        readings.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        readings.ColumnDefinitions.Add(new ColumnDefinition());
        var value = new TextBlock { Text = "—", Width = 78, FontSize = 34, FontWeight = FontWeights.Bold,
            Foreground = TextPrimaryBrush(), VerticalAlignment = VerticalAlignment.Center };
        readings.Children.Add(value);
        var sparkHost = new Canvas { Height = 38, MinWidth = 66, Background = ThemeColor(0,0,0,0), IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center };
        var spark = new Polyline { Stroke = accent, StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round };
        var dot = new Ellipse { Width = 4, Height = 4, Fill = accent, Visibility = Visibility.Collapsed };
        sparkHost.Children.Add(spark); sparkHost.Children.Add(dot);
        // Keep one native clip geometry for the whole lifetime of this canvas.
        // Allocating and reattaching a RectangleGeometry on every resize makes
        // the compositor rebuild its clip/resource chain needlessly.
        sparkHost.Clip = new RectangleGeometry();
        sparkHost.SizeChanged += (_, e) =>
        {
            if (sparkHost.Clip is not RectangleGeometry clip) return;
            var rect = new Windows.Foundation.Rect(0, 0,
                Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height));
            if (!clip.Rect.Equals(rect)) clip.Rect = rect;
        };
        Grid.SetColumn(sparkHost,1); readings.Children.Add(sparkHost); body.Children.Add(readings);
        var fill = new Border { Width = 0, Height = 3, HorizontalAlignment = HorizontalAlignment.Left,
            Background = accent, CornerRadius = new CornerRadius(2) };
        var track = new Border { Height = 3, MinWidth = 66, Background = OverviewLiquidInset(), CornerRadius = new CornerRadius(2), Child = fill };
        body.Children.Add(track);
        TextBlock Stat(string text) => new() { Text = text, FontSize = 10.5, Foreground = TextSecondaryBrush() };
        var min = Stat("MIN —"); var avg = Stat("AVG —"); var max = Stat("MAX —");
        var stats = new Grid { ColumnSpacing = 5 };
        for (int i=0;i<3;i++) stats.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(avg,1); Grid.SetColumn(max,2);
        stats.Children.Add(min); stats.Children.Add(avg); stats.Children.Add(max); body.Children.Add(stats);
        refs = new GaugeRefs { ValueText=value, SecondaryText=secondary, MinText=min, AvgText=avg, PeakText=max,
            StatusText=status, StatusBadge=badge, Sparkline=spark, EndDot=dot, SparkHost=sparkHost, Fill=fill,
            TrackHost=track, VerticalLevel=false };
        var card = BlackCard(body, 210); card.Tag = "OverviewLiquidTelemetry"; return card;
    }

    private void VerifyOverviewLiquidCards()
    {
        if (!UseGlassChrome) return;
        if (_overviewTelemetryGrid?.Children.Count != 6) throw new InvalidOperationException("Six reference telemetry cards required.");
        foreach (var gauge in new[] { _cpuGauge, _gpuGauge, _ramGauge, _diskGauge })
        {
            if (gauge == null || gauge.SparkHost.Background is not SolidColorBrush { Color.A: 0 })
                throw new InvalidOperationException("Overview plot acquired an opaque surface.");
            if (gauge.ValueText.FontSize > 34 || gauge.StatusBadge.CornerRadius.TopLeft < 12)
                throw new InvalidOperationException("Overview compact glass typography/geometry regressed.");
        }

        foreach (var child in _overviewTelemetryGrid.Children)
        {
            if (child is not Border card ||
                card.Background is not SolidColorBrush { Color.A: 0 } ||
                card.Child is not Grid layers ||
                layers.Children.Count < 2 ||
                layers.Children[0] is not SystemBackdropElement)
                throw new InvalidOperationException("Overview telemetry card lost its native glass layer.");
        }

        LogService.Info("visual-qa", "OVERVIEW_LIQUID_SURFACE_OK channels=6 plots=transparent nativeGlass=true compactText=true");
    }
    private readonly System.Collections.Generic.Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.BitmapImage> _blackHardwareSources = new();

    private Image BlackHardwareVisual(string asset, int index)
    {
        if (!_blackHardwareSources.TryGetValue(asset, out var source))
        {
            source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
            {
                DecodePixelWidth = 128,
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

    private Border BuildBlackHardwareStrip(OverviewTextRefs refs, bool compact)
    {
        TextBlock HardwareValue(string text) => new()
        {
            Text = text,
            FontSize = compact ? 10.5 : 11.25,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2
        };

        _overviewHardwareCpuText = HardwareValue(refs.Cpu.Text);
        _overviewHardwareGpuText = HardwareValue(refs.Gpu.Text);
        _overviewHardwareRamText = HardwareValue(refs.Ram.Text);
        _overviewHardwareDiskText = HardwareValue(refs.Disk.Text);

        var items = new (string Label, string Icon, TextBlock Value)[]
        {
            ("CPU", "system", _overviewHardwareCpuText),
            ("GPU", "gpu", _overviewHardwareGpuText),
            ("RAM", "components", _overviewHardwareRamText),
            ("Disk", "storage", _overviewHardwareDiskText)
        };

        int columns = compact ? 2 : 4;
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        for (int i = 0; i < columns; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < items.Length / columns; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < items.Length; i++)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            head.Children.Add(SectionIcon(items[i].Icon, 16));
            head.Children.Add(new TextBlock
            {
                Text = items[i].Label,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = TextSecondaryBrush(),
                VerticalAlignment = VerticalAlignment.Center
            });

            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(head);
            content.Children.Add(items[i].Value);

            var tile = new Border
            {
                MinHeight = compact ? 74 : 70,
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(15),
                Background = OverviewLiquidInset(),
                BorderBrush = OverviewLiquidEdge(),
                BorderThickness = new Thickness(1),
                Child = content
            };
            Grid.SetColumn(tile, i % columns);
            Grid.SetRow(tile, i / columns);
            grid.Children.Add(tile);
        }

        return new Border
        {
            Background = ThemeColor(0, 0, 0, 0),
            Child = grid
        };
    }
    private StackPanel BlackInfoHeading(string title, string icon)
    {
        var row = new StackPanel { Orientation=Orientation.Horizontal, Spacing=8 };
        row.Children.Add(SectionIcon(icon,18));
        row.Children.Add(new TextBlock { Text=title, FontSize=13, FontWeight=FontWeights.SemiBold, Foreground=TextPrimaryBrush(), VerticalAlignment=VerticalAlignment.Center });
        return row;
    }

    private Border BuildBlackSystemCard(TextBlock os, TextBlock board)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(BlackInfoHeading("Systém", "system"));
        os.FontSize = 12;
        os.FontWeight = FontWeights.SemiBold;
        os.TextWrapping = TextWrapping.Wrap;
        os.MaxLines = 4;
        board.FontSize = 10.75;
        board.TextWrapping = TextWrapping.Wrap;
        board.MaxLines = 3;
        body.Children.Add(os);
        body.Children.Add(board);
        return BlackCard(body, 190);
    }

    private Border BuildBlackUptimeCard(TextBlock uptime)
    {
        var body = new StackPanel { Spacing=12 };
        body.Children.Add(BlackInfoHeading("Doba provozu","power"));
        uptime.FontSize=20; uptime.Foreground=TextPrimaryBrush(); uptime.TextWrapping=TextWrapping.Wrap; uptime.MaxLines=0;
        body.Children.Add(uptime);
        body.Children.Add(new TextBlock { Text="Od posledního spuštění Windows", FontSize=11, Foreground=TextSecondaryBrush(), TextWrapping=TextWrapping.Wrap });
        return BlackCard(body, 208);
    }
}