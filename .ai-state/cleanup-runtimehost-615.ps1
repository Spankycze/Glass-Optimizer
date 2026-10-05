param([Parameter(Mandatory=$true)][int]$KeepPid)
$ErrorActionPreference='Stop'
Get-Process 'SpankyhoOptimizer.RuntimeHost' -ErrorAction SilentlyContinue |
  Where-Object { $_.SessionId -eq 1 -and $_.Id -ne $KeepPid } |
  Stop-Process -Force -ErrorAction Stop
