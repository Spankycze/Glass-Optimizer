$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$root=$PSScriptRoot
& (Join-Path $root 'compact-state.ps1') | Out-Null
Write-Host '=== CURRENT STATE ==='
Get-Content -LiteralPath (Join-Path $root 'CURRENT_STATE.md') -TotalCount 45
if(Test-Path (Join-Path $root '.git')){
  Write-Host '=== GIT ==='
  $changed=@(git -c core.autocrlf=false -C $root status --short)
  Write-Host ('dirty='+$changed.Count)
  $changed | Select-Object -First 20
  Write-Host ('last_commit='+(git -c core.autocrlf=false -C $root log -1 --pretty=format:'%h %s'))
}
if(Test-Path (Join-Path $root 'TASK_STATE.json')){
  Write-Host '=== TASK ==='
  Get-Content -Raw (Join-Path $root 'TASK_STATE.json') | Select-Object -First 1
}