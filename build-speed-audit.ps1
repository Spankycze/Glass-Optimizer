param([double]$CacheHitBudgetMs=800,[double]$CodeOnlyBudgetMs=12000)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
. (Join-Path $root 'build-state.ps1')
$buildFast=Join-Path $root 'build-fast.ps1'
$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$probe=Join-Path $root 'src\SpankyhoOptimizer\MainWindow.WindowsModernizer.cs'
$outDll=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.dll'
$hook=Join-Path $root 'build-fast-xaml-reuse.targets'

$sw=[Diagnostics.Stopwatch]::StartNew()
& $buildFast | Out-Null
$sw.Stop()
$cacheMs=[math]::Round($sw.Elapsed.TotalMilliseconds,1)
if($cacheMs -gt $CacheHitBudgetMs){throw ('BUILD_SPEED_CACHE_FAIL '+$cacheMs+'ms > '+$CacheHitBudgetMs+'ms')}

if(-not(Test-SpankyStructuralFresh) -or -not(Test-SpankyXamlReuseSafe)){
  Write-Output ('BUILD_SPEED_REUSE_SKIP safe=false cacheMs='+$cacheMs)
  exit 0
}
if(-not(Test-Path $probe) -or -not(Test-Path $outDll) -or -not(Test-Path $hook)){throw 'BUILD_SPEED prerequisites missing'}

$baseArgs=@(
  'build',$app,'-c','Debug','-r','win-x64','--no-restore','--nologo','-m','-v:q',
  '-p:RunAnalyzers=false','-p:RunAnalyzersDuringBuild=false',
  '-p:UseSharedCompilation=true','-p:GenerateDocumentationFile=false',
  '-p:BuildProjectReferences=false'
)
$oldStamp=(Get-Item $probe).LastWriteTimeUtc
try{
  (Get-Item $probe).LastWriteTimeUtc=Get-Date
  $reuseArgs=@($baseArgs)+@('-p:SpankyFastXamlReuse=true',('-p:CustomAfterMicrosoftCommonTargets='+$hook))
  $sw=[Diagnostics.Stopwatch]::StartNew()
  & dotnet @reuseArgs | Out-Null
  $reuseExit=$LASTEXITCODE
  $sw.Stop()
  if($reuseExit -ne 0){throw ('BUILD_SPEED_REUSE_BUILD_FAIL exit='+$reuseExit)}
  $codeMs=[math]::Round($sw.Elapsed.TotalMilliseconds,1)
  $reuseHash=(Get-FileHash $outDll -Algorithm SHA256).Hash

  Start-Sleep -Milliseconds 80
  (Get-Item $probe).LastWriteTimeUtc=Get-Date
  $sw=[Diagnostics.Stopwatch]::StartNew()
  & dotnet @baseArgs | Out-Null
  $normalExit=$LASTEXITCODE
  $sw.Stop()
  if($normalExit -ne 0){throw ('BUILD_SPEED_NORMAL_BUILD_FAIL exit='+$normalExit)}
  $normalMs=[math]::Round($sw.Elapsed.TotalMilliseconds,1)
  $normalHash=(Get-FileHash $outDll -Algorithm SHA256).Hash
}finally{
  (Get-Item $probe).LastWriteTimeUtc=$oldStamp
}
if($reuseHash -ne $normalHash){throw 'BUILD_SPEED_BINARY_MISMATCH'}
if($codeMs -gt $CodeOnlyBudgetMs){throw ('BUILD_SPEED_CODE_ONLY_FAIL '+$codeMs+'ms > '+$CodeOnlyBudgetMs+'ms')}
$gain=[math]::Round((1-($codeMs/[math]::Max(1,$normalMs)))*100,1)
Write-Output ('BUILD_SPEED_OK cacheMs='+$cacheMs+' reuseMs='+$codeMs+' normalMs='+$normalMs+' gainPct='+$gain+' binaryEqual=true')
