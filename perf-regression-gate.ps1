param([string]$ReportPath,[switch]$UpdateBaseline)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
if([string]::IsNullOrWhiteSpace($ReportPath)){$ReportPath=Join-Path $root 'artifacts\perf\latest.json'}
$baselinePath=Join-Path $root '.ai-state\perf-regression-baseline.json'
if(-not(Test-Path $ReportPath)){throw 'Performance report nebyl nalezen: '+$ReportPath}
$r=Get-Content $ReportPath -Raw|ConvertFrom-Json
if($UpdateBaseline -or -not(Test-Path $baselinePath)){
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $baselinePath)|Out-Null
  $r|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $baselinePath -Encoding UTF8
  Write-Output ('PERF_GATE_BASELINE_SAVED '+$baselinePath)
  exit 0
}
$b=Get-Content $baselinePath -Raw|ConvertFrom-Json
$warnings=New-Object System.Collections.Generic.List[string]
$fails=New-Object System.Collections.Generic.List[string]
function Add-Warn([string]$m){$warnings.Add($m)}
function Add-Fail([string]$m){$fails.Add($m)}
$sections='network','storage','gpu','startup','services','defender','debloat','power'
foreach($s in $sections){
  $cold=[double]$r.cold.$s.medianMs; $warm=[double]$r.warm.$s.medianMs
  if($cold -gt 30){Add-Fail ('cold {0} {1:n1} ms > 30 ms' -f $s,$cold)}
  if($warm -gt 10){Add-Warn ('warm {0} {1:n1} ms > 10 ms' -f $s,$warm)}
}
if($null -ne $r.soak){if([double]$r.soak.privateDeltaMb -gt 20){Add-Fail ('soak private delta {0:n1} MB > 20 MB' -f $r.soak.privateDeltaMb)};if([double]$r.soak.handleDelta -gt 20){Add-Fail ('soak handles +{0:n0}' -f $r.soak.handleDelta)}}
if($null -ne $b.subprocess -and $null -ne $r.subprocess){if([int]$r.subprocess.powershellCount -gt ([int]$b.subprocess.powershellCount+2)){Add-Warn ('PowerShell launches {0}->{1}' -f $b.subprocess.powershellCount,$r.subprocess.powershellCount)}}
$statScript=Join-Path $root 'perf-statistics.ps1'
if(Test-Path $statScript){
  @(& $statScript -ReportPath $ReportPath)|ForEach-Object{Write-Output $_}
  $statPath=Join-Path $root 'artifacts\perf\statistics-latest.json'
  if(Test-Path $statPath){
    try{
      $stats=Get-Content $statPath -Raw|ConvertFrom-Json
      foreach($x in @($stats.regressions)){
        $severity=[math]::Max([double]$x.deltaPct,[double]$x.p95DeltaPct)
        $msg=('stat {0} median={1:+0.0;-0.0;0.0}% p95={2:+0.0;-0.0;0.0}% p={3} noiseThreshold={4:n1}% var={5:n3}' -f $x.metric,$x.deltaPct,$x.p95DeltaPct,$x.pValue,$x.thresholdPct,$x.baselineVariance)
        if([double]$x.pValue -lt 0.01 -and $severity -gt ([double]$x.thresholdPct*1.5)){Add-Fail $msg}else{Add-Warn $msg}
      }
      foreach($x in @($stats.changePoints)){
        Add-Warn ('change-point {0} +{1:n1}% score={2:n2}' -f $x.metric,$x.changePoint.deltaPct,$x.changePoint.score)
      }
    }catch{Add-Warn ('stat detector read failed: '+$_.Exception.Message)}
  }
}
$warnings|ForEach-Object{Write-Output ('PERF_GATE_WARN '+$_)}
$fails|ForEach-Object{Write-Output ('PERF_GATE_FAIL '+$_)}
Write-Output ('PERF_GATE_SUMMARY warnings={0} fails={1}' -f $warnings.Count,$fails.Count)
if($fails.Count -gt 0){exit 42}
exit 0