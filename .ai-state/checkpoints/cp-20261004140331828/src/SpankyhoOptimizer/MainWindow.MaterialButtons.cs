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
        button.CornerRadius = new CornerRadius(7);
        button.BorderThickness = new Thickness(1);
        button.Foreground = TextPrimaryBrush();
        button.FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal;
        if (_darkTheme)
        {
            button.Background = primary
                ? (_materialPrimarySurface ??= NeutralDepthBrush(42, 22))
                : (_materialButtonSurface ??= NeutralDepthBrush(26, 13));
            button.BorderBrush = primary
                ? (_materialPrimaryEdge ??= NeutralDepthBrush(82, 38))
                : (_materialButtonEdge ??= NeutralDepthBrush(58, 26));
            if (primary)
            {
                button.Resources["ButtonBackgroundPointerOver"] = NeutralDepthBrush(52, 30);
                button.Resources["ButtonBackgroundPressed"] = NeutralDepthBrush(22, 16);
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