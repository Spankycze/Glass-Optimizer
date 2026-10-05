$ErrorActionPreference='Stop'
$statePath=Join-Path $env:LOCALAPPDATA 'SpankyDevDrive\state.json'
if(!(Test-Path $statePath)){exit 0}
$s=Get-Content -Raw $statePath | ConvertFrom-Json
$letter=[string]$s.drive_letter
$vhd=[string]$s.vhd_path
if(Test-Path ($letter+':\')){exit 0}

# Wait for the backing NVMe volume and logon storage stack.
for($i=0;$i -lt 15;$i++){
  if(Test-Path $vhd){break}
  Start-Sleep -Seconds 2
}
if(!(Test-Path $vhd)){exit 2}

$dp=Join-Path $env:TEMP 'spanky-devdrive-mount.txt'
$cmds=@(
  ('select vdisk file='+$vhd)
  'attach vdisk'
  'exit'
)
[IO.File]::WriteAllText($dp,($cmds -join [Environment]::NewLine),[Text.Encoding]::ASCII)

$code=1
for($i=0;$i -lt 5;$i++){
  & diskpart.exe /s $dp | Out-Null
  $code=$LASTEXITCODE
  if($code -eq 0 -and (Test-Path ($letter+':\'))){break}
  Start-Sleep -Seconds 2
}
Remove-Item $dp -Force -ErrorAction SilentlyContinue
if($code -ne 0){exit $code}
if(!(Test-Path ($letter+':\'))){exit 3}
exit 0
