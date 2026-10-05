param([int]$Seconds=8)
$ErrorActionPreference='Stop'
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $admin){
  Write-Output 'PERF_TOOL_SKIP_NEEDS_ELEVATION'
  exit 0
}
$root=$PSScriptRoot
$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$outDir=Join-Path $root '.perf\latest'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$out=Join-Path $outDir 'runtime-counters.csv'
$duration=('00:00:00:{0:00}' -f [math]::Max(2,$Seconds))
$c='System.Runtime[dotnet.process.cpu.time,dotnet.gc.heap.total_allocated,dotnet.gc.last_collection.heap.size,dotnet.gc.pause.time,dotnet.thread_pool.thread.count,dotnet.thread_pool.queue.length,dotnet.monitor.lock_contentions]'
& dotnet-counters collect --refresh-interval 1 --duration $duration --format csv --output $out --counters $c -- $exe --full-smoke *> (Join-Path $outDir 'runtime-counters.log')
$code=$LASTEXITCODE
Write-Output ("PERF_COUNTERS_OK exit={0} file={1}" -f $code,$out)
if($code -ne 0){exit $code}
