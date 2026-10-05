Get-CimInstance Win32_Process -Filter "Name='SpankyhoOptimizer.exe'" -ErrorAction SilentlyContinue |
  Select-Object ProcessId,ExecutablePath,CommandLine |
  Format-List
