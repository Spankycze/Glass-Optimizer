param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Command,
    [int]$MaxInlineBytes = 20000,
    [int]$HeadLines = 12,
    [int]$TailLines = 24,
    [int]$MaxImportantLines = 8,
    [ValidateSet('Quiet','Normal','Verbose')]
    [string]$Mode = 'Quiet'
)

$ErrorActionPreference = 'Continue'
$project = 'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer'
$logDir = Join-Path $project '.dc-logs'
$aiDir = Join-Path $project '.ai-state'
$statusPath = Join-Path $aiDir 'status.json'
$errorsPath = Join-Path $aiDir 'errors.json'
$openErrorsPath = Join-Path $aiDir 'errors-open.json'
$maxErrorHistoryBytes = 2MB
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
New-Item -ItemType Directory -Path $aiDir -Force | Out-Null

function Write-JsonAtomic([string]$Path, $Object) {
    $json = $Object | ConvertTo-Json -Depth 10 -Compress
    if(Test-Path $Path){
        try{if([IO.File]::ReadAllText($Path) -eq $json){return}}catch{}
    }
    $tmp = $Path + '.tmp'
    [IO.File]::WriteAllText($tmp, $json, [Text.UTF8Encoding]::new($false))
    Move-Item $tmp $Path -Force
}

function Get-CommandKind([string]$Text) {
    if ($Text -match '(?i)(^|[\\/ ])build-fast\.ps1(\s|$)') { return 'build-fast' }
    if ($Text -match '(?i)(^|[\\/ ])build-test\.ps1(\s|$)') { return 'build-test' }
    if ($Text -match '(?i)(^|[\\/ ])build-release\.ps1(\s|$)') { return 'build-release' }
    return 'other'
}

function Get-ExecutionMode([string]$Kind, [string]$Text) {
    switch ($Kind) {
        'build-fast' {
            if ($Text -match 'FAST_BUILD_CACHE_HIT|FAST_BUILD_STAGED_CACHE_HIT') { return 'cache-hit' }
            if ($Text -match 'CORE_FAST_BUILD_OK') { return 'core-build' }
            if ($Text -match 'FAST_BUILD_STAGED_LOCK_OK|FAST_STRUCTURAL_STAGED') { return 'staged-build' }
            if ($Text -match 'FAST_BUILD_OK') { return 'build' }
            return 'unknown'
        }
        'build-test' {
            if ($Text -match 'BUILD_TEST_FULL_CACHE_HIT|BUILD_TEST_CACHE_HIT') { return 'cache-hit' }
            if ($Text -match 'BUILD_TEST_OK') { return 'test-run' }
            return 'unknown'
        }
        'build-release' {
            if ($Text -match 'RELEASE_OK') { return 'release' }
            if ($Text -match 'CACHE_HIT') { return 'cached-release' }
            return 'unknown'
        }
    }
    return 'other'
}

function Get-VersionFromText([string]$Text) {
    $m = [regex]::Match($Text, '(?im)(?:FINAL:.*?V|VERSION_BUMP_OK\s+)(\d+\.\d+\.\d+)')
    if ($m.Success) { return $m.Groups[1].Value }
    $props = Join-Path $project 'Directory.Build.props'
    if (Test-Path $props) {
        $pm=[regex]::Match([IO.File]::ReadAllText($props),'<SpankyVersion>([^<]+)</SpankyVersion>')
        if($pm.Success){return $pm.Groups[1].Value}
    }
    return $null
}

function Update-FreshStatus([string]$Kind,[string]$Text,[int]$Exit,[double]$DurationMs,[string]$Log,[int64]$Bytes,[datetime]$Started,[datetime]$Ended) {
    if ($Kind -eq 'other') { return }
    $root = [ordered]@{
        schema = 1
        updatedAt = $Ended.ToString('o')
        entries = [ordered]@{}
    }
    if (Test-Path $statusPath) {
        try {
            $existing = Get-Content $statusPath -Raw | ConvertFrom-Json
            $entries = [ordered]@{}
            foreach($p in $existing.entries.PSObject.Properties){$entries[$p.Name]=$p.Value}
            $root.entries=$entries
        } catch {}
    }

    $mode = Get-ExecutionMode $Kind $Text
    $outcome = if($Exit -eq 0){'OK'}else{'FAIL'}
    $record = [ordered]@{
        kind = $Kind
        outcome = $outcome
        exitCode = $Exit
        mode = $mode
        startedAt = $Started.ToString('o')
        finishedAt = $Ended.ToString('o')
        durationMs = [math]::Round($DurationMs,1)
        version = Get-VersionFromText $Text
        log = $Log
        bytes = $Bytes
    }
    $root.entries[$Kind] = $record
    $root.updatedAt = $Ended.ToString('o')
    Write-JsonAtomic $statusPath $root
}

function Normalize-ErrorKey([string]$Line) {
    $s=$Line.Trim()
    $s=$s -replace '^[A-Z]:\\[^:]+:\s*',''
    $s=$s -replace '\b0x[0-9A-Fa-f]+\b','0x#'
    $s=$s -replace '\b\d{2,}\b','#'
    $s=$s -replace '\s+',' '
    return $s.ToLowerInvariant()
}

function Update-ErrorIndex([string]$Kind,[string[]]$Lines,[int]$Exit,[string]$Log,[datetime]$Now) {
    if ($Kind -notin @('build-fast','build-test','build-release')) { return }

    if(Test-Path $errorsPath){
        try{
            $historyInfo=[IO.FileInfo]::new($errorsPath)
            if($historyInfo.Length -gt $maxErrorHistoryBytes){
                $archive=Join-Path $aiDir ('errors-archive-'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'.json')
                Move-Item $errorsPath $archive -Force
                Write-Output ('[WARN] error-history rotated bytes='+$historyInfo.Length+' archive='+$archive)
                $archives=@(Get-ChildItem $aiDir -File -Filter 'errors-archive-*.json' -ErrorAction SilentlyContinue|Sort-Object LastWriteTimeUtc -Descending)
                if($archives.Count -gt 2){$archives|Select-Object -Skip 2|Remove-Item -Force -ErrorAction SilentlyContinue}
            }
        }catch{}
    }

    $candidates=@(
        $Lines | Where-Object {
            $_ -match '(?i)(\berror\b|\bfail(?:ed|ure)?\b|\bexception\b|\bfatal\b|\btimeout\b|\bdenied\b|\bselhal\b|\bchyba\b|CS\d{4}|MSB\d{4}|exit=\d+)' -and
            $_ -notmatch '(?i)(\bOK\b|\bPASS(?:ED)?\b|CACHE_HIT|exit\s*[=:]\s*0\b)'
        }
    )

    $openDb=[ordered]@{schema=1;updatedAt=$Now.ToString('o');errors=@()}
    if(Test-Path $openErrorsPath){
        try{$tmp=[IO.File]::ReadAllText($openErrorsPath)|ConvertFrom-Json;$openDb.errors=@($tmp.errors)}catch{}
    }
    $hasOpenKind=@($openDb.errors|Where-Object kind -eq $Kind).Count -gt 0
    if($Exit -eq 0 -and $candidates.Count -eq 0 -and -not $hasOpenKind){return}

    $db = [ordered]@{schema=1;updatedAt=$Now.ToString('o');errors=@()}
    if (Test-Path $errorsPath) {
        try {$old=[IO.File]::ReadAllText($errorsPath)|ConvertFrom-Json;$db.errors=@($old.errors)} catch {}
    }

    $currentKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($line in $candidates){
        $msg=$line.Trim()
        if([string]::IsNullOrWhiteSpace($msg)){continue}
        $key=Normalize-ErrorKey $msg
        if([string]::IsNullOrWhiteSpace($key)){continue}
        [void]$currentKeys.Add($key)

        $found=@($db.errors|Where-Object key -eq $key)|Select-Object -First 1
        if($null -eq $found){
            $found=[pscustomobject][ordered]@{
                key=$key;message=$msg;kind=$Kind
                firstSeen=$Now.ToString('o');lastSeen=$Now.ToString('o')
                count=1;regressionCount=0;lastLog=$Log
                rootCause=$null;fixThatWorked=$null;filesInvolved=@()
                resolved=$false;resolvedAt=$null
            }
            $db.errors += $found
        }else{
            $wasResolved=[bool]$found.resolved
            $found.message=$msg;$found.kind=$Kind;$found.lastSeen=$Now.ToString('o')
            $found.count=[int]$found.count+1
            if($wasResolved){$found.regressionCount=[int]$found.regressionCount+1}
            $found.lastLog=$Log;$found.resolved=$false;$found.resolvedAt=$null
        }
    }

    if($Exit -eq 0){
        foreach($e in @($db.errors)){
            if($e.kind -eq $Kind -and -not [bool]$e.resolved){
                $e.resolved=$true;$e.resolvedAt=$Now.ToString('o')
            }
        }
    }

    $db.errors=@($db.errors|Sort-Object @{Expression={$_.resolved};Ascending=$true},@{Expression={$_.lastSeen};Descending=$true}|Select-Object -First 250)
    $db.updatedAt=$Now.ToString('o')
    Write-JsonAtomic $errorsPath $db

    $openDb.errors=@($db.errors|Where-Object{-not [bool]$_.resolved}|Select-Object -First 50)
    $openDb.updatedAt=$Now.ToString('o')
    Write-JsonAtomic $openErrorsPath $openDb
}

$rotateStamp=Join-Path $aiDir 'log-rotation.stamp'
$rotateNow=$true
if(Test-Path $rotateStamp){
    try{$rotateNow=(Get-Item $rotateStamp).LastWriteTimeUtc -lt (Get-Date).ToUniversalTime().AddHours(-6)}catch{}
}
if($rotateNow){
    $allLogs=@(Get-ChildItem $logDir -File -Filter 'dc_*.log' -ErrorAction SilentlyContinue|Sort-Object LastWriteTime -Descending)
    $allLogs|Where-Object LastWriteTime -lt (Get-Date).AddDays(-7)|Remove-Item -Force -ErrorAction SilentlyContinue
    if($allLogs.Count -gt 100){$allLogs|Select-Object -Skip 100|Remove-Item -Force -ErrorAction SilentlyContinue}
    [IO.File]::WriteAllText($rotateStamp,(Get-Date).ToString('o'))
}

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$log = Join-Path $logDir ("dc_" + $stamp + ".log")
$started = Get-Date
$timer = [Diagnostics.Stopwatch]::StartNew()

Push-Location $project
try {
    $directSafe=$Command -match '^\s*\.\\(build-fast|build-test|build-release|project-index|update-project-state|continue-project|smart-quality|change-impact|validate-tooling|source-index|cleanup-audit|cache-correctness-audit|background-interference-audit|ui-thread-budget-audit|performance-diagnose|memory-retention-audit|build-speed-audit)\.ps1\b'
    if($directSafe){
        try{
            $sb=[scriptblock]::Create($Command)
            $raw=@(& $sb *>&1)
            $exit=0
        }catch{
            $raw=@($_)
            $exit=1
        }
    }else{
        $raw = & cmd.exe /d /s /c $Command 2>&1
        $exit = $LASTEXITCODE
    }
    $timer.Stop()
    $ended = Get-Date
    $combined = ($raw | Out-String)
    $preKind = Get-CommandKind $Command
    if($exit -eq 0 -and $preKind -eq 'build-release' -and $combined -notmatch '(?im)\bRELEASE_OK\b|\bCACHE_HIT\b'){
        $exit = 3
        $combined += [Environment]::NewLine + 'ERROR DC_SMART_RELEASE_MARKER_MISSING: build-release returned exit 0 without RELEASE_OK/CACHE_HIT.'
    }

    [IO.File]::WriteAllText($log, $combined, [Text.UTF8Encoding]::new($false))
    $bytes = (Get-Item $log).Length
    $kind = Get-CommandKind $Command

    $lastRecord=[ordered]@{
        Timestamp=$ended.ToString('yyyy-MM-dd HH:mm:ss')
        Command=$Command
        Kind=$kind
        ExitCode=$exit
        DurationMs=[math]::Round($timer.Elapsed.TotalMilliseconds,1)
        Log=$log
        Bytes=$bytes
    }
    Write-JsonAtomic (Join-Path $project '.dc-last.json') $lastRecord

    $lines = @($combined -split "\r?\n")
    Update-FreshStatus $kind $combined $exit $timer.Elapsed.TotalMilliseconds $log $bytes $started $ended
    Update-ErrorIndex $kind $lines $exit $log $ended

    $importantRaw = @(
        $lines | Where-Object {
            $_ -match '(?i)\b(error|failed|failure|exception|fatal|warning|warn|crash|timeout|denied|cannot|could not|CS\d{4}|MSB\d{4})\b'
        }
    )
    $seen=@{}
    $important=[Collections.Generic.List[string]]::new()
    foreach($line in $importantRaw){
        $key=$line.Trim().ToLowerInvariant()
        $key=$key -replace '\b\d{4}-\d{2}-\d{2}[t ][0-9:.+-z]+\b','<time>'
        $key=$key -replace '\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b','<guid>'
        $key=$key -replace '\b0x[0-9a-f]+\b','0x#'
        $key=$key -replace '\(\d+,\d+\)','(#,#)'
        $key=$key -replace '\b\d{2,}\b','#'
        $key=$key -replace '(?i)[a-z]:\\users\\[^\\]+\\appdata\\local\\temp\\[^ ]+','<temp>'
        $key=$key -replace '\s+',' '
        if(-not $key){continue}
        if($seen.ContainsKey($key)){$seen[$key].count++;continue}
        $seen[$key]=[pscustomobject]@{count=1;line=$line.Trim()}
    }
    foreach($v in $seen.Values){
        $text=[string]$v.line
        if([int]$v.count -gt 1){$text+=' [repeated '+$v.count+'x]'}
        $important.Add($text)
        if($important.Count -ge $MaxImportantLines){break}
    }

    $duration=[math]::Round($timer.Elapsed.TotalMilliseconds,1)
    $label=if($kind -eq 'other'){'CMD'}else{$kind.ToUpperInvariant()}

    if($Mode -eq 'Verbose'){
        Write-Output $combined
    }elseif($exit -ne 0){
        Write-Output ('[FAIL] '+$label+' exit='+$exit+' '+$duration+' ms')
        $important|Select-Object -First $MaxImportantLines
        Write-Output ('[LOG] '+$log)
    }elseif($Mode -eq 'Normal'){
        Write-Output ('[OK] '+$label+' '+$duration+' ms | '+$lines.Count+' lines -> log')
        $summary=@($lines|Where-Object{$_ -match '(?i)(\bOK\b|CACHE_HIT|\[BUILD\]|\[TEST\]|\[PERF\]|\[INDEX\]|\[WARN\])'}|Select-Object -Unique|Select-Object -Last 12)
        $summary
    }else{
        $marker=@($lines|Where-Object{$_ -match '(?i)(CACHE_HIT|BUILD_TEST_OK|FAST_BUILD_OK|FULL_TEST_OK|RELEASE_OK|PROJECT_STATE_OK|\[INDEX\]|\[OK\])'}|Select-Object -Last 1)
        $suffix=if($marker.Count){' | '+$marker[0].Trim()}else{''}
        Write-Output ('[OK] '+$label+' '+$duration+' ms'+$suffix)
    }

    exit $exit
}
finally {
    Pop-Location
    try {
        & (Join-Path $project 'update-project-state.ps1') -Reason 'dc-smart' -Light | Out-Null
    } catch {}
}
