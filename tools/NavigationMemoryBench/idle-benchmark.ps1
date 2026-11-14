param([string]$Exe='C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.exe',[string]$Output='V:\ChatGPT Projects\Spankyho Optimizer\artifacts\memory-profile-896\idle-baseline.json')
$ErrorActionPreference='Stop'
if(@(Get-Process SpankyhoOptimizer -ErrorAction SilentlyContinue).Count){throw 'Optimizer already running'}
$pr=Start-Process $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
try {
 for($i=0;$i -lt 80;$i++){Start-Sleep -Milliseconds 100;$pr.Refresh();if($pr.MainWindowHandle){break}}
 if(!$pr.MainWindowHandle){throw 'Window not ready'}
 $samples=@();$timer=[Diagnostics.Stopwatch]::StartNew();$lastCpu=$pr.TotalProcessorTime.TotalMilliseconds
 foreach($delay in @(5,25,25,25,25,25)){Start-Sleep -Seconds $delay;$pr.Refresh();$nowCpu=$pr.TotalProcessorTime.TotalMilliseconds;$samples+=@{atSec=[math]::Round($timer.Elapsed.TotalSeconds,2);wsMiB=[math]::Round($pr.WorkingSet64/1MB,2);privateMiB=[math]::Round($pr.PrivateMemorySize64/1MB,2);cpuDeltaMs=[math]::Round($nowCpu-$lastCpu,1);handles=$pr.HandleCount};$lastCpu=$nowCpu;Write-Host ('IDLE_SAMPLE '+($samples[-1]|ConvertTo-Json -Compress))}
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Output))|Out-Null
 [IO.File]::WriteAllText($Output,(@{executable=$Exe;samples=$samples}|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
 'IDLE_RESULT='+$Output
}finally{if(!$pr.HasExited){$pr.CloseMainWindow()|Out-Null;if(!$pr.WaitForExit(8500)){throw 'Application refused graceful close'}}}
