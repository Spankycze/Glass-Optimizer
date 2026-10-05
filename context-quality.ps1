param(
  [Parameter(Mandatory=$true,Position=0)][string]$Task
)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$root=$PSScriptRoot
$changed=@()
if(Test-Path (Join-Path $root '.git')){
  $changed=@(git -c core.autocrlf=false -C $root diff --name-only HEAD 2>$null | Where-Object {$_})
}
$routeFile=Join-Path $root '.smart-route.json'
$route=$null
if(Test-Path $routeFile){
  try{$route=Get-Content -Raw $routeFile | ConvertFrom-Json}catch{}
}
$score=0
$evidence=@()
if(Test-Path (Join-Path $root 'CURRENT_STATE.md')){$score+=20;$evidence+='current-state'}
if($changed.Count -gt 0){$score+=25;$evidence+='git-diff'}else{$score+=10;$evidence+='clean-tree'}
if($route){$score+=20;$evidence+='smart-route'}
if(Test-Path (Join-Path $root 'atlas-map.md')){$score+=10;$evidence+='repo-map'}
if(Test-Path (Join-Path $root '.calm\index.db')){$score+=15;$evidence+='calm-index'}
if(Test-Path (Join-Path $root '.git')){$score+=10;$evidence+='git-baseline'}
$threshold=70
$status=if($score -ge $threshold){'ENOUGH'}else{'ESCALATE'}
Write-Host ('CONTEXT_QUALITY='+$status)
Write-Host ('CONTEXT_SCORE='+$score)
Write-Host ('EVIDENCE='+($evidence -join ','))
if($status -eq 'ENOUGH'){
  Write-Host 'NEXT=edit/validate; avoid extra broad reads'
  exit 0
}
Write-Host 'NEXT=escalate exactly one level via smart-context/CALM/Redcon'
exit 2
