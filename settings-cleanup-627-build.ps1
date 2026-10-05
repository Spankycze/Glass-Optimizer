$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\settings-cleanup-627'
& .\build-fast.ps1 *> (Join-Path $dir 'build-fast.log')
if(!$?){throw 'Build failed'}
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
& .\build-test.ps1 -NoBuild -ForceTests -ExePath $exe *> (Join-Path $dir 'build-test.log')
if(!$?){throw 'Tests failed'}
$proc=Start-Process $exe -ArgumentList '--visual-capture-smoke' -WorkingDirectory (Split-Path $exe) -PassThru
if(!$proc.WaitForExit(120000)){throw 'Visual capture timeout'}
if($proc.ExitCode -ne 0){throw 'Visual capture failed'}
Copy-Item 'C:\ProgramData\SpankyhoOptimizer\visual-current' (Join-Path $dir 'screens') -Recurse -Force
Write-Output 'SETTINGS627_BUILD_CAPTURE_OK'
