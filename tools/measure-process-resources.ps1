param([string]$Exe,[string]$Arguments='--full-smoke',[string]$Output,[int]$PollMs=100)
$ErrorActionPreference='Stop'
if(-not(Test-Path -LiteralPath $Exe)){throw "Missing exe: $Exe"}
$items=[Collections.Generic.List[object]]::new()
$wall=[Diagnostics.Stopwatch]::StartNew()
$proc=Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru
try {
  while(-not $proc.HasExited) {
    try {
      $proc.Refresh()
      $items.Add([pscustomobject]@{
        ms=$wall.ElapsedMilliseconds
        cpuMs=[math]::Round($proc.TotalProcessorTime.TotalMilliseconds,1)
        workingSetMB=[math]::Round($proc.WorkingSet64/1MB,2)
        privateMB=[math]::Round($proc.PrivateMemorySize64/1MB,2)
        threads=$proc.Threads.Count
        handles=$proc.HandleCount
      })
    } catch [InvalidOperationException] { break }
    Start-Sleep -Milliseconds $PollMs
  }
  $proc.WaitForExit()
  $wall.Stop()
  $valid=@($items)
  if($valid.Count -lt 2){throw "Too few process samples"}
  $first=$valid[0];$last=$valid[-1]
  $peakWS=($valid|Measure-Object workingSetMB -Maximum).Maximum
  $peakPrivate=($valid|Measure-Object privateMB -Maximum).Maximum
  $peakThreads=($valid|Measure-Object threads -Maximum).Maximum
  $peakHandles=($valid|Measure-Object handles -Maximum).Maximum
  $report=[ordered]@{
    measuredAt=(Get-Date).ToString('o');exe=$Exe;argument=$Arguments;intervalMs=$PollMs
    samples=$valid.Count;wallMs=$wall.ElapsedMilliseconds;exitCode=$proc.ExitCode
    cpuTimeMs=[math]::Round($last.cpuMs-$first.cpuMs,1)
    avgCpuCores=[math]::Round(($last.cpuMs-$first.cpuMs)/[math]::Max(1,($last.ms-$first.ms)),3)
    peakWorkingSetMB=$peakWS;peakPrivateMB=$peakPrivate
    peakThreads=$peakThreads;peakHandles=$peakHandles
    startWorkingSetMB=$first.workingSetMB;lastWorkingSetMB=$last.workingSetMB
  }
  $dir=Split-Path -Parent $Output
  New-Item -ItemType Directory -Force -Path $dir|Out-Null
  $report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $Output -Encoding utf8
  $valid|Export-Csv -NoTypeInformation -LiteralPath ([IO.Path]::ChangeExtension($Output,'.csv')) -Encoding utf8
  Write-Output ('RESOURCE_MEASURE_OK wallMs={0} cpuMs={1} avgCores={2} wsPeakMB={3} privatePeakMB={4} threadsPeak={5} handlesPeak={6} samples={7} exit={8}' -f $report.wallMs,$report.cpuTimeMs,$report.avgCpuCores,$peakWS,$peakPrivate,$peakThreads,$peakHandles,$valid.Count,$proc.ExitCode)
  if($proc.ExitCode -ne 0){exit $proc.ExitCode}
} finally {$proc.Dispose()}
