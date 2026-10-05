param([switch]$Deep,[int]$SinceMinutes=240,[switch]$NoRuntime)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$impactPath=Join-Path $root '.ai-state\change-impact.json'
& (Join-Path $root 'change-impact.ps1') -SinceMinutes $SinceMinutes -OutputPath $impactPath
$impact=Get-Content $impactPath -Raw|ConvertFrom-Json
$selected=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($x in @($impact.tests)){[void]$selected.Add([string]$x)}
if($Deep){foreach($x in @('fault','fuzz','rollback','architecture','platform','mutation','coyote','flaky','visual-regression','cache','interference','perf','memory-retention')){[void]$selected.Add($x)}}
if($NoRuntime){foreach($x in @('navigation-race','page-scroll-input','visual-size','visual-regression','cache','interference','perf')){[void]$selected.Remove($x)}}
$steps=New-Object Collections.Generic.List[object]
function RunStep([string]$name,[scriptblock]$action){
  $sw=[Diagnostics.Stopwatch]::StartNew();$status='PASS';$msg=''
  try{$global:LASTEXITCODE=0;$out=@(& $action 2>&1);$code=if($null -eq $LASTEXITCODE){0}else{[int]$LASTEXITCODE};$msg=($out|Select-Object -Last 12)-join [Environment]::NewLine;if($code -ne 0){$status='FAIL'}}
  catch{$status='FAIL';$msg=$_.Exception.Message}
  finally{$sw.Stop()}
  $steps.Add([pscustomobject]@{name=$name;status=$status;ms=[math]::Round($sw.Elapsed.TotalMilliseconds,1);message=$msg})|Out-Null
  Write-Output ('SMART_STEP '+$name+' '+$status+' '+[math]::Round($sw.Elapsed.TotalMilliseconds,1)+'ms')
}
$map=@{}
$map['build-fast']={& (Join-Path $root 'build-fast.ps1')}
$map['build-speed']={& (Join-Path $root 'xaml-reuse-audit.ps1'); if($LASTEXITCODE -eq 0){& (Join-Path $root 'build-speed-audit.ps1')}}
$map['validate-tooling']={& (Join-Path $root 'validate-tooling.ps1')}
$map['source-index']={& (Join-Path $root 'source-index.ps1')}
$map['extended-analyzers']={& (Join-Path $root 'extended-analyzers.ps1') -Full:$Deep; if($LASTEXITCODE -eq 0){& (Join-Path $root 'analyzer-regression-gate.ps1')}}
$map['api-compat']={& (Join-Path $root 'api-compat-gate.ps1')}
$map['architecture']={dotnet test (Join-Path $root 'tools\ArchitectureTests\ArchitectureTests.csproj') -c Release --nologo --no-restore}
$map['platform']={& (Join-Path $root 'platform-compat-gate.ps1')}
$map['navigation-race']={& (Join-Path $root 'navigation-race-audit.ps1')}
$map['page-scroll-input']={& (Join-Path $root 'page-scroll-input-audit.ps1')}
$map['visual-size']={& (Join-Path $root 'visual-size-audit.ps1')}
$map['visual-regression']={& (Join-Path $root 'visual-regression.ps1')}
$map['fault']={& (Join-Path $root 'fault-audit.ps1')}
$map['fuzz']={& (Join-Path $root 'run-fuzz.ps1') -Mode smoke}
$map['rollback']={& (Join-Path $root 'rollback-matrix.ps1') -NoRestore}
$map['mutation']={& (Join-Path $root 'mutation-audit.ps1') -Concurrency 4}
$map['coyote']={& (Join-Path $root 'run-coyote.ps1')}
$map['flaky']={& (Join-Path $root 'flaky-audit.ps1') -Iterations 3}
$map['cache']={& (Join-Path $root 'cache-correctness-audit.ps1') -Quick:(!$Deep)}
$map['interference']={& (Join-Path $root 'background-interference-audit.ps1') -Repeats $(if($Deep){6}else{3})}
$map['memory-retention']={& (Join-Path $root 'memory-retention-audit.ps1') -CriticalRepeats $(if($Deep){3}else{1}) -NavRepeats $(if($Deep){2}else{1})}
$map['perf']={
  & (Join-Path $root 'perf-bench.ps1') -Repeats $(if($Deep){5}else{3}) -NetworkRepeats 0
  if($LASTEXITCODE -ne 0){return}
  & (Join-Path $root 'perf-gate.ps1')
  if($LASTEXITCODE -ne 0){return}
  & (Join-Path $root 'ui-thread-budget-audit.ps1')
  if($LASTEXITCODE -ne 0){return}
  & (Join-Path $root 'performance-diagnose.ps1')
}
$order=@('build-fast','build-speed','validate-tooling','source-index','extended-analyzers','api-compat','architecture','platform','navigation-race','page-scroll-input','visual-size','cache','interference','memory-retention','perf','fault','fuzz','rollback','coyote','flaky','mutation','visual-regression')
foreach($name in $order){if($selected.Contains($name) -and $map.ContainsKey($name)){RunStep $name $map[$name]}}
if($Deep -or (@($impact.areas) -contains 'tooling')){RunStep 'cleanup-audit' {& (Join-Path $root 'cleanup-audit.ps1')}}
$fail=@($steps|Where-Object status -eq 'FAIL')
$summary=[ordered]@{generatedAt=(Get-Date).ToString('o');deep=[bool]$Deep;areas=@($impact.areas);files=@($impact.files);selected=@($selected|Sort-Object);pass=$steps.Count-$fail.Count;fail=$fail.Count;steps=$steps}
$out=Join-Path $root 'artifacts\quality\smart-latest.json'
New-Item -ItemType Directory -Force -Path (Split-Path $out)|Out-Null
$summary|ConvertTo-Json -Depth 8|Set-Content $out -Encoding UTF8
Write-Output ('SMART_QUALITY_SUMMARY pass='+$summary.pass+' fail='+$summary.fail+' areas='+($summary.areas -join ',')+' report='+$out)
if($fail.Count){$fail|ForEach-Object{Write-Output ('SMART_FAIL '+$_.name+' '+$_.message)};exit 42}
$baselineOut=@(& (Join-Path $root 'change-impact.ps1') -SaveBaseline 2>&1)
$baselineOut|Select-Object -Last 1|ForEach-Object{Write-Output $_}
Write-Output 'SMART_QUALITY_OK'
