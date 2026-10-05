using System;
using System.Numerics;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Color = Windows.UI.Color;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace SpankyhoOptimizer;

internal sealed class GlassRimCornerBrush : XamlCompositionBrushBase
{
    private readonly Vector2 _center;
    private readonly bool _lit;
    internal GlassRimCornerBrush(Vector2 center, bool lit) { _center = center; _lit = lit; }
    protected override void OnConnected()
    {
        if (CompositionBrush != null) return;
        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var brush = compositor.CreateRadialGradientBrush();
        brush.CenterPoint = _center;
        brush.EllipseRadius = Vector2.One;
        brush.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(120, 0, 0, 0)));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(.66f, Color.FromArgb(120, 0, 0, 0)));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(.82f, Color.FromArgb(24, 220, 220, 220)));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(.95f, _lit
            ? Color.FromArgb(130, 245, 245, 245) : Color.FromArgb(50, 235, 235, 235)));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(24, 0, 0, 0)));
        CompositionBrush = brush;
    }
    protected override void OnDisconnected()
    {
        CompositionBrush?.Dispose();
        CompositionBrush = null;
    }
}

public sealed partial class MainWindow
{
    private Canvas? _glassRim;
    private (double Width, double Height, double Scale, bool Enabled)? _rimNativeSignature;
    private (double Width, double Height)? _rimGeometrySize;
    private readonly ShapePath[] _glassRimSides = new ShapePath[4];
    private readonly ShapePath[] _glassRimCorners = new ShapePath[4];

    private static Brush RimProfile(bool vertical, bool reverse, bool lit)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(vertical ? 0 : (reverse ? 1 : 0), vertical ? (reverse ? 1 : 0) : 0),
            EndPoint = new Point(vertical ? 0 : (reverse ? 0 : 1), vertical ? (reverse ? 0 : 1) : 0)
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(24, 0, 0, 0) });
        brush.GradientStops.Add(new GradientStop { Offset = .22, Color = lit
            ? Color.FromArgb(130, 245, 245, 245) : Color.FromArgb(50, 235, 235, 235) });
        brush.GradientStops.Add(new GradientStop { Offset = .55, Color = Color.FromArgb(24, 220, 220, 220) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(120, 0, 0, 0) });
        return brush;
    }

    private void AddShapedGlassRim()
    {
        _glassRim = new Canvas { IsHitTestVisible = false, Tag = "shaped-glass-rim" };
        Canvas.SetZIndex(_glassRim, 300);
        var sideBrushes = new[] { RimProfile(true, false, true), RimProfile(false, true, false),
            RimProfile(true, true, false), RimProfile(false, false, true) };
        var centers = new[] { new Vector2(1,1), new Vector2(0,1), Vector2.Zero, new Vector2(1,0) };
        for (int i = 0; i < 4; i++)
        {
            _glassRimSides[i] = new ShapePath { Fill = sideBrushes[i], IsHitTestVisible = false };
            _glassRimCorners[i] = new ShapePath { Fill = new GlassRimCornerBrush(centers[i], i is 0 or 3), IsHitTestVisible = false };
            _glassRim.Children.Add(_glassRimSides[i]);
            _glassRim.Children.Add(_glassRimCorners[i]);
        }
        _rimGeometrySize = null;
        RootGrid.Children.Add(_glassRim);
        RootGrid.Loaded += (_, _) => UpdateShapedGlassRim();
        RootGrid.SizeChanged += (_, _) => UpdateShapedGlassRim();
        UpdateShapedGlassRim();
    }

    private void VerifyRoundedGlassWindow()
    {
        UpdateShapedGlassRim();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized }) return;
        if (!NativeTitleBarService.VerifyRoundedGlassRegion(WindowNative.GetWindowHandle(this)))
            throw new InvalidOperationException("Glass window corners are not physically rounded.");
        if (_mainTabsShell?.Width != 200) throw new InvalidOperationException("Compact sidebar width regression.");
        LogService.Info("visual-qa", "GLASS_WINDOW_SHAPE_OK radius=25 sidebar=200");
    }
    private static PathGeometry RimCorner(Point outerStart, Point outerEnd, Point innerStart, Point innerEnd)
    {
        var figure = new PathFigure { StartPoint = outerStart, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment { Point = outerEnd, Size = new Size(24,24), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = innerStart });
        figure.Segments.Add(new ArcSegment { Point = innerEnd, Size = new Size(16,16), SweepDirection = SweepDirection.Counterclockwise });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void UpdateShapedGlassRim()
    {
        if (_glassRim == null) return;
        bool maximized = AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized };
        _glassRim.Visibility = UseGlassChrome && !maximized ? Visibility.Visible : Visibility.Collapsed;
        double w = RootGrid.ActualWidth, h = RootGrid.ActualHeight;
        if (w < 50 || h < 50) return;
        bool enabled = UseGlassChrome && !maximized;
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        var nativeSignature = (w,h,scale,enabled);
        if (RootGrid.XamlRoot != null && _rimNativeSignature != nativeSignature)
        {
            // Set before the native call, which can synchronously send window-size messages.
            _rimNativeSignature = nativeSignature;
            if (!NativeTitleBarService.TrySetRoundedGlassRegion(WindowNative.GetWindowHandle(this), enabled, 25 * scale, out int error))
            {
                _rimNativeSignature = null;
                LogService.Info("window", $"GLASS_WINDOW_REGION_FAILED error={error}");
            }
        }
        if (_rimGeometrySize == (w,h)) return;
        _rimGeometrySize = (w,h);
        static void Side(ShapePath path, double x, double y, double width, double height)
        {
            Canvas.SetLeft(path, x); Canvas.SetTop(path, y);
            path.Width = width; path.Height = height;
            path.Data = new RectangleGeometry { Rect = new Rect(0,0,width,height) };
        }
        Side(_glassRimSides[0],25,1,w-50,8);
        Side(_glassRimSides[1],w-9,25,8,h-50);
        Side(_glassRimSides[2],25,h-9,w-50,8);
        Side(_glassRimSides[3],1,25,8,h-50);
        _glassRimCorners[0].Data = RimCorner(new Point(0,24),new Point(24,0),new Point(24,8),new Point(8,24));
        _glassRimCorners[1].Data = RimCorner(new Point(0,0),new Point(24,24),new Point(16,24),new Point(0,8));
        _glassRimCorners[2].Data = RimCorner(new Point(24,0),new Point(0,24),new Point(0,16),new Point(16,0));
        _glassRimCorners[3].Data = RimCorner(new Point(24,24),new Point(0,0),new Point(8,0),new Point(24,16));
        for (int i = 0; i < 4; i++)
        {
            var corner = _glassRimCorners[i]; corner.Width = corner.Height = 24;
            Canvas.SetLeft(corner, i is 0 or 3 ? 1 : w-25);
            Canvas.SetTop(corner, i < 2 ? 1 : h-25);
        }
    }
}
