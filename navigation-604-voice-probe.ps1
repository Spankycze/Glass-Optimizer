$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& '.\dc-smart.ps1' '.\build-fast.ps1'
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
$exe=& '.\resolve-debug-exe.ps1'
$p=Start-Process $exe -ArgumentList '--release-ui-smoke' -WorkingDirectory (Split-Path $exe) -PassThru
if(-not $p.WaitForExit(120000)){try{$p.Kill()}catch{};throw 'UI probe timeout'}
Write-Output ("NAV604_UI_PROBE_EXIT="+$p.ExitCode)
Get-Content 'C:\ProgramData\SpankyhoOptimizer\performance.log' -Tail 500 | Where-Object {$_ -match 'AI_RUNTIME_VOICE_SYNC_DATA|AI_RUNTIME_VOICE_DEFERRED'}
exit $p.ExitCode
