param([string]$Reason='scheduled',[switch]$Light)

$ErrorActionPreference='SilentlyContinue'
$root='C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer'
$out=Join-Path $root 'PROJECT_STATE.md'
$props=Join-Path $root 'Directory.Build.props'
$work=Join-Path $root 'WORK_PROGRESS.md'
$handoff=Join-Path $root 'SESSION_HANDOFF.md'
$debugExe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$aiDir=Join-Path $root '.ai-state'
$statusPath=Join-Path $aiDir 'status.json'
$errorsPath=Join-Path $aiDir 'errors.json'
$taskPath=Join-Path $aiDir 'TASK_STATE.json'
$checkpointPath=Join-Path $aiDir 'CHECKPOINT.json'
$projectIndexPath=Join-Path $root 'PROJECT_INDEX.json'

$version='unknown'
if(Test-Path $props){
    $m=[regex]::Match([IO.File]::ReadAllText($props),'<SpankyVersion>([^<]+)</SpankyVersion>')
    if($m.Success){$version=$m.Groups[1].Value}
}

if($Light){
    $lightPath=Join-Path $aiDir 'state-light.json'
    $status=$null
    if(Test-Path $statusPath){try{$status=[IO.File]::ReadAllText($statusPath)|ConvertFrom-Json}catch{}}
    $openPath=Join-Path $aiDir 'errors-open.json'
    $openCount=0
    if(Test-Path $openPath){try{$odb=[IO.File]::ReadAllText($openPath)|ConvertFrom-Json;$openCount=@($odb.errors).Count}catch{}}
    $task='none';$checkpoint='none'
    if(Test-Path $taskPath){
        try{$tdb=[IO.File]::ReadAllText($taskPath)|ConvertFrom-Json;if($tdb.currentTask){$task=[string]$tdb.currentTask}}catch{}
    }
    if(Test-Path $checkpointPath){
        try{$cdb=[IO.File]::ReadAllText($checkpointPath)|ConvertFrom-Json;if($cdb.lastKnownGood){$checkpoint=[string]$cdb.lastKnownGood.id}}catch{}
    }
    function Get-LightStatus([string]$kind){
        if($null -eq $status){return $null}
        $p=$status.entries.PSObject.Properties[$kind]
        if($null -eq $p){return $null}
        $e=$p.Value
        return [ordered]@{outcome=$e.outcome;mode=$e.mode;exitCode=$e.exitCode;durationMs=$e.durationMs;finishedAt=$e.finishedAt}
    }
    $lightState=[ordered]@{
        schema=1;version=$version;reason=$Reason
        buildFast=$(Get-LightStatus 'build-fast');buildTest=$(Get-LightStatus 'build-test');buildRelease=$(Get-LightStatus 'build-release')
        openErrors=$openCount;task=$task;checkpoint=$checkpoint
    }
    $json=$lightState|ConvertTo-Json -Depth 5 -Compress
    $same=$false
    if(Test-Path $lightPath){try{$same=[IO.File]::ReadAllText($lightPath) -eq $json}catch{}}
    if(-not $same){
        $tmp=$lightPath+'.tmp'
        [IO.File]::WriteAllText($tmp,$json,[Text.UTF8Encoding]::new($false))
        Move-Item $tmp $lightPath -Force
    }
    Write-Output ('[STATE] light v'+$version+' errors='+$openCount)
    return
}

$latestSource=[datetime]::MinValue
$recentText='- not refreshed in light mode'
$previous=''
if(Test-Path $out){$previous=Get-Content $out -Raw -ErrorAction SilentlyContinue}

if($Light -and $previous){
    $m=[regex]::Match($previous,'(?m)^Latest source modification:\s*(.+)$')
    if($m.Success){[datetime]::TryParse($m.Groups[1].Value,[ref]$latestSource)|Out-Null}
    $m2=[regex]::Match($previous,'(?s)## Recent source changes\r?\n(.*?)\r?\n\r?\n## Last DC command')
    if($m2.Success){$recentText=$m2.Groups[1].Value.TrimEnd()}
}else{
    $sourceFiles=@(
        Get-ChildItem (Join-Path $root 'src') -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {$_.FullName -notmatch '\\(bin|obj)\\'} |
        Sort-Object LastWriteTime -Descending
    )
    $latestSource=if($sourceFiles.Count){$sourceFiles[0].LastWriteTime}else{[datetime]::MinValue}
    $recentText=(@(
        $sourceFiles | Select-Object -First 12 |
        ForEach-Object {'- '+$_.FullName.Substring($root.Length+1)+' | '+$_.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')}
    ) -join [Environment]::NewLine)
}

$latestReleaseVersion=$null
$latestReleaseArtifact=Get-ChildItem (Join-Path $root 'artifacts\release') -File -Filter 'Spankyho_Optimizer_V*_Setup_Compact.exe' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if($latestReleaseArtifact -and $latestReleaseArtifact.Name -match 'V(\d+)_(\d+)_(\d+)_Setup_Compact'){
    $latestReleaseVersion=$matches[1]+'.'+$matches[2]+'.'+$matches[3]
}

function Format-FreshStatus([string]$Kind){
    if(-not(Test-Path $statusPath)){return 'not recorded yet'}
    try{
        $db=Get-Content $statusPath -Raw|ConvertFrom-Json
        $p=$db.entries.PSObject.Properties[$Kind]
        if($null -eq $p){return 'not recorded yet'}
        $e=$p.Value
        $when=[datetime]::Parse([string]$e.finishedAt).ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss')
        $recordedVersion=[string]$e.version
        $expectedVersion=$(if($Kind -eq 'build-release' -and $latestReleaseVersion){$latestReleaseVersion}else{$version})
        $freshness=$(if($recordedVersion -and $expectedVersion -and $recordedVersion -ne $expectedVersion){'STALE | '}else{''})
        return ($freshness+[string]$e.outcome+' | '+[string]$e.mode+' | exit='+[string]$e.exitCode+' | '+[string]$e.durationMs+' ms | '+$when+' | v'+$recordedVersion)
    }catch{return 'status read failed'}
}

$fastStatus=Format-FreshStatus 'build-fast'
$testStatus=Format-FreshStatus 'build-test'
$releaseStatus=Format-FreshStatus 'build-release'

$exeText='missing'
if(Test-Path $debugExe){
    $e=Get-Item $debugExe
    $exeText=$e.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')+' | '+[math]::Round($e.Length/1KB,1)+' KB'
}

$runnable='not checked (light)'
$structural='not checked (light)'
if(-not $Light){
    try{
        . (Join-Path $root 'build-state.ps1')
        $runnable=[string](Test-SpankyRunnableFresh)
        $structural=[string](Test-SpankyStructuralFresh)
    }catch{}
}

$desktop=[Environment]::GetFolderPath('Desktop')
$setup=Get-ChildItem $desktop -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^Spankyho_Optimizer_V\d+_\d+_\d+_Setup(?:_Compact)?\.exe$' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$setupText=if($setup){
    $setup.Name+' | '+[math]::Round($setup.Length/1MB,2)+' MB | '+$setup.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
}else{'none'}

$notesText='none'
if(Test-Path $work){
    $notes=@(Get-Content $work -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object {$_} | Select-Object -First 8)
    if($notes.Count){$notesText=$notes -join [Environment]::NewLine}
}

$handoffText=if(Test-Path $handoff){(Get-Item $handoff).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')+' | SESSION_HANDOFF.md'}else{'none'}
$dcLogs=@(Get-ChildItem (Join-Path $root '.dc-logs') -File -Filter 'dc_*.log' -ErrorAction SilentlyContinue)
$ccDb=Join-Path $root '.context-compress\store.db'
$ccText=if(Test-Path $ccDb){'DB ready | '+[math]::Round((Get-Item $ccDb).Length/1KB,1)+' KB'}elseif(Test-Path (Join-Path $root '.context-compress.json')){'configured; DB not materialized yet'}else{'missing'}

$lastDcText='none'
$lastDcPath=Join-Path $root '.dc-last.json'
if(Test-Path $lastDcPath){
    try{
        $lastDc=Get-Content $lastDcPath -Raw|ConvertFrom-Json
        $lastDcText=$lastDc.Timestamp+' | '+$lastDc.Kind+' | exit='+$lastDc.ExitCode+' | '+$lastDc.DurationMs+' ms | '+$lastDc.Command
    }catch{}
}

$errorSummary='none'
$errorExamples=@()
if(Test-Path $errorsPath){
    try{
        $edb=Get-Content $errorsPath -Raw|ConvertFrom-Json
        $open=@($edb.errors|Where-Object{-not [bool]$_.resolved})
        $resolved=@($edb.errors|Where-Object{[bool]$_.resolved})
        $errorSummary='open='+$open.Count+' | resolved='+$resolved.Count+' | indexed='+@($edb.errors).Count
        $errorExamples=@($open|Sort-Object lastSeen -Descending|Select-Object -First 5|ForEach-Object{'- '+$_.kind+' | '+$_.message})
    }catch{$errorSummary='read failed'}
}

$aiTask='none'
$aiSkill='none'
$aiCheckpoint='none'
$aiIndex='missing'
if(Test-Path $taskPath){
    try{
        $tdb=Get-Content $taskPath -Raw -Encoding UTF8|ConvertFrom-Json
        $cur=@($tdb.tasks|Where-Object id -eq $tdb.currentTask)|Select-Object -First 1
        if($cur){$aiTask=$cur.id+' | '+$cur.status+' | '+$cur.title;$aiSkill=[string]$cur.skill}
    }catch{}
}
if(Test-Path $checkpointPath){
    try{$cdb=Get-Content $checkpointPath -Raw -Encoding UTF8|ConvertFrom-Json;if($cdb.lastKnownGood){$aiCheckpoint=$cdb.lastKnownGood.id+' | '+$cdb.lastKnownGood.phase+' | v'+$cdb.lastKnownGood.version}}catch{}
}
if(Test-Path $projectIndexPath){
    try{$ix=Get-Content $projectIndexPath -Raw -Encoding UTF8|ConvertFrom-Json;$aiIndex='ready | files='+$ix.counts.files+' classes='+$ix.counts.classes+' methods='+$ix.counts.methods+' services='+$ix.counts.services}catch{$aiIndex='read failed'}
}

$lines=@()
$lines+='# PROJECT_STATE'
$lines+='Read RESUME.md first for current task, next action and verified release evidence.'
$lines+=''
$lines+='Generated: '+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
$lines+='Reason: '+$Reason+$(if($Light){' (light)'}else{''})
$lines+='Project: Spankyho Optimizer'
$lines+='Current source version: '+$version
$lines+='Latest source modification: '+$(if($latestSource -gt [datetime]::MinValue){$latestSource.ToString('yyyy-MM-dd HH:mm:ss')}else{'unknown'})
$lines+='Debug EXE: '+$exeText
$lines+=''
$lines+='## Fresh build/test status'
$lines+='- build-fast: '+$fastStatus
$lines+='- build-test: '+$testStatus
$lines+='- build-release: '+$releaseStatus
$lines+='- Runnable fingerprint fresh: '+$runnable
$lines+='- Structural fingerprint fresh: '+$structural
$lines+='- Latest desktop installer: '+$setupText
$lines+=''
$lines+='## Error index'
$lines+='- '+$errorSummary
if($errorExamples.Count){$lines+=$errorExamples}
$lines+=''
$lines+='## AI workflow'
$lines+='- task: '+$aiTask
$lines+='- skill: '+$aiSkill
$lines+='- checkpoint: '+$aiCheckpoint
$lines+='- project index: '+$aiIndex
$lines+=''
$lines+='## Recent source changes'
$lines+=$recentText
$lines+=''
$lines+='## Last DC command'
$lines+='- '+$lastDcText
$lines+=''
$lines+='## Continuity'
$lines+='- WORK_PROGRESS.md: '+$(if(Test-Path $work){'present'}else{'missing'})
$lines+='- Latest handoff: '+$handoffText
$lines+='- context-compress: '+$ccText
$lines+='- DC logs retained: '+$dcLogs.Count
$lines+=''
$lines+='## Recent project notes'
$lines+=$notesText
$lines+=''
$lines+='## Rules for ChatGPT / DC Remote'
$lines+='- Read PROJECT_STATE.md first when asked to continue the Optimizer.'
$lines+='- Prefer current release/deployment/validation reports in RESUME.md; .ai-state\status.json can be historical and must match the tested version.'
$lines+='- Read SESSION_HANDOFF.md next when present and newer than the last relevant project change.'
$lines+='- Use WORK_PROGRESS.md only when more detail is needed.'
$lines+='- For verbose builds/tests/logs use .\dc-smart.ps1 "<command>".'
$lines+='- Check .ai-state\errors.json only when the compact error summary is insufficient.'
$lines+='- Scheduled light refresh intentionally skips source fingerprint hashing; dc-smart/full refresh validates fingerprints.'

[IO.File]::WriteAllText($out,($lines -join [Environment]::NewLine),[Text.UTF8Encoding]::new($false))
Write-Output ('PROJECT_STATE_OK '+$out)
