param(
  [Parameter(Mandatory=$true)][string]$Kind,
  [string]$Version='',
  [string]$Outcome='OK',
  [string]$Evidence=''
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$ai=Join-Path $root '.ai-state'
New-Item -ItemType Directory -Force -Path $ai | Out-Null

function Read-JsonSafe([string]$Path){
  if(Test-Path $Path){
    try { return [IO.File]::ReadAllText($Path,[Text.Encoding]::UTF8) | ConvertFrom-Json } catch {}
  }
  return $null
}
function Write-Atomic([string]$Path,[string]$Text){
  $tmp=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
  [IO.File]::WriteAllText($tmp,$Text,[Text.UTF8Encoding]::new($false))
  Move-Item $tmp $Path -Force
}
function Get-SourceVersion {
  $props=Join-Path $root 'Directory.Build.props'
  if(Test-Path $props){
    $m=[regex]::Match([IO.File]::ReadAllText($props,[Text.Encoding]::UTF8),'<SpankyVersion>([^<]+)</SpankyVersion>')
    if($m.Success){ return $m.Groups[1].Value }
  }
  return 'unknown'
}

$sourceVersion=Get-SourceVersion
$builtVersion=if([string]::IsNullOrWhiteSpace($Version)){$sourceVersion}else{$Version}
$now=[DateTime]::UtcNow.ToString('O')
$taskDb=Read-JsonSafe (Join-Path $ai 'TASK_STATE.json')
$currentTask=$null
$taskStatus='none'
$taskTitle=''
if($taskDb -and $taskDb.currentTask){
  $currentTask=[string]$taskDb.currentTask
  $task=@($taskDb.tasks | Where-Object id -eq $taskDb.currentTask) | Select-Object -First 1
  if($task){$taskStatus=[string]$task.status;$taskTitle=[string]$task.title}
}

$checkpoint=[ordered]@{
  schema=1
  updatedUtc=$now
  kind=$Kind
  outcome=$Outcome
  builtVersion=$builtVersion
  sourceVersion=$sourceVersion
  taskId=$currentTask
  taskStatus=$taskStatus
  taskTitle=$taskTitle
  evidence=$Evidence
}
$checkpointPath=Join-Path $ai 'BUILD_CHECKPOINT.json'
# A successful quick/Debug build is NOT a new release. Keep the most recent
# verified release identity separate from the latest build checkpoint.
if($Kind -eq 'release' -and $Outcome -eq 'OK'){
  Write-Atomic (Join-Path $ai 'LAST_RELEASE_CHECKPOINT.json') ($checkpoint | ConvertTo-Json -Depth 8)
}
Write-Atomic $checkpointPath ($checkpoint | ConvertTo-Json -Depth 8)
[IO.File]::AppendAllText(
  (Join-Path $ai 'build-checkpoint-history.jsonl'),
  (($checkpoint | ConvertTo-Json -Depth 8 -Compress)+[Environment]::NewLine),
  [Text.UTF8Encoding]::new($false))

# Keep continuity files synchronized with the source/version/task state.
$resumeScript=Join-Path $root 'resume-state.ps1'
if(Test-Path $resumeScript){
  if($currentTask){
    & $resumeScript -Action refresh | Out-Null
  } else {
    $autoSummary=('Latest successful '+$Kind+' checkpoint for v'+$builtVersion+'.')
    $autoNext=('Continue from current source v'+$sourceVersion+' and the latest user request.')
    $autoEvidence=@('.ai-state\BUILD_CHECKPOINT.json')
    if($Evidence){$autoEvidence+=$Evidence}
    & $resumeScript -Action save -Summary $autoSummary -NextAction $autoNext -Done @('Build checkpoint updated automatically.') -Pending @() -FilesTouched @() -Evidence $autoEvidence | Out-Null
  }
}

$resume=Read-JsonSafe (Join-Path $ai 'RESUME_STATE.json')
$summary=if($resume -and $resume.summary){[string]$resume.summary}elseif($taskTitle){$taskTitle}else{'No active task.'}
$nextAction=if($resume -and $resume.nextAction){[string]$resume.nextAction}else{'Continue from the latest successful build checkpoint.'}
$installed=if($resume -and $resume.installedVersion){[string]$resume.installedVersion}else{'unknown'}
$latestRelease=if($resume -and $resume.latestRelease){$resume.latestRelease}else{$null}

$lines=@(
  '# CURRENT_STATE - BUILD-TIME SNAPSHOT (NOT LIVE INSTALL STATUS)',
  '',
  ('Updated UTC: '+$now),
  ('Build kind: '+$Kind),
  ('Build outcome: '+$Outcome),
  ('Built version: '+$builtVersion),
  ('Current source version: '+$sourceVersion),
  ('Installed version observed at build checkpoint (may be stale after later installation): '+$installed),
  ('Task: '+$currentTask+' | '+$taskStatus),
  '',
  '## Current work',
  $summary,
  '',
  '## Exact next action',
  $nextAction,
  '',
  '## Build evidence',
  ('- BUILD_CHECKPOINT: '+$checkpointPath)
)
if($Evidence){$lines+=('- '+$Evidence)}
if($latestRelease){$lines+=('- Latest verified release: '+($latestRelease | ConvertTo-Json -Compress))}
$lines+=@(
  '',
  '## Continuity',
  '- RESUME.md and .ai-state/RESUME_STATE.json refreshed automatically.',
  '- .ai-state/build-checkpoint-history.jsonl contains the full build checkpoint history.',
  '- This file is a BUILD-TIME snapshot, not a live installation report. Before claiming the current version, run get-live-project-state.ps1 -AsJson on Spankyvole.',
  '- Never use historical Library/chat summaries or old C: project paths to determine active versions.'
)
Write-Atomic (Join-Path $root 'CURRENT_STATE.md') ($lines -join [Environment]::NewLine)
Write-Output ('BUILD_CHECKPOINT_OK kind='+$Kind+' built='+$builtVersion+' source='+$sourceVersion)
