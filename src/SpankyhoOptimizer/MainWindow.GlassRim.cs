using System;
using System.Linq;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Microsoft.UI.Xaml;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private bool _nativeGlassBorderSuppressed;
    private int _nativeGlassBorderRepairQueued;
    private (double Width, double Height, double Scale, bool Enabled)? _rimNativeSignature;
    // One timer per window. A native HRGN update synchronously sends sizing/
    // redraw messages and should never run for every intermediate XAML layout.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _roundedGlassResizeTimer;

    // The window silhouette is native. No perimeter canvas, stroke or gradient is drawn.
    private void ApplyNativeGlassWindowShape()
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        _nativeGlassBorderSuppressed = NativeTitleBarService.TryApplyGlassBorder(hwnd, _darkTheme, out int borderError);
        if (!_nativeGlassBorderSuppressed)
        {
            LogService.Info("window", $"WINDOW_BORDER_DISABLE_DEGRADED error={borderError}");
            // A newly activated WinUI window can briefly reject one DWM
            // attribute. Retry ONCE after the dispatcher settles, without
            // making successful activation/drag pay for another native call.
            if (System.Threading.Interlocked.Exchange(ref _nativeGlassBorderRepairQueued, 1) == 0 &&
                !DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    try
                    {
                        if (_windowLifetimeCts.IsCancellationRequested || _nativeGlassBorderSuppressed) return;
                        var activeHwnd = WindowNative.GetWindowHandle(this);
                        _nativeGlassBorderSuppressed = NativeTitleBarService.TryApplyGlassBorder(
                            activeHwnd, _darkTheme, out int retryError);
                        LogService.Info("window",
                            $"WINDOW_BORDER_RECOVERY success={_nativeGlassBorderSuppressed} error={retryError}");
                    }
                    finally { System.Threading.Volatile.Write(ref _nativeGlassBorderRepairQueued, 0); }
                }))
                System.Threading.Volatile.Write(ref _nativeGlassBorderRepairQueued, 0);
        }

        if (!NativeTitleBarService.TryRoundCorners(hwnd, out int cornerError))
            LogService.Info("window", $"WINDOW_ROUND_CORNERS_DEGRADED error={cornerError}");
        // Caption changes can clear the native region. This explicit repair
        // remains immediate; only SizeChanged is coalesced.
        _rimNativeSignature = null;
        UpdateRoundedGlassWindow();
    }

    private void InitializeRoundedGlassWindow()
    {
        _rimNativeSignature = null;
        RootGrid.Loaded += (_, _) => UpdateRoundedGlassWindow();
        RootGrid.SizeChanged += (_, _) => QueueRoundedGlassResize();
        UpdateRoundedGlassWindow();
    }

    private void QueueRoundedGlassResize()
    {
        if (_windowLifetimeCts.IsCancellationRequested) return;
        // Maximized windows must never retain the old clipping region even
        // briefly; release their native region synchronously.
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized })
        {
            _roundedGlassResizeTimer?.Stop();
            UpdateRoundedGlassWindow();
            UpdateHeroDragRegions();
            return;
        }

        var timer = _roundedGlassResizeTimer;
        if (timer == null)
        {
            timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(32);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                if (_windowLifetimeCts.IsCancellationRequested) return;
                UpdateRoundedGlassWindow();
                UpdateHeroDragRegions();
            };
            _roundedGlassResizeTimer = timer;
        }
        // Never restart an already-running timer: a continuous resize must
        // commit approximately every 32ms, and the final size still commits.
        if (!timer.IsRunning) timer.Start();
    }

    private void StopRoundedGlassResizeTimer()
    {
        _roundedGlassResizeTimer?.Stop();
        _roundedGlassResizeTimer = null;
    }

    private void VerifyRoundedGlassWindow()
    {
        RootGrid.UpdateLayout();
        UpdateRoundedGlassWindow();
        if (RootGrid.Children.OfType<FrameworkElement>().Any(child => child.Tag is "shaped-glass-rim"))
            throw new InvalidOperationException("Visible perimeter decoration returned.");
        if (!_nativeGlassBorderSuppressed)
        {
            // The queued Low-priority recovery may not have run yet during an
            // isolated startup smoke. Verify the actual DWM result here, not
            // just a cached flag. Persistent rejection must still FAIL smoke.
            _nativeGlassBorderSuppressed = NativeTitleBarService.TryApplyGlassBorder(
                WindowNative.GetWindowHandle(this), _darkTheme, out int verifyError);
            LogService.Info("window",
                $"WINDOW_BORDER_VERIFY_RECOVERY success={_nativeGlassBorderSuppressed} error={verifyError}");
        }
        if (!_nativeGlassBorderSuppressed)
            throw new InvalidOperationException("Native border suppression was not accepted by Windows.");
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized }) return;
        if (!NativeTitleBarService.VerifyRoundedGlassRegion(WindowNative.GetWindowHandle(this), out string detail))
            throw new InvalidOperationException("Glass window corners are not physically rounded: " + detail);
        NativeTitleBarService.VerifyGlassResizeEdges(WindowNative.GetWindowHandle(this));
        if (_mainTabsShell?.Width != 200) throw new InvalidOperationException("Compact sidebar width regression.");
        LogService.Info("visual-qa", "GLASS_WINDOW_SHAPE_OK radius=25 sidebar=200 border=false");
    }

    private void UpdateRoundedGlassWindow()
    {
        bool maximized = AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized };
        double w = RootGrid.ActualWidth, h = RootGrid.ActualHeight;
        if (w < 50 || h < 50 || RootGrid.XamlRoot == null) return;
        bool enabled = UseGlassChrome && !maximized;
        double scale = RootGrid.XamlRoot.RasterizationScale;
        var signature = (w, h, scale, enabled);
        if (_rimNativeSignature == signature) return;
        _rimNativeSignature = signature; // Native call can synchronously send sizing messages.
        if (!NativeTitleBarService.TrySetRoundedGlassRegion(WindowNative.GetWindowHandle(this), enabled, 25 * scale, out int error))
        {
            _rimNativeSignature = null;
            LogService.Info("window", $"GLASS_WINDOW_REGION_FAILED error={error}");
        }
    }
}
