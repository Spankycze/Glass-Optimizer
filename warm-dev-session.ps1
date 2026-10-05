param([switch]$Quiet)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'dev-perf-env.ps1')
$sw=[Diagnostics.Stopwatch]::StartNew()
if(Get-Command sccache -ErrorAction SilentlyContinue){
  sccache --show-stats *> $null
}
if(Test-Path (Join-Path $PSScriptRoot 'session-warm.ps1')){
  & (Join-Path $PSScriptRoot 'session-warm.ps1') *> $null
}
if(Test-Path (Join-Path $PSScriptRoot 'smart-refresh.ps1')){
  & (Join-Path $PSScriptRoot 'smart-refresh.ps1') *> $null
}
# Prime filesystem metadata with ripgrep file enumeration; discard output.
if(Get-Command rg -ErrorAction SilentlyContinue){
  rg --files $PSScriptRoot -g '!.git/**' -g '!bin/**' -g '!obj/**' -g '!node_modules/**' -g '!artifacts/**' *> $null
}
$sw.Stop()
if(-not $Quiet){Write-Host ('DEV_WARM_OK ms='+[int]$sw.Elapsed.TotalMilliseconds)}
