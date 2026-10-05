param([int]$TimeoutSec=120)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
if(-not(Test-Path $exe)){throw 'Debug EXE missing. Run build-fast first.'}
$sw=[Diagnostics.Stopwatch]::StartNew()
$proc=Start-Process -FilePath $exe -ArgumentList '--visual-size-smoke' -WorkingDirectory (Split-Path $exe) -PassThru
if(-not $proc.WaitForExit($TimeoutSec*1000)){
  try{Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue}catch{}
  throw ('Visual size smoke timeout after '+$TimeoutSec+'s')
}
$sw.Stop()
if($proc.ExitCode -ne 0){throw ('Visual size smoke failed exit='+$proc.ExitCode)}
Write-Output ('VISUAL_SIZE_AUDIT_OK ms='+[math]::Round($sw.Elapsed.TotalMilliseconds,1))
