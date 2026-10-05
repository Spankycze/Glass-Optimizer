$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\section-632'
New-Item (Join-Path $dir 'before') -ItemType Directory -Force|Out-Null
& .\ai-workflow.ps1 -Action start-task -TaskId 'section-632' -Summary 'Unify section headers, restore readable helper/status text, label UI motion toggle and add subtle black workspace depth.' -NextAction 'Save bounded visual changes then fresh build and six visual/navigation suites.'
$files=@('MainWindow.NeutralUi.cs','MainWindow.NetworkDashboard.cs','MainWindow.Tools.cs','MainWindow.PowerNetwork.cs','MainWindow.PowerPlan.cs','MainWindow.Spicetify.cs','MainWindow.Winget.cs','MainWindow.AiProgress.cs')
foreach($file in $files){Copy-Item (Join-Path $PWD ('src\SpankyhoOptimizer\'+$file)) (Join-Path $dir 'before') -Force}
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.NeutralUi.cs'
$text=[IO.File]::ReadAllText($path)
$text=$text.Replace('private static Brush? _neutralPrimarySurface;','private static Brush? _neutralPrimarySurface;'+[Environment]::NewLine+'    private static Brush? _neutralWorkspaceSurface;')
$text=$text.Replace('return new Border { Background = SurfaceAltBrush(), BorderBrush = SectionBorderBrush(),','return new Border { Background = _darkTheme ? (_neutralWorkspaceSurface ??= NeutralMaterialBrush(10, 3, 0)) : SurfaceAltBrush(), BorderBrush = _darkTheme ? NeutralCardEdge() : SectionBorderBrush(),')
$text=$text.Replace('BorderThickness = new Thickness(0,1,0,0), CornerRadius = new CornerRadius(0),','BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),')
$text=$text.Replace('Padding = new Thickness(0,24,0,24), Margin = new Thickness(0,0,0,8), Child = body','Padding = new Thickness(16,20,16,20), Margin = new Thickness(0,0,0,10), Child = body')
[IO.File]::WriteAllText($path,$text)
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.NetworkDashboard.cs'
$text=[IO.File]::ReadAllText($path).Replace('FontSize = 22,','FontSize = UseCompactSectionChrome ? 32 : 22,').Replace('FontWeight = FontWeights.SemiBold,'+[Environment]::NewLine+'            Foreground = TextPrimaryBrush()','FontWeight = UseCompactSectionChrome ? FontWeights.Light : FontWeights.SemiBold,'+[Environment]::NewLine+'            Foreground = TextPrimaryBrush()')
# Handle existing CRLF line endings as well.
$text=$text.Replace('FontWeight = FontWeights.SemiBold,','FontWeight = UseCompactSectionChrome ? FontWeights.Light : FontWeights.SemiBold,')
[IO.File]::WriteAllText($path,$text)
foreach($file in @('MainWindow.PowerNetwork.cs','MainWindow.PowerPlan.cs','MainWindow.Spicetify.cs','MainWindow.Winget.cs','MainWindow.AiProgress.cs')){
 $path=Join-Path $PWD ('src\SpankyhoOptimizer\'+$file)
 $text=[IO.File]::ReadAllText($path).Replace('Foreground = ButtonVioletBrush()','Foreground = TextSecondaryBrush()')
 [IO.File]::WriteAllText($path,$text)
}
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.Tools.cs'
$text=[IO.File]::ReadAllText($path).Replace('theme.Children.Add(motion);',@'
var motionRow = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 6, 0, 6) };
                motionRow.ColumnDefinitions.Add(new ColumnDefinition());
                motionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                motionRow.Children.Add(new TextBlock { Text = "Animace rozhran\u00ed", Foreground = TextPrimaryBrush(), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
                motion.Header = null;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(motion, "Animace rozhran\u00ed");
                Grid.SetColumn(motion, 1);
                motionRow.Children.Add(motion);
                theme.Children.Add(motionRow);
'@)
$text=$text.Replace('Motiv a animace současného cinematic UI','Motiv a plynulost rozhraní')
[IO.File]::WriteAllText($path,$text)
Write-Output 'SECTION632_FIX_SAVED'
