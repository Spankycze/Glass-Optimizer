param([int]$Repeats=3)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$outDir=Join-Path $root '.perf\latest'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$appLog='C:\ProgramData\SpankyhoOptimizer\optimizer.log'
$perfLog='C:\ProgramData\SpankyhoOptimizer\performance.log'
$nicLog='C:\ProgramData\SpankyhoOptimizer\nic.log'
$networkLog='C:\ProgramData\SpankyhoOptimizer\network.log'
$procLog='C:\ProgramData\SpankyhoOptimizer\process-telemetry.log'
$procStart=if(Test-Path $procLog){(Get-Item $procLog).Length}else{0}
$env:SPANKY_PROCESS_TELEMETRY='1'
$sentinel=Join-Path $env:TEMP 'SpankyOptimizer.PerfTelemetry.enabled'
Set-Content -LiteralPath $sentinel -Value ((Get-Date).ToString('o')) -Encoding ascii

function Pct([double[]]$a,[double]$p){
  $a=@($a|Where-Object {$null -ne $_})
  if($a.Count -eq 0){return $null}
  $s=@($a|Sort-Object);$i=[math]::Min($s.Count-1,[math]::Max(0,[math]::Ceiling($p*$s.Count)-1))
  [math]::Round([double]$s[$i],1)
}
function Latest([string]$path,[string]$marker){
  if(-not(Test-Path $path)){return $null}
  Get-Content $path -Tail 600|Where-Object {$_ -like "*$marker*"}|Select-Object -Last 1
}
function Field([string]$line,[string]$name){
  if([string]::IsNullOrWhiteSpace($line)){return $null}
  $m=[regex]::Match($line,'(?:^|\s)'+[regex]::Escape($name)+'=(-?\d+(?:[\.,]\d+)?)')
  if($m.Success){[double]::Parse($m.Groups[1].Value.Replace(',','.'),[Globalization.CultureInfo]::InvariantCulture)}
}
function Run([string]$arg){
  $sw=[Diagnostics.Stopwatch]::StartNew();$p=Start-Process $exe -ArgumentList $arg -Wait -PassThru;$sw.Stop()
  if($p.ExitCode -ne 0){throw "$arg exit=$($p.ExitCode)"}
  [math]::Round($sw.Elapsed.TotalMilliseconds,1)
}

$page=@();$nic=@();$dnsLatency=@();$dnsElapsed=@();$dnsReadbackWall=@();$mtuRead=@();$mtuProbe=@()
$currentMtu=$null;$recommendedMtu=$null;$conclusive=$null
1..$Repeats|ForEach-Object{
  [void](Run '--performance-smoke')
  $page+=Field (Latest $perfLog 'NETWORK_BUILD') 'totalMs'

  [void](Run '--nic-capability-smoke')
  $nic+=Field (Latest $nicLog 'NIC_CAPABILITY_SMOKE_OK') 'elapsedMs'

  [void](Run '--dns-benchmark-smoke')
  $dnsLine=Latest $appLog 'DNS_BENCHMARK_SMOKE_OK'
  $dnsLatency+=Field $dnsLine 'avgMs';$dnsElapsed+=Field $dnsLine 'elapsedMs'

  $dnsReadbackWall+=Run '--dns-readback-guard-smoke'

  [void](Run '--mtu-smoke')
  $mtuRead+=Field (Latest $appLog 'MTU_READBACK_SMOKE_OK') 'elapsedMs'

  [void](Run '--mtu-probe-smoke')
  $mtuLine=Latest $networkLog 'MTU_PROBE_SMOKE_OK'
  $mtuProbe+=Field $mtuLine 'elapsedMs'
  $currentMtu=Field $mtuLine 'current';$recommendedMtu=Field $mtuLine 'recommended'
  if($mtuLine -match 'conclusive=(True|False)'){$conclusive=$matches[1]}
}

$procRows=@()
if(Test-Path $procLog){
  $fs=[IO.File]::Open($procLog,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
  try{
    if($procStart -le $fs.Length){[void]$fs.Seek($procStart,[IO.SeekOrigin]::Begin)}
    $sr=[IO.StreamReader]::new($fs,[Text.Encoding]::UTF8,$true,4096,$true);$txt=$sr.ReadToEnd();$sr.Dispose()
  }finally{$fs.Dispose()}
  foreach($line in @($txt -split "\r?\n")){
    $m=[regex]::Match($line,'PROCESS_TELEMETRY file=(\S+) caller=(\S*) ms=(\d+) exit=(-?\d+) stdoutBytes=(\d+) stderrBytes=(\d+)')
    if($m.Success){$procRows+=[pscustomobject]@{file=$m.Groups[1].Value;caller=$m.Groups[2].Value;ms=[int]$m.Groups[3].Value}}
  }
}
$report=[ordered]@{
 generated=(Get-Date).ToString('o');repeats=$Repeats
 pageMs=[ordered]@{median=Pct ([double[]]$page) .5;p95=Pct ([double[]]$page) .95}
 nicCapabilityMs=[ordered]@{median=Pct ([double[]]$nic) .5;p95=Pct ([double[]]$nic) .95}
 dnsResolverMs=[ordered]@{p50=Pct ([double[]]$dnsLatency) .5;p95=Pct ([double[]]$dnsLatency) .95}
 dnsBenchmarkMs=[ordered]@{median=Pct ([double[]]$dnsElapsed) .5;p95=Pct ([double[]]$dnsElapsed) .95}
 dnsReadbackGuardWallMs=[ordered]@{median=Pct ([double[]]$dnsReadbackWall) .5;p95=Pct ([double[]]$dnsReadbackWall) .95}
 mtuReadbackMs=[ordered]@{median=Pct ([double[]]$mtuRead) .5;p95=Pct ([double[]]$mtuRead) .95}
 mtuProbeMs=[ordered]@{median=Pct ([double[]]$mtuProbe) .5;p95=Pct ([double[]]$mtuProbe) .95;current=$currentMtu;recommended=$recommendedMtu;conclusive=$conclusive}
 subprocesses=[ordered]@{total=$procRows.Count;powershell=@($procRows|Where-Object {$_.file -ieq 'powershell.exe'}).Count;pwsh=@($procRows|Where-Object {$_.file -ieq 'pwsh.exe'}).Count}
}
$report|ConvertTo-Json -Depth 6|Set-Content (Join-Path $outDir 'network.json') -Encoding utf8
Remove-Item -LiteralPath $sentinel -Force -ErrorAction SilentlyContinue
Write-Output ("NETWORK_BENCH_OK page={0}/{1}ms nic={2}/{3}ms dns={4}/{5}ms dnsBench={6}/{7}ms mtuRead={8}/{9}ms mtuProbe={10}/{11}ms mtu={12}->{13} conclusive={14} proc={15}" -f
 $report.pageMs.median,$report.pageMs.p95,$report.nicCapabilityMs.median,$report.nicCapabilityMs.p95,
 $report.dnsResolverMs.p50,$report.dnsResolverMs.p95,$report.dnsBenchmarkMs.median,$report.dnsBenchmarkMs.p95,
 $report.mtuReadbackMs.median,$report.mtuReadbackMs.p95,$report.mtuProbeMs.median,$report.mtuProbeMs.p95,
 $currentMtu,$recommendedMtu,$conclusive,$procRows.Count)
