$ErrorActionPreference='Stop'
$debug=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$staged=Join-Path $PSScriptRoot '.buildcache\staged-debug'
$selected=$debug
if((Test-Path "$staged\SpankyhoOptimizer.dll") -and (Get-Item "$staged\SpankyhoOptimizer.dll").LastWriteTimeUtc -gt (Get-Item "$debug\SpankyhoOptimizer.dll").LastWriteTimeUtc){$selected=$staged}
$exe=Join-Path $selected 'SpankyhoOptimizer.exe'
if(-not(Test-Path $exe)){throw 'Selected UI executable missing'}
Write-Output $exe