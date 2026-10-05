using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace SpankyhoOptimizer;
public sealed partial class MainWindow
{
    private static Brush? _materialButtonSurface;
    private static Brush? _materialPrimarySurface;
    private static Brush? _materialButtonEdge;
    private static Brush? _materialPrimaryEdge;

    private static void ApplyMaterialButtonAppearance(Button button, bool primary = false)
    {
        button.CornerRadius = new CornerRadius(16);
        button.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        button.FontSize = 13;
        button.MinHeight = 36;
        button.Padding = new Thickness(16,8,16,8);
        button.BorderThickness = new Thickness(1);
        button.Foreground = TextPrimaryBrush();
        button.FontWeight = FontWeights.SemiBold;
        if (_darkTheme)
        {
            button.Background = primary
                ? (_materialPrimarySurface ??= GlassBlueBrush())
                : (_materialButtonSurface ??= GlassPanelBrush());
            button.BorderBrush = primary
                ? (_materialPrimaryEdge ??= GlassBlueEdgeBrush())
                : (_materialButtonEdge ??= GlassEdgeBrush());
            if (primary)
            {
                button.Resources["ButtonBackgroundPointerOver"] = GlassBlueBrush();
                button.Resources["ButtonBackgroundPressed"] = ThemeColor(24,87,166);
                button.Resources["ButtonBorderBrushPointerOver"] = ThemeColor(108,108,108);
                button.Resources["ButtonBorderBrushPressed"] = ThemeColor(54,54,54);
            }
        }
        else
        {
            button.Background = primary ? ThemeColor(227,233,240) : ThemeColor(245,246,248);
            button.BorderBrush = primary ? ThemeColor(153,163,175) : ThemeColor(200,205,213);
        }
    }
}