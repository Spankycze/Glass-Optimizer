param(
  [ValidateSet('record','check')][string]$Mode='check',
  [int]$Runs=5,
  [double]$RegressionPct=20
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'dev-perf-env.ps1')
$hf=Get-Command hyperfine.exe -ErrorAction SilentlyContinue
if(!$hf){throw 'hyperfine missing'}
$dir=Join-Path $PSScriptRoot '.perf-baseline'
New-Item -ItemType Directory -Force $dir | Out-Null
$name=Split-Path $PSScriptRoot -Leaf
$json=Join-Path $dir 'latest.json'
$baseline=Join-Path $dir 'baseline.json'
$cmds=@()
$names=@()
if($name -eq 'RAM Guard'){
  $cmds+='powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-fast.ps1 -Mode Addon -NoLaunch'
  $names+='addon-fast'
  $cmds+='powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\fast-search.ps1 RAM_GUARD_FAST_BUILD_OK build-fast.ps1'
  $names+='search'
}else{
  $cmds+='dotnet build .\src\SpankyhoOptimizer.Core\SpankyhoOptimizer.Core.csproj -c Debug --no-restore --nologo -m -v:q -p:RunAnalyzersDuringBuild=false'
  $names+='core-build'
  $cmds+='powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-native.ps1 -Debug'
  $names+='native-cache'
  $cmds+='powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\fast-search.ps1 SpankyhoOptimizer src'
  $names+='search'
}
$args=@('--warmup','1','--runs',[string]$Runs,'--export-json',$json)
for($i=0;$i -lt $cmds.Count;$i++){
  $args+=@('--command-name',$names[$i],$cmds[$i])
}
Push-Location $PSScriptRoot
try{
  & $hf.Source @args
  $hfCode=$LASTEXITCODE
}finally{Pop-Location}
if($hfCode -ne 0){exit $hfCode}
if($Mode -eq 'record' -or !(Test-Path $baseline)){
  Copy-Item $json $baseline -Force
  Write-Host 'PERF_BASELINE_RECORDED'
  exit 0
}
$cur=Get-Content -Raw $json|ConvertFrom-Json
$base=Get-Content -Raw $baseline|ConvertFrom-Json
$failed=$false
foreach($r in $cur.results){
  $b=$base.results|Where-Object command -eq $r.command|Select-Object -First 1
  if(!$b){continue}
  $pct=(($r.mean-$b.mean)/$b.mean)*100
  Write-Host ('PERF '+$r.command+' mean='+([math]::Round($r.mean,3))+'s delta='+([math]::Round($pct,1))+'%')
  if($pct -gt $RegressionPct){$failed=$true}
}
if($failed){Write-Error ('PERF_REGRESSION > '+$RegressionPct+'%');exit 2}
Write-Host 'PERF_REGRESSION_GATE_OK'