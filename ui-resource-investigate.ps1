$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$out=& {
Get-ChildItem 'src\SpankyhoOptimizer\MainWindow*.cs' | Select-String 'Resources\[|BrushCache.Clear|SpankyControlOnBrush' | Select-Object Path,LineNumber,Line | Format-Table -Wrap -AutoSize | Out-String -Width 180
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifacts\ui-neutral-redesign\resource-uses.txt'),($out|Out-String -Width 180))
Write-Output 'RESOURCE_USES_OK'