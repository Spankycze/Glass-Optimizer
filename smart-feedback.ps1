param(
  [ValidateSet('success','fail')][string]$Result,
  [string]$Note=''
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$routeFile=Join-Path $root '.smart-route.json'
if(!(Test-Path $routeFile)){ throw 'No .smart-route.json found. Run smart-context.ps1 first.' }
$d=Get-Content -Raw $routeFile | ConvertFrom-Json
if(!$d.category -or !$d.route){ throw 'Invalid smart route state: missing category/route.' }
$dir=Join-Path $root '.smart-learning'
New-Item -ItemType Directory -Force $dir | Out-Null
$statsFile=Join-Path $dir 'stats.json'
function ConvertTo-Hashtable($obj){
  if($null -eq $obj){ return $null }
  if($obj -is [System.Collections.IDictionary]){
    $h=@{}
    foreach($k in $obj.Keys){$h[$k]=ConvertTo-Hashtable $obj[$k]}
    return $h
  }
  if($obj -is [pscustomobject]){
    $h=@{}
    foreach($p in $obj.PSObject.Properties){$h[$p.Name]=ConvertTo-Hashtable $p.Value}
    return $h
  }
  if($obj -is [System.Collections.IEnumerable] -and -not ($obj -is [string])){
    return @($obj | ForEach-Object {ConvertTo-Hashtable $_})
  }
  return $obj
}
$stats=if(Test-Path $statsFile){ConvertTo-Hashtable (Get-Content -Raw $statsFile | ConvertFrom-Json)}else{@{}}
$key=[string]$d.category
if(!$stats.ContainsKey($key)){
  $stats[$key]=@{budget=[int]$d.budget;success=0;fail=0;routes=@{}}
}
$s=$stats[$key]
if(!$s.routes.ContainsKey([string]$d.route)){
  $s.routes[[string]$d.route]=@{success=0;fail=0;blocked_until=$null}
}
$r=$s.routes[[string]$d.route]
if($Result -eq 'success'){
  $s.success=[int]$s.success+1
  $r.success=[int]$r.success+1
  if(([int]$s.success % 3) -eq 0){$s.budget=[Math]::Max(2500,[int]$s.budget-500)}
  $r.blocked_until=$null
}else{
  $s.fail=[int]$s.fail+1
  $r.fail=[int]$r.fail+1
  $s.budget=[Math]::Min(16000,[int]$s.budget+1000)
  if(([int]$r.fail-[int]$r.success) -ge 3){
    $r.blocked_until=(Get-Date).AddHours(24).ToString('o')
  }
}
$stats | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $statsFile
$event=[ordered]@{time=(Get-Date).ToString('o');category=$key;route=$d.route;result=$Result;budget=$d.budget;note=$Note}
$event | ConvertTo-Json -Compress | Add-Content -Encoding utf8 (Join-Path $dir 'events.jsonl')
# Keep only last 40 events.
$ev=Get-Content (Join-Path $dir 'events.jsonl') -ErrorAction SilentlyContinue
if($ev.Count -gt 40){$ev | Select-Object -Last 40 | Set-Content -Encoding utf8 (Join-Path $dir 'events.jsonl')}
Write-Host ('SMART_FEEDBACK='+$Result+' category='+$key+' next_budget='+$s.budget)