$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& .\build-fast.ps1 *> 'artifacts\changelog-630\red-build.log'
if(!$?){throw 'Red build failed'}
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
$proc=Start-Process $exe -ArgumentList '--visual-capture-smoke' -WorkingDirectory (Split-Path $exe) -PassThru
if(!$proc.WaitForExit(120000)){throw 'Red capture timeout'}
if($proc.ExitCode -eq 0){throw 'Expected old code regression failure'}
$lines=Get-Content 'C:\ProgramData\SpankyhoOptimizer\optimizer.log' -Tail 65
$lines|Set-Content 'artifacts\changelog-630\red-regression.log' -Encoding UTF8
if(($lines -join [Environment]::NewLine) -notmatch 'CHANGELOG_REFRESH_REGRESSION'){throw 'Unexpected failure; inspect logs'}
Write-Output 'CHANGELOG630_RED_CONFIRMED'
