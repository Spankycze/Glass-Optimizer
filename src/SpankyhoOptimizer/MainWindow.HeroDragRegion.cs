using System;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // Native caption geometry is a single non-client input region: unlike
    // pointer-pressed window moves, it preserves Windows dragging, snapping,
    // touch and double-click maximize without a custom mouse event loop.
    private (int Width, int BarHeight, int HeroHeight, int Sidebar, int CaptionButtons, bool Overview)? _heroDragSignature;
    // InputNonClientPointerSource is associated with this window ID, not with
    // individual resize/layout events. Reuse it instead of repeated native
    // WinRT acquisition on every 32ms live resize tick.
    private InputNonClientPointerSource? _cachedHeroDragPointerSource;
    private int _heroDragPointerSourceAcquireCount;

    private static RectInt32[] BuildHeaderDragRectangles(
        int width, int barHeight, int heroHeight, int sidebar, int captionButtons)
    {
        if (width <= 0 || barHeight <= 0) return Array.Empty<RectInt32>();
        int titleWidth = Math.Max(1, width - Math.Max(0, captionButtons));
        var title = new RectInt32 { X = 0, Y = 0,
            Width = titleWidth, Height = barHeight };

        // Only Overview has a full-width decorative hero below the actual
        // titlebar. The sidebar starts immediately underneath the titlebar;
        // its navigation hit targets must remain clickable.
        if (heroHeight <= barHeight || sidebar >= width)
            return new[] { title };

        var hero = new RectInt32 {
            X = sidebar, Y = barHeight,
            Width = width - sidebar, Height = heroHeight - barHeight
        };
        return new[] { title, hero };
    }

    private void UpdateHeroDragRegions()
    {
        if (!_customTitleBarPrimed || _windowLifetimeCts.IsCancellationRequested ||
            RootGrid.XamlRoot == null) return;

        double dipWidth = RootGrid.ActualWidth;
        if (!double.IsFinite(dipWidth) || dipWidth < ShellSidebarWidth + 50)
            return;

        double scale = RootGrid.XamlRoot.RasterizationScale;
        if (!double.IsFinite(scale) || scale <= 0) return;
        int width = (int)Math.Round(dipWidth * scale);
        int barHeight = (int)Math.Round(AppTitleBar.Height * scale);
        bool overview = string.Equals(_section, "overview", StringComparison.OrdinalIgnoreCase);
        int heroHeight = overview && HeaderCard.Visibility == Microsoft.UI.Xaml.Visibility.Visible
            ? (int)Math.Round(HeaderCard.Height * scale)
            : barHeight;
        int sidebar = (int)Math.Round(ShellSidebarWidth * scale);

        try
        {
            var appWindow = _cachedCaptionAppWindow ??
                AppWindow.GetFromWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                        WindowNative.GetWindowHandle(this)));
            _cachedCaptionAppWindow = appWindow;
            var titleBar = _cachedCaptionTitleBar ?? appWindow.TitleBar;
            _cachedCaptionTitleBar = titleBar;
            // Keep the three Windows caption buttons (and their Snap Layout
            // hover hit testing) outside the titlebar drag region. Native
            // inset can change independently of client geometry/DPI.
            int captionButtons = Math.Max(
                (int)Math.Ceiling(AppTitleBar.Padding.Right * scale),
                titleBar.RightInset);
            var signature = (width, barHeight, heroHeight, sidebar, captionButtons, overview);
            if (_heroDragSignature == signature) return;

            var regions = BuildHeaderDragRectangles(
                width, barHeight, heroHeight, sidebar, captionButtons);
            var pointerSource = _cachedHeroDragPointerSource;
            if (pointerSource == null)
            {
                pointerSource = InputNonClientPointerSource.GetForWindowId(appWindow.Id);
                _cachedHeroDragPointerSource = pointerSource;
                _heroDragPointerSourceAcquireCount++;
            }
            pointerSource.SetRegionRects(NonClientRegionKind.Caption, regions);
            _heroDragSignature = signature;
        }
        catch (Exception ex)
        {
            // A platform-specific failure must not remove the original
            // SetTitleBar behavior. Retry after the next real layout change.
            _heroDragSignature = null;
            _cachedHeroDragPointerSource = null;
            SpankyhoOptimizer.Services.LogService.Error(
                "window", "Extended Overview caption region not available", ex);
        }
    }

    private void VerifyHeroDragRegions()
    {
        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                WindowNative.GetWindowHandle(this)));
        var regions = InputNonClientPointerSource.GetForWindowId(appWindow.Id)
            .GetRegionRects(NonClientRegionKind.Caption);
        int expected = _section == "overview" ? 2 : 1;
        if (regions.Length != expected)
            throw new InvalidOperationException(
                $"Caption regions: {regions.Length}, expected {expected} for {_section}.");
        if (regions[0].X != 0 || regions[0].Y != 0 ||
            regions[0].Height < 30 || regions[0].Width <= 0)
            throw new InvalidOperationException("Original titlebar drag region missing.");
        if (expected == 2)
        {
            var hero = regions[1];
            int sidebar = (int)Math.Round(ShellSidebarWidth *
                RootGrid.XamlRoot.RasterizationScale);
            if (hero.X != sidebar || hero.Y != regions[0].Height ||
                hero.Width < 300 || hero.Height < 35)
                throw new InvalidOperationException(
                    "Extended hero caption region overlaps navigation or does not cover header.");
        }
    }
}
