param(
  [Parameter(Mandatory=$true,Position=0)][string]$Task,
  [ValidateSet('auto','plan','pack')][string]$Mode='auto'
)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$root=$PSScriptRoot
$changed=@()
if(Test-Path (Join-Path $root '.git')){
  $changed=@(git -c core.autocrlf=false -C $root diff --name-only HEAD 2>$null | Where-Object {$_})
}
$text=$Task.ToLowerInvariant()
$wide=($text -match 'rework|refactor|vse|cel|komplet|architecture|architektur|napric|across|multi|performance audit|audit')
$risk=($text -match 'crash|deadlock|freeze|driver|service|registry|defender|security|install|runtime|startup|thread|async')
$symbol=($text -match 'method|class|symbol|caller|callee|reference|funkc|trid|metod')
$loggy=($text -match 'log|trace|dump|report|build output|diagnostic')
$budget=if($wide){12000}elseif($risk){9000}elseif($changed.Count -ge 8){8000}elseif($changed.Count -ge 3){6000}else{4000}
if((Split-Path $root -Leaf) -eq 'RAM Guard'){ $budget=[Math]::Min($budget,8000) }

$route=if($loggy){'capsule/context-compress'}elseif($risk -or $wide){'calm+redcon'}elseif($symbol){'codeindex/calm'}elseif($changed.Count -gt 0){'git-diff+redcon'}else{'repo-map+redcon'}
Write-Host ('SMART_ROUTE='+$route)
Write-Host ('SMART_BUDGET='+$budget)
Write-Host ('CHANGED_FILES='+$changed.Count)
$decision=[ordered]@{task=$Task;route=$route;budget=$budget;changed_files=$changed.Count}
$decision | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $root '.smart-route.json')

if($changed.Count -gt 0){
  Write-Host '--- CHANGED ---'
  $changed | Select-Object -First 20
  Write-Host '--- DIFF STAT ---'
  git -c core.autocrlf=false -C $root diff --stat HEAD
}

$isSpanky=((Split-Path $root -Leaf) -eq 'Spankyho Optimizer')
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
exit $LASTEXITCODE