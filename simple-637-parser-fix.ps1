$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$f=Join-Path $PWD 'simple-637-final.ps1'
$t=[IO.File]::ReadAllText($f).Replace("'([A-Za-z][A-Za-z0-9]*)=(-?\d+(?:[.,]\d+)?)'","'(?:^|\s)([A-Za-z][A-Za-z0-9]*)=(-?\d+(?:[.,]\d+)?)'")
[IO.File]::WriteAllText($f,$t)
& .\simple-637-final.ps1
