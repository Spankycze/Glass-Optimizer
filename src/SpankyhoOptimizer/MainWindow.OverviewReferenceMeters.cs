using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Media.Animation;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

// Live, native WinUI overview components. No image backgrounds, fake percentages
// or per-frame polling. Data comes from the existing 2-second metrics worker.

public sealed partial class MainWindow
{
    private OverviewSegmentedChart? _referenceCpuPlot;
    private OverviewSegmentedChart? _referenceCpuArea;
    private OverviewSegmentedChart? _referenceCpuGlow;
    private readonly List<LineGeometry> _referenceVerticalGridLines = new();
    private readonly List<Windows.Foundation.Point> _referenceRawPlotPoints = new(64);
    private double _referenceGridWidth = double.NaN;
    private double _referenceGridHeight = double.NaN;
    private Canvas? _referenceCpuPlotHost;
    private TextBlock[]? _referencePlotYTickLabels;
    private Canvas? _referencePlotYTicksHost;
    private Canvas? _referenceNetworkPlotHost;
    private StackPanel? _referenceTemperatureRoot;
    private StackPanel? _overviewInlineSecondHistory;
    private double[]? _referenceBaseGaugeRowHeights;
    private const double ReferenceBasePlotHeight = 99;
    private const double ReferenceBaseSparkHeight = 42;
    private const double ReferenceBaseNetworkHeight = 33;
    private const double ReferenceBaseInfoHeight = 78;
    private TextBlock? _referenceChartCpu, _referenceChartRam, _referenceChartGpu;
    private TextBlock? _referenceChartDisk, _referenceChartNetwork;
    private readonly record struct ReferenceHeaderTextKey(
        double? Cpu, double? Ram, double? Gpu, double? Disk,
        double? ClockMhz, double? GpuTemp, string? GpuLabel);
    private ReferenceHeaderTextKey? _referenceLastHeaderTextKey;
    private TextBlock? _referenceLeftUsage;
    private TextBlock? _referenceNetworkValue;
    private TextBlock? _referenceNetworkDetail;
    private TextBlock? _referenceNetworkFoot;
    private Button? _overviewInfoButton;
    private Flyout? _overviewInfoFlyout;
    private Grid? _overviewInfoCardsGrid;
    private Button? _overviewChangelogAction;
    private Polyline? _referenceNetworkSpark;
    private readonly Queue<double> _referenceNetworkHistory = new();
    private double _referenceNetworkLastTotal = double.NaN;
    private int _referenceNetworkSteadySamples;
    private double _referenceNetworkLastSparkWidth = double.NaN;
    private double _referenceNetworkLastSparkHeight = double.NaN;
    private Border[]? _referenceNetworkBlocks;
    private int _referenceNetworkLastLit = -1;
    private readonly record struct ReferenceNetworkTextState(
        double Down, double Up, bool Online, string Adapter);
    private ReferenceNetworkTextState? _referenceNetworkLastTextState;
    private string _referenceNetworkLastTotalLabel = "—";
    private TextBlock? _referenceCpuTemperature;
    private TextBlock? _referenceGpuTemperature;
    private Border[]? _referenceLeftBlocks;
    private int _referenceLeftLastLit = -1;
    private double? _referenceCpuClockMhz;
    private TextBlock? _referenceBoardTemperature;
    private Border[][]? _referenceTempBlocks;
    private readonly int[] _referenceTemperatureLastLit = { -1, -1, -1 };
    private DateTime _referenceLastPlotUtc;
    private double? _referenceSensorCpu;
    private double? _referenceSensorGpu;
    private double? _referenceSensorBoard;
    private int? _referenceSensorFanRpm;
    private readonly record struct TemperatureVisualState(
        double? Cpu, double? Gpu, double? Board, int? Fan);
    private TemperatureVisualState? _referenceLastRenderedTemperature;
    private TextBlock? _referenceSensorStatusText;
    private TextBlock? _referenceFanTelemetryText;

    private static SolidColorBrush ReferenceColor(byte r, byte g, byte b) => ThemeColor(r, g, b);
    private static readonly Dictionary<uint, Brush> ReferenceSurfaceCache = new(16);
    private static readonly Dictionary<uint, SolidColorBrush> ReferenceIconChipCache = new(12);
    private static Brush? _referenceRimBrush;

    private static SolidColorBrush ReferenceCpuAccent() => ReferenceColor(0, 204, 255);
    private static SolidColorBrush ReferenceRamAccent() => ReferenceColor(145, 113, 255);
    private static SolidColorBrush ReferenceGpuAccent() => ReferenceColor(226, 66, 238);
    private static SolidColorBrush ReferenceDiskAccent() => ReferenceColor(29, 227, 111);

    // Smoked-glass card surface with a top specular reflection and deep
    // translucent slate body over WinUI's native glass compositor.
    // All stops are static per card. No effects, per-frame allocation or timers.
    private static Brush ReferenceGlassSurface(SolidColorBrush? accent = null)
    {
        var hue = accent?.Color;
        byte red = (byte)Math.Clamp(22 + (hue?.R ?? 58) / 8, 22, 54);
        byte green = (byte)Math.Clamp(42 + (hue?.G ?? 140) / 6, 42, 82);
        byte blue = (byte)Math.Clamp(78 + (hue?.B ?? 220) / 4, 86, 144);
        byte topAlpha = UseGlassChrome ? (byte)68 : (byte)218;
        byte midAlpha = UseGlassChrome ? (byte)86 : (byte)226;
        byte lowerAlpha = UseGlassChrome ? (byte)110 : (byte)235;
        uint key = ((uint)topAlpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue;
        if (ReferenceSurfaceCache.TryGetValue(key, out var cached)) return cached;
        var brush = new LinearGradientBrush {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0.85, 1),
            GradientStops = {
                new GradientStop {
                    Offset = 0,
                    Color = Windows.UI.Color.FromArgb(topAlpha, red, green, blue)
                },
                new GradientStop {
                    Offset = .48,
                    Color = Windows.UI.Color.FromArgb(
                        midAlpha, 10, 20, 38)
                },
                new GradientStop {
                    Offset = 1,
                    Color = Windows.UI.Color.FromArgb(
                        lowerAlpha, 5, 11, 24)
                }
            }
        };
        if (ReferenceSurfaceCache.Count >= 64) ReferenceSurfaceCache.Clear();
        ReferenceSurfaceCache[key] = brush;
        return brush;
    }

    private static Brush ReferenceThinRim() => _referenceRimBrush ??= new LinearGradientBrush {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(0.25, 1),
        GradientStops = {
            new GradientStop {
                Offset = 0, Color = Windows.UI.Color.FromArgb(132, 192, 232, 255)
            },
            new GradientStop {
                Offset = .45, Color = Windows.UI.Color.FromArgb(56, 84, 164, 226)
            },
            new GradientStop {
                Offset = 1, Color = Windows.UI.Color.FromArgb(28, 36, 82, 134)
            }
        }
    };

    private TextBlock ReferenceText(string text, double size, SolidColorBrush? color = null, bool bold = false) =>
        new() {
            Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1, TextWrapping = TextWrapping.NoWrap,
            Foreground = color ?? ReferenceColor(232, 242, 255),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };

    private Border ReferenceCard(
        UIElement content, double minHeight = 148, SolidColorBrush? accent = null)
    {
        // Reuse existing native-backed cards so light/dark glass and fast detach
        // behave consistently with all sections of Spankyho Optimizer.
        var card = BlackCard(content, minHeight);
        card.CornerRadius = new CornerRadius(17);
        card.BorderBrush = ReferenceThinRim();
        card.BorderThickness = new Thickness(1);
        // BlackCard adds 18 px around content by default. The design reference
        // uses ~12 px once; double padding squeezed three columns into tiny plots.
        if (card.Child is Grid layers && layers.Children.Count == 2 &&
            layers.Children[1] is Border contentPad)
        {
            contentPad.Padding = new Thickness(0);
            contentPad.Background = ReferenceGlassSurface(accent);
            contentPad.CornerRadius = card.CornerRadius;
        }
        else
        {
            card.Padding = new Thickness(0);
            card.Background = ReferenceGlassSurface(accent);
        }
        return card;
    }

    private static void ReferenceStyleSystemInformationCard(Border card)
    {
        card.CornerRadius = new CornerRadius(16);
        card.MinHeight = 0;
        card.BorderBrush = ReferenceThinRim();
        card.BorderThickness = new Thickness(1);
        if (card.Child is Grid layers && layers.Children.Count == 2 &&
            layers.Children[1] is Border pad)
        {
            pad.Background = ReferenceGlassSurface();
            pad.CornerRadius = card.CornerRadius;
            pad.Padding = new Thickness(11, 10, 11, 11);
        }
        else
            card.Background = ReferenceGlassSurface();
    }

    private StackPanel ReferenceHeaderIcon(string label, SolidColorBrush accent)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        string tag = label switch {
            "CPU" => "system", "RAM" => "components", "GPU" => "gpu",
            "Úložiště" => "storage", "Síť" => "network", _ => "power"
        };
        uint chipKey = ((uint)accent.Color.R << 16) | ((uint)accent.Color.G << 8) | accent.Color.B;
        if (!ReferenceIconChipCache.TryGetValue(chipKey, out var chipBg))
        {
            if (ReferenceIconChipCache.Count >= 24) ReferenceIconChipCache.Clear();
            chipBg = new SolidColorBrush(Windows.UI.Color.FromArgb(
                38, accent.Color.R, accent.Color.G, accent.Color.B));
            ReferenceIconChipCache[chipKey] = chipBg;
        }
        var iconFrame = new Border {
            Width = 36, Height = 36,
            CornerRadius = new CornerRadius(10),
            Background = chipBg,
            Child = SectionIcon(tag, 22),
            VerticalAlignment = VerticalAlignment.Center
        };
        p.Children.Add(iconFrame);
        p.Children.Add(ReferenceText(label, 15, ReferenceColor(235, 245, 255), true));
        return p;
    }

    private Border[] ReferenceSegments(StackPanel row, SolidColorBrush accent, int count = 22)
    {
        var result = new Border[count];
        for (int i = 0; i < count; i++)
        {
            var block = new Border {
                Height = 12, MinWidth = 3, CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(0.6),
                BorderBrush = ReferenceColor(15, 70, 109),
                Background = accent, Opacity = .13,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            result[i] = block;
            row.Children.Add(block);
        }
        return result;
    }

    // Weakly keyed per-native-segment storyboard cache. The same 240ms
    // easing/From/To transition is reused when a value changes; detached
    // Overview trees do not remain rooted by the window.
    private CubicEase? _referenceSegmentEase;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Border, ReferenceSegmentAnimation>
        _referenceSegmentAnimations = new();

    private sealed class ReferenceSegmentAnimation
    {
        private readonly Storyboard _storyboard = new();
        private readonly DoubleAnimation _animation;

        public ReferenceSegmentAnimation(Border target, CubicEase easing)
        {
            _animation = new DoubleAnimation {
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EasingFunction = easing
            };
            Storyboard.SetTarget(_animation, target);
            Storyboard.SetTargetProperty(_animation, "Opacity");
            _storyboard.Children.Add(_animation);
        }

        public void Begin(Border target, double from, double to)
        {
            _storyboard.Stop();
            target.Opacity = to;
            _animation.From = from;
            _animation.To = to;
            _storyboard.Begin();
        }
    }

    private void ReferenceUpdateSegments(Border[]? blocks, int lit, ref int last)
    {
        if (blocks == null) return;
        lit = Math.Clamp(lit, 0, blocks.Length);
        if (last == lit) return;
        int first = last < 0 ? 0 : Math.Min(last, lit);
        int end = last < 0 ? blocks.Length : Math.Max(last, lit);
        for (int i = first; i < end; i++)
        {
            var block = blocks[i];
            double to = i < lit ? 1.0 : .27;
            double from = block.Opacity;
            if (Math.Abs(from - to) < .001) continue;
            if (!_referenceSegmentAnimations.TryGetValue(block, out var animation))
            {
                animation = new ReferenceSegmentAnimation(block,
                    _referenceSegmentEase ??= new CubicEase { EasingMode = EasingMode.EaseOut });
                _referenceSegmentAnimations.Add(block, animation);
            }
            animation.Begin(block, from, to);
        }
        last = lit;
    }

    private Border BuildReferenceGauge(string label, SolidColorBrush accent, out GaugeRefs refs)
    {
        var root = new Grid { Padding = new Thickness(12, 10, 12, 10), MinHeight = 148 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sparkRow = new RowDefinition { Height = new GridLength(ReferenceBaseSparkHeight) };
        root.RowDefinitions.Add(sparkRow);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(ReferenceHeaderIcon(label, accent));
        var trend = ReferenceText("—", 11, accent, true);
        trend.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(trend, 1);
        header.Children.Add(trend);
        root.Children.Add(header);

        var main = new Grid { ColumnSpacing = 6 };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        main.ColumnDefinitions.Add(new ColumnDefinition());
        var value = ReferenceText("—", 29, accent, true);
        value.VerticalAlignment = VerticalAlignment.Center;
        main.Children.Add(value);
        var sparkHost = new Canvas {
            Height = 42, MinWidth = 68,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Clip = new RectangleGeometry(),
            IsHitTestVisible = false
        };
        var sparkFill = new Polygon {
            Fill = new LinearGradientBrush {
                StartPoint = new Windows.Foundation.Point(.5,0),
                EndPoint = new Windows.Foundation.Point(.5,1),
                GradientStops = {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(
                        76,accent.Color.R,accent.Color.G,accent.Color.B), Offset=0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(
                        0,accent.Color.R,accent.Color.G,accent.Color.B), Offset=1 }
                }
            }
        };
        sparkHost.Children.Add(sparkFill);
        var spark = new Polyline { Stroke = accent, StrokeThickness = 2.8,
            StrokeLineJoin = PenLineJoin.Round };
        var dot = new Ellipse { Width = 4, Height = 4, Fill = accent,
            Visibility = Visibility.Collapsed };
        sparkHost.Children.Add(spark);
        sparkHost.Children.Add(dot);
        sparkHost.SizeChanged += (_, e) =>
        {
            if (sparkHost.Clip is RectangleGeometry clip)
                clip.Rect = new Windows.Foundation.Rect(0, 0,
                    Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height));
        };
        Grid.SetColumn(sparkHost, 1); main.Children.Add(sparkHost);
        Grid.SetRow(main, 1); root.Children.Add(main);

        string description = label switch {
            "CPU" => "Načítám procesor…",
            "RAM" => "Načítám paměť…",
            "GPU" => "Načítám grafiku…",
            _ => "Aktivita disku"
        };
        var secondary = ReferenceText(description, 10.5, ReferenceColor(173, 196, 226));
        secondary.Margin = new Thickness(0, 0, 0, 5);
        Grid.SetRow(secondary, 2); root.Children.Add(secondary);

        var blockGrid = new Grid { ColumnSpacing = 2, Height = 14,
            VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 22; i++) blockGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var blocks = new Border[22];
        // A brush is a shareable WinUI resource: all 22 borders have the same
        // immutable tint, so there is no reason for 22 native brush objects.
        var segmentBorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(
            154, accent.Color.R, accent.Color.G, accent.Color.B));
        for (int i = 0; i < blocks.Length; i++)
        {
            var block = new Border {
                Height = 14, CornerRadius = new CornerRadius(2),
                BorderBrush = segmentBorderBrush,
                BorderThickness = new Thickness(.85),
                Background = accent, Opacity = .27
            };
            blocks[i] = block; Grid.SetColumn(block, i); blockGrid.Children.Add(block);
        }
        Grid.SetRow(blockGrid, 3); root.Children.Add(blockGrid);

        var status = ReferenceText("NAČÍTÁM", 10, ReferenceColor(153, 191, 220));
        status.Visibility = Visibility.Collapsed; // state for diagnostics, no extra badge on reference card
        var badge = new Border { CornerRadius = new CornerRadius(12), Child = status };
        var filler = new Border { Background = accent, Height = 0, Width = 0 };
        var min = ReferenceText("MIN —", 10);
        var avg = ReferenceText("AVG —", 10);
        var peak = ReferenceText("MAX —", 10);
        int lastLit = -1;
        double? previous = null;
        Action<double, bool> update = (amount, sparklineChanged) => {
            // The glow fill mirrors the sparkline. Its native PointCollection
            // must not be cleared/rebuilt on an identical two-second sample.
            // A value change, first render, invalidated plot or resize still
            // rebuilds both curve and fill together.
            if (sparklineChanged && spark.Points.Count >= 2)
            {
                double bottom = Math.Max(30, sparkHost.ActualHeight > 0 ?
                    sparkHost.ActualHeight - 1 : 34);
                // Fill and line share the same rolling window. Preserve
                // attached native point slots rather than deleting them.
                var fillPoints = sparkFill.Points;
                SetOverviewChartPoint(fillPoints, 0,
                    new Windows.Foundation.Point(0, bottom));
                int index = 1;
                foreach (var point in spark.Points)
                    SetOverviewChartPoint(fillPoints, index++, point);
                SetOverviewChartPoint(fillPoints, index++,
                    new Windows.Foundation.Point(spark.Points[^1].X, bottom));
                TrimOverviewChartPoints(fillPoints, index);
            }
            int lit = Math.Clamp((int)Math.Round(amount * blocks.Length / 100), 0, blocks.Length);
            ReferenceUpdateSegments(blocks, lit, ref lastLit);
            if (previous is { } prior)
            {
                double delta = amount - prior;
                SetOverviewTextIfChanged(trend,
                    Math.Abs(delta) < .5 ? "→ 0 %" :
                    delta > 0 ? $"↑ {Math.Abs(delta):0} %" : $"↓ {Math.Abs(delta):0} %");
            }
            previous = amount;
        };
        Action resetVisibleState = () =>
        {
            ReferenceUpdateSegments(blocks, 0, ref lastLit);
            SetOverviewTextIfChanged(trend, "—");
            previous = null;
        };
        refs = new GaugeRefs {
            SegmentedLevel = true,
            ResetVisibleState = resetVisibleState,
            ValueText = value, SecondaryText = secondary,
            MinText = min, AvgText = avg, PeakText = peak, StatusText = status,
            StatusBadge = badge, Sparkline = spark, SparkFill = sparkFill, EndDot = dot, SparkHost = sparkHost,
            ReferenceSparkRow = sparkRow,
            Fill = filler, TrackHost = blockGrid, VerticalLevel = false,
            LocalSparkScale = true, VisualUpdater = update
        };

        // Gauge's old continuous-width fill is disconnected: segmented blocks
        // are the real visualization and avoid arbitrary nonresponsive widths.
        var card = ReferenceCard(root, accent: accent);
        card.Tag = "ReferenceOverviewMeter:" + label;
        return card;
    }

    private Border BuildReferenceNetworkCard()
    {
        // Rebuilt cards must receive the current labels, even if counters
        // match the last values published to the previous visual tree.
        _referenceNetworkLastTextState = null;
        var accent = ReferenceColor(9, 186, 255);
        var root = new StackPanel { Spacing = 7, Padding = new Thickness(12, 10, 12, 10) };
        var heading = ReferenceHeaderIcon("Síť", accent);
        root.Children.Add(heading);
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        _referenceNetworkValue = ReferenceText("0 Mb/s", 28, accent, true);
        content.Children.Add(_referenceNetworkValue);
        var plot = new Canvas { Height = ReferenceBaseNetworkHeight, MinWidth = 80, IsHitTestVisible = false,
            Clip = new RectangleGeometry() };
        _referenceNetworkPlotHost = plot;
        _referenceNetworkLastSparkWidth = double.NaN;
        _referenceNetworkLastSparkHeight = double.NaN;
        _referenceNetworkSteadySamples = 0;
        _referenceNetworkLastTotal = double.NaN;
        _referenceNetworkSpark = new Polyline { Stroke = accent, StrokeThickness = 2.1,
            StrokeLineJoin = PenLineJoin.Round };
        plot.Children.Add(_referenceNetworkSpark);
        plot.SizeChanged += (_, e) => {
            if (plot.Clip is RectangleGeometry clip)
                clip.Rect = new Windows.Foundation.Rect(0, 0,
                    Math.Max(0,e.NewSize.Width), Math.Max(0,e.NewSize.Height));
            RenderReferenceNetworkSpark(plot);
        };
        Grid.SetColumn(plot, 1); content.Children.Add(plot);
        root.Children.Add(content);
        _referenceNetworkDetail = ReferenceText("↓ —  ↑ —", 10.5, ReferenceColor(173,196,226));
        root.Children.Add(_referenceNetworkDetail);
        var track = new Grid { ColumnSpacing = 2, Height = 14 };
        for (int i=0;i<22;i++)track.ColumnDefinitions.Add(new ColumnDefinition());
        _referenceNetworkBlocks = new Border[22];
        var networkBorderBrush = ReferenceColor(22,69,110);
        for(int i=0;i<22;i++){
            var block = new Border { CornerRadius = new CornerRadius(2),
                Background = accent, BorderBrush = networkBorderBrush,
                BorderThickness = new Thickness(.85), Opacity = .27 };
            Grid.SetColumn(block,i);track.Children.Add(block);
            _referenceNetworkBlocks[i] = block;
        }
        root.Children.Add(track);
        _referenceNetworkFoot = ReferenceText("Načítám adaptér…", 10.5, ReferenceColor(146,213,218));
        root.Children.Add(_referenceNetworkFoot);
        var card = ReferenceCard(root, accent: accent);
        card.Tag = "ReferenceOverviewMeter:Network";
        return card;
    }
}
