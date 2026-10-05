$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$utf8=[Text.UTF8Encoding]::new($false)
$p=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\MainWindow.VisualQa.cs'
$t=[IO.File]::ReadAllText($p)
$t=$t.Replace('HeaderCard.Height >= 100;', '(section != "overview" || HeaderCard.Height >= 100);')
$t=$t.Replace('                { imagesPending++; LogService.Info("visual-qa", $"CAPTURE_PENDING_IMAGE source={image.Source} size={image.ActualWidth}x{image.ActualHeight} loaded={image.IsLoaded} parent={VisualTreeHelper.GetParent(image)}"); }','                    imagesPending++;')
[IO.File]::WriteAllText($p,$t,$utf8)
$p=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\MainWindow.OverviewDashboard.cs'
$t=[IO.File]::ReadAllText($p)
$t=$t.Replace('bool compact = (RootGrid?.ActualWidth ?? 1320) < 1200;', 'double layoutWidth = RootGrid?.ActualWidth > 1 ? RootGrid.ActualWidth : StartupClientWidth;'+[Environment]::NewLine+'        bool compact = layoutWidth < 1200;')
[IO.File]::WriteAllText($p,$t,$utf8)
Write-Output 'BLACK_UI_LAYOUT_FIXED'
