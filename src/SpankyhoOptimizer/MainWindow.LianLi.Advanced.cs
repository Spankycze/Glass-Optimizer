using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private readonly SolidColorBrush?[] _fanPreviewInnerBrushes = new SolidColorBrush?[4];
    private readonly SolidColorBrush?[] _fanPreviewOuterBrushes = new SolidColorBrush?[4];

    private Grid BuildFanRingPreview()
    {
        var root = new Grid { ColumnSpacing = 12, Padding = new Thickness(12) };
        for (int i = 0; i < 4; i++)
        {
            root.ColumnDefinitions.Add(new ColumnDefinition());
            var fan = new Grid
            {
                Width = 54,
                Height = 54,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _fanPreviewOuterBrushes[i] = new SolidColorBrush(ParseColor(_lianLiPreviewA));
            _fanPreviewInnerBrushes[i] = new SolidColorBrush(ParseColor(_lianLiPreviewB));

            fan.Children.Add(new Ellipse
            {
                Width = 52,
                Height = 52,
                Stroke = _fanPreviewOuterBrushes[i],
                StrokeThickness = 7
            });
            fan.Children.Add(new Ellipse
            {
                Width = 30,
                Height = 30,
                Stroke = _fanPreviewInnerBrushes[i],
                StrokeThickness = 5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            fan.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = TextSecondaryBrush(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            Grid.SetColumn(fan, i);
            root.Children.Add(fan);
        }

        return root;
    }

    private void UpdateFanRingPreview(Color a, Color b)
    {
        for (int i = 0; i < 4; i++)
        {
            double phase = (i * 0.16 + _lianLiPreviewPhase / 120d) % 1d;
            double wave = (Math.Sin(phase * Math.PI * 2d) + 1d) * 0.5d;
            Color inner = LerpColor(a, b, wave);
            Color outer = LerpColor(b, a, wave);

            if (_fanPreviewInnerBrushes[i] != null)
                _fanPreviewInnerBrushes[i]!.Color = inner;
            if (_fanPreviewOuterBrushes[i] != null)
                _fanPreviewOuterBrushes[i]!.Color = outer;
        }
    }
}
