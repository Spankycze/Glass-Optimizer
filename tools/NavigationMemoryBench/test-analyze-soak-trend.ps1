$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('spanky-soak-'+[guid]::NewGuid().ToString('N'))
try{
 [IO.Directory]::CreateDirectory($root)|Out-Null
 $rows=@(1..8|ForEach-Object{[ordered]@{cycle=$_;workingSetMiB=100+$_;privateMiB=60+($_*2);handles=1000+($_*3)}})
 $j=[ordered]@{samples=$rows;actions=96;finalWsMiB=108;idleFinalWsMiB=101;idleHandleCount=1003}
 $file=Join-Path $root 'one.json'
 [IO.File]::WriteAllText($file,($j|ConvertTo-Json -Depth 5))
 $out=Join-Path $root 'summary.json'
 & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'analyze-soak-trend.ps1') -InputGlob $file -Output $out | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Analyzer did not finish'}
 $r=Get-Content $out -Raw|ConvertFrom-Json
 if($r.detail[0].wsLateMinusEarlyMiB -ne 6 -or $r.detail[0].privateLateMinusEarlyMiB -ne 12 -or $r.detail[0].handlesLateMinusEarly -ne 18 -or $r.detail[0].idleWsReleasedMiB -ne 7 -or $r.detail[0].wsLastQuarterMinusThirdMiB -ne 2 -or $r.detail[0].privateLastQuarterMinusThirdMiB -ne 4 -or $r.detail[0].handlesLastQuarterMinusThird -ne 6){throw 'Incorrect trend calculation'}
 Write-Output 'SOAK_ANALYZER_TESTS_PASS count=7'
}finally{Remove-Item $root -Force -Recurse -ErrorAction SilentlyContinue}
