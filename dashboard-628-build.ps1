$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\dashboard-628'
$note=[IO.File]::ReadAllText((Join-Path $dir 'release-note.txt'))
$progress=Join-Path $PWD 'WORK_PROGRESS.md'
$existing=[IO.File]::ReadAllText($progress)
if(!$existing.StartsWith($note)){[IO.File]::WriteAllText($progress,$note+$existing)}
& .\build-fast.ps1 *> (Join-Path $dir 'build-fast.log')
if(!$?){throw 'Build failed'}
$exe=(& .\resolve-debug-exe.ps1|Select-Object -Last 1)
& .\build-test.ps1 -NoBuild -ForceTests -ExePath $exe *> (Join-Path $dir 'build-test.log')
if(!$?){throw 'Tests failed'}
$validate=[IO.File]::ReadAllText((Join-Path $PWD 'obsidian-626-validate.ps1')).Replace('obsidian-626','dashboard-628').Replace('OBSIDIAN626','DASHBOARD628')
[IO.File]::WriteAllText((Join-Path $PWD 'dashboard-628-validate.ps1'),$validate)
& .\dashboard-628-validate.ps1
if(!$?){throw 'Validation failed'}
Write-Output 'DASHBOARD628_BUILD_OK'
