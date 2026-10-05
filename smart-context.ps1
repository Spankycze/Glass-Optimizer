param(
  [Parameter(Mandatory=$true,Position=0)][string]$Task,
  [ValidateSet('auto','plan','pack')][string]$Mode='auto'
)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$root=$PSScriptRoot
$projectName=Split-Path $root -Leaf
$changed=@()
if(Test-Path (Join-Path $root '.git')){
  $changed=@(git -c core.autocrlf=false -C $root diff --name-only HEAD 2>$null | Where-Object {$_})
}
$text=$Task.ToLowerInvariant()
$wide=($text -match 'rework|refactor|vse|cel|komplet|architecture|architektur|napric|across|multi|performance audit|audit')
$risk=($text -match 'crash|deadlock|freeze|driver|service|registry|defender|security|install|runtime|startup|thread|async')
$symbol=($text -match 'method|class|symbol|caller|callee|reference|funkc|trid|metod')
$loggy=($text -match 'log|trace|dump|report|build output|diagnostic')
$category=if($loggy){'logs'}elseif($risk){'risk'}elseif($wide){'wide'}elseif($symbol){'symbol'}elseif($changed.Count -gt 0){'changed'}else{'general'}

$budget=if($wide){12000}elseif($risk){9000}elseif($changed.Count -ge 8){8000}elseif($changed.Count -ge 3){6000}else{4000}
if($projectName -eq 'RAM Guard'){ $budget=[Math]::Min($budget,8000) }

$learnDir=Join-Path $root '.smart-learning'
$statsFile=Join-Path $learnDir 'stats.json'
$stats=$null
if(Test-Path $statsFile){
  try{$stats=Get-Content -Raw $statsFile | ConvertFrom-Json}catch{}
}
if($stats){
  $cat=$stats.PSObject.Properties[$category]
  if($cat){$budget=[int]$cat.Value.budget}
}
if($projectName -eq 'RAM Guard'){ $budget=[Math]::Min($budget,8000) }

$route=if($loggy){'capsule/context-compress'}elseif($risk -or $wide){'calm+redcon'}elseif($symbol){'codeindex/calm'}elseif($changed.Count -gt 0){'git-diff+redcon'}else{'repo-map+redcon'}

# Adaptive route blacklist.
$blocked=$false
if($stats){
  $cat=$stats.PSObject.Properties[$category]
  if($cat -and $cat.Value.routes){
    $rp=$cat.Value.routes.PSObject.Properties[$route]
    if($rp -and $rp.Value.blocked_until){
      try{$until=[datetime]$rp.Value.blocked_until; if($until -gt (Get-Date)){$blocked=$true}}catch{}
    }
  }
}
if($blocked){
  $old=$route
  $route=if($projectName -eq 'Spankyho Optimizer'){'codeindex/calm'}else{'repo-map+redcon'}
  Write-Host ('ADAPTIVE_BLACKLIST='+$old+' -> '+$route)
}

# Failed-attempt dedup. Same task + same HEAD + same changed file set will not repeat a recent failed route.
$head=if(Test-Path (Join-Path $root '.git')){(git -C $root rev-parse HEAD).Trim()}else{'nogit'}
$fingerprint=($Task+'|'+$route+'|'+$head+'|'+(($changed | Sort-Object) -join ','))
$sha=[Security.Cryptography.SHA256]::Create()
$hash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($fingerprint)))).Replace('-','').Substring(0,16)
$failFile=Join-Path $learnDir ('fail-'+$hash+'.json')
if(Test-Path $failFile){
  try{
    $f=Get-Content -Raw $failFile | ConvertFrom-Json
    $when=[datetime]$f.time
    if((Get-Date)-$when -lt [timespan]::FromHours(12)){
      Write-Host ('FAILED_ATTEMPT_DEDUP=skip route='+$route+' previous='+$f.time)
      Write-Host 'ESCALATE=use alternative route or inspect prior failure before retry'
      exit 3
    }
  }catch{}
}

Write-Host ('SMART_CATEGORY='+$category)
Write-Host ('SMART_ROUTE='+$route)
Write-Host ('SMART_BUDGET='+$budget)
Write-Host ('CHANGED_FILES='+$changed.Count)
New-Item -ItemType Directory -Force $learnDir | Out-Null
$decision=[ordered]@{task=$Task;category=$category;route=$route;budget=$budget;changed_files=$changed.Count;fingerprint=$hash}
$decision | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $root '.smart-route.json')

if($changed.Count -gt 0){
  Write-Host '--- CHANGED ---'
  $changed | Select-Object -First 20
  Write-Host '--- DIFF STAT ---'
  git -c core.autocrlf=false -C $root diff --stat HEAD
}

$isSpanky=($projectName -eq 'Spankyho Optimizer')
if($isSpanky -and $Mode -eq 'auto' -and ($route -eq 'calm+redcon' -or $route -eq 'codeindex/calm')){
  Write-Host 'AUTO_ESCALATE=CALM/CODEINDEX_FIRST'
  Write-Host 'RED_CON_ESCALATION=only-if-lightweight-index-insufficient'
  exit 0
}

if($Mode -eq 'plan' -or ($Mode -eq 'auto' -and -not $wide)){
  $args=@('plan',$Task,'--repo',$root)
}else{
  $args=@('pack',$Task,'--repo',$root,'--max-tokens',$budget,'--compression-profile','max','--format','context-only')
}
if($changed.Count -gt 0){$args += '--changed'; $args += $changed}
& 'C:\Users\spnka\AppData\Roaming\Python\Python314\Scripts\redcon.exe' @args
$code=$LASTEXITCODE
if($code -ne 0){
  $fail=[ordered]@{time=(Get-Date).ToString('o');task=$Task;route=$route;category=$category;code=$code}
  $fail | ConvertTo-Json | Set-Content -Encoding utf8 $failFile
}else{
  Remove-Item $failFile -ErrorAction SilentlyContinue
}
exit $code