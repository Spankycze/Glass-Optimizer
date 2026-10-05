$ErrorActionPreference='Stop'
$statePath=Join-Path $env:LOCALAPPDATA 'SpankyDevDrive\state.json'
if(!(Test-Path $statePath)){exit 0}
$s=Get-Content -Raw $statePath | ConvertFrom-Json
$letter=[string]$s.drive_letter
$vhd=[string]$s.vhd_path
if(Test-Path ($letter+':\')){exit 0}
if(!(Test-Path $vhd)){exit 2}
$dp=Join-Path $env:TEMP 'spanky-devdrive-mount.txt'
@(
  'select vdisk file="'+$vhd+'"',
  'attach vdisk',
  'exit'
) | Set-Content -Encoding ascii $dp
& diskpart.exe /s $dp | Out-Null
Remove-Item $dp -Force -ErrorAction SilentlyContinue
if(!(Test-Path ($letter+':\'))){exit 3}
exit 0
