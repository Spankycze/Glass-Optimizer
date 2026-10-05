$pids=9028,100876,101324
Get-CimInstance Win32_Process |
  Where-Object { $pids -contains $_.ProcessId } |
  Select-Object ProcessId,ParentProcessId,SessionId,CreationDate,Name,CommandLine |
  ConvertTo-Json -Depth 3