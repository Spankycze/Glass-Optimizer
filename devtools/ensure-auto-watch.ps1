[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if($env:SPANKY_DISABLE_AUTO_WATCH -eq '1'){return}
$watch=Join-Path $PSScriptRoot 'watch-fast.ps1'
if(!(Test-Path -LiteralPath $watch -PathType Leaf)){return}
if(!(Get-Command watchexec.exe -ErrorAction SilentlyContinue)){return}
$state=if((Split-Path $root -Leaf) -eq 'RAM Guard'){Join-Path $root 'artifacts/.cache'}else{Join-Path $root '.ai-state'}
[IO.Directory]::CreateDirectory($state)|Out-Null
$lease=Join-Path $state 'dev-auto-watch.lock'
$probe=$null
try{
 $probe=[IO.File]::Open($lease,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
}catch [System.IO.IOException]{
 Write-Host 'DEV_AUTO_WATCH_ALREADY_RUNNING'
 return
}finally{if($null -ne $probe){$probe.Dispose()}}
# This runs only after a successful normal dev build. It never changes
# Windows startup, Task Scheduler, browser profiles or installed applications.
$pwsh=(Get-Command pwsh.exe -ErrorAction Stop).Source
$args=@('-NoProfile','-NonInteractive','-File',('"'+$watch+'"'),'-Run')
$child=Start-Process -FilePath $pwsh -ArgumentList $args -WorkingDirectory $root -WindowStyle Hidden -PassThru
Start-Sleep -Milliseconds 600
$child.Refresh()
if($child.HasExited){throw ('DEV_AUTO_WATCH_FAILED exit='+$child.ExitCode)}
Write-Host ('DEV_AUTO_WATCH_STARTED pid='+$child.Id)
