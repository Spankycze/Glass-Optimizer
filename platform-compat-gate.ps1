$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$app=Join-Path $root 'src\SpankyhoOptimizer.Core\SpankyhoOptimizer.Core.csproj'
$outDir=Join-Path $root 'artifacts\quality\platform'
New-Item -ItemType Directory -Force -Path $outDir|Out-Null
$log=Join-Path $outDir 'latest.log'
$args=@('build',$app,'-c','Release','-r','win-x64','--nologo','-p:EnableNETAnalyzers=true','-p:AnalysisLevel=latest-all','-p:RunAnalyzersDuringBuild=true','-p:TreatWarningsAsErrors=false')
$out=@(& dotnet @args 2>&1);$code=$LASTEXITCODE
$out|Set-Content $log -Encoding UTF8
$hits=@($out|Where-Object{$_ -match ': (warning|error) CA(1416|1422):'}|Sort-Object -Unique)
$hits|Select-Object -First 80|ForEach-Object{Write-Output ('PLATFORM_FINDING '+$_)}
if($code -ne 0){Write-Output ('PLATFORM_BUILD_FAIL findings='+$hits.Count+' log='+$log);exit $code}
if($hits.Count -gt 0){Write-Output ('PLATFORM_COMPAT_FAIL findings='+$hits.Count+' log='+$log);exit 42}
Write-Output ('PLATFORM_COMPAT_OK findings=0 log='+$log)
