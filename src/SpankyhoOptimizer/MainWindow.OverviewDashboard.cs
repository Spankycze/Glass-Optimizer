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
    private sealed class GaugeRefs
    {
        public required TextBlock ValueText { get; init; }
        public required TextBlock SecondaryText { get; init; }
        public required TextBlock MinText { get; init; }
        public required TextBlock AvgText { get; init; }
        public required TextBlock PeakText { get; init; }
        public required TextBlock StatusText { get; init; }
        public required Border StatusBadge { get; init; }
        public required Polyline Sparkline { get; init; }
        public required Ellipse EndDot { get; init; }
        public required Canvas SparkHost { get; init; }
        public RowDefinition? ReferenceSparkRow { get; init; }
        public required Border Fill { get; init; }
        public required FrameworkElement TrackHost { get; init; }
        public bool VerticalLevel { get; init; }
        public bool LocalSparkScale { get; init; }
        public FrameworkElement? ReactiveElement { get; init; }
        public FrameworkElement? ScanElement { get; init; }
        // The fill only needs rebuilding when the underlying sparkline changes.
        public Action<double, bool>? VisualUpdater { get; init; }
        // Reference cards use segmented bars; their legacy Fill is detached.
        public bool SegmentedLevel { get; init; }
        // Clear visible segments/trend on transition to unavailable telemetry.
        public Action? ResetVisibleState { get; init; }
        public Polygon? SparkFill { get; init; }
        public int SparklineRedrawCount { get; set; }
        public System.Collections.Generic.Queue<double> Samples { get; } = new();
        public double CurrentValue { get; set; }
        public int MissingSamples { get; set; }
        // Avoid changing WinUI brushes and re-uploading identical sparkline
        // geometry every 2–4 seconds while values and layout are steady.
        public int LastAppearance { get; set; } = -1;
        public double LastSparkValue { get; set; } = double.NaN;
        public int ConsecutiveSameSamples { get; set; }
        public double LastSparkWidth { get; set; } = double.NaN;
        public double LastSparkHeight { get; set; } = double.NaN;
        public double LastValueLabel { get; set; } = double.NaN;
        public double LastMinLabel { get; set; } = double.NaN;
        public double LastAvgLabel { get; set; } = double.NaN;
        public double LastPeakLabel { get; set; } = double.NaN;
    }

    private sealed class OverviewTextRefs
    {
        public required TextBlock Cpu { get; init; }
        public required TextBlock Gpu { get; init; }
        public required TextBlock Ram { get; init; }
        public required TextBlock Disk { get; init; }
        public required TextBlock Os { get; init; }
        public required TextBlock Board { get; init; }
        public required TextBlock Power { get; init; }
        public required TextBlock Network { get; init; }
        public required TextBlock Driver { get; init; }
        public required TextBlock History { get; init; }
        public required TextBlock Windows { get; init; }
        // Data completion can precede creation of the deferred status card.
        public string SystemStatus { get; set; } = OverviewSystemLoading;
        public string DriverStatus { get; set; } = OverviewDriverLoading;
    }

    private System.Collections.Generic.List<UIElement>? _overviewPageCache;
    private OverviewTextRefs? _overviewTextRefs;
    private Grid? _overviewGaugeHost;
    private Grid? _overviewGaugeQueuedHost;
    private CancellationToken _overviewGaugeQueuedToken;
    private Border? _overviewGaugePanel;
    private Grid? _overviewTelemetryGrid;
    private Grid? _overviewMetricsHost;
    private Grid? _overviewLiveHost;
    private Grid? _overviewCardsQueuedHost;
    private CancellationToken _overviewCardsQueuedToken;
    private bool _overviewCacheCompact;
    private DateTime _overviewPageCacheUsedAt;

    private CancellationTokenSource? _liveMetricsCts;
    private Image? _titleBarLogoImage;
    private GaugeRefs? _cpuGauge;
    private GaugeRefs? _ramGauge;
    private GaugeRefs? _gpuGauge;
    private GaugeRefs? _diskGauge;

    private readonly record struct OverviewPerfSample(DateTime Utc, double? Cpu, double? Gpu, double? Ram, double? Disk);
    private readonly System.Collections.Generic.List<OverviewPerfSample> _overviewPerfHistory = new(2048);
    private DateTime _overviewNextHistoryPruneUtc = DateTime.MinValue;
    private DateTime _overviewChartLastRenderedSampleUtc;
    private DateTime _overviewChartLastRenderedRightEdgeUtc;
    private int _overviewChartLastRenderedRangeMinutes;
    private int _overviewChartLastRenderedFirst = -1;
    private int _overviewChartLastRenderedSourceCount = -1;
    private OverviewSegmentedChart? _overviewCpuChartLine;
    private OverviewSegmentedChart? _overviewGpuChartLine;
    private OverviewSegmentedChart? _overviewRamChartLine;
    private OverviewSegmentedChart? _overviewDiskChartLine;
    private OverviewSegmentedChart? _overviewRamChartFill;

    // A PathFigure per contiguous VALID sensor run. Reuse the WinUI figures and
    // native point collections across renders, so missing readings never draw
    // a misleading line/fill between two distant measurements.
    private sealed class OverviewSegmentedChart
    {
        private readonly bool _fill;
        private const int MaxRetainedFigures = 96;
        private const int MinRetainedFigures = 24;
        private readonly PathGeometry _geometry = new();
        private readonly System.Collections.Generic.List<(PathFigure Figure, PolyLineSegment Line)>
            _pool = new();
        private PolyLineSegment? _current;
        private double _lastX, _lastY, _lastBottom = 78;
        private int _used;
        private int _sparseFrames;
        public Microsoft.UI.Xaml.Shapes.Path Visual { get; }
        public int SegmentCount => _geometry.Figures.Count;
        public PathFigure? GetSegment(int index) =>
            index >= 0 && index < _geometry.Figures.Count ? _geometry.Figures[index] : null;
        public int AllocatedSegments => _pool.Count;

        public OverviewSegmentedChart(Brush? stroke, Brush? fill = null)
        {
            _fill = fill != null;
            Visual = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = _geometry,
                Stroke = stroke,
                Fill = fill,
                StrokeThickness = stroke != null ? 2.2 : 0,
                StrokeLineJoin = PenLineJoin.Round
            };
        }

        public void Reset()
        {
            // Release native path figures after a burst of transient telemetry
            // failures; bounded pooling avoids retaining hundreds per chart.
            _geometry.Figures.Clear();
            if (_pool.Count > MinRetainedFigures && _used * 4 < _pool.Count)
            {
                if (++_sparseFrames >= 12)
                {
                    int keep = Math.Max(MinRetainedFigures, _used * 2);
                    if (keep < _pool.Count)
                        _pool.RemoveRange(keep, _pool.Count - keep);
                    _sparseFrames = 0;
                }
            }
            else _sparseFrames = 0;
            _current = null;
            _used = 0;
        }


        public void Add(double? percent, double x)
        {
            if (percent is not { } value || !double.IsFinite(value))
            {
                FinishRun();
                return;
            }
            AddPoint(x, 78 - Math.Clamp(value, 0, 100) * .68, 78);
        }

        // The large reference CPU chart uses custom height/time coordinates,
        // but can reuse exactly the same bounded native-figure pool.
        public void AddPoint(double x, double y, double bottom)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                FinishRun();
                return;
            }
            if (_current == null)
            {
                (PathFigure figure, PolyLineSegment line) entry;
                if (_used < _pool.Count) entry = _pool[_used];
                else
                {
                    var line = new PolyLineSegment();
                    var figure = new PathFigure { IsClosed = _fill, IsFilled = _fill };
                    figure.Segments.Add(line);
                    entry = (figure, line);
                    if (_pool.Count < MaxRetainedFigures) _pool.Add(entry);
                }
                _used++;
                entry.line.Points.Clear();
                entry.figure.StartPoint = new Windows.Foundation.Point(x, _fill ? bottom : y);
                _geometry.Figures.Add(entry.figure);
                _current = entry.line;
                if (_fill) _current.Points.Add(new Windows.Foundation.Point(x, y));
            }
            else _current.Points.Add(new Windows.Foundation.Point(x, y));
            _lastX = x;
            _lastY = y;
            _lastBottom = bottom;
        }

        public void FinishRun()
        {
            if (_current == null) return;
            if (_fill)
                _current.Points.Add(new Windows.Foundation.Point(_lastX, _lastBottom));
            else if (_current.Points.Count == 0)
                _current.Points.Add(new Windows.Foundation.Point(_lastX + .4, _lastY));
            _current = null;
        }
    }
    private TextBlock[]? _overviewChartXLabels;
    private static readonly string[] OverviewRangeLabels1m = { "60 s", "45 s", "30 s", "15 s", "0 s" };
    private static readonly string[] OverviewRangeLabels5m = { "5 m", "4 m", "3 m", "2 m", "0 m" };
    private static readonly string[] OverviewRangeLabels15m = { "15 m", "11 m", "7 m", "3 m", "0 m" };
    private static readonly string[] OverviewRangeLabels60m = { "60 m", "45 m", "30 m", "15 m", "0 m" };
    private int _overviewRangeMinutes = 1;
    private readonly System.Collections.Generic.List<(Border Pill, string Label, int Minutes)> _overviewRangePills = new();

    private TextBlock? _overviewCpuFooterText;
    private TextBlock? _overviewGpuFooterText;
    private TextBlock? _overviewRamFooterText;
    private TextBlock? _overviewDiskFooterText;
    private Border? _overviewCpuFooterFill;
    private Border? _overviewGpuFooterFill;
    private Border? _overviewRamFooterFill;
    private Border? _overviewDiskFooterFill;
    private TextBlock? _overviewUptimeText;
    // A complete native-target/value key also handles an Overview card rebuild
    // with numerically unchanged sensors; skip text formatting only, never plots.
    private readonly record struct OverviewFooterTextKey(
        TextBlock? CpuText, TextBlock? GpuText, TextBlock? RamText, TextBlock? DiskText,
        double? Cpu, double? Gpu, double? Ram, double? Disk);
    private OverviewFooterTextKey? _overviewLastFooterTextKey;
    private TextBlock? _overviewLastUptimeTextTarget;
    private long _overviewLastUptimeMinute = -1;
    private TextBlock? _overviewNetworkStatusText;
    private TextBlock? _overviewNetworkSpeedText;
    // Preserve the existing chart cadence but avoid rebuilding two labels on
    // steady 2-second readings. Include the native targets in the key so a
    // rebuilt/reattached strip receives its current values on the first tick.
    private readonly record struct OverviewNetworkStripKey(
        TextBlock? Status, TextBlock? Speed, double Down, double Up, bool Online);
    private OverviewNetworkStripKey? _overviewLastNetworkStripKey;
    private Polyline? _overviewNetDownLine;
    private Polyline? _overviewNetUpLine;
    private readonly System.Collections.Generic.Queue<double> _overviewNetDownSamples = new();
    private readonly System.Collections.Generic.Queue<double> _overviewNetUpSamples = new();
    private System.Net.NetworkInformation.NetworkInterface[]? _overviewNetInterfaces;
    private DateTime _overviewNetInterfaceRefreshUtc;
    private string _overviewNetInterfaceIdentity = string.Empty;
    // Only successfully read NICs contribute to totals. Track that membership
    // separately from the 10-second Up-NIC discovery, with no per-tick array.
    private string[]? _overviewMeasuredNicIds;
    private readonly System.Collections.Generic.List<string> _overviewSampleNicIds = new(4);
    private DateTime _overviewNextNetworkChartUtc = DateTime.MinValue;
    // Retain the most recent genuinely flat chart geometry without native writes.
    // The next changed sample, resize or reset uses a single Clear/Add batch.
    private bool _overviewNetworkWasFlat;
    private double _overviewNetworkFlatWidth = double.NaN;
    private double _overviewNetworkFlatDown = double.NaN;
    private double _overviewNetworkFlatUp = double.NaN;
    private long _overviewPrevRxBytes;
    private long _overviewPrevTxBytes;
    private DateTime _overviewPrevNetUtc;

    private TextBlock? _overviewChangelogCaption;
    private TextBlock[]? _overviewHistoryTimes;
    private TextBlock[]? _overviewHistoryRows;
    private TextBlock? _overviewSystemStatusText;
    private TextBlock? _overviewDriverStatusText;
    private TextBlock? _overviewHardwareCpuText;
    private TextBlock? _overviewHardwareGpuText;
    private TextBlock? _overviewHardwareRamText;
    private TextBlock? _overviewHardwareDiskText;
    private string? _overviewActiveTweaksLogSignature;
    private DateTime _overviewActiveTweaksLastLogUtc;

    private System.Collections.Generic.Dictionary<string, bool> GetDetectedTweakStates()
    {
        // ConcurrentDictionary protects its keys, but cached List<TweakDefinition>
        // and Current/IsLoaded are UI-managed mutable objects.
        if (!DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Overview tweak snapshot must be read on the UI dispatcher.");
        var latest = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in _tweakCache.Values)
        {
            foreach (var tweak in list)
            {
                if (!tweak.IsLoaded || tweak.Id.StartsWith("smoke.", StringComparison.OrdinalIgnoreCase))
                    continue;
                latest[tweak.Id] = tweak.Current;
            }
        }

        return latest;
    }

    private (int Loaded, int Active) GetDetectedTweakSummary()
    {
        var latest = GetDetectedTweakStates();
        return (latest.Count, latest.Count(x => x.Value));
    }

    private static System.Collections.Generic.Dictionary<string, bool> MergeActiveTweakStates(
        System.Collections.Generic.IReadOnlyDictionary<string, bool> expected,
        System.Collections.Generic.IReadOnlyDictionary<string, bool> detected)
    {
        var merged = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in expected)
            merged[pair.Key] = pair.Value;
        foreach (var pair in detected)
            merged[pair.Key] = pair.Value;
        return merged;
    }

    private static string BuildOverviewActiveTweakStatusText(
        int activeTweaks,
        int knownTweaks,
        string activeSource,
        DateTimeOffset? lastChange)
    {
        if (knownTweaks == 0)
        {
            return lastChange.HasValue
                ? $"Stav tweaků zatím není načten • poslední změna {lastChange.Value.LocalDateTime:dd.MM. HH:mm}"
                : "Stav tweaků zatím není načten";
        }

        return lastChange.HasValue
            ? $"{activeTweaks} aktivních tweaků • {activeSource} • poslední změna {lastChange.Value.LocalDateTime:dd.MM. HH:mm}"
            : $"{activeTweaks} aktivních tweaků • {activeSource}";
    }

    internal static string RunOverviewActiveTweakMergeSmoke()
    {
        string unknownText = BuildOverviewActiveTweakStatusText(0, 0, "historie", null);
        if (!unknownText.StartsWith("Stav tweaků zatím není načten", StringComparison.Ordinal) ||
            unknownText.StartsWith("0 aktivních", StringComparison.Ordinal))
            throw new InvalidOperationException("Unknown active tweak state is rendered as a false zero.");

        var expected = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = true,
            ["two"] = true,
            ["three"] = false
        };

        var partialInactive = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["three"] = false
        };
        var mergedPartial = MergeActiveTweakStates(expected, partialInactive);
        if (mergedPartial.Count(x => x.Value) != 2)
            throw new InvalidOperationException("Partial detected tweak state collapsed expected active count.");

        var overrideActive = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["two"] = false
        };
        var mergedOverride = MergeActiveTweakStates(expected, overrideActive);
        if (mergedOverride.Count(x => x.Value) != 1)
            throw new InvalidOperationException("Detected tweak state did not override expected history state.");

        return "OVERVIEW_ACTIVE_TWEAK_MERGE_SMOKE_OK unknown=not-loaded partialActive=2 overrideActive=1";
    }

    private long _lastCpuIdle;
    private long _lastCpuKernel;
    private long _lastCpuUser;
    private bool _customTitleBarPrimed;
    private bool _titleContentBuiltSynchronously;

    private static bool IsUiPreviewBuild() => true;

    private Task BuildOverviewDashboardAsync(CancellationToken token = default)
    {
        int generation = _navigationGeneration;
        token.ThrowIfCancellationRequested();
        var phase = System.Diagnostics.Stopwatch.StartNew();
        double layoutWidth = RootGrid?.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth;
        bool compact = layoutWidth < 1200;

        if (_overviewPageCache != null && _overviewTextRefs != null && _overviewCacheCompact == compact)
        {
            Span<long> perElementMs = stackalloc long[3];
            int attachedIndex = 0;
            foreach (var element in _overviewPageCache)
            {
                long elementBeforeMs = phase.ElapsedMilliseconds;
                AttachCachedElement(element, deferHostInvalidation: true);
                if (attachedIndex < perElementMs.Length)
                    perElementMs[attachedIndex] = phase.ElapsedMilliseconds - elementBeforeMs;
                attachedIndex++;
            }
            // Reused cached children each invalidate their own layout slot, while
            // the common hosts are invalidated only once after the complete reattach.
            ContentStack.InvalidateMeasure();
            ContentStack.InvalidateArrange();
            _contentLayerHost?.InvalidateMeasure();
            PageHost?.InvalidateMeasure();
            long elementsMs = phase.ElapsedMilliseconds;
            // Compact (<1200px) spans BOTH the two-column and three-column
            // gauge breakpoints. Cached visuals must follow the live width
            // without allocating six replacement gauges or a new chart.
            ReflowOverviewGaugeColumns(layoutWidth);
            _overviewPageCacheUsedAt = DateTime.UtcNow;
            if (_overviewGaugeHost is { } cachedGaugeHost &&
                _overviewTelemetryGrid is not { Children.Count: 6 })
                ScheduleOverviewGaugeCards(cachedGaugeHost, token);
            var refs = _overviewTextRefs;
            if (_overviewMetricsHost is { Children.Count: 0 } metricsHost &&
                _overviewLiveHost is { } liveHost && liveHost.Children.Count == 0)
                ScheduleOverviewCards(metricsHost, liveHost, refs, compact, token);
            long cardsMs = phase.ElapsedMilliseconds - elementsMs;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "overview") return;
                var cachedSnapshotTask = HardwareSnapshotService.GetOverviewAsync(false, token);
                SafeTaskService.Forget(
                    CompleteOverviewDashboardAsync(cachedSnapshotTask, refs.Cpu, refs.Gpu, refs.Ram, refs.Disk, refs.Os, refs.Board, refs.Power, refs.Network, refs.Driver, refs.History, refs.Windows, token, generation),
                    "overview", "Overview cached data refresh");
            });
            long attachMs = phase.ElapsedMilliseconds;
            if (attachMs >= 15)
                LogService.Info("performance",
                    $"OVERVIEW_CACHE_PHASE totalMs={attachMs} elementsMs={elementsMs} cardsMs={cardsMs} " +
                    $"dispatcherMs={attachMs - elementsMs - cardsMs} element0Ms={perElementMs[0]} element1Ms={perElementMs[1]} element2Ms={perElementMs[2]} count={attachedIndex}");
            if (attachMs >= 5)
                LogService.Info("performance", $"OVERVIEW_CACHE_HIT attachMs={attachMs} compact={compact}");
            return Task.CompletedTask;
        }

        if (_overviewPageCache != null || _overviewTextRefs != null)
            InvalidateOverviewPageCache(forceRelease: true);
        else
            ReleaseOverviewGaugeVisuals();

        var snapshotTask = HardwareSnapshotService.GetOverviewAsync(false, token);
        long snapshotKickoffMs = phase.ElapsedMilliseconds;

        var gauges = new Grid
        {
            Margin = new Thickness(0, 0, 0, 2)
        };
        _overviewGaugeHost = gauges;
        ContentStack.Children.Add(gauges);
        long gaugesMs = phase.ElapsedMilliseconds;

        var metrics = new Grid
        {
            Margin = new Thickness(0),
            MinHeight = 0,
            Visibility = Visibility.Collapsed
        };
        _overviewMetricsHost = metrics;

        var cpuValue = new TextBlock { Text = "Načítám…", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        var gpuValue = new TextBlock { Text = "Načítám…", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        var ramValue = new TextBlock { Text = "Načítám…", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        var diskValue = new TextBlock { Text = "Načítám…", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        var osValue = new TextBlock { Text = "Načítám…", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        var boardValue = new TextBlock { Text = "Načítám…", FontSize = 10.5, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap };
        long primaryRefsMs = phase.ElapsedMilliseconds;

        ContentStack.Children.Add(metrics);
        long metricsMs = phase.ElapsedMilliseconds - gaugesMs;

        var live = new Grid
        {
            MinHeight = 0,
            Margin = new Thickness(0, 0, 0, 2)
        };
        _overviewLiveHost = live;

        var powerValue = new TextBlock { Text = "Načítám…", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        var networkValue = new TextBlock { Text = "Načítám…", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        var driverValue = new TextBlock { Text = "Načítám…", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        var historyValue = new TextBlock { Text = "Načítám…", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        var windowsValue = new TextBlock { Text = "Načítám…", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), TextWrapping = TextWrapping.Wrap };
        long secondaryRefsMs = phase.ElapsedMilliseconds;

        ContentStack.Children.Add(live);
        long liveMs = phase.ElapsedMilliseconds - gaugesMs - metricsMs;

        _overviewTextRefs = new OverviewTextRefs
        {
            Cpu = cpuValue,
            Gpu = gpuValue,
            Ram = ramValue,
            Disk = diskValue,
            Os = osValue,
            Board = boardValue,
            Power = powerValue,
            Network = networkValue,
            Driver = driverValue,
            History = historyValue,
            Windows = windowsValue
        };
        _overviewPageCache = SnapshotChildren(ContentStack);
        _overviewCacheCompact = compact;
        _overviewPageCacheUsedAt = DateTime.UtcNow;

        long cacheMs = phase.ElapsedMilliseconds;
        ScheduleOverviewGaugeCards(gauges, token);
        ScheduleOverviewCards(metrics, live, _overviewTextRefs, compact, token);
        long scheduleMs = phase.ElapsedMilliseconds;

        SafeTaskService.Forget(CompleteOverviewDashboardAsync(snapshotTask, cpuValue, gpuValue, ramValue, diskValue, osValue, boardValue, powerValue, networkValue, driverValue, historyValue, windowsValue, token, generation),
            "overview", "Overview data");
        long forgetMs = phase.ElapsedMilliseconds;
        long overviewUiMs = forgetMs;
        // Avoid synchronous perf logging on the normal Overview cold path.
        // Only unusually slow builds are worth emitting to disk.
        if (overviewUiMs >= 10)
            LogService.Info("performance",
                $"OVERVIEW_UI_PHASE snapshotKickoffMs={snapshotKickoffMs} gaugesMs={gaugesMs - snapshotKickoffMs} primaryRefsMs={primaryRefsMs - gaugesMs} metricsAttachMs={gaugesMs + metricsMs - primaryRefsMs} liveShellMs={gaugesMs + metricsMs + liveMs - (gaugesMs + metricsMs)} secondaryRefsMs={secondaryRefsMs - (gaugesMs + metricsMs)} cacheMs={cacheMs - secondaryRefsMs} scheduleMs={scheduleMs - cacheMs} forgetMs={forgetMs - scheduleMs} totalMs={overviewUiMs} compact={compact}");

        return Task.CompletedTask;
    }

    // The six cached gauges must reflow when the actual column breakpoint
    // changes, even if the wider compact/noncompact page-cache key is unchanged.
    // Reposition existing WinUI children: no new charts, samplers or GPU visuals.
    private bool ReflowOverviewGaugeColumns(double windowWidth)
    {
        if (_section != "overview" || _overviewTelemetryGrid is not { Children.Count: 6 } grid ||
            !double.IsFinite(windowWidth) || windowWidth <= 0)
            return false;

        int columns = ReferenceOverviewColumnCount(windowWidth);
        int rows = 6 / columns;
        if (grid.ColumnDefinitions.Count == columns && grid.RowDefinitions.Count == rows)
            return false;

        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (int column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int row = 0; row < rows; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int index = 0; index < grid.Children.Count; index++)
        {
            var gauge = (FrameworkElement)grid.Children[index];
            Grid.SetColumn(gauge, index % columns);
            Grid.SetRow(gauge, index / columns);
        }
        // The row count changed, so viewport fitting needs fresh row heights.
        _referenceBaseGaugeRowHeights = null;
        return true;
    }

    private void ScheduleOverviewGaugeCards(Grid host, CancellationToken token)
    {
        int generation = _navigationGeneration;
        // A canceled visit can leave the chart shell with only some gauges.
        // Resume the missing gauges when the cached Overview is revisited;
        // do not rebuild the chart or duplicate existing native visuals.
        if (host.Children.Count > 0 && _overviewTelemetryGrid is { Children.Count: 6 }) return;
        if (ReferenceEquals(_overviewGaugeQueuedHost, host) && _overviewGaugeQueuedToken == token) return;
        _overviewGaugeQueuedHost = host;
        _overviewGaugeQueuedToken = token;
        if (host.Children.Count > 0)
        {
            if (_overviewTelemetryGrid is { Children.Count: < 6 })
                QueueReferenceOverviewCardBatch(host, token, generation,
                    _overviewTelemetryGrid.ColumnDefinitions.Count,
                    System.Diagnostics.Stopwatch.StartNew());
            else
                _overviewGaugeQueuedHost = null;
            return;
        }

        bool queued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "overview" ||
                !ReferenceEquals(_overviewGaugeQueuedHost, host) || _overviewGaugeQueuedToken != token ||
                !ReferenceEquals(_overviewGaugeHost, host) || host.Parent == null || host.Children.Count > 0)
            {
                if (ReferenceEquals(_overviewGaugeQueuedHost, host) && _overviewGaugeQueuedToken == token)
                    _overviewGaugeQueuedHost = null;
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Reference composition: wide 60-second chart followed by six compact
            // gauges in two rows of three. Reuse existing live CPU/RAM/GPU/disk
            // workers; only native WinUI shapes, no bitmap mockups.
            var shell = new StackPanel { Spacing = 8 };
            var mainChart = BuildReferenceMainPerformanceChart();
            shell.Children.Add(mainChart);
            _overviewTelemetryGrid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
            double windowWidth = RootGrid.ActualWidth > 1 ?
                RootGrid.ActualWidth : StartupClientWidth;
            int columns = ReferenceOverviewColumnCount(windowWidth);
            for (int i=0;i<columns;i++)
                _overviewTelemetryGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i=0;i<6/columns;i++)
                _overviewTelemetryGrid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            shell.Children.Add(_overviewTelemetryGrid);

            var panel = new Border {
                Background = ThemeColor(0,0,0,0), BorderThickness = new Thickness(0),
                Padding = new Thickness(0), Child = shell };
            _overviewGaugePanel = panel;
            host.Children.Add(panel);
            if (sw.ElapsedMilliseconds >= 8)
                LogService.Info("performance", $"OVERVIEW_REFERENCE_STAGE stage=shell ms={sw.ElapsedMilliseconds}");

            // Two cards per dispatcher turn avoid seven chained callbacks.
            // Keep each batch bounded so the first frame and user input remain responsive.
            QueueReferenceOverviewCardBatch(host, token, generation, columns, sw);
        });
        if (!queued && ReferenceEquals(_overviewGaugeQueuedHost, host) &&
            _overviewGaugeQueuedToken == token)
            _overviewGaugeQueuedHost = null;
    }

    private void QueueReferenceOverviewCardBatch(
        Grid host, CancellationToken token, int generation, int columns,
        System.Diagnostics.Stopwatch sw)
    {
        bool queued = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "overview" || !ReferenceEquals(_overviewGaugeQueuedHost, host) ||
                _overviewGaugeQueuedToken != token ||
                !ReferenceEquals(_overviewGaugeHost, host) || host.Parent == null ||
                _overviewTelemetryGrid is not { } grid)
                return;

            int start = grid.Children.Count;
            int end = Math.Min(start + 2, 6);
            var batchTiming = System.Diagnostics.Stopwatch.StartNew();
            for (int index = start; index < end; index++)
            {
                var card = index switch
                {
                    0 => BuildReferenceGauge("CPU", ReferenceCpuAccent(), out _cpuGauge),
                    1 => BuildReferenceGauge("RAM", ReferenceRamAccent(), out _ramGauge),
                    2 => BuildReferenceGauge("GPU", ReferenceGpuAccent(), out _gpuGauge),
                    3 => BuildReferenceGauge("Úložiště", ReferenceDiskAccent(), out _diskGauge),
                    4 => BuildReferenceNetworkCard(),
                    _ => BuildReferenceTemperatureCard()
                };
                Grid.SetColumn(card, index % columns);
                Grid.SetRow(card, index / columns);
                grid.Children.Add(card);
                if (_overviewTextRefs is { } textRefs)
                {
                    var gauge = index switch
                    {
                        0 => _cpuGauge, 1 => _ramGauge,
                        2 => _gpuGauge, 3 => _diskGauge, _ => null
                    };
                    string detail = index switch
                    {
                        0 => textRefs.Cpu.Text, 1 => textRefs.Ram.Text,
                        2 => textRefs.Gpu.Text, 3 => textRefs.Disk.Text, _ => ""
                    };
                    if (gauge != null && detail != "Načítám…")
                        SetOverviewTextIfChanged(gauge.SecondaryText, detail);
                }
            }

            // Normal two-card batches cost 3–6ms; avoid a log write for
            // each frame-sized dispatcher callback unless it exceeds 8ms.
            long batchMs = batchTiming.ElapsedMilliseconds;
            if (batchMs >= 8)
                LogService.Info("performance",
                    $"OVERVIEW_GAUGE_BATCH start={start} cards={end-start} ms={batchMs}");
            if (end < 6)
                QueueReferenceOverviewCardBatch(host, token, generation, columns, sw);
            else
            {
                _overviewGaugeQueuedHost = null;
                // A resize can happen while the three two-card batches run.
                // The final batch must honor the latest window width.
                ReflowOverviewGaugeColumns(RootGrid.ActualWidth);
                SchedulePageScrollPolicyRefresh();
                LogService.Info("performance",
                    $"OVERVIEW_REFERENCE_STAGE stage=complete cards=6 chart=true ms={sw.ElapsedMilliseconds}");
            }
        });
        if (!queued && ReferenceEquals(_overviewGaugeQueuedHost, host) &&
            _overviewGaugeQueuedToken == token)
            _overviewGaugeQueuedHost = null;
    }

    private void ScheduleOverviewCards(
        Grid metricsHost,
        Grid liveHost,
        OverviewTextRefs refs,
        bool compact,
        CancellationToken token)
    {
        int generation = _navigationGeneration;
        if ((liveHost.Children.Count > 0 || metricsHost.Children.Count > 0) ||
            (ReferenceEquals(_overviewCardsQueuedHost, metricsHost) && _overviewCardsQueuedToken == token))
            return;

        _overviewCardsQueuedHost = metricsHost;
        _overviewCardsQueuedToken = token;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "overview" ||
                !ReferenceEquals(_overviewMetricsHost, metricsHost) ||
                !ReferenceEquals(_overviewLiveHost, liveHost) || metricsHost.Parent == null || liveHost.Parent == null ||
                metricsHost.Children.Count > 0 || liveHost.Children.Count > 0)
            {
                if ((ReferenceEquals(_overviewCardsQueuedHost, metricsHost) && _overviewCardsQueuedToken == token))
                    _overviewCardsQueuedHost = null;
                return;
            }

            var metricsSw = System.Diagnostics.Stopwatch.StartNew();
            // Hardware identity is already shown directly inside the four live telemetry cards.
            // Keep this legacy host empty/collapsed so Overview remains a single-screen dashboard.
            long metricsStageMs = metricsSw.ElapsedMilliseconds;
            LogService.Info("performance", $"OVERVIEW_CARDS_STAGE stage=metrics ms={metricsStageMs} compact={compact}");

            {
                if (token.IsCancellationRequested || _section != "overview" ||
                    !ReferenceEquals(_overviewMetricsHost, metricsHost) ||
                    !ReferenceEquals(_overviewLiveHost, liveHost) ||
                    liveHost.Children.Count > 0)
                {
                    if ((ReferenceEquals(_overviewCardsQueuedHost, metricsHost) && _overviewCardsQueuedToken == token))
                        _overviewCardsQueuedHost = null;
                    return;
                }

                var liveSw = System.Diagnostics.Stopwatch.StartNew();
                var uptimeValue = new TextBlock
                {
                    Text = FormatUptime(),
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    Foreground = TextPrimaryBrush(),
                    TextWrapping = TextWrapping.NoWrap,
                    MaxLines = 1
                };
                _overviewUptimeText = uptimeValue;

                // No second network meter. System/status/changelog are now
                // real, permanently visible cards INSIDE the Overview page.
                _overviewInfoCardsGrid=BuildReferenceInlineSystemCards(refs,uptimeValue);
                liveHost.Children.Add(_overviewInfoCardsGrid);
                SchedulePageScrollPolicyRefresh();
                SafeTaskService.Forget(RefreshOverviewChangelogRowsAsync(token),
                    "overview","Overview inline changelog refresh");
                LogService.Info("visual-qa",
                    "OVERVIEW_INLINE_INFO_READY uniqueCards=3 networkDuplicate=0 flyout=false");
                if ((ReferenceEquals(_overviewCardsQueuedHost, metricsHost) && _overviewCardsQueuedToken == token))
                    _overviewCardsQueuedHost = null;
                long liveStageMs = liveSw.ElapsedMilliseconds;
                LogService.Info("performance", $"OVERVIEW_CARDS_STAGE stage=live ms={liveStageMs} compact={compact}");
                LogService.Info("performance", $"OVERVIEW_CARDS_DEFERRED staged=true metricsMs={metricsStageMs} liveMs={liveStageMs} compact={compact}");
            }
        });
    }

    private void InvalidateOverviewPageCache(bool forceRelease = false)
    {
        _overviewPageCache = null;
        _overviewTextRefs = null;
        _latestOverviewRamSample = null;
        _overviewGaugeHost = null;
        _overviewGaugeQueuedHost = null;
        _overviewGaugePanel = null;
        _overviewTelemetryGrid = null;
        _overviewMetricsHost = null;
        _overviewLiveHost = null;
        _overviewInfoFlyout = null;
        _overviewInfoCardsGrid = null;
        _overviewInfoButton = null;
        _overviewChangelogAction = null;
        _overviewCardsQueuedHost = null;
        _overviewPageCacheUsedAt = default;
        _overviewCacheCompact = false;

        _referenceCpuGlow?.Reset();
        _referenceCpuPlot?.Reset();
        _referenceCpuArea?.Reset();
        _referenceCpuGlow = null;
        _referenceVerticalGridLines.Clear();
        _referenceCpuPlot = null;
        _referenceCpuArea = null;
        _referenceCpuPlotHost = null;
        _referencePlotYTickLabels = null;
        _referencePlotYTicksHost = null;
        _referenceNetworkPlotHost = null;
        _referenceTemperatureRoot = null;
        _overviewInlineSecondHistory = null;
        _referenceBaseGaugeRowHeights = null;
        _referenceChartCpu = _referenceChartRam = _referenceChartGpu =
            _referenceChartDisk = _referenceChartNetwork = null;
        _referenceLeftUsage = null;
        _referenceNetworkValue = _referenceNetworkDetail = _referenceNetworkFoot = null;
        _referenceNetworkSpark = null;
        _referenceNetworkBlocks = null;
        _referenceNetworkLastLit = -1;
        _referenceNetworkHistory.Clear();
        _referenceTempBlocks = null;
        _referenceCpuTemperature = _referenceGpuTemperature = _referenceBoardTemperature = null;
        _referencePlotGridLines.Clear();
        _referenceLastPlotUtc = default;
        _referenceSensorCpu = _referenceSensorGpu = _referenceCpuClockMhz = _referenceSensorBoard = null;
        _referenceSensorFanRpm = null;
        _referenceLastRenderedTemperature = null;
        _referenceSensorStatusText = _referenceFanTelemetryText = null;
        _referenceLeftBlocks = null;
        _referenceLeftLastLit = -1;

        _overviewCpuChartLine?.Reset();
        _overviewGpuChartLine?.Reset();
        _overviewRamChartLine?.Reset();
        _overviewDiskChartLine?.Reset();
        _overviewRamChartFill?.Reset();
        _overviewCpuChartLine = null;
        _overviewGpuChartLine = null;
        _overviewRamChartLine = null;
        _overviewDiskChartLine = null;
        _overviewRamChartFill = null;
        _overviewChartXLabels = null;
        _overviewRangePills.Clear();
        _overviewCpuFooterText = _overviewGpuFooterText = _overviewRamFooterText = _overviewDiskFooterText = null;
        _overviewCpuFooterFill = _overviewGpuFooterFill = _overviewRamFooterFill = _overviewDiskFooterFill = null;
        _overviewUptimeText = null;
        _overviewLastFooterTextKey = null;
        _overviewLastUptimeTextTarget = null;
        _overviewLastUptimeMinute = -1;
        _overviewNetworkStatusText = null;
        _overviewNetworkSpeedText = null;
        _overviewLastNetworkStripKey = null;
        _overviewNetDownLine = null;
        _overviewNetUpLine = null;
        _overviewNetworkWasFlat = false;
        _overviewChangelogCaption = null;
        _overviewHistoryTimes = null;
        _overviewHistoryRows = null;
        _overviewSystemStatusText = null;
        _overviewDriverStatusText = null;
        _overviewHardwareCpuText = null;
        _overviewHardwareGpuText = null;
        _overviewHardwareRamText = null;
        _overviewHardwareDiskText = null;
        if (forceRelease || _section != "overview")
            ReleaseOverviewGaugeVisuals();
    }

    internal async Task RunOverviewCacheSmokeAsync()
    {
        InvalidateOverviewPageCache(forceRelease: true);
        await ShowSectionAsync("overview");
        await ShowSectionAsync("settings");
        await ShowSectionAsync("overview"); // Canceled partial gauges must resume on cached return.
        await ShowSectionAsync("overview"); // A repeated visit must never schedule duplicate gauges.
        for (int i = 0; i < 40 && (_cpuGauge == null ||
             _overviewTelemetryGrid is not { Children.Count: 6 } ||
             _overviewInfoCardsGrid is not { Children.Count: 3 }); i++)
            await Task.Delay(25);
        if (_overviewPageCache == null || _overviewPageCache.Count == 0 || _overviewTextRefs == null || _cpuGauge == null ||
            _overviewMetricsHost == null || _overviewMetricsHost.Visibility != Visibility.Collapsed ||
            _overviewLiveHost is not { Children.Count: 1 } || _overviewInfoCardsGrid is not { Children.Count: 3 })
            throw new InvalidOperationException("Overview cache/deferred cards were not captured after first render.");

        VerifyOverviewLiquidCards();
        if (_overviewTelemetryGrid is not { Children.Count: 6 } || _referenceCpuPlotHost == null)
            throw new InvalidOperationException("Reference Overview must have chart and six live cards.");
        // Both column breakpoints are inside compact mode. Reflow a *live*
        // cached grid without recreating any of the six gauge UI elements.
        var responsiveGrid = _overviewTelemetryGrid;
        var retainedGauges = responsiveGrid.Children.ToArray();
        ReflowOverviewGaugeColumns(1304);
        if (!ReflowOverviewGaugeColumns(920) ||
            responsiveGrid.ColumnDefinitions.Count != 2 || responsiveGrid.RowDefinitions.Count != 3 ||
            ReflowOverviewGaugeColumns(920))
            throw new InvalidOperationException("Overview did not enter or retain its two-column cached layout.");
        for (int i = 0; i < retainedGauges.Length; i++)
            if (!ReferenceEquals(responsiveGrid.Children[i], retainedGauges[i]) ||
                Grid.GetColumn((FrameworkElement)retainedGauges[i]) != i % 2 || Grid.GetRow((FrameworkElement)retainedGauges[i]) != i / 2)
                throw new InvalidOperationException("Two-column reflow replaced or misplaced live gauges.");
        if (!ReflowOverviewGaugeColumns(1304) ||
            responsiveGrid.ColumnDefinitions.Count != 3 || responsiveGrid.RowDefinitions.Count != 2)
            throw new InvalidOperationException("Overview did not restore its three-column cached layout.");
        for (int i = 0; i < retainedGauges.Length; i++)
            if (!ReferenceEquals(responsiveGrid.Children[i], retainedGauges[i]) ||
                Grid.GetColumn((FrameworkElement)retainedGauges[i]) != i % 3 || Grid.GetRow((FrameworkElement)retainedGauges[i]) != i / 3)
                throw new InvalidOperationException("Three-column reflow replaced or misplaced live gauges.");
        ReflowOverviewGaugeColumns(RootGrid.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth);
        SchedulePageScrollPolicyRefresh();
        LogService.Info("performance", "OVERVIEW_RESPONSIVE_CACHE_SMOKE_OK columns=2,3 retainedGauges=6 rebuilt=0");
        // The user's current compact Overview has one reference chart and one
        // network card, not duplicate detached legacy charts. Keep legacy
        // renderers available for other callers without running them live.
        if (_overviewCpuChartLine != null || _overviewNetDownLine != null ||
            _overviewNetUpLine != null || _overviewNetworkStatusText != null ||
            _overviewNetworkSpeedText != null)
            throw new InvalidOperationException(
                "Compact Overview unexpectedly materialized duplicate legacy chart/network visuals.");
        LogService.Info("performance", "OVERVIEW_LEGACY_HIDDEN_SKIP_SMOKE_OK performance=true network=true");
        // Stroke and glow must own distinct native PathGeometry/PointCollections.
        // Sharing WinUI geometry previously caused native failures.
        if (_referenceCpuPlot == null || _referenceCpuGlow == null ||
            ReferenceEquals(_referenceCpuPlot.Visual.Data, _referenceCpuGlow.Visual.Data))
            throw new InvalidOperationException("Reference plot strokes must own independent native geometries.");
        LogService.Info("regression", "REFERENCE_PLOT_INDEPENDENT_STROKES_OK");
        var firstRoot = _overviewPageCache[0];
        var firstCpuGauge = _cpuGauge;
        await ShowSectionAsync("settings");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("overview");
        long attachMs = sw.ElapsedMilliseconds;

        if (_overviewPageCache == null || _overviewTextRefs == null)
            throw new InvalidOperationException("Overview cache disappeared before reuse.");
        if (!ReferenceEquals(_overviewPageCache[0], firstRoot))
            throw new InvalidOperationException("Overview cached UI tree was rebuilt instead of reused.");
        if (!ReferenceEquals(_cpuGauge, firstCpuGauge))
            throw new InvalidOperationException("Overview gauge composition objects were rebuilt instead of reused.");
        if (!ContentStack.Children.Cast<UIElement>().Any(x => ReferenceEquals(x, firstRoot)))
            throw new InvalidOperationException("Overview cached root was not reattached.");
        if (attachMs > 25)
        {
            // A single scheduler/GC spike must not fail the release. Preserve the
            // 25 ms limit, but require the median of three measured cache hits to pass.
            long[] samples = { attachMs, 0, 0 };
            for (int sample = 1; sample < samples.Length; sample++)
            {
                await ShowSectionAsync("settings");
                sw.Restart();
                await ShowSectionAsync("overview");
                samples[sample] = sw.ElapsedMilliseconds;
            }
            Array.Sort(samples);
            LogService.Info("performance", $"OVERVIEW_CACHE_LATENCY_RECHECK medianMs={samples[1]} minMs={samples[0]} maxMs={samples[2]}");
            if (samples[1] > 25)
                throw new InvalidOperationException($"Overview cached navigation persistently slow: median {samples[1]} ms.");
        }

        VerifyOverviewInteractionSmoke();
        // Exercise the exact card the user sees, not just the reducer. These
        // values are injected only in the isolated release UI smoke process.
        UpdateReferenceTemperature(58, 46, 40, 1590);
        if (_referenceCpuTemperature?.Text != "58 °C" ||
            _referenceGpuTemperature?.Text != "46 °C" ||
            _referenceBoardTemperature?.Text != "40 °C" ||
            _referenceFanTelemetryText?.Text?.Contains("1590", StringComparison.Ordinal) != true ||
            _referenceSensorStatusText?.Text != "● Senzory: 3/3 dostupné")
            throw new InvalidOperationException("Temperature/board/fan Overview card is not populated.");
        // Same-state refresh must not replace any UI contents; a changed GPU
        // reading must update despite the retained visual-state shortcut.
        UpdateReferenceTemperature(58, 46, 40, 1590);
        UpdateReferenceTemperature(58, 47, 40, 1590);
        if (_referenceGpuTemperature?.Text != "47 °C")
            throw new InvalidOperationException("Temperature card ignored a changed GPU sensor.");
        UpdateReferenceTemperature(58, null, null, null);
        if (_referenceGpuTemperature?.Text != "—" ||
            _referenceBoardTemperature?.Text != "—" ||
            _referenceSensorStatusText?.Text != "● Senzory: 1/3 dostupné")
            throw new InvalidOperationException("Temperature card retained stale unavailable sensors.");
        UpdateReferenceTemperature(_referenceSensorCpu, _referenceSensorGpu,
            _referenceSensorBoard, _referenceSensorFanRpm);
        if (_overviewLiveHost is not { Children.Count: 1 } ||
            _overviewInfoCardsGrid is not { Children.Count: 3 } ||
            _overviewInfoFlyout != null)
            throw new InvalidOperationException(
                "System / Optimizer / Changelog must remain inside Overview, not a popup.");
        LogService.Info("performance", RunOverviewChartPointReuseSmoke());
        LogService.Info("performance", RunOverviewChartRenderPolicySmoke());
        LogService.Info("performance", RunOverviewHistoryEnvelopeSmoke());
        LogService.Info("performance", RunOverviewHistoryBatchParitySmoke());
        LogService.Info("performance", RunOverviewSegmentedChartSmoke());
        LogService.Info("performance", RunOverviewNativeFigureRetentionSmoke());
        LogService.Info("performance", RunOverviewOptionalTelemetrySmoke());
        LogService.Info("performance", RunOverviewRamValiditySmoke());
        LogService.Info("performance", RunOverviewHistoryLookupBenchmark());
        LogService.Info("performance", RunReferenceOverviewPolicySmoke());
        LogService.Info("performance", RunReferenceCpuGapSmoke());
        LogService.Info("performance", RunChangelogPagingPolicySmoke());
        LogService.Info("performance", RunOverviewPointCollectionBenchmark());
        LogService.Info("performance", RunOverviewNetworkPointReuseBenchmark());
        LogService.Info("performance", RunOverviewNetworkBaselineSmoke());
        LogService.Info("performance", RunOverviewNetworkContinuitySmoke());
        LogService.Info("performance", RunReferenceNetworkSparkReuseBenchmark());
        LogService.Info("performance", $"OVERVIEW_CACHE_SMOKE_OK attachMs={attachMs} elements={_overviewPageCache.Count}");
    }

    internal async Task RunStartupOverviewVisualSmokeAsync()
    {
        for (int i = 0; i < 80 &&
             (!_initialSectionNavigationCompleted || _section != "overview" ||
              _overviewPageCache == null || _overviewPageCache.Count == 0 ||
              _overviewGaugePanel == null || _overviewTelemetryGrid is not { Children.Count: 6 } ||
              _overviewMetricsHost == null ||
              _overviewLiveHost is not { Children.Count: 1 } || _overviewInfoCardsGrid is not { Children.Count: 3 } ||
              _cpuGauge == null); i++)
            await Task.Delay(25);

        if (_section != "overview")
            throw new InvalidOperationException($"Startup section is '{_section}', expected overview.");
        if (!_initialSectionNavigationCompleted)
            throw new InvalidOperationException("Initial Overview navigation did not complete.");
        if (_overviewPageCache == null || _overviewPageCache.Count == 0)
            throw new InvalidOperationException("Overview startup root is missing.");
        if (_overviewGaugePanel == null || _overviewTelemetryGrid is not { Children.Count: 6 } ||
            _overviewLiveHost is not { Children.Count: 1 } || _overviewInfoCardsGrid is not { Children.Count: 3 } ||
            _cpuGauge == null)
            throw new InvalidOperationException("Overview startup cards/gauges are missing.");
        if (_overviewMetricsHost == null || _overviewMetricsHost.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Overview legacy hardware host should stay collapsed.");
        if (_overviewSystemStatusText == null || _overviewDriverStatusText == null)
            throw new InvalidOperationException("Overview optimizer status card was not created.");
        if (_overviewInfoFlyout != null || _overviewInfoButton != null ||
            _overviewLiveHost is not { Children.Count: 1 } ||
            _overviewInfoCardsGrid is not { Children.Count: 3 } ||
            !_overviewInfoCardsGrid.IsLoaded)
            throw new InvalidOperationException(
                "Three permanent inline cards must be visible with no info Flyout.");
        if (!ContentStack.Children.Cast<UIElement>().Any(x => _overviewPageCache.Contains(x)))
            throw new InvalidOperationException("Overview startup root is not attached to ContentStack.");

        await Task.Delay(75);
        if (!ContentStack.IsLoaded || ContentStack.Opacity < 0.99 ||
            ContentStack.ActualWidth < 100 || ContentStack.ActualHeight < 100)
            throw new InvalidOperationException($"Overview ContentStack not visible/laid out: loaded={ContentStack.IsLoaded} opacity={ContentStack.Opacity:0.##} size={ContentStack.ActualWidth:0.#}x{ContentStack.ActualHeight:0.#}.");
        if (_contentLayerHost == null || !_contentLayerHost.IsLoaded || _contentLayerHost.Opacity < 0.99 ||
            _contentLayerHost.ActualWidth < 100 || _contentLayerHost.ActualHeight < 100)
            throw new InvalidOperationException($"Overview content host not visible/laid out: loaded={_contentLayerHost?.IsLoaded} opacity={_contentLayerHost?.Opacity:0.##} size={_contentLayerHost?.ActualWidth:0.#}x{_contentLayerHost?.ActualHeight:0.#}.");
        if (_pageScroll == null || !_pageScroll.IsLoaded || _pageScroll.Opacity < 0.99 ||
            _pageScroll.ActualWidth < 100 || _pageScroll.ActualHeight < 100)
            throw new InvalidOperationException($"Overview page scroll not visible/laid out: loaded={_pageScroll?.IsLoaded} opacity={_pageScroll?.Opacity:0.##} size={_pageScroll?.ActualWidth:0.#}x{_pageScroll?.ActualHeight:0.#}.");
        for (int i = 0; i < 20 && (!_heroDeferredCompleted || _heroImageFrame?.Child is not Grid); i++)
            await Task.Delay(25);
        if (!_heroDeferredCompleted || _heroImageFrame?.Child is not Grid ||
            HeaderCard.Height < 100 || _heroImageFrame is not { Height: >= 100 })
            throw new InvalidOperationException($"Startup hero not configured: deferred={_heroDeferredCompleted} source={_heroBackgroundImage?.Source != null} headerH={HeaderCard.Height:0.#} frameH={_heroImageFrame?.Height:0.#}.");

        VerifyGlassBackdropLifecycle();

        double clientWidth = RootGrid.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth;
        double expectedHeroHeight = CalculateSectionHeroHeight("overview", clientWidth);
        double expectedFrameHeight = expectedHeroHeight;
        var expectedHeroMargin = new Thickness(HeroFrameLeft, 0, HeroFrameRight, 0);
        double subNavHeight = SubNavShell.Visibility == Visibility.Visible ? Math.Max(0, SubNavShell.ActualHeight) : 0;
        double expectedPageTop = Math.Max(0, expectedHeroHeight + subNavHeight - OverviewPageLift);

        if (Math.Abs(HeaderCard.Height - expectedHeroHeight) > 1 ||
            _heroImageFrame == null || Math.Abs(_heroImageFrame.Height - expectedFrameHeight) > 1 ||
            !_heroImageFrame.Margin.Equals(expectedHeroMargin) ||
            Math.Abs((_pageScroll?.Margin.Top ?? -1) - expectedPageTop) > 1)
            throw new InvalidOperationException(
                $"Overview shell geometry invariant failed: header={HeaderCard.Height:0.#}/{expectedHeroHeight:0.#} frame={_heroImageFrame?.Height:0.#}/{expectedFrameHeight:0.#} pageTop={_pageScroll?.Margin.Top:0.#}/{expectedPageTop:0.#}.");

        if (_overviewGaugePanel == null || _overviewTelemetryGrid == null)
            throw new InvalidOperationException("Overview gauge geometry refs are missing.");
        if (_overviewGaugePanel.Child is not StackPanel gaugeRoot || !gaugeRoot.Margin.Equals(new Thickness(0)))
            throw new InvalidOperationException("Overview gauge root gained an unexpected offset.");
        if (!_overviewGaugePanel.Padding.Equals(new Thickness(0)))
            throw new InvalidOperationException($"Overview gauge panel padding changed: {_overviewGaugePanel.Padding}.");

        double hostExcess = (_overviewGaugeHost?.ActualHeight ?? 0) - _overviewGaugePanel.ActualHeight;
        if (hostExcess > 3)
            throw new InvalidOperationException(
                $"Overview gauge host whitespace regression: host={_overviewGaugeHost?.ActualHeight:0.#} panel={_overviewGaugePanel.ActualHeight:0.#} excess={hostExcess:0.#}.");

        double headerHeight = HeaderCard.Height;
        double pageTop = _pageScroll?.Margin.Top ?? -1;
        int contentCount = ContentStack.Children.Count;
        await Task.Delay(150);
        if (ContentStack.Children.Count != contentCount)
            throw new InvalidOperationException("Overview startup content tree changed after first render.");
        if (Math.Abs(HeaderCard.Height - headerHeight) > 0.5 ||
            Math.Abs((_pageScroll?.Margin.Top ?? -1) - pageTop) > 0.5)
            throw new InvalidOperationException("Overview/header geometry shifted after first render.");

        // Validate the actual native controls under a taller simulated viewport,
        // then restore the real viewport before the final no-scroll check.
        // Pure release smoke only; no Windows settings or user window changes.
        if (_referenceCpuPlotHost != null && _cpuGauge != null &&
            _overviewInfoCardsGrid != null && _referenceNetworkPlotHost != null)
        {
            double available = _pageScroll.ViewportHeight;
            double actualContent = MeasurePageContentExtentHeight();
            if (available >= 590 && actualContent > 0)
            {
                double simulated = actualContent;
                for (int pass = 0; pass < 3 &&
                     FitReferenceOverviewToViewport(available + 100, simulated); pass++)
                {
                    PageHost.UpdateLayout();
                    simulated = MeasurePageContentExtentHeight();
                }
                double expandedChart = _referenceCpuPlotHost.Height;
                double expandedGauge = _cpuGauge.SparkHost.Height;
                double expandedInfo = _overviewInfoCardsGrid.MinHeight;
                if (expandedChart < ReferenceBasePlotHeight + 30 ||
                    expandedGauge < ReferenceBaseSparkHeight + 15 ||
                    expandedInfo < ReferenceBaseInfoHeight + 10 ||
                    Math.Abs(simulated - actualContent - 100) > 10)
                    throw new InvalidOperationException(
                        $"Overview tall-window distribution failed: chart={expandedChart:0.#}, gauge={expandedGauge:0.#}, info={expandedInfo:0.#}, growth={simulated-actualContent:0.#}.");
                for (int pass = 0; pass < 3 &&
                     FitReferenceOverviewToViewport(available, simulated); pass++)
                {
                    PageHost.UpdateLayout();
                    simulated = MeasurePageContentExtentHeight();
                }
            }
        }
        ApplyPageScrollPolicyNow();
        double fitViewport = _pageScroll.ViewportHeight;
        double fitContent = MeasurePageContentExtentHeight();
        LogService.Info("performance",
            $"OVERVIEW_VIEWPORT_FIT width={RootGrid.ActualWidth:0.#} height={RootGrid.ActualHeight:0.#} viewport={fitViewport:0.#} content={fitContent:0.#} scroll={_pageScroll.VerticalScrollMode}");
        if (_overviewInfoCardsGrid is not { Children.Count: 3 } ||
            _overviewLiveHost is not { Children.Count: 1 } ||
            _overviewInfoFlyout != null ||
            _overviewInfoCardsGrid.ActualHeight < 55)
            throw new InvalidOperationException(
                "Overview Systém/Stav/Changelog are not permanently visible.");
        if (RootGrid.ActualWidth >= 1024 && RootGrid.ActualHeight >= 740 &&
            (_pageScroll.VerticalScrollMode != ScrollMode.Disabled ||
             fitContent > fitViewport + 1 || fitViewport - fitContent > 12))
            throw new InvalidOperationException(
                $"Inline details don't fit without scrolling: viewport={fitViewport:0.#} content={fitContent:0.#}");
        LogService.Info("performance",
            $"STARTUP_OVERVIEW_VISUAL_SMOKE_OK roots={_overviewPageCache.Count} metrics={_overviewMetricsHost.Children.Count} live={_overviewLiveHost.Children.Count} stable=true heroUnderTitlebar=true inlineNoScroll=true");
    }

    private async Task CompleteOverviewDashboardAsync(Task<OverviewSnapshot> snapshotTask,
        TextBlock cpu, TextBlock gpu, TextBlock ram, TextBlock disk, TextBlock os, TextBlock board,
        TextBlock power, TextBlock network, TextBlock driver, TextBlock history, TextBlock windows, CancellationToken token, int generation, Task<TweakBatchOverviewSnapshot>? injectedHistory = null)
    {
        var statusRefs = _overviewTextRefs;
        if (statusRefs == null || !ReferenceEquals(statusRefs.Os, os)) return;
        bool snapshotLoaded = false;
        try
        {
            await Task.Yield();
            if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
            SafeTaskService.Forget(
                CompleteOverviewDriverAsync(driver, token, generation),
                "overview",
                "Deferred overview GPU driver");
            // Keep UI-owned status and tweak-cache reads on the WinUI dispatcher.
            // ConfigureAwait(false) here allowed continuations to enumerate live
            // List<TweakDefinition> values on a ThreadPool thread during navigation.
            var s = await snapshotTask;
            snapshotLoaded = true;
            if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
                SetOverviewTextIfChanged(cpu, s.Cpu);
                SetOverviewTextIfChanged(gpu, s.Gpu);
                SetOverviewTextIfChanged(ram, s.Ram);
                SetOverviewTextIfChanged(disk, s.Disk);
                SetOverviewTextIfChanged(os, s.Windows.Replace(" • 26H2", "\n26H2", StringComparison.OrdinalIgnoreCase));
                SetOverviewTextIfChanged(board, s.Motherboard);
                SetOverviewTextIfChanged(power, s.PowerPlan);
                SetOverviewTextIfChanged(network, s.Network);
                SetOverviewTextIfChanged(windows, s.Windows);

                if (_cpuGauge != null) SetOverviewTextIfChanged(_cpuGauge.SecondaryText, s.Cpu);
                if (_gpuGauge != null) SetOverviewTextIfChanged(_gpuGauge.SecondaryText, s.Gpu);
                if (_latestOverviewRamSample is { } liveRam)
                    UpdateOverviewRamSummary(liveRam);
                else if (_ramGauge != null) SetOverviewTextIfChanged(_ramGauge.SecondaryText, s.Ram);
                if (_diskGauge != null) SetOverviewTextIfChanged(_diskGauge.SecondaryText, s.Disk);
                if (_overviewHardwareCpuText != null) SetOverviewTextIfChanged(_overviewHardwareCpuText, s.Cpu);
                if (_overviewHardwareGpuText != null) SetOverviewTextIfChanged(_overviewHardwareGpuText, s.Gpu);
                if (_overviewHardwareRamText != null) SetOverviewTextIfChanged(_overviewHardwareRamText, string.IsNullOrWhiteSpace(s.RamHardware) ? s.Ram : s.RamHardware);
                if (_overviewHardwareDiskText != null) SetOverviewTextIfChanged(_overviewHardwareDiskText, s.Disk);
                ApplyOverviewSystemStatus(statusRefs, "●  Systém načten");
            });

            if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
            var tweakHistory = await (injectedHistory ?? TweakBatchHistoryService.ReadOverviewSnapshotAsync(4, 120, token));
            if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
            var recent = tweakHistory.Recent;
            var expectedTweakStates = tweakHistory.ExpectedStates;
            var detectedTweakStates = GetDetectedTweakStates();
            var mergedTweakStates = MergeActiveTweakStates(expectedTweakStates, detectedTweakStates);
            int expectedActiveTweaks = expectedTweakStates.Count(x => x.Value);
            int detectedActiveTweaks = detectedTweakStates.Count(x => x.Value);
            int activeTweaks = mergedTweakStates.Count(x => x.Value);
            string activeSource = detectedTweakStates.Count > 0
                ? (expectedTweakStates.Count > 0
                    ? $"ověřeno {detectedTweakStates.Count} + historie"
                    : $"ověřeno {detectedTweakStates.Count}")
                : "historie";
            string activeLogSignature = $"{activeTweaks}|{expectedActiveTweaks}|{expectedTweakStates.Count}|{detectedActiveTweaks}|{detectedTweakStates.Count}|{mergedTweakStates.Count}";
            DateTime activeLogNow = DateTime.UtcNow;
            if (OverviewDiagnosticLogPolicy.ShouldLog(_overviewActiveTweaksLogSignature,
                    activeLogSignature, _overviewActiveTweaksLastLogUtc, activeLogNow))
            {
                _overviewActiveTweaksLogSignature = activeLogSignature;
                _overviewActiveTweaksLastLogUtc = activeLogNow;
                LogService.Info("overview",
                    $"OVERVIEW_ACTIVE_TWEAKS active={activeTweaks} expectedActive={expectedActiveTweaks} expectedKnown={expectedTweakStates.Count} detectedActive={detectedActiveTweaks} detectedKnown={detectedTweakStates.Count} mergedKnown={mergedTweakStates.Count}");
            }
            string historyText = BuildOverviewActiveTweakStatusText(
                activeTweaks,
                mergedTweakStates.Count,
                activeSource,
                recent.Length == 0 ? null : recent[0].Time);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
                SetOverviewTextIfChanged(history, historyText);
            });


        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _windowLifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("overview", "Overview data failed", ex);
            // Complete each independent card even when the other data source
            // fails: otherwise History remains at "Načítám..." indefinitely.
            // Ownership checks prevent an older navigation from repainting a
            // replacement Overview tree after cancellation or retry.
            if (snapshotLoaded)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (CanPublishOverviewStatus(statusRefs, token, generation))
                        SetOverviewTextIfChanged(history, "Historie tweaků je dočasně nedostupná");
                });
            }
            else
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (CanPublishOverviewStatus(statusRefs, token, generation))
                        ApplyOverviewSystemStatus(statusRefs, "●  Systém se nepodařilo načíst");
                });
            }
        }
    }

    private async Task CompleteOverviewDriverAsync(TextBlock driver, CancellationToken token, int generation, Task<GpuDriverInfo?>? injectedWarmCache = null)
    {
        var statusRefs = _overviewTextRefs;
        if (statusRefs == null || !ReferenceEquals(statusRefs.Driver, driver) ||
            !CanPublishOverviewStatus(statusRefs, token, generation)) return;
        try
        {
            if (_gpuDetectionGeneration == GpuDriverService.DetectionCacheGeneration && _gpuInfo != null)
            {
                string cachedVersion = _gpuInfo.DisplayVersion;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (CanPublishOverviewStatus(statusRefs, token, generation))
                    {
                        driver.Text = cachedVersion;
                        ApplyOverviewDriverStatus(statusRefs, string.IsNullOrWhiteSpace(cachedVersion) ? "●  Ovladač nezjištěn" : "●  Ovladač zjištěn");
                    }
                });
                return;
            }

            // A valid persisted GPU identity takes only a few milliseconds
            // and needs no 900ms startup deferral or registry/WMI detection.
            // Defer the *expensive* hardware probe only on a true cache miss.
            var timing = System.Diagnostics.Stopwatch.StartNew();
            SynchronizeGpuDetectionGeneration();
            long detectionGeneration = _gpuDetectionGeneration;
            var persisted = await (injectedWarmCache ?? GpuDriverService.TryWarmPersistentDetectionCacheAsync(token));
            // The cached driver and navigation ownership are WinUI-managed state.
            if (!DispatcherQueue.HasThreadAccess)
                throw new InvalidOperationException("Overview GPU warm-cache completion must resume on UI dispatcher.");
            if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
            bool cacheHit = persisted != null &&
                detectionGeneration == GpuDriverService.DetectionCacheGeneration;
            GpuDriverInfo? gpuDriver;
            if (cacheHit)
            {
                gpuDriver = persisted;
                _gpuInfo = persisted;
            }
            else
            {
                // Protect the initial UI render from expensive driver detection,
                // while keeping cached status responsive on every navigation.
                await Task.Delay(900, token);
                if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
                gpuDriver = await GetOverviewGpuDriverAsync(token);
            }
            if (!CanPublishOverviewStatus(statusRefs, token, generation)) return;
            LogService.Info("performance",
                $"OVERVIEW_GPU_DRIVER_READY cacheHit={cacheHit} ms={timing.ElapsedMilliseconds} " +
                $"fallbackDeferred={!cacheHit}");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (CanPublishOverviewStatus(statusRefs, token, generation))
                {
                    SetOverviewTextIfChanged(driver, gpuDriver == null ? "Driver nezjištěn" : gpuDriver.DisplayVersion);
                    ApplyOverviewDriverStatus(statusRefs, gpuDriver == null || string.IsNullOrWhiteSpace(gpuDriver.DisplayVersion)
                        ? "●  Ovladač nezjištěn" : "●  Ovladač zjištěn");
                }
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _windowLifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("overview", "Overview GPU driver failed", ex);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (CanPublishOverviewStatus(statusRefs, token, generation))
                    ApplyOverviewDriverStatus(statusRefs, "●  Ovladač se nepodařilo načíst");
            });
        }
    }

    private const string OverviewSystemLoading = "●  Načítám systém…";
    private const string OverviewDriverLoading = "●  Načítám ovladač…";

    private bool CanPublishOverviewStatus(OverviewTextRefs owner, CancellationToken token, int generation) =>
        !_windowLifetimeCts.IsCancellationRequested && !token.IsCancellationRequested &&
        generation == _navigationGeneration && _section == "overview" && ReferenceEquals(owner, _overviewTextRefs);

    // UI-thread publication keeps completion even while the corresponding label does not exist.
    private void ApplyOverviewSystemStatus(OverviewTextRefs owner, string status)
    {
        owner.SystemStatus = status;
        if (ReferenceEquals(owner, _overviewTextRefs) && _overviewSystemStatusText != null)
            SetOverviewTextIfChanged(_overviewSystemStatusText, status);
    }

    private void ApplyOverviewDriverStatus(OverviewTextRefs owner, string status)
    {
        owner.DriverStatus = status;
        if (ReferenceEquals(owner, _overviewTextRefs) && _overviewDriverStatusText != null)
            SetOverviewTextIfChanged(_overviewDriverStatusText, status);
    }

    private Border BuildOverviewHardwareStrip(OverviewTextRefs refs, bool compact)
    { return BuildBlackHardwareStrip(refs, compact); }
    private void ReleaseOverviewGaugeVisuals()
    {
        ReleaseGauge(ref _cpuGauge);
        ReleaseGauge(ref _ramGauge);
        ReleaseGauge(ref _gpuGauge);
        ReleaseGauge(ref _diskGauge);
    }

    private static void ReleaseGauge(ref GaugeRefs? refs) => refs = null;

    private static void SetOverviewTextIfChanged(TextBlock block, string value)
    {
        if (!string.Equals(block.Text, value, StringComparison.Ordinal))
            block.Text = value;
    }

    private static void SetOverviewWidthIfChanged(FrameworkElement element, double value)
    {
        if (Math.Abs(element.Width - value) >= 0.1)
            element.Width = value;
    }

    internal async Task RunOverviewGaugeHotpathSmokeAsync(bool includeNativeBenchmark = false)
    {
        // This isolated test can overlap initial startup navigation. Its
        // first deferred gauge factory may be canceled by the later Overview
        // activation; make one bounded fresh attempt before reporting failure.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await ShowSectionAsync("overview");
            for (int i = 0; i < 40 &&
                (_cpuGauge == null || _ramGauge == null ||
                 _gpuGauge == null || _diskGauge == null ||
                 _referenceNetworkValue == null || _referenceChartCpu == null); i++)
                await Task.Delay(25);
            if (_cpuGauge != null && _ramGauge != null &&
                _gpuGauge != null && _diskGauge != null &&
                _referenceNetworkValue != null && _referenceChartCpu != null)
                break;
            if (attempt == 0)
                InvalidateOverviewPageCache(forceRelease: true);
        }
        if (_cpuGauge == null || _ramGauge == null || _gpuGauge == null || _diskGauge == null ||
            _referenceNetworkValue == null || _referenceChartCpu == null)
            throw new InvalidOperationException("Overview gauges or reference cards were not materialized.");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        void Frame()
        {
            AnimateGauge(_cpuGauge, 31);
            AnimateGauge(_ramGauge, 52);
            AnimateGauge(_gpuGauge, 40);
            AnimateGauge(_diskGauge, 12);
        }
        for (int i = 0; i < 35; i++) Frame(); // warmed sample windows
        int initialRedraws = _cpuGauge.SparklineRedrawCount + _ramGauge.SparklineRedrawCount +
            _gpuGauge.SparklineRedrawCount + _diskGauge.SparklineRedrawCount;
        // Before measuring, ensure the native fill matches all four curves.
        foreach (var gauge in new[] { _cpuGauge, _ramGauge, _gpuGauge, _diskGauge })
            if (gauge.SparkFill == null || gauge.SparkFill.Points.Count != gauge.Sparkline.Points.Count + 2)
                throw new InvalidOperationException("Overview fill geometry not aligned with its sparkline.");
        long before = GC.GetAllocatedBytesForCurrentThread();
        process.Refresh();
        TimeSpan cpuStart = process.TotalProcessorTime;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 120; i++) Frame();
        sw.Stop();
        process.Refresh();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        int finalRedraws = _cpuGauge.SparklineRedrawCount + _ramGauge.SparklineRedrawCount +
            _gpuGauge.SparklineRedrawCount + _diskGauge.SparklineRedrawCount;
        if (finalRedraws != initialRedraws)
            throw new InvalidOperationException($"Steady gauges were unnecessarily redrawn: {finalRedraws - initialRedraws}.");
        LogService.Info("performance",
            $"OVERVIEW_GAUGE_HOTPATH_SMOKE_OK frames=120 gauges=4" +
            $" wallMs={sw.Elapsed.TotalMilliseconds:F2}" +
            $" cpuMs={(process.TotalProcessorTime - cpuStart).TotalMilliseconds:F2}" +
            $" allocatedBytes={allocated} wsMB={process.WorkingSet64 / 1048576d:F2}" +
            $" redraws={finalRedraws - initialRedraws} fillWritesSkipped={120 * 4 * 33}");

        // The steady fast path must not mask an independently invalidated
        // native Polygon fill. A rebuilt fill and a new sample both re-enter
        // the full path, then subsequent equal frames remain allocation-free.
        _cpuGauge.SparkFill!.Points.Clear();
        int fillRecoveryBefore = _cpuGauge.SparklineRedrawCount;
        AnimateGauge(_cpuGauge, 31);
        if (_cpuGauge.SparklineRedrawCount != fillRecoveryBefore + 1 ||
            _cpuGauge.SparkFill.Points.Count != _cpuGauge.Sparkline.Points.Count + 2)
            throw new InvalidOperationException("Steady gauge failed to restore lost native fill.");
        int recoveredRedraws = _cpuGauge.SparklineRedrawCount;
        AnimateGauge(_cpuGauge, 31);
        if (_cpuGauge.SparklineRedrawCount != recoveredRedraws)
            throw new InvalidOperationException("Restored gauge redrew an identical second sample.");

        // Verify changing data too: retaining WinUI point slots must preserve
        // the same line/fill polygon geometry under a rolling 30-sample window,
        // not just on the identical-value fast path above.
        int rollingRedrawsBefore = _cpuGauge.SparklineRedrawCount +
            _ramGauge.SparklineRedrawCount + _gpuGauge.SparklineRedrawCount +
            _diskGauge.SparklineRedrawCount;
        long rollingAllocatedStart = GC.GetAllocatedBytesForCurrentThread();
        var rollingWatch = System.Diagnostics.Stopwatch.StartNew();
        for (int frame = 0; frame < 90; frame++)
        {
            AnimateGauge(_cpuGauge, (frame * 7) % 100);
            AnimateGauge(_ramGauge, (frame * 3 + 18) % 100);
            AnimateGauge(_gpuGauge, (frame * 11) % 100);
            AnimateGauge(_diskGauge, (frame * 5 + 2) % 100);
            foreach (var gauge in new[] { _cpuGauge, _ramGauge, _gpuGauge, _diskGauge })
            {
                if (gauge.Sparkline.Points.Count != gauge.Samples.Count ||
                    gauge.SparkFill?.Points.Count != gauge.Sparkline.Points.Count + 2 ||
                    gauge.Sparkline.Points.Any(point =>
                        !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                    throw new InvalidOperationException(
                        "Rolling gauge geometry lost a point or an aligned fill.");
            }
        }
        rollingWatch.Stop();
        long rollingAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - rollingAllocatedStart;
        LogService.Info("performance",
            $"OVERVIEW_GAUGE_ROLLING_BENCH_OK frames=90 gauges=4 wallMs={rollingWatch.Elapsed.TotalMilliseconds:F3} allocatedB={rollingAllocatedBytes}");
        int rollingRedrawsAfter = _cpuGauge.SparklineRedrawCount +
            _ramGauge.SparklineRedrawCount + _gpuGauge.SparklineRedrawCount +
            _diskGauge.SparklineRedrawCount;
        if (rollingRedrawsAfter <= rollingRedrawsBefore)
            throw new InvalidOperationException(
                "Changing gauge samples failed to redraw native geometry.");

        // Validate the final value of each real, visible 22-segment grid, not
        // merely the counters/underlying sparklines. The optimized update
        // range must light and dim exactly the same blocks as before.
        static void VerifySegmentLevel(GaugeRefs refs, double value, string phase)
        {
            if (refs.TrackHost is not Grid grid || grid.Children.Count != 22)
                throw new InvalidOperationException("Overview segment grid missing: " + phase);
            int lit = Math.Clamp((int)Math.Round(value * grid.Children.Count / 100), 0, grid.Children.Count);
            for (int i = 0; i < grid.Children.Count; i++)
            {
                if (grid.Children[i] is not Border block ||
                    Math.Abs(block.Opacity - (i < lit ? 1d : .27d)) > .001)
                    throw new InvalidOperationException($"Segment opacity mismatch {phase}: index={i} lit={lit}.");
            }
        }
        VerifySegmentLevel(_cpuGauge, 23, "cpu-rolling");
        VerifySegmentLevel(_ramGauge, 85, "ram-rolling");
        VerifySegmentLevel(_gpuGauge, 79, "gpu-rolling");
        VerifySegmentLevel(_diskGauge, 47, "disk-rolling");

        AnimateGauge(_cpuGauge, 97);
        VerifySegmentLevel(_cpuGauge, 97, "cpu-high");
        if (_cpuGauge.LastAppearance != 2 || _cpuGauge.StatusText.Text != "HIGH")
            throw new InvalidOperationException("High telemetry state did not update.");
        // An existing segment must retain its WinUI animation across rapid
        // HIGH -> NORMAL changes rather than creating a new Storyboard.
        if (_cpuGauge.TrackHost is not Grid cpuTrack ||
            cpuTrack.Children[15] is not Border cpuSegment ||
            !_referenceSegmentAnimations.TryGetValue(cpuSegment, out var cachedAnimation))
            throw new InvalidOperationException("Reference segment animation cache not primed.");
        AnimateGauge(_cpuGauge, 31);
        if (!_referenceSegmentAnimations.TryGetValue(cpuSegment, out var retainedAnimation) ||
            !ReferenceEquals(cachedAnimation, retainedAnimation))
            throw new InvalidOperationException("Reference segment animation was allocated again.");
        if (_cpuGauge.LastAppearance != 0 || _cpuGauge.StatusText.Text != "NORMAL")
            throw new InvalidOperationException("Normal telemetry state did not recover.");
        _cpuGauge.Sparkline.Points.Clear(); // simulate invalidated visual after layout
        int priorRecoveryRedraws = _cpuGauge.SparklineRedrawCount;
        AnimateGauge(_cpuGauge, 31);
        if (_cpuGauge.Sparkline.Points.Count != _cpuGauge.Samples.Count ||
            _cpuGauge.SparkFill?.Points.Count != _cpuGauge.Sparkline.Points.Count + 2 ||
            _cpuGauge.SparklineRedrawCount != priorRecoveryRedraws + 1)
            throw new InvalidOperationException("Gauge line/fill geometry was not restored after invalidation.");
        // Invalid non-finite samples must be treated as unavailable, never
        // passed to segment geometry or left rendered as live sensor values.
        AnimateGauge(_gpuGauge, double.NaN);
        AnimateGauge(_gpuGauge, null);
        AnimateGauge(_gpuGauge, double.PositiveInfinity);
        if (_gpuGauge.LastAppearance != 4 || _gpuGauge.StatusText.Text != "NEDOSTUPNÉ")
            throw new InvalidOperationException("Missing telemetry state was not shown.");
        if (_gpuGauge.Samples.Count != 0 || _gpuGauge.Sparkline.Points.Count != 0 ||
            _gpuGauge.SparkFill?.Points.Count != 0 || _gpuGauge.ValueText.Text != "N/A")
            throw new InvalidOperationException("Unavailable GPU left stale samples or visible sparkline.");
        VerifySegmentLevel(_gpuGauge, 0, "gpu-unavailable");
        AnimateGauge(_gpuGauge, 40);
        VerifySegmentLevel(_gpuGauge, 40, "gpu-recovered");
        if (_gpuGauge.LastAppearance != 0 || _gpuGauge.StatusText.Text != "NORMAL" ||
            _gpuGauge.Samples.Count != 1 ||
            _gpuGauge.SparkFill?.Points.Count != _gpuGauge.Sparkline.Points.Count + 2)
            throw new InvalidOperationException("Recovered telemetry state/first sample was not restored.");
        // Verify that the real 22-segment cards use one shared immutable
        // border brush, not a separate native brush for every segment.
        static void VerifyGaugeResources(GaugeRefs gauge)
        {
            if (gauge.TrackHost is not Grid track ||
                track.Children.Count != 22 ||
                track.Children[0] is not Border first ||
                first.BorderBrush is not SolidColorBrush brush ||
                gauge.SparkHost.Clip is not RectangleGeometry)
                throw new InvalidOperationException("Overview gauge resources missing.");
            for (int i = 1; i < track.Children.Count; i++)
                if (track.Children[i] is not Border segment ||
                    !ReferenceEquals(brush, segment.BorderBrush))
                    throw new InvalidOperationException("Overview gauge replicated native segment border brushes.");
        }
        VerifyGaugeResources(_cpuGauge);
        VerifyGaugeResources(_ramGauge);
        VerifyGaugeResources(_gpuGauge);
        VerifyGaugeResources(_diskGauge);
        if (_referenceNetworkBlocks is not { Length: 22 } networkSegments ||
            networkSegments[0].BorderBrush is not SolidColorBrush networkBrush ||
            networkSegments.Any(b => !ReferenceEquals(b.BorderBrush, networkBrush)))
            throw new InvalidOperationException("Overview network meter replicated its native segment brushes.");
        // A skipped native frame must keep the rolling 2-second samples,
        // numeric readout, trend and 22 LEDs live, then repaint BOTH line
        // and fill on the existing 4-second cadence. No new runtime probe.
        foreach (GaugeRefs gauge in new[] { _cpuGauge, _ramGauge, _gpuGauge, _diskGauge })
        {
            for (int i = 0; i < 33; i++)
                AnimateGauge(gauge, 35 + i % 7);
            if (gauge.Sparkline.Points.Count != 30 ||
                gauge.SparkFill?.Points.Count != 32)
                throw new InvalidOperationException("Gauge cadence smoke requires warmed line/fill.");
            var beforePoints = gauge.Sparkline.Points.ToArray();
            int beforePaints = gauge.SparklineRedrawCount;
            AnimateGauge(gauge, 92, refreshSpark: false);
            if (gauge.SparklineRedrawCount != beforePaints ||
                gauge.CurrentValue != 92 || gauge.Samples.Count != 30 ||
                gauge.ValueText.Text != "92%" ||
                gauge.Sparkline.Points.Count != beforePoints.Length)
                throw new InvalidOperationException("Gauge off-cadence telemetry or native paint mutated.");
            for (int i = 0; i < beforePoints.Length; i++)
                if (gauge.Sparkline.Points[i] != beforePoints[i])
                    throw new InvalidOperationException("Off-cadence gauge wrote native curve points.");
            VerifySegmentLevel(gauge, 92, "cadence-live");
            AnimateGauge(gauge, 73, refreshSpark: true);
            if (gauge.SparklineRedrawCount != beforePaints + 1 ||
                gauge.SparkFill?.Points.Count != gauge.Sparkline.Points.Count + 2 ||
                gauge.Sparkline.Points.Any(point =>
                    !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                throw new InvalidOperationException("On-cadence gauge failed to repaint curve/fill.");
        }
        // Regression: tick 1/3/5 is the slow GPU/disk sample, while the
        // historical chart's native redraw tick is 0/2/4. If a future change
        // applies only the latter to all gauges, slow sensor charts freeze.
        for (int cycle = 0; cycle < 6; cycle++)
        {
            bool slowDue = cycle > 0 && cycle % 2 == 1;
            bool historyDue = cycle % 2 == 0;
            if (OverviewGaugeSparkDue(slowDue, historyDue) !=
                (slowDue || historyDue))
                throw new InvalidOperationException("Gauge slow-sensor/chart phase policy is incorrect.");
            if (slowDue && !OverviewGaugeSparkDue(true, false))
                throw new InvalidOperationException("GPU/disk spark was skipped on a fresh 4-second sensor tick.");
        }
        int gpuBeforeSlow = _gpuGauge.SparklineRedrawCount;
        int diskBeforeSlow = _diskGauge.SparklineRedrawCount;
        AnimateGauge(_gpuGauge, 51, OverviewGaugeSparkDue(true, false));
        AnimateGauge(_diskGauge, 29, OverviewGaugeSparkDue(true, false));
        AnimateGauge(_gpuGauge, 64, OverviewGaugeSparkDue(true, false));
        AnimateGauge(_diskGauge, 39, OverviewGaugeSparkDue(true, false));
        if (_gpuGauge.SparklineRedrawCount != gpuBeforeSlow + 2 ||
            _diskGauge.SparklineRedrawCount != diskBeforeSlow + 2)
            throw new InvalidOperationException("Two consecutive slow PDH ticks did not repaint GPU/disk sparklines.");
        LogService.Info("performance", "OVERVIEW_GPU_DISK_SPARK_PHASE_SMOKE_OK slowTicks=2 nativePaints=2 cpuChartPhase=opposite");
        // Detailed performance benchmarks belong ONLY to the dedicated
        // --overview-gauge-hotpath-smoke command. The full release UI smoke
        // keeps all behavioral assertions but must not inject 240 artificial
        // changing-value frames on four WinUI cards and distort A/B CPU.
        if (includeNativeBenchmark)
        {
            (int paints, double wallMs, long allocatedB) ProbeGaugeGeometryCadence(bool half)
            {
                GaugeRefs[] gauges = { _cpuGauge, _ramGauge, _gpuGauge, _diskGauge };
                int previousPaints = gauges.Sum(g => g.SparklineRedrawCount);
                long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                for (int frame = 0; frame < 120; frame++)
                    for (int g = 0; g < gauges.Length; g++)
                        AnimateGauge(gauges[g], (frame * (7 + g * 4) + g * 13 + 3) % 100,
                            !half || frame % 2 == 0);
                timer.Stop();
                return (gauges.Sum(g => g.SparklineRedrawCount) - previousPaints,
                    timer.Elapsed.TotalMilliseconds,
                    GC.GetAllocatedBytesForCurrentThread() - beforeBytes);
            }
            var everyTick = ProbeGaugeGeometryCadence(half: false);
            var everyOtherTick = ProbeGaugeGeometryCadence(half: true);
            if (everyTick.paints != 480 || everyOtherTick.paints != 240)
                throw new InvalidOperationException(
                    $"Gauge cadence expected 480 vs 240 native curve+fill frames: {everyTick.paints}/{everyOtherTick.paints}.");
            LogService.Info("performance",
                $"OVERVIEW_GAUGE_4S_BENCH_OK samples=120 gauges=4 fullPaints={everyTick.paints} halfPaints={everyOtherTick.paints} " +
                $"fullMs={everyTick.wallMs:F2} halfMs={everyOtherTick.wallMs:F2} " +
                $"fullAllocatedB={everyTick.allocatedB} halfAllocatedB={everyOtherTick.allocatedB}");
        }
        LogService.Info("performance",
            "OVERVIEW_GAUGE_4S_CADENCE_SMOKE_OK telemetry2s=true lineFill4s=true history30=true nativeWritesSkipped=true");
        LogService.Info("performance",
            "OVERVIEW_GAUGE_RENDER_GUARD_OK steady=suppressed high=true normal=true unavailable=true recovered=true geometry=true sharedBrushes=true");

        // Smoke-only steady-rate network comparison, using the normal
        // visible reference card without any extra production polling.
        if (_referenceNetworkValue == null || _referenceNetworkDetail == null)
            throw new InvalidOperationException("Overview network card not materialized.");
        for (int i = 0; i < 35; i++)
            UpdateReferenceNetwork(18.25, 4.75, online: true);
        long networkBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var networkWatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 180; i++)
            UpdateReferenceNetwork(18.25, 4.75, online: true);
        networkWatch.Stop();
        LogService.Info("performance",
            $"OVERVIEW_NETWORK_STEADY_BENCH_OK frames=180 wallMs={networkWatch.Elapsed.TotalMilliseconds:F3} allocatedB={GC.GetAllocatedBytesForCurrentThread() - networkBytesBefore}");
        if (_referenceNetworkValue.Text != FormatRate(23) ||
            _referenceNetworkDetail.Text != $"↓ {FormatRate(18.25)}  |  ↑ {FormatRate(4.75)}" ||
            _referenceNetworkHistory.Count != 30)
            throw new InvalidOperationException("Steady network labels/history incorrect.");
        UpdateReferenceNetwork(18.25, 4.75, online: false);
        if (_referenceNetworkValue.Text != "—" ||
            _referenceNetworkDetail.Text != "Síť odpojena" ||
            _referenceNetworkFoot?.Text != "Síť odpojena")
            throw new InvalidOperationException("Offline network labels were not refreshed.");
        UpdateReferenceNetwork(18.25, 4.75, online: true);
        if (_referenceNetworkValue.Text != FormatRate(23) ||
            _referenceNetworkDetail.Text != $"↓ {FormatRate(18.25)}  |  ↑ {FormatRate(4.75)}" ||
            _referenceNetworkHistory.Count != 30)
            throw new InvalidOperationException("Reconnected network labels/history were not restored.");
        UpdateReferenceNetwork(18.25, 4.75, online: true, adapterName: "Ethernet 2");
        if (_referenceNetworkFoot?.Text != "Ethernet 2")
            throw new InvalidOperationException("Measured NIC label was not displayed.");
        UpdateReferenceNetwork(18.25, 4.75, online: true, adapterName: "2 aktivní rozhraní");
        if (_referenceNetworkFoot?.Text != "2 aktivní rozhraní")
            throw new InvalidOperationException("Aggregated NIC traffic was incorrectly attributed to one adapter.");
        LogService.Info("performance",
            "OVERVIEW_NETWORK_TEXT_GUARD_OK steady=true offline=true reconnect=true multiNicLabel=true historyPreserved=true");

        // The 30-value rolling peak must expire on the exact frame it leaves
        // the history. Otherwise the 22 native segments stay nearly empty
        // long after an old high-traffic sample disappeared.
        UpdateReferenceNetwork(799, 1, online: true);
        for (int i = 0; i < 29; i++)
            UpdateReferenceNetwork(9, 1, online: true);
        if (_referenceNetworkLastLit != 0 || _referenceNetworkHistory.Count != 30)
            throw new InvalidOperationException("Network meter prematurely lost its rolling peak.");
        UpdateReferenceNetwork(9, 1, online: true);
        if (_referenceNetworkLastLit != 22 || _referenceNetworkHistory.Count != 30)
            throw new InvalidOperationException("Network meter retained a peak past the 30-sample window.");
        LogService.Info("performance", "OVERVIEW_NETWORK_PEAK_EXPIRY_SMOKE_OK window=30 stalePeak=false nativeSegments=22");

        // On an off-cadence sample, values/history/segments must update while
        // the existing native PointCollection is left untouched. The next
        // chart cadence must catch up with the latest rolling samples.
        if (_referenceNetworkSpark?.Parent is Canvas actualNetworkChart &&
            actualNetworkChart.ActualWidth >= 10)
        {
            var priorPoints = _referenceNetworkSpark.Points.ToArray();
            UpdateReferenceNetwork(77, 1, online: true, refreshSpark: false);
            if (_referenceNetworkHistory.Count != 30 ||
                _referenceNetworkValue?.Text != FormatRate(78) ||
                _referenceNetworkSpark.Points.Count != priorPoints.Length)
                throw new InvalidOperationException("Off-cadence network tick lost live telemetry.");
            for (int i = 0; i < priorPoints.Length; i++)
                if (_referenceNetworkSpark.Points[i] != priorPoints[i])
                    throw new InvalidOperationException("Off-cadence network tick mutated native geometry.");
            UpdateReferenceNetwork(77, 1, online: true, refreshSpark: true);
            if (_referenceNetworkSpark.Points.Count != _referenceNetworkHistory.Count ||
                _referenceNetworkLastSparkWidth != actualNetworkChart.ActualWidth)
                throw new InvalidOperationException("Network chart failed to catch up at its 4-second cadence.");
        }
        LogService.Info("performance",
            "OVERVIEW_NETWORK_CADENCE_SMOKE_OK live2s=true geometry4s=true history30=true");

        // Smoke-only measurement of the legacy network status strip. Use
        // detached native labels to avoid interfering with the active Overview.
        var originalStatus = _overviewNetworkStatusText;
        var originalSpeed = _overviewNetworkSpeedText;
        var originalNextChartUtc = _overviewNextNetworkChartUtc;
        try
        {
            _overviewNetworkStatusText = new TextBlock();
            _overviewNetworkSpeedText = new TextBlock();
            _overviewNextNetworkChartUtc = DateTime.MaxValue;
            for (int i = 0; i < 15; i++)
                UpdateOverviewNetworkVisuals(12.5, 3.75, online: true);
            long stripBefore = GC.GetAllocatedBytesForCurrentThread();
            var stripWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 360; i++)
                UpdateOverviewNetworkVisuals(12.5, 3.75, online: true);
            stripWatch.Stop();
            LogService.Info("performance",
                $"OVERVIEW_NETWORK_STATUS_STEADY_BENCH_OK frames=360 wallMs={stripWatch.Elapsed.TotalMilliseconds:F3} allocatedB={GC.GetAllocatedBytesForCurrentThread() - stripBefore}");
            if (_overviewNetworkStatusText.Text != "Aktivní připojení" ||
                _overviewNetworkSpeedText.Text != $"↓ {FormatRate(12.5)}      ↑ {FormatRate(3.75)}")
                throw new InvalidOperationException("Legacy Overview network strip lost steady data.");
            UpdateOverviewNetworkVisuals(0, 0, online: false);
            if (_overviewNetworkStatusText.Text != "Síť odpojena")
                throw new InvalidOperationException("Legacy Overview network strip failed offline state.");
            UpdateOverviewNetworkVisuals(18.5, 2.25, online: true);
            if (_overviewNetworkStatusText.Text != "Aktivní připojení" ||
                _overviewNetworkSpeedText.Text != $"↓ {FormatRate(18.5)}      ↑ {FormatRate(2.25)}")
                throw new InvalidOperationException("Legacy Overview network strip failed reconnect.");
            // A recreated UI tree must not inherit the old label memo.
            _overviewNetworkStatusText = new TextBlock();
            _overviewNetworkSpeedText = new TextBlock();
            UpdateOverviewNetworkVisuals(18.5, 2.25, online: true);
            if (_overviewNetworkStatusText.Text != "Aktivní připojení" ||
                _overviewNetworkSpeedText.Text != $"↓ {FormatRate(18.5)}      ↑ {FormatRate(2.25)}")
                throw new InvalidOperationException("Legacy network strip memo masked rebuilt native labels.");
        }
        finally
        {
            _overviewNetworkStatusText = originalStatus;
            _overviewNetworkSpeedText = originalSpeed;
            _overviewNextNetworkChartUtc = originalNextChartUtc;
        }

        // Detached native readouts only. Measure the same actual four-footer
        // and uptime paths used by Overview without touching visible state.
        var priorCpuFooter = _overviewCpuFooterText;
        var priorGpuFooter = _overviewGpuFooterText;
        var priorRamFooter = _overviewRamFooterText;
        var priorDiskFooter = _overviewDiskFooterText;
        var priorCpuFill = _overviewCpuFooterFill;
        var priorGpuFill = _overviewGpuFooterFill;
        var priorRamFill = _overviewRamFooterFill;
        var priorDiskFill = _overviewDiskFooterFill;
        var priorUptime = _overviewUptimeText;
        var priorFooterKey = _overviewLastFooterTextKey;
        var priorUptimeTarget = _overviewLastUptimeTextTarget;
        var priorUptimeMinute = _overviewLastUptimeMinute;
        try
        {
            _overviewCpuFooterText = new TextBlock();
            _overviewGpuFooterText = new TextBlock();
            _overviewRamFooterText = new TextBlock();
            _overviewDiskFooterText = new TextBlock();
            _overviewCpuFooterFill = new Border { Width = 0 };
            _overviewGpuFooterFill = new Border { Width = 0 };
            _overviewRamFooterFill = new Border { Width = 0 };
            _overviewDiskFooterFill = new Border { Width = 0 };
            _overviewUptimeText = new TextBlock();
            for (int i = 0; i < 12; i++)
            {
                UpdateOverviewFooterReadouts(28.25, 34.5, 53.75, 12.25);
                UpdateOverviewUptimeReadout();
            }
            long beforeFooterBytes = GC.GetAllocatedBytesForCurrentThread();
            var footerSw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 360; i++)
                UpdateOverviewFooterReadouts(28.25, 34.5, 53.75, 12.25);
            footerSw.Stop();
            LogService.Info("performance",
                $"OVERVIEW_FOOTER_STEADY_BENCH_OK frames=360 wallMs={footerSw.Elapsed.TotalMilliseconds:F3} allocatedB={GC.GetAllocatedBytesForCurrentThread() - beforeFooterBytes}");
            long beforeUptimeBytes = GC.GetAllocatedBytesForCurrentThread();
            var uptimeSw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 360; i++)
                UpdateOverviewUptimeReadout();
            uptimeSw.Stop();
            LogService.Info("performance",
                $"OVERVIEW_UPTIME_STEADY_BENCH_OK frames=360 wallMs={uptimeSw.Elapsed.TotalMilliseconds:F3} allocatedB={GC.GetAllocatedBytesForCurrentThread() - beforeUptimeBytes}");
            if (_overviewCpuFooterText.Text != $"{28.25:0}% využití" ||
                _overviewGpuFooterText.Text != $"{34.5:0}% využití" ||
                _overviewRamFooterText.Text != $"{53.75:0}% využití" ||
                _overviewDiskFooterText.Text != $"{12.25:0}% aktivita" ||
                string.IsNullOrWhiteSpace(_overviewUptimeText.Text))
                throw new InvalidOperationException("Overview footer/uptime labels lost steady live values.");
            UpdateOverviewFooterReadouts(double.NaN, double.PositiveInfinity,
                double.NegativeInfinity, null);
            if (_overviewCpuFooterText.Text != "Telemetrie CPU čeká…" ||
                _overviewGpuFooterText.Text != "Telemetrie GPU čeká…" ||
                _overviewRamFooterText.Text != "Telemetrie RAM čeká…" ||
                _overviewDiskFooterText.Text != "Telemetrie disku čeká…" ||
                _overviewCpuFooterFill.Width != 0 ||
                _overviewGpuFooterFill.Width != 0 ||
                _overviewRamFooterFill.Width != 0 ||
                _overviewDiskFooterFill.Width != 0)
                throw new InvalidOperationException("Overview nonfinite/missing footers retained stale text or width.");
            UpdateOverviewFooterReadouts(64, 31, 52, 9);
            if (_overviewCpuFooterText.Text != "64% využití" ||
                _overviewGpuFooterText.Text != "31% využití" ||
                _overviewRamFooterText.Text != "52% využití" ||
                _overviewDiskFooterText.Text != "9% aktivita" ||
                Math.Abs(_overviewCpuFooterFill.Width - 30.72) > .01)
                throw new InvalidOperationException("Overview footer recovery lost live text or bar width.");
            // Recreated native elements must receive values despite a matching
            // last-input cache; uptime also rebinds within the same minute.
            _overviewCpuFooterText = new TextBlock();
            _overviewGpuFooterText = new TextBlock();
            _overviewRamFooterText = new TextBlock();
            _overviewDiskFooterText = new TextBlock();
            _overviewUptimeText = new TextBlock();
            UpdateOverviewFooterReadouts(64, 31, 52, 9);
            UpdateOverviewUptimeReadout();
            if (_overviewCpuFooterText.Text != "64% využití" ||
                _overviewGpuFooterText.Text != "31% využití" ||
                _overviewRamFooterText.Text != "52% využití" ||
                _overviewDiskFooterText.Text != "9% aktivita" ||
                string.IsNullOrWhiteSpace(_overviewUptimeText.Text))
                throw new InvalidOperationException("Overview footer/uptime native rebuild cache failed.");
        }
        finally
        {
            _overviewCpuFooterText = priorCpuFooter;
            _overviewGpuFooterText = priorGpuFooter;
            _overviewRamFooterText = priorRamFooter;
            _overviewDiskFooterText = priorDiskFooter;
            _overviewCpuFooterFill = priorCpuFill;
            _overviewGpuFooterFill = priorGpuFill;
            _overviewRamFooterFill = priorRamFill;
            _overviewDiskFooterFill = priorDiskFill;
            _overviewUptimeText = priorUptime;
            _overviewLastFooterTextKey = priorFooterKey;
            _overviewLastUptimeTextTarget = priorUptimeTarget;
            _overviewLastUptimeMinute = priorUptimeMinute;
        }

        // Existing UI smoke only: quantify repeat header work without
        // changing live telemetry frequency or adding runtime diagnostics.
        for (int i = 0; i < 15; i++)
            UpdateReferenceHeader(31.25, 52.5, 40.75, 12.5);
        long headerBefore = GC.GetAllocatedBytesForCurrentThread();
        var headerWatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 360; i++)
            UpdateReferenceHeader(31.25, 52.5, 40.75, 12.5);
        headerWatch.Stop();
        LogService.Info("performance",
            $"OVERVIEW_REFERENCE_HEADER_STEADY_BENCH_OK frames=360 wallMs={headerWatch.Elapsed.TotalMilliseconds:F3} allocatedB={GC.GetAllocatedBytesForCurrentThread() - headerBefore}");
        if (_referenceChartCpu?.Text != $"{31.25:0} %" ||
            _referenceChartRam?.Text != $"{52.5:0} %" ||
            _referenceChartGpu?.Text != $"{40.75:0} %" ||
            _referenceChartDisk?.Text != $"{12.5:0} %" ||
            _referenceLeftUsage?.Text != $"{31.25:0} %")
            throw new InvalidOperationException(
                $"Reference header stable labels mismatch cpu={_referenceChartCpu?.Text} ram={_referenceChartRam?.Text} gpu={_referenceChartGpu?.Text} disk={_referenceChartDisk?.Text} left={_referenceLeftUsage?.Text}.");
        UpdateReferenceHeader(double.NaN, double.PositiveInfinity, double.NaN, double.NegativeInfinity);
        if (_referenceChartCpu?.Text != "—" || _referenceChartRam?.Text != "—" ||
            _referenceChartGpu?.Text != "—" || _referenceChartDisk?.Text != "—" ||
            _referenceLeftUsage?.Text != "—")
            throw new InvalidOperationException("Nonfinite reference header telemetry rendered as live values.");
        UpdateReferenceHeader(80, 79, 78, 77);
        if (_referenceChartCpu?.Text != "80 %" || _referenceChartRam?.Text != "79 %" ||
            _referenceChartGpu?.Text != "78 %" || _referenceChartDisk?.Text != "77 %" ||
            _referenceLeftUsage?.Text != "80 %")
            throw new InvalidOperationException("Reference header failed to recover after sensor loss.");
        LogService.Info("performance",
            "OVERVIEW_REFERENCE_HEADER_GUARD_OK steady=true missingNonfinite=true recovered=true chartPreserved=true");
    }

    // Slow GPU/disk sensors sample on the opposite 4-second phase from the
    // fast CPU/RAM history timer. A fresh slow sensor MUST repaint its own
    // native curve even when the fast chart cadence is not due.
    internal static bool OverviewGaugeSparkDue(bool freshSlowSample, bool historyChartTick) =>
        freshSlowSample || historyChartTick;

    private void AnimateGauge(GaugeRefs? refs, double? rawValue, bool refreshSpark = true)
    {
        if (refs == null) return;
        if (rawValue is not { } finiteValue || !double.IsFinite(finiteValue))
        {
            if (refs.MissingSamples == 0)
            {
                // Do this only once per outage, not on every 2-second tick.
                // Otherwise stale graph points and previously lit segments imply
                // a currently available sensor that no longer has a reading.
                refs.Samples.Clear();
                refs.Sparkline.Points.Clear();
                refs.SparkFill?.Points.Clear();
                refs.LastSparkValue = double.NaN;
                refs.ConsecutiveSameSamples = 0;
                refs.LastSparkWidth = refs.LastSparkHeight = double.NaN;
                refs.ResetVisibleState?.Invoke();
            }
            SetOverviewTextIfChanged(refs.ValueText, "N/A");
            SetOverviewTextIfChanged(refs.MinText, "MIN —");
            SetOverviewTextIfChanged(refs.AvgText, "AVG —");
            SetOverviewTextIfChanged(refs.PeakText, "MAX —");
            refs.LastValueLabel = refs.LastMinLabel = refs.LastAvgLabel = refs.LastPeakLabel = double.NaN;
            refs.MissingSamples++;
            int missingAppearance = refs.MissingSamples < 3 ? 3 : 4;
            if (refs.SparkHost.Visibility != Visibility.Collapsed)
                refs.SparkHost.Visibility = Visibility.Collapsed;
            if (refs.LastAppearance != missingAppearance)
            {
                SetOverviewTextIfChanged(refs.StatusText,
                    missingAppearance == 3 ? "NA\u010c\u00cdT\u00c1M" : "NEDOSTUPN\u00c9");
                refs.StatusText.Foreground = TextSecondaryBrush();
                refs.StatusBadge.BorderBrush = BorderThemeBrush();
                refs.StatusBadge.Background = OverviewLiquidInset();
                refs.LastAppearance = missingAppearance;
            }
            if (refs.EndDot.Visibility != Visibility.Collapsed)
                refs.EndDot.Visibility = Visibility.Collapsed;
            return;
        }

        double value = Math.Clamp(finiteValue, 0, 100);
        // Once the 30-sample window has settled at one value, repeating that
        // value changes neither visible text, statistics, trend, fill, bars,
        // nor sparkline geometry. Skip all native setters/queue enumeration,
        // while still detecting a resized or externally invalidated plot.
        // The next different sample takes the original full update path.
        if (refs.MissingSamples == 0 &&
            refs.ConsecutiveSameSamples >= 30 &&
            refs.LastSparkValue == value &&
            refs.Samples.Count == 30 &&
            refs.LastAppearance == (value >= 85 ? 2 : value >= 45 ? 1 : 0) &&
            refs.Sparkline.Points.Count == 30 &&
            (refs.SparkFill == null || refs.SparkFill.Points.Count == 32) &&
            refs.LastSparkWidth ==
                (refs.SparkHost.ActualWidth > 1 ? Math.Max(1, refs.SparkHost.ActualWidth - 4) : 230) &&
            refs.LastSparkHeight ==
                Math.Max(24, refs.SparkHost.ActualHeight > 1 ? refs.SparkHost.ActualHeight : 38) &&
            refs.SparkHost.Visibility == Visibility.Visible &&
            refs.EndDot.Visibility == Visibility.Visible)
            return;
        refs.MissingSamples = 0;
        if (refs.SparkHost.Visibility != Visibility.Visible)
            refs.SparkHost.Visibility = Visibility.Visible;
        double roundedValue = Math.Round(value);
        if (refs.LastValueLabel != roundedValue)
        {
            SetOverviewTextIfChanged(refs.ValueText, $"{value:0}%");
            refs.LastValueLabel = roundedValue;
        }
        refs.CurrentValue = value;
        refs.ConsecutiveSameSamples = value == refs.LastSparkValue
            ? Math.Min(30, refs.ConsecutiveSameSamples + 1) : 1;
        refs.LastSparkValue = value;

        refs.Samples.Enqueue(value);
        while (refs.Samples.Count > 30) refs.Samples.Dequeue();
        double sum = 0;
        double min = 100;
        double peak = 0;
        int sampleCount = 0;
        foreach (double sample in refs.Samples)
        {
            sum += sample;
            min = Math.Min(min, sample);
            peak = Math.Max(peak, sample);
            sampleCount++;
        }
        double avg = sampleCount == 0 ? value : sum / sampleCount;
        if (sampleCount == 0) min = value;
        double roundedMin = Math.Round(min), roundedAvg = Math.Round(avg), roundedPeak = Math.Round(peak);
        if (refs.LastMinLabel != roundedMin)
        {
            SetOverviewTextIfChanged(refs.MinText, $"MIN {min:0}%");
            refs.LastMinLabel = roundedMin;
        }
        if (refs.LastAvgLabel != roundedAvg)
        {
            SetOverviewTextIfChanged(refs.AvgText, $"AVG {avg:0}%");
            refs.LastAvgLabel = roundedAvg;
        }
        if (refs.LastPeakLabel != roundedPeak)
        {
            SetOverviewTextIfChanged(refs.PeakText, $"MAX {peak:0}%");
            refs.LastPeakLabel = roundedPeak;
        }

        int appearance = value >= 85 ? 2 : value >= 45 ? 1 : 0;
        if (refs.LastAppearance != appearance)
        {
            // Native TextBlock getter is unnecessary on every rolling sample
            // when the status classification has not changed.
            SetOverviewTextIfChanged(refs.StatusText,
                appearance == 2 ? "HIGH" : appearance == 1 ? "ACTIVE" : "NORMAL");
            refs.StatusText.Foreground = appearance == 2 ? WarningBrush() : TextSecondaryBrush();
            refs.StatusBadge.BorderBrush = appearance == 2 ? WarningBrush() : ButtonVioletBorderBrush();
            refs.StatusBadge.Background = OverviewLiquidInset();
            refs.LastAppearance = appearance;
        }

        double width = refs.SparkHost.ActualWidth > 1 ? Math.Max(1, refs.SparkHost.ActualWidth - 4) : 230;
        double height = Math.Max(24, refs.SparkHost.ActualHeight > 1 ? refs.SparkHost.ActualHeight : 38);
        // A 30-point window of identical samples has identical geometry on
        // every tick. Leave the WinUI PointCollection untouched until a value
        // or size changes, avoiding dozens of native bridge writes per gauge.
        double SparkY(double sample) => refs.LocalSparkScale && sampleCount>=2
            ? 4 + (1 - Math.Clamp(.5 + (sample-(peak+min)/2)/Math.Max(4,peak-min),0,1))*(height-8)
            : height - 4 - sample*(height - 8)/100d;
        bool identicalLine = refs.ConsecutiveSameSamples >= 30 && sampleCount == 30 &&
            refs.Sparkline.Points.Count == sampleCount &&
            (refs.SparkFill == null || refs.SparkFill.Points.Count == sampleCount + 2) &&
            refs.LastSparkWidth == width && refs.LastSparkHeight == height;
        // A 2-second telemetry sample must still update values, rolling stats,
        // trend and LED segments, but the native sparkline + glow fill can
        // share the existing 4-second history cadence. Never defer first paint,
        // sensor-recovery paint or a plot size change.
        bool needsSparkPaint = !identicalLine &&
            (refreshSpark || refs.Sparkline.Points.Count == 0 ||
             (refs.SparkFill != null &&
                 refs.SparkFill.Points.Count != refs.Sparkline.Points.Count + 2) ||
             refs.LastSparkWidth != width || refs.LastSparkHeight != height);
        if (needsSparkPaint)
        {
            // Retain the four live gauge PointCollection slots: avoid a
            // Clear and up to 30 native Add notifications on every sample.
            // Geometry and plot order are unchanged; stale trailing slots
            // are removed when the sample window shrinks.
            var sparkPoints = refs.Sparkline.Points;
            double lastX = 0;
            double lastY = SparkY(value);
            if (sampleCount == 1)
            {
                lastX = width;
                SetOverviewChartPoint(sparkPoints, 0, new Windows.Foundation.Point(0, lastY));
                SetOverviewChartPoint(sparkPoints, 1, new Windows.Foundation.Point(width, lastY));
            }
            else
            {
                int index = 0;
                foreach (double sample in refs.Samples)
                {
                    double x = index * width / (sampleCount - 1);
                    double y = SparkY(sample);
                    SetOverviewChartPoint(sparkPoints, index, new Windows.Foundation.Point(x, y));
                    lastX = x;
                    lastY = y;
                    index++;
                }
            }
            TrimOverviewChartPoints(sparkPoints, sampleCount == 1 ? 2 : sampleCount);
            Canvas.SetLeft(refs.EndDot, Math.Max(0, lastX - refs.EndDot.Width / 2));
            Canvas.SetTop(refs.EndDot, Math.Max(0, lastY - refs.EndDot.Height / 2));
            refs.LastSparkWidth = width;
            refs.LastSparkHeight = height;
            refs.SparklineRedrawCount++;
        }
        if (refs.EndDot.Visibility != Visibility.Visible)
            refs.EndDot.Visibility = Visibility.Visible;

        // Segmented Overview gauges have no attached legacy Fill. Reading the
        // layout and writing Width/Height on that detached Border costs native
        // WinUI work without affecting any pixel. Keep legacy gauge behavior.
        if (!refs.SegmentedLevel)
        {
            if (refs.VerticalLevel)
            {
                double trackHeight = refs.TrackHost.ActualHeight > 1 ? refs.TrackHost.ActualHeight - 8 : 90;
                double targetHeight = Math.Max(0, trackHeight * value / 100d);
                if (Math.Abs(refs.Fill.Height - targetHeight) >= .1)
                    refs.Fill.Height = targetHeight;
            }
            else
            {
                double trackWidth = refs.TrackHost.ActualWidth > 1 ? refs.TrackHost.ActualWidth : 100;
                double targetWidth = Math.Max(0, trackWidth * value / 100d);
                if (Math.Abs(refs.Fill.Width - targetWidth) >= .1)
                    refs.Fill.Width = targetWidth;
            }
        }

        if (refs.ReactiveElement != null)
        {
            double targetOpacity = Math.Clamp(.14 + value / 180d, .14, .72);
            if (Math.Abs(refs.ReactiveElement.Opacity - targetOpacity) >= .001)
                refs.ReactiveElement.Opacity = targetOpacity;
        }
        // Keep live 22-segment levels/trend at 2 seconds even when the native
        // chart's line/fill is deliberately not redrawn on this sample.
        refs.VisualUpdater?.Invoke(value, needsSparkPaint);
    }

    private Brush BuildCustomTitleBarBackground()
    {
        // In Glass21 the titlebar must be part of the same acrylic surface as the window,
        // not a separate dark strip. The native caption buttons are already transparent.
        if (_darkTheme) return ThemeColor(0, 0, 0, 0);
        var fade = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, _themePalette.SurfaceElevated.R, _themePalette.SurfaceElevated.G, _themePalette.SurfaceElevated.B), Offset = 0 });
        fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, _themePalette.Surface.R, _themePalette.Surface.G, _themePalette.Surface.B), Offset = .55 });
        fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, _themePalette.SurfaceAlt.R, _themePalette.SurfaceAlt.G, _themePalette.SurfaceAlt.B), Offset = 1 });
        return fade;
    }

    private void ActivateCustomTitleBarNow()
    {
        if (_customTitleBarPrimed) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        AppTitleBar.Background = BuildCustomTitleBarBackground();
        long backgroundMs = sw.ElapsedMilliseconds;
        try
        {
            var captionWindow = _cachedCaptionAppWindow ??
                Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle));
            _cachedCaptionAppWindow = captionWindow;
            var titleBar = _cachedCaptionTitleBar ?? captionWindow.TitleBar;
            _cachedCaptionTitleBar = titleBar;
            // Cold startup must register the native caption before Windows
            // can drag/snap the full Overview hero. The OS reports this flag
            // false here even when early Activated handlers have run; never
            // omit the required native extension on the first frame.
            titleBar.ExtendsContentIntoTitleBar = true;
        }
        catch
        {
            ExtendsContentIntoTitleBar = true;
        }

        long extendMs = sw.ElapsedMilliseconds;
        SetTitleBar(AppTitleBar);
        long setMs = sw.ElapsedMilliseconds;

        AppTitleBar.Visibility = Visibility.Visible;
        HeaderCard.Margin = new Thickness(0);
        _customTitleBarPrimed = true;
        // SetTitleBar may reset the native caption regions after a theme
        // rebuild; invalidate only this small native rectangle signature.
        _heroDragSignature = null;
        // The complete Overview hero is a native caption drag surface;
        // only the top row remains draggable on other pages.
        UpdateHeroDragRegions();

        LogService.Info("performance",
            $"TITLEBAR_ACTIVATE_SYNC backgroundMs={backgroundMs} extendMs={extendMs - backgroundMs} setMs={setMs - extendMs} totalMs={sw.ElapsedMilliseconds} deferredVisuals=true");

        // The brand/drag surface is already visible and interactive. Split
        // native caption color writes and rounded-HRGN repair across dispatcher
        // turns so they do not monopolize the first responsive input frame.
        int generation = ++_deferredTitleBarVisualsGeneration;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => StageDeferredTitleBarCaptionColors(generation, 0));
    }

    private int _deferredTitleBarVisualsGeneration;

    private void StageDeferredTitleBarCaptionColors(int generation, int stage)
    {
        if (_windowLifetimeCts.IsCancellationRequested ||
            _startupTitlebarThemeCompleted ||
            generation != _deferredTitleBarVisualsGeneration) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ApplyTitleBarCaptionColorStage(stage);
            LogService.Info("performance",
                $"TITLEBAR_CAPTION_STAGE stage={stage} ms={sw.ElapsedMilliseconds}");

            // Four native WinRT property writes per UI turn; the final region
            // update gets a separate turn. Native caption and hero drag were
            // installed before any of these deferred visual-only operations.
            if (DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (generation != _deferredTitleBarVisualsGeneration) return;
                    if (stage < 2) StageDeferredTitleBarCaptionColors(generation, stage + 1);
                    else FinishDeferredTitleBarVisuals();
                })) return;

            // A teardown race must never leave only half of the native style
            // installed; complete any remaining color groups if still alive.
            if (!_windowLifetimeCts.IsCancellationRequested &&
                generation == _deferredTitleBarVisualsGeneration)
            {
                for (int remaining = stage + 1; remaining < 3; remaining++)
                    ApplyTitleBarCaptionColorStage(remaining);
                FinishDeferredTitleBarVisuals();
            }
        }
        catch (System.Exception ex)
        {
            LogService.Error("theme", "Deferred titlebar caption colors failed", ex);
        }
    }

    // Used synchronously by a user-requested theme switch to avoid a
    // temporarily mismatched native caption; startup uses the two staged
    // dispatcher callbacks instead.
    private void CompleteDeferredTitleBarVisuals()
    {
        if (_startupTitlebarThemeCompleted) return;
        try
        {
            for (int stage = 0; stage < 3; stage++)
                ApplyTitleBarCaptionColorStage(stage);
            FinishDeferredTitleBarVisuals();
        }
        catch (System.Exception ex)
        {
            LogService.Error("theme", "Custom titlebar visuals failed", ex);
        }
    }

    private void ApplyTitleBarCaptionColorStage(int stage)
    {
        var captionWindow = _cachedCaptionAppWindow ??
            Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle));
        _cachedCaptionAppWindow = captionWindow;
        var captionBar = _cachedCaptionTitleBar ?? captionWindow.TitleBar;
        _cachedCaptionTitleBar = captionBar;
        var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        var captionFg = Windows.UI.Color.FromArgb(255, _themePalette.TextPrimary.R, _themePalette.TextPrimary.G, _themePalette.TextPrimary.B);
        var captionInactive = Windows.UI.Color.FromArgb(255, _themePalette.TextSecondary.R, _themePalette.TextSecondary.G, _themePalette.TextSecondary.B);
        var captionHover = Windows.UI.Color.FromArgb(48, _themePalette.SurfaceElevated.R, _themePalette.SurfaceElevated.G, _themePalette.SurfaceElevated.B);
        var captionPressed = Windows.UI.Color.FromArgb(76, _themePalette.Border.R, _themePalette.Border.G, _themePalette.Border.B);

        switch (stage)
        {
            case 0:
                captionBar.BackgroundColor = transparent;
                captionBar.ForegroundColor = captionFg;
                captionBar.InactiveBackgroundColor = transparent;
                captionBar.InactiveForegroundColor = captionInactive;
                break;
            case 1:
                captionBar.ButtonBackgroundColor = transparent;
                captionBar.ButtonInactiveBackgroundColor = transparent;
                captionBar.ButtonForegroundColor = captionFg;
                captionBar.ButtonInactiveForegroundColor = captionInactive;
                break;
            case 2:
                captionBar.ButtonHoverBackgroundColor = captionHover;
                captionBar.ButtonHoverForegroundColor = captionFg;
                captionBar.ButtonPressedBackgroundColor = captionPressed;
                captionBar.ButtonPressedForegroundColor = captionFg;
                // Startup colors are fully installed after the third stage.
                // Future focus changes need no new WinRT writes unless the
                // actual palette changes or native caption extension is lost.
                _appliedNativeCaptionColors =
                    (Foreground: captionFg, Inactive: captionInactive,
                     Hover: captionHover, Pressed: captionPressed);
                _nativeCaptionColorApplyCount++;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown titlebar color stage");
        }
    }

    private void FinishDeferredTitleBarVisuals()
    {
        if (_windowLifetimeCts.IsCancellationRequested || _startupTitlebarThemeCompleted) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ApplyNativeGlassWindowShape();
            RunWithOverviewPalette(PopulateCustomTitleBarContent);
            Volatile.Write(ref _startupTitlebarThemeCompleted, true);
            LogService.Info("performance",
                $"TITLEBAR_NATIVE_SHAPE_DEFERRED ms={sw.ElapsedMilliseconds} content={AppTitleBar.Children.Count}");
        }
        catch (System.Exception ex)
        {
            LogService.Error("theme", "Deferred native titlebar shape failed", ex);
        }
    }

    private void PopulateCustomTitleBarContent()
    {
        if (AppTitleBar.ColumnDefinitions.Count != 0 || AppTitleBar.Children.Count != 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        AppTitleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AppTitleBar.ColumnDefinitions.Add(new ColumnDefinition());
        long columnsMs = sw.ElapsedMilliseconds;

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        long brandShellMs = sw.ElapsedMilliseconds - columnsMs;
        _titleBarLogoImage = new Image
        {
            Width = 24,
            Height = 24,
            Stretch = Stretch.Uniform,
            Source = SidebarIconSource("overview")
        };
        long logoMs = sw.ElapsedMilliseconds - columnsMs - brandShellMs;
        brand.Children.Add(_titleBarLogoImage);
        brand.Children.Add(new TextBlock
        {
            Text = "Glass Optimizer",
            FontSize = 15.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Display, Segoe UI"),
            Foreground = TextPrimaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });
        AppTitleBar.Children.Add(brand);
        long brandMs = sw.ElapsedMilliseconds - columnsMs - brandShellMs - logoMs;
        StartupDiagnostics.BufferInfo($"TITLEBAR_CONTENT columnsMs={columnsMs} brandShellMs={brandShellMs} logoMs={logoMs} brandMs={brandMs} totalMs={sw.ElapsedMilliseconds}");
    }

    private volatile bool _windowTelemetryActive = true;
    private (double? Percent, double UsedGb, double TotalGb)? _latestOverviewRamSample;

    private static string FormatOverviewRamSummary(
        (double? Percent, double UsedGb, double TotalGb) sample) =>
        sample.Percent is { } percent
            ? $"{sample.UsedGb:0.0} / {sample.TotalGb:0.0} GB \u2022 {percent:0}% vyu\u017eito"
            : "Telemetrie RAM nedostupná";

    private void UpdateOverviewRamSummary((double? Percent, double UsedGb, double TotalGb) sample)
    {
        string summary = FormatOverviewRamSummary(sample);
        if (_overviewTextRefs != null) SetOverviewTextIfChanged(_overviewTextRefs.Ram, summary);
        if (_ramGauge != null) SetOverviewTextIfChanged(_ramGauge.SecondaryText, summary);
    }

    private async Task StartLiveMetricsAfterFirstRenderAsync()
    {
        CancellationToken windowToken = _windowLifetimeCts.Token;
        try
        {
            // Prime the cheap CPU delta counter after first paint, then start the worker on the
            // next UI turn. Avoid the previous 220+260 ms staged timer without moving PDH work to UI.
            await Task.Yield();
            windowToken.ThrowIfCancellationRequested();
            if (_liveMetricsCts != null) return;
            _ = ReadCpuUsage();
            await Task.Delay(90, windowToken).ConfigureAwait(false);
            if (_liveMetricsCts != null || windowToken.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!windowToken.IsCancellationRequested)
                    StartLiveMetricsLoop();
            });
        }
        catch (OperationCanceledException) when (windowToken.IsCancellationRequested)
        {
            // Normal window shutdown before the deferred metrics worker starts.
        }
    }

    private void StartLiveMetricsLoop()
    {
        if (_windowLifetimeCts.IsCancellationRequested) return;
        _liveMetricsCts?.Cancel();
        _liveMetricsCts?.Dispose();
        _liveMetricsCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
        var token = _liveMetricsCts.Token;
        SafeTaskService.Forget(
            Task.Run(() => RunLiveMetricsLoopAsync(token), token),
            "performance",
            "Live system metrics");
    }

    // Remain asleep while inactive, but resume the existing worker immediately
    // when Overview becomes visible or the app regains focus.
    private readonly SemaphoreSlim _liveMetricsWake = new(0, 1);

    private void SignalLiveMetricsWake()
    {
        if (_windowLifetimeCts.IsCancellationRequested) return;
        try { _liveMetricsWake.Release(); }
        catch (SemaphoreFullException) { } // collapse repeated focus/navigation events
    }

    private volatile bool _liveMetricsDiagnosticSuppressSlow;
    private volatile bool _liveMetricsDiagnosticSuppressUi;
    // Only the explicit one-shot CPU audit records native Overview UI phase timings.
    // No extra telemetry, logging or dispatcher work in normal app execution.
    private static readonly bool OverviewUiPhaseAudit = Array.Exists(
        Environment.GetCommandLineArgs(), argument =>
            string.Equals(argument, "--idle-cpu-smoke", StringComparison.OrdinalIgnoreCase));

    private async Task RunLiveMetricsLoopAsync(CancellationToken token)
    {
        int tick = 0;
        double? gpu = null;
        double? disk = null;
        while (!token.IsCancellationRequested)
        {
            bool onOverview = _section == "overview";
            bool activeOverview = onOverview && _windowTelemetryActive;
            if (!activeOverview)
            {
                // Restart the sampling schedule after returning: do not burn an
                // expensive GPU/disk PDH sample immediately merely because many
                // inactive wake cycles have elapsed.
                tick = 0;
                // Invalidate old readings: the first frame after activation
                // must not present a GPU/disk value from minutes ago, or use
                // a CPU delta accumulated throughout the inactive period.
                gpu = null;
                disk = null;
                _lastCpuKernel = 0;
                // Fully idle when Overview is hidden or the window is inactive. No hardware
                // probes/UI work is needed here; wake only occasionally to notice reactivation.
                // A ten-second timer only applies while actually idle. A UI
                // activation/navigation signal wakes immediately with no polling.
                await _liveMetricsWake.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                continue;
            }

            long sampleStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            // Record the time of observation on the sensor worker, not when
            // the WinUI dispatcher happens to consume a queued sample.
            DateTime sampleUtc = DateTime.UtcNow;
            double? cpu = ReadCpuUsage();
            var ramSample = ReadRamSnapshot();
            double? ram = ramSample.Percent;
            var networkRates = ReadNetworkRates();
            // Initialize native counters on the worker after first paint (~2 s).
            // Never run the measured cold PDH initialization on the UI thread.
            // Native GPU/disk performance counters are more expensive than CPU/RAM samples.
            // Poll them every other active iteration (4 s); CPU/RAM remain at 2 s.
            bool firstActiveSample = tick == 0;
            bool slowMetricsTick = tick >= 1 && tick % 2 == 1 && !_liveMetricsDiagnosticSuppressSlow;
            bool gpuMetricsSample = slowMetricsTick;
            bool diskMetricsSample = slowMetricsTick;
            if (diskMetricsSample)
            {
                disk = ReadDiskActivity();
                System.Threading.Interlocked.Increment(ref _latestDiskSensorGeneration);
            }
            if (gpuMetricsSample)
            {
                gpu = ReadGpuUsage();
                System.Threading.Interlocked.Increment(ref _latestGpuSensorGeneration);
            }
            // Sample on first active Overview frame, retry at 4 and 8 seconds
            // in case an elevated bridge is still starting, then every 16s.
            // No extra worker process or sampler is scheduled.
            if (OverviewTemperatureReadingPolicy.ShouldRead(tick))
            {
                var sensor = HardwareTelemetryBridgeService.TryReadFresh(TimeSpan.FromSeconds(15));
                FanAgentHealth? fan = FanProfileService.ReadHealthStatus();
                if (fan != null && (!fan.Healthy ||
                    !OverviewTemperatureReadingPolicy.IsFresh(
                        fan.UpdatedUtc, sampleUtc, TimeSpan.FromSeconds(15))))
                    fan = null;
                _referenceSensorCpu = OverviewTemperatureReadingPolicy.SelectCpu(
                    sensor?.CpuTempC, fan?.CpuTempC);
                _referenceSensorBoard = OverviewTemperatureReadingPolicy.ValidTemperature(
                    sensor?.MotherboardTempC);
                _referenceSensorFanRpm = fan?.HidOk == true
                    ? OverviewTemperatureReadingPolicy.AverageActiveFanRpm(fan.Rpms)
                    : null;
                _referenceCpuClockMhz = sensor?.EffectiveClockMHz ??
                    fan?.EffectiveClockMHz;
                try
                {
                    // A broken NVML result such as 0/200C must not mask a
                    // valid LibreHardwareMonitor GPU fallback.
                    _referenceSensorGpu = OverviewTemperatureReadingPolicy.ValidTemperature(
                        GpuNvapiUsageService.TryReadTemperature()) ??
                        OverviewTemperatureReadingPolicy.ValidTemperature(
                            GpuNvmlUsageService.TryReadTemperature()) ??
                        OverviewTemperatureReadingPolicy.ValidTemperature(
                            CpuTemperatureService.ReadGpuTelemetry().Celsius);
                }
                catch { _referenceSensorGpu = null; }
            }
            // The dispatcher may drop the odd (slow-sensor) sample and keep
            // only the following fast CPU sample. Carry the latest slow
            // generation with BOTH UI frames until actually painted.
            long gpuSensorGeneration = System.Threading.Volatile.Read(ref _latestGpuSensorGeneration);
            long diskSensorGeneration = System.Threading.Volatile.Read(ref _latestDiskSensorGeneration);
            // GPU/disk locals above are reused across worker iterations. Capture
            // the matching values per queued UI frame, not a mutable future
            // reading whose generation might belong to another frame.
            double? sampledGpu = gpu;
            double? sampledDisk = disk;
            bool refreshFastGauges = true;
            bool refreshOverviewChart = tick % 2 == 0;
            tick++;

            if (token.IsCancellationRequested) break;
            if (_windowTelemetryActive && !_liveMetricsDiagnosticSuppressUi &&
                (activeOverview || refreshFastGauges || gpuMetricsSample || diskMetricsSample))
            {
                QueueLiveMetricsUiUpdate(() =>
                {
                    long uiPhaseStart = OverviewUiPhaseAudit
                        ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    if (refreshFastGauges)
                    {
                        AnimateGauge(_cpuGauge, cpu, refreshOverviewChart);
                        AnimateGauge(_ramGauge, ram, refreshOverviewChart);
                        _latestOverviewRamSample = ramSample;
                        UpdateOverviewRamSummary(ramSample);
                    }
                    // On resume first show the explicit "loading" state
                    // rather than retaining stale GPU/disk gauge readings.
                    // Normal PDH cadence remains every fourth second.
                    // GPU and disk are SAMPLED only on odd worker ticks
                    // (each 4 seconds). The shared CPU/RAM chart repaint
                    // flag is true on even ticks, so using that flag here
                    // starves subsequent GPU/disk sparklines of every fresh
                    // sensor reading. Their own sensor cadence already caps
                    // native geometry writes at once per 4 seconds.
                    bool gpuNeedsPaint = ShouldPaintSlowSensorSample(
                        gpuSensorGeneration, _paintedGpuSensorGeneration,
                        firstActiveSample || (_gpuGauge?.Samples.Count == 0 &&
                            _gpuGauge?.MissingSamples == 0));
                    if (_gpuGauge != null && gpuNeedsPaint)
                    {
                        AnimateGauge(_gpuGauge, sampledGpu,
                            OverviewGaugeSparkDue(gpuNeedsPaint, refreshOverviewChart));
                        _paintedGpuSensorGeneration = gpuSensorGeneration;
                    }
                    bool diskNeedsPaint = ShouldPaintSlowSensorSample(
                        diskSensorGeneration, _paintedDiskSensorGeneration,
                        firstActiveSample || (_diskGauge?.Samples.Count == 0 &&
                            _diskGauge?.MissingSamples == 0));
                    if (_diskGauge != null && diskNeedsPaint)
                    {
                        AnimateGauge(_diskGauge, sampledDisk,
                            OverviewGaugeSparkDue(diskNeedsPaint, refreshOverviewChart));
                        _paintedDiskSensorGeneration = diskSensorGeneration;
                    }
                    long afterGauges = OverviewUiPhaseAudit
                        ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

                    if (activeOverview)
                    {
                        // The lower-bound search assumes chronological samples.
                        // Wall-clock correction after sleep/NTP can move backwards;
                        // discard the invalid time axis rather than showing stale
                        // future points or silently misplacing the cutoff.
                        if (_overviewPerfHistory.Count > 0 && sampleUtc < _overviewPerfHistory[^1].Utc)
                        {
                            _overviewPerfHistory.Clear();
                            _overviewNextHistoryPruneUtc = DateTime.MinValue;
                            _overviewChartLastRenderedSampleUtc = default;
                            _overviewChartLastRenderedRightEdgeUtc = default;
                            _overviewChartLastRenderedFirst = -1;
                        }
                        _overviewPerfHistory.Add(new OverviewPerfSample(sampleUtc, cpu, sampledGpu, ram, sampledDisk));
                        if (sampleUtc >= _overviewNextHistoryPruneUtc)
                        {
                            DateTime keepAfter = sampleUtc.AddMinutes(-65);
                            int expired = FindFirstOverviewSampleAtOrAfter(_overviewPerfHistory, keepAfter);
                            if (expired > 0) _overviewPerfHistory.RemoveRange(0, expired);
                            _overviewNextHistoryPruneUtc = sampleUtc.AddMinutes(1);
                        }
                        UpdateReferenceHeader(cpu, ram, sampledGpu, sampledDisk, refreshOverviewChart);
                        // The active reference Overview has its own live 60-second
                        // chart. Skip the detached legacy chart path entirely;
                        // if a legacy host is reintroduced, retain its cadence.
                        if (refreshOverviewChart && _overviewCpuChartLine != null)
                            UpdateOverviewPerformanceChart();
                        long afterCharts = OverviewUiPhaseAudit
                            ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

                        UpdateOverviewFooterReadouts(cpu, sampledGpu, ram, sampledDisk);
                        UpdateOverviewUptimeReadout();
                        if (networkRates.BaselineReset)
                            ResetOverviewNetworkHistory();
                        // The current Overview uses the reference network chart.
                        // Legacy strip/plot elements are null on that page;
                        // avoid filling unused rolling queues every four seconds.
                        if (_overviewNetworkStatusText != null ||
                            _overviewNetworkSpeedText != null ||
                            _overviewNetDownLine != null ||
                            _overviewNetUpLine != null)
                            UpdateOverviewNetworkVisuals(networkRates.DownMbps,
                                networkRates.UpMbps, networkRates.Online);
                        UpdateReferenceNetwork(networkRates.DownMbps, networkRates.UpMbps,
                            networkRates.Online, refreshOverviewChart, networkRates.AdapterLabel);
                        long afterNetwork = OverviewUiPhaseAudit
                            ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                        UpdateReferenceTemperature(_referenceSensorCpu, _referenceSensorGpu,
                            _referenceSensorBoard, _referenceSensorFanRpm);
                        if (OverviewUiPhaseAudit)
                        {
                            long afterTemperature = System.Diagnostics.Stopwatch.GetTimestamp();
                            double Ms(long a, long b) =>
                                System.Diagnostics.Stopwatch.GetElapsedTime(a, b).TotalMilliseconds;
                            LogService.Info("performance",
                                $"OVERVIEW_UI_PHASE gaugesMs={Ms(uiPhaseStart, afterGauges):F2} " +
                                $"chartsMs={Ms(afterGauges, afterCharts):F2} " +
                                $"networkMs={Ms(afterCharts, afterNetwork):F2} " +
                                $"temperatureMs={Ms(afterNetwork, afterTemperature):F2} " +
                                $"totalMs={Ms(uiPhaseStart, afterTemperature):F2}");
                        }
                    }
                }, token, sampleStartedTicks);
            }
            // Full telemetry stays responsive only while Overview is active. When the app loses
            // focus, keep a lightweight CPU/RAM snapshot at the relaxed cadence and avoid GPU,
            // disk, network and chart work until the window becomes active again.
            await Task.Delay(activeOverview ? 2000 : 10000, token).ConfigureAwait(false);
        }
    }

    // A blocked dispatcher or quick hide/show can leave stale samples queued.
    // Discard data whose measurement is older than the network/CPU sampling
    // freshness horizon rather than letting it repaint a recently reopened UI.
    internal static bool IsOverviewLiveSampleFresh(long sampleTicks, long nowTicks) =>
        sampleTicks > 0 && nowTicks >= sampleTicks &&
        System.Diagnostics.Stopwatch.GetElapsedTime(sampleTicks, nowTicks) <= TimeSpan.FromSeconds(8);

    // At most ONE queued dispatcher callback for Overview telemetry. A busy
    // WinUI thread must show the latest sample rather than replaying every
    // outdated 2-second reading when it becomes responsive again.
    private readonly object _liveMetricsUiGate = new();
    private readonly record struct QueuedLiveMetricsUiSample(
        Action Update, CancellationToken Token, long StartedTicks,
        int NavigationGeneration, int ActivationEpoch);
    private QueuedLiveMetricsUiSample? _latestLiveMetricsUiSample;
    private bool _liveMetricsUiEnqueued;
    private int _liveMetricsActivationEpoch;
    // Monotonic sample generations survive coalescing and worker restarts.
    // Updated only by the sensor worker, acknowledged only on the UI thread.
    private long _latestGpuSensorGeneration;
    private long _latestDiskSensorGeneration;
    private long _paintedGpuSensorGeneration;
    private long _paintedDiskSensorGeneration;

    internal static bool ShouldPaintSlowSensorSample(
        long sampledGeneration, long paintedGeneration, bool firstActiveFrame) =>
        firstActiveFrame || sampledGeneration != paintedGeneration;

    private void QueueLiveMetricsUiUpdate(
        Action update, CancellationToken token, long sampleStartedTicks)
    {
        if (token.IsCancellationRequested || _windowLifetimeCts.IsCancellationRequested)
            return;
        var sample = new QueuedLiveMetricsUiSample(
            update, token, sampleStartedTicks,
            System.Threading.Volatile.Read(ref _navigationGeneration),
            System.Threading.Volatile.Read(ref _liveMetricsActivationEpoch));
        lock (_liveMetricsUiGate)
        {
            _latestLiveMetricsUiSample = sample;
            if (_liveMetricsUiEnqueued) return;
            _liveMetricsUiEnqueued = true;
        }

        if (DispatcherQueue.TryEnqueue(DrainLatestLiveMetricsUiUpdate)) return;
        // Dispatcher disposed during shutdown: do not retain the last UI tree.
        lock (_liveMetricsUiGate)
        {
            _latestLiveMetricsUiSample = null;
            _liveMetricsUiEnqueued = false;
        }
    }

    private void DrainLatestLiveMetricsUiUpdate()
    {
        QueuedLiveMetricsUiSample? sample;
        lock (_liveMetricsUiGate)
        {
            sample = _latestLiveMetricsUiSample;
            _latestLiveMetricsUiSample = null;
            _liveMetricsUiEnqueued = false;
        }
        if (sample is not { } latest ||
            latest.Token.IsCancellationRequested ||
            _windowLifetimeCts.IsCancellationRequested ||
            !_windowTelemetryActive || _section != "overview" ||
            latest.NavigationGeneration != _navigationGeneration ||
            latest.ActivationEpoch != _liveMetricsActivationEpoch ||
            !IsOverviewLiveSampleFresh(
                latest.StartedTicks, System.Diagnostics.Stopwatch.GetTimestamp()))
            return;
        latest.Update();
    }

    private double? ReadCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            _lastCpuKernel = 0; // Do not compare with counters from a prior failed read.
            return null;
        }
        long i = idle.Value, k = kernel.Value, u = user.Value;
        if (_lastCpuKernel == 0)
        {
            _lastCpuIdle = i; _lastCpuKernel = k; _lastCpuUser = u;
            return null; // One counter snapshot is not a CPU utilization sample.
        }
        long idleDelta = i - _lastCpuIdle;
        long kernelDelta = k - _lastCpuKernel;
        long userDelta = u - _lastCpuUser;
        _lastCpuIdle = i; _lastCpuKernel = k; _lastCpuUser = u;
        return ComputeOverviewCpuPercent(idleDelta, kernelDelta, userDelta);
    }

    internal static double? ComputeOverviewCpuPercent(
        long idleDelta, long kernelDelta, long userDelta)
    {
        if (idleDelta < 0 || kernelDelta < 0 || userDelta < 0 ||
            kernelDelta > long.MaxValue - userDelta)
            return null;
        long totalDelta = kernelDelta + userDelta;
        if (totalDelta <= 0 || idleDelta > totalDelta)
            return null; // Rollback/reset/invalid native counters, not 0% load.
        return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private static (double? Percent, double UsedGb, double TotalGb) ReadRamSnapshot()
    {
        var status = new LiveMemoryStatus { Length = (uint)Marshal.SizeOf<LiveMemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
            return (null, 0, 0);
        return ComputeOverviewRamSnapshot(
            status.TotalPhys, status.AvailPhys, status.MemoryLoad);
    }

    internal static (double? Percent, double UsedGb, double TotalGb)
        ComputeOverviewRamSnapshot(ulong totalBytes, ulong availableBytes, uint memoryLoad)
    {
        if (totalBytes == 0 || availableBytes > totalBytes || memoryLoad > 100)
            return (null, 0, 0);
        const double gb = 1024d * 1024d * 1024d;
        return (memoryLoad, (totalBytes - availableBytes) / gb, totalBytes / gb);
    }

    private static double? ReadDiskActivity() => DiskPdhUsageService.TryRead();

    private static double? ReadGpuUsage() => GpuPdhUsageService.TryRead();

    [StructLayout(LayoutKind.Sequential)]
    private struct LiveFileTime
    {
        public uint Low;
        public uint High;
        public long Value => ((long)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LiveMemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out LiveFileTime idle, out LiveFileTime kernel, out LiveFileTime user);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref LiveMemoryStatus status);
    private FrameworkElement OverviewGlowIcon(string tag, double iconSize, SolidColorBrush accent)
    { return SectionIcon(tag, iconSize); }
    private static Brush? _overviewCardBackground;
    private static Brush? _overviewNeonBorder;

    private static Brush OverviewCardBackgroundBrush()
    { return _darkTheme ? (_overviewCardBackground ??= GlassPanelBrush()) : SurfaceElevatedBrush(); }
    private static Brush OverviewNeonBorderBrush()
    { return _darkTheme ? (_overviewNeonBorder ??= GlassEdgeBrush()) : BorderThemeBrush(); }

    private Border BuildOverviewPerformanceChart()
    {
        _overviewChartLastRenderedSampleUtc = default;
        _overviewChartLastRenderedRightEdgeUtc = default;
        _overviewChartLastRenderedFirst = -1;
        var root = new Grid { MinHeight = 132, Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var item in new[]
        {
            ("CPU", CpuMetricBrush()),
            ("GPU", GpuMetricBrush()),
            ("RAM", RamMetricBrush()),
            ("Disk", DiskMetricBrush())
        })
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            entry.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = item.Item2, VerticalAlignment = VerticalAlignment.Center });
            entry.Children.Add(new TextBlock { Text = item.Item1, FontSize = 10.5, Foreground = TextSecondaryBrush() });
            legend.Children.Add(entry);
        }
        root.Children.Add(legend);

        var canvas = new Canvas
        {
            Height = 96,
            Margin = new Thickness(0, 8, 0, 0),
            Background = UseGlassChrome ? GlassSurfaceLowBrush() : ThemeColor(35, 9, 12, 24)
        };
        string[] yLabels = { "100%", "75%", "50%", "25%", "0%" };
        for (int i = 0; i < yLabels.Length; i++)
        {
            double y = 10 + i * 17d;
            canvas.Children.Add(new Line
            {
                X1 = 38,
                X2 = 520,
                Y1 = y,
                Y2 = y,
                Stroke = ThemeColor(43, 52, 74),
                StrokeThickness = 1,
                Opacity = .65
            });
            var label = new TextBlock { Text = yLabels[i], FontSize = 10.5, Foreground = TextSecondaryBrush() };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 7);
            canvas.Children.Add(label);
        }

        string[] xLabels = { "60 s", "45 s", "30 s", "15 s", "0 s" };
        _overviewChartXLabels = new TextBlock[xLabels.Length];
        for (int i = 0; i < xLabels.Length; i++)
        {
            var label = new TextBlock { Text = xLabels[i], FontSize = 10.5, Foreground = TextSecondaryBrush() };
            _overviewChartXLabels[i] = label;
            Canvas.SetLeft(label, 38 + i * 118d);
            Canvas.SetTop(label, 80);
            canvas.Children.Add(label);
        }

        _overviewRamChartFill = new OverviewSegmentedChart(null,
            ThemeColor(28, RamMetricBrush().Color.R, RamMetricBrush().Color.G, RamMetricBrush().Color.B));
        canvas.Children.Add(_overviewRamChartFill.Visual);

        _overviewRamChartLine = new OverviewSegmentedChart(RamMetricBrush());
        _overviewCpuChartLine = new OverviewSegmentedChart(CpuMetricBrush());
        _overviewGpuChartLine = new OverviewSegmentedChart(GpuMetricBrush());
        _overviewDiskChartLine = new OverviewSegmentedChart(DiskMetricBrush());
        canvas.Children.Add(_overviewRamChartLine.Visual);
        canvas.Children.Add(_overviewCpuChartLine.Visual);
        canvas.Children.Add(_overviewGpuChartLine.Visual);
        canvas.Children.Add(_overviewDiskChartLine.Visual);
        UpdateOverviewPerformanceChart();

        Grid.SetRow(canvas, 1);
        root.Children.Add(canvas);

        return new Border
        {
            Background = UseGlassChrome ? OverviewLiquidSurface() : SurfaceAltBrush(),
            BorderBrush = UseGlassChrome ? OverviewLiquidEdge() : OverviewNeonBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = root
        };
    }

    private void SetOverviewRange(int minutes)
    {
        _overviewRangeMinutes = Math.Clamp(minutes, 1, 60);
        foreach (var item in _overviewRangePills)
        {
            bool active = item.Minutes == _overviewRangeMinutes;
            item.Pill.Background = active
                ? (UseGlassChrome ? GlassAccentTintBrush() : ButtonVioletBrush())
                : (UseGlassChrome ? OverviewLiquidInset() : SurfaceAltBrush());
            item.Pill.BorderBrush = active ? ButtonVioletBorderBrush() : (UseGlassChrome ? OverviewLiquidEdge() : BorderThemeBrush());
            if (item.Pill.Child is TextBlock text)
                text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
        }

        if (_overviewChartXLabels is { Length: 5 })
        {
            string[] labels = _overviewRangeMinutes switch
            {
                <= 1 => OverviewRangeLabels1m,
                5 => OverviewRangeLabels5m,
                15 => OverviewRangeLabels15m,
                _ => OverviewRangeLabels60m
            };
            for (int i = 0; i < labels.Length; i++) SetOverviewTextIfChanged(_overviewChartXLabels[i], labels[i]);
        }
        UpdateOverviewPerformanceChart();
    }

    private void UpdateOverviewPerformanceChart()
    {
        if (_overviewCpuChartLine == null || _overviewGpuChartLine == null ||
            _overviewRamChartLine == null || _overviewDiskChartLine == null || _overviewRamChartFill == null)
            return;

        DateTime rightEdge = DateTime.UtcNow;
        DateTime cutoff = rightEdge.AddMinutes(-_overviewRangeMinutes);
        int first = FindFirstOverviewSampleAtOrAfter(_overviewPerfHistory, cutoff);
        int sourceCount = _overviewPerfHistory.Count - first;
        if (sourceCount <= 0)
        {
            _overviewCpuChartLine.Reset();
            _overviewGpuChartLine.Reset();
            _overviewRamChartLine.Reset();
            _overviewDiskChartLine.Reset();
            _overviewRamChartFill.Reset();
            _overviewChartLastRenderedSampleUtc = default;
            _overviewChartLastRenderedRightEdgeUtc = default;
            return;
        }

        DateTime latestSample = _overviewPerfHistory[^1].Utc;
        if (IsOverviewChartRenderRedundant(latestSample, _overviewRangeMinutes, first, sourceCount,
                rightEdge, _overviewChartLastRenderedSampleUtc, _overviewChartLastRenderedRangeMinutes,
                _overviewChartLastRenderedFirst, _overviewChartLastRenderedSourceCount,
                _overviewChartLastRenderedRightEdgeUtc))
            return;

        // Use sample timestamps and preserve each bucket's extrema: a short
        // history must not stretch across 60 min or hide brief real spikes.
        RenderOverviewHistoryBatch(_overviewPerfHistory, first, cutoff, rightEdge,
            _overviewEnvelopeCursors, _overviewCpuChartLine, _overviewGpuChartLine,
            _overviewRamChartLine, _overviewDiskChartLine, _overviewRamChartFill);
        _overviewChartLastRenderedSampleUtc = latestSample;
        _overviewChartLastRenderedRightEdgeUtc = rightEdge;
        _overviewChartLastRenderedRangeMinutes = _overviewRangeMinutes;
        _overviewChartLastRenderedFirst = first;
        _overviewChartLastRenderedSourceCount = sourceCount;
    }

    private enum OverviewMetricKind { Cpu, Gpu, Ram, Disk }

    private static double? OverviewMetricValue(
        in OverviewPerfSample sample, OverviewMetricKind metric) => metric switch
    {
        OverviewMetricKind.Cpu => sample.Cpu,
        OverviewMetricKind.Gpu => sample.Gpu,
        OverviewMetricKind.Ram => sample.Ram,
        _ => sample.Disk
    };

    private static double OverviewHistoryTimeX(
        DateTime sampleUtc, DateTime cutoff, DateTime rightEdge)
    {
        double duration = (rightEdge - cutoff).TotalSeconds;
        if (duration <= 0) return 512;
        return 38 + 474 * Math.Clamp(
            (sampleUtc - cutoff).TotalSeconds / duration, 0, 1);
    }

    // A per-metric 96-column envelope retains real short peaks and minima,
    // unlike taking every Nth history sample. Missing native counters and
    // >8s pauses terminate independent native path figures. At steady load
    // there are at most two extrema/column (~192 points/metric) rather than
    // rebuilding thousands of native points on each 4-second UI refresh.
    private static int RenderOverviewMetricHistory(
        System.Collections.Generic.IReadOnlyList<OverviewPerfSample> history,
        int first, DateTime cutoff, DateTime rightEdge,
        OverviewMetricKind metric, OverviewSegmentedChart chart)
    {
        chart.Reset();
        const int columns = 96;
        int bucket = -1, lowIndex = -1, highIndex = -1;
        int lastIndex = -1, emittedIndex = -1;
        double low = 0, high = 0;
        DateTime previousValidUtc = default;
        int rendered = 0;

        void Emit(int index)
        {
            if (index < 0 || index == emittedIndex) return;
            OverviewPerfSample item = history[index];
            chart.Add(OverviewMetricValue(in item, metric),
                OverviewHistoryTimeX(item.Utc, cutoff, rightEdge));
            emittedIndex = index;
            rendered++;
        }

        void Flush(bool includeLast)
        {
            if (lowIndex < 0) return;
            if (lowIndex < highIndex)
            {
                Emit(lowIndex); Emit(highIndex);
            }
            else
            {
                Emit(highIndex); Emit(lowIndex);
            }
            if (includeLast) Emit(lastIndex);
            lowIndex = highIndex = lastIndex = -1;
        }

        for (int i = Math.Max(first, 0); i < history.Count; i++)
        {
            OverviewPerfSample sample = history[i];
            double? metricValue = OverviewMetricValue(in sample, metric);
            bool valid = metricValue is { } v && double.IsFinite(v);
            bool gap = previousValidUtc != default &&
                (sample.Utc <= previousValidUtc ||
                 sample.Utc - previousValidUtc > TimeSpan.FromSeconds(8));
            if (!valid || gap)
            {
                Flush(true);
                chart.FinishRun();
                bucket = -1;
                emittedIndex = -1;
                previousValidUtc = default;
            }
            if (!valid) continue;
            double value = metricValue!.Value;
            double x = OverviewHistoryTimeX(sample.Utc, cutoff, rightEdge);
            int nextBucket = Math.Clamp(
                (int)((x - 38) * columns / 474), 0, columns - 1);
            if (bucket != nextBucket)
            {
                Flush(false);
                bucket = nextBucket;
                lowIndex = highIndex = lastIndex = i;
                low = high = value;
                if (emittedIndex < 0) Emit(i);
            }
            else
            {
                if (value < low) { low = value; lowIndex = i; }
                if (value > high) { high = value; highIndex = i; }
                lastIndex = i;
            }
            previousValidUtc = sample.Utc;
        }
        Flush(true);
        chart.FinishRun();
        return rendered;
    }

    // Reusable per-metric cursor. Unlike the legacy five-pass traversal,
    // sample time, x and bucket are computed ONCE per history item.
    // RAM fill shares RAM's extrema instead of rescanning all samples.
    private sealed class OverviewEnvelopeCursor
    {
        private System.Collections.Generic.IReadOnlyList<OverviewPerfSample>? _history;
        private OverviewSegmentedChart? _line;
        private OverviewSegmentedChart? _fill;
        private OverviewMetricKind _metric;
        private DateTime _cutoff, _rightEdge, _previousValidUtc;
        private int _bucket, _lowIndex, _highIndex, _lastIndex, _emittedIndex;
        private double _low, _high;

        public void Begin(
            System.Collections.Generic.IReadOnlyList<OverviewPerfSample> history,
            DateTime cutoff, DateTime rightEdge, OverviewMetricKind metric,
            OverviewSegmentedChart line, OverviewSegmentedChart? fill = null)
        {
            _history = history;
            _metric = metric;
            _line = line;
            _fill = fill;
            _cutoff = cutoff;
            _rightEdge = rightEdge;
            _previousValidUtc = default;
            _bucket = _lowIndex = _highIndex = _lastIndex = _emittedIndex = -1;
            _low = _high = 0;
            line.Reset();
            fill?.Reset();
        }

        private void Emit(int index)
        {
            if (index < 0 || index == _emittedIndex || _history == null)
                return;
            OverviewPerfSample item = _history[index];
            double x = OverviewHistoryTimeX(item.Utc, _cutoff, _rightEdge);
            double? value = OverviewMetricValue(in item, _metric);
            _line?.Add(value, x);
            _fill?.Add(value, x);
            _emittedIndex = index;
        }

        private void Flush(bool includeLast)
        {
            if (_lowIndex < 0) return;
            if (_lowIndex < _highIndex)
            {
                Emit(_lowIndex); Emit(_highIndex);
            }
            else
            {
                Emit(_highIndex); Emit(_lowIndex);
            }
            if (includeLast) Emit(_lastIndex);
            _lowIndex = _highIndex = _lastIndex = -1;
        }

        public void Push(int index, in OverviewPerfSample sample,
            int nextBucket)
        {
            double? metricValue = OverviewMetricValue(in sample, _metric);
            bool valid = metricValue is { } v && double.IsFinite(v);
            bool gap = _previousValidUtc != default &&
                (sample.Utc <= _previousValidUtc ||
                 sample.Utc - _previousValidUtc > TimeSpan.FromSeconds(8));
            if (!valid || gap)
            {
                Flush(true);
                _line?.FinishRun();
                _fill?.FinishRun();
                _bucket = -1;
                _emittedIndex = -1;
                _previousValidUtc = default;
            }
            if (!valid) return;

            double value = metricValue!.Value;
            if (_bucket != nextBucket)
            {
                Flush(false);
                _bucket = nextBucket;
                _lowIndex = _highIndex = _lastIndex = index;
                _low = _high = value;
                if (_emittedIndex < 0) Emit(index);
            }
            else
            {
                if (value < _low) { _low = value; _lowIndex = index; }
                if (value > _high) { _high = value; _highIndex = index; }
                _lastIndex = index;
            }
            _previousValidUtc = sample.Utc;
        }

        public void End()
        {
            Flush(true);
            _line?.FinishRun();
            _fill?.FinishRun();
            // Do not retain a navigated-away UI tree through long-lived cursors.
            _line = null;
            _fill = null;
            _history = null;
        }
    }

    private readonly OverviewEnvelopeCursor[] _overviewEnvelopeCursors =
    {
        new(), new(), new(), new()
    };

    // One chronological history traversal for four series + RAM area.
    // Cursor state is pooled for the life of MainWindow; no per-refresh
    // LINQ enumerators, delegate captures, arrays or native figure churn.
    private static void RenderOverviewHistoryBatch(
        System.Collections.Generic.IReadOnlyList<OverviewPerfSample> history,
        int first, DateTime cutoff, DateTime rightEdge,
        OverviewEnvelopeCursor[] cursors,
        OverviewSegmentedChart cpu, OverviewSegmentedChart gpu,
        OverviewSegmentedChart ram, OverviewSegmentedChart disk,
        OverviewSegmentedChart ramFill)
    {
        const int columns = 96;
        cursors[0].Begin(history, cutoff, rightEdge, OverviewMetricKind.Cpu, cpu);
        cursors[1].Begin(history, cutoff, rightEdge, OverviewMetricKind.Gpu, gpu);
        cursors[2].Begin(history, cutoff, rightEdge, OverviewMetricKind.Ram, ram, ramFill);
        cursors[3].Begin(history, cutoff, rightEdge, OverviewMetricKind.Disk, disk);
        try
        {
            for (int i = Math.Max(first, 0); i < history.Count; i++)
            {
                OverviewPerfSample sample = history[i];
                double x = OverviewHistoryTimeX(sample.Utc, cutoff, rightEdge);
                int bucket = Math.Clamp(
                    (int)((x - 38) * columns / 474), 0, columns - 1);
                cursors[0].Push(i, in sample, bucket);
                cursors[1].Push(i, in sample, bucket);
                cursors[2].Push(i, in sample, bucket);
                cursors[3].Push(i, in sample, bucket);
            }
        }
        finally
        {
            for (int i = 0; i < cursors.Length; i++)
                cursors[i].End();
        }
    }


    internal static string RunOverviewHistoryBatchParitySmoke()
    {
        DateTime start = new(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc);
        DateTime end = start.AddMinutes(60);
        var history = new System.Collections.Generic.List<OverviewPerfSample>(1800);
        for (int i = 0; i < 1800; i++)
            history.Add(new OverviewPerfSample(start.AddSeconds(i * 2),
                i == 1041 ? 100 : i == 300 ? null : i == 1300 ? 0 : 25 + i % 10,
                i % 239 == 0 ? null : i % 70,
                i % 501 == 0 ? null : 40 + i % 15,
                i % 311 == 0 ? null : i % 90));

        var color = new SolidColorBrush(Microsoft.UI.Colors.Blue);
        var before = new OverviewSegmentedChart[5];
        var after = new OverviewSegmentedChart[5];
        for (int i = 0; i < 5; i++)
        {
            before[i] = new OverviewSegmentedChart(i == 4 ? null : color,
                i == 4 ? color : null);
            after[i] = new OverviewSegmentedChart(i == 4 ? null : color,
                i == 4 ? color : null);
        }
        var cursors = new[]
        {
            new OverviewEnvelopeCursor(), new OverviewEnvelopeCursor(),
            new OverviewEnvelopeCursor(), new OverviewEnvelopeCursor()
        };

        void Legacy()
        {
            RenderOverviewMetricHistory(history, 0, start, end, OverviewMetricKind.Cpu, before[0]);
            RenderOverviewMetricHistory(history, 0, start, end, OverviewMetricKind.Gpu, before[1]);
            RenderOverviewMetricHistory(history, 0, start, end, OverviewMetricKind.Ram, before[2]);
            RenderOverviewMetricHistory(history, 0, start, end, OverviewMetricKind.Disk, before[3]);
            RenderOverviewMetricHistory(history, 0, start, end, OverviewMetricKind.Ram, before[4]);
        }

        void Batch() => RenderOverviewHistoryBatch(history, 0, start, end, cursors,
            after[0], after[1], after[2], after[3], after[4]);

        Legacy();
        Batch();
        for (int i = 0; i < 5; i++)
        {
            if (before[i].SegmentCount != after[i].SegmentCount)
                throw new InvalidOperationException($"History batch segment count mismatch metric={i}.");
            for (int s = 0; s < before[i].SegmentCount; s++)
            {
                var a = before[i].GetSegment(s)!;
                var b = after[i].GetSegment(s)!;
                if (a.StartPoint.X != b.StartPoint.X || a.StartPoint.Y != b.StartPoint.Y ||
                    a.IsClosed != b.IsClosed ||
                    a.Segments.Count != b.Segments.Count)
                    throw new InvalidOperationException($"History batch figure mismatch metric={i}.");
                var aLine = (PolyLineSegment)a.Segments[0];
                var bLine = (PolyLineSegment)b.Segments[0];
                if (aLine.Points.Count != bLine.Points.Count)
                    throw new InvalidOperationException($"History batch points mismatch metric={i}.");
                for (int k = 0; k < aLine.Points.Count; k++)
                    if (aLine.Points[k].X != bLine.Points[k].X ||
                        aLine.Points[k].Y != bLine.Points[k].Y)
                        throw new InvalidOperationException(
                            $"History batch path coordinates differ metric={i} segment={s} point={k}.");
            }
        }

        for (int i = 0; i < 3; i++) { Legacy(); Batch(); }
        const int frames = 24;
        long oldAlloc = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < frames; i++) Legacy();
        double oldMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        oldAlloc = GC.GetAllocatedBytesForCurrentThread() - oldAlloc;

        long newAlloc = GC.GetAllocatedBytesForCurrentThread();
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < frames; i++) Batch();
        double newMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        newAlloc = GC.GetAllocatedBytesForCurrentThread() - newAlloc;
        // Native WinUI PointCollection writes dominate both variants and
        // allocation samples vary with UI dispatcher/GC state. Guard only
        // meaningful regressions (>20% plus 64 KiB), not <2% sampling noise.
        if (newAlloc > oldAlloc * 1.20 + 65_536)
            throw new InvalidOperationException(
                $"One-pass chart allocation regression oldAlloc={oldAlloc} newAlloc={newAlloc}.");
        return $"OVERVIEW_HISTORY_BATCH_PARITY_OK source=1800 metrics=5 pathsEqual=true " +
            $"oldMs={oldMs:F2} newMs={newMs:F2} oldAllocB={oldAlloc} newAllocB={newAlloc} " +
            "frames=24 oneHistoryScan=true RAMfillShared=true writes=0";
    }

    internal static string RunOverviewHistoryEnvelopeSmoke()
    {
        DateTime start = new(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc);
        DateTime right = start.AddMinutes(60);
        var chart = new OverviewSegmentedChart(
            new SolidColorBrush(Microsoft.UI.Colors.Blue));
        var history = new System.Collections.Generic.List<OverviewPerfSample>(1800);
        for (int i = 0; i < 1800; i++)
        {
            double? load = i == 1001 ? 100 : i == 1300 ? null : i == 1500 ? 0 : 30;
            history.Add(new OverviewPerfSample(start.AddSeconds(i * 2), load, 10, 40, 5));
        }
        int firstCount = RenderOverviewMetricHistory(
            history, 0, start, right, OverviewMetricKind.Cpu, chart);
        if (chart.SegmentCount != 2 || firstCount > 225 ||
            firstCount < 5 || chart.AllocatedSegments != 2)
            throw new InvalidOperationException("Long-range graph not bounded or gap skipped.");

        bool peakFound = false, trueZeroFound = false;
        for (int s = 0; s < chart.SegmentCount; s++)
        {
            var figure = chart.GetSegment(s)!;
            if (figure.StartPoint.Y <= 10.01) peakFound = true;
            if (figure.StartPoint.Y >= 77.99) trueZeroFound = true;
            foreach (var segment in figure.Segments)
            {
                if (segment is not PolyLineSegment line) continue;
                foreach (var point in line.Points)
                {
                    if (point.Y <= 10.01) peakFound = true;
                    if (point.Y >= 77.99) trueZeroFound = true;
                }
            }
        }
        if (!peakFound || !trueZeroFound)
            throw new InvalidOperationException("Sparse bucket downsampling lost spike or real 0%.");

        DateTime shortStart = right.AddSeconds(-4);
        if (OverviewHistoryTimeX(shortStart, start, right) < 511 ||
            Math.Abs(OverviewHistoryTimeX(start, start, right) - 38) > .001 ||
            Math.Abs(OverviewHistoryTimeX(right, start, right) - 512) > .001)
            throw new InvalidOperationException("Partial history stretched across selected time range.");

        int initialPool = chart.AllocatedSegments;
        for (int i = 0; i < 40; i++)
            RenderOverviewMetricHistory(
                history, 0, start, right, OverviewMetricKind.Cpu, chart);
        if (chart.AllocatedSegments != initialPool)
            throw new InvalidOperationException("History envelope reallocates native figures.");
        return "OVERVIEW_HISTORY_ENVELOPE_OK source=1800 bins=96 " +
            "realSpike=true trueZero=true missingGap=true timeAnchored=true " +
            "frames=40 figurePoolStable=true writes=0";
    }

    // A rare driver/PDH failure can alternate valid/missing readings for
    // hours. PathFigures beyond the bounded cache stay visible THIS frame
    // but are never retained in the pool afterward. Once the sensor recovers,
    // shrink old cached native figures after a grace interval.
    internal static string RunOverviewNativeFigureRetentionSmoke()
    {
        var chart = new OverviewSegmentedChart(
            new SolidColorBrush(Microsoft.UI.Colors.Blue));
        const int values = 600;
        for (int frame = 0; frame < 3; frame++)
        {
            chart.Reset();
            for (int i = 0; i < values; i++)
                chart.Add(i % 2 == 0 ? i % 100 : null, 38 + i * .1);
            chart.FinishRun();
            if (chart.SegmentCount != values / 2 ||
                chart.AllocatedSegments != 96 ||
                chart.GetSegment(values / 2 - 1) == null)
                throw new InvalidOperationException(
                    "Overview prolonged native sensor failure retained unbounded figures.");
        }
        for (int frame = 0; frame < 16; frame++)
        {
            chart.Reset();
            chart.Add(40, 38);
            chart.Add(41, 48);
            chart.FinishRun();
            if (chart.SegmentCount != 1 ||
                chart.GetSegment(0)?.StartPoint.X != 38)
                throw new InvalidOperationException("Overview figure pool shrank a live line.");
        }
        if (chart.AllocatedSegments != 24)
            throw new InvalidOperationException(
                $"Overview recovered native figure pool did not shrink: {chart.AllocatedSegments}.");
        chart.Reset();
        if (chart.SegmentCount != 0)
            throw new InvalidOperationException("Overview stale figures survived history reset.");
        return "OVERVIEW_NATIVE_FIGURE_RETENTION_OK stormRuns=300 " +
            "retainedMax=96 recoveredRetained=24 sparseFrames=16 " +
            "livePathPreserved=true emptyCleared=true writes=0";
    }


    internal static string RunOverviewSegmentedChartSmoke()
    {
        var blue = new SolidColorBrush(Microsoft.UI.Colors.Blue);
        var cpu = new OverviewSegmentedChart(blue);
        var fill = new OverviewSegmentedChart(null, blue);
        double?[] readings = { null, 25, 50, null, 0, 75, null, null, 100 };
        for (int i = 0; i < readings.Length; i++)
        {
            cpu.Add(readings[i], 38 + i * 10);
            fill.Add(readings[i], 38 + i * 10);
        }
        cpu.FinishRun();
        fill.FinishRun();
        if (cpu.SegmentCount != 3 || fill.SegmentCount != 3 ||
            cpu.AllocatedSegments != 3 || fill.AllocatedSegments != 3 ||
            cpu.GetSegment(0)?.StartPoint.X != 48 ||
            cpu.GetSegment(1)?.StartPoint.X != 78 ||
            cpu.GetSegment(2)?.StartPoint.X != 118 ||
            fill.GetSegment(0)?.StartPoint.Y != 78 ||
            fill.GetSegment(1)?.StartPoint.Y != 78 ||
            fill.GetSegment(2)?.IsClosed != true)
            throw new InvalidOperationException("Missing telemetry bridged a graphical gap.");
        var first = cpu.GetSegment(0);
        var firstFill = fill.GetSegment(0);
        for (int frame = 0; frame < 120; frame++)
        {
            cpu.Reset();
            fill.Reset();
            for (int i = 0; i < readings.Length; i++)
            {
                double? value = frame % 2 == 0 ? readings[i] : i % 3 == 0 ? null : i * 9d;
                cpu.Add(value, 38 + i * 10);
                fill.Add(value, 38 + i * 10);
            }
            cpu.FinishRun();
            fill.FinishRun();
        }
        if (cpu.SegmentCount != 3 || fill.SegmentCount != 3 ||
            cpu.AllocatedSegments != 3 || fill.AllocatedSegments != 3 ||
            !ReferenceEquals(first, cpu.GetSegment(0)) ||
            !ReferenceEquals(firstFill, fill.GetSegment(0)))
            throw new InvalidOperationException("Segmented chart leaked/recreated native run figures.");
        cpu.Reset();
        fill.Reset();
        if (cpu.SegmentCount != 0 || fill.SegmentCount != 0)
            throw new InvalidOperationException("Expired chart retained stale plotted lines.");
        return "OVERVIEW_SEGMENTED_CHART_OK gaps=3 fillIsolated=true " +
            "frames=120 poolStable=3 singleton=true expiredCleared=true writes=0";
    }

    private static bool AppendOverviewOptionalMetricPoint(
        PointCollection points, double? percent, double x)
    {
        if (percent is not { } value || !double.IsFinite(value))
            return false;
        points.Add(new Windows.Foundation.Point(
            x, 78 - Math.Clamp(value, 0, 100) * .68));
        return true;
    }

    // Verifies that missing native samples remain missing through history and
    // actual detached WinUI chart points. No hardware probes or OS writes.
    internal static string RunOverviewOptionalTelemetrySmoke()
    {
        var cpu = new PointCollection();
        var gpu = new PointCollection();
        var disk = new PointCollection();
        DateTime now = new(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc);
        var unavailable = new OverviewPerfSample(now, null, null, 50, null);
        var idle = new OverviewPerfSample(now.AddSeconds(2), 0, 0, 50, 0);
        var loaded = new OverviewPerfSample(now.AddSeconds(4), 50, 75, 50, 40);
        if (AppendOverviewOptionalMetricPoint(cpu, unavailable.Cpu, 38) ||
            AppendOverviewOptionalMetricPoint(gpu, unavailable.Gpu, 38) ||
            AppendOverviewOptionalMetricPoint(disk, unavailable.Disk, 38) ||
            cpu.Count != 0 || gpu.Count != 0 || disk.Count != 0 ||
            !AppendOverviewOptionalMetricPoint(cpu, idle.Cpu, 138) ||
            !AppendOverviewOptionalMetricPoint(gpu, idle.Gpu, 138) ||
            !AppendOverviewOptionalMetricPoint(disk, idle.Disk, 138) ||
            !AppendOverviewOptionalMetricPoint(cpu, loaded.Cpu, 238) ||
            !AppendOverviewOptionalMetricPoint(gpu, loaded.Gpu, 238) ||
            !AppendOverviewOptionalMetricPoint(disk, loaded.Disk, 238) ||
            cpu.Count != 2 || gpu.Count != 2 || disk.Count != 2 ||
            Math.Abs(cpu[0].Y - 78) > .001 ||
            Math.Abs(gpu[1].Y - 27) > .001 ||
            Math.Abs(disk[1].Y - 50.8) > .001 ||
            AppendOverviewOptionalMetricPoint(gpu, double.NaN, 338) ||
            AppendOverviewOptionalMetricPoint(disk, double.PositiveInfinity, 338) ||
            gpu.Count != 2 || disk.Count != 2)
            throw new InvalidOperationException(
                "Overview missing telemetry rendered as a false 0% graph dip.");

        // In kernel32 GetSystemTimes, kernelDelta includes idleDelta.
        if (ComputeOverviewCpuPercent(500, 750, 250) != 50 ||
            ComputeOverviewCpuPercent(0, 500, 500) != 100 ||
            ComputeOverviewCpuPercent(1000, 1000, 0) != 0 ||
            ComputeOverviewCpuPercent(1100, 1000, 0) != null ||
            ComputeOverviewCpuPercent(-1, 500, 500) != null ||
            ComputeOverviewCpuPercent(100, -1, 500) != null ||
            ComputeOverviewCpuPercent(100, 500, -1) != null ||
            ComputeOverviewCpuPercent(0, 0, 0) != null ||
            ComputeOverviewCpuPercent(0, long.MaxValue, 1) != null)
            throw new InvalidOperationException("CPU idle/native counter validity regression.");
        return "OVERVIEW_OPTIONAL_TELEMETRY_OK missingCpuGpuDisk=true " +
            "trueIdleZero=true recovery=true invalidFinite=true " +
            "counterRollback=true counterOverflow=true writes=0";
    }

    // Read-only synthetic native-memory edge cases, detached WinUI data.
    // Never allocates physical RAM, changes Windows settings or forces GC.
    internal static string RunOverviewRamValiditySmoke()
    {
        const ulong gib = 1024UL * 1024 * 1024;
        var normal = ComputeOverviewRamSnapshot(8 * gib, 3 * gib, 63);
        var empty = ComputeOverviewRamSnapshot(8 * gib, 8 * gib, 0);
        var full = ComputeOverviewRamSnapshot(8 * gib, 0, 100);
        var failed = ComputeOverviewRamSnapshot(0, 0, 0);
        var invalidAvailable = ComputeOverviewRamSnapshot(8 * gib, 9 * gib, 50);
        var invalidLoad = ComputeOverviewRamSnapshot(8 * gib, 4 * gib, 101);
        var huge = ComputeOverviewRamSnapshot(ulong.MaxValue, ulong.MaxValue, 0);
        if (normal.Percent != 63 || normal.UsedGb != 5 ||
            normal.TotalGb != 8 ||
            empty.Percent != 0 || empty.UsedGb != 0 ||
            full.Percent != 100 || full.UsedGb != 8 ||
            failed.Percent != null || invalidAvailable.Percent != null ||
            invalidLoad.Percent != null || huge.Percent != 0 ||
            !double.IsFinite(huge.TotalGb) ||
            !FormatOverviewRamSummary(failed).Contains("nedostupn", StringComparison.Ordinal) ||
            !FormatOverviewRamSummary(normal).Contains("63%", StringComparison.Ordinal))
            throw new InvalidOperationException("RAM native snapshot validity/summary regression.");

        var ramPoints = new PointCollection();
        if (AppendOverviewOptionalMetricPoint(ramPoints, failed.Percent, 38) ||
            !AppendOverviewOptionalMetricPoint(ramPoints, empty.Percent, 138) ||
            !AppendOverviewOptionalMetricPoint(ramPoints, normal.Percent, 238) ||
            ramPoints.Count != 2 ||
            Math.Abs(ramPoints[0].Y - 78) > 0.001 ||
            Math.Abs(ramPoints[1].Y - 35.16) > 0.001)
            throw new InvalidOperationException("RAM missing/true-zero history graph regression.");
        return "OVERVIEW_RAM_VALIDITY_OK cases=12 unavailableNotZero=true " +
            "nativeValidation=true trueZero=true full=true hugeSafe=true " +
            "summaryExplicit=true chartRecovered=true writes=0";
    }

    // History is chronological (clock rollback resets it on ingestion).
    // The 60-minute chart keeps ~1,800 samples: lower-bound is ~11 timestamp
    // comparisons rather than scanning most of the list every 4 seconds.
    private static int FindFirstOverviewSampleAtOrAfter(
        System.Collections.Generic.IReadOnlyList<OverviewPerfSample> samples, DateTime cutoff)
    {
        int lo = 0, hi = samples.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (samples[mid].Utc < cutoff) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // Historical X coordinates are anchored to NOW, not just the latest
    // telemetry sample. If readings pause or a range is reselected, shift
    // the geometry after one horizontal pixel of elapsed window time.
    // This avoids stale screen coordinates without 4-second redraw churn at 60m.
    private static bool IsOverviewChartRenderRedundant(
        DateTime latestUtc, int rangeMinutes, int first, int count,
        DateTime rightEdgeUtc, DateTime renderedUtc, int renderedRange,
        int renderedFirst, int renderedCount, DateTime renderedRightEdgeUtc)
    {
        if (latestUtc != renderedUtc || rangeMinutes != renderedRange ||
            first != renderedFirst || count != renderedCount ||
            renderedRightEdgeUtc == default || rightEdgeUtc < renderedRightEdgeUtc)
            return false;
        long onePixelTicks = Math.Max(1, TimeSpan.FromMinutes(rangeMinutes).Ticks / 474);
        return rightEdgeUtc.Ticks - renderedRightEdgeUtc.Ticks < onePixelTicks;
    }

    internal static string RunOverviewHistoryLookupBenchmark()
    {
        DateTime first = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
        const int points = 1950;
        const int iterations = 4800;
        var history = new System.Collections.Generic.List<OverviewPerfSample>(points);
        for (int i = 0; i < points; i++)
            history.Add(new OverviewPerfSample(first.AddSeconds(i * 2), i % 100, null, 42, 0));
        static int Linear(System.Collections.Generic.IReadOnlyList<OverviewPerfSample> list, DateTime cutoff)
        {
            int i = 0;
            while (i < list.Count && list[i].Utc < cutoff) i++;
            return i;
        }

        if (FindFirstOverviewSampleAtOrAfter(Array.Empty<OverviewPerfSample>(), first) != 0)
            throw new InvalidOperationException("Overview empty history lower-bound regression.");
        for (int i = -3; i < points + 3; i++)
        {
            DateTime cutoff = first.AddSeconds(i * 2);
            if (Linear(history, cutoff) != FindFirstOverviewSampleAtOrAfter(history, cutoff))
                throw new InvalidOperationException("Overview time cutoff does not match prior linear scan.");
        }
        for (int i = 0; i < 15; i++)
        {
            var cutoff = first.AddSeconds((i * 251) % 4100);
            _ = Linear(history, cutoff);
            _ = FindFirstOverviewSampleAtOrAfter(history, cutoff);
        }
        DateTime latest = history[^1].Utc;
        int checksumOld = 0, checksumNew = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            int range = i % 4 switch { 0 => 1, 1 => 5, 2 => 15, _ => 60 };
            checksumOld += Linear(history, latest.AddMinutes(-range));
        }
        double oldMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            int range = i % 4 switch { 0 => 1, 1 => 5, 2 => 15, _ => 60 };
            checksumNew += FindFirstOverviewSampleAtOrAfter(history, latest.AddMinutes(-range));
        }
        double newMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long newBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        if (checksumOld != checksumNew || oldBytes > 1024 || newBytes > 1024)
            throw new InvalidOperationException("Overview binary cutoff work or allocations regressed.");
        return $"OVERVIEW_HISTORY_CUTOFF_BENCH_OK points={points} queries={iterations} " +
            $"linearMs={oldMs:F3} binaryMs={newMs:F3} " +
            $"linearAllocB={oldBytes} binaryAllocB={newBytes} sameResults=true";
    }

    internal static string RunOverviewChartRenderPolicySmoke()
    {
        DateTime time = new(2026, 10, 9, 4, 0, 0, DateTimeKind.Utc);
        bool Skip(DateTime latest, int range, int first, int count,
            DateTime rightEdge, DateTime lastRenderTime = default,
            DateTime lastRightEdge = default) =>
            IsOverviewChartRenderRedundant(latest, range, first, count, rightEdge,
                lastRenderTime == default ? time : lastRenderTime, 5, 1, 48,
                lastRightEdge == default ? time : lastRightEdge);

        TimeSpan pixel5m = TimeSpan.FromTicks(TimeSpan.FromMinutes(5).Ticks / 474);
        TimeSpan pixel60m = TimeSpan.FromTicks(TimeSpan.FromMinutes(60).Ticks / 474);
        if (!Skip(time, 5, 1, 48, time) ||
            !Skip(time, 5, 1, 48, time.AddTicks(pixel5m.Ticks - 1)) ||
            Skip(time, 5, 1, 48, time.AddTicks(pixel5m.Ticks)) ||
            Skip(time, 5, 1, 48, time.AddSeconds(-2)) ||
            Skip(time.AddSeconds(2), 5, 1, 48, time) ||
            Skip(time, 15, 1, 48, time) ||
            Skip(time, 5, 2, 48, time) ||
            Skip(time, 5, 1, 47, time))
            throw new InvalidOperationException("Overview chart ignored a changed window, sample or time axis.");
        // 60m is intentionally coarser: old geometry is reused until one
        // 474-column pixel has passed without any new sensor readings.
        if (!IsOverviewChartRenderRedundant(time, 60, 1, 48,
                time.AddTicks(pixel60m.Ticks - 1), time, 60, 1, 48, time) ||
            IsOverviewChartRenderRedundant(time, 60, 1, 48,
                time.AddTicks(pixel60m.Ticks), time, 60, 1, 48, time) ||
            IsOverviewChartRenderRedundant(time, 60, 1, 48,
                time, time, 60, 1, 48, default))
            throw new InvalidOperationException("Overview 60m horizontal-pixel refresh policy regressed.");

        // Native WinUI regression: same sensor readings must move to the left
        // when the displayed 5-minute clock window moves, preserving height.
        var history = new System.Collections.Generic.List<OverviewPerfSample>
        {
            new(time.AddSeconds(-60), 32, 10, 40, 12),
            new(time.AddSeconds(-58), 100, 15, 50, 14)
        };
        var probeCharts = new OverviewSegmentedChart[]
        {
            new(new SolidColorBrush(Microsoft.UI.Colors.Blue)),
            new(new SolidColorBrush(Microsoft.UI.Colors.Blue)),
            new(new SolidColorBrush(Microsoft.UI.Colors.Blue)),
            new(new SolidColorBrush(Microsoft.UI.Colors.Blue)),
            new(null, new SolidColorBrush(Microsoft.UI.Colors.Blue))
        };
        var cursors = new OverviewEnvelopeCursor[]
        {
            new(), new(), new(), new()
        };
        void RenderClockProbe(DateTime now) =>
            RenderOverviewHistoryBatch(history, 0, now.AddMinutes(-5), now,
                cursors, probeCharts[0], probeCharts[1], probeCharts[2],
                probeCharts[3], probeCharts[4]);
        RenderClockProbe(time);
        if (probeCharts[0].SegmentCount != 1 ||
            probeCharts[2].SegmentCount != 1 ||
            probeCharts[4].SegmentCount != 1)
            throw new InvalidOperationException("Chart clock regression broke contiguous sensor runs.");
        var original = probeCharts[0].GetSegment(0)!;
        double originalX = original.StartPoint.X;
        double originalY = original.StartPoint.Y;
        RenderClockProbe(time.AddSeconds(8));
        var shifted = probeCharts[0].GetSegment(0)!;
        double deltaX = originalX - shifted.StartPoint.X;
        if (probeCharts[0].SegmentCount != 1 ||
            Math.Abs(deltaX - 474d * 8 / 300) > .001 ||
            shifted.StartPoint.Y != originalY ||
            probeCharts[4].SegmentCount != 1)
            throw new InvalidOperationException("Historical native geometry froze on unchanged sensor readings.");

        // Pure hotpath probe. Per-call predicate overhead is timed and
        // allocated bytes measured independently of WinUI native rendering.
        const int samples = 32768;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int redraws = 0;
        for (int i = 0; i < samples; i++)
            if (!IsOverviewChartRenderRedundant(time, 60, 1, 48,
                    time.AddSeconds(i % 12), time, 60, 1, 48, time))
                redraws++;
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (redraws <= 0 || redraws >= samples || allocated != 0)
            throw new InvalidOperationException("Overview clock-shift predicate introduced allocation or skipped all redraws.");
        return $"OVERVIEW_CHART_RENDER_POLICY_SMOKE_OK samples={samples} elapsedMs={elapsedMs:F3} allocatedB={allocated} " +
            "duplicate=skipped rolling=redraw range=redraw expired=redraw 5mPixel=true 60mPixel=true nativePathShift=true clockRollback=true";
    }
    private static bool SetOverviewChartPoint(PointCollection points, int index, Windows.Foundation.Point next)
    {
        if (index >= points.Count)
        {
            points.Add(next);
            return true;
        }

        Windows.Foundation.Point current = points[index];
        if (current.X == next.X && current.Y == next.Y)
            return false;

        points[index] = next;
        return true;
    }

    private static void TrimOverviewChartPoints(PointCollection points, int count)
    {
        while (points.Count > count)
            points.RemoveAt(points.Count - 1);
    }

    internal static string RunOverviewChartPointReuseSmoke()
    {
        var points = new PointCollection();
        var first = new Windows.Foundation.Point(38, 78);
        var last = new Windows.Foundation.Point(512, 65);
        if (!SetOverviewChartPoint(points, 0, first) || !SetOverviewChartPoint(points, 1, last))
            throw new InvalidOperationException("Overview chart new points were not added.");
        if (SetOverviewChartPoint(points, 0, first) || SetOverviewChartPoint(points, 1, last))
            throw new InvalidOperationException("Overview chart unchanged points caused redundant WinUI writes.");
        if (points.Count != 2 || points[0].X != 38 || points[1].X != 512 || points[1].Y != 65)
            throw new InvalidOperationException("Overview chart steady-state points were not retained.");

        if (!SetOverviewChartPoint(points, 1, new Windows.Foundation.Point(512, 30)))
            throw new InvalidOperationException("Overview chart changing a point must write the new value.");
        if (points[1].Y != 30)
            throw new InvalidOperationException("Overview chart changed point was not updated.");
        TrimOverviewChartPoints(points, 1);
        if (points.Count != 1 || points[0].X != 38)
            throw new InvalidOperationException("Overview chart shrinking sample count retained stale points.");

        return "OVERVIEW_CHART_POINT_REUSE_SMOKE_OK retained=2 updated=1 trimmed=1";
    }

    // Detached WinUI collections: isolates point write overhead, not a claim about visible FPS.
    internal static string RunOverviewPointCollectionBenchmark()
    {
        static (double Milliseconds, long Bytes) Measure(bool reuse, bool rolling)
        {
            const int frames = 120;
            const int pointsPerSeries = 48;
            const int seriesCount = 5;
            var series = new PointCollection[seriesCount];
            for (int j = 0; j < series.Length; j++) series[j] = new PointCollection();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int frame = 0; frame < frames; frame++)
            {
                for (int j = 0; j < series.Length; j++)
                {
                    PointCollection points = series[j];
                    if (!reuse) points.Clear();
                    for (int i = 0; i < pointsPerSeries; i++)
                    {
                        int sample = rolling ? (i + frame) % 47 : i % 47;
                        var next = new Windows.Foundation.Point(38 + i * 10, 78 - sample - j);
                        if (reuse) SetOverviewChartPoint(points, i, next);
                        else points.Add(next);
                    }
                }
            }
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            if (series.Any(x => x.Count != pointsPerSeries))
                throw new InvalidOperationException("Chart benchmark generated incomplete data.");
            return (ticks * 1000d / System.Diagnostics.Stopwatch.Frequency, allocated);
        }

        // Warm up both code paths before reporting results.
        _ = Measure(false, false);
        _ = Measure(true, false);
        var legacySteady = Measure(false, false);
        var reuseSteady = Measure(true, false);
        var legacyRolling = Measure(false, true);
        var reuseRolling = Measure(true, true);
        return $"OVERVIEW_POINT_COLLECTION_BENCH_OK mode=detached-winui frames=120 series=5 points=48 " +
               $"baselineSteadyMs={legacySteady.Milliseconds:0.###} reuseSteadyMs={reuseSteady.Milliseconds:0.###} " +
               $"baselineRollingMs={legacyRolling.Milliseconds:0.###} reuseRollingMs={reuseRolling.Milliseconds:0.###} " +
               $"baselineSteadyBytes={legacySteady.Bytes} reuseSteadyBytes={reuseSteady.Bytes} " +
               $"baselineRollingBytes={legacyRolling.Bytes} reuseRollingBytes={reuseRolling.Bytes}";
    }
    private static string FormatRate(double mbps)
        => mbps >= 100 ? $"{mbps:0} Mb/s" : mbps >= 10 ? $"{mbps:0.0} Mb/s" : $"{mbps:0.00} Mb/s";

    private (double DownMbps, double UpMbps, bool Online, bool BaselineReset, string AdapterLabel) ReadNetworkRates()
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            bool adapterChanged = false;
            if (_overviewNetInterfaces == null || now >= _overviewNetInterfaceRefreshUtc ||
                now < _overviewPrevNetUtc)
            {
                var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                                  nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Tunnel &&
                                  nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                                  nic.Speed > 0)
                    .ToArray();
                // Only compute topology identity at the already existing 10s
                // refresh; NIC order may change without a topology change.
                string identity = NetworkAdapterIdentity(interfaces.Select(nic => nic.Id));
                adapterChanged = _overviewNetInterfaces != null &&
                    !string.Equals(_overviewNetInterfaceIdentity, identity,
                        StringComparison.OrdinalIgnoreCase);
                _overviewNetInterfaces = interfaces;
                _overviewNetInterfaceIdentity = identity;
                _overviewNetInterfaceRefreshUtc = now.AddSeconds(10);
            }

            long rx = 0, tx = 0;
            bool online = false;
            int measuredAdapterCount = 0;
            string? measuredAdapterName = null;
            _overviewSampleNicIds.Clear();
            foreach (var nic in _overviewNetInterfaces)
            {
                // A cached adapter can go down before the 10-second topology
                // refresh. Do not treat its old byte counters as live traffic,
                // and force a normal enumeration on the next active tick.
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    adapterChanged = true;
                    _overviewNetInterfaceRefreshUtc = now;
                    continue;
                }
                try
                {
                    var stats = nic.GetIPv4Statistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                    online = true;
                    measuredAdapterCount++;
                    measuredAdapterName ??= nic.Name;
                    _overviewSampleNicIds.Add(nic.Id);
                }
                catch (System.Net.NetworkInformation.NetworkInformationException)
                {
                    // Exclude this NIC from this sample and re-discover on the
                    // next tick. The successful-NIC membership below forces
                    // a fresh baseline on both failure and recovery.
                    _overviewNetInterfaceRefreshUtc = now;
                }
            }

            bool measuredSetChanged = !SameNetworkAdapterSet(
                _overviewMeasuredNicIds, _overviewSampleNicIds);
            if (measuredSetChanged)
                _overviewMeasuredNicIds = _overviewSampleNicIds.ToArray();
            bool reset = ShouldResetNetworkRateBaseline(
                _overviewPrevNetUtc, now, _overviewPrevRxBytes,
                _overviewPrevTxBytes, rx, tx, online,
                adapterChanged || measuredSetChanged);
            double seconds = Math.Max(.1, (now - _overviewPrevNetUtc).TotalSeconds);
            double down = reset ? 0 : (rx - _overviewPrevRxBytes) * 8d / seconds / 1_000_000d;
            double up = reset ? 0 : (tx - _overviewPrevTxBytes) * 8d / seconds / 1_000_000d;
            _overviewPrevNetUtc = now;
            _overviewPrevRxBytes = rx;
            _overviewPrevTxBytes = tx;
            return (down, up, online, reset,
                FormatOverviewNetworkAdapterLabel(measuredAdapterCount, measuredAdapterName));
        }
        catch
        {
            // The next successful NIC read must establish a fresh baseline;
            // otherwise a temporary query failure can yield a false traffic spike.
            _overviewPrevNetUtc = default;
            _overviewNetInterfaceRefreshUtc = default;
            _overviewMeasuredNicIds = null;
            return (0, 0, false, true, "Síť odpojena");
        }
    }

    // Display a label from the same successful-NIC sample as the total rates.
    // Multiple adapters must not be misidentified as the first enumerated NIC.
    internal static string FormatOverviewNetworkAdapterLabel(int count, string? singleName) =>
        count <= 0 ? "Síť odpojena" : count == 1
            ? (string.IsNullOrWhiteSpace(singleName) ? "Připojení" : singleName)
            : count < 5 ? $"{count} aktivní rozhraní" : $"{count} aktivních rozhraní";

    private static string NetworkAdapterIdentity(System.Collections.Generic.IEnumerable<string> adapterIds) =>
        string.Join("|", adapterIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));

    // Membership ignores provider order/casing. No LINQ iterator, string
    // join or array allocation on the frequent unchanged 2-second path.
    internal static bool SameNetworkAdapterSet(
        System.Collections.Generic.IReadOnlyList<string>? previous,
        System.Collections.Generic.IReadOnlyList<string> current)
    {
        if (previous == null || previous.Count != current.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            int beforeCount = 0, afterCount = 0;
            for (int j = 0; j < current.Count; j++)
            {
                if (string.Equals(current[i], previous[j], StringComparison.OrdinalIgnoreCase))
                    beforeCount++;
                if (string.Equals(current[i], current[j], StringComparison.OrdinalIgnoreCase))
                    afterCount++;
            }
            if (beforeCount != afterCount) return false;
        }
        return true;
    }

    internal static bool ShouldResetNetworkRateBaseline(
        DateTime previousUtc, DateTime nowUtc, long previousRx, long previousTx,
        long currentRx, long currentTx, bool online, bool adapterChanged) =>
        previousUtc == default || nowUtc <= previousUtc ||
        nowUtc - previousUtc > TimeSpan.FromSeconds(8) || !online ||
        adapterChanged || currentRx < previousRx || currentTx < previousTx;

    // Read-only regression for switched/vanished NICs and reset byte counters.
    // No network changes, actual adapter enumeration or filesystem writes.
    internal static string RunOverviewNetworkBaselineSmoke()
    {
        DateTime now = new(2026, 10, 12, 17, 0, 0, DateTimeKind.Utc);
        DateTime previous = now.AddSeconds(-2);
        bool Check(long priorRx, long priorTx, long currentRx, long currentTx,
            bool online = true, bool changed = false,
            DateTime? oldTime = null, DateTime? currentTime = null) =>
            ShouldResetNetworkRateBaseline(
                oldTime ?? previous, currentTime ?? now,
                priorRx, priorTx, currentRx, currentTx, online, changed);

        if (Check(1000, 2000, 3000, 4000) ||
            Check(1000, 2000, 1000, 2000) ||
            !Check(1000, 2000, 3000, 4000, changed: true) ||
            !Check(1000, 2000, 3000, 4000, online: false) ||
            !Check(1000, 2000, 900, 4000) ||
            !Check(1000, 2000, 3000, 1900) ||
            !Check(1000, 2000, 3000, 4000, oldTime: default(DateTime)) ||
            !Check(1000, 2000, 3000, 4000, currentTime: previous) ||
            !Check(1000, 2000, 3000, 4000, currentTime: previous.AddSeconds(-1)) ||
            !Check(1000, 2000, 3000, 4000, oldTime: now.AddSeconds(-30)) ||
            !Check(1000, 2000, 3000, 4000, currentTime: previous.AddSeconds(9)) ||
            Check(1000, 2000, 3000, 4000, currentTime: previous.AddSeconds(8)) ||
            !string.Equals(NetworkAdapterIdentity(new[] { "eth", "wifi" }),
                NetworkAdapterIdentity(new[] { "WIFI", "ETH" }),
                StringComparison.OrdinalIgnoreCase) )
            throw new InvalidOperationException("Overview network baseline/topology regression.");

        // A reset returns (0,0) for one sample, then the next 2s delta
        // is measured against the new interface's captured counters.
        long received = 800_000_000, sent = 500_000_000;
        if (!Check(5_000_000_000, 7_000_000_000, received, sent,
                changed: true) ||
            Check(received, sent, received + 250_000, sent + 100_000))
            throw new InvalidOperationException("Overview adapter-reconnect spike regression.");
        double downstreamMbps = 250_000 * 8d / 2d / 1_000_000d;
        if (Math.Abs(downstreamMbps - 1d) > 0.000001)
            throw new InvalidOperationException("Overview network post-reset rate regression.");
        return "OVERVIEW_NETWORK_BASELINE_RESET_OK cases=16 " +
            "reconnect=true topologyOrderIndependent=true counterReset=true " +
            "offline=true clockCorrection=true pauseResume=true " +
            "deltaResumed=true writes=0";
    }

    // Read-only continuation regression: a failed NIC must not contaminate
    // the byte totals for surviving NICs or produce an enormous reconnection
    // spike; a busy dispatcher must not paint a stale telemetry sample.
    internal static string RunOverviewNetworkContinuitySmoke()
    {
        if (FormatOverviewNetworkAdapterLabel(0, null) != "Síť odpojena" ||
            FormatOverviewNetworkAdapterLabel(1, "Ethernet 2") != "Ethernet 2" ||
            FormatOverviewNetworkAdapterLabel(1, null) != "Připojení" ||
            FormatOverviewNetworkAdapterLabel(2, "Ethernet 2") != "2 aktivní rozhraní" ||
            FormatOverviewNetworkAdapterLabel(5, "Wi-Fi") != "5 aktivních rozhraní")
            throw new InvalidOperationException("Aggregated network adapter labeling regression.");
        string[] a = { "ethernet", "wifi" };
        string[] reordered = { "WIFI", "ETHERNET" };
        string[] partial = { "ethernet" };
        string[] duplicate = { "ethernet", "ethernet" };
        if (!SameNetworkAdapterSet(a, reordered) ||
            !SameNetworkAdapterSet(Array.Empty<string>(), Array.Empty<string>()) ||
            SameNetworkAdapterSet(null, a) ||
            SameNetworkAdapterSet(a, partial) ||
            SameNetworkAdapterSet(partial, a) ||
            SameNetworkAdapterSet(a, duplicate))
            throw new InvalidOperationException("NIC successful-sample membership regression.");

        DateTime now = new(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc);
        if (!ShouldResetNetworkRateBaseline(now.AddSeconds(-2), now,
                7_000_000, 5_000_000, 100_000, 200_000, true,
                !SameNetworkAdapterSet(a, partial)) ||
            !ShouldResetNetworkRateBaseline(now.AddSeconds(-2), now,
                100_000, 200_000, 8_000_000, 6_000_000, true,
                !SameNetworkAdapterSet(partial, reordered)) ||
            ShouldResetNetworkRateBaseline(now.AddSeconds(-2), now,
                7_000_000, 5_000_000, 7_250_000, 5_125_000, true,
                !SameNetworkAdapterSet(a, reordered)))
            throw new InvalidOperationException("NIC partial-failure or recovery baseline regression.");

        long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        long frequency = System.Diagnostics.Stopwatch.Frequency;
        if (!IsOverviewLiveSampleFresh(timestamp, timestamp) ||
            !IsOverviewLiveSampleFresh(timestamp, timestamp + frequency * 8L) ||
            IsOverviewLiveSampleFresh(timestamp, timestamp + frequency * 9L) ||
            IsOverviewLiveSampleFresh(timestamp, timestamp - 1) ||
            IsOverviewLiveSampleFresh(0, timestamp))
            throw new InvalidOperationException("Overview queued sample freshness regression.");

        var down = new PointCollection();
        var up = new PointCollection();
        var spark = new PointCollection();
        foreach (var collection in new[] { down, up, spark })
        {
            collection.Add(new Windows.Foundation.Point(12, 14));
            collection.Add(new Windows.Foundation.Point(22, 24));
            ClearOverviewNetworkPlot(collection);
            if (collection.Count != 0)
                throw new InvalidOperationException("Reset left stale network chart points rendered.");
        }
        return "OVERVIEW_NETWORK_CONTINUITY_OK cases=15 " +
            "partialFailure=true recovery=true duplicateIds=true " +
            "staleUiDropped=true activeUiRetained=true plotsCleared=true writes=0";
    }

    private void ResetOverviewNetworkHistory()
    {
        // These UI collections belong to the dispatcher, not the sensor worker.
        // Never visually join samples from different adapters/counter epochs.
        _overviewNetDownSamples.Clear();
        _overviewNetUpSamples.Clear();
        _overviewNetworkWasFlat = false;
        _referenceNetworkHistory.Clear();
        _referenceNetworkLastTotal = double.NaN;
        _referenceNetworkSteadySamples = 0;
        _referenceNetworkLastSparkWidth = double.NaN;
        _referenceNetworkLastSparkHeight = double.NaN;
        // Clear the *rendered* WinUI points too. An empty history alone does
        // not hide the old plot, because the renderer returns on empty input.
        ClearOverviewNetworkPlot(_overviewNetDownLine?.Points);
        ClearOverviewNetworkPlot(_overviewNetUpLine?.Points);
        ClearOverviewNetworkPlot(_referenceNetworkSpark?.Points);
        _overviewNextNetworkChartUtc = DateTime.MinValue;
    }

    // Format the four footers only when their actual data or native visual
    // target changes; the four fill bars still update on every 2-second tick.
    // Missing/nonfinite input must not send NaN/Infinity into WinUI Width.
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
