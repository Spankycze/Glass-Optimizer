param([string]$Label='before',[string]$Exe='C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.exe')
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$dir=Join-Path $PWD 'artifacts\glass21-639'
New-Item $dir -ItemType Directory -Force|Out-Null
$rows=@()
foreach($i in 1..3){
 foreach($flag in @('--performance-smoke','--startup-hotpath-smoke')){
 $p=Start-Process $Exe -ArgumentList $flag -WorkingDirectory (Split-Path $Exe) -PassThru
 if(!$p.WaitForExit(120000)){throw 'Measure timeout'}
 if($p.ExitCode -ne 0){throw "Measure failed $flag"}
 if($flag -eq '--performance-smoke'){
 $lines=@(Get-Content 'C:\ProgramData\SpankyhoOptimizer\performance.log' -Tail 500|Where-Object {$_ -match 'PERF_COLD |PERF_WARM |PERF_MEMORY '}|Select-Object -Last 3)
 }else{$lines=@(Get-Content 'C:\ProgramData\SpankyhoOptimizer\optimizer.log' -Tail 500|Where-Object {$_ -match 'STARTUP_HOTPATH_SMOKE_OK'}|Select-Object -Last 1)}
 $rows+=@{repeat=$i;flag=$flag;lines=@($lines|ForEach-Object {$_.ToString()})}
 Write-Output ($lines -join [Environment]::NewLine)
 }
}
@{label=$Label;version=(Get-Item $Exe).VersionInfo.ProductVersion;exe=$Exe;samples=$rows}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $dir ($Label+'-performance.json')) -Encoding UTF8
Write-Output 'MEASURE637_OK'
