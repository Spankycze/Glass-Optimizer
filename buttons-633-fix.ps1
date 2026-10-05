$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\buttons-633'
New-Item (Join-Path $dir 'before') -ItemType Directory -Force|Out-Null
& .\ai-workflow.ps1 -Action start-task -TaskId 'buttons-633' -Summary 'Replace outlined text buttons with solid black material buttons, neutral edges and distinct primary/hover/pressed states.' -NextAction 'Save shared Quick/dialog appearance and theme resources; build and inspect actual screens.'
foreach($file in @('MainWindow.cs','MainWindow.UiPrimitives.cs','MainWindow.NeutralUi.cs','MainWindow.StyledDialogs.cs')){
 Copy-Item (Join-Path $PWD ('src\SpankyhoOptimizer\'+$file)) (Join-Path $dir 'before') -Force
}
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.MaterialButtons.cs'
$code=@'
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
'@
[IO.File]::WriteAllText($path,$code)
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.cs'
$text=[IO.File]::ReadAllText($path)
$text=$text.Replace('button.Background = UseNeutralSectionChrome ? ThemeColor(8,8,8) : ButtonVioletSoftBrush();'+[Environment]::NewLine+'        button.BorderBrush = ButtonVioletBorderBrush();','ApplyMaterialButtonAppearance(button);')
$text=[regex]::Replace($text,'button.Background = UseNeutralSectionChrome \? ThemeColor\(8,8,8\) : ButtonVioletSoftBrush\(\);\s*button.BorderBrush = ButtonVioletBorderBrush\(\);','ApplyMaterialButtonAppearance(button);')
# Helper call after all sizing to retain material corners.
$text=$text.Replace('// Shared compact buttons across every page.','// Shared material buttons across every page.')
[IO.File]::WriteAllText($path,$text)
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.UiPrimitives.cs'
$text=[IO.File]::ReadAllText($path)
$text=[regex]::Replace($text,'        button.Background = UseNeutralSectionChrome[^;]*;\s*button.Foreground = UseNeutralSectionChrome[^;]*;\s*button.BorderBrush = ButtonVioletBorderBrush\(\);','        ApplyMaterialButtonAppearance(button, primary: true);')
$text=$text.Replace('button.CornerRadius = new CornerRadius(4);','button.CornerRadius = new CornerRadius(7);')
[IO.File]::WriteAllText($path,$text)
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.NeutralUi.cs'
$text=[IO.File]::ReadAllText($path)
$anchor='        // Disabled/busy labels remain readable without looking actionable.'
$resources=@'
        resources["ButtonBackground"] = _darkTheme ? NeutralDepthBrush(26,13) : ThemeColor(245,246,248);
        resources["ButtonBackgroundPointerOver"] = _darkTheme ? NeutralDepthBrush(36,21) : ThemeColor(232,235,240);
        resources["ButtonBackgroundPressed"] = _darkTheme ? NeutralDepthBrush(14,9) : ThemeColor(220,225,232);
        SetOwnedThemeBrush(resources, "ButtonBorderBrush", _darkTheme ? ThemeColor(50,50,50) : ThemeColor(200,205,213));
        SetOwnedThemeBrush(resources, "ButtonBorderBrushPointerOver", _darkTheme ? ThemeColor(86,86,86) : ThemeColor(160,170,182));
        SetOwnedThemeBrush(resources, "ButtonBorderBrushPressed", _darkTheme ? ThemeColor(38,38,38) : ThemeColor(145,155,167));
        SetOwnedThemeBrush(resources, "ButtonForegroundPointerOver", TextPrimaryBrush());
        SetOwnedThemeBrush(resources, "ButtonForegroundPressed", TextPrimaryBrush());
'@
if(!$text.Contains($anchor)){throw 'Resource anchor missing'}
$text=$text.Replace($anchor,$resources+[Environment]::NewLine+$anchor)
[IO.File]::WriteAllText($path,$text)
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.StyledDialogs.cs'
$text=[IO.File]::ReadAllText($path).Replace('        EnsureAccessibleButton(b, text);','        ApplyMaterialButtonAppearance(b, primary);'+[Environment]::NewLine+'        EnsureAccessibleButton(b, text);').Replace('            EnsureAccessibleButton(button, text);','            ApplyMaterialButtonAppearance(button, primaryStyle);'+[Environment]::NewLine+'            EnsureAccessibleButton(button, text);')
$text=[regex]::Replace($text,'        b.PointerEntered \+= \(_, _\) => b.BorderBrush = DialogNeutralHoverBorderBrush\(\);\s*        b.PointerExited \+= \(_, _\) => b.BorderBrush = DialogNeutralBorderBrush\(\);','')
[IO.File]::WriteAllText($path,$text)
Write-Output 'BUTTONS633_FIX_SAVED'
