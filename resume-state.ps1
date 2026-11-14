param(
 [ValidateSet('refresh','save','show')][string]$Action='show',
 [string]$Root=$PSScriptRoot,[string]$Summary='',[string]$NextAction='',
 [string[]]$Done=@(),[string[]]$Pending=@(),[string[]]$FilesTouched=@(),[string[]]$Evidence=@()
)
$ErrorActionPreference='Stop'
$ai=Join-Path $Root '.ai-state'
New-Item $ai -ItemType Directory -Force|Out-Null
$path=Join-Path $ai 'RESUME_STATE.json'
function ReadJson($file){if(Test-Path $file){return ConvertFrom-Json ([IO.File]::ReadAllText($file))};return $null}
function WriteAtomic($file,$text){
 $temp=$file+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
 [IO.File]::WriteAllText($temp,$text,[Text.UTF8Encoding]::new($false))
 Move-Item $temp $file -Force
}
$state=ReadJson $path
if(!$state){$state=[pscustomobject][ordered]@{schema=1;updatedUtc=$null;sourceVersion='unknown';installedVersion='unknown';currentTask=$null;taskStatus='none';summary='';nextAction='';done=@();pending=@();filesTouched=@();evidence=@();latestRelease=$null;warnings=@()}}
$oldTask=$state.currentTask
$tasks=ReadJson (Join-Path $ai 'TASK_STATE.json')
$current=$null
if($tasks -and $tasks.currentTask){$current=@($tasks.tasks|Where-Object id -eq $tasks.currentTask)|Select-Object -First 1}
$state.currentTask=if($current){$current.id}else{$null}
$state.taskStatus=if($current){$current.status}else{'none'}
if($oldTask -ne $state.currentTask){
 $state.summary=if($current){$current.title}else{$state.summary}
 $state.nextAction=if($current){'Record the exact next action before editing.'}else{'No active task. Follow the latest user request; do not repeat completed work.'}
 $state.done=@();$state.pending=@();$state.filesTouched=@();$state.evidence=@()
}
$props=Join-Path $Root 'Directory.Build.props'
if(Test-Path $props){$m=[regex]::Match([IO.File]::ReadAllText($props),'<SpankyVersion>([^<]+)</SpankyVersion>');if($m.Success){$state.sourceVersion=$m.Groups[1].Value}}
$state.warnings=@()
$core=Join-Path 'C:\Program Files\Spankyho Optimizer' 'SpankyhoOptimizer.Core.dll'
# Never carry a previous installed version forward if the actual binary disappears.
$state.installedVersion='unknown'
if(Test-Path $core){$state.installedVersion=(Get-Item $core).VersionInfo.ProductVersion}
$reports=@(Get-ChildItem (Join-Path $Root 'artifacts') -Filter 'release-result.json' -Recurse -File -ErrorAction SilentlyContinue)
$valid=@()
foreach($report in $reports){
 $r=ReadJson $report.FullName
 $ver=$null
 if($r -and [Version]::TryParse([string]$r.version,[ref]$ver)){
  $deployment=Join-Path $report.DirectoryName 'deployment-result.json'
  $valid+=[pscustomobject]@{version=$ver;report=$report;release=$r;deployment=$deployment}
 }
}
$latest=$valid|Sort-Object version -Descending|Select-Object -First 1
if($latest){
 $d=ReadJson $latest.deployment
 $state.latestRelease=[ordered]@{version=$latest.version.ToString();releaseReport=$latest.report.FullName;deploymentReport=$latest.deployment;installer=$latest.release.desktopInstaller;sha256=$latest.release.sha256;payloadVerified=($d -and $d.payloadVerified);installedVersion=if($d){$d.installedVersion}else{$null};validationReport=(Join-Path $latest.report.DirectoryName 'validation-result.json');releaseLog=(Join-Path $latest.report.DirectoryName 'release.log')}
 if(!$d -or !$d.payloadVerified){$state.warnings+='Latest release has no verified deployment report.'}
}
$releaseCheckpoint=$null
$historyPath=Join-Path $ai 'build-checkpoint-history.jsonl'
if(Test-Path $historyPath){
 foreach($line in [IO.File]::ReadLines($historyPath)){
  if([string]::IsNullOrWhiteSpace($line)){continue}
  try{
   $candidate=$line|ConvertFrom-Json
   if($candidate.kind -eq 'release' -and $candidate.outcome -eq 'OK'){
    $releaseCheckpoint=$candidate
   }
  }catch{}
 }
}
if(!$releaseCheckpoint){
 $candidate=ReadJson (Join-Path $ai 'BUILD_CHECKPOINT.json')
 if($candidate -and $candidate.kind -eq 'release' -and $candidate.outcome -eq 'OK'){$releaseCheckpoint=$candidate}
}
if($releaseCheckpoint){
 $checkpointVersion=$null
 if([Version]::TryParse([string]$releaseCheckpoint.builtVersion,[ref]$checkpointVersion)){
  $knownVersion=$null
  if($state.latestRelease){[Version]::TryParse([string]$state.latestRelease.version,[ref]$knownVersion)|Out-Null}
  if(!$knownVersion -or $checkpointVersion -gt $knownVersion){
   $installer=[string]$releaseCheckpoint.evidence
   if($installer -and (Test-Path $installer)){
    $hash=(Get-FileHash $installer -Algorithm SHA256).Hash
    $state.latestRelease=[ordered]@{version=$checkpointVersion.ToString();releaseReport=$null;deploymentReport=$null;installer=$installer;sha256=$hash;payloadVerified=$true;installedVersion=$null;validationReport=$null;releaseLog=$null;source='successful-build-checkpoint-history'}
   }
  }
 }
}
if($state.latestRelease){
 # Reflect current disk read-back, not only the installation status at release build time.
 $actualInstalled=([string]$state.installedVersion -split '\+')[0]
 $released=[string]$state.latestRelease.version
 if($actualInstalled -ne 'unknown' -and $actualInstalled -eq $released){
  $state.latestRelease.installedVersion=$state.installedVersion
 } elseif($actualInstalled -eq 'unknown'){
  $state.warnings+='Installed version cannot be verified on disk; do not claim it from checkpoint or history.'
 } elseif($actualInstalled -ne $released){
  $state.warnings+=('Latest verified release '+$released+' differs from installed '+$actualInstalled+'; do not claim it is installed.')
 }
}
if($state.latestRelease -and $state.sourceVersion -ne $state.latestRelease.version){$state.warnings+='Source version differs from released version; an automatic next-version bump is not a new verified build.'}
$status=ReadJson (Join-Path $ai 'status.json')
# Old per-command debug/build statuses are historical caches, not version
# authority. Summarize their exclusion without repeating obsolete version
# numbers in the default resume/handoff shown to future assistants.
$ignoredLegacyStatuses=0
if($status -and $status.entries){
 foreach($entry in $status.entries.PSObject.Properties){
  if($state.latestRelease -and $entry.Value.version -ne $state.latestRelease.version){
   $ignoredLegacyStatuses++
  }
 }
}
if($ignoredLegacyStatuses -gt 0){
 $state.warnings+=('Ignored '+$ignoredLegacyStatuses+' historical build-status entries; verify active versions with get-live-project-state.ps1 -AsJson.')
}
if($Action -eq 'save'){
 if(!$Summary -or !$NextAction){throw 'save requires Summary and NextAction'}
 $state.summary=$Summary;$state.nextAction=$NextAction
 $state.done=@($Done);$state.pending=@($Pending);$state.filesTouched=@($FilesTouched);$state.evidence=@($Evidence)
}
foreach($item in @($state.evidence)){
 $full=if([IO.Path]::IsPathRooted($item)){$item}else{Join-Path $Root $item}
 if(!(Test-Path $full)){$state.warnings+=('Evidence missing: '+$item)}
}
if($Action -ne 'show'){
 $state.updatedUtc=(Get-Date).ToUniversalTime().ToString('O')
 [IO.File]::AppendAllText((Join-Path $ai 'resume-history.jsonl'),($state|ConvertTo-Json -Depth 10 -Compress)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
 WriteAtomic $path ($state|ConvertTo-Json -Depth 10)
 $lines=@('# RESUME - current work only','',('Updated UTC: '+$state.updatedUtc),('Source: '+$state.sourceVersion),('Installed: '+$state.installedVersion),('Task: '+$state.currentTask+' | '+$state.taskStatus),'','## Where work stopped',$state.summary,'','## Exact next action',$state.nextAction,'','## Completed')
 $lines+=@($state.done|ForEach-Object{'- '+$_})
 $lines+=@('','## Pending');$lines+=@($state.pending|ForEach-Object{'- '+$_})
 $lines+=@('','## Files');$lines+=@($state.filesTouched|ForEach-Object{'- '+$_})
 $lines+=@('','## Evidence');$lines+=@($state.evidence|ForEach-Object{'- '+$_})
 if($state.latestRelease){$lines+=('- Verified release: '+($state.latestRelease|ConvertTo-Json -Compress))}
 $lines+=@('','## Freshness warnings');$lines+=@($state.warnings|ForEach-Object{'- '+$_})
 $lines+=@('','History: .ai-state/resume-history.jsonl and SESSION_HISTORY.md. Read only when needed.','Use ai-workflow checkpoint with Summary, NextAction, Done, Pending and Evidence after each meaningful batch.')
 WriteAtomic (Join-Path $Root 'RESUME.md') ($lines -join [Environment]::NewLine)
}
Write-Output ('RESUME_TASK '+$state.currentTask+' | '+$state.taskStatus)
Write-Output ('RESUME_VERSION source='+$state.sourceVersion+' installed='+$state.installedVersion)
Write-Output ('RESUME_STOP '+$state.summary)
Write-Output ('RESUME_NEXT '+$state.nextAction)
foreach($warning in $state.warnings){Write-Output ('RESUME_WARNING '+$warning)}
