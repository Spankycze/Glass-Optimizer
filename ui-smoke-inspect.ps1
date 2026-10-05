$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$report=& {
Select-String -Path 'src\SpankyhoOptimizer\App.cs' -Pattern 'toggle.*smoke|visual.*smoke|release-ui-smoke|page-scroll-input-smoke' | ForEach-Object { "$($_.LineNumber):$($_.Line.Trim())" }
Get-ChildItem 'src\SpankyhoOptimizer' -File | Where-Object {$_.Name -match 'Visual|Capture|Toggle|Cli'} | Select-Object -ExpandProperty Name
Select-String -Path 'src\SpankyhoOptimizer\MainWindow.ServicePrewarm.cs' -Pattern '_themeSection|Build.*Shell|Build.*Card|Build.*Manager' -Context 1,2 | Out-String
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifacts\ui-neutral-redesign\smoke-inspect.txt'),($report|Out-String -Width 160))
Write-Output 'SMOKE_INSPECT_OK'