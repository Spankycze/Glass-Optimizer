$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\fonts-634'
New-Item (Join-Path $dir 'before') -ItemType Directory -Force|Out-Null
& .\ai-workflow.ps1 -Action start-task -TaskId 'fonts-634' -Summary 'Use Segoe UI Variable typography, stronger page/dialog titles, clearer metric numbers and readable action labels.' -NextAction 'Save typography changes, build and verify actual captures at all tested sizes.'
foreach($file in @('App.xaml','MainWindow.NeutralUi.cs','MainWindow.NetworkDashboard.cs','MainWindow.BlackUi.cs','MainWindow.MaterialButtons.cs','MainWindow.UiPrimitives.cs','MainWindow.cs','MainWindow.StyledDialogs.cs')){
 Copy-Item (Join-Path $PWD ('src\SpankyhoOptimizer\'+$file)) (Join-Path $dir 'before') -Force
}
$path=Join-Path $PWD 'src\SpankyhoOptimizer\App.xaml'
$text=[IO.File]::ReadAllText($path)
$anchor='            <SolidColorBrush x:Key="ToggleSwitchFillOn"'
$font=@'
            <FontFamily x:Key="ContentControlThemeFontFamily">Segoe UI Variable Text, Segoe UI</FontFamily>
            <FontFamily x:Key="ControlContentThemeFontFamily">Segoe UI Variable Text, Segoe UI</FontFamily>
            <Style TargetType="TextBlock">
                <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/>
            </Style>
'@
if(!$text.Contains($anchor)){throw 'Font resource anchor missing'}
$text=$text.Replace($anchor,$font+[Environment]::NewLine+$anchor)
[IO.File]::WriteAllText($path,$text)
foreach($file in @('MainWindow.NeutralUi.cs','MainWindow.BlackUi.cs')){
 $path=Join-Path $PWD ('src\SpankyhoOptimizer\'+$file)
 $text=[IO.File]::ReadAllText($path)
 $text=$text.Replace('FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.Light,','FontSize = 30, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,')
 $text=$text.Replace('FontSize = 32, FontWeight = FontWeights.Light,','FontSize = 30, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold,')
 $text=$text.Replace('FontSize = 34, FontWeight = FontWeights.Light,','FontSize = 34, FontWeight = FontWeights.Normal,')
 [IO.File]::WriteAllText($path,$text)
}
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.NetworkDashboard.cs'
$text=[IO.File]::ReadAllText($path).Replace('FontSize = UseCompactSectionChrome ? 32 : 22,','FontSize = UseCompactSectionChrome ? 30 : 22,'+[Environment]::NewLine+'            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable Display, Segoe UI"),').Replace('FontWeight = UseCompactSectionChrome ? FontWeights.Light : FontWeights.SemiBold,','FontWeight = FontWeights.SemiBold,')
[IO.File]::WriteAllText($path,$text)
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.MaterialButtons.cs'
$text=[IO.File]::ReadAllText($path).Replace('button.CornerRadius = new CornerRadius(7);','button.CornerRadius = new CornerRadius(7);'+[Environment]::NewLine+'        button.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");'+[Environment]::NewLine+'        button.FontSize = 12.5;')
[IO.File]::WriteAllText($path,$text)
foreach($file in @('MainWindow.cs','MainWindow.UiPrimitives.cs')){
 $path=Join-Path $PWD ('src\SpankyhoOptimizer\'+$file)
 $text=[IO.File]::ReadAllText($path).Replace('b.FontSize = 12;','b.FontSize = 12.5;').Replace('button.FontSize = 12;','button.FontSize = 12.5;')
 [IO.File]::WriteAllText($path,$text)
}
$path=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.StyledDialogs.cs'
$text=[IO.File]::ReadAllText($path).Replace('FontSize = 25, FontWeight = FontWeights.Light,','FontSize = 24, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold,')
[IO.File]::WriteAllText($path,$text)
Write-Output 'FONTS634_FIX_SAVED'
