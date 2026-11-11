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

    // The telemetry tick already calculates the peak for its 22 segments.
    // Reuse it for the sparkline instead of scanning the same queue twice.
    // Resizing alone still recomputes the peak from retained samples.
    private static bool ReferenceNetworkSparkNeedsRedraw(int steadySamples,
        int historyCount, int renderedPoints, double width, double height,
        double lastWidth, double lastHeight) =>
        steadySamples < 30 || historyCount != 30 || renderedPoints != 30 ||
        width != lastWidth || height != lastHeight;

    private void RenderReferenceNetworkSpark(Canvas chart, double? measuredPeak = null)
    {
        if (_referenceNetworkSpark == null || _referenceNetworkHistory.Count == 0) return;
        double width = chart.ActualWidth;
        if (width < 10) return;
        double height = chart.ActualHeight;
        if (!ReferenceNetworkSparkNeedsRedraw(_referenceNetworkSteadySamples,
                _referenceNetworkHistory.Count, _referenceNetworkSpark.Points.Count,
                width, height, _referenceNetworkLastSparkWidth, _referenceNetworkLastSparkHeight))
            return;
        double max = measuredPeak ?? Math.Max(1, _referenceNetworkHistory.Max());
        // This is the VISIBLE sixth meter, not the legacy network detail plot.
        // Updating retained PointCollection slots avoids a Clear + 30 Add
        // native WinUI notifications on each fluctuating two-second sample.
        var points = _referenceNetworkSpark.Points;
        int i = 0, n = _referenceNetworkHistory.Count;
        foreach(var value in _referenceNetworkHistory){
            double x = n == 1 ? width-2 : 2 + (width-4)*i/(n-1d);
            SetOverviewChartPoint(points, i, new Windows.Foundation.Point(x,
                (height-2) - Math.Clamp(value/max,0,1)*Math.Max(2,height-7)));
            i++;
        }
        TrimOverviewChartPoints(points, n);
        _referenceNetworkLastSparkWidth = width;
        _referenceNetworkLastSparkHeight = height;
    }

    // Detached WinUI microbenchmark for the visible sixth Overview meter.
    // The same 30-point geometry is fed to the former Clear+Add renderer and
    // the retained-slot renderer; no GUI screenshots or real network changes.
    internal static string RunReferenceNetworkSparkReuseBenchmark()
    {
        const int frames = 360;
        const int sampleMax = 30;
        static (double Ms, long Bytes, int Writes, PointCollection Points)
            Measure(bool reuse)
        {
            var points = new PointCollection();
            int writes = 0;
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int frame = 0; frame < frames; frame++)
            {
                // Grow, roll, resize and shrink as on real telemetry/layout.
                int count = frame >= 330 ? 9 : Math.Min(sampleMax, frame + 1);
                double width = frame is >= 120 and < 240 ? 288 : 240;
                if (!reuse) { points.Clear(); writes++; }
                for (int i = 0; i < count; i++)
                {
                    double v = ((i + frame / 3) * 7d) % 43;
                    double x = count == 1 ? width - 2 : 2 + (width - 4) * i / (count - 1d);
                    var next = new Windows.Foundation.Point(x, 31 - v / 43 * 26);
                    if (reuse)
                    {
                        if (SetOverviewChartPoint(points, i, next)) writes++;
                    }
                    else { points.Add(next); writes++; }
                }
                if (reuse)
                {
                    int beforeCount = points.Count;
                    TrimOverviewChartPoints(points, count);
                    writes += beforeCount - points.Count;
                }
            }
            return (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - beforeBytes, writes, points);
        }

        var prior = Measure(false);
        var retained = Measure(true);
        if (prior.Points.Count != 9 || retained.Points.Count != prior.Points.Count)
            throw new InvalidOperationException("Visible network plot size regression.");
        for (int i = 0; i < prior.Points.Count; i++)
            if (prior.Points[i] != retained.Points[i])
                throw new InvalidOperationException("Visible network plot coordinates regressed.");
        if (retained.Writes >= prior.Writes)
            throw new InvalidOperationException("Visible network plot retained-slot updates did not reduce writes.");
        return $"REFERENCE_NETWORK_SPARK_REUSE_BENCH_OK frames={frames} " +
            $"oldWrites={prior.Writes} newWrites={retained.Writes} " +
            $"oldMs={prior.Ms:F3} newMs={retained.Ms:F3} " +
            $"oldAllocatedB={prior.Bytes} newAllocatedB={retained.Bytes} " +
            "geometry=identical resize=true shrink=true";
    }

    private Border BuildReferenceTemperatureCard()
    {
        var root = new StackPanel { Spacing = 10, Padding = new Thickness(12, 10, 12, 10) };
        _referenceTemperatureRoot = root;
        _referenceLastRenderedTemperature = null;
        root.Children.Add(ReferenceHeaderIcon("Teplota / Stav systému", ReferenceColor(64,189,255)));
        var grid = new Grid { ColumnSpacing = 8 };
        for(int j=0;j<3;j++)grid.ColumnDefinitions.Add(new ColumnDefinition());
        string[] labels = {"GPU teplota","CPU balíček","Základní deska"};
        var colors = new[] { ReferenceColor(53,227,173), ReferenceColor(255,150,44),
            ReferenceColor(115,177,255) };
        var values = new TextBlock[3];
        _referenceTempBlocks = new Border[3][];
        Array.Fill(_referenceTemperatureLastLit, -1);
        for(int j=0;j<3;j++){
            var stack = new StackPanel { Spacing = 4 };
            values[j]=ReferenceText("—", 19, colors[j],true);
            stack.Children.Add(values[j]);
            stack.Children.Add(ReferenceText(labels[j],10.5,ReferenceColor(167,194,219)));
            var ticks = new Grid { ColumnSpacing = 2, Height = 10 };
            for(int k=0;k<7;k++)ticks.ColumnDefinitions.Add(new ColumnDefinition());
            _referenceTempBlocks[j]=new Border[7];
            for(int k=0;k<7;k++){
                var cell=new Border { Background=colors[j],Opacity=.12,
                    BorderThickness=new Thickness(.5),BorderBrush=ReferenceColor(25,62,92),
                    CornerRadius=new CornerRadius(2) };
                _referenceTempBlocks[j][k]=cell;Grid.SetColumn(cell,k);ticks.Children.Add(cell);
            }
            stack.Children.Add(ticks); Grid.SetColumn(stack,j);grid.Children.Add(stack);
        }
        _referenceGpuTemperature=values[0];
        _referenceCpuTemperature=values[1];
        _referenceBoardTemperature=values[2];
        root.Children.Add(grid);
        var bottom=new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        var stability=ReferenceText("● Senzory: podle dostupnosti", 10.5,
            ReferenceColor(63,220,167));
        _referenceSensorStatusText=stability;
        bottom.Children.Add(stability);
        var fan=ReferenceText("Ventilátory: —", 10.5, ReferenceColor(90,173,245));
        _referenceFanTelemetryText=fan;
        Grid.SetColumn(fan,1);bottom.Children.Add(fan);
        root.Children.Add(bottom);
        var card=ReferenceCard(root, accent: ReferenceColor(90, 155, 240));
        card.Tag="ReferenceOverviewMeter:Temperature";
        return card;
    }

    private void UpdateReferenceTemperature(double? cpuC, double? gpuC,
        double? boardC, int? fanRpm)
    {
        if (_referenceGpuTemperature==null || _referenceCpuTemperature==null ||
            _referenceTempBlocks==null) return;
        var state = new TemperatureVisualState(cpuC, gpuC, boardC, fanRpm);
        if (_referenceLastRenderedTemperature == state) return;
        _referenceLastRenderedTemperature = state;
        SetOverviewTextIfChanged(_referenceCpuTemperature, cpuC is { } a ? $"{a:0} °C" : "—");
        SetOverviewTextIfChanged(_referenceGpuTemperature, gpuC is { } b ? $"{b:0} °C" : "—");
        SetOverviewTextIfChanged(_referenceBoardTemperature!,
            boardC is { } board ? $"{board:0} °C" : "—");
        if (_referenceSensorStatusText != null)
        {
            int count = OverviewTemperatureReadingPolicy.AvailableSensorCount(cpuC, gpuC, boardC);
            SetOverviewTextIfChanged(_referenceSensorStatusText,
                count == 0 ? "● Senzory: nedostupné" : $"● Senzory: {count}/3 dostupné");
        }
        if (_referenceFanTelemetryText != null)
            SetOverviewTextIfChanged(_referenceFanTelemetryText,
                fanRpm is { } rpm ? $"Ventilátory: {rpm:0} RPM" : "Ventilátory: —");
        for(int j=0;j<3;j++)
        {
            double? value = j == 0 ? gpuC : j == 1 ? cpuC : boardC;
            int lit=value.HasValue ? Math.Clamp((int)Math.Round(value.Value*7/100),0,7):0;
            // The 7 native opacity reads depend on the lit bucket, not on
            // each raw 2s temperature sample. Refresh on rebuild or bucket change.
            if (_referenceTemperatureLastLit[j] == lit) continue;
            for(int k=0;k<7;k++){
                double target=k<lit?.94:.13;
                if(Math.Abs(_referenceTempBlocks[j][k].Opacity-target)>.01)
                    _referenceTempBlocks[j][k].Opacity=target;
            }
            _referenceTemperatureLastLit[j] = lit;
        }
    }

    private static double ReferenceSafeRate(double rate) =>
        double.IsFinite(rate) ? Math.Max(0, rate) : 0;

    private static double ReferenceNetworkTotal(double down, double up, bool online)
    {
        if (!online) return 0;
        double total = ReferenceSafeRate(down) + ReferenceSafeRate(up);
        return double.IsFinite(total) ? total : 0;
    }

    private void UpdateReferenceNetwork(double down, double up, bool online,
        bool refreshSpark = true, string? adapterName = null)
    {
        if(_referenceNetworkValue==null)return;
        double safeDown = ReferenceSafeRate(down);
        double safeUp = ReferenceSafeRate(up);
        double total = ReferenceNetworkTotal(down, up, online);
        string adapter = adapterName ?? "Připojení";
        var labelState = new ReferenceNetworkTextState(safeDown, safeUp, online, adapter);
        if (_referenceNetworkLastTextState != labelState)
        {
            // Labels depend on rates, connectivity and adapter identity.
            // Preserve the 2-second history/segment updates below even when
            // rates are steady; only skip redundant string formatting.
            _referenceNetworkLastTotalLabel = online ? FormatRate(total) : "—";
            SetOverviewTextIfChanged(_referenceNetworkValue, _referenceNetworkLastTotalLabel);
            SetOverviewTextIfChanged(_referenceNetworkDetail!,
                online ? $"↓ {FormatRate(safeDown)}  |  ↑ {FormatRate(safeUp)}" : "Síť odpojena");
            SetOverviewTextIfChanged(_referenceNetworkFoot!, online ? adapter : "Síť odpojena");
            _referenceNetworkLastTextState = labelState;
        }
        _referenceNetworkSteadySamples = total == _referenceNetworkLastTotal
            ? Math.Min(30, _referenceNetworkSteadySamples + 1) : 1;
        _referenceNetworkLastTotal = total;
        _referenceNetworkHistory.Enqueue(total);
        while(_referenceNetworkHistory.Count>30)_referenceNetworkHistory.Dequeue();
        // Queue<T>.Max() through LINQ boxes its enumerator on every 2s UI tick.
        // A bounded 30-value scan avoids that allocation and preserves the
        // exact maximum even when the former peak just left the queue.
        double max = 1;
        foreach (double reading in _referenceNetworkHistory)
            max = Math.Max(max, reading);
        // Keep speed labels and 22 live segments at 2 seconds. Native 30-point
        // sparkline geometry shares the existing 4-second chart cadence;
        // resize continues to redraw immediately via SizeChanged.
        if (refreshSpark && _referenceNetworkSpark?.Parent is Canvas canvas)
            RenderReferenceNetworkSpark(canvas, max);
        int lit = online?Math.Clamp((int)Math.Round(total/max*22),0,22):0;
        ReferenceUpdateSegments(_referenceNetworkBlocks,lit,ref _referenceNetworkLastLit);
        if (_referenceChartNetwork != null)
            SetOverviewTextIfChanged(_referenceChartNetwork, _referenceNetworkLastTotalLabel);
    }

    private Border BuildReferenceInlineInfoCard(string name, string icon,
        SolidColorBrush accent, params UIElement[] lines)
    {
        var header=new StackPanel { Orientation=Orientation.Horizontal,Spacing=5 };
        header.Children.Add(SectionIcon(icon,14));
        header.Children.Add(ReferenceText(name,11,ReferenceColor(224,239,255),true));
        var contents=new StackPanel { Spacing=2, Padding=new Thickness(9,7,9,7) };
        contents.Children.Add(header);
        foreach(var line in lines) contents.Children.Add(line);
        var card=ReferenceCard(contents,ReferenceBaseInfoHeight,accent);
        // Shared inline-card geometry. A Grid row may expand when a taller
        // viewport permits more detail; all cards must fill that exact row.
        card.HorizontalAlignment=HorizontalAlignment.Stretch;
        card.VerticalAlignment=VerticalAlignment.Stretch;
        card.Margin=new Thickness(0);
        return card;
    }

    private Grid BuildReferenceInlineSystemCards(OverviewTextRefs refs, TextBlock uptime)
    {
        // Always visible beneath the six gauges, with no repeated Network card.
        // Three 78px cards take less space than the former 190px Flyout items.
        var row=new Grid { ColumnSpacing=8, MinHeight=78, MaxHeight=82 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());

        refs.Os.FontSize=10.5;
        refs.Os.FontWeight=FontWeights.SemiBold;
        refs.Os.TextWrapping=TextWrapping.NoWrap;
        refs.Os.TextTrimming=TextTrimming.CharacterEllipsis;
        refs.Os.MaxLines=1;
        refs.Board.FontSize=10.5;
        refs.Board.TextWrapping=TextWrapping.NoWrap;
        refs.Board.TextTrimming=TextTrimming.CharacterEllipsis;
        refs.Board.MaxLines=1;
        refs.Power.FontSize = 10.5;
        refs.Power.TextTrimming = TextTrimming.CharacterEllipsis;
        refs.Power.MaxLines = 1;
        refs.Power.Visibility = Visibility.Collapsed;
        var system=BuildReferenceInlineInfoCard("Systém","system",
            ReferenceColor(65,170,255),refs.Os,refs.Board,refs.Power);
        system.Tag="OverviewInlineSystem";
        row.Children.Add(system);

        refs.History.FontSize=10.5;
        refs.History.TextWrapping=TextWrapping.NoWrap;
        refs.History.TextTrimming=TextTrimming.CharacterEllipsis;
        refs.History.MaxLines=1;
        _overviewSystemStatusText=ReferenceText(
            _overviewTextRefs?.SystemStatus??OverviewSystemLoading,10,
            ReferenceColor(152,129,255));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            _overviewSystemStatusText,"OverviewSystemLoadStatus");
        _overviewDriverStatusText=ReferenceText(
            _overviewTextRefs?.DriverStatus??OverviewDriverLoading,10.5,
            ReferenceColor(163,187,216));
        _overviewDriverStatusText.Visibility = Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            _overviewDriverStatusText,"OverviewDriverLoadStatus");
        uptime.FontSize=10.5;uptime.TextTrimming=TextTrimming.CharacterEllipsis;
        uptime.MaxLines=1;
        // Keep driver/sensor fields refreshed even though limited vertical room:
        // use a single compact row with status and duration in the UI.
        var state=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};
        _overviewSystemStatusText.MaxWidth=95;
        state.Children.Add(_overviewSystemStatusText);
        uptime.MaxWidth=125;
        state.Children.Add(uptime);
        var optimizer=BuildReferenceInlineInfoCard("Stav optimizeru","system",
            ReferenceColor(150,113,255),refs.History,state,_overviewDriverStatusText);
        optimizer.Tag="OverviewInlineOptimizer";
        Grid.SetColumn(optimizer,1);row.Children.Add(optimizer);

        _overviewChangelogCaption=ReferenceText("Celá historie →",10,
            ReferenceColor(140,189,240));
        _overviewHistoryTimes=new TextBlock[4];
        _overviewHistoryRows=new TextBlock[4];
        for(int i=0;i<4;i++)
        {
            _overviewHistoryTimes[i]=ReferenceText("—",10,ReferenceColor(185,205,238),true);
            _overviewHistoryRows[i]=ReferenceText("Načítám…",10,ReferenceColor(162,183,218));
        }
        var latest=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5};
        latest.Children.Add(_overviewHistoryTimes[0]);
        latest.Children.Add(_overviewHistoryRows[0]);
        _overviewInlineSecondHistory = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 5,
            Visibility = Visibility.Collapsed
        };
        _overviewInlineSecondHistory.Children.Add(_overviewHistoryTimes[1]);
        _overviewInlineSecondHistory.Children.Add(_overviewHistoryRows[1]);
        var historyCard=BuildReferenceInlineInfoCard("Changelog","fixer",
            ReferenceColor(222,74,219),latest,_overviewInlineSecondHistory,_overviewChangelogCaption);
        historyCard.Tag="OverviewInlineChangelog";
        var action=new Button{
            Background=new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0)),
            BorderThickness=new Thickness(0),Padding=new Thickness(0),
            Margin=new Thickness(0),MinHeight=ReferenceBaseInfoHeight,
            HorizontalAlignment=HorizontalAlignment.Stretch,
            VerticalAlignment=VerticalAlignment.Stretch,
            HorizontalContentAlignment=HorizontalAlignment.Stretch,
            VerticalContentAlignment=VerticalAlignment.Stretch,
            Content=historyCard,CornerRadius=historyCard.CornerRadius
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            action,"OverviewChangelogButton");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(action,"Changelog");
        action.Click+=(_,_)=>SafeTaskService.Forget(
            ShowChangelogPopupAsync(),"overview","Open full history");
        _overviewChangelogAction=action;
        Grid.SetColumn(action,2);row.Children.Add(action);
        return row;
    }

    private Border BuildReferenceMainPerformanceChart()
    {
        // Memoized text belongs to this visual tree, not the prior one.
        _referenceLastHeaderTextKey = null;
        _referenceLeftLastLit = -1;
        // A rebuilt Overview must not retain the old WinUI grid elements.
        _referencePlotGridLines.Clear();
        _referenceVerticalGridLines.Clear();
        _referenceGridWidth = double.NaN;
        _referenceGridHeight = double.NaN;
        var root=new Grid { Padding=new Thickness(14,11,14,10),MinHeight=163 };
        root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(41)});
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(18)});
        var head=new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var chartHeading = new StackPanel { Orientation=Orientation.Horizontal,
            Spacing=8, VerticalAlignment=VerticalAlignment.Center };
        chartHeading.Children.Add(SectionIcon("performance",21));
        chartHeading.Children.Add(ReferenceText("Výkon systému",19,
            ReferenceColor(236,246,255),true));
        head.Children.Add(chartHeading);
        var stats=new StackPanel{Orientation=Orientation.Horizontal,Spacing=9,
            VerticalAlignment=VerticalAlignment.Center};
        void AddStat(string name, SolidColorBrush color, out TextBlock text)
        {
            if(stats.Children.Count!=0)
                stats.Children.Add(ReferenceText("│",12,ReferenceColor(93,137,186)));
            stats.Children.Add(ReferenceText(name,11,ReferenceColor(188,210,236)));
            text=ReferenceText("—",12,color,true);stats.Children.Add(text);
        }
        AddStat("CPU",ReferenceColor(15,202,255),out _referenceChartCpu!);
        AddStat("RAM",ReferenceColor(129,113,255),out _referenceChartRam!);
        AddStat("GPU",ReferenceColor(231,81,238),out _referenceChartGpu!);
        AddStat("DISK",ReferenceColor(37,221,125),out _referenceChartDisk!);
        AddStat("SÍŤ",ReferenceColor(70,159,255),out _referenceChartNetwork!);
        Grid.SetColumn(stats,1);head.Children.Add(stats);root.Children.Add(head);

        var display=new Grid{ColumnSpacing=12};
        display.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(67)});
        display.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(38)});
        display.ColumnDefinitions.Add(new ColumnDefinition());
        var meter=new StackPanel{Spacing=1,VerticalAlignment=VerticalAlignment.Center};
        _referenceLeftBlocks = new Border[12];
        for(int i=0;i<12;i++){
            var segment=new Border{
                Height=5,Width=36,CornerRadius=new CornerRadius(2),
                Background=ReferenceColor(0,190,255),
                Opacity=.12
            };
            _referenceLeftBlocks[i] = segment;
            meter.Children.Add(segment);
        }
        _referenceLeftUsage=ReferenceText("—",17,ReferenceColor(200,238,255),true);
        _referenceLeftUsage.Margin=new Thickness(0,4,0,0);
        meter.Children.Add(_referenceLeftUsage);
        meter.Children.Add(ReferenceText("Využití celkem",10.5,ReferenceColor(153,182,212)));
        Grid.SetColumn(meter,0);display.Children.Add(meter);

        var yTicks = new Canvas { Height = ReferenceBasePlotHeight };
        _referencePlotYTicksHost = yTicks;
        _referencePlotYTickLabels = new TextBlock[5];
        string[] tickLabels = { "100 %", "75 %", "50 %", "25 %", "0 %" };
        for (int i = 0; i < tickLabels.Length; i++)
        {
            var tick = ReferenceText(tickLabels[i], 10.5, ReferenceColor(151, 176, 209));
            Canvas.SetTop(tick, i * (ReferenceBasePlotHeight - 16) / 4);
            _referencePlotYTickLabels[i] = tick;
            yTicks.Children.Add(tick);
        }
        Grid.SetColumn(yTicks,1); display.Children.Add(yTicks);

        _referenceCpuPlotHost=new Canvas{Height=ReferenceBasePlotHeight,MinWidth=150,
            Background=new SolidColorBrush(Windows.UI.Color.FromArgb(42,3,17,38)),
            IsHitTestVisible=false,
            Clip = new RectangleGeometry()
        };
        for(int j=0;j<=4;j++){
            var line=new Line{Stroke=ReferenceColor(38,84,126),
                StrokeThickness=.65,Opacity=.75,Y1=j*24.25,Y2=j*24.25};
            _referenceCpuPlotHost.Children.Add(line);
            _referencePlotGridLines.Add(line);
        }
        // 39 grid lines share ONE UI Path instead of 39 FrameworkElements.
        // Native LineGeometry objects change only when the plot resizes.
        var verticalLines = new GeometryGroup();
        for(int j=1;j<40;j++){
            var vertical = new LineGeometry {
                StartPoint = new Windows.Foundation.Point(0,0),
                EndPoint = new Windows.Foundation.Point(0,99)
            };
            verticalLines.Children.Add(vertical);
            _referenceVerticalGridLines.Add(vertical);
        }
        _referenceCpuPlotHost.Children.Add(new Path {
            Stroke = ReferenceColor(26,69,109), StrokeThickness = .6,
            Opacity = .6, Data = verticalLines, IsHitTestVisible = false
        });
        _referenceCpuArea = new OverviewSegmentedChart(null,
            new LinearGradientBrush {
                StartPoint=new Windows.Foundation.Point(.5,0),
                EndPoint=new Windows.Foundation.Point(.5,1),
                GradientStops = {
                    new GradientStop { Color=Windows.UI.Color.FromArgb(95,0,150,255),Offset=0 },
                    new GradientStop { Color=Windows.UI.Color.FromArgb(32,0,106,220),Offset=.52 },
                    new GradientStop { Color=Windows.UI.Color.FromArgb(0,0,66,165),Offset=1 }
                }
            });
        _referenceCpuGlow = new OverviewSegmentedChart(
            new SolidColorBrush(Windows.UI.Color.FromArgb(74,0,170,255)));
        _referenceCpuGlow.Visual.StrokeThickness = 7;
        _referenceCpuPlot = new OverviewSegmentedChart(ReferenceColor(0,198,255));
        _referenceCpuPlot.Visual.StrokeThickness = 2.65;
        _referenceCpuPlotHost.Children.Add(_referenceCpuArea.Visual);
        _referenceCpuPlotHost.Children.Add(_referenceCpuGlow.Visual);
        _referenceCpuPlotHost.Children.Add(_referenceCpuPlot.Visual);
        _referenceCpuPlotHost.SizeChanged+=(_,e)=>{
            if(_referenceCpuPlotHost.Clip is RectangleGeometry clip)
                clip.Rect = new Windows.Foundation.Rect(0,0,
                    Math.Max(0,e.NewSize.Width),Math.Max(0,e.NewSize.Height));
            RenderReferenceMainPlot();
        };
        Grid.SetColumn(_referenceCpuPlotHost,2);
        display.Children.Add(_referenceCpuPlotHost);
        Grid.SetRow(display,1);root.Children.Add(display);
        var xCaption=ReferenceText("Posledních 60 sekund",10.5,ReferenceColor(136,163,199));
        xCaption.Margin = new Thickness(111,0,0,0);
        Grid.SetRow(xCaption,2);root.Children.Add(xCaption);
        var card=ReferenceCard(root,168,ReferenceCpuAccent());
        card.Tag="ReferenceOverviewMainChart";
        return card;
    }
    private readonly List<Line> _referencePlotGridLines=new();

    // Distribute unused viewport height between the 60s chart, BOTH gauge
    // rows and the inline information row. Subtract our previous additions
    // first, so a repeated pass or window resize cannot keep growing content.
    private readonly record struct ReferenceOverviewHeights(double Plot, double Spark, double Info);

    private static ReferenceOverviewHeights ReferenceFitHeights(
        double viewport, double content, double plot, double spark, double info,
        int gaugeRows = 2)
    {
        if (!double.IsFinite(viewport) || !double.IsFinite(content) ||
            !double.IsFinite(plot) || !double.IsFinite(spark) ||
            !double.IsFinite(info) || viewport <= 0 || content <= 0 || gaugeRows < 1)
            return new(ReferenceBasePlotHeight, ReferenceBaseSparkHeight, ReferenceBaseInfoHeight);

        double naturalContent = content
            - Math.Max(0, plot - ReferenceBasePlotHeight)
            - gaugeRows * Math.Max(0, spark - ReferenceBaseSparkHeight)
            - Math.Max(0, info - ReferenceBaseInfoHeight);
        double free = Math.Max(0, viewport - naturalContent - 6);
        return new(ReferenceBasePlotHeight + free * .40,
            ReferenceBaseSparkHeight + free * .44 / gaugeRows,
            ReferenceBaseInfoHeight + free * .16);
    }

    private bool FitReferenceOverviewToViewport(double viewport, double content)
    {
        if (_section != "overview" || _referenceCpuPlotHost == null ||
            _referencePlotYTicksHost == null ||
            _referencePlotYTickLabels is not { Length: 5 } ||
            _referenceNetworkPlotHost == null || _referenceTemperatureRoot == null ||
            _cpuGauge?.ReferenceSparkRow == null || _ramGauge?.ReferenceSparkRow == null ||
            _gpuGauge?.ReferenceSparkRow == null || _diskGauge?.ReferenceSparkRow == null ||
            _overviewTelemetryGrid is not { Children.Count: 6 } ||
            _overviewInfoCardsGrid is not { Children.Count: 3 })
            return false;

        int gaugeRows = _overviewTelemetryGrid.RowDefinitions.Count;
        if (_referenceBaseGaugeRowHeights == null ||
            _referenceBaseGaugeRowHeights.Length != gaugeRows)
        {
            var actualRows = new double[gaugeRows];
            for (int i = 0; i < gaugeRows; i++)
            {
                actualRows[i] = _overviewTelemetryGrid.RowDefinitions[i].ActualHeight;
                if (actualRows[i] <= 0) return false;
            }
            _referenceBaseGaugeRowHeights = actualRows;
        }

        double currentPlot = _referenceCpuPlotHost.Height;
        double currentSpark = _cpuGauge.SparkHost.Height;
        double currentInfo = _overviewInfoCardsGrid.MinHeight;
        var target = ReferenceFitHeights(viewport, content, currentPlot, currentSpark, currentInfo, gaugeRows);
        if (Math.Abs(target.Plot - currentPlot) < .75 &&
            Math.Abs(target.Spark - currentSpark) < .75 &&
            Math.Abs(target.Info - currentInfo) < .75)
            return false;

        _referenceCpuPlotHost.Height = target.Plot;
        _referencePlotYTicksHost.Height = target.Plot;
        for (int i = 0; i < _referencePlotYTickLabels.Length; i++)
            Canvas.SetTop(_referencePlotYTickLabels[i], i * (target.Plot - 16) / 4);

        foreach (var gauge in new[] { _cpuGauge, _ramGauge, _gpuGauge, _diskGauge })
        {
            if (gauge == null) continue;
            gauge.SparkHost.Height = target.Spark;
            gauge.ReferenceSparkRow!.Height = new GridLength(target.Spark);
        }
        double extraPerRow = target.Spark - ReferenceBaseSparkHeight;
        for (int i = 0; i < gaugeRows; i++)
            _overviewTelemetryGrid.RowDefinitions[i].Height =
                new GridLength(_referenceBaseGaugeRowHeights[i] + extraPerRow);
        _referenceNetworkPlotHost.Height = ReferenceBaseNetworkHeight + extraPerRow;
        _referenceTemperatureRoot.Spacing = 10 + extraPerRow / 2;

        _overviewInfoCardsGrid.MinHeight = target.Info;
        _overviewInfoCardsGrid.MaxHeight = target.Info + 4;
        // Additional data are already fetched in the normal Overview snapshot.
        // Show them only when there is room, never create a new worker.
        bool expanded = target.Info >= 90;
        if (_overviewTextRefs != null)
            _overviewTextRefs.Power.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (_overviewDriverStatusText != null)
            _overviewDriverStatusText.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (_overviewInlineSecondHistory != null)
            _overviewInlineSecondHistory.Visibility = target.Info >= 96 ?
                Visibility.Visible : Visibility.Collapsed;
        return true;
    }

    // Windows can transiently report non-finite CPU utilization while a
    // sensor initializes. Never send invalid points to native WinUI geometry.
    private static double ReferenceFiniteCpuPercent(double percent) =>
        double.IsFinite(percent) ? Math.Clamp(percent, 0d, 100d) : 0d;

    private static double ReferencePlotTimeX(DateTime sampleUtc, DateTime rightEdgeUtc, double width) =>
        Math.Clamp((sampleUtc - rightEdgeUtc.AddSeconds(-60)).TotalSeconds / 60d,
            0d, 1d) * Math.Max(0, width - 2);

    private static bool ReferenceGridNeedsResize(double width, double height,
        double lastWidth, double lastHeight) =>
        width != lastWidth || height != lastHeight;

    private void RenderReferenceMainPlot()
    {
        if (_referenceCpuPlotHost == null || _referenceCpuPlot == null ||
            _referenceCpuGlow == null || _referenceCpuArea == null) return;
        double w = _referenceCpuPlotHost.ActualWidth;
        if (w < 10) return;
        double h = Math.Max(ReferenceBasePlotHeight, _referenceCpuPlotHost.ActualHeight);
        if (ReferenceGridNeedsResize(w, h, _referenceGridWidth, _referenceGridHeight))
        {
            for (int i = 0; i < _referencePlotGridLines.Count; i++)
            {
                var l = _referencePlotGridLines[i];
                l.X1 = 0; l.X2 = w;
                l.Y1 = l.Y2 = i * (h - 2) / 4;
            }
            for (int i = 0; i < _referenceVerticalGridLines.Count; i++)
            {
                double x = (i + 1d) * w / 40d;
                _referenceVerticalGridLines[i].StartPoint = new Windows.Foundation.Point(x, 0);
                _referenceVerticalGridLines[i].EndPoint = new Windows.Foundation.Point(x, h);
            }
            _referenceGridWidth = w; _referenceGridHeight = h;
        }

        DateTime rightEdge = DateTime.UtcNow;
        int end = _overviewPerfHistory.Count;
        int first = end;
        DateTime cutoff = rightEdge.AddSeconds(-60);
        while (first > 0 && _overviewPerfHistory[first - 1].Utc >= cutoff) first--;
        first = Math.Max(first, end - 60);
        RenderReferenceCpuHistorySegments(_overviewPerfHistory, first, rightEdge, w, h,
            _referenceRawPlotPoints, _referenceCpuPlot, _referenceCpuGlow, _referenceCpuArea);
        _referenceLastPlotUtc = end == 0 ? default : _overviewPerfHistory[^1].Utc;
    }

    // Preserve Catmull-Rom smoothing only inside consecutive valid samples.
    // Missing native readings and sleep/resume gaps must not be bridged with
    // fabricated curves or filled areas. All 3 paths reuse native figures.
    private static void RenderReferenceCpuHistorySegments(
        IReadOnlyList<OverviewPerfSample> history, int first, DateTime rightEdge,
        double w, double h, List<Windows.Foundation.Point> raw,
        OverviewSegmentedChart plot, OverviewSegmentedChart glow, OverviewSegmentedChart area)
    {
        plot.Reset(); glow.Reset(); area.Reset();
        raw.Clear();
        double bottom = h - 3;
        DateTime lastValid = default;
        for (int i = Math.Max(0, first); i < history.Count; i++)
        {
            var item = history[i];
            bool valid = item.Cpu is { } v && double.IsFinite(v);
            bool gap = lastValid != default &&
                (item.Utc <= lastValid || item.Utc - lastValid > TimeSpan.FromSeconds(8));
            if (!valid || gap)
            {
                AppendReferenceSmoothCpuRun(raw, bottom, plot, glow, area);
                raw.Clear();
                lastValid = default;
            }
            if (!valid) continue;
            double x = ReferencePlotTimeX(item.Utc, rightEdge, w);
            double y = bottom - ReferenceFiniteCpuPercent(item.Cpu!.Value) * (h - 7) / 100;
            raw.Add(new Windows.Foundation.Point(x, y));
            lastValid = item.Utc;
        }
        AppendReferenceSmoothCpuRun(raw, bottom, plot, glow, area);
        raw.Clear();
    }

    private static void AppendReferenceSmoothCpuRun(
        List<Windows.Foundation.Point> raw, double bottom,
        OverviewSegmentedChart plot, OverviewSegmentedChart glow, OverviewSegmentedChart area)
    {
        if (raw.Count == 0) return;
        var p0 = raw[0];
        plot.AddPoint(p0.X, p0.Y, bottom);
        glow.AddPoint(p0.X, p0.Y, bottom);
        area.AddPoint(p0.X, p0.Y, bottom);
        for (int i = 1; i < raw.Count; i++)
        {
            var a = raw[Math.Max(0, i - 2)];
            var b = raw[i - 1];
            var c = raw[i];
            var d = raw[Math.Min(raw.Count - 1, i + 1)];
            for (int step = 1; step <= 4; step++)
            {
                double t = step / 4d, t2 = t * t, t3 = t2 * t;
                double y = .5 * ((2 * b.Y) + (-a.Y + c.Y) * t +
                    (2 * a.Y - 5 * b.Y + 4 * c.Y - d.Y) * t2 +
                    (-a.Y + 3 * b.Y - 3 * c.Y + d.Y) * t3);
                y = Math.Clamp(y, Math.Min(b.Y, c.Y), Math.Max(b.Y, c.Y));
                double x = b.X + (c.X - b.X) * t;
                plot.AddPoint(x, y, bottom);
                glow.AddPoint(x, y, bottom);
                area.AddPoint(x, y, bottom);
            }
        }
        plot.FinishRun(); glow.FinishRun(); area.FinishRun();
    }

    internal static string RunReferenceCpuGapSmoke()
    {
        var plot = new OverviewSegmentedChart(ReferenceCpuAccent());
        var glow = new OverviewSegmentedChart(ReferenceCpuAccent());
        var area = new OverviewSegmentedChart(null, ReferenceCpuAccent());
        var raw = new List<Windows.Foundation.Point>(64);
        DateTime start = new(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc);
        var history = new List<OverviewPerfSample>
        {
            new(start, 25, null, 40, null),
            new(start.AddSeconds(2), 80, null, 40, null),
            new(start.AddSeconds(4), null, null, 40, null),
            new(start.AddSeconds(6), 35, null, 40, null),
            new(start.AddSeconds(20), 70, null, 40, null),
            new(start.AddSeconds(22), 0, null, 40, null)
        };
        for (int repeat = 0; repeat < 100; repeat++)
        {
            RenderReferenceCpuHistorySegments(history, 0, start.AddSeconds(24),
                500, 120, raw, plot, glow, area);
            if (plot.SegmentCount != 3 || glow.SegmentCount != 3 ||
                area.SegmentCount != 3 || plot.AllocatedSegments != 3 ||
                glow.AllocatedSegments != 3 || area.AllocatedSegments != 3 ||
                area.GetSegment(1)?.IsClosed != true)
                throw new InvalidOperationException("Reference CPU outage was bridged or geometry leaked.");
        }
        history.Clear();
        RenderReferenceCpuHistorySegments(history, 0, start.AddSeconds(24),
            500, 120, raw, plot, glow, area);
        if (plot.SegmentCount != 0 || glow.SegmentCount != 0 ||
            area.SegmentCount != 0)
            throw new InvalidOperationException("Reference CPU chart retained expired points.");
        if (ReferenceCpuPlotDue(false, start.AddSeconds(2), start) ||
            !ReferenceCpuPlotDue(true, start.AddSeconds(2), start) ||
            ReferenceCpuPlotDue(true, start.AddSeconds(2), start.AddSeconds(2)) ||
            ReferenceCpuPlotDue(false, start, default))
            throw new InvalidOperationException("Reference CPU chart 4-second cadence guard regressed.");
        return "REFERENCE_CPU_GAP_SMOKE_OK gapMissing=true gapSleep=true " +
            "smoothedRuns=3 frames=100 pooled=3 expiredCleared=true writes=0";
    }
    // The main 60-second chart shares the established 4-second history cadence.
    // Keep the independent 2-second live gauge/header path untouched.
    private static bool ReferenceCpuPlotDue(bool cadenceTick, DateTime newestUtc,
        DateTime lastRenderedUtc) => cadenceTick && newestUtc != lastRenderedUtc;

    private void UpdateReferenceHeader(double? cpu,double? ram,double?gpu,double?disk,
        bool refreshPlot = true)
    {
        if (_referenceChartCpu == null) return;
        // Native TextBlock setters already skip equal text, but formatting all
        // 4 rates plus clock and GPU temperature every 2 seconds still allocates
        // managed strings. Cache the exact *inputs* so a new value with the same
        // rounded label is still handled by the existing display policy.
        double? safeCpu = cpu is { } c && double.IsFinite(c)
            ? ReferenceFiniteCpuPercent(c) : null;
        double? safeRam = ram is { } m && double.IsFinite(m) ? m : null;
        double? safeGpu = gpu is { } g && double.IsFinite(g) ? g : null;
        double? safeDisk = disk is { } d && double.IsFinite(d) ? d : null;
        double? clock = _cpuGauge != null && _referenceCpuClockMhz is { } mhz &&
            double.IsFinite(mhz) && mhz > 100 ? mhz : null;
        double? gpuTemperature = _gpuGauge != null && _referenceSensorGpu is { } t &&
            double.IsFinite(t) && _overviewTextRefs != null ? t : null;
        string? gpuLabel = gpuTemperature.HasValue ? _overviewTextRefs!.Gpu.Text : null;
        var next = new ReferenceHeaderTextKey(
            safeCpu, safeRam, safeGpu, safeDisk, clock, gpuTemperature, gpuLabel);
        var previous = _referenceLastHeaderTextKey;
        if (previous != next)
        {
            if (!previous.HasValue || previous.Value.Cpu != next.Cpu)
            {
                string label = next.Cpu is { } value ? $"{value:0} %" : "—";
                SetOverviewTextIfChanged(_referenceChartCpu, label);
                SetOverviewTextIfChanged(_referenceLeftUsage!, label);
            }
            if (!previous.HasValue || previous.Value.Ram != next.Ram)
                SetOverviewTextIfChanged(_referenceChartRam!,
                    next.Ram is { } value ? $"{value:0} %" : "—");
            if (!previous.HasValue || previous.Value.Gpu != next.Gpu)
                SetOverviewTextIfChanged(_referenceChartGpu!,
                    next.Gpu is { } value ? $"{value:0} %" : "—");
            if (!previous.HasValue || previous.Value.Disk != next.Disk)
                SetOverviewTextIfChanged(_referenceChartDisk!,
                    next.Disk is { } value ? $"{value:0} %" : "—");
            if (_cpuGauge != null && next.ClockMhz is { } freq &&
                (!previous.HasValue || previous.Value.ClockMhz != next.ClockMhz))
                SetOverviewTextIfChanged(_cpuGauge.SecondaryText,
                    $"{freq / 1000d:0.00} GHz  |  {Environment.ProcessorCount} vláken");
            if (_gpuGauge != null && next.GpuTemp is { } temp &&
                (!previous.HasValue || previous.Value.GpuTemp != next.GpuTemp ||
                 previous.Value.GpuLabel != next.GpuLabel))
                SetOverviewTextIfChanged(_gpuGauge.SecondaryText,
                    $"{next.GpuLabel}  |  {temp:0} °C");
            _referenceLastHeaderTextKey = next;
        }
        // Meter levels and history/geometry remain independent of text updates.
        ReferenceUpdateLeftMeter(safeCpu ?? 0);
        // Only rebuild the three native smoothed CPU geometries on the existing
        // 4-second history-chart cadence. Gauge values, 22 segments and all
        // text remain live at 2 seconds; resize still renders immediately.
        if (_overviewPerfHistory.Count > 0 &&
            ReferenceCpuPlotDue(refreshPlot, _overviewPerfHistory[^1].Utc, _referenceLastPlotUtc))
            RenderReferenceMainPlot();
    }

    private void ReferenceUpdateLeftMeter(double cpu)
    {
        if(_referenceLeftBlocks==null)return;
        cpu=ReferenceFiniteCpuPercent(cpu);
        int lit=Math.Clamp((int)Math.Round(cpu*12/100),0,12);
        int previous=_referenceLeftLastLit;
        if(lit==previous)return;
        int first = previous < 0 ? 0 : 12 - Math.Max(previous, lit);
        int end = previous < 0 ? 12 : 12 - Math.Min(previous, lit);
        for(int i=first;i<end;i++)
        {
            double opacity = i >= 12-lit ? .94 : .12;
            if(Math.Abs(_referenceLeftBlocks[i].Opacity-opacity)>.01)
                _referenceLeftBlocks[i].Opacity=opacity;
        }
        _referenceLeftLastLit=lit;
    }

    private const int ReferenceOverviewDetailsCardCount = 3;

    internal static int ReferenceOverviewColumnCount(double windowWidth) =>
        windowWidth - ShellSidebarWidth - 48 >= 770 ? 3 : 2;

    internal static string RunReferenceOverviewPolicySmoke()
    {
        // Three information cards stay permanently inline, with no repeated
        // network card and no hidden system flyout.
        if (ReferenceOverviewDetailsCardCount != 3)
            throw new InvalidOperationException("Overview must show 3 unique system detail cards.");
        var surface = ReferenceGlassSurface(ReferenceGpuAccent()) as LinearGradientBrush;
        if (surface == null || surface.GradientStops.Count != 3 ||
            surface.GradientStops[0].Color.B <= surface.GradientStops[0].Color.R ||
            surface.GradientStops[0].Color.A >= 255 ||
            surface.GradientStops[2].Color.R >= 20)
            throw new InvalidOperationException("Overview glass regressed to flat opaque blue.");
        var edge = ReferenceThinRim() as LinearGradientBrush;
        if (edge == null || edge.GradientStops[0].Color.A > 140)
            throw new InvalidOperationException("Overview acquired bright opaque white borders.");
        if (ReferenceOverviewColumnCount(1304)!=3 ||
            ReferenceOverviewColumnCount(920)!=2)
            throw new InvalidOperationException("Reference Overview must have 3 columns at normal size.");
        if(ReferenceColor(0,191,255).Color.B<200)throw new InvalidOperationException("cyan gauge palette");
        double[] values={0,12,48,100};
        foreach(double value in values){
            int lit=Math.Clamp((int)Math.Round(value*22/100),0,22);
            if(lit<0||lit>22)throw new InvalidOperationException("segment bound");
        }
        var compact = ReferenceFitHeights(636,630,99,42,78);
        var tall = ReferenceFitHeights(736,630,99,42,78);
        var repeated = ReferenceFitHeights(736,730,tall.Plot,tall.Spark,tall.Info);
        var smaller = ReferenceFitHeights(536,730,tall.Plot,tall.Spark,tall.Info);
        if (compact != new ReferenceOverviewHeights(99,42,78) ||
            Math.Abs(tall.Plot-139)>0.01 ||
            Math.Abs(tall.Spark-64)>0.01 ||
            Math.Abs(tall.Info-94)>0.01 ||
            Math.Abs((tall.Plot-99) + 2*(tall.Spark-42) + (tall.Info-78)-100)>0.01 ||
            Math.Abs(repeated.Plot-tall.Plot)>0.01 ||
            Math.Abs(repeated.Spark-tall.Spark)>0.01 ||
            Math.Abs(repeated.Info-tall.Info)>0.01 ||
            smaller != compact)
            throw new InvalidOperationException("Distributed viewport height or resize idempotence regressed.");
        var now=new DateTime(2026,10,12,4,0,0,DateTimeKind.Utc);
        double left=ReferencePlotTimeX(now.AddSeconds(-60),now,100);
        double mid=ReferencePlotTimeX(now.AddSeconds(-30),now,100);
        double right=ReferencePlotTimeX(now,now,100);
        if (left!=0 || Math.Abs(mid-49)>.01 || Math.Abs(right-98)>.01)
            throw new InvalidOperationException("Reference chart fakes a full minute before samples exist.");
        // Repeated telemetry samples do not change grid geometry; any actual
        // width/height change must still trigger a redraw.
        if (ReferenceGridNeedsResize(400,99,400,99) ||
            !ReferenceGridNeedsResize(401,99,400,99) ||
            !ReferenceGridNeedsResize(400,139,400,99) ||
            !ReferenceGridNeedsResize(400,99,double.NaN,double.NaN))
            throw new InvalidOperationException("Overview grid redraw guard regressed.");
        // A full 30-sample steady history does not need 30 native WinUI
        // PointCollection writes each tick, but a resize forces rendering.
        if (ReferenceNetworkSparkNeedsRedraw(30,30,30,200,33,200,33) ||
            !ReferenceNetworkSparkNeedsRedraw(29,30,30,200,33,200,33) ||
            !ReferenceNetworkSparkNeedsRedraw(30,30,30,201,33,200,33) ||
            !ReferenceNetworkSparkNeedsRedraw(30,30,30,200,43,200,33))
            throw new InvalidOperationException("Network steady sparkline redraw guard regressed.");
        // Disconnected and invalid NIC counters cannot appear as fake traffic.
        if (ReferenceNetworkTotal(8, 3, false) != 0 ||
            ReferenceNetworkTotal(8, 3, true) != 11 ||
            ReferenceNetworkTotal(-5, 2, true) != 2 ||
            ReferenceNetworkTotal(double.NaN, double.PositiveInfinity, true) != 0)
            throw new InvalidOperationException("Overview network traffic sanitization regressed.");
        // Non-finite sensor samples must not produce invalid native chart
        // coordinates, percentages, or segment counts.
        if (ReferenceFiniteCpuPercent(double.NaN)!=0 ||
            ReferenceFiniteCpuPercent(double.PositiveInfinity)!=0 ||
            ReferenceFiniteCpuPercent(double.NegativeInfinity)!=0 ||
            ReferenceFiniteCpuPercent(-30)!=0 ||
            ReferenceFiniteCpuPercent(130)!=100 ||
            Math.Abs(ReferenceFiniteCpuPercent(48.5)-48.5)>.001)
            throw new InvalidOperationException("Overview CPU finite-range policy regressed.");
        if (ReferenceGpuAccent().Color.R<180 ||
            ReferenceDiskAccent().Color.G<170)
            throw new InvalidOperationException("Overview theme reverted to incorrect GPU/disk colors.");
        return "REFERENCE_OVERVIEW_POLICY_SMOKE_OK grid=3x2 cards=6 gpu=magenta disk=green metric=live refresh=2s";
    }
}
