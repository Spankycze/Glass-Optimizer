$ErrorActionPreference='Stop'
$analyzer=Join-Path $PSScriptRoot 'analyze-idle-timeline.ps1'
$temp=Join-Path ([IO.Path]::GetTempPath()) ('spanky-idle-' + [guid]::NewGuid().ToString('N'))
function Make([string]$group,[int]$tail,[double]$finalWs) {
 $dir=Join-Path $temp $group
 [IO.Directory]::CreateDirectory($dir)|Out-Null
 for($i=0;$i -lt 2;$i++){
  $r=[ordered]@{
    cycles=8;warmupCycles=2;warmupIdleMs=3000;actions=16;pauseMs=105;sections=@('Themes','Nastavení')
    profileSections=$false;idleTailMs=$tail;idleSampleMs=1000
    processCpuMs=900.0;finalWsMiB=250.0;idleCpuMs=30.0;idleFinalWsMiB=$finalWs;idleHandleCount=40
    idleSamples=@(
      @{elapsedMs=1000;cpuDeltaMs=20.0;workingSetMiB=260.0;handles=50},
      @{elapsedMs=2000;cpuDeltaMs=10.0;workingSetMiB=$finalWs;handles=40})
  }
  [IO.File]::WriteAllText((Join-Path $dir "test-$i.json"),($r|ConvertTo-Json -Depth 6))
 }
 return (Join-Path $dir '*.json')
}
try{
 $baseline=Make 'baseline' 2000 250
 $candidate=Make 'candidate' 2000 245
 $invalid=Make 'invalid' 3000 245
 $out=Join-Path $temp 'summary.json'
 & pwsh -NoProfile -File $analyzer -BaselineGlob $baseline -CandidateGlob $candidate -Output $out | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Valid idle analysis failed'}
 $report=Get-Content -LiteralPath $out -Raw|ConvertFrom-Json
 if($report.candidateMinusBaseline.idleFinalWsMiB -ne -5 -or
    $report.baseline.mean.idleHandlesReleasedFromPeak -ne 10){
  throw 'Incorrect timeline averages'
 }
 & pwsh -NoProfile -File $analyzer -BaselineGlob $baseline -CandidateGlob $invalid 2>&1 | Out-Null
 if($LASTEXITCODE -eq 0){throw 'Mismatched methodology not rejected'}
 Write-Output 'IDLE_ANALYZER_TESTS_PASS count=2'
}
finally{Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue}
