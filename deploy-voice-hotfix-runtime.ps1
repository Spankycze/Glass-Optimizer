$ErrorActionPreference='Stop'
$src='C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\artifacts\voice-hotfix-runtime'
$dst='C:\Program Files\Spankyho Optimizer'
Get-Process -Name 'whisper-stream','SpankyhoOptimizer.RuntimeHost' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
foreach($name in @(
 'SpankyhoOptimizer.Core.dll',
 'SpankyhoOptimizer.RuntimeHost.exe',
 'SpankyhoOptimizer.RuntimeHost.dll',
 'SpankyhoOptimizer.RuntimeHost.deps.json',
 'SpankyhoOptimizer.RuntimeHost.runtimeconfig.json'
)){
 Copy-Item -LiteralPath (Join-Path $src $name) -Destination (Join-Path $dst $name) -Force
}
Start-Process -FilePath (Join-Path $dst 'SpankyhoOptimizer.RuntimeHost.exe') -ArgumentList '--fan-agent'
