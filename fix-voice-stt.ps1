$ErrorActionPreference='Stop'
$src='C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\tools\whisper'
$dst='C:\Program Files\Spankyho Optimizer\Tools\Whisper'
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item -LiteralPath (Join-Path $src 'ggml-base.bin') -Destination (Join-Path $dst 'ggml-base.bin') -Force
Copy-Item -Path (Join-Path $src 'bin\Release\*') -Destination $dst -Recurse -Force
Get-Process -Name 'SpankyhoOptimizer.RuntimeHost' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
Start-Process -FilePath 'C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.RuntimeHost.exe' -ArgumentList '--fan-agent'
