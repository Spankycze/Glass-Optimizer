$core='C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.Core.dll'
$host='C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.RuntimeHost.exe'
$health=Join-Path $env:LOCALAPPDATA 'SpankyhoOptimizer\FanAgent\fan-agent-health.json'
$bridge='C:\Program Files\SpankyhoOptimizer\HardwareBridge\hardware-telemetry-bridge.json'
[ordered]@{
 CoreVersion=if(Test-Path $core){(Get-Item $core).VersionInfo.ProductVersion}else{'missing'}
 HostVersion=if(Test-Path $host){(Get-Item $host).VersionInfo.ProductVersion}else{'missing'}
 Health=if(Test-Path $health){Get-Content $health -Raw}else{'missing'}
 Bridge=if(Test-Path $bridge){Get-Content $bridge -Raw}else{'missing'}
 RuntimeHosts=@(Get-Process SpankyhoOptimizer.RuntimeHost -ErrorAction SilentlyContinue | Select-Object Id,SessionId,StartTime).Count
} | ConvertTo-Json -Depth 6