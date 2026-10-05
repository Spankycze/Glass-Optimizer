$ErrorActionPreference='Stop'
$src='C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\artifacts\gemini-live-stt-test'
$dst='C:\Program Files\Spankyho Optimizer'
Get-Process -Name 'whisper-stream','SpankyhoOptimizer.RuntimeHost' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force
Start-Process -FilePath (Join-Path $dst 'SpankyhoOptimizer.RuntimeHost.exe') -ArgumentList '--fan-agent'
