param(
 [Parameter(Mandatory)][string]$BeforeGlob,
 [Parameter(Mandatory)][string]$AfterGlob,
 [double]$CpuRegressionPercent=10,
 [double]$MemoryRegressionPercent=10,
 [int]$MinSamples=3,
 [switch]$RequireMemoryImprovement,
 [ValidateRange(0.1,100)][double]$MinMemoryImprovementPercent=3,
 [ValidateRange(0.1,1024)][double]$MinMemoryImprovementMiB=2
)
$ErrorActionPreference='Stop'
function Read-Series([string]$glob) {
 $files=@(Get-ChildItem -Path $glob -File | Sort-Object Name)
 if($files.Count -lt $MinSamples){throw "Insufficient runs for $glob : $($files.Count), expected $MinSamples"}
 $runs=@($files | ForEach-Object {Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json})
 foreach($run in $runs) {if($run.exitCode -ne 0){throw "Nonzero test exit for $($run.exe)"}}
 $arguments=@($runs | Select-Object -ExpandProperty argument -Unique)
 $intervals=@($runs | Select-Object -ExpandProperty intervalMs -Unique)
 if($arguments.Count -ne 1 -or $intervals.Count -ne 1){throw "Mixed workloads or poll intervals in $glob"}
 return ,$runs
}
function Median([object[]]$values) {
 $sorted=@($values | Sort-Object)
 $n=$sorted.Count
 if($n%2){return [double]$sorted[[int][math]::Floor($n/2)]}
 return ([double]$sorted[$n/2-1]+[double]$sorted[$n/2])/2
}
$b=Read-Series $BeforeGlob
$a=Read-Series $AfterGlob
if($b[0].argument -ne $a[0].argument -or $b[0].intervalMs -ne $a[0].intervalMs){throw 'Workloads or sampling intervals do not match'}
$fields=@(
 @{name='wallMs';limit=$CpuRegressionPercent},
 @{name='cpuTimeMs';limit=$CpuRegressionPercent},
 @{name='peakWorkingSetMB';limit=$MemoryRegressionPercent},
 @{name='peakPrivateMB';limit=$MemoryRegressionPercent},
 @{name='peakThreads';limit=$MemoryRegressionPercent},
 @{name='peakHandles';limit=$MemoryRegressionPercent}
)
$bad=0
$memoryEvidence=@{}
foreach($field in $fields){
 $name=$field.name
 $before=Median @($b | ForEach-Object {[double]($_.$name)})
 $after=Median @($a | ForEach-Object {[double]($_.$name)})
 if($before -le 0){throw "Invalid baseline $name"}
 $change=100*($after-$before)/$before
 if($name -in @('peakWorkingSetMB','peakPrivateMB')){
   $memoryEvidence[$name]=[pscustomobject]@{Before=$before;After=$after;SavedMiB=($before-$after);SavedPercent=(-$change)}
 }
 $status=if($change -gt $field.limit){$bad++;'REGRESSION'}elseif($change -lt -3){'IMPROVED'}else{'STABLE'}
 Write-Output ('SERIES_DELTA {0} beforeMedian={1:N2} afterMedian={2:N2} change={3:N2}% status={4} beforeRuns={5} afterRuns={6}' -f $name,$before,$after,$change,$status,$b.Count,$a.Count)
}
if($bad){Write-Output "SERIES_GATE_FAIL regressions=$bad";exit 25}
if($RequireMemoryImprovement){
 $missing=@()
 foreach($name in @('peakWorkingSetMB','peakPrivateMB')){
   $e=$memoryEvidence[$name]
   $passed=$e.SavedMiB -ge $MinMemoryImprovementMiB -and $e.SavedPercent -ge $MinMemoryImprovementPercent
   Write-Output ('MEMORY_GAIN_EVIDENCE {0} savedMiB={1:N2} savedPercent={2:N2} requiredMiB={3:N2} requiredPercent={4:N2} passed={5}' -f $name,$e.SavedMiB,$e.SavedPercent,$MinMemoryImprovementMiB,$MinMemoryImprovementPercent,$passed)
   if(-not $passed){$missing+=$name}
 }
 if($missing.Count){Write-Output ('MEMORY_GAIN_GATE_FAIL missing='+($missing -join ','));exit 26}
 Write-Output 'MEMORY_GAIN_GATE_PASS'
}
Write-Output 'SERIES_GATE_PASS'
exit 0
