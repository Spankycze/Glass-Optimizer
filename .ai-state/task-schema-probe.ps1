$tasks=Get-ScheduledTask -ErrorAction SilentlyContinue
$restart=$tasks | Where-Object { $_.Settings.RestartCount -gt 0 } | Select-Object -First 1
$logon=$tasks | Where-Object { $_.Triggers.CimClass.CimClassName -contains 'MSFT_TaskLogonTrigger' } | Select-Object -First 1
'---RESTART---'
if($restart){Export-ScheduledTask -TaskName $restart.TaskName -TaskPath $restart.TaskPath}
'---LOGON---'
if($logon){Export-ScheduledTask -TaskName $logon.TaskName -TaskPath $logon.TaskPath}
