$ErrorActionPreference='SilentlyContinue'
schtasks.exe /Change /TN "SpankyhoOptimizer-FanAgent-Watchdog" /Disable | Out-Null
Get-Process -Name "SpankyhoOptimizer.RuntimeHost" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
Start-Process -FilePath "C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer\artifacts\runtimehost-whisper-495\SpankyhoOptimizer.RuntimeHost.exe" -ArgumentList "--fan-agent"
