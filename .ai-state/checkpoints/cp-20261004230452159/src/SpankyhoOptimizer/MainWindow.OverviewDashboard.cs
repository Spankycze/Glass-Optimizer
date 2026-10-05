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
        public required Border Fill { get; init; }
        public required FrameworkElement TrackHost { get; init; }
        public bool VerticalLevel { get; init; }
        public FrameworkElement? ReactiveElement { get; init; }
        public FrameworkElement? ScanElement { get; init; }
        public Action<double>? VisualUpdater { get; init; }
        public System.Collections.Generic.Queue<double> Samples { get; } = new();
        public double CurrentValue { get; set; }
        public int MissingSamples { get; set; }
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

    private readonly record struct OverviewPerfSample(DateTime Utc, double Cpu, double? Gpu, double Ram, double Disk);
    private readonly System.Collections.Generic.List<OverviewPerfSample> _overviewPerfHistory = new(2048);
    private readonly OverviewPerfSample[] _overviewChartSampleBuffer = new OverviewPerfSample[48];
    private Polyline? _overviewCpuChartLine;
    private Polyline? _overviewGpuChartLine;
    private Polyline? _overviewRamChartLine;
    private Polyline? _overviewDiskChartLine;
    private Polygon? _overviewRamChartFill;
    private TextBlock[]? _overviewChartXLabels;
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
    private TextBlock? _overviewNetworkStatusText;
    private TextBlock? _overviewNetworkSpeedText;
    private Polyline? _overviewNetDownLine;
    private Polyline? _overviewNetUpLine;
    private readonly System.Collections.Generic.Queue<double> _overviewNetDownSamples = new();
    private readonly System.Collections.Generic.Queue<double> _overviewNetUpSamples = new();
    private System.Net.NetworkInformation.NetworkInterface[]? _overviewNetInterfaces;
    private DateTime _overviewNetInterfaceRefreshUtc;
    private long _overviewPrevRxBytes;
    private long _overviewPrevTxBytes;
    private DateTime _overviewPrevNetUtc;

    private TextBlock? _overviewChangelogCaption;
    private TextBlock[]? _overviewHistoryTimes;
    private TextBlock[]? _overviewHistoryRows;
    private TextBlock? _overviewSystemStatusText;
    private TextBlock? _overviewDriverStatusText;

    private System.Collections.Generic.Dictionary<string, bool> GetDetectedTweakStates()
    {
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
        token.ThrowIfCancellationRequested();
        var phase = System.Diagnostics.Stopwatch.StartNew();
        double layoutWidth = RootGrid?.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth;
        bool compact = layoutWidth < 1200;

        if (_overviewPageCache != null && _overviewTextRefs != null && _overviewCacheCompact == compact)
        {
            foreach (var element in _overviewPageCache) AttachCachedElement(element);
            _overviewPageCacheUsedAt = DateTime.UtcNow;
            if (_overviewGaugeHost is { Children.Count: 0 })
                ScheduleOverviewGaugeCards(_overviewGaugeHost, token);
            var refs = _overviewTextRefs;
            if (_overviewMetricsHost is { Children.Count: 0 } metricsHost &&
                _overviewLiveHost is { Children.Count: 0 } liveHost)
                ScheduleOverviewCards(metricsHost, liveHost, refs, compact, token);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || _section != "overview") return;
                var cachedSnapshotTask = HardwareSnapshotService.GetOverviewAsync(false, CancellationToken.None);
                SafeTaskService.Forget(
                    CompleteOverviewDashboardAsync(cachedSnapshotTask, refs.Cpu, refs.Gpu, refs.Ram, refs.Disk, refs.Os, refs.Board, refs.Power, refs.Network, refs.Driver, refs.History, refs.Windows, token),
                    "overview", "Overview cached data refresh");
            });
            long attachMs = phase.ElapsedMilliseconds;
            if (attachMs >= 5)
                LogService.Info("performance", $"OVERVIEW_CACHE_HIT attachMs={attachMs} compact={compact}");
            return Task.CompletedTask;
        }

        if (_overviewPageCache != null || _overviewTextRefs != null)
            InvalidateOverviewPageCache(forceRelease: true);
        else
            ReleaseOverviewGaugeVisuals();

        var snapshotTask = HardwareSnapshotService.GetOverviewAsync(false, CancellationToken.None);
        long snapshotKickoffMs = phase.ElapsedMilliseconds;

        var gauges = new Grid
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        _overviewGaugeHost = gauges;
        ContentStack.Children.Add(gauges);
        long gaugesMs = phase.ElapsedMilliseconds;

        var metrics = new Grid
        {
            Margin = new Thickness(0, 0, 0, 8),
            MinHeight = 0, Visibility = Visibility.Collapsed
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
            MinHeight = compact ? 330 : 148,
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
        _overviewPageCache = ContentStack.Children.Cast<UIElement>().ToList();
        _overviewCacheCompact = compact;
        _overviewPageCacheUsedAt = DateTime.UtcNow;

        long cacheMs = phase.ElapsedMilliseconds;
        ScheduleOverviewGaugeCards(gauges, token);
        ScheduleOverviewCards(metrics, live, _overviewTextRefs, compact, token);
        long scheduleMs = phase.ElapsedMilliseconds;

        SafeTaskService.Forget(CompleteOverviewDashboardAsync(snapshotTask, cpuValue, gpuValue, ramValue, diskValue, osValue, boardValue, powerValue, networkValue, driverValue, historyValue, windowsValue, token),
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

    private void ScheduleOverviewGaugeCards(Grid host, CancellationToken token)
    {
        if (host.Children.Count > 0 || (ReferenceEquals(_overviewGaugeQueuedHost, host) && _overviewGaugeQueuedToken == token)) return;
        _overviewGaugeQueuedHost = host;
        _overviewGaugeQueuedToken = token;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(_overviewGaugeQueuedHost, host))
                _overviewGaugeQueuedHost = null;
            if (token.IsCancellationRequested || _section != "overview" ||
                !ReferenceEquals(_overviewGaugeHost, host) || host.Children.Count > 0)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            var root = new StackPanel
            {
                Spacing = 0,
                Margin = new Thickness(0)
            };
            long headerBuildMs = 0;


            var body = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
            if (_overviewCacheCompact)
            {
                body.ColumnDefinitions.Add(new ColumnDefinition());
                body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            else
            {
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.35, GridUnitType.Star) });
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            root.Children.Add(body);

            var gaugePanel = new Border
            {
                Background = ThemeColor(0,0,0,0),
                BorderBrush = null,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = root
            };
            _overviewGaugePanel = gaugePanel;
            host.Children.Add(gaugePanel);

            long shellBuildMs = sw.ElapsedMilliseconds;
            LogService.Info("performance", $"OVERVIEW_GAUGES_STAGE stage=shell ms={shellBuildMs} headerMs={headerBuildMs}");

            var telemetry = new Grid { ColumnSpacing = 24, RowSpacing = 16 };
            _overviewTelemetryGrid = telemetry;
            int telemetryColumns = _overviewCacheCompact ? 1 : 2;
            for (int i = 0; i < telemetryColumns; i++) telemetry.ColumnDefinitions.Add(new ColumnDefinition());
            for (int row = 0; row < 4 / telemetryColumns; row++)
                telemetry.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.Children.Add(telemetry);

            var quickTitle = new TextBlock
            {
                Text = "Rychlé ovládání",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimaryBrush()
            };
            var quickSub = new TextBlock
            {
                Text = "Nejdůležitější části optimizeru na jeden klik",
                FontSize = 12.5,
                Foreground = TextSecondaryBrush(),
                TextWrapping = TextWrapping.Wrap
            };
            var quickStack = new StackPanel { Spacing = 10 };
            quickStack.Children.Add(quickTitle);
            quickStack.Children.Add(quickSub);

            Button QuickButton(string label, string section, string glyph)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, IsHitTestVisible = false };
                row.Children.Add(new FontIcon
                {
                    Glyph = glyph,
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 18,
                    Foreground = SidebarIconAccent(section),
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 13.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = TextPrimaryBrush(),
                    VerticalAlignment = VerticalAlignment.Center
                });
                var b = new Button
                {
                    Content = row,
                    Height = 46,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(14,0,14,0),
                    CornerRadius = new CornerRadius(14),
                    Background = ThemeColor(44, 17, 25, 35),
                    BorderBrush = ThemeColor(76, 125, 161, 190),
                    BorderThickness = new Thickness(1)
                };
                AttachHoverMotion(b, 1.012f);
                b.Click += (_, _) => SafeTaskService.Forget(
                    NavigateMainSectionFromUiAsync(section), "navigation", "Overview quick action " + section);
                return b;
            }

            quickStack.Children.Add(QuickButton("Systémové tweaky", "system", "\uE770"));
            quickStack.Children.Add(QuickButton("Power plán", "power", "\uE945"));
            quickStack.Children.Add(QuickButton("Síť a DNS", "network", "\uE968"));
            quickStack.Children.Add(QuickButton("GPU / Display", "gpu", "\uE7F8"));

            var quickCard = new Border
            {
                Background = GlassPanelBrush(),
                BorderBrush = GlassEdgeBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(22),
                Padding = new Thickness(18),
                Child = quickStack,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            if (_overviewCacheCompact)
            {
                Grid.SetRow(quickCard, 1);
                Grid.SetColumn(quickCard, 0);
            }
            else Grid.SetColumn(quickCard, 1);
            body.Children.Add(quickCard);

            long telemetryStageMs = 0;
            void QueueTelemetryCard(int index)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    if (token.IsCancellationRequested || _section != "overview" ||
                        !ReferenceEquals(_overviewGaugeHost, host) || host.Children.Count == 0)
                        return;

                    if (index < 4)
                    {
                        var cardSw = System.Diagnostics.Stopwatch.StartNew();
                        FrameworkElement card = index switch
                        {
                            0 => BuildHudTelemetryCard("CPU", "Načítám…", CpuMetricBrush(), out _cpuGauge),
                            1 => BuildHudTelemetryCard("GPU", "Načítám…", GpuMetricBrush(), out _gpuGauge),
                            2 => BuildHudTelemetryCard("RAM", "Načítám…", RamMetricBrush(), out _ramGauge),
                            _ => BuildHudTelemetryCard("DISK", "Načítám…", DiskMetricBrush(), out _diskGauge)
                        };
                        Grid.SetColumn(card, index % telemetryColumns); Grid.SetRow(card, index / telemetryColumns);
                        telemetry.Children.Add(card);

                        if (_overviewTextRefs is { } liveRefs)
                        {
                            if (index == 0 && _cpuGauge != null && liveRefs.Cpu.Text != "Načítám…") _cpuGauge.SecondaryText.Text = liveRefs.Cpu.Text;
                            else if (index == 1 && _gpuGauge != null && liveRefs.Gpu.Text != "Načítám…") _gpuGauge.SecondaryText.Text = liveRefs.Gpu.Text;
                            else if (index == 2 && _ramGauge != null && liveRefs.Ram.Text != "Načítám…") _ramGauge.SecondaryText.Text = liveRefs.Ram.Text;
                            else if (index == 3 && _diskGauge != null && liveRefs.Disk.Text != "Načítám…") _diskGauge.SecondaryText.Text = liveRefs.Disk.Text;
                        }

                        long cardMs = cardSw.ElapsedMilliseconds;
                        telemetryStageMs += cardMs;
                        LogService.Info("performance", $"OVERVIEW_GAUGES_STAGE stage=telemetry-card index={index} ms={cardMs}");
                        QueueTelemetryCard(index + 1);
                        return;
                    }

                    LogService.Info("performance", $"OVERVIEW_GAUGES_STAGE stage=telemetry ms={telemetryStageMs}");
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                    {
                        if (token.IsCancellationRequested || _section != "overview" ||
                            !ReferenceEquals(_overviewGaugeHost, host) || host.Children.Count == 0)
                            return;

                        var chartSw = System.Diagnostics.Stopwatch.StartNew();
                        // Individual visible sparklines replace the unused aggregate chart.


                        long chartStageMs = chartSw.ElapsedMilliseconds;
                        LogService.Info("performance", $"OVERVIEW_GAUGES_STAGE stage=chart ms={chartStageMs}");
                        LogService.Info("performance", $"OVERVIEW_GAUGES_DEFERRED staged=true shellMs={shellBuildMs} telemetryMs={telemetryStageMs} chartMs={chartStageMs} layout=rich");
                    });
                });
            }
            QueueTelemetryCard(0);
        });
    }

    private void ScheduleOverviewCards(
        Grid metricsHost,
        Grid liveHost,
        OverviewTextRefs refs,
        bool compact,
        CancellationToken token)
    {
        if ((metricsHost.Children.Count > 0 || liveHost.Children.Count > 0) ||
            (ReferenceEquals(_overviewCardsQueuedHost, metricsHost) && _overviewCardsQueuedToken == token))
            return;

        _overviewCardsQueuedHost = metricsHost;
        _overviewCardsQueuedToken = token;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (token.IsCancellationRequested || _section != "overview" ||
                !ReferenceEquals(_overviewMetricsHost, metricsHost) ||
                !ReferenceEquals(_overviewLiveHost, liveHost) ||
                metricsHost.Children.Count > 0 || liveHost.Children.Count > 0)
            {
                if ((ReferenceEquals(_overviewCardsQueuedHost, metricsHost) && _overviewCardsQueuedToken == token))
                    _overviewCardsQueuedHost = null;
                return;
            }

            var metricsSw = System.Diagnostics.Stopwatch.StartNew();
            var hardwareHeading = new StackPanel { Spacing = 2, Margin = new Thickness(2, 8, 0, 10) };
            hardwareHeading.Children.Add(new TextBlock
            {
                Text = "Hardware",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimaryBrush()
            });
            hardwareHeading.Children.Add(new TextBlock
            {
                Text = "Komponenty a jejich aktuální stav",
                FontSize = 12.5,
                Foreground = TextSecondaryBrush()
            });
            var hardwareStack = new StackPanel { Spacing = 0 };
            hardwareStack.Children.Add(hardwareHeading);
            hardwareStack.Children.Add(BuildOverviewHardwareStrip(refs, compact));
            metricsHost.Children.Add(hardwareStack);
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

                FrameworkElement[] infoCards =
                {
                    BuildOverviewSystemRichCard(refs.Os, refs.Board),
                    BuildOverviewNetworkCard(refs.Network),
                    BuildOverviewUptimeCard(uptimeValue),
                    BuildOverviewHistoryCard(refs.History)
                };
                SafeTaskService.Forget(RefreshOverviewChangelogRowsAsync(token), "overview", "Overview changelog refresh");

                var systemHeading = new StackPanel { Spacing = 2, Margin = new Thickness(2, 8, 0, 10) };
                systemHeading.Children.Add(new TextBlock
                {
                    Text = "Systém",
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    Foreground = TextPrimaryBrush()
                });
                systemHeading.Children.Add(new TextBlock
                {
                    Text = "Windows, síť, uptime a poslední změny",
                    FontSize = 12.5,
                    Foreground = TextSecondaryBrush()
                });

                var infoGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
                int columns = compact ? 2 : 4;
                for (int i = 0; i < columns; i++) infoGrid.ColumnDefinitions.Add(new ColumnDefinition());
                infoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (compact) infoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int i = 0; i < infoCards.Length; i++)
                {
                    Grid.SetColumn(infoCards[i], i % columns);
                    Grid.SetRow(infoCards[i], compact ? i / columns : 0);
                    infoGrid.Children.Add(infoCards[i]);
                }

                var systemStack = new StackPanel { Spacing = 0 };
                systemStack.Children.Add(systemHeading);
                systemStack.Children.Add(infoGrid);
                liveHost.Children.Add(systemStack);

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
        _overviewCardsQueuedHost = null;
        _overviewPageCacheUsedAt = default;
        _overviewCacheCompact = false;

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
        _overviewNetworkStatusText = null;
        _overviewNetworkSpeedText = null;
        _overviewNetDownLine = null;
        _overviewNetUpLine = null;
        _overviewChangelogCaption = null;
        _overviewHistoryTimes = null;
        _overviewHistoryRows = null;
        _overviewSystemStatusText = null;
        _overviewDriverStatusText = null;
        if (forceRelease || _section != "overview")
            ReleaseOverviewGaugeVisuals();
    }

    internal async Task RunOverviewCacheSmokeAsync()
    {
        InvalidateOverviewPageCache(forceRelease: true);
        await ShowSectionAsync("overview");
        await ShowSectionAsync("overview"); // Canceled factories must be rescheduled for repeated navigation.
        for (int i = 0; i < 40 && (_cpuGauge == null ||
             _overviewMetricsHost?.Children.Count == 0 ||
             _overviewLiveHost?.Children.Count == 0); i++)
            await Task.Delay(25);
        if (_overviewPageCache == null || _overviewPageCache.Count == 0 || _overviewTextRefs == null || _cpuGauge == null ||
            _overviewMetricsHost is not { Children.Count: > 0 } ||
            _overviewLiveHost is not { Children.Count: > 0 })
            throw new InvalidOperationException("Overview cache/deferred cards were not captured after first render.");

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
            throw new InvalidOperationException($"Overview cached navigation too slow: {attachMs} ms.");

        LogService.Info("performance", $"OVERVIEW_CACHE_SMOKE_OK attachMs={attachMs} elements={_overviewPageCache.Count}");
    }

    internal async Task RunStartupOverviewVisualSmokeAsync()
    {
        for (int i = 0; i < 80 &&
             (!_initialSectionNavigationCompleted || _section != "overview" ||
              _overviewPageCache == null || _overviewPageCache.Count == 0 ||
              _overviewGaugePanel == null || _overviewTelemetryGrid is not { Children.Count: 4 } ||
              _overviewMetricsHost is not { Children.Count: > 0 } ||
              _overviewLiveHost is not { Children.Count: > 0 } || _cpuGauge == null); i++)
            await Task.Delay(25);

        if (_section != "overview")
            throw new InvalidOperationException($"Startup section is '{_section}', expected overview.");
        if (!_initialSectionNavigationCompleted)
            throw new InvalidOperationException("Initial Overview navigation did not complete.");
        if (_overviewPageCache == null || _overviewPageCache.Count == 0)
            throw new InvalidOperationException("Overview startup root is missing.");
        if (_overviewGaugePanel == null || _overviewTelemetryGrid is not { Children.Count: 4 } ||
            _overviewMetricsHost is not { Children.Count: > 0 } ||
            _overviewLiveHost is not { Children.Count: > 0 } || _cpuGauge == null)
            throw new InvalidOperationException("Overview startup cards/gauges are missing.");
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

        LogService.Info("performance",
            $"STARTUP_OVERVIEW_VISUAL_SMOKE_OK roots={_overviewPageCache.Count} metrics={_overviewMetricsHost.Children.Count} live={_overviewLiveHost.Children.Count} stable=true heroUnderTitlebar=true");
    }

    private async Task CompleteOverviewDashboardAsync(Task<OverviewSnapshot> snapshotTask,
        TextBlock cpu, TextBlock gpu, TextBlock ram, TextBlock disk, TextBlock os, TextBlock board,
        TextBlock power, TextBlock network, TextBlock driver, TextBlock history, TextBlock windows, CancellationToken token)
    {
        try
        {
            await Task.Yield();
            if (token.IsCancellationRequested) return;
            var historyTask = TweakBatchHistoryService.ReadRecentAsync(4, token);
            var expectedTweakStatesTask = TweakBatchHistoryService.ReadLatestExpectedStatesAsync(token: token);
            var s = await snapshotTask.ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "overview") return;
                cpu.Text = s.Cpu;
                gpu.Text = s.Gpu;
                ram.Text = s.Ram;
                disk.Text = s.Disk;
                os.Text = s.Windows.Replace(" • 26H2", "\n26H2", StringComparison.OrdinalIgnoreCase);
                board.Text = s.Motherboard;
                power.Text = s.PowerPlan;
                network.Text = s.Network;
                windows.Text = s.Windows;

                if (_cpuGauge != null) _cpuGauge.SecondaryText.Text = s.Cpu;
                if (_gpuGauge != null) _gpuGauge.SecondaryText.Text = s.Gpu;
                if (_latestOverviewRamSample is { } liveRam)
                    UpdateOverviewRamSummary(liveRam);
                else if (_ramGauge != null) _ramGauge.SecondaryText.Text = s.Ram;
                if (_diskGauge != null) _diskGauge.SecondaryText.Text = s.Disk;
                if (_overviewSystemStatusText != null) _overviewSystemStatusText.Text = "●  Systém načten";
            });

            var recent = await historyTask.ConfigureAwait(false);
            var expectedTweakStates = await expectedTweakStatesTask.ConfigureAwait(false);
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
            LogService.Info("overview",
                $"OVERVIEW_ACTIVE_TWEAKS active={activeTweaks} expectedActive={expectedActiveTweaks} expectedKnown={expectedTweakStates.Count} detectedActive={detectedActiveTweaks} detectedKnown={detectedTweakStates.Count} mergedKnown={mergedTweakStates.Count}");
            string historyText = BuildOverviewActiveTweakStatusText(
                activeTweaks,
                mergedTweakStates.Count,
                activeSource,
                recent.Length == 0 ? null : recent[0].Time);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section != "overview") return;
                history.Text = historyText;
            });

            SafeTaskService.Forget(
                CompleteOverviewDriverAsync(driver, token),
                "overview",
                "Deferred overview GPU driver");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Error("overview", "Overview data failed", ex); }
    }

    private async Task CompleteOverviewDriverAsync(TextBlock driver, CancellationToken token)
    {
        try
        {
            if (_gpuDetectionGeneration == GpuDriverService.DetectionCacheGeneration && _gpuInfo != null)
            {
                string cachedVersion = _gpuInfo.DisplayVersion;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_section == "overview")
                    {
                        driver.Text = cachedVersion;
                        if (_overviewDriverStatusText != null) _overviewDriverStatusText.Text = "●  Ovladač zjištěn";
                    }
                });
                return;
            }

            await Task.Delay(900, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || _section != "overview") return;

            var gpuDriver = await GetOverviewGpuDriverAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_section == "overview")
                {
                    driver.Text = gpuDriver == null ? "Driver nezjištěn" : gpuDriver.DisplayVersion;
                    if (_overviewDriverStatusText != null)
                        _overviewDriverStatusText.Text = gpuDriver == null ? "●  Ovladač nezjištěn" : "●  Ovladač zjištěn";
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Error("overview", "Overview GPU driver failed", ex); }
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

    private void AnimateGauge(GaugeRefs? refs, double? rawValue)
    {
        if (refs == null) return;
        if (!rawValue.HasValue)
        {
            refs.ValueText.Text = "N/A";
            refs.MinText.Text = "MIN —";
            refs.AvgText.Text = "AVG —";
            refs.PeakText.Text = "MAX —";
            refs.MissingSamples++;
            refs.StatusText.Text = refs.MissingSamples < 3 ? "NA\u010c\u00cdT\u00c1M" : "NEDOSTUPN\u00c9";
            refs.SparkHost.Visibility = Visibility.Collapsed;
            refs.StatusText.Foreground = TextSecondaryBrush();
            refs.StatusBadge.BorderBrush = BorderThemeBrush();
            refs.StatusBadge.Background = SurfaceAltBrush();
            refs.EndDot.Visibility = Visibility.Collapsed;
            return;
        }

        refs.MissingSamples = 0;
        refs.SparkHost.Visibility = Visibility.Visible;
        double value = Math.Clamp(rawValue.Value, 0, 100);
        refs.ValueText.Text = $"{value:0}%";
        refs.CurrentValue = value;

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
        refs.MinText.Text = $"MIN {min:0}%";
        refs.AvgText.Text = $"AVG {avg:0}%";
        refs.PeakText.Text = $"MAX {peak:0}%";

        if (value >= 85)
        {
            refs.StatusText.Text = "HIGH";
            refs.StatusText.Foreground = WarningBrush();
            refs.StatusBadge.BorderBrush = WarningBrush();
            refs.StatusBadge.Background = SurfaceAltBrush();
        }
        else if (value >= 45)
        {
            refs.StatusText.Text = "ACTIVE";
            refs.StatusText.Foreground = TextSecondaryBrush();
            refs.StatusBadge.BorderBrush = ButtonVioletBorderBrush();
            refs.StatusBadge.Background = ButtonVioletSoftBrush();
        }
        else
        {
            refs.StatusText.Text = "NORMAL";
            refs.StatusText.Foreground = TextSecondaryBrush();
            refs.StatusBadge.BorderBrush = ButtonVioletBorderBrush();
            refs.StatusBadge.Background = ButtonVioletSoftBrush();
        }

        double width = refs.SparkHost.ActualWidth > 1 ? Math.Max(1, refs.SparkHost.ActualWidth - 4) : 230;
        double height = Math.Max(24, refs.SparkHost.ActualHeight > 1 ? refs.SparkHost.ActualHeight : 38);
        refs.Sparkline.Points.Clear();

        double lastX = 0;
        double lastY = height - 4 - value * (height - 8) / 100d;
        if (sampleCount == 1)
        {
            lastX = width;
            refs.Sparkline.Points.Add(new Windows.Foundation.Point(0, lastY));
            refs.Sparkline.Points.Add(new Windows.Foundation.Point(width, lastY));
        }
        else
        {
            int index = 0;
            foreach (double sample in refs.Samples)
            {
                double x = index * width / (sampleCount - 1);
                double y = height - 4 - sample * (height - 8) / 100d;
                refs.Sparkline.Points.Add(new Windows.Foundation.Point(x, y));
                lastX = x;
                lastY = y;
                index++;
            }
        }

        Canvas.SetLeft(refs.EndDot, Math.Max(0, lastX - refs.EndDot.Width / 2));
        Canvas.SetTop(refs.EndDot, Math.Max(0, lastY - refs.EndDot.Height / 2));
        refs.EndDot.Visibility = Visibility.Visible;

        if (refs.VerticalLevel)
        {
            double trackHeight = refs.TrackHost.ActualHeight > 1 ? refs.TrackHost.ActualHeight - 8 : 90;
            refs.Fill.Height = Math.Max(0, trackHeight * value / 100d);
        }
        else
        {
            double trackWidth = refs.TrackHost.ActualWidth > 1 ? refs.TrackHost.ActualWidth : 100;
            refs.Fill.Width = Math.Max(0, trackWidth * value / 100d);
        }

        if (refs.ReactiveElement != null)
            refs.ReactiveElement.Opacity = Math.Clamp(.14 + value / 180d, .14, .72);
        refs.VisualUpdater?.Invoke(value);
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
        if (_darkTheme)
        {
            fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0, 0, 0), Offset = 0 });
            fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 2, 2, 2), Offset = .34 });
            fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 4, 4, 4), Offset = .62 });
            fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 6, 6, 6), Offset = .82 });
            fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 3, 3, 3), Offset = 1 });
        }
        else
        {
            fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, _themePalette.SurfaceElevated.R, _themePalette.SurfaceElevated.G, _themePalette.SurfaceElevated.B), Offset = 0 });
            fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, _themePalette.Surface.R, _themePalette.Surface.G, _themePalette.Surface.B), Offset = .55 });
            fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, _themePalette.SurfaceAlt.R, _themePalette.SurfaceAlt.G, _themePalette.SurfaceAlt.B), Offset = 1 });
        }

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
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
            appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
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

        LogService.Info("performance",
            $"TITLEBAR_ACTIVATE_SYNC backgroundMs={backgroundMs} extendMs={extendMs - backgroundMs} setMs={setMs - extendMs} totalMs={sw.ElapsedMilliseconds} deferredVisuals=true");

        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            CompleteDeferredTitleBarVisuals);
    }

    private void CompleteDeferredTitleBarVisuals()
    {
        if (_startupTitlebarThemeCompleted) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var captionWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle));
            var captionBar = captionWindow.TitleBar;
            var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            var captionFg = Windows.UI.Color.FromArgb(255, _themePalette.TextPrimary.R, _themePalette.TextPrimary.G, _themePalette.TextPrimary.B);
            var captionInactive = Windows.UI.Color.FromArgb(255, _themePalette.TextSecondary.R, _themePalette.TextSecondary.G, _themePalette.TextSecondary.B);
            var captionHover = Windows.UI.Color.FromArgb(48, _themePalette.SurfaceElevated.R, _themePalette.SurfaceElevated.G, _themePalette.SurfaceElevated.B);
            var captionPressed = Windows.UI.Color.FromArgb(76, _themePalette.Border.R, _themePalette.Border.G, _themePalette.Border.B);

            captionBar.BackgroundColor = transparent;
            captionBar.ForegroundColor = captionFg;
            captionBar.InactiveBackgroundColor = transparent;
            captionBar.InactiveForegroundColor = captionInactive;
            captionBar.ButtonBackgroundColor = transparent;
            captionBar.ButtonInactiveBackgroundColor = transparent;
            captionBar.ButtonForegroundColor = captionFg;
            captionBar.ButtonInactiveForegroundColor = captionInactive;
            captionBar.ButtonHoverBackgroundColor = captionHover;
            captionBar.ButtonHoverForegroundColor = captionFg;
            captionBar.ButtonPressedBackgroundColor = captionPressed;
            captionBar.ButtonPressedForegroundColor = captionFg;

            RunWithOverviewPalette(PopulateCustomTitleBarContent);
            Volatile.Write(ref _startupTitlebarThemeCompleted, true);
            LogService.Info("performance",
                $"TITLEBAR_VISUALS_DEFERRED totalMs={sw.ElapsedMilliseconds} content={AppTitleBar.Children.Count}");
        }
        catch (System.Exception ex)
        {
            LogService.Error("theme", "Deferred custom titlebar visuals failed", ex);
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
            Text = "Spankyho Optimizer",
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
    private (double Percent, double UsedGb, double TotalGb)? _latestOverviewRamSample;

    private void UpdateOverviewRamSummary((double Percent, double UsedGb, double TotalGb) sample)
    {
        string summary = $"{sample.UsedGb:0.0} / {sample.TotalGb:0.0} GB \u2022 {sample.Percent:0}% vyu\u017eito";
        if (_overviewTextRefs != null) _overviewTextRefs.Ram.Text = summary;
        if (_ramGauge != null) _ramGauge.SecondaryText.Text = summary;
    }

    private async Task StartLiveMetricsAfterFirstRenderAsync()
    {
        await Task.Delay(220).ConfigureAwait(false);
        if (_liveMetricsCts != null) return;
        _ = ReadCpuUsage();
        await Task.Delay(260).ConfigureAwait(false);
        if (_liveMetricsCts != null) return;
        DispatcherQueue.TryEnqueue(StartLiveMetricsLoop);
    }

    private void StartLiveMetricsLoop()
    {
        _liveMetricsCts?.Cancel();
        _liveMetricsCts?.Dispose();
        _liveMetricsCts = new CancellationTokenSource();
        var token = _liveMetricsCts.Token;
        SafeTaskService.Forget(
            Task.Run(() => RunLiveMetricsLoopAsync(token), token),
            "performance",
            "Live system metrics");
    }

    private volatile bool _liveMetricsDiagnosticSuppressSlow;
    private volatile bool _liveMetricsDiagnosticSuppressUi;

    private async Task RunLiveMetricsLoopAsync(CancellationToken token)
    {
        int tick = 0;
        bool gpuSampleLogged = false;
        double? gpu = null;
        double? disk = null;
        while (!token.IsCancellationRequested)
        {
            bool onOverview = _section == "overview";
            bool activeOverview = onOverview && _windowTelemetryActive;
            if (!activeOverview)
            {
                tick++;
                await Task.Delay(2000, token).ConfigureAwait(false);
                continue;
            }

            double cpu = ReadCpuUsage();
            var ramSample = ReadRamSnapshot();
            double ram = ramSample.Percent;
            var networkRates = activeOverview
                ? ReadNetworkRates()
                : (DownMbps: 0d, UpMbps: 0d, Online: false);
            // Initialize native counters on the worker after first paint (~2 s).
            // Never run the measured cold PDH initialization on the UI thread.
            bool gpuMetricsSample = activeOverview && !_liveMetricsDiagnosticSuppressSlow && tick >= 1;
            bool diskMetricsSample = activeOverview && !_liveMetricsDiagnosticSuppressSlow && tick >= 1;
            if (diskMetricsSample)
            {
                disk = ReadDiskActivity();
            }
            if (gpuMetricsSample)
            {
                gpu = ReadGpuUsage();
                if (!gpuSampleLogged)
                {
                    gpuSampleLogged = true;
                    LogService.Info("performance", $"LIVE_GPU_PDH value={(gpu.HasValue ? gpu.Value.ToString("0.0") : "N/A")}");
                }
            }
            bool refreshFastGauges = activeOverview;
            tick++;

            if (token.IsCancellationRequested) break;
            if (_windowTelemetryActive && !_liveMetricsDiagnosticSuppressUi &&
                (activeOverview || refreshFastGauges || gpuMetricsSample || diskMetricsSample))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (refreshFastGauges)
                    {
                        AnimateGauge(_cpuGauge, cpu);
                        AnimateGauge(_ramGauge, ram);
                        _latestOverviewRamSample = ramSample;
                        UpdateOverviewRamSummary(ramSample);
                    }
                    if (gpuMetricsSample)
                        AnimateGauge(_gpuGauge, gpu);
                    if (diskMetricsSample)
                        AnimateGauge(_diskGauge, disk);

                    if (activeOverview)
                    {
                        DateTime sampleUtc = DateTime.UtcNow;
                        _overviewPerfHistory.Add(new OverviewPerfSample(sampleUtc, cpu, gpu, ram, disk ?? 0));
                        DateTime keepAfter = sampleUtc.AddMinutes(-65);
                        int expired = 0;
                        while (expired < _overviewPerfHistory.Count && _overviewPerfHistory[expired].Utc < keepAfter) expired++;
                        if (expired > 0) _overviewPerfHistory.RemoveRange(0, expired);
                        UpdateOverviewPerformanceChart();

                        if (_overviewCpuFooterText != null) _overviewCpuFooterText.Text = $"{cpu:0}% využití";
                        if (_overviewGpuFooterText != null) _overviewGpuFooterText.Text = gpu.HasValue ? $"{gpu.Value:0}% využití" : "Telemetrie GPU čeká…";
                        if (_overviewRamFooterText != null) _overviewRamFooterText.Text = $"{ram:0}% využití";
                        if (_overviewDiskFooterText != null) _overviewDiskFooterText.Text = disk.HasValue ? $"{disk.Value:0}% aktivita" : "Telemetrie disku čeká…";

                        if (_overviewCpuFooterFill != null) _overviewCpuFooterFill.Width = 48 * Math.Clamp(cpu, 0, 100) / 100d;
                        if (_overviewGpuFooterFill != null) _overviewGpuFooterFill.Width = 48 * Math.Clamp(gpu ?? 0, 0, 100) / 100d;
                        if (_overviewRamFooterFill != null) _overviewRamFooterFill.Width = 48 * Math.Clamp(ram, 0, 100) / 100d;
                        if (_overviewDiskFooterFill != null) _overviewDiskFooterFill.Width = 48 * Math.Clamp(disk ?? 0, 0, 100) / 100d;

                        if (_overviewUptimeText != null) _overviewUptimeText.Text = FormatUptime();
                        UpdateOverviewNetworkVisuals(networkRates.DownMbps, networkRates.UpMbps, networkRates.Online);
                    }
                });
            }
            // Full telemetry stays responsive only while Overview is active. When the app loses
            // focus, keep a lightweight CPU/RAM snapshot at the relaxed cadence and avoid GPU,
            // disk, network and chart work until the window becomes active again.
            await Task.Delay(activeOverview ? 2000 : 10000, token).ConfigureAwait(false);
        }
    }

    private double ReadCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long i = idle.Value, k = kernel.Value, u = user.Value;
        if (_lastCpuKernel == 0) { _lastCpuIdle = i; _lastCpuKernel = k; _lastCpuUser = u; return 0; }
        long idleDelta = i - _lastCpuIdle;
        long totalDelta = (k - _lastCpuKernel) + (u - _lastCpuUser);
        _lastCpuIdle = i; _lastCpuKernel = k; _lastCpuUser = u;
        return totalDelta <= 0 ? 0 : Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private static (double Percent, double UsedGb, double TotalGb) ReadRamSnapshot()
    {
        var status = new LiveMemoryStatus { Length = (uint)Marshal.SizeOf<LiveMemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0, 0);
        const double gb = 1024d * 1024d * 1024d;
        double total = status.TotalPhys / gb;
        double used = (status.TotalPhys - status.AvailPhys) / gb;
        return (status.MemoryLoad, used, total);
    }

    private static double ReadCpuGHzFast()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            double mhz = Convert.ToDouble(key?.GetValue("~MHz") ?? 0);
            return mhz > 0 ? mhz / 1000d : 0;
        }
        catch { return 0; }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out LiveFileTime idle, out LiveFileTime kernel, out LiveFileTime user);

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
            entry.Children.Add(new TextBlock { Text = item.Item1, FontSize = 9.5, Foreground = TextSecondaryBrush() });
            legend.Children.Add(entry);
        }
        root.Children.Add(legend);

        var canvas = new Canvas
        {
            Height = 96,
            Margin = new Thickness(0, 8, 0, 0),
            Background = ThemeColor(35, 9, 12, 24)
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
            var label = new TextBlock { Text = yLabels[i], FontSize = 8.5, Foreground = TextSecondaryBrush() };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 7);
            canvas.Children.Add(label);
        }

        string[] xLabels = { "60 s", "45 s", "30 s", "15 s", "0 s" };
        _overviewChartXLabels = new TextBlock[xLabels.Length];
        for (int i = 0; i < xLabels.Length; i++)
        {
            var label = new TextBlock { Text = xLabels[i], FontSize = 8.5, Foreground = TextSecondaryBrush() };
            _overviewChartXLabels[i] = label;
            Canvas.SetLeft(label, 38 + i * 118d);
            Canvas.SetTop(label, 80);
            canvas.Children.Add(label);
        }

        Polyline MakeLine(SolidColorBrush color, params double[] ys)
        {
            var line = new Polyline { Stroke = color, StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i < ys.Length; i++)
                line.Points.Add(new Windows.Foundation.Point(38 + i * 474d / (ys.Length - 1), ys[i]));
            return line;
        }

        _overviewRamChartFill = new Polygon
        {
            Fill = ThemeColor(28, RamMetricBrush().Color.R, RamMetricBrush().Color.G, RamMetricBrush().Color.B)
        };
        canvas.Children.Add(_overviewRamChartFill);

        _overviewRamChartLine = MakeLine(RamMetricBrush(), 78, 78);
        _overviewCpuChartLine = MakeLine(CpuMetricBrush(), 78, 78);
        _overviewGpuChartLine = MakeLine(GpuMetricBrush(), 78, 78);
        _overviewDiskChartLine = MakeLine(DiskMetricBrush(), 78, 78);
        canvas.Children.Add(_overviewRamChartLine);
        canvas.Children.Add(_overviewCpuChartLine);
        canvas.Children.Add(_overviewGpuChartLine);
        canvas.Children.Add(_overviewDiskChartLine);
        UpdateOverviewPerformanceChart();

        Grid.SetRow(canvas, 1);
        root.Children.Add(canvas);

        return new Border
        {
            Background = SurfaceAltBrush(),
            BorderBrush = OverviewNeonBorderBrush(),
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
            item.Pill.Background = active ? ButtonVioletBrush() : SurfaceAltBrush();
            item.Pill.BorderBrush = active ? ButtonVioletBorderBrush() : BorderThemeBrush();
            if (item.Pill.Child is TextBlock text)
                text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
        }

        if (_overviewChartXLabels is { Length: 5 })
        {
            string[] labels = _overviewRangeMinutes switch
            {
                <= 1 => new[] { "60 s", "45 s", "30 s", "15 s", "0 s" },
                5 => new[] { "5 m", "4 m", "3 m", "2 m", "0 m" },
                15 => new[] { "15 m", "11 m", "7 m", "3 m", "0 m" },
                _ => new[] { "60 m", "45 m", "30 m", "15 m", "0 m" }
            };
            for (int i = 0; i < labels.Length; i++) _overviewChartXLabels[i].Text = labels[i];
        }
        UpdateOverviewPerformanceChart();
    }

    private void UpdateOverviewPerformanceChart()
    {
        if (_overviewCpuChartLine == null || _overviewGpuChartLine == null ||
            _overviewRamChartLine == null || _overviewDiskChartLine == null || _overviewRamChartFill == null)
            return;

        DateTime cutoff = DateTime.UtcNow.AddMinutes(-_overviewRangeMinutes);
        int first = 0;
        while (first < _overviewPerfHistory.Count && _overviewPerfHistory[first].Utc < cutoff) first++;
        int sourceCount = _overviewPerfHistory.Count - first;
        if (sourceCount <= 0) return;

        const int maxPoints = 48;
        int sampleCount = Math.Min(maxPoints, sourceCount);
        if (sampleCount == 1)
        {
            _overviewChartSampleBuffer[0] = _overviewPerfHistory[^1];
        }
        else
        {
            for (int i = 0; i < sampleCount; i++)
            {
                int sourceOffset = sourceCount <= maxPoints
                    ? i
                    : (int)Math.Round(i * (sourceCount - 1d) / (sampleCount - 1d));
                _overviewChartSampleBuffer[i] = _overviewPerfHistory[first + sourceOffset];
            }
        }

        void FillLine(Polyline line, int metric)
        {
            line.Points.Clear();
            for (int i = 0; i < sampleCount; i++)
            {
                OverviewPerfSample sample = _overviewChartSampleBuffer[i];
                double x = sampleCount == 1 ? 512 : 38 + i * 474d / (sampleCount - 1);
                double raw = metric switch
                {
                    0 => sample.Cpu,
                    1 => sample.Gpu ?? 0,
                    2 => sample.Ram,
                    _ => sample.Disk
                };
                double y = 78 - Math.Clamp(raw, 0, 100) * .68;
                line.Points.Add(new Windows.Foundation.Point(x, y));
            }
        }

        FillLine(_overviewCpuChartLine, 0);
        FillLine(_overviewGpuChartLine, 1);
        FillLine(_overviewRamChartLine, 2);
        FillLine(_overviewDiskChartLine, 3);

        _overviewRamChartFill.Points.Clear();
        _overviewRamChartFill.Points.Add(new Windows.Foundation.Point(38, 78));
        for (int i = 0; i < sampleCount; i++)
        {
            OverviewPerfSample sample = _overviewChartSampleBuffer[i];
            double x = sampleCount == 1 ? 512 : 38 + i * 474d / (sampleCount - 1);
            double y = 78 - Math.Clamp(sample.Ram, 0, 100) * .68;
            _overviewRamChartFill.Points.Add(new Windows.Foundation.Point(x, y));
        }
        _overviewRamChartFill.Points.Add(new Windows.Foundation.Point(512, 78));
    }

    private static string FormatRate(double mbps)
        => mbps >= 100 ? $"{mbps:0} Mb/s" : mbps >= 10 ? $"{mbps:0.0} Mb/s" : $"{mbps:0.00} Mb/s";

    private (double DownMbps, double UpMbps, bool Online) ReadNetworkRates()
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            if (_overviewNetInterfaces == null || now >= _overviewNetInterfaceRefreshUtc)
            {
                _overviewNetInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                _overviewNetInterfaceRefreshUtc = now.AddSeconds(10);
            }

            long rx = 0, tx = 0;
            bool online = false;
            foreach (var nic in _overviewNetInterfaces)
            {
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel ||
                    nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    nic.Speed <= 0)
                    continue;
                online = true;
                var stats = nic.GetIPv4Statistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }

            if (_overviewPrevNetUtc == default)
            {
                _overviewPrevNetUtc = now;
                _overviewPrevRxBytes = rx;
                _overviewPrevTxBytes = tx;
                return (0, 0, online);
            }

            double seconds = Math.Max(.1, (now - _overviewPrevNetUtc).TotalSeconds);
            double down = Math.Max(0, rx - _overviewPrevRxBytes) * 8d / seconds / 1_000_000d;
            double up = Math.Max(0, tx - _overviewPrevTxBytes) * 8d / seconds / 1_000_000d;
            _overviewPrevNetUtc = now;
            _overviewPrevRxBytes = rx;
            _overviewPrevTxBytes = tx;
            return (down, up, online);
        }
        catch
        {
            return (0, 0, false);
        }
    }

    private void UpdateOverviewNetworkVisuals(double downMbps, double upMbps, bool online)
    {
        if (_overviewNetworkStatusText != null)
            _overviewNetworkStatusText.Text = online ? "Aktivní připojení" : "Síť odpojena";
        if (_overviewNetworkSpeedText != null)
            _overviewNetworkSpeedText.Text = $"↓ {FormatRate(downMbps)}      ↑ {FormatRate(upMbps)}";

        _overviewNetDownSamples.Enqueue(downMbps);
        _overviewNetUpSamples.Enqueue(upMbps);
        while (_overviewNetDownSamples.Count > 14) _overviewNetDownSamples.Dequeue();
        while (_overviewNetUpSamples.Count > 14) _overviewNetUpSamples.Dequeue();

        if (_overviewNetDownLine == null || _overviewNetUpLine == null) return;
        double max = 1;
        foreach (double value in _overviewNetDownSamples) max = Math.Max(max, value);
        foreach (double value in _overviewNetUpSamples) max = Math.Max(max, value);

        void Fill(Polyline line, System.Collections.Generic.Queue<double> queue, double baseY)
        {
            line.Points.Clear();
            int count = queue.Count;
            int i = 0;
            foreach (double value in queue)
            {
                double x = count <= 1 ? 260 : i * 260d / (count - 1);
                double y = baseY - Math.Clamp(value / max, 0, 1) * 28;
                line.Points.Add(new Windows.Foundation.Point(x, y));
                i++;
            }
        }

        Fill(_overviewNetDownLine, _overviewNetDownSamples, 44);
        Fill(_overviewNetUpLine, _overviewNetUpSamples, 46);
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
            FontSize = 9.5,
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
            Background = ThemeColor(31, 37, 58),
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

        var chart = new Canvas { Height = 48, Background = _darkTheme ? ThemeColor(28, 6, 14, 25) : ThemeColor(38, _themePalette.Border.R, _themePalette.Border.G, _themePalette.Border.B) };
        _overviewNetDownLine = new Polyline { Stroke = DiskHardwareBrush(), StrokeThickness = 2.1, StrokeLineJoin = PenLineJoin.Round };
        _overviewNetUpLine = new Polyline { Stroke = RamMetricBrush(), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round };
        chart.Children.Add(_overviewNetDownLine);
        chart.Children.Add(_overviewNetUpLine);
        root.Children.Add(chart);

        return new Border
        {
            Background = OverviewCardBackgroundBrush(),
            BorderBrush = OverviewNeonBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10),
            MinHeight = 176,
            Child = root
        };
    }

    private Border BuildOverviewSystemRichCard(TextBlock os, TextBlock board)
    { return BuildBlackSystemCard(os, board); }

    private Border BuildOverviewUptimeCard(TextBlock uptime)
    { return BuildBlackUptimeCard(uptime); }
    private Border BuildOverviewHistoryCard(TextBlock history)
    {
        history.FontSize = 9.5;
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
            FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = TextSecondaryBrush()
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
            var timeText = new TextBlock { Text = "—", FontSize = 8.5, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() };
            var rowText = new TextBlock { Text = "Načítám…", FontSize = 8.5, Foreground = TextSecondaryBrush(), TextTrimming = TextTrimming.CharacterEllipsis };
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

        var card = new Border
        {
            Background = OverviewCardBackgroundBrush(),
            BorderBrush = OverviewNeonBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10),
            MinHeight = 176,
            Child = root
        };
        card.Tapped += (_, _) => SafeTaskService.Forget(ShowChangelogPopupAsync(), "overview", "Open changelog popup");
        return card;
    }

    private static string FormatUptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (uptime.TotalDays >= 1) return $"{(int)uptime.TotalDays} d {uptime.Hours} h {uptime.Minutes} min";
        return $"{uptime.Hours} h {uptime.Minutes} min";
    }
}