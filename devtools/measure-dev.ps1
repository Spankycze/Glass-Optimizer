[CmdletBinding()]
param(
 [Parameter(Mandatory)][ValidateSet('Graphics','AddonTests','BrowserQuick','BrowserQuickNode','CoreBuild','Quality','WinUIBuild','DevCycleSelfTest')][string]$Target,
 [ValidateRange(1,10)][int]$Runs=1,
 [switch]$NoRecord
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$project=Split-Path $root -Leaf
$ram=$project -eq 'RAM Guard'
$optimizer=$project -eq 'Spankyho Optimizer'
if(!$ram -and !$optimizer){throw 'MEASURE_UNKNOWN_PROJECT'}
if($ram -and $Target -in @('CoreBuild','Quality','WinUIBuild','DevCycleSelfTest')){throw 'MEASURE_TARGET_NOT_APPLICABLE'}
if($optimizer -and $Target -in @('AddonTests','BrowserQuick','BrowserQuickNode')){throw 'MEASURE_TARGET_NOT_APPLICABLE'}
if($Target -in @('WinUIBuild','CoreBuild','Quality','BrowserQuick','BrowserQuickNode')){
 $others=@(Get-CimInstance Win32_Process -ErrorAction Stop |
  Where-Object {$_.ProcessId -ne $PID -and $_.Name -eq 'pwsh.exe' -and
   $_.CommandLine -match 'ChatGPT Projects' -and
   $_.CommandLine -match '(?:build-current\.ps1|build-release\.ps1|publish-current\.ps1)'})
 if($others.Count -gt 0){throw ('MEASURE_CONCURRENT_RELEASE_BUILD_BLOCKED count='+$others.Count)}
}
Push-Location $root
try{
 $records=@()
 for($run=1;$run -le $Runs;$run++){
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $output=''
  $rc=0
  switch($Target){
   Graphics {
    $first=& node.exe 'devtools/graphics/generate-assets.mjs' 2>&1|Out-String
    $rc=$LASTEXITCODE
    if($rc -ne 0){throw ('MEASURE_GENERATOR_FAILED '+$first)}
    $second=& node.exe 'devtools/graphics/smoke.mjs' 2>&1|Out-String
    $rc=$LASTEXITCODE
    $output=$first+$second
   }
   AddonTests { $output=& npm.cmd run test:addons --silent 2>&1|Out-String;$rc=$LASTEXITCODE }
   BrowserQuick { $output=& pwsh.exe -NoProfile -File 'devtools/verify-fast.ps1' -Area Browser 2>&1|Out-String;$rc=$LASTEXITCODE }
   BrowserQuickNode { $output=& pwsh.exe -NoProfile -File 'devtools/verify-fast.ps1' -Area Browser -ForceNodeBrowser 2>&1|Out-String;$rc=$LASTEXITCODE }
   CoreBuild { $output=& dotnet build 'src/SpankyhoOptimizer.Core/SpankyhoOptimizer.Core.csproj' -c Release --no-restore --nologo -v:q 2>&1|Out-String;$rc=$LASTEXITCODE }
   WinUIBuild { $output=& dotnet build 'src/SpankyhoOptimizer/SpankyhoOptimizer.csproj' -c Release -r win-x64 --no-restore --nologo -m -v:q 2>&1|Out-String;$rc=$LASTEXITCODE }
   Quality { $output=& dotnet test 'tools/Spankyho.QualityTests/Spankyho.QualityTests.csproj' -c Release --no-restore --nologo -v:q 2>&1|Out-String;$rc=$LASTEXITCODE }
   DevCycleSelfTest { $output=& pwsh.exe -NoProfile -File 'dev-cycle.ps1' -Mode SelfTest 2>&1|Out-String;$rc=$LASTEXITCODE }
  }
  $sw.Stop()
  if($rc -ne 0){throw ('MEASURE_TASK_FAILED target='+$Target+' run='+$run+' exit='+$rc+' '+$output.Substring(0,[Math]::Min(1500,$output.Length)))}
  if($Target -eq 'Graphics' -and $output -notmatch 'GRAPHICS_TOOLCHAIN_SMOKE_PASS'){throw 'MEASURE_GRAPHICS_SIGNAL_MISSING'}
  if($Target -in @('BrowserQuick','BrowserQuickNode') -and $output -notmatch 'RAM_GUARD_QUICK_PASS area=Browser files=0 syntax=0 fixtures=37'){throw 'MEASURE_BROWSER_37_FIXTURES_MISSING'}
  if($Target -eq 'AddonTests' -and $output -notmatch '\b12 pass\b'){throw 'MEASURE_ADDON_12_TESTS_MISSING'}
  $ms=[Math]::Round($sw.Elapsed.TotalMilliseconds,2)
  $records+= $ms
  Write-Host ('DEV_PERF_RESULT target='+$Target+' run='+$run+' ms='+$ms+' exit=0')
 }
 $ordered=@($records|Sort-Object)
 $median=if($ordered.Count % 2){[double]$ordered[[int][math]::Floor($ordered.Count/2)]}else{([double]$ordered[$ordered.Count/2-1]+[double]$ordered[$ordered.Count/2])/2}
 if(!$NoRecord){
  $folder=if($ram){Join-Path $root 'artifacts/.cache'}else{Join-Path $root '.ai-state'}
  [IO.Directory]::CreateDirectory($folder)|Out-Null
  $history=Join-Path $folder 'dev-performance-history.json'
  $prior=@()
  if(Test-Path -LiteralPath $history){
   $old=Get-Content -LiteralPath $history -Raw -Encoding UTF8 |ConvertFrom-Json
   $prior=@($old.runs)
  }
  $same=@($prior|Where-Object {$_.target -eq $Target -and $_.ms -gt 0}|Select-Object -Last 8)
  if($same.Count -ge 3){
   $vals=@($same|ForEach-Object {[double]$_.ms}|Sort-Object)
   $baseline=if($vals.Count%2){$vals[[int][math]::Floor($vals.Count/2)]}else{($vals[$vals.Count/2-1]+$vals[$vals.Count/2])/2}
   $limit=[Math]::Max($baseline*1.4,$baseline+2000)
   if($median -gt $limit){Write-Warning ('DEV_PERF_REGRESSION target='+$Target+' currentMs='+$median+' medianBaselineMs='+[Math]::Round($baseline,2)+' limitMs='+[Math]::Round($limit,2))}
   else{Write-Host ('DEV_PERF_STABLE target='+$Target+' medianBaselineMs='+[Math]::Round($baseline,2))}
  }else{Write-Host ('DEV_PERF_BASELINE_COLLECTING '+$Target+' priorSamples='+$same.Count)}
  $entry=[pscustomobject]@{utc=[DateTimeOffset]::UtcNow.ToString('o');project=$project;target=$Target;ms=$median;samples=$records;node=(& node.exe --version).Trim()}
  $new=@($prior|Select-Object -Last 119)+@($entry)
  $payload=@{schema=1;runs=$new}|ConvertTo-Json -Depth 7
  $temporary=Join-Path $folder ('dev-perf-'+[guid]::NewGuid().ToString('N')+'.tmp')
  try{
   [IO.File]::WriteAllText($temporary,$payload,[Text.UTF8Encoding]::new($false))
   [IO.File]::Move($temporary,$history,$true)
  }finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
  Write-Host ('DEV_PERF_HISTORY '+$history)
 }
 Write-Host ('DEV_PERF_PASS target='+$Target+' medianMs='+$median+' runs='+$Runs+' strictReleaseCertified=false')
}finally{Pop-Location}
