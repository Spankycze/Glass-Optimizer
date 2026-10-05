param(
  [string]$Label='task'
)
$ErrorActionPreference='Continue'
$root=$PSScriptRoot
$dir=Join-Path $root '.roi'
New-Item -ItemType Directory -Force $dir | Out-Null
$profile=Join-Path $root '.tool-profile\latest.json'
$route=Join-Path $root '.smart-route.json'
$learning=Join-Path $root '.smart-learning\stats.json'
$p=if(Test-Path $profile){Get-Content -Raw $profile | ConvertFrom-Json}else{@()}
$r=if(Test-Path $route){Get-Content -Raw $route | ConvertFrom-Json}else{$null}
$sumMs=0
$ok=0
$bad=0
foreach($x in @($p)){
  $sumMs += [int]$x.ms
  if($x.ok){$ok++}else{$bad++}
}
$changed=if(Test-Path (Join-Path $root '.git')){@(git -c core.autocrlf=false -C $root diff --name-only HEAD)}else{@()}
$report=[ordered]@{
  label=$Label
  time=(Get-Date).ToString('o')
  route=if($r){$r.route}else{$null}
  category=if($r){$r.category}else{$null}
  budget=if($r){$r.budget}else{$null}
  changed_files=$changed.Count
  profiled_tools=@($p).Count
  tool_ms_total=$sumMs
  tool_ok=$ok
  tool_fail=$bad
  learning_present=(Test-Path $learning)
}
$report | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $dir 'latest.json')
Write-Host ('ROI route='+$report.route+' budget='+$report.budget+' changed='+$report.changed_files+' tool_ms='+$sumMs+' failures='+$bad)
