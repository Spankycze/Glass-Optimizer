param([switch]$ForceCompile,[string]$OutputDir)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
if([string]::IsNullOrWhiteSpace($OutputDir)){$OutputDir=Join-Path $root ('artifacts\build-perf\'+(Get-Date -Format 'yyyyMMdd_HHmmss'))}
New-Item -ItemType Directory -Force -Path $OutputDir|Out-Null
function Invoke-BuildPerf([string]$name,[switch]$Touch){
  if($Touch){$probe=Join-Path $root 'src\SpankyhoOptimizer\BuildInfo.cs';if(-not(Test-Path $probe)){$probe=Join-Path $root 'src\SpankyhoOptimizer\MainWindow.cs'};(Get-Item $probe).LastWriteTimeUtc=Get-Date}
  $bin=Join-Path $OutputDir ($name+'.binlog');$log=Join-Path $OutputDir ($name+'.txt')
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $out=@(dotnet build $app -c Debug -r win-x64 --no-restore --nologo -m -v:minimal -bl:$bin -clp:PerformanceSummary -p:RunAnalyzers=false -p:RunAnalyzersDuringBuild=false -p:UseSharedCompilation=true -p:GenerateDocumentationFile=false 2>&1)
  $exit=$LASTEXITCODE;$sw.Stop();$out|Set-Content -LiteralPath $log -Encoding UTF8
  if($exit -ne 0){throw ('build perf '+$name+' failed')}
  $targets=@{};foreach($line in $out){if($line -match '^\s*(\d+)\s+ms\s+(.+?)\s+\d+\s+\S+\s*$'){ $targets[$matches[2].Trim()]=[int]$matches[1] }}
  [pscustomobject]@{name=$name;elapsedMs=[math]::Round($sw.Elapsed.TotalMilliseconds,1);targets=$targets;binlog=$bin;log=$log}
}
$actual=Invoke-BuildPerf 'incremental' -Touch:$ForceCompile
$noop=Invoke-BuildPerf 'noop'
$keys='CoreCompile','XamlPreCompile','ResolveProjectReferences','Compile','XamlMarkupCompilePass1','XamlMarkupCompilePass2'
Write-Output ('BUILD_PERF incremental={0:n1}ms noop={1:n1}ms' -f $actual.elapsedMs,$noop.elapsedMs)
foreach($k in $keys){$a=if($actual.targets.ContainsKey($k)){$actual.targets[$k]}else{0};$n=if($noop.targets.ContainsKey($k)){$noop.targets[$k]}else{0};if($a -or $n){Write-Output ('BUILD_PERF_TARGET {0} incremental={1}ms noop={2}ms' -f $k,$a,$n)}}
$report=[pscustomobject]@{generatedAt=(Get-Date).ToString('o');incremental=$actual;noop=$noop}
$report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $OutputDir 'report.json') -Encoding UTF8
Write-Output ('BUILD_PERF_OK dir='+$OutputDir)