using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // One ON/OFF switch layout for tweak rows, context-menu settings and runtime controls.
    // Colors and knob geometry are supplied by the global WinUI ToggleSwitch theme resources.
    private static ToggleSwitch NormalizeToggleAppearance(ToggleSwitch toggle)
    {
        toggle.OnContent = string.Empty;
        toggle.OffContent = string.Empty;
        toggle.MinWidth = 50;
        toggle.MinHeight = 32;
        toggle.VerticalAlignment = VerticalAlignment.Center;
        return toggle;
    }
}
