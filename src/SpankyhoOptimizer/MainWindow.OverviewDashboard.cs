using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
}
