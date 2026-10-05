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

$stateDir=Join-Path $env:LOCALAPPDATA 'SpankyDevDrive'
New-Item -ItemType Directory -Force $stateDir | Out-Null
$pending=Join-Path $stateDir 'enable-pending.txt'

$queryOut=& fsutil.exe devdrv query 2>&1
$queryText=($queryOut | Out-String)
if($queryText -match 'Developer volumes are enabled'){
  Remove-Item $pending -Force -ErrorAction SilentlyContinue
  Write-Host 'DEV_DRIVE_SUPPORT_ALREADY_ENABLED'
}else{
  $enableOut=& fsutil.exe devdrv enable /allowAv 2>&1
  $enableCode=$LASTEXITCODE
  $enableOut | Write-Host
  if($enableCode -ne 0){throw 'Failed to enable Dev Drive support.'}
  if(($enableOut | Out-String) -match 'reboot is required|restart is required|restart required'){
    Set-Content -LiteralPath $pending -Value (Get-Date -Format o) -Encoding ascii
    $resume=Join-Path $PSScriptRoot 'setup-dev-drive.ps1'
    $action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+$resume+'"')
    $trigger=New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $principalTask=New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
    Register-ScheduledTask -TaskName 'Spanky Dev Drive Setup Resume' -Action $action -Trigger $trigger -Principal $principalTask -Force | Out-Null
    Write-Host 'DEV_DRIVE_REBOOT_REQUIRED'
    Write-Host 'Setup will continue automatically after the next Windows restart.'
    exit 3010
  }
}

Start-Service vds -ErrorAction SilentlyContinue

if(!(Test-Path $VhdPath)){
  $dp=Join-Path $env:TEMP 'spanky-devdrive-create.txt'
  $mb=$SizeGB*1024
  $dpCommands=@(
    'create vdisk file='+$VhdPath+' maximum='+$mb+' type=expandable'
    'select vdisk file='+$VhdPath
    'attach vdisk'
    'create partition primary'
    'assign letter='+$letter
    'exit'
  )
  [IO.File]::WriteAllText($dp,($dpCommands -join [Environment]::NewLine),[Text.Encoding]::ASCII)
  & diskpart.exe /s $dp
  $code=$LASTEXITCODE
  if($code -eq 0){Remove-Item $dp -Force -ErrorAction SilentlyContinue}
  if($code -ne 0){throw ('DiskPart create failed: '+$code+' script='+$dp)}
}else{
  $existingPartition=Get-Partition -DriveLetter $letter -ErrorAction SilentlyContinue
  if(!$existingPartition){
    $dp=Join-Path $env:TEMP 'spanky-devdrive-attach.txt'
    $dpCommands=@('select vdisk file='+$VhdPath,'attach vdisk','exit')
    [IO.File]::WriteAllText($dp,($dpCommands -join [Environment]::NewLine),[Text.Encoding]::ASCII)
    & diskpart.exe /s $dp | Out-Null
    if($LASTEXITCODE -ne 0){throw ('DiskPart attach failed: '+$LASTEXITCODE)}
    Remove-Item $dp -Force -ErrorAction SilentlyContinue
  }
}

$partition=Get-Partition -DriveLetter $letter -ErrorAction SilentlyContinue
if(!$partition){throw 'VHD mounted but drive letter partition was not available.'}
$vol=Get-Volume -DriveLetter $letter -ErrorAction SilentlyContinue
if($vol -and $vol.FileSystem -and $vol.FileSystem -ne 'RAW'){
  Write-Host ('Existing formatted volume detected: '+$vol.FileSystem)
}else{
  Format-Volume -DriveLetter $letter -DevDrive -NewFileSystemLabel 'SpankyDevDrive' -Force -Confirm:$false | Out-Null
  $vol=Get-Volume -DriveLetter $letter -ErrorAction Stop
  Write-Host ('DEV_DRIVE_FORMATTED fs='+$vol.FileSystem)
}

& fsutil.exe devdrv trust ($letter+':')
if($LASTEXITCODE -ne 0){throw 'Dev Drive trust failed.'}

$cacheRoot=$letter+':\SpankyDevCache'
foreach($sub in @('sccache','npm','uv')){
  New-Item -ItemType Directory -Force (Join-Path $cacheRoot $sub) | Out-Null
}
[Environment]::SetEnvironmentVariable('SPANKY_DEV_DRIVE',($letter+':'),'User')

$state=[ordered]@{drive_letter=$letter;vhd_path=$VhdPath;size_gb=$SizeGB;cache_root=$cacheRoot}
$state | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $stateDir 'state.json')

$mountScript=Join-Path $PSScriptRoot 'mount-dev-drive.ps1'
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -File "'+$mountScript+'"')
$trigger=New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principalTask=New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName 'Spanky Dev Drive Mount' -Action $action -Trigger $trigger -Principal $principalTask -Force | Out-Null
Unregister-ScheduledTask -TaskName 'Spanky Dev Drive Setup Resume' -Confirm:$false -ErrorAction SilentlyContinue

& fsutil.exe devdrv query ($letter+':')
Write-Host ('DEV_DRIVE_READY drive='+$letter+': cache='+$cacheRoot)