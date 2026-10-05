param([string]$Project='')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'dev-perf-env.ps1')
if((Split-Path $PSScriptRoot -Leaf) -eq 'RAM Guard'){
  Write-Host 'MSBUILD_PERF_SKIP=not-dotnet-primary'
  exit 0
}
if([string]::IsNullOrWhiteSpace($Project)){
  $Project=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
}
$out=Join-Path $PSScriptRoot 'artifacts\perf'
New-Item -ItemType Directory -Force $out|Out-Null
$binlog=Join-Path $out 'msbuild-perf.binlog'
$txt=Join-Path $out 'msbuild-performance.txt'
dotnet build $Project -c Debug --no-restore --nologo -m -v:minimal -bl:$binlog -clp:PerformanceSummary *> $txt
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
Write-Host ('MSBUILD_PERF_OK binlog='+$binlog)
