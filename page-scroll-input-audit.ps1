param([int]$TimeoutSec=120)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
if(-not(Test-Path $exe)){throw 'Debug EXE missing. Run build-fast first.'}
$sw=[Diagnostics.Stopwatch]::StartNew()
$proc=Start-Process -FilePath $exe -ArgumentList '--page-scroll-input-smoke' -WorkingDirectory (Split-Path $exe) -PassThru
if(-not $proc.WaitForExit($TimeoutSec*1000)){
  try{Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue}catch{}
  throw ('Page scroll input smoke timeout after '+$TimeoutSec+'s')
}
$sw.Stop()
if($proc.ExitCode -ne 0){throw ('Page scroll input smoke failed exit='+$proc.ExitCode)}
Write-Output ('PAGE_SCROLL_INPUT_AUDIT_OK ms='+[math]::Round($sw.Elapsed.TotalMilliseconds,1))
