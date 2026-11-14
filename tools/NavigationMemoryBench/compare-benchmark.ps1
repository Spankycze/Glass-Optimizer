param(
 [Parameter(Mandatory)][string]$BaselineGlob,
 [Parameter(Mandatory)][string]$CandidateGlob,
 [string]$Output = '',
 [double]$MaxCpuRegressionPct = 3.0,
 [double]$MaxWorkingSetRegressionMiB = 2.0,
 [double]$MinCpuImprovementPct = 2.5,
 [double]$MinWorkingSetImprovementMiB = 2.0
)
$ErrorActionPreference='Stop'
function Run-Identity($r) {
 $warmupIdle=[int]$r.warmupIdleMs
 $tail=[int]$r.idleTailMs
 $idleSample=if($tail -gt 0){[int]$r.idleSampleMs}else{0}
 $profile=[bool]$r.profileSections
 $managed=[bool]$r.profileManagedHeap
 return "$($r.cycles)|$($r.warmupCycles)|$warmupIdle|$($r.actions)|$($r.pauseMs)|$tail|$idleSample|$profile|$managed|$($r.sections -join ',')"
}
function Read-Runs([string]$pattern) {
 $files=@(Get-ChildItem -Path $pattern -File -ErrorAction Stop)
 if($files.Count -lt 2){throw "At least 2 runs required for $pattern"}
 $runs=@(foreach($f in $files){Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json})
 $key=$null
 foreach($r in $runs){
  $identity=Run-Identity $r
  if($key -eq $null){$key=$identity}
  if($identity -ne $key){throw "Incompatible benchmark configurations"}
 }
 return $runs
}
$baseline=@(Read-Runs $BaselineGlob)
$candidate=@(Read-Runs $CandidateGlob)
$identityA=Run-Identity $baseline[0]
$identityB=Run-Identity $candidate[0]
if($identityA -ne $identityB){throw "Baseline/candidate settings differ"}
function Average($runs,[string]$property){return [double](($runs|Measure-Object -Property $property -Average).Average)}
$oldCpu=Average $baseline 'processCpuMs'; $newCpu=Average $candidate 'processCpuMs'
$oldWs=Average $baseline 'finalWsMiB'; $newWs=Average $candidate 'finalWsMiB'
$oldPriv=Average $baseline 'finalPrivateMiB'; $newPriv=Average $candidate 'finalPrivateMiB'
if($oldCpu -le 0){throw 'Invalid baseline CPU measurement'}
$cpuDelta=100*($newCpu-$oldCpu)/$oldCpu
$wsDelta=$newWs-$oldWs
$meaningfulGain = ($cpuDelta -le -$MinCpuImprovementPct -and $wsDelta -le 0) -or
    ($wsDelta -le -$MinWorkingSetImprovementMiB -and $cpuDelta -le 0)
$verdict=if($cpuDelta -gt $MaxCpuRegressionPct -or $wsDelta -gt $MaxWorkingSetRegressionMiB){
    'REGRESSION'
} elseif($meaningfulGain){
    'POTENTIAL_GAIN_NEEDS_GATE'
} else {
    'NO_GAIN'
}
$result=[ordered]@{
 verdict=$verdict; baselineRuns=$baseline.Count; candidateRuns=$candidate.Count
 actions=$baseline[0].actions; warmupCycles=$baseline[0].warmupCycles
 warmupIdleMs=[int]$baseline[0].warmupIdleMs;idleTailMs=[int]$baseline[0].idleTailMs
 baselineCpuMs=[math]::Round($oldCpu,2); candidateCpuMs=[math]::Round($newCpu,2); cpuDeltaPct=[math]::Round($cpuDelta,2)
 baselineWsMiB=[math]::Round($oldWs,2); candidateWsMiB=[math]::Round($newWs,2); wsDeltaMiB=[math]::Round($wsDelta,2)
 baselinePrivateMiB=[math]::Round($oldPriv,2); candidatePrivateMiB=[math]::Round($newPriv,2)
}
$json=$result|ConvertTo-Json -Depth 4
if($Output){[IO.File]::WriteAllText($Output,$json,[Text.UTF8Encoding]::new($false))}
Write-Output ('NAV_COMPARE '+($json -replace '\s+',' '))
if($verdict -eq 'REGRESSION'){exit 13}
if($verdict -eq 'NO_GAIN'){exit 12}
