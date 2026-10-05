param(
  [int]$SizeGB=80,
  [string]$PreferredLetter='V',
  [string]$VhdPath='D:\DevDrive\SpankyDevDrive.vhdx'
)
$ErrorActionPreference='Stop'
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
  throw 'Administrator elevation is required. Run setup-dev-drive-admin.cmd and approve UAC.'
}
if($SizeGB -lt 50){throw 'Dev Drive minimum is 50 GB.'}
$parent=Split-Path $VhdPath -Parent
New-Item -ItemType Directory -Force $parent | Out-Null

$letters=@($PreferredLetter,'W','X','Y','Z') | Select-Object -Unique
$letter=$null
foreach($l in $letters){
  if(-not(Test-Path ($l+':\'))){$letter=$l;break}
}
if(!$letter){throw 'No free V/W/X/Y/Z drive letter.'}

& fsutil.exe devdrv enable /allowAv
if($LASTEXITCODE -ne 0){throw 'Failed to enable Dev Drive support.'}

if(!(Test-Path $VhdPath)){
  $dp=Join-Path $env:TEMP 'spanky-devdrive-create.txt'
  $mb=$SizeGB*1024
  @(
    'create vdisk file="'+$VhdPath+'" maximum='+$mb+' type=expandable',
    'select vdisk file="'+$VhdPath+'"',
    'attach vdisk',
    'create partition primary',
    'assign letter='+$letter,
    'exit'
  ) | Set-Content -Encoding ascii $dp
  & diskpart.exe /s $dp
  $code=$LASTEXITCODE
  Remove-Item $dp -Force -ErrorAction SilentlyContinue
  if($code -ne 0){throw ('DiskPart create failed: '+$code)}
}else{
  $dp=Join-Path $env:TEMP 'spanky-devdrive-attach.txt'
  @('select vdisk file="'+$VhdPath+'"','attach vdisk','exit') | Set-Content -Encoding ascii $dp
  & diskpart.exe /s $dp | Out-Null
  Remove-Item $dp -Force -ErrorAction SilentlyContinue
}

if(!(Test-Path ($letter+':\'))){throw 'VHD mounted but drive letter was not available.'}
$vol=Get-Volume -DriveLetter $letter -ErrorAction SilentlyContinue
if($vol -and $vol.FileSystem){
  Write-Host ('Existing formatted volume detected: '+$vol.FileSystem)
}else{
  & format.com ($letter+':') /DevDrv /Q /Y /V:SpankyDevDrive
  if($LASTEXITCODE -ne 0){throw ('Dev Drive format failed: '+$LASTEXITCODE)}
}

& fsutil.exe devdrv trust ($letter+':')
if($LASTEXITCODE -ne 0){throw 'Dev Drive trust failed.'}

$cacheRoot=$letter+':\SpankyDevCache'
foreach($sub in @('sccache','npm','uv')){
  New-Item -ItemType Directory -Force (Join-Path $cacheRoot $sub) | Out-Null
}
[Environment]::SetEnvironmentVariable('SPANKY_DEV_DRIVE',($letter+':'),'User')

$stateDir=Join-Path $env:LOCALAPPDATA 'SpankyDevDrive'
New-Item -ItemType Directory -Force $stateDir | Out-Null
$state=[ordered]@{drive_letter=$letter;vhd_path=$VhdPath;size_gb=$SizeGB;cache_root=$cacheRoot}
$state | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $stateDir 'state.json')

$mountScript=Join-Path $PSScriptRoot 'mount-dev-drive.ps1'
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -File "'+$mountScript+'"')
$trigger=New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principalTask=New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName 'Spanky Dev Drive Mount' -Action $action -Trigger $trigger -Principal $principalTask -Force | Out-Null

& fsutil.exe devdrv query ($letter+':')
Write-Host ('DEV_DRIVE_READY drive='+$letter+': cache='+$cacheRoot)