# Read-only, reproducible resource sampler for the current Windows processes.
# Intended for comparing EXE releases under equivalent REAL workloads.
# No process start/stop, privilege elevation, Windows setting or UI changes.
param(
    [ValidateRange(2,300)][int]$DurationSeconds=12,
    [ValidateRange(100,2000)][int]$IntervalMilliseconds=250,
    [string]$OutputDirectory='',
    [switch]$NoFiles
)
$ErrorActionPreference='Stop'
$project=(Split-Path $PSScriptRoot -Parent)
if([string]::IsNullOrWhiteSpace($OutputDirectory)){
    $OutputDirectory=Join-Path $project '.ai-state\resource-measurements'
}
if(-not $NoFiles){[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null}
$rows=[Collections.Generic.List[object]]::new()
$sw=[Diagnostics.Stopwatch]::StartNew()
$logical=[Environment]::ProcessorCount
$sample=0

while($true){
    $elapsed=$sw.Elapsed.TotalSeconds
    foreach($process in @(Get-Process -Name 'SpankyhoOptimizer*' -ErrorAction SilentlyContinue)){
        try{
            $process.Refresh()
            $rows.Add([pscustomobject]@{
                Sample=$sample
                Seconds=[math]::Round($elapsed,3)
                Pid=$process.Id
                Name=$process.ProcessName
                WorkingMb=[math]::Round($process.WorkingSet64/1MB,2)
                PrivateMb=[math]::Round($process.PrivateMemorySize64/1MB,2)
                CpuMs=[math]::Round($process.TotalProcessorTime.TotalMilliseconds,2)
                Handles=$process.HandleCount
                Threads=$process.Threads.Count
            })
        }catch [System.InvalidOperationException]{
            # A process could exit naturally during the sample.
        }catch [System.ComponentModel.Win32Exception]{
            # System service process may deny non-elevated inspection.
        }
    }
    $sample++
    if($sw.Elapsed.TotalSeconds -ge $DurationSeconds){break}
    Start-Sleep -Milliseconds $IntervalMilliseconds
}
$sw.Stop()
$results=@(
    foreach($group in ($rows|Group-Object Pid)){
        $observations=@($group.Group|Sort-Object Seconds)
        $first=$observations[0];$last=$observations[-1]
        $window=[math]::Max(0.001,$last.Seconds-$first.Seconds)
        $cpuDelta=[math]::Max(0,$last.CpuMs-$first.CpuMs)
        [pscustomobject]@{
            Name=$first.Name
            Pid=$first.Pid
            Samples=$observations.Count
            CpuDeltaMs=[math]::Round($cpuDelta,1)
            CpuOneCorePercent=[math]::Round(100*$cpuDelta/($window*1000),2)
            CpuTotalSystemPercent=[math]::Round(100*$cpuDelta/($window*1000*$logical),3)
            WorkingStartMb=$first.WorkingMb
            WorkingEndMb=$last.WorkingMb
            WorkingPeakMb=[math]::Round(($observations.WorkingMb|Measure-Object -Maximum).Maximum,2)
            PrivateStartMb=$first.PrivateMb
            PrivateEndMb=$last.PrivateMb
            PrivatePeakMb=[math]::Round(($observations.PrivateMb|Measure-Object -Maximum).Maximum,2)
            HandlesStart=$first.Handles
            HandlesEnd=$last.Handles
            ThreadsStart=$first.Threads
            ThreadsEnd=$last.Threads
        }
    }
)
if(-not $NoFiles){
    $stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
    $csv=Join-Path $OutputDirectory "optimizer-resources-$stamp.csv"
    $summary=Join-Path $OutputDirectory "optimizer-resources-$stamp-summary.json"
    $rows|Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
    [pscustomobject]@{
        DurationSeconds=[math]::Round($sw.Elapsed.TotalSeconds,2)
        IntervalMilliseconds=$IntervalMilliseconds
        LogicalProcessors=$logical
        TimestampLocal=(Get-Date).ToString('o')
        Measurements=$results
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summary -Encoding utf8
    "RESOURCE_CSV=$csv"
    "RESOURCE_SUMMARY=$summary"
}
"RESOURCE_WINDOW_SECONDS=$([math]::Round($sw.Elapsed.TotalSeconds,2)) LOGICAL_CPU=$logical PROCESS_COUNT=$($results.Count)"
if($results.Count -eq 0){'RESOURCE_NO_OPTIMIZER_PROCESS: open the app normally to measure WinUI'}
else{$results|Format-Table -AutoSize|Out-String -Width 290}
