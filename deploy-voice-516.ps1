$ErrorActionPreference='Stop'
$src='C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\artifacts\.cache\app-publish'
$dst='C:\Program Files\Spankyho Optimizer'
Get-Process -Name 'SpankyhoOptimizer','SpankyhoOptimizer.RuntimeHost','whisper-stream' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
$files=@(
 'SpankyhoOptimizer.exe',
 'SpankyhoOptimizer.dll',
 'SpankyhoOptimizer.deps.json',
 'SpankyhoOptimizer.runtimeconfig.json',
 'SpankyhoOptimizer.Core.dll',
 'SpankyhoOptimizer.RuntimeHost.exe',
 'SpankyhoOptimizer.RuntimeHost.dll',
 'SpankyhoOptimizer.RuntimeHost.deps.json',
 'SpankyhoOptimizer.RuntimeHost.runtimeconfig.json'
)
foreach($name in $files){
  Copy-Item -LiteralPath (Join-Path $src $name) -Destination (Join-Path $dst $name) -Force
}
$whisperSrc=Join-Path $src 'Tools\Whisper'
$whisperDst=Join-Path $dst 'Tools\Whisper'
New-Item -ItemType Directory -Force -Path $whisperDst | Out-Null
Copy-Item -Path (Join-Path $whisperSrc '*') -Destination $whisperDst -Recurse -Force
Start-Process -FilePath (Join-Path $dst 'SpankyhoOptimizer.RuntimeHost.exe') -ArgumentList '--fan-agent'
