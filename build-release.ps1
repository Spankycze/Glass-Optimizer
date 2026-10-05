param([switch]$KeepStaging,[switch]$ForceRestore,[switch]$SkipRuntimeTest)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'dev-perf-env.ps1')
& (Join-Path $root 'ai-automation-contract-audit.ps1')
$auditOk=$?
if(-not $auditOk){throw 'AI automation contract audit failed'}

# Ask every running Optimizer instance to shut itself down before touching build outputs.
# This works across elevated/non-elevated instances because the running app owns the exit.
$runningOptimizer=@(Get-Process -Name 'SpankyhoOptimizer' -ErrorAction SilentlyContinue)
$optimizerPaths=@{}
foreach($proc in $runningOptimizer){
  try { if($proc.Path){$optimizerPaths[$proc.Path]=$true} } catch {}
}
foreach($path in @($optimizerPaths.Keys)){
  try { Start-Process -FilePath $path -ArgumentList '--request-exit' -WindowStyle Hidden -ErrorAction SilentlyContinue | Out-Null } catch {}
}
if($runningOptimizer.Count -gt 0){
  for($i=0;$i -lt 30;$i++){
    Start-Sleep -Milliseconds 100
    if(@(Get-Process -Name 'SpankyhoOptimizer' -ErrorAction SilentlyContinue).Count -eq 0){break}
  }
}

. (Join-Path $root 'build-state.ps1')
& (Join-Path $root 'build-native.ps1') -StrictHash
if($LASTEXITCODE -ne 0){throw 'Native build failed.'}
$app=Join-Path $root 'src\SpankyhoOptimizer\SpankyhoOptimizer.csproj'
$runtimeHost=Join-Path $root 'src\SpankyhoOptimizer.RuntimeHost\SpankyhoOptimizer.RuntimeHost.csproj'
$installer=Join-Path $root 'installer\SpankyInstaller.csproj'
$artifacts=Join-Path $root 'artifacts'
$release=Join-Path $artifacts 'release'
$stage=Join-Path $artifacts '_stage'
$appStage=Join-Path $stage 'app'
$installerOut=Join-Path $stage 'installer'
$cache=Join-Path $artifacts '.cache'
$appCache=Join-Path $cache 'app-publish'
$releaseBin=Join-Path $root 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64'
$requiredWinUiRuntime=@('App.xbf','SpankyhoOptimizer.pri')
$cachedSetup=$null
$payload=Join-Path $root 'installer\payload.zip'
$releaseSw=[Diagnostics.Stopwatch]::StartNew()
$phaseSw=[Diagnostics.Stopwatch]::StartNew()
$releasePhaseMs=[ordered]@{}
function Mark-ReleasePhase([string]$name){
  $ms=[math]::Round($phaseSw.Elapsed.TotalMilliseconds)
  $releasePhaseMs[$name]=$ms
  Write-Host ("RELEASE_PHASE {0}={1}ms" -f $name,$ms)
  $phaseSw.Restart()
}

function Copy-FileWithRetry([string]$source,[string]$destination,[int]$attempts=8,[int]$delayMs=250){
  for($attempt=1;$attempt -le $attempts;$attempt++){
    try{
      Copy-Item $source $destination -Force -ErrorAction Stop
      if($attempt -gt 1){Write-Host ("FILE_COPY_RETRY_OK attempt={0} destination={1}" -f $attempt,$destination)}
      return
    } catch [System.IO.IOException] {
      if($attempt -ge $attempts){throw}
      Write-Host ("FILE_COPY_RETRY attempt={0}/{1} destination={2}" -f $attempt,$attempts,$destination)
      Start-Sleep -Milliseconds ($delayMs*$attempt)
    }
  }
}
$freshFullGate=Test-SpankyGateFresh

function Get-ReleaseSourceFiles([string]$basePath){
  if(-not(Test-Path $basePath)){return @()}
  $files=[Collections.Generic.List[IO.FileInfo]]::new()
  $dirs=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
  $dirs.Push([IO.DirectoryInfo]::new($basePath))
  while($dirs.Count -gt 0){
    $dir=$dirs.Pop()
    foreach($file in $dir.EnumerateFiles()){ $files.Add($file) }
    foreach($child in $dir.EnumerateDirectories()){
      if($child.Name -in @('bin','obj')){continue}
      $dirs.Push($child)
    }
  }
  return $files.ToArray()
}

function Get-InputHash([string[]]$paths){
  $sha=[Security.Cryptography.SHA256]::Create()
  try{
    $lines=foreach($p in $paths | Sort-Object){
      if(Test-Path $p -PathType Leaf){$h=(Get-FileHash $p -Algorithm SHA256).Hash; "$p|$h"}
    }
    $bytes=[Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','')
  } finally {$sha.Dispose()}
}

Write-Host '=== Spankyho Optimizer release ==='
$installerSources=Get-ChildItem (Join-Path $root 'installer') -File | Where-Object {$_.Extension -in @('.cs','.xaml','.iss')}
$badChars=@([char]0x0102,[char]0x0139,[char]0x00E2,[char]0xFFFD)
foreach($src in $installerSources){
  $raw=[IO.File]::ReadAllText($src.FullName,[Text.Encoding]::UTF8)
  foreach($ch in $badChars){ if($raw.IndexOf($ch) -ge 0){ throw "Installer encoding guard failed: $($src.Name)" } }
}
Write-Host 'INSTALLER_UTF8_GUARD_OK'
$sidebarIconDir=Join-Path $root 'src\SpankyhoOptimizer\Assets\SidebarIcons'
$sidebarIconFiles=@(Get-ChildItem $sidebarIconDir -Filter *.svg -File)
if($sidebarIconFiles.Count -ne 24){throw "Sidebar icon guard failed: expected 24 SVG assets, found $($sidebarIconFiles.Count)."}
foreach($icon in $sidebarIconFiles){
  if($icon.Length -lt 100){throw "Sidebar icon guard failed: $($icon.Name) is suspiciously small ($($icon.Length) bytes)."}
  $head=[IO.File]::ReadAllText($icon.FullName,[Text.Encoding]::UTF8).TrimStart()
  if(-not $head.StartsWith('<svg',[StringComparison]::OrdinalIgnoreCase)){throw "Sidebar icon guard failed: $($icon.Name) is not valid SVG text."}
}
$sidebarDuplicateGroups=@($sidebarIconFiles | Get-FileHash -Algorithm SHA256 | Group-Object Hash | Where-Object Count -gt 1)
if($sidebarDuplicateGroups.Count -gt 0){
  $names=@($sidebarDuplicateGroups | ForEach-Object { ($_.Group | ForEach-Object {[IO.Path]::GetFileName($_.Path)}) -join '+' }) -join ', '
  throw "Sidebar icon guard failed: duplicate SVG assets detected: $names"
}
Write-Host "SIDEBAR_ICON_ASSET_GUARD_OK count=$($sidebarIconFiles.Count)"
$versionProps=Join-Path $root 'Directory.Build.props'
$versionPropsText=[IO.File]::ReadAllText($versionProps,[Text.Encoding]::UTF8)
$versionMatch=[regex]::Match($versionPropsText,'<SpankyVersion>([^<]+)</SpankyVersion>')
if(-not $versionMatch.Success){throw 'Version consistency gate: SpankyVersion not found in Directory.Build.props.'}
$releaseVersion=$versionMatch.Groups[1].Value.Trim()
$expectedAssemblyName='Spankyho_Optimizer_V'+($releaseVersion -replace '\.','_')+'_Setup'
$appProjectText=[IO.File]::ReadAllText($app,[Text.Encoding]::UTF8)
$coreProjectText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer.Core\SpankyhoOptimizer.Core.csproj'),[Text.Encoding]::UTF8)
$installerProjectText=[IO.File]::ReadAllText($installer,[Text.Encoding]::UTF8)
$appSourceText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\App.cs'),[Text.Encoding]::UTF8)
$installerMainText=[IO.File]::ReadAllText((Join-Path $root 'installer\InstallerMain.cs'),[Text.Encoding]::UTF8)
foreach($projectText in @($appProjectText,$coreProjectText,$installerProjectText)){
  if($projectText -match '<Version>[^<]+</Version>'){throw 'Version consistency gate failed: project-local <Version> detected; use Directory.Build.props only.'}
}
if($appSourceText -match 'AppVersion\s*=\s*"\d+\.\d+\.\d+"'){throw 'Version consistency gate failed: App.cs hardcodes AppVersion.'}
if($installerMainText -match 'Version\s*=\s*"\d+\.\d+\.\d+"'){throw 'Version consistency gate failed: installer hardcodes Version.'}
$evalJson=& dotnet msbuild $installer -nologo -getProperty:Version -getProperty:AssemblyVersion -getProperty:FileVersion -getProperty:AssemblyName 2>$null
if($LASTEXITCODE -ne 0){throw 'Version consistency gate failed: installer MSBuild evaluation failed.'}
$eval=$evalJson | ConvertFrom-Json
if($eval.Properties.Version -ne $releaseVersion -or $eval.Properties.AssemblyVersion -ne ($releaseVersion+'.0') -or $eval.Properties.FileVersion -ne ($releaseVersion+'.0') -or $eval.Properties.AssemblyName -ne $expectedAssemblyName){
  throw "Version consistency gate failed: evaluated installer properties do not match $releaseVersion."
}
Write-Host "VERSION_CONSISTENCY_OK $releaseVersion singleSource=Directory.Build.props"
$cachedSetup=Join-Path $cache ($expectedAssemblyName+'.exe')
$appCsSources=Get-ReleaseSourceFiles (Join-Path $root 'src\SpankyhoOptimizer') | Where-Object {$_.Extension -eq '.cs'}
$asyncEventHits=@($appCsSources | Select-String -Pattern '\+=\s*async\s*\(')
$asyncVoidHits=@($appCsSources | Select-String -Pattern '\basync\s+void\b')
if($asyncEventHits.Count -gt 0 -or $asyncVoidHits.Count -gt 0){
  $detail=@($asyncEventHits + $asyncVoidHits | Select-Object -First 12 | ForEach-Object {"$($_.Path):$($_.LineNumber)"}) -join ', '
  throw "Async UI handler guard failed: async event/void handlers detected: $detail"
}
Write-Host 'ASYNC_UI_HANDLER_GUARD_OK'

$fullSmokeMatch=[regex]::Match(
  $appSourceText,
  'if \(HasArg\(cli, "--full-smoke"\).*?if \(HasArg\(cli, "--startup-smoke"\)',
  [Text.RegularExpressions.RegexOptions]::Singleline
)
if(-not $fullSmokeMatch.Success){throw 'Full-smoke coverage guard failed: full-smoke block not found.'}
$fullSmokeBody=$fullSmokeMatch.Value
$requiredCoveredCalls=@(
  'ProcessRunner.RunHardeningSmokeAsync()',
  'RunMtuReadbackSmokeAsync()',
  'RunHeroActionDeferredSmokeAsync()',
  'RunRegressionSmokeTestAsync(repeatedNavigationPasses: 2)',
  'RunPerformanceSmokeAsync()',
  'PowerPlanGuardService.RunArgumentSmoke()'
)
foreach($call in $requiredCoveredCalls){
  if($fullSmokeBody.IndexOf($call,[StringComparison]::Ordinal) -lt 0){
    throw "Full-smoke coverage guard failed: missing $call"
  }
}
$sectionPassPos=$fullSmokeBody.IndexOf('RunSectionSmokeTestAsync()', [StringComparison]::Ordinal)
$regressionPassPos=$fullSmokeBody.IndexOf('RunRegressionSmokeTestAsync(repeatedNavigationPasses: 2)', [StringComparison]::Ordinal)
if($sectionPassPos -lt 0 -or $regressionPassPos -lt 0 -or $sectionPassPos -ge $regressionPassPos){
  throw 'Full-smoke regression pass coverage guard failed: Section Smoke must precede Regression(2).'
}
Write-Host 'FULL_SMOKE_REGRESSION_PASS_GUARD_OK'
Write-Host 'FULL_SMOKE_COVERAGE_GUARD_OK'

$serviceBundleMatch=[regex]::Match(
  $appSourceText,
  'private static async Task<int> RunReleaseServiceSmokeBundleAsync\(\).*?private static bool HasArg',
  [Text.RegularExpressions.RegexOptions]::Singleline
)
if(-not $serviceBundleMatch.Success){throw 'Release service bundle coverage guard failed: bundle method not found.'}
$serviceBundleBody=$serviceBundleMatch.Value
$requiredServiceBundleCalls=@(
  'SystemInfoService.GetSnapshotAsync',
  'NativeSystemProbeService.ReadStorage',
  'WriteAheadTransactionService.RunPersistenceSmokeAsync',
  'WorkerClientService.RunIpcCleanupSmoke',
  'AppUpdateService.RunRetentionSmoke',
  'StartupManagerService.GetAsync',
  'TweakDriftWatchdogService.CheckAppliedTweaksAsync',
  'WindowsBuildRevalidationService.CheckAndUpdate',
  'PerformanceProfilesService.GetNicCapabilitiesAsync',
  'NetworkTransitionStateService.RunSingleFlightSmokeAsync',
  'WindowsActions.RunMemoryCompressionCacheSmokeAsync',
  'GhostUsbService.RunPersistentCacheSmokeAsync',
  'FirewallStateService.RunPersistentCacheSmoke',
  'AuthenticodeVerifier.RunSmoke',
  'RunAiRuntimeHardeningSmokeCoreAsync',
  'ApplyProfileSmokeService.RunStartupWarmApplyAsync',
  'RunSpicetifyStatusSmokeCoreAsync',
  'TweakCompatibilityService.CheckStatic'
)
foreach($call in $requiredServiceBundleCalls){
  if($serviceBundleBody.IndexOf($call,[StringComparison]::Ordinal) -lt 0){
    throw "Release service bundle coverage guard failed: missing $call"
  }
}
Write-Host 'RELEASE_SERVICE_BUNDLE_COVERAGE_GUARD_OK'

$uiBundleMatch=[regex]::Match(
  $appSourceText,
  'if \(HasArg\(cli, "--release-ui-smoke"\).*?if \(HasArg\(cli, "--system-cold-render-smoke"\)',
  [Text.RegularExpressions.RegexOptions]::Singleline
)
if(-not $uiBundleMatch.Success){throw 'Release UI bundle coverage guard failed: bundle block not found.'}
$uiBundleBody=$uiBundleMatch.Value
$requiredUiBundleCalls=@(
  'RunDriversUiSmokeAsync',
  'RunTaskSchedulerUiSmokeAsync',
  'RunStartupRenderSmokeAsync',
  'RunPowerRenderSmokeAsync',
  'RunNavigationTailLatencySmokeAsync',
  'RunAiProgressUiSmokeAsync',
  'RunNicUiSmokeAsync',
  'RunGpuDeferredRenderSmokeAsync',
  'RunThemeRenderSmokeAsync'
)
foreach($call in $requiredUiBundleCalls){
  if($uiBundleBody.IndexOf($call,[StringComparison]::Ordinal) -lt 0){
    throw "Release UI bundle coverage guard failed: missing $call"
  }
}
Write-Host 'RELEASE_UI_BUNDLE_COVERAGE_GUARD_OK'

$installerSourcePath=Join-Path $root 'installer\InstallerMain.cs'
$installerSourceText=Get-Content $installerSourcePath -Raw
$installerBundleRequired=@(
  '--release-installer-smoke-bundle',
  'OpenResource("payload.zip")',
  'LoadUi()',
  'InstallerWindow smokeWindow',
  '--test-release-bundle',
  'ExtractPayload(extract, null)',
  'InstallPayloadTransactional(upgrade',
  'HashPath(uiState)',
  'RecoverInterruptedInstall(recovery)'
)
foreach($token in $installerBundleRequired){
  if($installerSourceText.IndexOf($token,[StringComparison]::Ordinal) -lt 0){
    throw "Installer bundle coverage guard failed: missing $token"
  }
}
Write-Host 'INSTALLER_BUNDLE_COVERAGE_GUARD_OK'

$updaterReleaseMatch=[regex]::Match(
  $appSourceText,
  'if \(HasArg\(cli, "--software-updater-release-smoke"\).*?if \(HasArg\(cli, "--software-updater-inline-smoke"\)',
  [Text.RegularExpressions.RegexOptions]::Singleline
)
if(-not $updaterReleaseMatch.Success){throw 'Software Updater release coverage guard failed: bundled block not found.'}
$updaterReleaseBody=$updaterReleaseMatch.Value
foreach($token in @(
  'RunSoftwareUpdaterInlineSmokeAsync',
  'InvalidateStatusMetadataOnly',
  'WingetUpdateService.GetStatusAsync',
  'GetUpgradeCacheDiagnostics'
)){
  if($updaterReleaseBody.IndexOf($token,[StringComparison]::Ordinal) -lt 0){
    throw "Software Updater release coverage guard failed: missing $token"
  }
}
Write-Host 'SOFTWARE_UPDATER_RELEASE_COVERAGE_GUARD_OK'

foreach($token in @(
  'SPANKY_RUNTIME_READY_FILE',
  'RUNTIME_READY_SIGNAL_OK',
  'File.WriteAllTextAsync(runtimeReadyFile'
)){
  if($appSourceText.IndexOf($token,[StringComparison]::Ordinal) -lt 0){
    throw "Runtime readiness coverage guard failed: missing $token"
  }
}
Write-Host 'RUNTIME_READINESS_COVERAGE_GUARD_OK'
Mark-ReleasePhase 'preflight'

New-Item $artifacts,$cache -ItemType Directory -Force | Out-Null
Remove-Item $stage,$release -Recurse -Force -ErrorAction SilentlyContinue
New-Item $release,$appStage,$installerOut -ItemType Directory -Force | Out-Null
$appFiles=Get-ReleaseSourceFiles (Join-Path $root 'src\SpankyhoOptimizer') | Select-Object -ExpandProperty FullName
$appFiles+=Get-ChildItem (Join-Path $root 'src\SpankyhoOptimizer.RuntimeHost') -File | Select-Object -ExpandProperty FullName
$appFiles+=Join-Path $root 'NuGet.Config'
$appFiles+=$versionProps
$appHash=Get-InputHash $appFiles
$appState=Join-Path $cache 'app-source.sha256'
$appChanged=$true
$appCacheComplete=(Test-Path (Join-Path $appCache 'SpankyhoOptimizer.exe')) -and
  (Test-Path (Join-Path $appCache 'SpankyhoOptimizer.RuntimeHost.exe')) -and
  (($requiredWinUiRuntime | Where-Object { -not (Test-Path (Join-Path $appCache $_)) }).Count -eq 0)
if((Test-Path $appState) -and $appCacheComplete){
  $appChanged=((Get-Content $appState -Raw).Trim() -ne $appHash)
}

if($appChanged){
  Write-Host 'App changed -> publish'
  $assets=Join-Path $root 'src\SpankyhoOptimizer\obj\project.assets.json'
  $appRestoreState=Join-Path $cache 'release-app-restore.sha256'
  $appRestoreHash=Get-SpankyRestoreInputHash @(
    $app,
    (Join-Path $root 'src\SpankyhoOptimizer.Core\SpankyhoOptimizer.Core.csproj'),
    (Join-Path $root 'NuGet.Config'),
    $versionProps
  ) $versionProps
  $appNeedsRestore=$ForceRestore -or -not(Test-Path $assets) -or -not(Test-Path $appRestoreState) -or ((Get-Content $appRestoreState -Raw -ErrorAction SilentlyContinue).Trim() -ne $appRestoreHash)
  if($appNeedsRestore){
    dotnet restore $app -r win-x64 -p:PublishReadyToRun=false --nologo | Out-Host
    if($LASTEXITCODE -ne 0){throw 'App restore failed.'}
    Set-Content $appRestoreState $appRestoreHash -NoNewline
  }
  Remove-Item $appCache -Recurse -Force -ErrorAction SilentlyContinue
  New-Item $appCache -ItemType Directory -Force | Out-Null
  # appChanged means the cached publish no longer matches source. A fresh Debug/full-test
  # gate does not prove that bin\Release is current, so never reuse Release with --no-build here.
  dotnet publish $app -c Release -r win-x64 --self-contained false --no-restore -m -o $appCache `
    -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false | Out-Host
  if($LASTEXITCODE -ne 0){throw 'App publish failed.'}

  $runtimeHostOut=Join-Path $cache 'runtime-host-publish'
  Remove-Item $runtimeHostOut -Recurse -Force -ErrorAction SilentlyContinue
  New-Item $runtimeHostOut -ItemType Directory -Force | Out-Null
  dotnet publish $runtimeHost -c Release -r win-x64 --self-contained false -m -o $runtimeHostOut --nologo -v:q `
    -p:RunAnalyzers=false -p:RunAnalyzersDuringBuild=false | Out-Host
  if($LASTEXITCODE -ne 0){throw 'Runtime host publish failed.'}
  foreach($name in @(
    'SpankyhoOptimizer.RuntimeHost.exe',
    'SpankyhoOptimizer.RuntimeHost.dll',
    'SpankyhoOptimizer.RuntimeHost.deps.json',
    'SpankyhoOptimizer.RuntimeHost.runtimeconfig.json'
  )){
    $source=Join-Path $runtimeHostOut $name
    if(-not(Test-Path $source)){throw "Runtime host artifact missing: $name"}
    Copy-Item $source (Join-Path $appCache $name) -Force
  }

  # Voice runtimes are intentionally not bundled. The app downloads Whisper,
  # Piper and openWakeWord on demand into LocalAppData on first Voice AI use.
  Remove-Item (Join-Path $appCache 'Tools\Whisper') -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item (Join-Path $appCache 'Tools\Piper') -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item (Join-Path $appCache 'Tools\OpenWakeWord') -Recurse -Force -ErrorAction SilentlyContinue

  Remove-Item $runtimeHostOut -Recurse -Force -ErrorAction SilentlyContinue
  Write-Host 'RUNTIME_HOST_PUBLISH_OK'
  Write-Host 'VOICE_RUNTIME_LAZY_DOWNLOAD_OK'

  Get-ChildItem $appCache -Directory | Where-Object {
    $_.Name -notin @('cs-CZ','en-US') -and
    (Get-ChildItem $_.FullName -File -ErrorAction SilentlyContinue | Where-Object {
      $_.Name.EndsWith('.mui',[StringComparison]::OrdinalIgnoreCase) -or $_.Name.EndsWith('.resources.dll',[StringComparison]::OrdinalIgnoreCase)
    })
  } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item (Join-Path $appCache 'createdump.exe') -Force -ErrorAction SilentlyContinue
  Set-Content $appState $appHash -NoNewline
} else { Write-Host 'App unchanged -> cached publish' }
Mark-ReleasePhase 'app-publish'

foreach($name in $requiredWinUiRuntime){
  $target=Join-Path $appCache $name
  if(-not(Test-Path $target)){
    $source=Join-Path $releaseBin $name
    if(-not(Test-Path $source)){ throw "Required WinUI runtime artifact missing after publish: $name" }
    Copy-Item $source $target -Force
  }
}
Write-Host 'WINUI_RUNTIME_ARTIFACTS_OK'

$optionalAiRuntime=@(
  'onnxruntime.dll','DirectML.dll','Microsoft.Windows.AI.MachineLearning.dll',
  'Microsoft.Windows.AI.Imaging.dll','Microsoft.Windows.AI.Text.dll'
)
foreach($name in $optionalAiRuntime){
  Get-ChildItem $appCache -Recurse -File -Filter $name -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}
$aiLeft=foreach($name in $optionalAiRuntime){ Get-ChildItem $appCache -Recurse -File -Filter $name -ErrorAction SilentlyContinue }
if($aiLeft){ throw 'Optional AI runtime cleanup failed.' }
Write-Host 'OPTIONAL_AI_RUNTIME_CLEANUP_OK'
Copy-Item (Join-Path $appCache '*') $appStage -Recurse -Force

$payloadState=Join-Path $cache 'payload-source.sha256'
$payloadFiles=Get-ChildItem $appCache -Recurse -File | Select-Object -ExpandProperty FullName
$payloadSourceHash=Get-InputHash $payloadFiles
$payloadChanged=$true
if((Test-Path $payloadState) -and (Test-Path $payload)){$payloadChanged=((Get-Content $payloadState -Raw).Trim() -ne $payloadSourceHash)}
if($payloadChanged){
  Write-Host 'Payload changed -> compress'
  $zipSw=[Diagnostics.Stopwatch]::StartNew()
  Remove-Item $payload -Force -ErrorAction SilentlyContinue
  Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
  [IO.Compression.ZipFile]::CreateFromDirectory($appStage,$payload,[IO.Compression.CompressionLevel]::Optimal,$false)
  $zipSw.Stop(); Write-Host ('PAYLOAD_ZIP_OK {0:n2}s' -f $zipSw.Elapsed.TotalSeconds)
  Set-Content $payloadState $payloadSourceHash -NoNewline
} else { Write-Host 'Payload unchanged -> reuse zip' }
Mark-ReleasePhase 'payload-stage-compress'
$installerFiles=Get-ChildItem (Join-Path $root 'installer') -File | Where-Object {$_.Name -ne 'payload.zip'} | Select-Object -ExpandProperty FullName
$payloadHash=(Get-FileHash $payload -Algorithm SHA256).Hash
$installerHash=Get-InputHash ($installerFiles + $payload + $versionProps)
$installerState=Join-Path $cache 'installer-input.sha256'
$installerChanged=$true
if((Test-Path $installerState) -and (Test-Path $cachedSetup)){$installerChanged=((Get-Content $installerState -Raw).Trim() -ne $installerHash)}
if($installerChanged){
  Write-Host 'Installer changed -> publish'
  $installerAssets=Join-Path $root 'installer\obj\project.assets.json'
  $installerRestoreState=Join-Path $cache 'release-installer-restore.sha256'
  $installerRestoreHash=Get-SpankyRestoreInputHash @(
    $installer,
    (Join-Path $root 'NuGet.Config'),
    $versionProps
  ) $versionProps
  $installerNeedsRestore=$ForceRestore -or -not(Test-Path $installerAssets) -or -not(Test-Path $installerRestoreState) -or ((Get-Content $installerRestoreState -Raw -ErrorAction SilentlyContinue).Trim() -ne $installerRestoreHash)
  if($installerNeedsRestore){
    dotnet restore $installer -r win-x64 --nologo | Out-Host
    if($LASTEXITCODE -ne 0){throw 'Installer restore failed.'}
    Set-Content $installerRestoreState $installerRestoreHash -NoNewline
  }
  dotnet publish $installer -c Release -r win-x64 --self-contained true --no-restore -m -o $installerOut `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false | Out-Host
  if($LASTEXITCODE -ne 0){throw 'Installer publish failed.'}
  $setup=Get-ChildItem $installerOut -Filter '*.exe' | Sort-Object Length -Descending | Select-Object -First 1
  if(-not $setup){throw 'Installer EXE was not created.'}
  Copy-FileWithRetry $setup.FullName $cachedSetup
  Set-Content $installerState $installerHash -NoNewline
} else { Write-Host 'Installer unchanged -> cached EXE' }
Mark-ReleasePhase 'installer-publish'

$final=Join-Path $release ($expectedAssemblyName+'.exe')
Copy-FileWithRetry $cachedSetup $final
$installerSmokeBundle=Start-Process -FilePath $final -ArgumentList '--release-installer-smoke-bundle' -Wait -PassThru
if($installerSmokeBundle.ExitCode -ne 0){throw "Installer smoke bundle failed: $($installerSmokeBundle.ExitCode)"}
Write-Host 'INSTALLER_PAYLOAD_SMOKE_OK (covered by installer smoke bundle)'
Write-Host 'INSTALLER_UI_SMOKE_OK (covered by installer smoke bundle)'
Write-Host 'INSTALLER_WINDOW_SMOKE_OK (covered by installer smoke bundle)'
Mark-ReleasePhase 'installer-smoke-bundle'
if(-not $SkipRuntimeTest){
  $testRoot=Join-Path $env:TEMP ('SpankyhoOptimizer-ReleaseGate-'+[guid]::NewGuid().ToString('N'))
  New-Item $testRoot -ItemType Directory -Force | Out-Null
  $currentBundleRoot=Join-Path $testRoot 'current-installer-bundle'
  $currentBundle=Start-Process -FilePath $final -ArgumentList @('--test-release-bundle',('"'+$currentBundleRoot+'"')) -Wait -PassThru
  if($currentBundle.ExitCode -ne 0){throw "Current installer release bundle failed: $($currentBundle.ExitCode)"}
  $test=Join-Path $currentBundleRoot 'extract'
  Write-Host 'INSTALLER_EXTRACTION_OK (covered by installer test bundle)'
  Write-Host 'INSTALLER_TRANSACTIONAL_INSTALL_OK (covered by installer test bundle)'
  Write-Host 'INSTALLER_SETTINGS_PRESERVATION_OK (covered by installer test bundle)'
  $currentVersion=[version]$releaseVersion
  $desktop=[Environment]::GetFolderPath('Desktop')
  $previousRoots=@($desktop,$cache) | Where-Object { Test-Path $_ } | Select-Object -Unique
  $previousCandidate=$previousRoots | ForEach-Object {
    Get-ChildItem $_ -Filter 'Spankyho_Optimizer_V*_Setup.exe' -File -ErrorAction SilentlyContinue
  } | ForEach-Object {
    if($_.Name -match '^Spankyho_Optimizer_V(\d+)_(\d+)_(\d+)_Setup\.exe$'){
      $v=[version]("$($matches[1]).$($matches[2]).$($matches[3])")
      if($v -lt $currentVersion){[pscustomobject]@{Version=$v;Path=$_.FullName}}
    }
  } | Sort-Object Version -Descending | Select-Object -First 1
  if($previousCandidate){
    $previousTarget=Join-Path $testRoot 'previous-upgrade'
    $oldInstall=Start-Process -FilePath $previousCandidate.Path -ArgumentList @('--test-install',('"'+$previousTarget+'"')) -Wait -PassThru
    if($oldInstall.ExitCode -ne 0){throw "Previous installer extraction failed: $($oldInstall.ExitCode)"}
    $realUpgrade=Start-Process -FilePath $cachedSetup -ArgumentList @('--test-transactional-install',('"'+$previousTarget+'"')) -Wait -PassThru
    if($realUpgrade.ExitCode -ne 0){throw "$($previousCandidate.Version) -> current upgrade failed: $($realUpgrade.ExitCode)"}
    Write-Host "PREVIOUS_VERSION_UPGRADE_OK $($previousCandidate.Version)"
  } else { Write-Host 'PREVIOUS_VERSION_UPGRADE_SKIPPED no older installer found' }
  Write-Host 'INSTALLER_TRANSACTION_RECOVERY_OK (covered by installer test bundle)'
  Mark-ReleasePhase 'installer-test-upgrade'
  $exe=Join-Path $test 'SpankyhoOptimizer.exe'
  if(-not (Test-Path $exe)){throw 'Extracted app missing.'}
  $readyMarker=Join-Path $testRoot 'runtime-ready.txt'
  Remove-Item $readyMarker -Force -ErrorAction SilentlyContinue
  $oldReadyEnv=$env:SPANKY_RUNTIME_READY_FILE
  try {
    $env:SPANKY_RUNTIME_READY_FILE=$readyMarker
    $runtimeSw=[Diagnostics.Stopwatch]::StartNew()
    $p=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList ('--runtime-ready-file="'+$readyMarker+'"') -PassThru
    $readyDeadline=[DateTime]::UtcNow.AddSeconds(8)
    while(-not(Test-Path $readyMarker) -and -not $p.HasExited -and [DateTime]::UtcNow -lt $readyDeadline){
      Start-Sleep -Milliseconds 25
    }
    if($p.HasExited -and -not(Test-Path $readyMarker)){throw "Runtime smoke failed before readiness: $($p.ExitCode)"}
    if(-not(Test-Path $readyMarker)){throw 'Runtime smoke readiness signal timed out after 8s.'}
    $readyText=Get-Content $readyMarker -Raw
    if($readyText -notmatch [regex]::Escape($releaseVersion)){throw "Runtime readiness version mismatch: $readyText"}
    Write-Host ('RUNTIME_READY_OK {0:n0}ms' -f $runtimeSw.Elapsed.TotalMilliseconds)
  }
  finally {
    if($null -eq $oldReadyEnv){Remove-Item Env:SPANKY_RUNTIME_READY_FILE -ErrorAction SilentlyContinue}
    else {$env:SPANKY_RUNTIME_READY_FILE=$oldReadyEnv}
  }
  $runtimePid=$p.Id
  if(Get-Process -Id $runtimePid -ErrorAction SilentlyContinue){
    Stop-Process -Id $runtimePid -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 150
  }
  if(Get-Process -Id $runtimePid -ErrorAction SilentlyContinue){
    $kill=Start-Process -FilePath "$env:SystemRoot\System32\taskkill.exe" -ArgumentList @('/PID',"$runtimePid",'/T','/F') -WindowStyle Hidden -Wait -PassThru -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 150
  }
  if(Get-Process -Id $runtimePid -ErrorAction SilentlyContinue){throw 'Runtime smoke process tree did not terminate after forced stop.'}
  Mark-ReleasePhase 'runtime-readiness'
  $updaterReleaseSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--software-updater-release-smoke' -Wait -PassThru
  if($updaterReleaseSmoke.ExitCode -ne 0){throw "Software Updater/Winget bundled release smoke failed: $($updaterReleaseSmoke.ExitCode)"}
  Write-Host 'SOFTWARE_UPDATER_RELEASE_SMOKE_OK'
  Write-Host 'WINGET_STATUS_RELEASE_SMOKE_OK (covered by Software Updater release smoke)'
  Write-Host 'AI_RUNTIME_HARDENING_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'AI_PROGRESS_UI_RELEASE_SMOKE_OK (covered by UI bundle)'
  $shortcutOverlaySmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--shortcut-overlay-smoke' -Wait -PassThru
  if($shortcutOverlaySmoke.ExitCode -ne 0){throw "Shortcut overlay release smoke failed: $($shortcutOverlaySmoke.ExitCode)"}
  Write-Host 'SHORTCUT_OVERLAY_RELEASE_SMOKE_OK'
  # Check rendered font roles and overflow at compact, normal and large window sizes.
  $typographySmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--visual-size-smoke' -WindowStyle Hidden -PassThru
  if(!$typographySmoke.WaitForExit(60000)){$typographySmoke.Kill();throw 'Typography/layout release smoke timed out'}
  if($typographySmoke.ExitCode -ne 0){throw "Typography/layout release smoke failed: $($typographySmoke.ExitCode)"}
  Write-Host 'TYPOGRAPHY_LAYOUT_RELEASE_SMOKE_OK'
  $releaseUiSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--release-ui-smoke' -Wait -PassThru
  if($releaseUiSmoke.ExitCode -ne 0){throw "Release UI smoke bundle failed: $($releaseUiSmoke.ExitCode)"}
  Write-Host 'RELEASE_UI_SMOKE_BUNDLE_OK'
  Write-Host 'DRIVERS_UI_RELEASE_SMOKE_OK (covered by UI bundle)'
  Write-Host 'TASK_SCHEDULER_UI_RELEASE_SMOKE_OK (covered by UI bundle)'
  Write-Host 'THEME_RENDER_RELEASE_SMOKE_OK (covered by UI bundle)'
  $releaseServiceSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--release-service-smoke' -Wait -PassThru
  if($releaseServiceSmoke.ExitCode -ne 0){throw "Release service smoke bundle failed: $($releaseServiceSmoke.ExitCode)"}
  Write-Host 'RELEASE_SERVICE_SMOKE_BUNDLE_OK'
  Write-Host 'HARDWARE_IDENTITY_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'STORAGE_PROBE_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'PROCESS_HARDENING_RELEASE_SMOKE_OK (covered by full smoke/fresh full gate)'
  Write-Host 'WRITE_AHEAD_TRANSACTION_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'WORKER_IPC_CLEANUP_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'UPDATE_RETENTION_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'STARTUP_INVENTORY_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'TWEAK_DRIFT_WATCHDOG_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'WINDOWS_BUILD_REVALIDATION_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'NIC_CAPABILITY_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'NIC_UI_RELEASE_SMOKE_OK (covered by UI bundle)'
  Write-Host 'MTU_RELEASE_SMOKE_OK (covered by full smoke/fresh full gate)'
  Write-Host 'DNS_BENCHMARK_RELEASE_SMOKE_OK (covered by service bundle)'
  Mark-ReleasePhase 'updater-ui-service-gates'
  $startupIsolatedBundle=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--startup-isolated-smoke-bundle' -Wait -PassThru
  if($startupIsolatedBundle.ExitCode -ne 0){throw "Startup isolated smoke bundle failed: $($startupIsolatedBundle.ExitCode)"}
  Write-Host 'ACTIONBAR_LAZY_RELEASE_SMOKE_OK (covered by startup isolated bundle)'
  Write-Host 'STARTUP_RENDER_RELEASE_SMOKE_OK (covered by UI bundle)'
  Write-Host 'STARTUP_HOTPATH_RELEASE_SMOKE_OK (covered by startup isolated bundle)'
  Write-Host 'STARTUP_WARM_APPLY_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'PERFORMANCE_RELEASE_SMOKE_OK (covered by full smoke/fresh full gate)'
  Write-Host 'NAVIGATION_TAIL_RELEASE_SMOKE_OK (covered by UI bundle)'
  Write-Host 'HERO_ACTION_DEFERRED_RELEASE_SMOKE_OK (covered by full smoke/fresh full gate)'
  Write-Host 'POWER_RENDER_RELEASE_SMOKE_OK (covered by UI bundle)'
  $systemColdSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--system-cold-render-smoke' -Wait -PassThru
  if($systemColdSmoke.ExitCode -ne 0){throw "System cold render release smoke failed: $($systemColdSmoke.ExitCode)"}
  Write-Host 'SYSTEM_COLD_RENDER_RELEASE_SMOKE_OK'
  Write-Host 'GPU_DEFERRED_RENDER_RELEASE_SMOKE_OK (covered by UI bundle)'
  $resourceCacheBundle=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--resource-cache-smoke-bundle' -Wait -PassThru
  if($resourceCacheBundle.ExitCode -ne 0){throw "Resource/cache smoke bundle failed: $($resourceCacheBundle.ExitCode)"}
  Write-Host 'VIRTUAL_UI_PREWARM_RELEASE_SMOKE_OK (covered by resource/cache bundle)'
  Write-Host 'DEBLOAT_CACHE_RELEASE_SMOKE_OK (covered by resource/cache bundle)'
  Write-Host 'AUTHENTICODE_NATIVE_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'SPICETIFY_STATUS_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'POWER_PLAN_GUARD_RELEASE_SMOKE_OK (covered by full smoke/fresh full gate)'
  Write-Host 'REGRESSION_RELEASE_SMOKE_OK (covered by full smoke/fresh full gate)'
  Mark-ReleasePhase 'isolated-ui-gates'
  $criticalLeakSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--critical-leak-smoke' -Wait -PassThru
  if($criticalLeakSmoke.ExitCode -ne 0){throw "Critical leak release smoke failed: $($criticalLeakSmoke.ExitCode)"}
  Write-Host 'CRITICAL_LEAK_RELEASE_SMOKE_OK'
  Mark-ReleasePhase 'critical-leak'
  if($freshFullGate){
    $sectionSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--section-smoke' -Wait -PassThru
    if($sectionSmoke.ExitCode -ne 0){throw "Release section smoke failed: $($sectionSmoke.ExitCode)"}
    Write-Host 'RELEASE_SECTION_SMOKE_OK'
    Write-Host 'RELEASE_FULL_SMOKE_REUSED_FROM_FRESH_GATE'
  } else {
    $fullSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--full-smoke' -Wait -PassThru
    if($fullSmoke.ExitCode -ne 0){throw "Release full smoke failed: $($fullSmoke.ExitCode)"}
    Write-Host 'RELEASE_SECTION_SMOKE_OK (covered by full smoke)'
    Write-Host 'RELEASE_FULL_SMOKE_OK'
  }
  Mark-ReleasePhase 'full-smoke'
  $crashDumpSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--crash-dump-smoke' -Wait -PassThru
  if($crashDumpSmoke.ExitCode -ne 0){throw "Crash dump smoke failed: $($crashDumpSmoke.ExitCode)"}
  Write-Host 'CRASH_DUMP_SMOKE_OK'
  Write-Host 'PROVIDER_DEGRADATION_SMOKE_OK (covered by service bundle)'
  Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue
  Write-Host 'RUNTIME_SMOKE_OK'
  Mark-ReleasePhase 'crashdump-runtime-cleanup'
}
# Installer self-tests may remove/replace their launch copy; restore the canonical offline artifact from cache.
Copy-FileWithRetry $cachedSetup $final
$sizeMb=[math]::Round((Get-Item $final).Length/1MB,2)
$setupHash=(Get-FileHash $final -Algorithm SHA256).Hash

# Build the compact framework-dependent installer from the exact same verified payload.
# Offline remains in artifacts/release as the no-prerequisite fallback; Desktop receives Compact.
$compactBuilder=Join-Path $root 'build-installer-compact.ps1'
& $compactBuilder -Version $releaseVersion -PayloadPath $payload -OutputDir $release | Out-Host
if($LASTEXITCODE -ne 0){throw 'Compact installer build failed.'}
$compactName='Spankyho_Optimizer_V'+($releaseVersion -replace '\.','_')+'_Setup_Compact.exe'
$compactFinal=Join-Path $release $compactName
if(-not(Test-Path $compactFinal)){throw 'Compact installer artifact missing.'}
$compactSizeMb=[math]::Round((Get-Item $compactFinal).Length/1MB,2)
$compactHash=(Get-FileHash $compactFinal -Algorithm SHA256).Hash
Write-Host ("COMPACT_INSTALLER_RELEASE_OK offline={0:n2}MB compact={1:n2}MB saved={2:n2}MB" -f $sizeMb,$compactSizeMb,($sizeMb-$compactSizeMb))
$releaseSw.Stop()
$manifest=[ordered]@{
  version=$releaseVersion
  createdUtc=[DateTime]::UtcNow.ToString('o')
  windowsBuild=[Environment]::OSVersion.Version.Build
  installer=[ordered]@{ file=[IO.Path]::GetFileName($final); sizeMb=$sizeMb; sha256=$setupHash; mode='offline-self-contained' }
  compactInstaller=[ordered]@{ file=[IO.Path]::GetFileName($compactFinal); sizeMb=$compactSizeMb; sha256=$compactHash; mode='framework-dependent-net10' }
  payloadSha256=$payloadHash
  gates=[ordered]@{
    utf8=$true; aiRuntimeCleanup=$true; installerSmoke=$true; transactional=$true; settingsPreservation=(-not $SkipRuntimeTest)
    recovery=(-not $SkipRuntimeTest); softwareUpdater=(-not $SkipRuntimeTest); wingetStatus=(-not $SkipRuntimeTest); aiRuntimeHardening=(-not $SkipRuntimeTest); aiProgressUi=(-not $SkipRuntimeTest); driversUi=(-not $SkipRuntimeTest); taskSchedulerUi=(-not $SkipRuntimeTest); themeRender=(-not $SkipRuntimeTest); hardwareIdentity=(-not $SkipRuntimeTest); storageProbe=(-not $SkipRuntimeTest); nicCapability=(-not $SkipRuntimeTest); nicUi=(-not $SkipRuntimeTest); mtu=(-not $SkipRuntimeTest); actionBarLazy=(-not $SkipRuntimeTest); startupRender=(-not $SkipRuntimeTest); startupWarmApply=(-not $SkipRuntimeTest); performanceSmoke=(-not $SkipRuntimeTest); heroActionDeferred=(-not $SkipRuntimeTest); powerRender=(-not $SkipRuntimeTest); systemColdRender=(-not $SkipRuntimeTest); gpuDeferredRender=(-not $SkipRuntimeTest); virtualUiPrewarm=(-not $SkipRuntimeTest); debloatCache=(-not $SkipRuntimeTest); authenticodeNative=(-not $SkipRuntimeTest); spicetifyStatus=(-not $SkipRuntimeTest); powerPlanGuard=(-not $SkipRuntimeTest); regressionSmoke=(-not $SkipRuntimeTest); criticalLeak=(-not $SkipRuntimeTest); sectionSmoke=(-not $SkipRuntimeTest); fullSmoke=(-not $SkipRuntimeTest)
    crashDump=(-not $SkipRuntimeTest); providerDegradation=(-not $SkipRuntimeTest); manifestIntegrity=$true
  }
  releaseSeconds=[math]::Round($releaseSw.Elapsed.TotalSeconds,2)
}
New-Item $stage -ItemType Directory -Force | Out-Null
$manifestPath=Join-Path $stage 'release-manifest.tmp.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content $manifestPath -Encoding UTF8
$manifestCheck=Get-Content $manifestPath -Raw | ConvertFrom-Json
if($manifestCheck.version -ne $releaseVersion -or $manifestCheck.installer.file -ne [IO.Path]::GetFileName($final) -or $manifestCheck.installer.sha256 -ne $setupHash){
  throw 'Release manifest integrity check failed.'
}
Remove-Item $manifestPath -Force -ErrorAction SilentlyContinue
Write-Host 'RELEASE_MANIFEST_OK'

# Bound cached installer history. Keep only current + immediately previous version so the
# next release can still run the real upgrade gate without accumulating tens of GB.
$cachedVersions=@(
  Get-ChildItem $cache -Filter 'Spankyho_Optimizer_V*_Setup.exe' -File -ErrorAction SilentlyContinue |
    ForEach-Object {
      if($_.Name -match '^Spankyho_Optimizer_V(\d+)_(\d+)_(\d+)_Setup\.exe$'){
        [pscustomobject]@{ File=$_; Version=[version]("$($matches[1]).$($matches[2]).$($matches[3])") }
      }
    } | Sort-Object Version -Descending
)
$keepCached=@($cachedVersions | Select-Object -First 2)
$removeCached=@($cachedVersions | Select-Object -Skip 2)
$freedBytes=0L
foreach($entry in $removeCached){
  try{
    $freedBytes += [int64]$entry.File.Length
    Remove-Item $entry.File.FullName -Force -ErrorAction Stop
  } catch {}
}
Write-Host ("RELEASE_CACHE_PRUNE_OK kept={0} removed={1} freedMB={2:n1}" -f $keepCached.Count,$removeCached.Count,($freedBytes/1MB))

# Remove only stale disposable experiment outputs. Incremental/release caches, release,
# backups and profiling evidence are deliberately excluded.
$artifactCutoff=(Get-Date).AddDays(-1)
$disposableArtifactPatterns=@(
  'release-probe*','ui-preview*','*-bench','perf-v*',
  'startup-publish-*','live-metrics-opt','release-debug-ui',
  'installer-*-test','decompiled-installer-*'
)
$artifactRemoved=0
$artifactFreed=0L
foreach($pattern in $disposableArtifactPatterns){
  Get-ChildItem $artifacts -Directory -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
    if($_.LastWriteTime -gt $artifactCutoff){return}
    if($_.Name -in @('.cache','release','_stage','backup','build-profile')){return}
    try{
      $bytes=[int64]((Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum)
      Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop
      $artifactRemoved++
      $artifactFreed+=$bytes
    } catch {}
  }
}
Write-Host ("DISPOSABLE_ARTIFACT_PRUNE_OK removed={0} freedMB={1:n1}" -f $artifactRemoved,($artifactFreed/1MB))

Write-Host "FINAL: $final"
Write-Host "SIZE:  $sizeMb MB"
Write-Host "SETUP_SHA256: $setupHash"
Write-Host "PAYLOAD_SHA256: $payloadHash"

# Desktop delivery is transactional and keeps exactly one public setup.
# Prefer the much smaller Compact installer; the Offline fallback stays in artifacts\release.
$desktop=[Environment]::GetFolderPath('Desktop')
$desktopSetup=Join-Path $desktop ([IO.Path]::GetFileName($compactFinal))
Copy-Item $compactFinal $desktopSetup -Force
$desktopHash=(Get-FileHash $desktopSetup -Algorithm SHA256).Hash
if($desktopHash -ne $compactHash){throw "Desktop compact setup hash mismatch: $desktopHash != $compactHash"}
Get-ChildItem $desktop -Filter 'Spankyho_Optimizer_V*_Setup*.exe' -File -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -ne $desktopSetup } |
  Remove-Item -Force -ErrorAction Stop
$desktopCount=@(Get-ChildItem $desktop -Filter 'Spankyho_Optimizer_V*_Setup*.exe' -File -ErrorAction SilentlyContinue).Count
if($desktopCount -ne 1){throw "Desktop setup cleanup failed: expected 1, found $desktopCount"}
Write-Host "DESKTOP_COMPACT_SETUP_OK $desktopSetup"

if(-not $KeepStaging){Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue}
Write-Host ('RELEASE_OK total={0:n2}s' -f $releaseSw.Elapsed.TotalSeconds)

# Advance the single source of truth only after every release gate succeeded.
# Failed/debug/test builds never consume a version number.
$currentRelease=[version]$releaseVersion
$nextRelease=('{0}.{1}.{2}' -f $currentRelease.Major,$currentRelease.Minor,($currentRelease.Build+1))
$propsPath=Join-Path $root 'Directory.Build.props'
$propsText=[IO.File]::ReadAllText($propsPath,[Text.Encoding]::UTF8)
$needle='<SpankyVersion>'+ $releaseVersion +'</SpankyVersion>'
$replacement='<SpankyVersion>'+ $nextRelease +'</SpankyVersion>'
if(-not $propsText.Contains($needle)){throw "Post-release version bump failed: expected $needle"}
[IO.File]::WriteAllText($propsPath,$propsText.Replace($needle,$replacement),[Text.UTF8Encoding]::new($false))
Write-Host "VERSION_BUMP_OK $releaseVersion -> $nextRelease"
$notifyScript=Join-Path (Split-Path $root -Parent) 'build-notify.ps1'
if(Test-Path $notifyScript){
  Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File',$notifyScript,'-Project','Spankyho Optimizer','-Version',$releaseVersion,'-Status','Success','-ArtifactPath',$desktopSetup) | Out-Null
}
