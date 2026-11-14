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

public sealed partial class MainWindow
{
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
}
