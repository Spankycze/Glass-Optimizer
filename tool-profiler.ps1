param([string]$Task='inspect current project state')
$ErrorActionPreference='Continue'
$PSNativeCommandUseErrorActionPreference=$false
$root=$PSScriptRoot
$outDir=Join-Path $root '.tool-profile'
New-Item -ItemType Directory -Force $outDir | Out-Null
$rows=@()
function Measure-Tool([string]$Name,[scriptblock]$Run,[switch]$CheckExit){
  $global:LASTEXITCODE=0
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $ok=$true
  try{
    & $Run *> $null
    if($CheckExit -and $LASTEXITCODE -ne 0){$ok=$false}
  }catch{$ok=$false}
  $sw.Stop()
  $script:rows += [pscustomobject]@{tool=$Name;ms=[int]$sw.Elapsed.TotalMilliseconds;ok=$ok}
}
Measure-Tool 'git-diff' { git -c core.autocrlf=false -C $root diff --name-only HEAD } -CheckExit
Measure-Tool 'redcon-plan' { & 'C:\Users\spnka\AppData\Roaming\Python\Python314\Scripts\redcon.exe' plan $Task --repo $root } -CheckExit
Measure-Tool 'compact-state' { & (Join-Path $root 'compact-state.ps1') }
Measure-Tool 'calm-doctor' { & 'C:\Users\spnka\AppData\Roaming\npm\calm-mcp.cmd' doctor --project-root $root } -CheckExit
$rows | Sort-Object ms | Format-Table -AutoSize
$rows | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $outDir 'latest.json')
