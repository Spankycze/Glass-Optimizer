param([switch]$Etw,[string]$Filter='*NetworkHotPathBenchmarks*')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$proj=Join-Path $root 'benchmarks\SpankyhoOptimizer.Benchmarks\SpankyhoOptimizer.Benchmarks.csproj'
$outDir=Join-Path $root ('artifacts\benchmarks\'+(Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$args=@('run','-c','Release','--project',$proj,'--','--filter',$Filter,'--artifacts',$outDir,'--exporters','json')
if($Etw){$args+=@('--profiler','ETW')}
& dotnet @args
if($LASTEXITCODE -ne 0){throw 'BenchmarkDotNet failed'}
Write-Output ('BENCHMARKS_OK '+$outDir)
