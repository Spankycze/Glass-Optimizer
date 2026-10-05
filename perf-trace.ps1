param(
  [ValidateSet('startup','sections','network','soak')][string]$Scenario='startup',
  [switch]$Etw,
  [switch]$Dotnet
)
$ErrorActionPreference='Stop'
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $admin){
  Write-Output 'PERF_TOOL_SKIP_NEEDS_ELEVATION'
  exit 0
}
$root=$PSScriptRoot
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$outDir=Join-Path $root ('.perf\traces\'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'_'+$Scenario)
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$arg=switch($Scenario){
 'startup' {'--startup-hotpath-smoke'}
 'sections' {'--performance-smoke'}
 'network' {'--nic-ui-smoke'}
 'soak' {'--soak-smoke'}
}
$wprStarted=$false
if($Etw -and (Get-Command wpr -ErrorAction SilentlyContinue)){
  try{& wpr -cancel *> $null}catch{}
  try{& wpr -start GeneralProfile -filemode *> $null;if($LASTEXITCODE -eq 0){$wprStarted=$true}}catch{}
}
$exit=0
try{
  if($Dotnet -and (Get-Command dotnet-trace -ErrorAction SilentlyContinue)){
    $trace=Join-Path $outDir 'runtime.nettrace'
    & dotnet-trace collect --profile dotnet-sampled-thread-time,gc-verbose --buffersize 128 --output $trace --format NetTrace -- $exe $arg *> (Join-Path $outDir 'dotnet-trace.log')
    $exit=$LASTEXITCODE
  }else{
    $p=Start-Process -FilePath $exe -ArgumentList $arg -Wait -PassThru
    $exit=$p.ExitCode
  }
}finally{
  if($wprStarted){
    try{& wpr -stop (Join-Path $outDir 'system.etl') *> (Join-Path $outDir 'wpr.log')}catch{try{& wpr -cancel *> $null}catch{}}
  }
}
Write-Output ("PERF_TRACE_OK scenario={0} exit={1} dotnet={2} etw={3} dir={4}" -f $Scenario,$exit,$Dotnet,$wprStarted,$outDir)
if($exit -ne 0){exit $exit}
