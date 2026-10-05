$ErrorActionPreference='SilentlyContinue'
Get-Process -Name "whisper-stream" -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name "SpankyhoOptimizer.RuntimeHost" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
Start-Process -FilePath "C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\artifacts\runtimehost-498\SpankyhoOptimizer.RuntimeHost.exe" -ArgumentList "--fan-agent"
