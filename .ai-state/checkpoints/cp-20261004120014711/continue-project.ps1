param(
  [ValidateSet('Quiet','Normal','Verbose')]
  [string]$Mode='Quiet',
  [switch]$RefreshIndex,
  [switch]$RefreshState
)
$ErrorActionPreference='SilentlyContinue'
$ProgressPreference='SilentlyContinue'
$root=$PSScriptRoot
$lightPath=Join-Path $root '.ai-state\state-light.json'
$statePath=Join-Path $root 'PROJECT_STATE.md'
$props=Join-Path $root 'Directory.Build.props'
$sw=[Diagnostics.Stopwatch]::StartNew()
& (Join-Path $root 'resume-state.ps1') -Action refresh

if($RefreshState -or -not(Test-Path $lightPath)){
  & (Join-Path $root 'update-project-state.ps1') -Reason 'continue-project' -Light | Out-Null
}
if($RefreshIndex){ & (Join-Path $root 'project-index.ps1') | Out-Null }

$s=$null
if(Test-Path $lightPath){
  try{$s=[IO.File]::ReadAllText($lightPath,[Text.Encoding]::UTF8)|ConvertFrom-Json}catch{}
}
$version=if($s -and $s.version){[string]$s.version}else{'unknown'}
if($version -eq 'unknown' -and (Test-Path $props)){
  $m=[regex]::Match([IO.File]::ReadAllText($props),'<SpankyVersion>([^<]+)</SpankyVersion>')
  if($m.Success){$version=$m.Groups[1].Value}
}
function Compact($v){
  if($null -eq $v){return 'n/a'}
  return ([string]$v.outcome+'/'+[string]$v.mode+'/'+[math]::Round([double]$v.durationMs,0)+'ms')
}
$fast=Compact $s.buildFast
$test=Compact $s.buildTest
$release=Compact $s.buildRelease
$errors=if($s){[int]$s.openErrors}else{0}
$task=if($s -and $s.task){[string]$s.task}else{'none'}
$checkpoint=if($s -and $s.checkpoint){[string]$s.checkpoint}else{'none'}
$sw.Stop()

if($Mode -eq 'Quiet'){
  Write-Output ('[STATE] OK '+[math]::Round($sw.Elapsed.TotalMilliseconds,1)+' ms | v'+$version)
  Write-Output ('[HISTORICAL_BUILD_STATUS] fast='+$fast+' test='+$test+' release='+$release)
  Write-Output ('[ERRORS] open='+$errors)
  Write-Output ('[AI] task='+$task+' checkpoint='+$checkpoint)
  Write-Output '[OK] Resume from RESUME.md first, then PROJECT_STATE.md; never use historical logs as current evidence.'
  return
}
Write-Output ('[STATE] OK '+[math]::Round($sw.Elapsed.TotalMilliseconds,1)+' ms | version='+$version)
Write-Output ('[HISTORICAL_BUILD_STATUS] fast='+$fast)
Write-Output ('[TEST] '+$test)
Write-Output ('[RELEASE] '+$release)
Write-Output ('[ERRORS] open='+$errors)
Write-Output ('[AI] task='+$task+' checkpoint='+$checkpoint)
if($Mode -eq 'Verbose' -and (Test-Path $statePath)){
  Write-Output '[STATE_FILE]'
  Get-Content $statePath -Encoding UTF8 | Select-Object -First 30
}
