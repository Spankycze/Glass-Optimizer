using System;
using System.Linq;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Microsoft.UI.Xaml;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private bool _nativeGlassBorderSuppressed;
    private (double Width, double Height, double Scale, bool Enabled)? _rimNativeSignature;

    // The window silhouette is native. No perimeter canvas, stroke or gradient is drawn.
    private void ApplyNativeGlassWindowShape()
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
            _nativeGlassBorderSuppressed = NativeTitleBarService.TryApplyGlassBorder(hwnd, _darkTheme, out int borderError);
            if (!_nativeGlassBorderSuppressed)
                LogService.Info("window", $"WINDOW_BORDER_DISABLE_DEGRADED error={borderError}");

            if (!NativeTitleBarService.TryRoundCorners(hwnd, out int cornerError))
                LogService.Info("window", $"WINDOW_ROUND_CORNERS_DEGRADED error={cornerError}");
            // Titlebar reconfiguration can clear the HWND region; apply our silhouette last.
            _rimNativeSignature = null;
            UpdateRoundedGlassWindow();
    }

    private void InitializeRoundedGlassWindow()
    {
        _rimNativeSignature = null;
        RootGrid.Loaded += (_, _) => UpdateRoundedGlassWindow();
        RootGrid.SizeChanged += (_, _) => UpdateRoundedGlassWindow();
        UpdateRoundedGlassWindow();
    }

    private void VerifyRoundedGlassWindow()
    {
        RootGrid.UpdateLayout();
        UpdateRoundedGlassWindow();
        if (RootGrid.Children.OfType<FrameworkElement>().Any(child => child.Tag is "shaped-glass-rim"))
            throw new InvalidOperationException("Visible perimeter decoration returned.");
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
        var signature = (w,h,scale,enabled);
        if (_rimNativeSignature == signature) return;
        _rimNativeSignature = signature; // Native call can synchronously send sizing messages.
        if (!NativeTitleBarService.TrySetRoundedGlassRegion(WindowNative.GetWindowHandle(this), enabled, 25 * scale, out int error))
        {
            _rimNativeSignature = null;
            LogService.Info("window", $"GLASS_WINDOW_REGION_FAILED error={error}");
        }
    }
}