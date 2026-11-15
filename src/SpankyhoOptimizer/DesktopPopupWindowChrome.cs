using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Windows.Graphics;

namespace SpankyhoOptimizer;

internal sealed class PopupChromeState
{
    internal int Height;
    internal bool Configured;
    internal int Failures;
}

// Native silhouette shared by both desktop notification popups: no activation or
// Alt+Tab entry, glass border, rounded region and the same bottom-right stack slot.
internal static class DesktopPopupWindowChrome
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    internal static bool HasNoActivateStyle(IntPtr hwnd) =>
        (GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExNoActivate) != 0;

    // Returns the applied client size, or null when the popup is already sized
    // or the native call failed and a later activation may retry.
    internal static SizeInt32? TryConfigure(
        Window window,
        int width,
        int _stackIndex,
        bool visualPreview,
        string logTag,
        string logCategory,
        string failureMessage,
        PopupChromeState state)
    {
        try
        {
            var root = (FrameworkElement)window.Content;
            root.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            int height = (int)Math.Ceiling(root.DesiredSize.Height);
            if (state.Configured && height == state.Height) return null;
            state.Height = height;
            var hwnd = WindowNative.GetWindowHandle(window);
            long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            exStyle |= WsExNoActivate | WsExToolWindow;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));

            WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(id);
            appWindow.IsShownInSwitchers = false;

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(false, false);
            }

            if (!NativeTitleBarService.TryRemovePopupNativeFrame(hwnd, out int chromeError))
                LogService.Info("window", logTag + "_NATIVE_FRAME_DEGRADED error=" + chromeError);
            double scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96d;
            var clientSize = new SizeInt32(
                (int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));
            appWindow.ResizeClient(clientSize);

            if (!NativeTitleBarService.TryApplyGlassBorder(hwnd, true, out int borderError))
                LogService.Info("window", logTag + "_GLASS_BORDER_DEGRADED error=" + borderError);
            NativeTitleBarService.TryRoundCorners(hwnd, out _);
            if (!NativeTitleBarService.TrySetRoundedGlassRegion(hwnd, true, 22 * scale, out int regionError, resizeEdges: false))
                LogService.Info("window", logTag + "_GLASS_REGION_DEGRADED error=" + regionError);

            if (App.IsSmokeUi && !visualPreview)
            {
                if (appWindow.Presenter is OverlappedPresenter smokePresenter)
                    smokePresenter.IsAlwaysOnTop = false;
                appWindow.IsShownInSwitchers = false;
                appWindow.Move(new PointInt32(-32000, -32000));
                state.Configured = true;
                return clientSize;
            }

            DisplayArea area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;
            SizeInt32 actualSize = appWindow.Size;
            int margin = (int)Math.Ceiling(18 * scale);
            int gap = (int)Math.Ceiling(10 * scale);
            appWindow.Move(new PointInt32(
                work.X + Math.Max(0, work.Width - actualSize.Width - margin),
                work.Y + Math.Max(0, work.Height - actualSize.Height - margin - _stackIndex * (actualSize.Height + gap))));
            state.Configured = true;
            return clientSize;
        }
        catch (Exception ex)
        {
            // Do not permanently mark the window as configured after transient native failure.
            if (++state.Failures <= 3)
                LogService.Error(logCategory, failureMessage, ex);
            return null;
        }
    }
}
