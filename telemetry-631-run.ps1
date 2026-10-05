$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$text=[IO.File]::ReadAllText((Join-Path $PWD 'changelog-630-build.ps1')).Replace('changelog-630','telemetry-631').Replace('Fresh630','Fresh631').Replace('CHANGELOG630','RAM631').Replace('Old code regression failed as expected: snapshot/history refresh overwrote release rows. Fix separates caption and removes batch-history writes to changelog rows.','Old code real telemetry regression failed. RAM summary now shares live sample with gauge and late snapshots retain latest sample.').Replace('src\SpankyhoOptimizer\MainWindow.ChangelogRegression.cs','src\SpankyhoOptimizer\MainWindow.RamTelemetryRegression.cs')
[IO.File]::WriteAllText((Join-Path $PWD 'telemetry-631-build.ps1'),$text)
& .\telemetry-631-build.ps1
