param([string]$Mode='startup',[int]$Repetitions=15)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$perf='C:\ProgramData\SpankyhoOptimizer\performance.log'
$app='C:\ProgramData\SpankyhoOptimizer\optimizer.log'
function Median([double[]]$v){$s=@($v|Sort-Object);if($s.Count%2){return [double]$s[[int]($s.Count/2)]};return ([double]$s[$s.Count/2-1]+[double]$s[$s.Count/2])/2}
function P95([double[]]$v){$s=@($v|Sort-Object);$i=[Math]::Max(0,[Math]::Min($s.Count-1,[Math]::Ceiling($s.Count*0.95)-1));return [double]$s[$i]}
function Run([string]$arg,[int]$timeout=120){$p=Start-Process $exe -ArgumentList $arg -PassThru;if(-not $p.WaitForExit($timeout*1000)){try{$p.Kill($true)}catch{};throw 'timeout '+$arg};if($p.ExitCode -ne 0){throw ('{0} exit={1}' -f $arg,$p.ExitCode)}}
function Last([string]$path,[string]$pat){((Select-String -Path $path -Pattern $pat|Select-Object -Last 1).Line)}
function Kv([string]$line,[string]$key){$m=[regex]::Match($line,[regex]::Escape($key)+'=(-?\d+(?:[\.,]\d+)?)');if(-not $m.Success){return 0};return [double]::Parse($m.Groups[1].Value.Replace(',','.'),[Globalization.CultureInfo]::InvariantCulture)}
$values=New-Object System.Collections.Generic.List[double]
1..$Repetitions|ForEach-Object{
 switch($Mode.ToLowerInvariant()){
  'startup' {Run '--startup-hotpath-smoke' 30;$values.Add((Kv (Last $app 'STARTUP_HOTPATH_SMOKE_OK') 'activatedMs'))|Out-Null}
  'network-page' {Run '--performance-smoke' 120;$values.Add((Kv (Last $perf 'PERF_COLD ') 'networkMs'))|Out-Null}
  'services-page' {Run '--performance-smoke' 120;$values.Add((Kv (Last $perf 'PERF_COLD ') 'servicesMs'))|Out-Null}
  'storage-page' {Run '--performance-smoke' 120;$values.Add((Kv (Last $perf 'PERF_COLD ') 'storageMs'))|Out-Null}
  'gpu-page' {Run '--performance-smoke' 120;$values.Add((Kv (Last $perf 'PERF_COLD ') 'gpuMs'))|Out-Null}
  'dns-readback' {Run '--dns-readback-guard-smoke' 30;$values.Add((Kv (Last $app 'DNS_READBACK_GUARD_SMOKE_OK') 'elapsedMs'))|Out-Null}
  'nic-capability' {Run '--nic-capability-smoke' 30;$values.Add((Kv (Last $app 'NIC_CAPABILITY_SMOKE_OK') 'elapsedMs'))|Out-Null}
  'dns-benchmark' {Run '--dns-benchmark-smoke' 45;$values.Add((Kv (Last $app 'DNS_BENCHMARK_SMOKE_OK') 'elapsedMs'))|Out-Null}
  'process-runner' {Run '--process-runner-bench' 60;$values.Add((Kv (Last $app 'PROCESS_RUNNER_BENCH_OK') 'medianMs'))|Out-Null}
  default {throw 'Unknown BENCH mode: '+$Mode}
 }
}
$med=[math]::Round((Median $values),1);$p95=[math]::Round((P95 $values),1)
Write-Output ('BENCH {0} n={1} median={2}ms p95={3}ms samples=[{4}]' -f $Mode,$values.Count,$med,$p95,(($values|ForEach-Object{[math]::Round($_,1)}) -join ','))