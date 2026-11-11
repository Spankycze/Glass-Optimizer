using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{

    private Border BuildDashboardCard(UIElement child, Thickness? padding = null)
    {
        var card = new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = padding ?? new Thickness(16),
            Child = child,
            // Reuse the same native shadow among legacy dashboard cards too.
            // Each Border keeps its independent 3D translation and content.
            Shadow = UseCompactSectionChrome ? null : (_sharedSurfaceShadow ??= new ThemeShadow())
        };
        card.Translation = new System.Numerics.Vector3(0, 0, UseCompactSectionChrome ? 0 : (_darkTheme ? 7 : 3));
        if (!UseNeutralSectionChrome) AttachHoverMotion(card, 1.004f);
        return card;
    }









}

