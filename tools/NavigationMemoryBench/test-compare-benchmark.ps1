$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'compare-benchmark.ps1'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('spanky-nav-compare-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($tmp)
function Put([string]$folder, [int]$idle, [int]$warmup, [int]$sample, [bool]$profile) {
 $path = Join-Path $tmp $folder
 [void][IO.Directory]::CreateDirectory($path)
 for ($i = 0; $i -lt 2; $i++) {
  $obj = [ordered]@{
   cycles=16;warmupCycles=2;warmupIdleMs=$warmup;actions=32;pauseMs=105
   sections=@('Themes','Nastavení');idleTailMs=$idle;idleSampleMs=$sample;profileSections=$profile
   processCpuMs=1000.0;finalWsMiB=250.0;finalPrivateMiB=180.0
  }
  [IO.File]::WriteAllText((Join-Path $path ("run-$i.json")),($obj | ConvertTo-Json -Depth 4))
 }
 return (Join-Path $path '*.json')
}
function Check([string]$caseName,[string]$baseline,[string]$candidate,[int]$wantExit) {
 $out = & pwsh -NoProfile -File $script -BaselineGlob $baseline -CandidateGlob $candidate 2>&1
 $code = $LASTEXITCODE
 if ($code -ne $wantExit) { throw "$caseName expected exit $wantExit but got $code. $out" }
 Write-Output "COMPARE_TEST_PASS $caseName"
}
try {
 $base = Put base 0 0 0 $false
 $same = Put same 0 0 0 $false
 $differentWarm = Put differentWarm 0 2000 0 $false
 $differentTail = Put differentTail 11000 0 1000 $false
 $differentProfile = Put differentProfile 0 0 0 $true
 $differentSample = Put differentSample 11000 0 2000 $false
 $sameTail = Put sameTail 11000 0 1000 $false
 Check equal-run-config $base $same 12
 Check reject-warmup-idle-mismatch $base $differentWarm 1
 Check reject-idle-tail-mismatch $base $differentTail 1
 Check reject-profile-mismatch $base $differentProfile 1
 Check reject-idle-sampling-mismatch $sameTail $differentSample 1
 Write-Output 'NAV_COMPARE_REGRESSION_TESTS_PASS count=5'
}
finally {
 if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
}
