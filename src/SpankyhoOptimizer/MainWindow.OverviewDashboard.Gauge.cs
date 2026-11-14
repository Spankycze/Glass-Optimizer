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
}
