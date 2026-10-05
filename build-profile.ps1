param(
  [string]$OutputDir,
  [switch]$FullXaml
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'build-state.ps1')
if([string]::IsNullOrWhiteSpace($OutputDir)){$OutputDir=Join-Path $root 'artifacts\perf\build-profile'}
New-Item -ItemType Directory -Force -Path $OutputDir|Out-Null
$env:DOTNET_CLI_UI_LANGUAGE='en'

$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$hook=Join-Path $root 'build-fast-xaml-reuse.targets'
$binlog=Join-Path $OutputDir 'incremental.binlog'
$textLog=Join-Path $OutputDir 'incremental-performance.txt'

$args=@(
  'build',$app,'-c','Debug','-r','win-x64','--no-restore','--nologo','-m','-v:normal',
  '-clp:PerformanceSummary',("-bl:"+$binlog),
  '-p:RunAnalyzers=false','-p:RunAnalyzersDuringBuild=false',
  '-p:UseSharedCompilation=true','-p:GenerateDocumentationFile=false'
)

$skipProjectRefs=(Test-SpankyCoreFresh)
$args+=('-p:BuildProjectReferences='+$(if($skipProjectRefs){'false'}else{'true'}))

$reuse=$false
if(-not $FullXaml -and (Test-SpankyStructuralFresh) -and (Test-SpankyXamlReuseSafe) -and (Test-Path $hook)){
  $reuse=$true
  $args+='-p:SpankyFastXamlReuse=true'
  $args+=('-p:CustomAfterMicrosoftCommonTargets='+$hook)
}

$sw=[Diagnostics.Stopwatch]::StartNew()
$out=@(& dotnet @args 2>&1)
$exit=$LASTEXITCODE
$sw.Stop()
$out|Set-Content -LiteralPath $textLog -Encoding UTF8

$hot=@($out|Where-Object{$_ -match 'CoreCompile|XamlPreCompile|ResolveProjectReferences|CompileXaml|MarkupCompile|GenerateXbf'})
$metrics=[ordered]@{
  wallMs=[math]::Round($sw.Elapsed.TotalMilliseconds,1)
  exit=$exit
  mode=$(if($FullXaml){'full-xaml'}elseif($reuse){'fast-xaml-reuse'}else{'normal'})
  xamlReuse=$reuse
  projectReferences=$(if($skipProjectRefs){'skip'}else{'build'})
  binlog=$binlog
  hotLines=$hot
}
$json=Join-Path $OutputDir 'build-profile.json'
$metrics|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $json -Encoding UTF8
Write-Output ('BUILD_PROFILE wallMs={0:n1} exit={1} mode={2} projectRefs={3} binlog={4}' -f $metrics.wallMs,$exit,$metrics.mode,$metrics.projectReferences,$binlog)
$hot|Select-Object -First 40|ForEach-Object{Write-Output ('BUILD_PROFILE_HOT '+$_.Trim())}
if($exit -ne 0){exit $exit}
