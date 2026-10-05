param(
  [int]$Repeats=5,
  [int]$NetworkRepeats=1,
  [switch]$Soak
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$exe=& (Join-Path $root 'resolve-debug-exe.ps1')
if(-not(Test-Path $exe)){throw "Debug/staged EXE missing: $exe"}
Write-Host ('PERF_BENCH_EXE ' + $exe)
$outDir=Join-Path $root '.perf\latest'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$perfLog='C:\ProgramData\SpankyhoOptimizer\performance.log'
$perfLogStart=if(Test-Path $perfLog){(Get-Item $perfLog).Length}else{0}
$appLog='C:\ProgramData\SpankyhoOptimizer\optimizer.log'
$procLog='C:\ProgramData\SpankyhoOptimizer\process-telemetry.log'
$procLogStart=if(Test-Path $procLog){(Get-Item $procLog).Length}else{0}
$env:SPANKY_PROCESS_TELEMETRY='1'
$sentinel=Join-Path $env:TEMP 'SpankyOptimizer.PerfTelemetry.enabled'
Set-Content -LiteralPath $sentinel -Value ((Get-Date).ToString('o')) -Encoding ascii

function Pct([double[]]$a,[double]$p){
  if(-not $a -or $a.Count -eq 0){return $null}
  $s=@($a|Sort-Object)
  $i=[math]::Min($s.Count-1,[math]::Max(0,[math]::Ceiling($p*$s.Count)-1))
  return [math]::Round([double]$s[$i],1)
}
function LatestLine([string]$path,[string]$marker){
  if(-not(Test-Path $path)){return $null}
  return Get-Content $path -Tail 500 | Where-Object {$_ -like "*$marker*"} | Select-Object -Last 1
}
function Field([string]$line,[string]$name){
  if([string]::IsNullOrWhiteSpace($line)){return $null}
  $m=[regex]::Match($line,'(?:^|\s)'+[regex]::Escape($name)+'=(-?\d+(?:[\.,]\d+)?)')
  if(-not $m.Success){return $null}
  return [double]::Parse($m.Groups[1].Value.Replace(',','.'),[Globalization.CultureInfo]::InvariantCulture)
}
function Run([string]$arg){
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $p=Start-Process -FilePath $exe -ArgumentList $arg -Wait -PassThru
  $sw.Stop()
  if($p.ExitCode -ne 0){throw "$arg failed exit=$($p.ExitCode)"}
  return [math]::Round($sw.Elapsed.TotalMilliseconds,1)
}

$page=@{services=@();gpu=@();overview=@();network=@();storage=@();wall=@()}
1..$Repeats|ForEach-Object{
  $page.wall+=Run '--performance-smoke'
  $coldLine=LatestLine $perfLog 'PERF_COLD '
  $warmLine=LatestLine $perfLog 'PERF_WARM '
  $page.services+=Field $coldLine 'servicesMs'
  $page.gpu+=Field $coldLine 'gpuMs'
  $page.overview+=Field $warmLine 'overviewMs'
  $page.network+=Field $coldLine 'networkMs'
  $page.storage+=Field $coldLine 'storageMs'
}

$startup=@()
1..$Repeats|ForEach-Object{
  [void](Run '--startup-hotpath-smoke')
  $startup+=Field (LatestLine $appLog 'STARTUP_HOTPATH_SMOKE_OK') 'activatedMs'
}

$storageCold=@();$storageRefresh=@();$gpuCold=@();$gpuWarm=@();$taskFresh=@();$taskCache=@()
1..([math]::Min(3,$Repeats))|ForEach-Object{
  [void](Run '--storage-probe-smoke')
  $l=LatestLine $appLog 'STORAGE_PROBE_SMOKE_OK'
  $storageCold+=Field $l 'coldMs';$storageRefresh+=Field $l 'refreshMs'
  [void](Run '--gpu-detection-smoke')
  $l=LatestLine $appLog 'GPU_DETECTION_SMOKE_OK'
  $gpuCold+=Field $l 'coldMs';$gpuWarm+=Field $l 'warmMs'
  [void](Run '--task-scheduler-backend-smoke')
  $l=LatestLine $appLog 'TASK_SCHEDULER_BACKEND_SMOKE_OK'
  $taskFresh+=Field $l 'freshMs';$taskCache+=Field $l 'persistentMs'
}

$networkWall=[ordered]@{nic=@();dns=@();mtu=@()}
if($NetworkRepeats -gt 0){
  1..$NetworkRepeats|ForEach-Object{
    $networkWall.nic+=Run '--nic-capability-smoke'
    $networkWall.dns+=Run '--dns-benchmark-smoke'
    $networkWall.mtu+=Run '--mtu-smoke'
  }
}

$soakResult=$null
if($Soak){
  [void](Run '--full-smoke')
  $line=LatestLine $appLog 'SOAK_AUDIT'
  $soakResult=[ordered]@{
    privateDeltaMB=Field $line 'privateDelta'
    wsDeltaMB=Field $line 'wsDelta'
    managedDeltaMB=Field $line 'managedDelta'
    handleDelta=Field $line 'handleDelta'
  }
}

$telemetry=@()
if(Test-Path $procLog){
  $fs=[IO.File]::Open($procLog,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
  try{
    if($procLogStart -le $fs.Length){[void]$fs.Seek($procLogStart,[IO.SeekOrigin]::Begin)}
    $sr=[IO.StreamReader]::new($fs,[Text.Encoding]::UTF8,$true,4096,$true)
    $newText=$sr.ReadToEnd();$sr.Dispose()
  }finally{$fs.Dispose()}
  $telemetry=@($newText -split "\r?\n" | Where-Object {$_ -match 'PROCESS_TELEMETRY'})
}
$procRows=@()
foreach($line in $telemetry){
  $m=[regex]::Match($line,'file=(\S+) caller=(\S*) ms=(\d+) exit=(-?\d+) stdoutBytes=(\d+) stderrBytes=(\d+)')
  if($m.Success){
    $procRows+=[pscustomobject]@{file=$m.Groups[1].Value;caller=$m.Groups[2].Value;ms=[int]$m.Groups[3].Value;exit=[int]$m.Groups[4].Value;stdoutBytes=[int]$m.Groups[5].Value;stderrBytes=[int]$m.Groups[6].Value}
  }
}
$procSummary=@($procRows|Group-Object file|ForEach-Object{
  [pscustomobject]@{
    file=$_.Name;launches=$_.Count;totalMs=($_.Group.ms|Measure-Object -Sum).Sum;
    p95Ms=Pct ([double[]]$_.Group.ms) .95;outputBytes=($_.Group|ForEach-Object {$_.stdoutBytes+$_.stderrBytes}|Measure-Object -Sum).Sum
  }
}|Sort-Object totalMs -Descending)

$report=[ordered]@{
  generated=(Get-Date).ToString('o')
  performanceLogStartBytes=$perfLogStart
  repeats=$Repeats
  metrics=[ordered]@{
    startupActivatedMs=[ordered]@{median=Pct ([double[]]$startup) .5;p95=Pct ([double[]]$startup) .95;samples=$startup}
    servicesPageMs=[ordered]@{median=Pct ([double[]]$page.services) .5;p95=Pct ([double[]]$page.services) .95;samples=$page.services}
    gpuPageMs=[ordered]@{median=Pct ([double[]]$page.gpu) .5;p95=Pct ([double[]]$page.gpu) .95;samples=$page.gpu}
    overviewReturnMs=[ordered]@{median=Pct ([double[]]$page.overview) .5;p95=Pct ([double[]]$page.overview) .95;samples=$page.overview}
    networkPageMs=[ordered]@{median=Pct ([double[]]$page.network) .5;p95=Pct ([double[]]$page.network) .95;samples=$page.network}
    storageForegroundMs=[ordered]@{median=Pct ([double[]]$page.storage) .5;p95=Pct ([double[]]$page.storage) .95;samples=$page.storage}
    storageColdMs=[ordered]@{median=Pct ([double[]]$storageCold) .5;p95=Pct ([double[]]$storageCold) .95;samples=$storageCold}
    storageRefreshMs=[ordered]@{median=Pct ([double[]]$storageRefresh) .5;p95=Pct ([double[]]$storageRefresh) .95;samples=$storageRefresh}
    gpuDetectColdMs=[ordered]@{median=Pct ([double[]]$gpuCold) .5;p95=Pct ([double[]]$gpuCold) .95;samples=$gpuCold}
    gpuDetectWarmMs=[ordered]@{median=Pct ([double[]]$gpuWarm) .5;p95=Pct ([double[]]$gpuWarm) .95;samples=$gpuWarm}
    taskSchedulerFreshMs=[ordered]@{median=Pct ([double[]]$taskFresh) .5;p95=Pct ([double[]]$taskFresh) .95;samples=$taskFresh}
    taskSchedulerCacheMs=[ordered]@{median=Pct ([double[]]$taskCache) .5;p95=Pct ([double[]]$taskCache) .95;samples=$taskCache}
  }
  network=[ordered]@{
    nicWallMs=[ordered]@{median=Pct ([double[]]$networkWall.nic) .5;p95=Pct ([double[]]$networkWall.nic) .95}
    dnsWallMs=[ordered]@{median=Pct ([double[]]$networkWall.dns) .5;p95=Pct ([double[]]$networkWall.dns) .95}
    mtuWallMs=[ordered]@{median=Pct ([double[]]$networkWall.mtu) .5;p95=Pct ([double[]]$networkWall.mtu) .95}
  }
  soak=$soakResult
  subprocesses=$procSummary
}
$json=Join-Path $outDir 'bench.json'
$jsonTemp=$json+'.tmp.'+[guid]::NewGuid().ToString('N')
$report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $jsonTemp -Encoding utf8
$written=$false
for($attempt=0;$attempt -lt 10 -and -not $written;$attempt++){
  try{
    Move-Item -LiteralPath $jsonTemp -Destination $json -Force
    $written=$true
  }catch{
    if($attempt -ge 9){throw}
    Start-Sleep -Milliseconds (50 + ($attempt*50))
  }
}
Write-Output ("BENCH_OK startup={0}/{1}ms services={2}/{3}ms gpu={4}/{5}ms network={6}/{7}ms storage={8}/{9}ms subprocesses={10}" -f
 $report.metrics.startupActivatedMs.median,$report.metrics.startupActivatedMs.p95,
 $report.metrics.servicesPageMs.median,$report.metrics.servicesPageMs.p95,
 $report.metrics.gpuPageMs.median,$report.metrics.gpuPageMs.p95,
 $report.metrics.networkPageMs.median,$report.metrics.networkPageMs.p95,
 $report.metrics.storageForegroundMs.median,$report.metrics.storageForegroundMs.p95,
 $procRows.Count)
Remove-Item -LiteralPath $sentinel -Force -ErrorAction SilentlyContinue

