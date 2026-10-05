$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$p=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.OverviewDashboard.cs';$s=[IO.File]::ReadAllText($p)
$start=$s.IndexOf('            var header = new Grid { ColumnSpacing = 10, Visibility = Visibility.Collapsed };');$end=$s.IndexOf('            long headerBuildMs = sw.ElapsedMilliseconds;', $start)
if($start -lt 0 -or $end -lt 0){throw 'Hidden header bounds missing'}
$s=$s.Substring(0,$start)+'            long headerBuildMs = 0;'+"`n"+$s.Substring($end+'            long headerBuildMs = sw.ElapsedMilliseconds;'.Length)
$old='                        var chart = BuildOverviewPerformanceChart(); chart.Visibility = Visibility.Collapsed;'
if(!$s.Contains($old)){throw 'Hidden chart anchor missing'}
$s=$s.Replace($old,'                        // Individual visible sparklines replace the unused aggregate chart.').Replace('                        Grid.SetColumn(chart, 1);','').Replace('                        body.Children.Add(chart);','')
[IO.File]::WriteAllText($p,$s)
$p=Join-Path $PWD 'src\SpankyhoOptimizer\MainWindow.StyledDialogs.cs';$s=[IO.File]::ReadAllText($p)
$s=[regex]::Replace($s,'        bool hideDefaultCancel =[^\r\n]*\r?\n            \|\|[^\r\n]*;\r?\n','')
[IO.File]::WriteAllText($p,$s)
Write-Output 'HIDDEN_OVERVIEW_RENDERING_REMOVED'
