param(
 [Parameter(Mandatory=$true)][string]$Before,
 [Parameter(Mandatory=$true)][string]$After,
 [double]$CpuRegressionPercent=10,
 [double]$MemoryRegressionPercent=10
)
$ErrorActionPreference='Stop'
$b=Get-Content -LiteralPath $Before -Raw|ConvertFrom-Json
$a=Get-Content -LiteralPath $After -Raw|ConvertFrom-Json
if($b.argument -ne $a.argument -or $b.intervalMs -ne $a.intervalMs){throw 'Measurement workloads or sampling intervals differ'}
$fields=@(
 @{name='cpuTimeMs';limit=$CpuRegressionPercent},
 @{name='peakWorkingSetMB';limit=$MemoryRegressionPercent},
 @{name='peakPrivateMB';limit=$MemoryRegressionPercent},
 @{name='peakThreads';limit=$MemoryRegressionPercent},
 @{name='peakHandles';limit=$MemoryRegressionPercent}
)
$regressions=0
foreach($item in $fields){
 $name=$item.name
 $base=[double]$b.$name
 $next=[double]$a.$name
 if($base -le 0){throw "Invalid baseline $name"}
 $pct=[math]::Round(($next-$base)/$base*100,2)
 $status=if($pct -gt $item.limit){$regressions++;'REGRESSION'}elseif($pct -lt -3){'IMPROVED'}else{'STABLE'}
 Write-Output ('RESOURCE_DELTA {0} before={1} after={2} change={3}% status={4}' -f $name,$base,$next,$pct,$status)
}
if($regressions){Write-Output "RESOURCE_GATE_FAIL regressions=$regressions";exit 25}
Write-Output 'RESOURCE_GATE_PASS'
