$ErrorActionPreference='Stop'
$xml='C:\Users\spnka\AppData\Local\SpankyhoOptimizer\fan-agent-task.xml'
$p=Start-Process 'schtasks.exe' -ArgumentList @('/Create','/F','/TN','SpankyhoOptimizer-FanAgent','/XML',$xml) -Verb RunAs -PassThru
if(-not $p.WaitForExit(15000)){throw 'Task registration still running'}
if($p.ExitCode -ne 0){throw "Task registration exit=$($p.ExitCode)"}
Write-Output 'TASK_REGISTER_OK'
