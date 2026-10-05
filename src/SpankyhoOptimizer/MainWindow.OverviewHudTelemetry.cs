using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{


    private Border BuildHudTelemetryCard(string label, string subtitle, SolidColorBrush accent, out GaugeRefs refs)
    { return BuildBlackTelemetryCard(label, subtitle, accent, out refs); }

}
