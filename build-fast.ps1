param([switch]$ForceFull)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
function Write-BuildStatus([string]$Kind,[string]$Text){
  $icon=if($Kind -eq 'OK'){[char]0x2713}elseif($Kind -eq 'WARN'){'!'}elseif($Kind -eq 'FAIL'){'X'}else{'>'}
  $color=if($Kind -eq 'OK'){'Green'}elseif($Kind -eq 'WARN'){'Yellow'}elseif($Kind -eq 'FAIL'){'Red'}else{'Cyan'}
  Write-Host '  [' -NoNewline -ForegroundColor DarkGray
  Write-Host 'BUILD' -NoNewline -ForegroundColor Cyan
  Write-Host '] ' -NoNewline -ForegroundColor DarkGray
  Write-Host "$icon " -NoNewline -ForegroundColor $color
  Write-Host $Text -ForegroundColor White
}

$root=Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $root 'ai-automation-contract-audit.ps1')
$auditOk=$?
if(-not $auditOk){throw 'AI automation contract audit failed'}
. (Join-Path $root 'build-state.ps1')
$debugExe=Join-Path $root 'src\SpankyhoOptimizer\bin\Debug\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
$debugExeFull=[IO.Path]::GetFullPath($debugExe)
$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$core=Join-Path $root 'src\SpankyhoOptimizer.Core\SpankyhoOptimizer.Core.csproj'
$appOut=Split-Path $debugExe
$coreOut=Join-Path $root 'src\SpankyhoOptimizer.Core\bin\Debug\net10.0-windows10.0.26100.0'
$xamlReuseTargets=Join-Path $root 'build-fast-xaml-reuse.targets'
function Get-SpankyXamlReuseArgs {
  if((Test-SpankyStructuralFresh) -and (Test-SpankyXamlReuseSafe) -and (Test-Path $xamlReuseTargets)){
    return @('-p:SpankyFastXamlReuse=true',('-p:CustomAfterMicrosoftCommonTargets='+$xamlReuseTargets))
  }
  return @()
}
function Stop-SpankyDebugProcesses {
  $running=@(Get-Process -Name 'SpankyhoOptimizer' -ErrorAction SilentlyContinue)
  $paths=@{}
  foreach($proc in $running){
    try {
      if($proc.Path){$paths[$proc.Path]=$true}
    } catch {}
  }
  foreach($path in @($paths.Keys)){
    try {
      Write-Host ('FAST_BUILD_REQUEST_EXIT path={0}' -f $path)
      Start-Process -FilePath $path -ArgumentList '--request-exit' -WindowStyle Hidden -ErrorAction SilentlyContinue | Out-Null
    } catch {}
  }
  if($running.Count -gt 0){
    for($i=0;$i -lt 20;$i++){
      Start-Sleep -Milliseconds 100
      if(@(Get-Process -Name 'SpankyhoOptimizer' -ErrorAction SilentlyContinue).Count -eq 0){break}
    }
  }

  $byPid=@{}
  $needsCimFallback=$false
  Get-Process -Name 'SpankyhoOptimizer' -ErrorAction SilentlyContinue | ForEach-Object {
    $candidatePath=$null
    try { $candidatePath=$_.Path } catch { $needsCimFallback=$true }
    if($candidatePath){
      try {
        if([IO.Path]::GetFullPath($candidatePath) -eq $debugExeFull){$byPid[$_.Id]=$true}
      } catch { $needsCimFallback=$true }
    } else {
      $needsCimFallback=$true
    }
  }
  if($byPid.Count -eq 0 -and $needsCimFallback){
    Get-CimInstance Win32_Process -Filter "Name='SpankyhoOptimizer.exe'" -ErrorAction SilentlyContinue | ForEach-Object {
      $candidatePath=$_.ExecutablePath
      if(-not $candidatePath -and $_.CommandLine -and $_.CommandLine.Contains($debugExeFull,[StringComparison]::OrdinalIgnoreCase)){
        $candidatePath=$debugExeFull
      }
      if($candidatePath){
        try {
          if([IO.Path]::GetFullPath($candidatePath) -eq $debugExeFull){$byPid[[int]$_.ProcessId]=$true}
        } catch {}
      }
    }
  }
  foreach($processId in @($byPid.Keys)){
    Write-Host ('FAST_BUILD_STOP_DEBUG pid={0}' -f $processId)
    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
  }
  if($byPid.Count -gt 0){Start-Sleep -Milliseconds 120}
}
Stop-SpankyDebugProcesses
function Assert-SpankyDebugOutputUnlocked {
  $probe=Join-Path (Split-Path $debugExe) 'SpankyhoOptimizer.Core.dll'
  if(-not(Test-Path $probe)){return}
  $stream=$null
  try {
    $stream=[IO.File]::Open($probe,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
  } catch {
    $pids=@(Get-Process -Name 'SpankyhoOptimizer' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $suffix=$(if($pids.Count -gt 0){' pids='+($pids -join ',')}else{''})
    throw ('Debug output is locked by a running SpankyhoOptimizer instance; close the elevated/debug app before rebuilding.'+$suffix)
  } finally {
    if($stream){$stream.Dispose()}
  }
}
function Test-SpankyDebugProcessStillRunning {
  return @(Get-Process -Name 'SpankyhoOptimizer' -ErrorAction SilentlyContinue).Count -gt 0
}
$stagedDebugDir=Join-Path $root '.buildcache\staged-debug'
$stagedDebugExe=Join-Path $stagedDebugDir 'SpankyhoOptimizer.exe'
$stagedDebugFingerprint=Join-Path $root '.buildcache\staged-debug.fp'
$stagedDebugMetadata=Join-Path $root '.buildcache\staged-debug.meta'
function Test-SpankyStagedDebugFresh {
  if(-not(Test-Path $stagedDebugExe) -or -not(Test-Path $stagedDebugFingerprint)){return $false}
  try {
    $saved=(Get-Content $stagedDebugFingerprint -Raw).Trim()
    if(-not $saved){return $false}
    return $saved -eq (Get-SpankyFingerprint)
  } catch { return $false }
}
function Save-SpankyStagedDebugState {
  Set-Content $stagedDebugFingerprint (Get-SpankyFingerprint) -NoNewline
  Set-Content $stagedDebugMetadata (Get-SpankyMetadataFingerprint) -NoNewline
}
function Invoke-StagedDebugBuild {
  if(Test-SpankyStagedDebugFresh){
    Write-BuildStatus 'OK' 'staged cache-hit'
    return
  }
  New-Item -ItemType Directory -Path $stagedDebugDir -Force | Out-Null
  $stageProjectRefs=$(if(Test-SpankyCoreFresh){'-p:BuildProjectReferences=false'}else{'-p:BuildProjectReferences=true'})
  Write-Host ('FAST_BUILD_STAGED_OUTPUT ' + $stagedDebugDir)
  Write-Host ('FAST_BUILD_STAGED_PROJECT_REFS ' + $(if(Test-SpankyCoreFresh){'skip'}else{'build'}))
  $xamlReuseArgs=@(Get-SpankyXamlReuseArgs)
  if($xamlReuseArgs.Count){Write-Host 'FAST_XAML_REUSE staged=on'}
  dotnet build $app -c Debug -r win-x64 --no-restore --nologo -m -v:q -o $stagedDebugDir `
    -p:RunAnalyzers=false -p:RunAnalyzersDuringBuild=false `
    -p:UseSharedCompilation=true -p:GenerateDocumentationFile=false $stageProjectRefs @xamlReuseArgs | Out-Host
  if($LASTEXITCODE -ne 0){throw 'Staged Debug build failed.'}
  Save-SpankyStagedDebugState
  Write-Host 'FAST_BUILD_STAGED_OK'
}
function Invoke-FullFallback([string]$reason){
  Write-Host ('FAST_FALLBACK_FULL ' + $reason)
  $scriptPath=Join-Path $root 'build-dev.ps1'
  $psi=[Diagnostics.ProcessStartInfo]::new()
  $psi.FileName='powershell.exe'
  $psi.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$scriptPath+'" -ForceBuild'
  $psi.UseShellExecute=$false
  $psi.CreateNoWindow=$true
  $p=[Diagnostics.Process]::new()
  $p.StartInfo=$psi
  if(-not $p.Start()){throw 'Full fallback child process failed to start.'}
  $watch=[Diagnostics.Stopwatch]::StartNew()
  while(-not $p.WaitForExit(15000)){
    Write-Host ('FAST_FALLBACK_HEARTBEAT elapsed={0:n0}s pid={1}' -f $watch.Elapsed.TotalSeconds,$p.Id)
    if($watch.Elapsed.TotalSeconds -ge 120){
      try{$p.Kill($true)}catch{try{Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}catch{}}
      throw ('Full fallback watchdog timeout after {0:n0}s.' -f $watch.Elapsed.TotalSeconds)
    }
  }
  $exitCode=$p.ExitCode
  $watch.Stop()
  $p.Dispose()
  if($exitCode -ne 0){throw ('Full fallback build failed. exit={0}' -f $exitCode)}
  Write-BuildStatus 'OK' ('full fallback {0:n2}s' -f $watch.Elapsed.TotalSeconds)
}
if($ForceFull){Invoke-FullFallback 'explicit force'; return}

$nativeFresh=Test-SpankyNativeFresh
if($nativeFresh -and (Test-SpankyRunnableFresh)){
  
  Write-BuildStatus 'OK' 'cache-hit'
  return
}

if(-not $nativeFresh){
  & (Join-Path $root 'build-native.ps1')
  if($LASTEXITCODE -ne 0){throw 'Native build failed.'}
}
if(Test-SpankyRunnableFresh){Write-BuildStatus 'OK' 'cache-hit'; return}
if((Test-SpankyDebugProcessStillRunning) -and (Test-SpankyStagedDebugFresh)){
  Write-BuildStatus 'OK' 'staged cache-hit'
  return
}

# A normal debug instance can be stopped and rebuilt in-place. Elevated instances
# cannot be terminated from the non-elevated build shell, so switch to the existing
# staged output before any lock assertion or in-place Core/App copy can fail.
Stop-SpankyDebugProcesses
if(Test-SpankyDebugProcessStillRunning){
  Write-BuildStatus 'WARN' 'debug output locked; using staged build'
  Invoke-StagedDebugBuild
  return
}

if(-not(Test-SpankyStructuralFresh)){
  Stop-SpankyDebugProcesses
  if(Test-SpankyDebugProcessStillRunning){
    Write-Host 'FAST_STRUCTURAL_STAGED running SpankyhoOptimizer still owns output'
    Invoke-StagedDebugBuild
    return
  }
  Invoke-FullFallback 'structural/XAML change'
  return
}
$apiProbeProj=Join-Path $root 'tools\PublicApiProbe\PublicApiProbe.csproj'
$apiProbeSrc=Join-Path $root 'tools\PublicApiProbe\Program.cs'
$apiProbe=Join-Path $root 'tools\PublicApiProbe\bin\Release\net10.0-windows10.0.26100.0\PublicApiProbe.dll'
function Get-PublicApiHash([string]$dll){
  if(-not(Test-Path $apiProbe) -or (Get-Item $apiProbeSrc).LastWriteTimeUtc -gt (Get-Item $apiProbe).LastWriteTimeUtc -or (Get-Item $apiProbeProj).LastWriteTimeUtc -gt (Get-Item $apiProbe).LastWriteTimeUtc){
    dotnet build $apiProbeProj -c Release --nologo -v:q | Out-Host
    if($LASTEXITCODE -ne 0){return $null}
  }
  $out=& dotnet $apiProbe $dll 2>$null
  if($LASTEXITCODE -ne 0 -or -not $out){return $null}
  return (($out | Select-Object -Last 1).ToString().Trim())
}
if((Test-SpankyUiFresh) -and -not(Test-SpankyCoreFresh) -and (Test-Path $debugExe)){
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $oldCore=Join-Path $appOut 'SpankyhoOptimizer.Core.dll'
  Assert-SpankyDebugOutputUnlocked
  $oldApi=$(if(Test-Path $oldCore){Get-PublicApiHash $oldCore}else{$null})
  $coreAssets=Join-Path $root 'src\SpankyhoOptimizer.Core\obj\project.assets.json'
  if(-not(Test-Path $coreAssets)){dotnet restore $core --nologo | Out-Host; if($LASTEXITCODE -ne 0){Invoke-FullFallback 'core restore failed'; return}}
  dotnet build $core -c Debug --no-restore --nologo -m -v:q -p:RunAnalyzersDuringBuild=false | Out-Host
  if($LASTEXITCODE -ne 0){Invoke-FullFallback 'core compile failed'; return}
  $newCore=Join-Path $coreOut 'SpankyhoOptimizer.Core.dll'
  $newApi=$(if(Test-Path $newCore){Get-PublicApiHash $newCore}else{$null})
  if(-not $oldApi -or -not $newApi -or $oldApi -ne $newApi){Invoke-FullFallback 'core public API changed'; return}
  Copy-Item $newCore $appOut -Force
  $pdb=Join-Path $coreOut 'SpankyhoOptimizer.Core.pdb'; if(Test-Path $pdb){Copy-Item $pdb $appOut -Force}
  if((Get-FileHash $newCore -Algorithm SHA256).Hash -ne (Get-FileHash (Join-Path $appOut 'SpankyhoOptimizer.Core.dll') -Algorithm SHA256).Hash){Invoke-FullFallback 'core copy verification failed'; return}
  Save-SpankyCoreFastState; $sw.Stop(); Write-BuildStatus 'OK' ('core {0:n2}s' -f $sw.Elapsed.TotalSeconds); return
}
$obj=Join-Path $root 'src\SpankyhoOptimizer\obj\project.assets.json'
if(-not(Test-Path $obj)){Invoke-FullFallback 'missing app restore/assets'; return}
$sw=[Diagnostics.Stopwatch]::StartNew()
$skipProjectRefs=(Test-SpankyCoreFresh) -and (Test-Path (Join-Path $appOut 'SpankyhoOptimizer.Core.dll'))
$projectRefArg=$(if($skipProjectRefs){'-p:BuildProjectReferences=false'}else{'-p:BuildProjectReferences=true'})
Write-BuildStatus 'INFO' ('project refs: ' + $(if($skipProjectRefs){'skip'}else{'build'}))
$xamlReuseArgs=@(Get-SpankyXamlReuseArgs)
if($xamlReuseArgs.Count){Write-BuildStatus 'INFO' 'XAML intermediate reuse: on'}
Stop-SpankyDebugProcesses
Assert-SpankyDebugOutputUnlocked
$buildOutput = @(dotnet build $app -c Debug -r win-x64 --no-restore --nologo -m -v:q `
  -p:RunAnalyzers=false -p:RunAnalyzersDuringBuild=false `
  -p:UseSharedCompilation=true -p:GenerateDocumentationFile=false `
  -p:CopyRetryCount=0 -p:CopyRetryDelayMilliseconds=0 $projectRefArg @xamlReuseArgs 2>&1)
$buildExit=$LASTEXITCODE
$buildText=$buildOutput -join "`n"
if($buildExit -ne 0){$buildOutput | Select-Object -Last 20 | Out-Host}
if($buildExit -ne 0 -and $buildText -match 'MSB3021|MSB3027'){
  Invoke-StagedDebugBuild
  $sw.Stop(); Write-BuildStatus 'OK' ('staged-lock {0:n2}s' -f $sw.Elapsed.TotalSeconds)
  return
}
if($buildExit -ne 0 -and $buildText -match 'CS2012|intermediatexaml.*being used by another process|intermediatexaml.*used by another process'){
  Write-BuildStatus 'WARN' 'XAML lock, retrying'
  Start-Sleep -Milliseconds 900
  Stop-SpankyDebugProcesses
  $buildOutput = @(dotnet build $app -c Debug -r win-x64 --no-restore --nologo -m -v:q `
    -p:RunAnalyzers=false -p:RunAnalyzersDuringBuild=false `
    -p:UseSharedCompilation=true -p:GenerateDocumentationFile=false `
    -p:CopyRetryCount=0 -p:CopyRetryDelayMilliseconds=0 $projectRefArg @xamlReuseArgs 2>&1)
  $buildExit=$LASTEXITCODE
  if($buildExit -ne 0){$buildOutput | Select-Object -Last 20 | Out-Host}
}
if($buildExit -ne 0){throw 'Fast incremental build failed.'}
Save-SpankyFullBuildState
$sw.Stop(); Write-BuildStatus 'OK' ('{0:n2}s' -f $sw.Elapsed.TotalSeconds)
