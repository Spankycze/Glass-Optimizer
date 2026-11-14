param(
    [ValidateSet('SelfTest','Record','Report')][string]$Mode='Report',
    [string]$Version='',
    [double]$TotalMilliseconds=0,
    [double]$PipelineMilliseconds=0,
    [System.Collections.IDictionary]$PhaseMilliseconds=@{}
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$historyPath=Join-Path $root 'artifacts\.cache\release-time-history.json'

function Get-Median([double[]]$values) {
    $v=@($values | Sort-Object)
    if($v.Count -eq 0){return 0.0}
    $mid=[int][math]::Floor($v.Count/2)
    if($v.Count % 2 -eq 1){return [double]$v[$mid]}
    return ([double]$v[$mid-1]+[double]$v[$mid])/2
}
# A reader can temporarily hold release-time-history.json without
# FILE_SHARE_DELETE. Retry only recognizable file I/O/access races; never
# truncate the destination or weaken the release's smoke-test contract.
function Test-RetryableHistoryMoveFailure([Exception]$errorToCheck) {
    while($null -ne $errorToCheck.InnerException) {
        $errorToCheck=$errorToCheck.InnerException
    }
    return $errorToCheck -is [System.IO.IOException] -or
        $errorToCheck -is [System.UnauthorizedAccessException]
}
function Move-AtomicHistoryWithRetry([string]$source,[string]$destination) {
    for($attempt=0;$attempt -lt 5;$attempt++) {
        try {
            [IO.File]::Move($source,$destination,$true)
            return
        }catch {
            if($attempt -eq 4 -or
               -not (Test-RetryableHistoryMoveFailure $_.Exception)) {
                throw
            }
            Start-Sleep -Milliseconds (50*($attempt+1))
        }
    }
}
# Keep exactly the same bounded snapshot window used by Read-History.
# Never prune the locked canonical history, current snapshot, or unrelated
# files. Cleanup failures are diagnostic only, never a reason to reject a
# successfully recorded release.
function Prune-OldHistorySnapshots([string]$directory) {
    try {
        $all=@(Get-ChildItem -LiteralPath $directory -File -Filter 'release-time-history-snapshot-*.json' |
            Sort-Object -Property LastWriteTimeUtc,Name -Descending)
        foreach($stale in @($all | Select-Object -Skip 24)) {
            if(($stale.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                continue
            }
            try {
                [IO.File]::Delete($stale.FullName)
            }catch {
                Write-Warning ('RELEASE_PERF_SNAPSHOT_PRUNE_SKIPPED file={0}' -f $stale.Name)
            }
        }
    }catch {
        Write-Warning 'RELEASE_PERF_SNAPSHOT_PRUNE_SKIPPED unable to enumerate cache'
    }
}
function Get-PhaseValue($entry,[string]$name){
    if($null -eq $entry.phases){return $null}
    if($entry.phases -is [System.Collections.IDictionary]){
        if($entry.phases.Contains($name)){return [double]$entry.phases[$name]}
    }else{
        $p=$entry.phases.PSObject.Properties[$name]
        if($null -ne $p){return [double]$p.Value}
    }
    return $null
}
function Get-SlowWarnings([object[]]$history,$current){
    $warnings=[Collections.Generic.List[string]]::new()
    $prior=@($history | Select-Object -Last 8)
    if($prior.Count -lt 3){return @()}
    $totalMedian=Get-Median ([double[]]@($prior | ForEach-Object {[double]$_.totalMs}))
    $totalLimit=[math]::Max($totalMedian*1.5,$totalMedian+15000)
    if([double]$current.totalMs -gt $totalLimit){
        $warnings.Add(('total current={0:n0}ms median={1:n0}ms limit={2:n0}ms' -f
            $current.totalMs,$totalMedian,$totalLimit))
    }
    foreach($name in $current.phases.Keys){
        $samples=[Collections.Generic.List[double]]::new()
        foreach($entry in $prior){
            $v=Get-PhaseValue $entry $name
            if($null -ne $v){$samples.Add([double]$v)}
        }
        if($samples.Count -lt 3){continue}
        $median=Get-Median $samples.ToArray()
        $limit=[math]::Max($median*1.5,$median+4000)
        if([double]$current.phases[$name] -gt $limit){
            $warnings.Add(('{0} current={1:n0}ms median={2:n0}ms limit={3:n0}ms' -f
                $name,$current.phases[$name],$median,$limit))
        }
    }
    return $warnings.ToArray()
}
# Older release history can be held open with FILE_SHARE_DELETE disabled by
# another process. Immutable snapshots in the same cache directory avoid
# touching that file while preserving the newest successful release baseline.
function Get-HistoryLastCompletion([object[]]$items) {
    if($items.Count -eq 0){return [datetimeoffset]::MinValue}
    try {
        return [datetimeoffset]::Parse([string]$items[-1].completedUtc,
            [Globalization.CultureInfo]::InvariantCulture)
    }catch{return [datetimeoffset]::MinValue}
}
function Read-History {
    $dir=Split-Path -Parent $historyPath
    $candidates=@()
    if(Test-Path -LiteralPath $historyPath){
        $candidates+=@(Get-Item -LiteralPath $historyPath)
    }
    if(Test-Path -LiteralPath $dir){
        $candidates+=@(Get-ChildItem -LiteralPath $dir -File -Filter 'release-time-history-snapshot-*.json' |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 24)
    }
    $best=@()
    $bestCompleted=[datetimeoffset]::MinValue
    foreach($candidate in $candidates){
        try{
            $parsed=Get-Content -LiteralPath $candidate.FullName -Raw -Encoding UTF8 |
                ConvertFrom-Json
            $entries=@($parsed | Where-Object { $_.totalMs -gt 0 -and $_.version })
            if($entries.Count -eq 0){continue}
            $completed=Get-HistoryLastCompletion $entries
            if($completed -gt $bestCompleted -or $best.Count -eq 0){
                $best=$entries
                $bestCompleted=$completed
            }
        }catch{
            Write-Warning ('RELEASE_PERF_HISTORY_INVALID skipped={0}' -f $candidate.Name)
        }
    }
    return $best
}
if($Mode -eq 'SelfTest'){
    $prior=@(1..3 | ForEach-Object{
        [pscustomobject]@{version="baseline$_";totalMs=100000;phases=@{app=30000;ui=40000}}
    })
    $steady=[pscustomobject]@{version='steady';totalMs=115000;phases=@{app=35000;ui=42000}}
    $slow=[pscustomobject]@{version='slow';totalMs=170000;phases=@{app=52000;ui=69000}}
    $warn=@(Get-SlowWarnings $prior $slow)
    if(@(Get-SlowWarnings $prior $steady).Count -ne 0 -or
       $warn.Count -ne 3 -or
       @(Get-SlowWarnings @($prior[0],$prior[1]) $slow).Count -ne 0 -or
       (Get-Median ([double[]]@(9,1,7,3))) -ne 5 -or
       (Get-HistoryLastCompletion @([pscustomobject]@{completedUtc='2026-10-23T10:00:00Z'})) -le
           (Get-HistoryLastCompletion @([pscustomobject]@{completedUtc='2026-10-22T10:00:00Z'})) -or
       -not (Test-RetryableHistoryMoveFailure ([IO.IOException]::new('locked'))) -or
       -not (Test-RetryableHistoryMoveFailure ([UnauthorizedAccessException]::new('locked'))) -or
       (Test-RetryableHistoryMoveFailure ([ArgumentException]::new('invalid')))){
        throw 'RELEASE_PERF_GUARD_SMOKE_FAIL'
    }
    Write-Host 'RELEASE_PERF_GUARD_SMOKE_OK median=true noiseMargin=true threeReferences=true writes=0'
    return
}
$history=@(Read-History)
if($Mode -eq 'Report'){
    if($history.Count -eq 0){Write-Host 'RELEASE_PERF_HISTORY_EMPTY';return}
    $recent=$history[-1]
    $endToEnd=if($recent.PSObject.Properties['pipelineMs'] -and $recent.pipelineMs -gt 0){$recent.pipelineMs}else{'not-measured'}
    Write-Host ('RELEASE_PERF_LAST version={0} publishAndSmokeMs={1} fullPipelineMs={2}' -f $recent.version,$recent.totalMs,$endToEnd)
    $smokeBreakdown=@(foreach($name in @('software-updater','shortcut-overlay','storage-concurrency','visual-size','ui-bundle','service-bundle')){
        $ms=Get-PhaseValue $recent ('smoke-'+$name)
        if($null -ne $ms){[pscustomobject]@{Name=$name;Ms=[math]::Round($ms)}}
    })
    if($smokeBreakdown.Count -gt 0){
        $slowest=@($smokeBreakdown | Sort-Object Ms -Descending | Select-Object -First 3)
        Write-Host ('RELEASE_PERF_SLOWEST_SMOKE '+(($slowest | ForEach-Object { '{0}={1}ms' -f $_.Name,$_.Ms }) -join ' '))
    }
    return
}
if([string]::IsNullOrWhiteSpace($Version) -or $TotalMilliseconds -le 0 -or
   [double]::IsInfinity($TotalMilliseconds) -or [double]::IsNaN($TotalMilliseconds) -or
   $PipelineMilliseconds -lt 0 -or -not [double]::IsFinite($PipelineMilliseconds) -or
   ($PipelineMilliseconds -gt 0 -and $PipelineMilliseconds -lt $TotalMilliseconds)){
    throw 'RELEASE_PERF_RECORD_INVALID'
}
$phases=[ordered]@{}
foreach($name in $PhaseMilliseconds.Keys){
    $v=[double]$PhaseMilliseconds[$name]
    if($v -lt 0 -or -not [double]::IsFinite($v)){throw 'RELEASE_PERF_PHASE_INVALID'}
    $phases[[string]$name]=[math]::Round($v)
}
$current=[pscustomobject]@{
    version=$Version
    completedUtc=[DateTime]::UtcNow.ToString('o')
    totalMs=[math]::Round($TotalMilliseconds)
    pipelineMs=[math]::Round($PipelineMilliseconds)
    phases=$phases
}
foreach($slow in @(Get-SlowWarnings $history $current)){
    Write-Warning ('RELEASE_PERFORMANCE_REGRESSION ' + $slow)
}
$new=@($history | Select-Object -Last 11)+@($current)
$dir=Split-Path -Parent $historyPath
[IO.Directory]::CreateDirectory($dir) | Out-Null
$tmp=$historyPath+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
try {
    [IO.File]::WriteAllText($tmp,($new | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
    try {
        Move-AtomicHistoryWithRetry $tmp $historyPath
    }catch{
        if(-not (Test-RetryableHistoryMoveFailure $_.Exception)){throw}
        # Never truncate or rename the locked original. This immutable,
        # unique sidecar carries the complete rolling history and can be
        # read by Report/Record on the next release.
        $snapshot=Join-Path $dir ('release-time-history-snapshot-'+
            ($Version -replace '[^0-9.]','_').Replace('.','_')+'-'+
            [Guid]::NewGuid().ToString('N')+'.json')
        [IO.File]::Move($tmp,$snapshot)
        Write-Warning ('RELEASE_PERF_HISTORY_SNAPSHOT_SAVED originalLocked=true version={0}' -f $Version)
    }
}finally{
    if(Test-Path -LiteralPath $tmp){Remove-Item -LiteralPath $tmp -Force}
}
Prune-OldHistorySnapshots $dir
Write-Host ('RELEASE_PERF_HISTORY_OK version={0} totalMs={1:n0} records={2}' -f
    $Version,$current.totalMs,$new.Count)
