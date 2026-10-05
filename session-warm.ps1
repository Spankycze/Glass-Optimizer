param(
  [int]$TopFiles=8,
  [int]$MaxStateLines=60,
  [int]$MaxStateChars=12000
)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$root=$PSScriptRoot
$dir=Join-Path $root '.session-warm'
New-Item -ItemType Directory -Force $dir | Out-Null
& (Join-Path $root 'compact-state.ps1') | Out-Null

$changed=@()
if(Test-Path (Join-Path $root '.git')){
  $changed=@(git -c core.autocrlf=false -C $root status --short | ForEach-Object {
    if($_.Length -gt 3){$_.Substring(3).Trim()}
  } | Where-Object {$_})
}
$stateLines=@(Get-Content -LiteralPath (Join-Path $root 'CURRENT_STATE.md') -TotalCount $MaxStateLines)
$state=($stateLines -join [Environment]::NewLine)
if($state.Length -gt $MaxStateChars){$state=$state.Substring(0,$MaxStateChars)}
$head=if(Test-Path (Join-Path $root '.git')){(git -C $root rev-parse --short HEAD).Trim()}else{'nogit'}

$out=[ordered]@{
  head=$head
  changed_count=$changed.Count
  changed=@($changed | Select-Object -First $TopFiles)
  current_state_excerpt=$state
}
$path=Join-Path $dir 'warm.json'
$out | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 $path
$bytes=(Get-Item $path).Length
Write-Host ('SESSION_WARM_OK head='+$head+' changed='+$changed.Count+' bytes='+$bytes)
if($bytes -gt 20000){ throw 'Warm cache exceeded 20 KB safety cap.' }
