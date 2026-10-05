param(
  [int]$ProcessId,
  [int]$DurationSeconds=30,
  [switch]$CollectDump,
  [switch]$CollectGcDump,
  [switch]$CollectTrace
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if($ProcessId -le 0){throw 'Use -Pid'}
$outDir=Join-Path $root ('artifacts\diagnostics\monitor-'+(Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Force -Path $outDir|Out-Null

$proc=Get-Process -Id $ProcessId -ErrorAction Stop
Write-Output ('MONITOR_TARGET pid='+$ProcessId+' name='+$proc.ProcessName)

if($CollectTrace){
  $trace=Join-Path $outDir 'trace.nettrace'
  dotnet-trace collect -p $ProcessId --duration ([TimeSpan]::FromSeconds($DurationSeconds)) -o $trace
  if($LASTEXITCODE -ne 0){throw 'dotnet-trace failed'}
}
if($CollectGcDump){
  $gc=Join-Path $outDir 'heap.gcdump'
  dotnet-gcdump collect -p $ProcessId -o $gc
  if($LASTEXITCODE -ne 0){throw 'dotnet-gcdump failed'}
}
if($CollectDump){
  $dump=Join-Path $outDir 'process.dmp'
  dotnet-dump collect -p $ProcessId -o $dump
  if($LASTEXITCODE -ne 0){throw 'dotnet-dump failed'}
}

Write-Output ('RUNTIME_CAPTURE_OK '+$outDir)
