$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$work=Join-Path $PSScriptRoot 'artifacts\ui-neutral-redesign'
New-Item -ItemType Directory -Force "$work\before","$work\backup" | Out-Null
$files=@('MainWindow.Theme.cs','MainWindow.Shell.cs','MainWindow.Dashboard.cs','MainWindow.DashboardModern.cs','MainWindow.StyledDialogs.cs','MainWindow.UiPrimitives.cs','MainWindow.cs')
foreach($f in $files){Copy-Item "src\SpankyhoOptimizer\$f" "$work\backup\$f"}
$protected=@(Get-ChildItem 'src\SpankyhoOptimizer' -Filter 'MainWindow.Overview*.cs')+@(Get-Item 'src\SpankyhoOptimizer\Assets\HeroTargetExact.png')
$protected | ForEach-Object { [pscustomobject]@{path=$_.FullName;hash=(Get-FileHash $_.FullName).Hash} } | ConvertTo-Json | Set-Content "$work\protected.json" -Encoding UTF8
& .\ai-workflow.ps1 -Action start-task -TaskId 'neutral-ui-redesign' -Title 'Neutral black UI; preserve Overview and header' -Area ui
$exe=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
[IO.File]::WriteAllText("$work\baseline-version.txt",(Get-Item $exe).VersionInfo.ProductVersion)
$p=Start-Process $exe -ArgumentList '--visual-capture-smoke' -PassThru -WorkingDirectory (Split-Path $exe)
if(-not $p.WaitForExit(120000)){throw 'Baseline visual capture timeout'}
if($p.ExitCode -ne 0){throw "Baseline visual exit=$($p.ExitCode)"}
Copy-Item 'C:\ProgramData\SpankyhoOptimizer\visual-current\*.png' "$work\before" -Force
$report=& {
Select-String -Path 'src\SpankyhoOptimizer\MainWindow*.cs' -Pattern 'private.*BuildSectionSurface|private.*BuildSectionHero|private.*TweakCard|private.*TweakRow|private.*Toggle|--visual|--section|--startup' | ForEach-Object {"$($_.Filename):$($_.LineNumber):$($_.Line.Trim())"}
Get-ChildItem "$work\before" -File | Select-Object -ExpandProperty Name
} | Out-String -Width 200
[IO.File]::WriteAllText("$work\baseline.txt",$report)
Write-Output 'UI_BASELINE_OK'
