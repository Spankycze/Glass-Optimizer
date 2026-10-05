param(
  [ValidateSet('etwprof','perfview')][string]$Mode='etwprof',
  [string]$SmokeArg='--performance-smoke'
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
if(-not(Test-Path $exe)){& (Join-Path $root 'build-fast.ps1')|Out-Null}
$out=Join-Path $root ('artifacts\profilers\'+(Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Force -Path $out|Out-Null

if($Mode -eq 'etwprof'){
  $tool=Get-ChildItem (Join-Path $root '.tools\etwprof') -Recurse -File -Filter etwprof.exe|Select-Object -First 1
  if(-not $tool){throw 'etwprof.exe missing'}
  $etl=Join-Path $out 'spanky-etwprof.etl'
  & $tool.FullName profile --output=$etl --compress=etw --cswitch --scache -- $exe $SmokeArg
  if($LASTEXITCODE -ne 0){throw 'etwprof failed'}
  Write-Output ('ETWPROF_OK '+$etl)
  exit 0
}

$perf=Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -File -Filter PerfView.exe -ErrorAction SilentlyContinue|Select-Object -First 1
if(-not $perf){throw 'PerfView.exe missing'}
$data=Join-Path $out 'spanky-perfview.etl.zip'
# PerfView collection is intentionally opt-in because it is heavier than etwprof.
& $perf.FullName /AcceptEULA /NoGui /DataFile:$data /MaxCollectSec:60 collect "$exe $SmokeArg"
if($LASTEXITCODE -ne 0){throw 'PerfView collect failed'}
Write-Output ('PERFVIEW_OK '+$data)
