$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$helper=Join-Path $PWD 'glass21-641-close-preview.ps1'
$p=Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$helper+'"')) -PassThru
if(!$p.WaitForExit(10000) -or $p.ExitCode -ne 0){throw 'Close preview failed'}
& .\glass21-641-build-preview.ps1
