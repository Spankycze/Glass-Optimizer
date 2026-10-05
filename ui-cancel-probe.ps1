$ErrorActionPreference='Stop'
$exe=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
Get-CimInstance Win32_Process -Filter "Name='SpankyhoOptimizer.exe'" | Where-Object {$_.ExecutablePath -eq $exe -and $_.CommandLine -match '\-\-open-section=network' -and $_.CommandLine -match '\-\-theme-render-smoke'} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Write-Output "COMBINED_PROBE_CANCELLED pid=$($_.ProcessId)" }
