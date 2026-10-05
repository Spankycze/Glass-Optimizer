$ErrorActionPreference='SilentlyContinue'
schtasks.exe /Change /TN "SpankyhoOptimizer-FanAgent-Watchdog" /Disable | Out-Null
Get-Process -Name "whisper-stream" -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name "SpankyhoOptimizer.RuntimeHost" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
Start-Process -FilePath "C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\artifacts\runtimehost-smart-500\SpankyhoOptimizer.RuntimeHost.exe" -ArgumentList "--fan-agent"
