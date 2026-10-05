param([switch]$Full)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$outDir=Join-Path $root '.perf\analyzers'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$log=Join-Path $outDir 'latest.log'
$proj=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$args=@(
  'build',$proj,'-c','Release','--no-restore',
  '-p:RunAnalyzersDuringBuild=true',
  '-p:AnalysisLevel=latest-recommended',
  '-p:TreatWarningsAsErrors=false',
  '-v:minimal'
)
$sw=[Diagnostics.Stopwatch]::StartNew()
$out=& dotnet @args 2>&1 | Tee-Object -FilePath $log
$code=$LASTEXITCODE
$sw.Stop()
$selected=$out | Where-Object {$_ -match 'CA(1849|1851|1869|2000|2012|2016|2213)'}
$selected | Set-Content (Join-Path $outDir 'selected.log') -Encoding utf8
Write-Output ("PERF_ANALYZERS exit={0} ms={1:n0} selected={2}" -f $code,$sw.Elapsed.TotalMilliseconds,@($selected).Count)
if($code -ne 0){exit $code}
