$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'build-state.ps1')

$normalExe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$stagedExe=Join-Path $root '.buildcache\staged-debug\SpankyhoOptimizer.exe'
$stagedFingerprint=Join-Path $root '.buildcache\staged-debug.fp'

if((Test-Path $stagedExe) -and (Test-Path $stagedFingerprint)){
  try {
    $saved=(Get-Content $stagedFingerprint -Raw).Trim()
    if($saved -and $saved -eq (Get-SpankyFingerprint)){
      Write-Output $stagedExe
      return
    }
  } catch {}
}

Write-Output $normalExe
