using System;
using System.Numerics;
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
        brush.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(72, 0, 0, 0)));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(.78f, Color.FromArgb(72, 0, 0, 0)));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(.88f, Color.FromArgb(22, 210, 210, 210)));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(.95f, _lit
            ? Color.FromArgb(108, 245, 245, 245) : Color.FromArgb(32, 235, 235, 235)));
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
            ? Color.FromArgb(108, 245, 245, 245) : Color.FromArgb(32, 235, 235, 235) });
        brush.GradientStops.Add(new GradientStop { Offset = .55, Color = Color.FromArgb(22, 210, 210, 210) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(72, 0, 0, 0) });
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
        RootGrid.Children.Add(_glassRim);
        RootGrid.SizeChanged += (_, _) => UpdateShapedGlassRim();
        UpdateShapedGlassRim();
    }

    private static PathGeometry RimCorner(Point outerStart, Point outerEnd, Point innerStart, Point innerEnd)
    {
        var figure = new PathFigure { StartPoint = outerStart, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment { Point = outerEnd, Size = new Size(18,18), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = innerStart });
        figure.Segments.Add(new ArcSegment { Point = innerEnd, Size = new Size(14,14), SweepDirection = SweepDirection.Counterclockwise });
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
        if (w < 40 || h < 40) return;
        static void Side(ShapePath path, double x, double y, double width, double height)
        {
            Canvas.SetLeft(path, x); Canvas.SetTop(path, y);
            path.Width = width; path.Height = height;
            path.Data = new RectangleGeometry { Rect = new Rect(0,0,width,height) };
        }
        Side(_glassRimSides[0],19,1,w-38,4);
        Side(_glassRimSides[1],w-5,19,4,h-38);
        Side(_glassRimSides[2],19,h-5,w-38,4);
        Side(_glassRimSides[3],1,19,4,h-38);
        _glassRimCorners[0].Data = RimCorner(new Point(0,18),new Point(18,0),new Point(18,4),new Point(4,18));
        _glassRimCorners[1].Data = RimCorner(new Point(0,0),new Point(18,18),new Point(14,18),new Point(0,4));
        _glassRimCorners[2].Data = RimCorner(new Point(18,0),new Point(0,18),new Point(0,14),new Point(14,0));
        _glassRimCorners[3].Data = RimCorner(new Point(18,18),new Point(0,0),new Point(4,0),new Point(18,14));
        for (int i = 0; i < 4; i++)
        {
            var corner = _glassRimCorners[i]; corner.Width = corner.Height = 18;
            Canvas.SetLeft(corner, i is 0 or 3 ? 1 : w-19);
            Canvas.SetTop(corner, i < 2 ? 1 : h-19);
        }
    }
}
