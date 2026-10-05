$ErrorActionPreference='Stop'
$wrapper=Join-Path $PSScriptRoot 'setup-dev-drive-elevated.ps1'
$args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $wrapper + '"'))
Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $args
