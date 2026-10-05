$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$report = & {
$src='src\SpankyhoOptimizer'
"FILES"
Get-ChildItem $src -Filter '*.cs' | Where-Object Name -match 'Theme|Overview|Primit|Style|Preview' | Select-Object -ExpandProperty Name
"MATCHES"
Select-String -Path "$src\MainWindow*.cs" -Pattern '_themeSection|private.*QuickButton|private.*SectionCard|private.*SectionHeader|private.*OverviewCardBackgroundBrush|private.*OverviewNeonBorderBrush' | ForEach-Object { "$($_.Filename):$($_.LineNumber):$($_.Line.Trim())" }
"SCRIPTS"
Get-ChildItem -File | Where-Object Name -match 'screen|capture|release-ui|smoke|regression' | Select-Object -ExpandProperty Name
} | Out-String -Width 220
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'ui-redesign-inspect.txt'),$report)
Write-Output 'INSPECT_OK'
