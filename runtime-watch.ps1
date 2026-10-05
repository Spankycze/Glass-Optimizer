param(
  [int]$ProcessId=0,
  [string]$ProcessName='SpankyhoOptimizer',
  [int]$DurationSeconds=120,
  [double]$CpuThreshold=80,
  [double]$PrivateMbThreshold=900,
  [int]$HangSamples=5
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$out=Join-Path $root ('artifacts\runtime-watch\'+(Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Force -Path $out|Out-Null
if($ProcessId -le 0){
  $proc=Get-Process -Name $ProcessName -ErrorAction Stop|Sort-Object StartTime -Descending|Select-Object -First 1
  $ProcessId=$proc.Id
}
$p=Get-Process -Id $ProcessId -ErrorAction Stop
$lastCpu=$p.TotalProcessorTime.TotalMilliseconds
$lastAt=[DateTime]::UtcNow
$highCpu=0;$hang=0;$traceTaken=$false;$gcTaken=$false;$dumpTaken=$false
$end=[DateTime]::UtcNow.AddSeconds($DurationSeconds)
Write-Output ('RUNTIME_WATCH pid='+$ProcessId+' until='+$end.ToString('o'))
while([DateTime]::UtcNow -lt $end){
  Start-Sleep -Seconds 1
  $p=Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if(-not $p){Write-Output 'RUNTIME_WATCH_PROCESS_EXIT';break}
  $now=[DateTime]::UtcNow
  $elapsed=($now-$lastAt).TotalMilliseconds
  $cpuDelta=$p.TotalProcessorTime.TotalMilliseconds-$lastCpu
  $cpu=if($elapsed -gt 0){($cpuDelta/$elapsed)*100/[Environment]::ProcessorCount}else{0}
  $private=$p.PrivateMemorySize64/1MB
  $responding=$true
  try{$responding=$p.Responding}catch{}
  if($cpu -ge $CpuThreshold){$highCpu++}else{$highCpu=0}
  if(-not $responding){$hang++}else{$hang=0}

  if($highCpu -ge 3 -and -not $traceTaken){
    $trace=Join-Path $out 'high-cpu.nettrace'
    Write-Output ('RUNTIME_TRIGGER high-cpu cpu='+[math]::Round($cpu,1))
    dotnet-trace collect -p $ProcessId --duration 00:00:15 -o $trace *> (Join-Path $out 'high-cpu-trace.log')
    $traceTaken=$true
  }
  if($private -ge $PrivateMbThreshold -and -not $gcTaken){
    $gc=Join-Path $out 'high-memory.gcdump'
    Write-Output ('RUNTIME_TRIGGER high-memory privateMb='+[math]::Round($private,1))
    dotnet-gcdump collect -p $ProcessId -o $gc *> (Join-Path $out 'high-memory-gcdump.log')
    $gcTaken=$true
  }
  if($hang -ge $HangSamples -and -not $dumpTaken){
    $dump=Join-Path $out 'hang.dmp'
    Write-Output ('RUNTIME_TRIGGER hang samples='+$hang)
    dotnet-dump collect -p $ProcessId -o $dump *> (Join-Path $out 'hang-dump.log')
    $dumpTaken=$true
  }

  $lastCpu=$p.TotalProcessorTime.TotalMilliseconds;$lastAt=$now
}
Write-Output ('RUNTIME_WATCH_OK output='+$out+' trace='+$traceTaken+' gcdump='+$gcTaken+' dump='+$dumpTaken)
