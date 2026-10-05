param([switch]$Rebuild,[switch]$Run,[switch]$ForceRestore,[switch]$ForceBuild)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'build-state.ps1')

$nativeFresh=Test-SpankyNativeFresh
if(-not $nativeFresh){
  & (Join-Path $root 'build-native.ps1')
  if($LASTEXITCODE -ne 0){throw 'Native build failed.'}
}

$exe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
if(-not $Rebuild -and -not $ForceRestore -and -not $ForceBuild -and (Test-SpankyRunnableFresh)){
  Write-Host 'DEV_BUILD_CACHE_HIT runnable Debug build is fresh'
  if($Run -and (Test-Path $exe)){Start-Process $exe -WorkingDirectory (Split-Path $exe)}
  return
}

$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$xamlReuseTargets=Join-Path $root 'build-fast-xaml-reuse.targets'
function Get-SpankyDevXamlReuseArgs {
  if((Test-SpankyStructuralFresh) -and (Test-SpankyXamlReuseSafe) -and (Test-Path $xamlReuseTargets)){
    return @('-p:SpankyFastXamlReuse=true',('-p:CustomAfterMicrosoftCommonTargets='+$xamlReuseTargets))
  }
  return @()
}
$nuget=Join-Path $root 'NuGet.Config'
$versionProps=Join-Path $root 'Directory.Build.props'
$obj=Join-Path $root 'src\SpankyhoOptimizer\obj\project.assets.json'
$restoreState=Join-Path $script:SpankyCache 'dev-restore.sha256'
$core=Join-Path $root 'src\SpankyhoOptimizer.Core\SpankyhoOptimizer.Core.csproj'
$restoreHash=Get-SpankyRestoreInputHash @($app,$core,$nuget,$versionProps) $versionProps
$oldRestore=(Get-Content $restoreState -Raw -ErrorAction SilentlyContinue).Trim()
$needsRestore=$ForceRestore -or -not(Test-Path $obj) -or ($oldRestore -ne $restoreHash)

if($Rebuild){dotnet clean $app -c Debug -r win-x64 --nologo -v:q | Out-Host}
if($needsRestore){
  $rs=[Diagnostics.Stopwatch]::StartNew()
  dotnet restore $app -r win-x64 --nologo | Out-Host
  if($LASTEXITCODE -ne 0){throw 'Restore failed.'}
  Set-Content $restoreState $restoreHash -NoNewline
  $rs.Stop(); Write-Host ('RESTORE_OK {0:n2}s' -f $rs.Elapsed.TotalSeconds)
}

$sw=[Diagnostics.Stopwatch]::StartNew()
$appOut=Split-Path $exe
$coreDll=Join-Path $appOut 'SpankyhoOptimizer.Core.dll'
$skipProjectRefs=(Test-SpankyCoreFresh) -and (Test-Path $coreDll) -and -not $Rebuild
$projectRefArg=$(if($skipProjectRefs){'-p:BuildProjectReferences=false'}else{'-p:BuildProjectReferences=true'})
Write-Host ('DEV_BUILD_PROJECT_REFS ' + $(if($skipProjectRefs){'skip'}else{'build'}))
$stagedDebugDir=Join-Path $root '.buildcache\staged-debug'
$stagedExe=Join-Path $stagedDebugDir 'SpankyhoOptimizer.exe'
function Invoke-StagedDevBuild {
  New-Item -ItemType Directory -Path $stagedDebugDir -Force | Out-Null
  Write-Host ('DEV_BUILD_LOCK_STAGED ' + $stagedDebugDir)
  $xamlReuseArgs=@(Get-SpankyDevXamlReuseArgs)
  if($xamlReuseArgs.Count){Write-Host 'DEV_XAML_REUSE staged=on'}
  # A separate output directory must contain matching project-reference assemblies.
  # Reusing normal-output reference freshness can leave an older Core DLL here.
  dotnet build $app -c Debug -r win-x64 --no-restore --nologo -m -v:minimal -o $stagedDebugDir `
    -p:BuildInParallel=true -p:DebugType=embedded -p:DebugSymbols=true `
    -p:UseSharedCompilation=true -p:RunAnalyzersDuringBuild=false -p:BuildProjectReferences=true @xamlReuseArgs | Out-Host
  if($LASTEXITCODE -ne 0){throw 'Staged Dev build failed.'}
  Set-Content (Join-Path $root '.buildcache\staged-debug.fp') (Get-SpankyFingerprint) -NoNewline
  Set-Content (Join-Path $root '.buildcache\staged-debug.meta') (Get-SpankyMetadataFingerprint) -NoNewline
}
$debugOutputLocked=$false
if(Test-Path $coreDll){
  $lockProbe=$null
  try {$lockProbe=[IO.File]::Open($coreDll,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
  catch {$debugOutputLocked=$true}
  finally {if($lockProbe){$lockProbe.Dispose()}}
}
if($debugOutputLocked){
  Write-Host 'DEV_BUILD_LOCK_PREDETECTED'
  Invoke-StagedDevBuild
  $sw.Stop(); Write-Host ('DEV_BUILD_STAGED_LOCK_OK {0:n2}s' -f $sw.Elapsed.TotalSeconds)
  if($Run -and (Test-Path $stagedExe)){Start-Process $stagedExe -WorkingDirectory $stagedDebugDir}
  return
}
$xamlReuseArgs=@(Get-SpankyDevXamlReuseArgs)
if($xamlReuseArgs.Count){Write-Host 'DEV_XAML_REUSE on'}
$buildOutput=@(dotnet build $app -c Debug -r win-x64 --no-restore --nologo -m -v:minimal `
  -p:BuildInParallel=true -p:DebugType=embedded -p:DebugSymbols=true `
  -p:UseSharedCompilation=true -p:RunAnalyzersDuringBuild=false `
  -p:CopyRetryCount=0 -p:CopyRetryDelayMilliseconds=0 $projectRefArg @xamlReuseArgs 2>&1)
$buildExit=$LASTEXITCODE
$buildText=$buildOutput -join "`n"
$buildOutput | Out-Host
if($buildExit -ne 0 -and $buildText -match 'MSB3021|MSB3027'){
  Invoke-StagedDevBuild
  $sw.Stop(); Write-Host ('DEV_BUILD_STAGED_LOCK_OK {0:n2}s' -f $sw.Elapsed.TotalSeconds)
  if($Run -and (Test-Path $stagedExe)){Start-Process $stagedExe -WorkingDirectory $stagedDebugDir}
  return
}
if($buildExit -ne 0){throw 'Dev build failed.'}
Save-SpankyFullBuildState
$sw.Stop(); Write-Host ('DEV_BUILD_OK {0:n2}s' -f $sw.Elapsed.TotalSeconds)
if($Run -and (Test-Path $exe)){Start-Process $exe -WorkingDirectory (Split-Path $exe)}

