using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static double? OverviewFiniteFooterValue(double? value) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(v, 0d, 100d) : null;

    private void UpdateOverviewFooterReadouts(double? cpu, double? gpu, double? ram, double? disk)
    {
        cpu = OverviewFiniteFooterValue(cpu);
        gpu = OverviewFiniteFooterValue(gpu);
        ram = OverviewFiniteFooterValue(ram);
        disk = OverviewFiniteFooterValue(disk);
        if (_overviewCpuFooterText != null || _overviewGpuFooterText != null ||
            _overviewRamFooterText != null || _overviewDiskFooterText != null)
        {
            var key = new OverviewFooterTextKey(
                _overviewCpuFooterText, _overviewGpuFooterText,
                _overviewRamFooterText, _overviewDiskFooterText,
                cpu, gpu, ram, disk);
            if (_overviewLastFooterTextKey != key)
            {
                if (_overviewCpuFooterText != null)
                    SetOverviewTextIfChanged(_overviewCpuFooterText,
                        cpu is { } c ? $"{c:0}% využití" : "Telemetrie CPU čeká…");
                if (_overviewGpuFooterText != null)
                    SetOverviewTextIfChanged(_overviewGpuFooterText,
                        gpu is { } g ? $"{g:0}% využití" : "Telemetrie GPU čeká…");
                if (_overviewRamFooterText != null)
                    SetOverviewTextIfChanged(_overviewRamFooterText,
                        ram is { } r ? $"{r:0}% využití" : "Telemetrie RAM čeká…");
                if (_overviewDiskFooterText != null)
                    SetOverviewTextIfChanged(_overviewDiskFooterText,
                        disk is { } d ? $"{d:0}% aktivita" : "Telemetrie disku čeká…");
                _overviewLastFooterTextKey = key;
            }
        }
        if (_overviewCpuFooterFill != null)
            SetOverviewWidthIfChanged(_overviewCpuFooterFill, 48 * (cpu ?? 0) / 100d);
        if (_overviewGpuFooterFill != null)
            SetOverviewWidthIfChanged(_overviewGpuFooterFill, 48 * (gpu ?? 0) / 100d);
        if (_overviewRamFooterFill != null)
            SetOverviewWidthIfChanged(_overviewRamFooterFill, 48 * (ram ?? 0) / 100d);
        if (_overviewDiskFooterFill != null)
            SetOverviewWidthIfChanged(_overviewDiskFooterFill, 48 * (disk ?? 0) / 100d);
    }

    private void UpdateOverviewUptimeReadout()
    {
        if (_overviewUptimeText == null) return;
        // The visible uptime has MINUTE precision. No new timer is required;
        // keep the same 2-second UI tick and refresh on minute/card changes.
        long minute = Environment.TickCount64 / 60000;
        if (_overviewLastUptimeMinute != minute ||
            !ReferenceEquals(_overviewLastUptimeTextTarget, _overviewUptimeText))
        {
            SetOverviewTextIfChanged(_overviewUptimeText, FormatUptime());
            _overviewLastUptimeMinute = minute;
            _overviewLastUptimeTextTarget = _overviewUptimeText;
        }
    }

    private static void ClearOverviewNetworkPlot(PointCollection? points)
    {
        if (points != null)
            TrimOverviewChartPoints(points, 0);
    }

    private void UpdateOverviewNetworkVisuals(double downMbps, double upMbps, bool online)
    {
        if (_overviewNetworkStatusText != null || _overviewNetworkSpeedText != null)
        {
            var key = new OverviewNetworkStripKey(_overviewNetworkStatusText,
                _overviewNetworkSpeedText, downMbps, upMbps, online);
            if (_overviewLastNetworkStripKey != key)
            {
                if (_overviewNetworkStatusText != null)
                    SetOverviewTextIfChanged(_overviewNetworkStatusText,
                        online ? "Aktivní připojení" : "Síť odpojena");
                if (_overviewNetworkSpeedText != null)
                    SetOverviewTextIfChanged(_overviewNetworkSpeedText,
                        $"↓ {FormatRate(downMbps)}      ↑ {FormatRate(upMbps)}");
                _overviewLastNetworkStripKey = key;
            }
        }

        DateTime now = DateTime.UtcNow;
        if (now < _overviewNextNetworkChartUtc) return;
        _overviewNextNetworkChartUtc = now.AddSeconds(4);

        _overviewNetDownSamples.Enqueue(downMbps);
        _overviewNetUpSamples.Enqueue(upMbps);
        while (_overviewNetDownSamples.Count > 14) _overviewNetDownSamples.Dequeue();
        while (_overviewNetUpSamples.Count > 14) _overviewNetUpSamples.Dequeue();

        if (_overviewNetDownLine?.Parent is Canvas chart) RenderOverviewNetworkChart(chart);
    }

    private void RenderOverviewNetworkChart(Canvas chart)
    {
        if (_overviewNetDownLine == null || _overviewNetUpLine == null) return;
        double width = chart.ActualWidth;
        if (!double.IsFinite(width) || width <= 4) return;
        double max = 1, lastDown = double.NaN, lastUp = double.NaN;
        bool flatDown = true, flatUp = true;
        foreach (double value in _overviewNetDownSamples)
        {
            max = Math.Max(max, value);
            if (!double.IsNaN(lastDown) && lastDown != value) flatDown = false;
            lastDown = value;
        }
        foreach (double value in _overviewNetUpSamples)
        {
            max = Math.Max(max, value);
            if (!double.IsNaN(lastUp) && lastUp != value) flatUp = false;
            lastUp = value;
        }

        int count = Math.Min(_overviewNetDownSamples.Count, _overviewNetUpSamples.Count);
        if (count <= 0)
        {
            _overviewNetworkWasFlat = false;
            TrimOverviewChartPoints(_overviewNetDownLine.Points, 0);
            TrimOverviewChartPoints(_overviewNetUpLine.Points, 0);
            return;
        }

        // Native reads followed by per-point setters are substantially more
        // expensive under rolling data than one Clear/Add batch. The flat
        // path avoids ALL native changes once the same full geometry was drawn.
        bool flat = flatDown && flatUp;
        if (flat && _overviewNetworkWasFlat &&
            _overviewNetworkFlatWidth == width &&
            _overviewNetworkFlatDown == lastDown &&
            _overviewNetworkFlatUp == lastUp &&
            _overviewNetDownLine.Points.Count == count &&
            _overviewNetUpLine.Points.Count == count)
            return;

        var downPoints = _overviewNetDownLine.Points;
        var upPoints = _overviewNetUpLine.Points;
        downPoints.Clear();
        upPoints.Clear();
        double plotWidth = width - 4;
        double xStep = count <= 1 ? 0 : plotWidth / (count - 1);
        using var downEnumerator = _overviewNetDownSamples.GetEnumerator();
        using var upEnumerator = _overviewNetUpSamples.GetEnumerator();
        for (int i = 0; i < count && downEnumerator.MoveNext() && upEnumerator.MoveNext(); i++)
        {
            double x = count <= 1 ? width - 2 : 2 + i * xStep;
            double downY = 44 - Math.Clamp(downEnumerator.Current / max, 0, 1) * 28;
            double upY = 46 - Math.Clamp(upEnumerator.Current / max, 0, 1) * 28;
            downPoints.Add(new Windows.Foundation.Point(x, downY));
            upPoints.Add(new Windows.Foundation.Point(x, upY));
        }
        _overviewNetworkWasFlat = flat;
        _overviewNetworkFlatWidth = width;
        _overviewNetworkFlatDown = lastDown;
        _overviewNetworkFlatUp = lastUp;
    }

    // Detached native WinUI collections: compare old Clear/Add with point-slot
    // reuse, assert identical geometry after steady, rolling, and shrinking
    // samples. This is not a claim about visible FPS.
    internal static string RunOverviewNetworkPointReuseBenchmark()
    {
        const int frames = 360;
        const int maxPoints = 14;
        static (double Ms, long Bytes, int Writes, PointCollection Down, PointCollection Up)
            Sample(bool reuse)
        {
            var down = new PointCollection();
            var up = new PointCollection();
            int writes = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int frame = 0; frame < frames; frame++)
            {
                // Include an unchanged segment, rolling data, then a shorter
                // collection to exercise retained geometry and stale trimming.
                int count = frame >= 320 ? 5 : Math.Min(maxPoints, 1 + frame);
                if (!reuse)
                {
                    down.Clear();
                    up.Clear();
                    writes += 2;
                }
                for (int i = 0; i < count; i++)
                {
                    double x = count == 1 ? 502 : 2 + i * 500d / (count - 1);
                    double yDown = 44 - ((i + (frame / 4)) % 13) * 1.4;
                    double yUp = 46 - ((i * 2 + (frame / 4)) % 13) * 1.2;
                    var nextDown = new Windows.Foundation.Point(x, yDown);
                    var nextUp = new Windows.Foundation.Point(x, yUp);
                    if (reuse)
                    {
                        if (SetOverviewChartPoint(down, i, nextDown)) writes++;
                        if (SetOverviewChartPoint(up, i, nextUp)) writes++;
                    }
                    else
                    {
                        down.Add(nextDown);
                        up.Add(nextUp);
                        writes += 2;
                    }
                }
                if (reuse)
                {
                    int oldDown = down.Count, oldUp = up.Count;
                    TrimOverviewChartPoints(down, count);
                    TrimOverviewChartPoints(up, count);
                    writes += oldDown - down.Count + oldUp - up.Count;
                }
            }
            return (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - before, writes, down, up);
        }
        var baseline = Sample(false);
        var optimized = Sample(true);
        if (baseline.Down.Count != optimized.Down.Count ||
            baseline.Up.Count != optimized.Up.Count ||
            baseline.Down.Count != 5 || baseline.Up.Count != 5)
            throw new InvalidOperationException("Network chart point count regression.");
        for (int i = 0; i < baseline.Down.Count; i++)
        {
            if (baseline.Down[i] != optimized.Down[i] ||
                baseline.Up[i] != optimized.Up[i])
                throw new InvalidOperationException("Network chart geometry regression.");
        }
        if (optimized.Writes >= baseline.Writes)
            throw new InvalidOperationException("Network chart reuse did not reduce collection writes.");
        return $"OVERVIEW_NETWORK_POINT_REUSE_BENCH_OK frames={frames} " +
            $"baselineWrites={baseline.Writes} reuseWrites={optimized.Writes} " +
            $"baselineMs={baseline.Ms:F3} reuseMs={optimized.Ms:F3} " +
            $"baselineAllocatedB={baseline.Bytes} reuseAllocatedB={optimized.Bytes} " +
            "geometry=identical shrink=true";
    }

    private Border BuildOverviewImageHardwareTile(string icon, string title, TextBlock value, string asset, SolidColorBrush accent, string footer)
    {
        value.FontSize = 13.5;
        value.FontWeight = FontWeights.SemiBold;
        value.TextWrapping = TextWrapping.Wrap;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        value.MaxLines = 2;

        var root = new Grid { MinHeight = 128 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8, GridUnitType.Star) });

        var left = new StackPanel
        {
            Spacing = 7,
            Margin = new Thickness(14, 12, 8, 12),
            VerticalAlignment = VerticalAlignment.Center
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        string headerIconTag = title.Contains("Procesor", StringComparison.OrdinalIgnoreCase) ? "system" :
            title.Contains("Grafika", StringComparison.OrdinalIgnoreCase) ? "gpu" :
            title.Contains("Paměť", StringComparison.OrdinalIgnoreCase) ? "components" : "storage";
        SolidColorBrush headerIconAccent = title.Contains("Procesor", StringComparison.OrdinalIgnoreCase) ? CpuMetricBrush() :
            title.Contains("Grafika", StringComparison.OrdinalIgnoreCase) ? GpuHardwareBrush() :
            title.Contains("Paměť", StringComparison.OrdinalIgnoreCase) ? RamMetricBrush() :
            DiskHardwareBrush();
        head.Children.Add(OverviewGlowIcon(headerIconTag, 20, headerIconAccent));
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            MaxWidth = 88
        });
        left.Children.Add(head);
        left.Children.Add(value);

        var usage = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var footerText = new TextBlock
        {
            Text = footer,
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            MaxWidth = 74
        };
        usage.Children.Add(footerText);
        var track = new Border
        {
            Width = 48,
            Height = 5,
            Background = GlassTrackBrush(),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center
        };
        var usageFill = new Border
        {
            Width = title.Contains("Paměť", StringComparison.OrdinalIgnoreCase) ? 40 :
                    title.Contains("Úložiště", StringComparison.OrdinalIgnoreCase) ? 23 : 14,
            Height = 5,
            Background = accent,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        track.Child = usageFill;

        if (title.Contains("Procesor", StringComparison.OrdinalIgnoreCase))
        {
            _overviewCpuFooterText = footerText;
            _overviewCpuFooterFill = usageFill;
        }
        else if (title.Contains("Grafika", StringComparison.OrdinalIgnoreCase))
        {
            _overviewGpuFooterText = footerText;
            _overviewGpuFooterFill = usageFill;
        }
        else if (title.Contains("Paměť", StringComparison.OrdinalIgnoreCase))
        {
            _overviewRamFooterText = footerText;
            _overviewRamFooterFill = usageFill;
        }
        else
        {
            _overviewDiskFooterText = footerText;
            _overviewDiskFooterFill = usageFill;
        }
        usage.Children.Add(track);
        left.Children.Add(usage);
        root.Children.Add(left);

        var visualHost = new Grid();
        visualHost.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/" + asset)),
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
            Opacity = 1
        });
        var visualWash = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, .5),
            EndPoint = new Windows.Foundation.Point(1, .5)
        };
        var washBase = _darkTheme ? new ThemeRgb(9, 12, 30) : _themePalette.SurfaceElevated;
        visualWash.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(_darkTheme ? (byte)150 : (byte)210, washBase.R, washBase.G, washBase.B), Offset = 0 });
        visualWash.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(_darkTheme ? (byte)34 : (byte)72, washBase.R, washBase.G, washBase.B), Offset = .6 });
        visualWash.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, washBase.R, washBase.G, washBase.B), Offset = 1 });
        visualHost.Children.Add(new Border { Background = visualWash });
        Grid.SetColumn(visualHost, 1);
        root.Children.Add(visualHost);

        return new Border
        {
            Background = OverviewCardBackgroundBrush(),
            BorderBrush = OverviewNeonBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = root
        };
    }

    private Border BuildOverviewNetworkCard(TextBlock value)
    {
        value.FontSize = 13;
        value.FontWeight = FontWeights.SemiBold;
        value.MaxLines = 2;

        var root = new StackPanel { Spacing = 5 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(OverviewGlowIcon("network", 22, DiskHardwareBrush()));
        head.Children.Add(new TextBlock { Text = "Síť / Internet", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        root.Children.Add(head);
        root.Children.Add(value);
        _overviewNetworkStatusText = new TextBlock { Text = "Načítám připojení…", FontSize = 10.5, Foreground = TextSecondaryBrush() };
        _overviewNetworkSpeedText = new TextBlock { Text = "↓ 0.00 Mb/s      ↑ 0.00 Mb/s", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = DiskHardwareBrush() };
        root.Children.Add(_overviewNetworkStatusText);
        root.Children.Add(_overviewNetworkSpeedText);

        var chart = new Canvas
        {
            Height = 48, Background = ThemeColor(0,0,0,0), IsHitTestVisible = false,
            Clip = new RectangleGeometry()
        };
        chart.SizeChanged += (_, e) =>
        {
            chart.Clip.Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height));
            RenderOverviewNetworkChart(chart);
        };
        _overviewNetworkWasFlat = false;
        _overviewNetDownLine = new Polyline { Stroke = DiskHardwareBrush(), StrokeThickness = 2.1, StrokeLineJoin = PenLineJoin.Round };
        _overviewNetUpLine = new Polyline { Stroke = RamMetricBrush(), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round };
        chart.Children.Add(_overviewNetDownLine);
        chart.Children.Add(_overviewNetUpLine);
        root.Children.Add(chart);

        var card = BlackCard(root, 190);
        card.Tag = "OverviewLiquidNetwork";
        return card;
    }

    private Border BuildOverviewSystemRichCard(TextBlock os, TextBlock board)
    { return BuildBlackSystemCard(os, board); }

    private Border BuildOverviewUptimeCard(TextBlock uptime)
    { return BuildBlackUptimeCard(uptime); }

    private Border BuildOverviewOptimizerStatusCard(TextBlock activeTweaks, TextBlock uptime)
    {
        activeTweaks.FontSize = 10.75;
        activeTweaks.FontWeight = FontWeights.SemiBold;
        activeTweaks.Foreground = TextPrimaryBrush();
        activeTweaks.TextWrapping = TextWrapping.Wrap;
        activeTweaks.MaxLines = 3;

        uptime.FontSize = 12;
        uptime.FontWeight = FontWeights.SemiBold;
        uptime.Foreground = TextPrimaryBrush();

        var root = new StackPanel { Spacing = 9 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(OverviewGlowIcon("system", 21, RamMetricBrush()));
        head.Children.Add(new TextBlock
        {
            Text = "Stav optimizeru",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        root.Children.Add(head);

        var tweakBox = new Border
        {
            Background = OverviewLiquidInset(),
            BorderBrush = OverviewLiquidEdge(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 8, 10, 8),
            Child = activeTweaks
        };
        root.Children.Add(tweakBox);

        _overviewSystemStatusText = new TextBlock
        {
            Text = _overviewTextRefs?.SystemStatus ?? OverviewSystemLoading,
            TextWrapping = TextWrapping.Wrap, MaxLines = 2,
            FontSize = 10.5,
            Foreground = RamMetricBrush()
        };
        _overviewDriverStatusText = new TextBlock
        {
            Text = _overviewTextRefs?.DriverStatus ?? OverviewDriverLoading,
            TextWrapping = TextWrapping.Wrap, MaxLines = 2,
            FontSize = 10.5,
            Foreground = TextSecondaryBrush()
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_overviewSystemStatusText, "OverviewSystemLoadStatus");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_overviewDriverStatusText, "OverviewDriverLoadStatus");
        root.Children.Add(_overviewSystemStatusText);
        root.Children.Add(_overviewDriverStatusText);

        var uptimeRow = new Grid { ColumnSpacing = 8 };
        uptimeRow.ColumnDefinitions.Add(new ColumnDefinition());
        uptimeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        uptimeRow.Children.Add(new TextBlock
        {
            Text = "Uptime",
            FontSize = 10.5,
            Foreground = TextSecondaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(uptime, 1);
        uptimeRow.Children.Add(uptime);
        root.Children.Add(uptimeRow);

        var card = BlackCard(root, 190);
        card.Tag = "OverviewOptimizerStatus";
        return card;
    }

    private Border BuildOverviewHistoryCard(TextBlock history)
    {
        history.FontSize = 10.5;
        history.FontWeight = FontWeights.SemiBold;

        var root = new StackPanel { Spacing = 5 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(OverviewGlowIcon("fixer", 21, ThemeColor(82, 176, 255)));
        head.Children.Add(new TextBlock { Text = "Changelog", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        root.Children.Add(head);
        // This card belongs to release history, not the hidden tweak-state summary.
        _overviewChangelogCaption = new TextBlock
        {
            Text = "Na\u010d\u00edt\u00e1m changelog\u2026",
            FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = TextSecondaryBrush()
        };
        root.Children.Add(_overviewChangelogCaption);

        _overviewHistoryTimes = new TextBlock[4];
        _overviewHistoryRows = new TextBlock[4];
        for (int i = 0; i < 4; i++)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = i % 2 == 0 ? ThemeColor(80, 169, 255) : ThemeColor(104, 124, 255),
                VerticalAlignment = VerticalAlignment.Center
            });
            var text = new StackPanel { Spacing = 1 };
            var timeText = new TextBlock { Text = "—", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() };
            var rowText = new TextBlock { Text = "Načítám…", FontSize = 10.5, Foreground = TextSecondaryBrush(), TextTrimming = TextTrimming.CharacterEllipsis };
            _overviewHistoryTimes[i] = timeText;
            _overviewHistoryRows[i] = rowText;
            text.Children.Add(timeText);
            text.Children.Add(rowText);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var arrow = new FontIcon { Glyph = "\uE76C", FontSize = 11, Foreground = TextSecondaryBrush(), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(arrow, 2);
            row.Children.Add(arrow);
            root.Children.Add(row);
        }

        var card = BlackCard(root, 190);
        card.Tag = "OverviewLiquidChangelog";
        var content = card.Child;
        card.Child = null;
        var action = new Button
        {
            Content = content, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = ThemeColor(0,0,0,0), BorderBrush = ThemeColor(0,0,0,0),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            UseSystemFocusVisuals = true, IsTabStop = true,
            CornerRadius = card.CornerRadius
        };
        foreach (string state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            action.Resources["ButtonBackground" + state] = ThemeColor(0,0,0,0);
            action.Resources["ButtonBorderBrush" + state] = ThemeColor(0,0,0,0);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(action, "Changelog");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(action, "OverviewChangelogButton");
        action.Click += (_, _) => SafeTaskService.Forget(ShowChangelogPopupAsync(), "overview", "Open changelog popup");
        _overviewChangelogAction = action;
        card.Child = action;
        return card;
    }

    private static string FormatUptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (uptime.TotalDays >= 1) return $"{(int)uptime.TotalDays} d {uptime.Hours} h {uptime.Minutes} min";
        return $"{uptime.Hours} h {uptime.Minutes} min";
    }
}
