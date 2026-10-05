$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\obsidian-626'
& .\build-fast.ps1 *> (Join-Path $dir 'build-fast.log')
if(!$?){throw 'Build failed'}
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
& .\build-test.ps1 -NoBuild -ForceTests -ExePath $exe *> (Join-Path $dir 'build-test.log')
if(!$?){throw 'Tests failed'}
Write-Output 'OBSIDIAN626_BUILD_TEST_OK'
