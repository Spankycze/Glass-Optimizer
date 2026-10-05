param([switch]$NoFail)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$benchPath=Join-Path $root '.perf\latest\bench.json'
$budgetPath=Join-Path $root 'perf-budget.json'
if(-not(Test-Path $benchPath)){throw 'bench.json missing; run perf-bench.ps1 first'}
$bench=Get-Content $benchPath -Raw|ConvertFrom-Json
$budget=Get-Content $budgetPath -Raw|ConvertFrom-Json
$rows=@();$failed=$false
foreach($p in $budget.metrics.psobject.Properties){
  $name=$p.Name;$b=$p.Value
  $metric=$bench.metrics.$name
  if($null -eq $metric){continue}
  $value=[double]$metric.median
  $warn=[double]$b.baseline*(1+[double]$b.warnPct/100)
  $fail=[math]::Min([double]$b.absoluteFail,[double]$b.baseline*(1+[double]$b.failPct/100))
  $status=if($value -gt $fail){'FAIL'}elseif($value -gt $warn){'WARN'}else{'OK'}
  if($status -eq 'FAIL'){$failed=$true}
  $rows+=[pscustomobject]@{metric=$name;value=$value;baseline=[double]$b.baseline;warn=[math]::Round($warn,1);fail=[math]::Round($fail,1);status=$status}
}
if($bench.soak){
  foreach($name in @('privateDeltaMB','handleDelta')){
    $b=$budget.metrics.$name
    if($null -eq $b){continue}
    $value=[double]$bench.soak.$name
    $status=if($value -gt [double]$b.absoluteFail){'FAIL'}else{'OK'}
    if($status -eq 'FAIL'){$failed=$true}
    $rows+=[pscustomobject]@{metric=$name;value=$value;baseline=[double]$b.baseline;warn='-';fail=[double]$b.absoluteFail;status=$status}
  }
}
$rows|Format-Table -AutoSize|Out-String|Write-Output
$rows|ConvertTo-Json -Depth 4|Set-Content (Join-Path $root '.perf\latest\gate.json') -Encoding utf8
if($failed -and -not $NoFail){Write-Error 'PERF_GATE_FAIL';exit 42}
Write-Output 'PERF_GATE_OK'
