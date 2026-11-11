using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace SpankyhoOptimizer;
public sealed partial class MainWindow
{
    // Keep action buttons genuinely translucent: visible purple or blue comes
    // from Windows wallpaper THROUGH glass, not an opaque purple button fill.
    // The actual requested ON/OFF switch treatment is controlled separately.
    private static Brush? _blueSecondarySurface;
    private static Brush? _blueSecondaryEdge;
    private static Brush BlueActionSurface(bool primary) => primary
        ? GlassBlueBrush()
        : _blueSecondarySurface ??= GlassGradient(
            Windows.UI.Color.FromArgb(62, 103, 183, 234),
            Windows.UI.Color.FromArgb(34, 33, 103, 158));

    private static Brush BlueActionEdge(bool primary) => primary
        ? GlassBlueEdgeBrush()
        : _blueSecondaryEdge ??= GlassGradient(
            Windows.UI.Color.FromArgb(99, 148, 210, 246),
            Windows.UI.Color.FromArgb(53, 66, 135, 186));

    private static void ApplyMaterialButtonAppearance(Button button, bool primary = false)
    {
        button.CornerRadius = new CornerRadius(UseGlassChrome ? 15 : 11);
        button.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        button.FontSize = 12.5;
        button.MinHeight = 36;
        button.Padding = new Thickness(15, 7, 15, 7);
        button.BorderThickness = new Thickness(1);
        button.Foreground = TextPrimaryBrush();
        AttachHoverMotion(button, primary ? 1.012f : 1.008f);
        button.FontWeight = FontWeights.SemiBold;

        if (_darkTheme)
        {
            button.Background = BlueActionSurface(primary);
            button.BorderBrush = BlueActionEdge(primary);
            // Preserve translucent glass in every visual state.
            button.Resources["ButtonBackground"] = BlueActionSurface(primary);
            button.Resources["ButtonBackgroundPointerOver"] = primary
                ? GlassBlueBrush() : GlassHoverBrush();
            button.Resources["ButtonBackgroundPressed"] = GlassPressedBrush();
            button.Resources["ButtonBorderBrush"] = BlueActionEdge(primary);
            button.Resources["ButtonBorderBrushPointerOver"] = GlassBlueEdgeBrush();
            button.Resources["ButtonBorderBrushPressed"] = GlassEdgeBrush();
        }
        else
        {
            button.Background = primary ? ThemeColor(132, 199, 245) : ThemeColor(225, 241, 255);
            button.BorderBrush = primary ? ThemeColor(73, 150, 212) : ThemeColor(146, 195, 230);
            button.Resources["ButtonBackgroundPointerOver"] = primary
                ? ThemeColor(108, 187, 239) : ThemeColor(207, 233, 252);
            button.Resources["ButtonBackgroundPressed"] = ThemeColor(179, 218, 244);
            button.Resources["ButtonBorderBrushPointerOver"] = ThemeColor(61, 153, 215);
            button.Resources["ButtonBorderBrushPressed"] = ThemeColor(83, 159, 211);
        }
        button.Resources["ButtonForegroundPointerOver"] = TextPrimaryBrush();
        button.Resources["ButtonForegroundPressed"] = TextPrimaryBrush();
    }
}