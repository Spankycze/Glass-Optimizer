param(
  [ValidateSet('resume','start-task','checkpoint','complete-task','fail-task','list-skills')]
  [string]$Action='resume',
  [string]$Title='',
  [string]$Area='general',
  [string]$TaskId='',
  [string[]]$DependsOn=@(),
  [string[]]$FilesTouched=@(),
  [string[]]$ValidationRequired=@(),
  [string]$Phase='',
  [string]$ErrorKey='',
  [string]$Fix='',
  [string]$RootCause='',
  [string]$Summary='',
  [string]$NextAction='',
  [string[]]$Done=@(),
  [string[]]$Pending=@(),
  [string[]]$Evidence=@(),
  [switch]$RefreshIndex
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$ai=Join-Path $root '.ai-state'
$taskPath=Join-Path $ai 'TASK_STATE.json'
$cpPath=Join-Path $ai 'CHECKPOINT.json'
$errorPath=Join-Path $ai 'errors.json'
$skillsDir=Join-Path $root 'skills'
New-Item -ItemType Directory -Force -Path $ai,$skillsDir|Out-Null

function Read-Json($path,$fallback){
  if(Test-Path $path){try{return Get-Content $path -Raw -Encoding UTF8|ConvertFrom-Json}catch{}}
  return $fallback
}
function Write-Json($path,$obj){
  $tmp=$path+'.tmp'
  [IO.File]::WriteAllText($tmp,($obj|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
  Move-Item $tmp $path -Force
}
function New-TaskDb {
  [pscustomobject][ordered]@{schema=1;updatedAt=(Get-Date).ToString('o');currentTask=$null;tasks=@()}
}
function New-CheckpointDb {
  [pscustomobject][ordered]@{schema=1;updatedAt=(Get-Date).ToString('o');lastKnownGood=$null;history=@()}
}
function Get-SkillForArea([string]$area){
  switch -Regex ($area.ToLowerInvariant()){
    'network|internet|dns|mtu|nic' {'network-debug.md';break}
    'xaml|ui|winui|performance' {'winui-performance.md';break}
    'release|build|installer' {'release-build.md';break}
    'power|powerplan' {'power-plan-validation.md';break}
    'regression|qa|test' {'xaml-regression.md';break}
    default {'general-debug.md'}
  }
}
function Get-Version {
  $props=Join-Path $root 'Directory.Build.props'
  if(Test-Path $props){$m=[regex]::Match([IO.File]::ReadAllText($props),'<SpankyVersion>([^<]+)</SpankyVersion>');if($m.Success){return $m.Groups[1].Value}}
  return 'unknown'
}
function Get-BuildStatus {
  $status=Join-Path $ai 'status.json'
  if(-not(Test-Path $status)){return $null}
  try{return Get-Content $status -Raw|ConvertFrom-Json}catch{return $null}
}

$tasks=Read-Json $taskPath (New-TaskDb)
$cp=Read-Json $cpPath (New-CheckpointDb)

switch($Action){
  'start-task' {
    if([string]::IsNullOrWhiteSpace($TaskId)){$TaskId=('task-'+(Get-Date -Format 'yyyyMMddHHmmss'))}
    if([string]::IsNullOrWhiteSpace($Title)){$Title=$TaskId}
    $skill=Get-SkillForArea $Area
    $blocked=@()
    foreach($dep in @($DependsOn)){
      $d=@($tasks.tasks|Where-Object id -eq $dep)|Select-Object -First 1
      if($null -eq $d -or $d.status -ne 'done'){$blocked+=$dep}
    }
    $task=[pscustomobject][ordered]@{
      id=$TaskId;title=$Title;area=$Area;skill=$skill;status=$(if($blocked.Count){'blocked'}else{'in-progress'})
      createdAt=(Get-Date).ToString('o');updatedAt=(Get-Date).ToString('o')
      dependsOn=@($DependsOn);blockedBy=@($blocked);subtasks=@();filesTouched=@();validationRequired=@($ValidationRequired)
      retryCount=0;lastError=$null
    }
    $list=@($tasks.tasks|Where-Object{$_.id -ne $TaskId})+$task
    $tasks.tasks=$list;$tasks.currentTask=$TaskId;$tasks.updatedAt=(Get-Date).ToString('o')
    Write-Json $taskPath $tasks
    Write-Output ('TASK_STARTED id='+$TaskId+' skill='+$skill)
  }
  'checkpoint' {
    if([string]::IsNullOrWhiteSpace($TaskId)){$TaskId=[string]$tasks.currentTask}
    $status=Get-BuildStatus
    $cpId=('cp-'+(Get-Date -Format 'yyyyMMddHHmmssfff'))
    $cpDir=Join-Path $ai ('checkpoints\'+$cpId)
    $snapshots=@()
    foreach($rel in @($FilesTouched)){
      if([string]::IsNullOrWhiteSpace($rel)){continue}
      $full=if([IO.Path]::IsPathRooted($rel)){$rel}else{Join-Path $root $rel}
      if(Test-Path $full -PathType Leaf){
        $safeRel=if($full.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){$full.Substring($root.Length).TrimStart('\')}else{Split-Path $full -Leaf}
        $backup=Join-Path $cpDir $safeRel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup)|Out-Null
        Copy-Item -LiteralPath $full -Destination $backup -Force
        $snapshots+=[pscustomobject][ordered]@{
          file=$rel;exists=$true;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash
          size=(Get-Item $full).Length;backup=$backup
        }
      } else {
        $snapshots+=[pscustomobject][ordered]@{file=$rel;exists=$false;sha256=$null;size=0;backup=$null}
      }
    }
    $entry=[pscustomobject][ordered]@{
      id=$cpId;taskId=$TaskId;phase=$Phase
      timestamp=(Get-Date).ToString('o');version=Get-Version
      filesTouched=@($FilesTouched);validationRequired=@($ValidationRequired)
      buildStatus=$status;errorKey=$ErrorKey;rootCause=$RootCause;fix=$Fix
      rollback=[ordered]@{
        lastKnownGoodVersion=if($cp.lastKnownGood){$cp.lastKnownGood.version}else{$null}
        backupDir=if($snapshots.Count){$cpDir}else{$null}
        snapshots=@($snapshots)
      }
    }
    $cp.history=@($cp.history)+$entry
    if($status -and $status.entries.'build-test'.outcome -eq 'OK' -and $status.entries.'build-test'.version -eq (Get-Version)){$cp.lastKnownGood=$entry}
    $cp.updatedAt=(Get-Date).ToString('o');Write-Json $cpPath $cp
    if(-not [string]::IsNullOrWhiteSpace($ErrorKey) -and (Test-Path $errorPath)){
      try{
        $edb=Get-Content $errorPath -Raw -Encoding UTF8|ConvertFrom-Json
        foreach($e in @($edb.errors)){
          if($e.key -eq $ErrorKey -or $e.message -like ('*'+$ErrorKey+'*')){
            if($null -eq $e.PSObject.Properties['rootCause']){$e|Add-Member -NotePropertyName 'rootCause' -NotePropertyValue $null}
            if($null -eq $e.PSObject.Properties['fixThatWorked']){$e|Add-Member -NotePropertyName 'fixThatWorked' -NotePropertyValue $null}
            if($null -eq $e.PSObject.Properties['filesInvolved']){$e|Add-Member -NotePropertyName 'filesInvolved' -NotePropertyValue @()}
            if(-not [string]::IsNullOrWhiteSpace($RootCause)){$e.rootCause=$RootCause}
            if(-not [string]::IsNullOrWhiteSpace($Fix)){$e.fixThatWorked=$Fix}
            if($FilesTouched.Count){$e.filesInvolved=@($e.filesInvolved+$FilesTouched|Select-Object -Unique)}
          }
        }
        Write-Json $errorPath $edb
      }catch{}
    }
    if($TaskId){
      foreach($t in @($tasks.tasks)){if($t.id -eq $TaskId){$t.updatedAt=(Get-Date).ToString('o');$t.filesTouched=@($t.filesTouched+$FilesTouched|Select-Object -Unique);$t.validationRequired=@($t.validationRequired+$ValidationRequired|Select-Object -Unique)}}
      $tasks.updatedAt=(Get-Date).ToString('o');Write-Json $taskPath $tasks
    }
    Write-Output ('CHECKPOINT_OK task='+$TaskId+' phase='+$Phase)
  }
  'complete-task' {
    if([string]::IsNullOrWhiteSpace($TaskId)){$TaskId=[string]$tasks.currentTask}
    foreach($t in @($tasks.tasks)){if($t.id -eq $TaskId){$t.status='done';$t.updatedAt=(Get-Date).ToString('o')}}
    foreach($t in @($tasks.tasks|Where-Object status -eq 'blocked')){
      $remaining=@()
      foreach($dep in @($t.dependsOn)){
        $d=@($tasks.tasks|Where-Object id -eq $dep)|Select-Object -First 1
        if($null -eq $d -or $d.status -ne 'done'){$remaining+=$dep}
      }
      $t.blockedBy=@($remaining)
      if($remaining.Count -eq 0){$t.status='in-progress';$t.updatedAt=(Get-Date).ToString('o')}
    }
    if($tasks.currentTask -eq $TaskId){$tasks.currentTask=$null}
    $tasks.updatedAt=(Get-Date).ToString('o');Write-Json $taskPath $tasks
    Write-Output ('TASK_DONE '+$TaskId)
  }
  'fail-task' {
    if([string]::IsNullOrWhiteSpace($TaskId)){$TaskId=[string]$tasks.currentTask}
    foreach($t in @($tasks.tasks)){if($t.id -eq $TaskId){$t.status='failed';$t.retryCount=[int]$t.retryCount+1;$t.lastError=$ErrorKey;$t.updatedAt=(Get-Date).ToString('o')}}
    $tasks.updatedAt=(Get-Date).ToString('o');Write-Json $taskPath $tasks
    Write-Output ('TASK_FAILED '+$TaskId+' retry='+(@($tasks.tasks|Where-Object id -eq $TaskId)[0].retryCount))
  }
  'list-skills' {
    Get-ChildItem $skillsDir -File -Filter '*.md'|Sort-Object Name|ForEach-Object{Write-Output ('SKILL '+$_.Name)}
  }
  'resume' {
    if($RefreshIndex -or -not(Test-Path (Join-Path $root 'PROJECT_INDEX.json'))){
      & (Join-Path $root 'project-index.ps1') | Out-Null
    }
    $current=@($tasks.tasks|Where-Object id -eq $tasks.currentTask)|Select-Object -First 1
    if($current){
      Write-Output ('AI_TASK '+$current.id+' | '+$current.status+' | '+$current.title)
      Write-Output ('AI_SKILL '+$current.skill)
      if(@($current.dependsOn).Count){Write-Output ('AI_DEPENDS '+(@($current.dependsOn)-join ','))}
      $memoryScript=Join-Path $root 'memory-store.ps1'
      if(Test-Path $memoryScript){
        try{
          @(& $memoryScript -Action search -Query ($current.area+' '+$current.title)) |
            Where-Object {$_ -like 'MEMORY_HIT*'} | Select-Object -First 3 |
            ForEach-Object {Write-Output ('AI_'+$_)}
        }catch{}
      }
    } else { Write-Output 'AI_TASK none' }
    if($cp.lastKnownGood){
      Write-Output ('AI_CHECKPOINT '+$cp.lastKnownGood.id+' | '+$cp.lastKnownGood.phase+' | v'+$cp.lastKnownGood.version)
    } else { Write-Output 'AI_CHECKPOINT none' }
    $open=@($tasks.tasks|Where-Object status -in @('in-progress','failed','blocked'))
    $next=@($open|Where-Object {$_.status -ne 'blocked'}|Sort-Object createdAt|Select-Object -First 1)
    if($next.Count){Write-Output ('AI_NEXT '+$next[0].id+' | '+$next[0].title+' | skill='+$next[0].skill)}
    else{Write-Output 'AI_NEXT none'}
    Write-Output ('AI_TASKS_OPEN '+$open.Count)
  }
}

# Continuity is updated on every workflow transition, without stale build promotion.
$continuity=Join-Path $root 'resume-state.ps1'
if(Test-Path $continuity){
 if($Action -eq 'resume'){& $continuity -Action show}
 elseif($Action -in @('start-task','checkpoint','complete-task','fail-task')){
  if($Summary -and $NextAction){& $continuity -Action save -Summary $Summary -NextAction $NextAction -Done $Done -Pending $Pending -FilesTouched $FilesTouched -Evidence $Evidence}
  else{& $continuity -Action refresh}
 }
}