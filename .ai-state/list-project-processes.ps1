Get-CimInstance Win32_Process |
  Where-Object {
    $_.CommandLine -match 'Spankyho Optimizer' -or
    $_.CommandLine -match 'ai-performance-614' -or
    $_.CommandLine -match 'continue-project' -or
    $_.CommandLine -match 'agent-loop'
  } |
  Select-Object ProcessId,ParentProcessId,Name,CreationDate,CommandLine |
  ConvertTo-Json -Depth 3