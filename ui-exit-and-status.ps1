$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$exe=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$p=Start-Process $exe -ArgumentList '--request-exit' -PassThru
if(-not $p.WaitForExit(6000)){throw 'Exit request timeout'}
$out=& {
Get-Process SpankyhoOptimizer -ErrorAction SilentlyContinue | Select-Object Id,StartTime | Format-Table | Out-String
$s=Get-Content '.ai-state\status.json' -Raw|ConvertFrom-Json
$s|ConvertTo-Json -Depth 4
$latest=Get-ChildItem '.dc-logs' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 2
foreach($file in $latest){$file.FullName;Get-Content $file.FullName -Tail 15}
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifacts\ui-neutral-redesign\latest-build-status.txt'),($out|Out-String -Width 180))
Write-Output 'EXIT_AND_STATUS_OK'