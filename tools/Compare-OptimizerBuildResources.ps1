# Safe A/B measurements for verified test-only Optimizer CLI paths.
# No installation, UI launch, process termination, registry or powercfg write.
param(
    [Parameter(Mandatory=$true)][string]$BaselineExe,
    [Parameter(Mandatory=$true)][string]$CandidateExe,
    [ValidateSet('--ai-power-refresh-smoke','--ai-power-details-cache-smoke','--full-smoke')]
    [string]$TestFlag='--ai-power-refresh-smoke',
    [ValidateRange(1,20)][int]$Rounds=6,
    [string]$OutputDirectory=''
)
$ErrorActionPreference='Stop'
foreach($exe in @($BaselineExe,$CandidateExe)){
    if(-not(Test-Path -LiteralPath $exe -PathType Leaf)){
        throw "RESOURCE_BENCH_FILE_NOT_FOUND: $exe"
    }
}
if([string]::IsNullOrWhiteSpace($OutputDirectory)){
    $OutputDirectory=Join-Path (Split-Path $PSScriptRoot -Parent) '.ai-state\resource-measurements'
}
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null

function Invoke-ResourceTrial([string]$file,[string]$label,[int]$round) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $proc=Start-Process -FilePath $file -ArgumentList $TestFlag -PassThru -ErrorAction Stop
    $ws=0L;$private=0L;$cpu=0.0;$handles=0;$threads=0
    while(-not $proc.HasExited){
        try{
            $proc.Refresh()
            $ws=[math]::Max($ws,$proc.WorkingSet64)
            $private=[math]::Max($private,$proc.PrivateMemorySize64)
            $cpu=[math]::Max($cpu,$proc.TotalProcessorTime.TotalMilliseconds)
            $handles=[math]::Max($handles,$proc.HandleCount)
            $threads=[math]::Max($threads,$proc.Threads.Count)
        }catch{}
        Start-Sleep -Milliseconds 40
    }
    $proc.WaitForExit();$clock.Stop()
    if($proc.ExitCode -ne 0){
        throw "RESOURCE_BENCH_TEST_FAILED version=$label round=$round code=$($proc.ExitCode)"
    }
    [pscustomobject]@{
        Version=$label;Round=$round;TestFlag=$TestFlag
        WallMs=[math]::Round($clock.Elapsed.TotalMilliseconds,1)
        CpuMs=[math]::Round($cpu,1)
        PeakWorkingMb=[math]::Round($ws/1MB,2)
        PeakPrivateMb=[math]::Round($private/1MB,2)
        PeakHandles=$handles;PeakThreads=$threads;ExitCode=$proc.ExitCode
    }
}
function Median([double[]]$values){
    $sorted=@($values|Sort-Object)
    $middle=[int][math]::Floor($sorted.Count/2)
    if($sorted.Count%2 -eq 1){return [double]$sorted[$middle]}
    return ([double]$sorted[$middle-1]+[double]$sorted[$middle])/2
}
$baselineVersion=(Get-Item -LiteralPath $BaselineExe).VersionInfo.FileVersion
$candidateVersion=(Get-Item -LiteralPath $CandidateExe).VersionInfo.FileVersion
$raw=[Collections.Generic.List[object]]::new()
for($i=1;$i -le $Rounds;$i++){
    # Alternating order limits systematic effects from cache warming.
    $pair=if($i%2 -eq 1){
        @(@('baseline',$BaselineExe),@('candidate',$CandidateExe))
    } else {
        @(@('candidate',$CandidateExe),@('baseline',$BaselineExe))
    }
    foreach($item in $pair){
        $result=Invoke-ResourceTrial $item[1] $item[0] $i
        $raw.Add($result)
        "RESOURCE_RUN version=$($result.Version) round=$i wallMs=$($result.WallMs) cpuMs=$($result.CpuMs) workingMb=$($result.PeakWorkingMb) privateMb=$($result.PeakPrivateMb)"
    }
}
$aggregate=@(foreach($label in @('baseline','candidate')){
    $entries=@($raw|Where-Object Version -eq $label)
    [pscustomobject]@{
        Version=$label
        FileVersion=if($label -eq 'baseline'){$baselineVersion}else{$candidateVersion}
        Runs=$entries.Count
        WallMedianMs=[math]::Round((Median @($entries.WallMs)),1)
        CpuMedianMs=[math]::Round((Median @($entries.CpuMs)),1)
        PeakWorkingMedianMb=[math]::Round((Median @($entries.PeakWorkingMb)),2)
        PeakPrivateMedianMb=[math]::Round((Median @($entries.PeakPrivateMb)),2)
        PeakHandlesMedian=[math]::Round((Median @($entries.PeakHandles)),1)
        PeakThreadsMedian=[math]::Round((Median @($entries.PeakThreads)),1)
    }
})
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$stem="optimizer-ab-$stamp"
$raw|Export-Csv -LiteralPath (Join-Path $OutputDirectory "$stem.csv") -NoTypeInformation -Encoding utf8
$aggregate|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $OutputDirectory "$stem-summary.json") -Encoding UTF8
"RESOURCE_A_B_RUNS=$Rounds PER_VERSION test=$TestFlag"
$aggregate|Format-Table -AutoSize|Out-String -Width 240
'RESOURCE_A_B_NOTE: CLI test-process numbers, not live WinUI performance. Compare more than one run.'
