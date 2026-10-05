param([int]$TargetPid=0,[string]$OutputPath='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if($PSVersionTable.PSEdition -ne 'Core'){
  $pwsh=(Get-Command pwsh.exe -ErrorAction Stop).Source
  $forward=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$MyInvocation.MyCommand.Path,'-TargetPid',$TargetPid)
  if(-not [string]::IsNullOrWhiteSpace($OutputPath)){$forward+=@('-OutputPath',$OutputPath)}
  & $pwsh @forward
  exit $LASTEXITCODE
}
$tool=Join-Path $root '.tools\etwprof\etwprof.exe'
if(-not(Test-Path $tool)){throw 'etwprof.exe missing'}
if($TargetPid -le 0){
  $target=Get-Process SpankyhoOptimizer -ErrorAction SilentlyContinue | Select-Object -First 1
  if(-not $target){throw 'Use -TargetPid or run SpankyhoOptimizer first'}
  $TargetPid=$target.Id
}
if(-not(Get-Process -Id $TargetPid -ErrorAction SilentlyContinue)){throw ('Target PID not running: '+$TargetPid)}
if([string]::IsNullOrWhiteSpace($OutputPath)){
  $dir=Join-Path $root 'artifacts\diagnostics'; New-Item -ItemType Directory -Force -Path $dir|Out-Null
  $OutputPath=Join-Path $dir ('etwprof-'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'.etl')
}
$psi=[Diagnostics.ProcessStartInfo]::new()
$psi.FileName=$tool
$psi.UseShellExecute=$false
$psi.ArgumentList.Add('profile')
$psi.ArgumentList.Add('--target='+$TargetPid)
$psi.ArgumentList.Add('--output='+$OutputPath)
$psi.ArgumentList.Add('--nologo')
$psi.ArgumentList.Add('--cswitch')
$p=[Diagnostics.Process]::Start($psi)
$p.WaitForExit()
if($p.ExitCode -ne 0){throw ('etwprof failed exit='+$p.ExitCode)}
if(-not(Test-Path $OutputPath)){throw 'etwprof output missing'}
Write-Output ('ETWPROF_OK pid='+$TargetPid+' output='+$OutputPath)