param([string]$BenchPath='')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
if(-not $BenchPath){$BenchPath=Join-Path $root '.perf\latest\bench.json'}
if(-not(Test-Path $BenchPath)){throw 'bench.json missing'}
$bench=Get-Content $BenchPath -Raw|ConvertFrom-Json
$budget=Get-Content (Join-Path $root 'perf-budget.json') -Raw|ConvertFrom-Json
$impactPath=Join-Path $root '.ai-state\change-impact.json'
if(Test-Path $impactPath){$impact=Get-Content $impactPath -Raw|ConvertFrom-Json}else{$impact=$null}
$map=@{
 startupActivatedMs='App.cs, MainWindow.Shell.cs, MainWindow.Motion.cs'
 networkPageMs='MainWindow.cs, MainWindow.PowerNetwork.cs, Network/DNS/MTU services'
 storageForegroundMs='MainWindow.cs, MainWindow.StoragePageCache.cs, storage services'
 gpuPageMs='MainWindow.cs, MainWindow.GpuDeferred.cs, GPU services'
 servicesPageMs='MainWindow.cs, service/component builders'
}
$rows=@()
foreach($p in $budget.metrics.psobject.Properties){
  $m=$bench.metrics.($p.Name);if($null -eq $m){continue}
  $v=[double]$m.median;$base=[double]$p.Value.baseline
  $delta=if($base){(($v-$base)/$base)*100}else{0}
  $rows+=[pscustomobject]@{metric=$p.Name;current=$v;baseline=$base;deltaPct=[math]::Round($delta,1);suspect=$map[$p.Name]}
}
$rows=$rows|Sort-Object deltaPct -Descending
$rows|Format-Table -AutoSize|Out-String|Write-Output
if($impact){Write-Output ('PERF_IMPACT areas='+($impact.areas -join ',')+' files='+$impact.fileCount)}
$top=@($rows|Select-Object -First 3)
$top|ConvertTo-Json -Depth 4|Set-Content (Join-Path $root 'artifacts\perf\diagnose-latest.json') -Encoding UTF8
Write-Output ('PERF_DIAGNOSE_OK top='+(($top.metric)-join ','))
