Get-Process SpankyhoOptimizer -ErrorAction SilentlyContinue|Select-Object Id,Path,MainWindowHandle,MainWindowTitle|ConvertTo-Json -Depth 3
