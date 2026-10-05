$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$helper=Join-Path $PWD 'glass21-639-live.ps1'
$p=Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$helper+'"')) -PassThru
if(!$p.WaitForExit(20000)){throw 'Live capture running; inspect'}
if($p.ExitCode -ne 0){throw 'Live capture failed'}
Get-Content 'artifacts\glass21-639\actual-result.txt'
