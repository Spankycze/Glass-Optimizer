$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$helper=Join-Path $PWD 'glass21-640-installed-capture.ps1'
$p=Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$helper+'"')) -PassThru
if(!$p.WaitForExit(25000)){throw 'Capture running; inspect'}
if($p.ExitCode -ne 0){throw 'Installed640 UI capture failed'}
Get-Content 'artifacts\glass21-640\installed-ui-result.json'
