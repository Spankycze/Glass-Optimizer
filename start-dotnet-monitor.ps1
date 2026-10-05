param([int]$Port=52325)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$config=Join-Path $root 'tools\dotnet-monitor\monitor-config.json'
if(-not(Test-Path $config)){throw 'dotnet-monitor config missing'}
$url='http://127.0.0.1:'+$Port
Write-Output ('DOTNET_MONITOR_START '+$url+' config='+$config)
dotnet-monitor collect --urls $url --configuration-file-path $config --no-auth
