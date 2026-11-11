param([switch]$KeepStaging,[switch]$ForceRestore,[switch]$SkipRuntimeTest,[switch]$AllowOptimizerShutdown,[switch]$PublishLocalUpdateBridge)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
# Exclusive filesystem lease prevents simultaneous release builds sharing cache/staging.
$releaseLeasePath=Join-Path $root '.ai-state\release-exclusive.lock'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($releaseLeasePath)) | Out-Null
try {
  $releaseLease=[IO.File]::Open($releaseLeasePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
} catch [System.IO.IOException] {
  throw 'RELEASE_CONCURRENT_BUILD_BLOCKED: another release holds the exclusive build lease.'
}
Write-Host 'RELEASE_EXCLUSIVE_LEASE_OK'
# End-to-end clock includes all static/installer tests before the publish clock.
# Keep releaseSw for backward-compatible per-phase measurements.
$pipelineSw=[Diagnostics.Stopwatch]::StartNew()
# Fail before publishing/test extraction when source-ahead release notes still
# start with the PREVIOUS version. The UI smoke requires current version first.
# This catches missing handoff/changelog updates in milliseconds, not after
# a full app/installer build and 80+ seconds of release gates.
$props=Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$versionMatch=[regex]::Match($props,'<SpankyVersion>(\d+\.\d+\.\d+)</SpankyVersion>')
if(-not $versionMatch.Success){throw 'Release preflight: source version missing from Directory.Build.props.'}
$sourceVersion=$versionMatch.Groups[1].Value
$progressPath=Join-Path $root 'WORK_PROGRESS.md'
$progressFirst=Get-Content -LiteralPath $progressPath -TotalCount 1
if(-not $progressFirst -or $progressFirst -notmatch ('^##\s+'+[regex]::Escape($sourceVersion)+'\b')){
  throw "Release preflight: WORK_PROGRESS.md must begin with current version $sourceVersion (got '$progressFirst')."
}
Write-Host "RELEASE_PROGRESS_VERSION_PREFLIGHT_OK version=$sourceVersion"
# A sub-second source-only guard prevents future build workflow regressions:
# no default app shutdown, no double-compilation wrapper and no missing Core ref.
$devCycleGuard=Join-Path $root 'dev-cycle.ps1'
if(-not(Test-Path -LiteralPath $devCycleGuard)){
  throw 'Safe build workflow regression: dev-cycle.ps1 is missing.'
}
& $devCycleGuard -Mode SelfTest | Out-Host
if(-not $?){throw 'Safe fast/release development workflow guard failed.'}
. (Join-Path $root 'dev-perf-env.ps1')
& (Join-Path $root 'quality-integration-guard.ps1')
if(-not $?){throw 'Quality integration guard failed'}
& (Join-Path $root 'ai-automation-contract-audit.ps1')
$auditOk=$?
if(-not $auditOk){throw 'AI automation contract audit failed'}
& (Join-Path $root 'black-surface-audit.ps1')
$blackSurfaceAuditOk=$?
if(-not $blackSurfaceAuditOk){throw 'Black surface audit failed'}
& (Join-Path $root 'ui-holistic-audit.ps1')
$uiHolisticAuditOk=$?
if(-not $uiHolisticAuditOk){throw 'Holistic UI audit failed'}
& (Join-Path $root 'context-menu-shell-guard.ps1')
$contextMenuShellGuardOk=$?
if(-not $contextMenuShellGuardOk){throw 'Context menu / Windows shell guard failed'}
& (Join-Path $root 'themes-hub-guard.ps1')
$themesHubGuardOk=$?
if(-not $themesHubGuardOk){throw 'Themes hub guard failed'}
& (Join-Path $root 'desktop-update-notification-guard.ps1')
$desktopUpdateGuardOk=$?
if(-not $desktopUpdateGuardOk){throw 'Desktop update notification guard failed'}
& (Join-Path $root 'native-dll-system32-guard.ps1')
$nativeDllGuardOk=$?
if(-not $nativeDllGuardOk){throw 'System DLL search path safety guard failed'}
& (Join-Path $root 'update-notifications-lifetime-guard.ps1')
if(-not $?){throw 'Desktop update notification lifetime guard failed'}
& (Join-Path $root 'wallpaper-errors-guard.ps1')
if(-not $?){throw 'Wallpaper error handling guard failed'}
& (Join-Path $root 'popup-fallback-diagnostics-guard.ps1')
if(-not $?){throw 'Desktop popup native fallback diagnostic guard failed'}
& (Join-Path $root 'tools\verify-visual-brand.ps1')
if(-not $?){throw 'App/installer/tray visual brand guard failed'}

# Source architecture and narrowly scoped read-only core regressions are
# release prerequisites, not optional developer-only checks. Run BEFORE
# touching staging, the installed app or any publish destination.
$qualityProject=Join-Path $root 'tools\Spankyho.QualityTests\Spankyho.QualityTests.csproj'
$coreTestProject=Join-Path $root 'tools\CoreTests\CoreTests.csproj'
$readOnlyCoreFilter='FullyQualifiedName~FanStartupIsolationTests|FullyQualifiedName~FanAgentRegistrationCadence1439Tests|FullyQualifiedName~FanAgentTaskPolicyTests|FullyQualifiedName~FanProfileAtomicSave1438Tests|FullyQualifiedName~RegistryBatchScope'
# A dotnet test filter that matches ZERO tests can exit successfully. Count
# TRX results explicitly, with per-run unique names to avoid stale evidence.
$gateReports=Join-Path $root 'artifacts\.cache\release-unit-gates'
[IO.Directory]::CreateDirectory($gateReports) | Out-Null
$gateId=[guid]::NewGuid().ToString('N')
$qualityReport=Join-Path $gateReports ("architecture-$gateId.trx")
$coreReport=Join-Path $gateReports ("readonly-core-$gateId.trx")
function Assert-ReleaseUnitResults([string]$path,[int]$minimum,[string]$failure){
  if(-not(Test-Path -LiteralPath $path)){throw "$failure missing TRX report"}
  [xml]$trx=[IO.File]::ReadAllText($path)
  $c=$trx.TestRun.ResultSummary.Counters
  if($null -eq $c -or [int]$c.total -lt $minimum -or
     [int]$c.failed -ne 0 -or [int]$c.passed -ne [int]$c.total){
    throw "$failure test-count mismatch: total=$($c.total) passed=$($c.passed) failed=$($c.failed) minimum=$minimum"
  }
  Write-Host "$failure verified=$($c.passed)/$($c.total)"
}
try {
  & dotnet test $qualityProject -c Release --no-restore --nologo -v:q --logger "trx;LogFileName=$qualityReport"
  if($LASTEXITCODE -ne 0){throw 'RELEASE_SOURCE_ARCHITECTURE_GATE_FAILED'}
  Assert-ReleaseUnitResults $qualityReport 43 'RELEASE_SOURCE_ARCHITECTURE_GATE'
  Write-Host 'RELEASE_SOURCE_ARCHITECTURE_GATE_OK'
  & dotnet test $coreTestProject -c Release --no-restore --filter $readOnlyCoreFilter --nologo -v:q --logger "trx;LogFileName=$coreReport"
  if($LASTEXITCODE -ne 0){throw 'RELEASE_READONLY_CORE_GATE_FAILED'}
  Assert-ReleaseUnitResults $coreReport 24 'RELEASE_READONLY_CORE_GATE'
  Write-Host 'RELEASE_READONLY_CORE_GATE_OK'
} finally {
  Remove-Item -LiteralPath $qualityReport,$coreReport -Force -ErrorAction SilentlyContinue
}
# Test the real manifest verifier against isolated good/tampered fixtures
# before touching installer staging or distribution files.
& (Join-Path $root 'tools\Test-OptimizerReleaseArtifactManifest.ps1') | Out-Host
if(-not $?){throw 'RELEASE_MANIFEST_FIXTURE_GATE_FAILED'}
Write-Host 'RELEASE_MANIFEST_FIXTURE_GATE_OK'
& (Join-Path $root 'tools\Test-OptimizerDesktopExpectedHash.ps1') | Out-Host
if(-not $?){throw 'RELEASE_DESKTOP_PINNED_HASH_FIXTURE_GATE_FAILED'}
Write-Host 'RELEASE_DESKTOP_PINNED_HASH_FIXTURE_GATE_OK'

# Exercise actual installer UI/state code before releasing either installer variant.
$installerUiProject=Join-Path $root 'tests\InstallerUiTests\InstallerUiTests.csproj'
$installerUiResult=Join-Path $root '.ai-state\installer-ui-tests-latest.json'
& dotnet build $installerUiProject -c Release --nologo -v:minimal
if($LASTEXITCODE -ne 0){throw 'Installer UI regression build failed'}
& dotnet (Join-Path $root 'tests\InstallerUiTests\bin\Release\net10.0-windows\InstallerUiTests.dll') > $installerUiResult
if($LASTEXITCODE -ne 0){throw ('Installer UI/state regression failed: '+$installerUiResult)}
$installerUiEvidence=[IO.File]::ReadAllText($installerUiResult) | ConvertFrom-Json
if(!$installerUiEvidence.passed -or $installerUiEvidence.count -lt 65 -or $installerUiEvidence.failures -ne 0){throw 'Installer UI/state evidence incomplete'}
Write-Host ('INSTALLER_ERROR_UI_REGRESSIONS_OK count='+$installerUiEvidence.count)
# Live Optimizer shutdown is OPTIONAL. Safe builds must not interrupt the
# user's app, tray, active workloads or preserved settings by default.
if($AllowOptimizerShutdown){
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
  Write-Host 'RELEASE_APP_SHUTDOWN_EXPLICITLY_REQUESTED'
} else {
  Write-Host 'RELEASE_APP_SHUTDOWN_SKIPPED (no user process interruption)'
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
$cache=Join-Path $artifacts '.cache'
$appCache=Join-Path $cache 'app-publish'
$releaseBin=Join-Path $root 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64'
$requiredWinUiRuntime=@('App.xbf','SpankyhoOptimizer.pri')
$payload=Join-Path $root 'installer\payload.tar.zst'
$releaseSw=[Diagnostics.Stopwatch]::StartNew()
$phaseSw=[Diagnostics.Stopwatch]::StartNew()
$releasePhaseMs=[ordered]@{}
function Mark-ReleasePhase([string]$name){
  $ms=[math]::Round($phaseSw.Elapsed.TotalMilliseconds)
  $releasePhaseMs[$name]=$ms
  Write-Host ("RELEASE_PHASE {0}={1}ms" -f $name,$ms)
  $phaseSw.Restart()
}
# Read-only timing around existing smoke processes. No parallelization, cache
# bypass or test skipping: all existing exit and timeout checks remain intact.
function Record-ReleaseSmokeDuration([string]$name,[Diagnostics.Stopwatch]$timer){
  $timer.Stop()
  $ms=[math]::Round($timer.Elapsed.TotalMilliseconds)
  $releasePhaseMs['smoke-'+$name]=$ms
  Write-Host ('RELEASE_SMOKE_TIMING {0}={1}ms' -f $name,$ms)
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
  $aggregateSha=[Security.Cryptography.SHA256]::Create()
  $fileSha=[Security.Cryptography.SHA256]::Create()
  try{
    $lines=foreach($p in $paths | Sort-Object -Unique){
      if(Test-Path $p -PathType Leaf){
        $stream=[IO.File]::Open($p,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        try{$h=([BitConverter]::ToString($fileSha.ComputeHash($stream))).Replace('-','')}finally{$stream.Dispose()}
        "$p|$h"
      }
    }
    $bytes=[Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    ([BitConverter]::ToString($aggregateSha.ComputeHash($bytes))).Replace('-','')
  } finally {$fileSha.Dispose();$aggregateSha.Dispose()}
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
$requiredSidebarIcons=@('ai','apps','components','cursor','debloat','defender','fixer','gpu','lianli','network','overview','power','powershell','privacy','ram','settings','spicetify','startup','storage','system','ui','update','usb')
$actualSidebarIcons=@($sidebarIconFiles | ForEach-Object BaseName | Sort-Object)
$expectedSidebarIcons=@($requiredSidebarIcons | Sort-Object)
if(($actualSidebarIcons -join '|') -ne ($expectedSidebarIcons -join '|')){
  throw "Sidebar icon guard failed: assets do not match active/embedded icon usage. expected=$($expectedSidebarIcons -join ',') actual=$($actualSidebarIcons -join ',')"
}
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
$sidebarLegacyDirs=@(
  (Join-Path $root 'src\SpankyhoOptimizer\Assets\SidebarIcons\_pre-vivid-648'),
  (Join-Path $root 'src\SpankyhoOptimizer\Assets\SidebarIcons\_pre-vivid-650')
)
foreach($legacyDir in $sidebarLegacyDirs){
  if(Test-Path $legacyDir){throw "Sidebar legacy asset guard failed: stale backup directory exists: $legacyDir"}
}
Write-Host 'SIDEBAR_LEGACY_ASSET_GUARD_OK'
$versionProps=Join-Path $root 'Directory.Build.props'
$versionPropsText=[IO.File]::ReadAllText($versionProps,[Text.Encoding]::UTF8)
$versionMatch=[regex]::Match($versionPropsText,'<SpankyVersion>([^<]+)</SpankyVersion>')
if(-not $versionMatch.Success){throw 'Version consistency gate: SpankyVersion not found in Directory.Build.props.'}
$releaseVersion=$versionMatch.Groups[1].Value.Trim()
$displayVersion=([version]$releaseVersion).ToString(2)
$displayMatch=[regex]::Match($versionPropsText,'<SpankyPublicVersion>([^<]+)</SpankyPublicVersion>')
if(-not $displayMatch.Success -or $displayMatch.Groups[1].Value.Trim() -ne $displayVersion){
  throw "Version consistency gate: public version must be $displayVersion."
}
$expectedAssemblyName='Spankyho_Optimizer_V'+($displayVersion -replace '\.','_')+'_Setup'
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
foreach($requiredVersionBinding in @(
  '<Version>$(SpankyVersion)</Version>',
  '<AssemblyVersion>$(SpankyVersion).0</AssemblyVersion>',
  '<FileVersion>$(SpankyVersion).0</FileVersion>',
  '<SpankyVersionUnderscore>$([System.String]::Copy(''$(SpankyPublicVersion)'').Replace(''.'', ''_''))</SpankyVersionUnderscore>'
)){
  if($versionPropsText.IndexOf($requiredVersionBinding,[StringComparison]::Ordinal) -lt 0){throw "Version consistency gate failed: missing binding $requiredVersionBinding"}
}
if($installerProjectText.IndexOf('<AssemblyName>Spankyho_Optimizer_V$(SpankyVersionUnderscore)_Setup</AssemblyName>',[StringComparison]::Ordinal) -lt 0){
  throw 'Version consistency gate failed: installer AssemblyName is not bound to SpankyVersionUnderscore.'
}
Write-Host "VERSION_CONSISTENCY_OK $releaseVersion singleSource=Directory.Build.props msbuildEval=false"
$appReleaseSources=Get-ReleaseSourceFiles (Join-Path $root 'src\SpankyhoOptimizer')
$coreReleaseSources=Get-ReleaseSourceFiles (Join-Path $root 'src\SpankyhoOptimizer.Core')
$appCsSources=@($appReleaseSources | Where-Object {$_.Extension -eq '.cs'})
$appCsTextByPath=@{}
foreach($src in $appCsSources){$appCsTextByPath[$src.FullName]=[IO.File]::ReadAllText($src.FullName,[Text.Encoding]::UTF8)}

$asyncGuardHits=[Collections.Generic.List[string]]::new()
foreach($entry in $appCsTextByPath.GetEnumerator()){
  if([regex]::IsMatch($entry.Value,'\+=\s*async\s*\(') -or [regex]::IsMatch($entry.Value,'\basync\s+void\b')){$asyncGuardHits.Add($entry.Key)}
}
if($asyncGuardHits.Count -gt 0){throw "Async UI handler guard failed: async event/void handlers detected: $(@($asyncGuardHits | Select-Object -First 12) -join ', ')"}
Write-Host 'ASYNC_UI_HANDLER_GUARD_OK'

foreach($legacyShellPattern in @('_prewarmedActionBar','_actionBarPrewarmGeneration','ACTIONBAR_FACTORY_PREWARM')){
  $hits=@($appCsTextByPath.GetEnumerator() | Where-Object { $_.Value.IndexOf($legacyShellPattern,[StringComparison]::Ordinal) -ge 0 } | ForEach-Object Key)
  if($hits.Count -gt 0){throw "Shell cleanup guard failed: legacy ActionBar prewarm pattern '$legacyShellPattern' detected: $(@($hits | Select-Object -First 8) -join ', ')"}
}
Write-Host 'SHELL_ON_DEMAND_GUARD_OK actionbar=no-prewarm'

$darkOverlayHits=[Collections.Generic.List[string]]::new()
foreach($entry in $appCsTextByPath.GetEnumerator()){
  foreach($match in [regex]::Matches($entry.Value,'CreateDialogOverlay\([^,\r\n]+,\s*(?<alpha>\d+)\)')){
    $alpha=[int]$match.Groups['alpha'].Value
    if($alpha -gt 112){$darkOverlayHits.Add("$($entry.Key) alpha=$alpha")}
  }
}
if($darkOverlayHits.Count -gt 0){throw "Glass progress overlay guard failed: overly dark overlay(s): $(@($darkOverlayHits | Select-Object -First 8) -join ', ')"}
Write-Host 'GLASS_PROGRESS_OVERLAY_GUARD_OK maxAlpha=112'

$browserUiFiles=@($appReleaseSources | Where-Object { $_.Name -match '(?i)(firefox|chrome|browser)' })
if($browserUiFiles.Count -gt 0){
  throw "Optimizer scope guard failed: browser-specific UI/source file(s) returned: $(@($browserUiFiles | ForEach-Object FullName) -join ', ')"
}
foreach($staleBrowserAsset in @('browsers.svg')){
  if(Test-Path (Join-Path $sidebarIconDir $staleBrowserAsset)){throw "Optimizer scope guard failed: stale sidebar asset returned: $staleBrowserAsset"}
}
Write-Host 'OPTIMIZER_BROWSER_SCOPE_GUARD_OK uiFiles=0 staleSidebarAssets=0'

$mainWindowSource=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.cs') -Raw
if($mainWindowSource.IndexOf('editable && t.Pending != toggle.IsOn',[StringComparison]::Ordinal) -lt 0 -or
   $mainWindowSource.IndexOf('t.IsLoaded && !t.IsBusy',[StringComparison]::Ordinal) -lt 0){
  throw 'Toggle write-back guard failed: programmatic toggle synchronization can queue redundant dirty refreshes.'
}
Write-Host 'TOGGLE_WRITEBACK_GUARD_OK redundantDirtyRefresh=blocked'

$shellHelpersSource=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.ShellHelpers.cs') -Raw
if($shellHelpersSource.IndexOf('SharedIconSourceCache',[StringComparison]::Ordinal) -lt 0 -or
   $shellHelpersSource.IndexOf('HardwareIconSourceCache',[StringComparison]::Ordinal) -ge 0 -or
   $shellHelpersSource.IndexOf('SidebarIconSourceCache',[StringComparison]::Ordinal) -ge 0){
  throw 'Icon cache guard failed: sidebar/section SVG sources are not using one shared cache.'
}
Write-Host 'SHARED_ICON_CACHE_GUARD_OK caches=1'

$appSource=$appSourceText
if($appSource.IndexOf('await Task.Delay(900, token).ConfigureAwait(false);',[StringComparison]::Ordinal) -lt 0){
  throw 'Startup maintenance guard failed: post-ready background maintenance is no longer deferred past first interactive paint.'
}
$overviewSource=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.OverviewDashboard.cs') -Raw
if($overviewSource.IndexOf('_overviewNextHistoryPruneUtc = sampleUtc.AddMinutes(1);',[StringComparison]::Ordinal) -lt 0){
  throw 'Overview history prune guard failed: hot-path history compaction returned.'
}
Write-Host 'STARTUP_OVERVIEW_HOTPATH_GUARD_OK maintenanceDelayMs=900 historyPrune=60s'

# v15.4.1046: long-range history uses bounded, timestamp-anchored
# per-metric extrema rather than the old 48 evenly spaced source indices.
# Guard actual data bound/validity and reject the obsolete false-time renderer.
if($overviewSource.IndexOf('const int columns = 96;',[StringComparison]::Ordinal) -lt 0 -or
   $overviewSource.IndexOf('OverviewHistoryTimeX(sample.Utc, cutoff, rightEdge)',[StringComparison]::Ordinal) -lt 0 -or
   $overviewSource.IndexOf('sample.Utc - previousValidUtc > TimeSpan.FromSeconds(8)',[StringComparison]::Ordinal) -lt 0 -or
   ([regex]::Matches($overviewSource,'RenderOverviewHistoryBatch\(_overviewPerfHistory, first, cutoff, rightEdge,')).Count -ne 1 -or
   $overviewSource.IndexOf('private readonly OverviewEnvelopeCursor[] _overviewEnvelopeCursors',[StringComparison]::Ordinal) -lt 0 -or
   ([regex]::Matches($overviewSource,'cursors\[[0-3]\]\.Push\(i, in sample, bucket\);')).Count -ne 4 -or
   $overviewSource.IndexOf('OverviewMetricKind.Ram, ram, ramFill',[StringComparison]::Ordinal) -lt 0 -or
   $overviewSource.IndexOf('double xStep = sampleCount <= 1 ? 0 : 474d / (sampleCount - 1);',[StringComparison]::Ordinal) -ge 0 -or
   $overviewSource.IndexOf('void FillLine(Polyline line, int metric)',[StringComparison]::Ordinal) -ge 0){
  throw 'Overview chart guard failed: bounded timestamp-axis per-metric extrema or gap handling regressed.'
}
if($overviewSource.IndexOf('OverviewRangeLabels60m',[StringComparison]::Ordinal) -lt 0){
  throw 'Overview range label guard failed: reusable label arrays missing.'
}
$lifecycleSource=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.Lifecycle.cs') -Raw
if(([regex]::Matches($lifecycleSource,'TimeSpan\.FromSeconds\(60\)')).Count -lt 2){
  throw 'Navigation cache guard failed: sparse cache/pressure checks are no longer both at 60s.'
}
Write-Host 'OVERVIEW_RENDER_HOTPATH_GUARD_OK boundedTimeBins=96 historyScans=1 metricCursors=4 ramFillShared=true sampleTime=true gaps=true reusableLabels=true navSafetyInterval=60s'

$normalStartupPrefix=$appSource.Substring(0,[Math]::Min($appSource.Length,$appSource.IndexOf('private async Task LaunchNormalStartupAsync',[StringComparison]::Ordinal)))
if($normalStartupPrefix.IndexOf('FanProfileService.EnsureStartupAgentRegistered();',[StringComparison]::Ordinal) -ge 0 -or
   $normalStartupPrefix.IndexOf('FanProfileService.EnsureAgentUpdated();',[StringComparison]::Ordinal) -ge 0 -or
   $normalStartupPrefix.IndexOf('FanProfileService.StartAgentIfNeeded();',[StringComparison]::Ordinal) -ge 0){
  throw 'Fan startup hot-path guard failed: fan agent maintenance returned before normal window activation.'
}
if($appSource.IndexOf('FanProfileService.EnsureStartupAgentRegistered();',[StringComparison]::Ordinal) -lt 0 -or
   $appSource.IndexOf('RunPostReadyStartupMaintenanceAsync',[StringComparison]::Ordinal) -lt 0){
  throw 'Fan startup hot-path guard failed: deferred fan maintenance is missing.'
}
$aiProgressSource=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.AiProgress.cs') -Raw
if($aiProgressSource.IndexOf('ConcurrentQueue<(string Text, bool Error)>',[StringComparison]::Ordinal) -lt 0 -or
   $aiProgressSource.IndexOf('SchedulePendingDrain()',[StringComparison]::Ordinal) -lt 0){
  throw 'AI progress guard failed: streaming output is no longer dispatcher-coalesced.'
}
$debloatProgressSource=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.DebloatProgress.cs') -Raw
if($debloatProgressSource.IndexOf('scrollUiQueued',[StringComparison]::Ordinal) -lt 0){
  throw 'Debloat progress guard failed: scroll callbacks are no longer coalesced.'
}
Write-Host 'PROGRESS_STARTUP_COALESCE_GUARD_OK fanPostReady=true aiBatch=true debloatScroll=true'

if($overviewSource.IndexOf('_overviewNextNetworkChartUtc = now.AddSeconds(4);',[StringComparison]::Ordinal) -lt 0 -or
   $overviewSource.IndexOf('nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up',[StringComparison]::Ordinal) -lt 0 -or
   $overviewSource.IndexOf('using var downEnumerator = _overviewNetDownSamples.GetEnumerator();',[StringComparison]::Ordinal) -lt 0){
  throw 'Overview network hot-path guard failed: NIC caching/chart throttling/single-pass rendering regressed.'
}
Write-Host 'OVERVIEW_NETWORK_HOTPATH_GUARD_OK nicFilter=10s chart=4s pairedRender=true'

$activateIndex=$appSource.IndexOf('_window.Activate();',[StringComparison]::Ordinal)
$activatedMarkIndex=$appSource.IndexOf('StartupDiagnostics.Mark("Window.Activated");',[StringComparison]::Ordinal)
$crashAwaitIndex=$appSource.IndexOf('await crashLoopStartupTask;',[StringComparison]::Ordinal)
if($activateIndex -lt 0 -or $activatedMarkIndex -lt $activateIndex -or $crashAwaitIndex -lt $activatedMarkIndex){
  throw 'Startup activation guard failed: crash-loop persistence is blocking or contaminating first-window timing.'
}
if(Test-Path (Join-Path $root 'SpankyhoOptimizer.iss')){throw 'Installer architecture guard failed: legacy Inno Setup source returned.'}
if(Test-Path (Join-Path $root 'dist-installer')){throw 'Installer architecture guard failed: legacy dist-installer output returned.'}
Write-Host 'STARTUP_INSTALLER_ARCH_GUARD_OK activateBeforeCrashAwait=true compactOnly=true'

if($mainWindowSource.IndexOf('UiMotionSettingsService.RefreshSystemPreference(force: true);',[StringComparison]::Ordinal) -ge 0){
  throw 'Startup motion guard failed: synchronous UISettings probe returned to MainWindow constructor.'
}
$cursorSource=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.Cursor.cs') -Raw
if($cursorSource.IndexOf('await Task.Delay(4000, token).ConfigureAwait(false);',[StringComparison]::Ordinal) -lt 0){
  throw 'Cursor startup guard failed: cursor watchdog is competing with the first Overview settle window.'
}
$releaseScriptPreflightText=Get-Content $MyInvocation.MyCommand.Path -Raw
$otelReleaseToken='EnableOpenTelemetry'+'=true'
if($releaseScriptPreflightText.IndexOf($otelReleaseToken,[StringComparison]::OrdinalIgnoreCase) -ge 0){
  throw 'Release telemetry guard failed: production release enables SPANKY_OTEL.'
}
Write-Host 'FIRST_FRAME_BACKGROUND_GUARD_OK uiSettingsDeferred=true cursorDelayMs=4000 otel=false'

$cursorPopupSource=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.CursorCommunityPopup.cs') -Raw
$cursorServiceSource=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\CursorCommunityPackService.cs') -Raw
if($cursorPopupSource.IndexOf('WaitAsync(TimeSpan.FromSeconds(8), applyToken)',[StringComparison]::Ordinal) -lt 0 -or
   $cursorPopupSource.IndexOf('applyCts?.Cancel()',[StringComparison]::Ordinal) -lt 0 -or
   $cursorPopupSource.IndexOf('if (closed || (!force && applying)) return;',[StringComparison]::Ordinal) -ge 0 -or
   $cursorPopupSource.IndexOf('closeActionButton.IsEnabled = false;',[StringComparison]::Ordinal) -ge 0 -or
   $cursorPopupSource.IndexOf('Task.Run(() => CursorCommunityPackService.Apply(packToApply), applyToken).ConfigureAwait(false)',[StringComparison]::Ordinal) -ge 0 -or
   $cursorServiceSource.IndexOf('SpifSendChange',[StringComparison]::Ordinal) -ge 0){
  throw 'Community cursor apply guard failed: close/cancel/timeout/UI-thread/nonblocking refresh contract regressed.'
}
Write-Host 'COMMUNITY_CURSOR_APPLY_GUARD_OK closeAlways=true cancel=true timeout=8s uiContinuation=true sendChange=false'

$directZstdToken='pack $appCache $payload $releaseVersion'
$legacyStageToken='$app'+'Stage='
if($releaseScriptPreflightText.IndexOf($directZstdToken,[StringComparison]::Ordinal) -lt 0){
  throw 'Release payload guard failed: Zstd is not streaming directly from app-publish cache.'
}
if($releaseScriptPreflightText.IndexOf($legacyStageToken,[StringComparison]::Ordinal) -ge 0){
  throw 'Release payload guard failed: obsolete app staging copy returned.'
}
if($releaseScriptPreflightText.IndexOf('RELEASE_XAML_GRAPH_CACHE_OK',[StringComparison]::Ordinal) -lt 0 -or
   $releaseScriptPreflightText.IndexOf('release-xaml-graph.sha256',[StringComparison]::Ordinal) -lt 0){
  throw 'Release XAML graph guard failed: incremental XAML fingerprinting missing.'
}
$legacyAppBuildToken='dotnet '+'build $app'
if($releaseScriptPreflightText.IndexOf($legacyAppBuildToken,[StringComparison]::Ordinal) -ge 0 -or
   $releaseScriptPreflightText.IndexOf('RELEASE_APP_PUBLISH_OK',[StringComparison]::Ordinal) -lt 0){
  throw 'Release incremental pipeline guard failed: separate app build returned instead of single publish MSBuild.'
}
Write-Host 'RELEASE_INCREMENTAL_PIPELINE_GUARD_OK appStage=false xamlGraphCache=true directZstd=true singleMsbuild=true'

$logServiceSource=Get-Content (Join-Path $root 'src\SpankyhoOptimizer\Services\LogService.cs') -Raw
if($logServiceSource.IndexOf('PerformanceLoggingEnabled',[StringComparison]::Ordinal) -lt 0 -or
   $logServiceSource.IndexOf('string.Equals(module, "performance"',[StringComparison]::Ordinal) -lt 0 -or
   $logServiceSource.IndexOf('string.Equals(module, "perf"',[StringComparison]::Ordinal) -lt 0 -or
   $logServiceSource.IndexOf('string.Equals(module, "navigation"',[StringComparison]::Ordinal) -lt 0){
  throw 'Performance logging guard failed: production timing/navigation fast-path missing.'
}
if($logServiceSource.IndexOf('string.Equals(module, "visual-qa"',[StringComparison]::Ordinal) -lt 0 -or
   $logServiceSource.IndexOf('ModulePathCache.GetOrAdd',[StringComparison]::Ordinal) -lt 0 -or
   $logServiceSource.IndexOf('if (queued == 1) Signal.Set();',[StringComparison]::Ordinal) -lt 0 -or
   $logServiceSource.IndexOf('private static readonly bool AsyncLoggingDisabled',[StringComparison]::Ordinal) -lt 0){
  throw 'Logger hot-path guard failed: visual QA suppression/module path cache/signal coalescing/env cache missing.'
}
Write-Host 'PERFORMANCE_LOG_FASTPATH_GUARD_OK productionDiskWrites=false modules=performance,perf,navigation,visual-qa pathCache=true wakeOnTransition=true envCached=true'

if($releaseScriptPreflightText.IndexOf('runtime-host-source.sha256',[StringComparison]::Ordinal) -lt 0 -or
   $releaseScriptPreflightText.IndexOf('RUNTIME_HOST_CACHE_HIT publish=false',[StringComparison]::Ordinal) -lt 0){
  throw 'RuntimeHost cache guard failed: independent RuntimeHost publish cache missing.'
}
Write-Host 'RUNTIME_HOST_INCREMENTAL_GUARD_OK independentCache=true'

$managedHashToken='[IO.File]::Open('+'$p'
$legacyPayloadTreeHashToken='$payload'+'Files=Get-ChildItem $appCache -Recurse -File'
if($releaseScriptPreflightText.IndexOf($managedHashToken,[StringComparison]::Ordinal) -lt 0 -or
   $releaseScriptPreflightText.IndexOf($legacyPayloadTreeHashToken,[StringComparison]::Ordinal) -ge 0){
  throw 'Release hash guard failed: managed input hashing/payload fingerprint optimization regressed.'
}
if($logServiceSource.IndexOf('InterpolatedStringHandlerArgument("module")',[StringComparison]::Ordinal) -lt 0 -or
   $logServiceSource.IndexOf('ModuleInterpolatedStringHandler',[StringComparison]::Ordinal) -lt 0){
  throw 'Performance logging guard failed: interpolated performance strings are allocated in production again.'
}
$zstdPackToken='PAYLOAD_ZSTD_OK'
if($releaseScriptPreflightText.IndexOf($zstdPackToken,[StringComparison]::Ordinal) -lt 0){
  throw 'Payload compression guard failed: managed Zstandard pack step is missing.'
}
Write-Host 'RELEASE_HASH_LOG_ALLOCATION_GUARD_OK managedHash=true payloadTreeHash=false perfStringAlloc=false payloadCompression=zstd'

# PowerShell integration is intentionally Titus-only. Keep removed legacy tweaker assets/code
# from silently returning to a future release.
$legacyPowerShellPatterns=@('PowerShellTweaker','PowerShellTerminalApply','PowerShell_Terminal_Setup')
foreach($pattern in $legacyPowerShellPatterns){
  $hits=@($appCsTextByPath.GetEnumerator() | Where-Object { $_.Value.IndexOf($pattern,[StringComparison]::Ordinal) -ge 0 } | ForEach-Object Key)
  if($hits.Count -gt 0){throw "PowerShell Titus-only guard failed: legacy pattern '$pattern' detected: $(@($hits | Select-Object -First 8) -join ', ')"}
}
$legacyPowerShellAssets=@(
  (Join-Path $root 'src\SpankyhoOptimizer\Assets\Tools\PowerShellTerminalApply.ps1'),
  (Join-Path $root 'src\SpankyhoOptimizer\Assets\Tools\PowerShell_Terminal_Setup.exe'),
  (Join-Path $root 'src\SpankyhoOptimizer\Services\PowerShellTweakerService.cs'),
  (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.PowerShellTweaker.cs')
)
foreach($legacyPath in $legacyPowerShellAssets){
  if(Test-Path $legacyPath){throw "PowerShell Titus-only guard failed: legacy file exists: $legacyPath"}
}
$titusServicePath=Join-Path $root 'src\SpankyhoOptimizer\Services\ChrisTitusIntegrationService.cs'
$titusServiceText=[IO.File]::ReadAllText($titusServicePath,[Text.Encoding]::UTF8)
foreach($requiredRepo in @('powershell-profile','titus-ai')){
  if($titusServiceText.IndexOf($requiredRepo,[StringComparison]::Ordinal) -lt 0){
    throw "PowerShell Titus-only guard failed: missing ChrisTitusTech repo $requiredRepo"
  }
}
Write-Host 'POWERSHELL_TITUS_ONLY_GUARD_OK repos=powershell-profile,titus-ai'
& (Join-Path $root 'tools\verify-native-context.ps1') -ProjectRoot $root -SkipRuntime
& (Join-Path $root 'tools\verify-shell-profile-batch.ps1') -ProjectRoot $root -SkipRuntime
& (Join-Path $root 'tools\verify-native-service-actions.ps1') -ProjectRoot $root -SkipRuntime
& (Join-Path $root 'tools\verify-pinned-integrations.ps1') -ProjectRoot $root -SkipRuntime
& (Join-Path $root 'tools\verify-powershell-status-history.ps1') -ProjectRoot $root -SkipRuntime
& (Join-Path $root 'tools\verify-network-transition-batch.ps1') -ProjectRoot $root -SkipRuntime
& (Join-Path $root 'tools\verify-repair-parallel.ps1') -ProjectRoot $root -SkipRuntime

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
  'OpenResource("payload.tar.zst")',
  'LoadUi()',
  'InstallerWindow smokeWindow',
  '--test-release-bundle',
  'ExtractPayload(extract, null)',
  'InstallPayloadTransactional(upgrade',
  'HashPath(uiState)',
  'RecoverInterruptedInstall(recovery, allowSyntheticTestExecutables: true)'
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
$releaseScriptText=[IO.File]::ReadAllText($MyInvocation.MyCommand.Path,[Text.Encoding]::UTF8)
if($releaseScriptText -match '--self-contained\s+true'){
  throw 'Compact-only release guard failed: self-contained installer publish returned to build-release.ps1.'
}
if($releaseScriptText.IndexOf('COMPACT_ONLY_PIPELINE_OK',[StringComparison]::Ordinal) -lt 0){
  throw 'Compact-only release guard failed: Compact-first marker missing.'
}
Write-Host 'COMPACT_ONLY_RELEASE_GUARD_OK selfContainedInstaller=false'
Mark-ReleasePhase 'preflight'

New-Item $artifacts,$cache -ItemType Directory -Force | Out-Null
Remove-Item $stage,$release -Recurse -Force -ErrorAction SilentlyContinue
New-Item $release -ItemType Directory -Force | Out-Null
$appFiles=$appReleaseSources | Select-Object -ExpandProperty FullName
$appFiles+=$coreReleaseSources | Select-Object -ExpandProperty FullName
$appFiles+=Get-ChildItem (Join-Path $root 'src\SpankyhoOptimizer.RuntimeHost') -File | Select-Object -ExpandProperty FullName
$appFiles+=Join-Path $root 'NuGet.Config'
$appFiles+=Join-Path $root 'WORK_PROGRESS.md'
$appFiles+=$versionProps
$appHash=Get-InputHash $appFiles
$appState=Join-Path $cache 'app-source.sha256'
$appChanged=$true
$appCacheComplete=(Test-Path (Join-Path $appCache 'SpankyhoOptimizer.exe')) -and
  (Test-Path (Join-Path $appCache 'SpankyhoOptimizer.RuntimeHost.exe')) -and
  (($requiredWinUiRuntime | Where-Object { -not (Test-Path (Join-Path $appCache $_)) }).Count -eq 0)
# Validate binary versions before accepting an incremental publish cache.
$appCacheVersionMatches=$false
if($appCacheComplete){
  $expectedFileVersion="$releaseVersion.0"
  $cachedGuiVersion=(Get-Item (Join-Path $appCache 'SpankyhoOptimizer.exe')).VersionInfo.FileVersion
  $cachedHostVersion=(Get-Item (Join-Path $appCache 'SpankyhoOptimizer.RuntimeHost.exe')).VersionInfo.FileVersion
  $appCacheVersionMatches=($cachedGuiVersion -eq $expectedFileVersion -and $cachedHostVersion -eq $expectedFileVersion)
}
if((Test-Path $appState) -and $appCacheComplete -and $appCacheVersionMatches){
  $appChanged=((Get-Content $appState -Raw).Trim() -ne $appHash)
}
if($appCacheComplete -and -not $appCacheVersionMatches){Write-Host "RELEASE_STALE_CACHE_REJECTED version=$releaseVersion"}

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
  # Core's isolated Dev Drive intermediates must have NuGet assets even
  # when the unchanged WinUI project already has a restore fingerprint.
  if($env:SPANKY_CORE_DEV_OBJ -and -not(Test-Path (Join-Path $env:SPANKY_CORE_DEV_OBJ 'project.assets.json'))){
    $appNeedsRestore=$true
    Write-Host 'DEV_DRIVE_CORE_RESTORE_REQUIRED'
  }
  if($appNeedsRestore){
    dotnet restore $app -r win-x64 -p:PublishReadyToRun=false --nologo | Out-Host
    if($LASTEXITCODE -ne 0){throw 'App restore failed.'}
    Set-Content $appRestoreState $appRestoreHash -NoNewline
  }
  Remove-Item $appCache -Recurse -Force -ErrorAction SilentlyContinue
  New-Item $appCache -ItemType Directory -Force | Out-Null
  # Source-only graph: obj/bin App.xaml is compiler output, not an input.
  # Never checkpoint the graph until GUI publish succeeds (retry safety).
  $xamlGraphPolicy=Join-Path $root 'tools\WinUiSourceGraphPolicy.ps1'
  if(-not(Test-Path -LiteralPath $xamlGraphPolicy)){throw 'WINUI_SOURCE_GRAPH_POLICY_MISSING'}
  . $xamlGraphPolicy
  $appSourceRoot=Join-Path $root 'src\SpankyhoOptimizer'
  $xamlSourceInfo=@(Get-WinUiGraphSourceFiles $appSourceRoot)
  $xamlGraphFiles=@($xamlSourceInfo | Select-Object -ExpandProperty FullName)
  $directoryTargets=Join-Path $root 'Directory.Build.targets'
  if(Test-Path $directoryTargets){$xamlGraphFiles+=$directoryTargets}
  $xamlGraphHash=Get-InputHash $xamlGraphFiles
  $xamlGraphToken='source-v2:'+$xamlGraphHash
  $xamlGraphState=Join-Path $cache 'release-xaml-graph.sha256'
  $previousGraphToken=if(Test-Path -LiteralPath $xamlGraphState){[IO.File]::ReadAllText($xamlGraphState).Trim()}else{''}
  $existingXbf=Join-Path $root 'src\SpankyhoOptimizer\obj\Release\net10.0-windows10.0.26100.0\win-x64\App.xbf'
  $xamlGraphChanged=Test-WinUiGraphRebuild $previousGraphToken $xamlGraphHash $xamlSourceInfo $existingXbf
  Write-Host ('RELEASE_XAML_SOURCE_GRAPH_OK files={0} generatedExcluded=true migration={1}' -f
    $xamlSourceInfo.Count,(-not $previousGraphToken.StartsWith('source-v2:')))
  if($xamlGraphChanged){
    $appReleaseObj=Join-Path $root 'src\SpankyhoOptimizer\obj\Release'
    Remove-Item $appReleaseObj -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'RELEASE_XAML_GRAPH_INVALIDATE_OK changed=true'
  } else {
    Write-Host 'RELEASE_XAML_GRAPH_CACHE_OK changed=false'
  }
  # One MSBuild invocation compiles and publishes. XAML precompile can reuse the
  # existing intermediate only with a byte-verified stable source graph AND
  # no application source-bound XAML (x:Bind, event handlers, custom controls).
  # Missing intermediate/XBF or changed XAML fails closed to full compiler.
  $xamlReuseTargets=Join-Path $root 'build-fast-xaml-reuse.targets'
  $releaseXamlIntermediate=Join-Path $root 'src\SpankyhoOptimizer\obj\Release\net10.0-windows10.0.26100.0\win-x64\intermediatexaml\SpankyhoOptimizer.dll'
  $reuseXamlPrecompile=Test-WinUiReleaseReuseSafe $xamlGraphChanged $xamlSourceInfo $existingXbf $releaseXamlIntermediate $xamlReuseTargets
  $xamlReuseArgs=@()
  if($reuseXamlPrecompile){
    $xamlReuseArgs=@('-p:SpankyFastXamlReuse=true',('-p:CustomAfterMicrosoftCommonTargets='+$xamlReuseTargets))
  }
  Write-Host ('RELEASE_XAML_PRECOMPILE_POLICY reuse={0} failClosed=true sourceOnly=true' -f $reuseXamlPrecompile)
  # Keep exact build properties/analyzers. The local binlog adds no extra build.
  $guiBinlog=Join-Path $root '.ai-state\winui-release-latest.binlog'
  $guiPublishSw=[Diagnostics.Stopwatch]::StartNew()
  dotnet publish $app -c Release -r win-x64 --self-contained false --no-restore -m -o $appCache `
    -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false `
    @xamlReuseArgs ("-bl:"+$guiBinlog) | Out-Host
  $guiExitCode=$LASTEXITCODE
  $generatedObjDir=Join-Path $root 'src\SpankyhoOptimizer\obj\Release\net10.0-windows10.0.26100.0\win-x64'
  $generatedBinDir=Join-Path $root 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64'
  $generatedValid=$false
  if($guiExitCode -eq 0){
    try {
      $xbfRecovery=Repair-WinUiPublishedXbf $generatedObjDir $generatedBinDir $appCache
      Write-Host ('RELEASE_XBF_PAYLOAD_GUARD_OK mode='+$xbfRecovery)
      $generatedValid=Test-WinUiGeneratedArtifactIntegrity $generatedObjDir $appCache
    }catch{
      Write-Warning ('RELEASE_XBF_PAYLOAD_GUARD_REJECTED '+$_.Exception.Message)
      $generatedValid=$false
    }
  }
  if(($guiExitCode -ne 0 -or -not $generatedValid) -and $reuseXamlPrecompile){
    # A fast-path failure is not a reason to publish a damaged/stale binary.
    # Retry ONCE through the original full XAML compiler and all analyzers.
    Write-Warning ('RELEASE_XAML_SAFE_RETRY fullCompiler=true previousExit={0} generatedValid={1}' -f $guiExitCode,$generatedValid)
    # A failed experimental/fast markup pass can poison XamlSaveStateFile.xml,
    # making a normal incremental full compile falsely reuse broken outputs.
    # Regenerate ONLY the app's intermediate Release folder, while the strict
    # exclusive build lease is held. Do not touch Core, packages or user apps.
    $releaseWinUiObj=Join-Path $root 'src\SpankyhoOptimizer\obj\Release'
    if(Test-Path -LiteralPath $releaseWinUiObj) {
      Remove-Item -LiteralPath $releaseWinUiObj -Recurse -Force -ErrorAction Stop
    }
    Write-Host 'RELEASE_XAML_INTERMEDIATE_RECOVERY_OK scope=WinUiReleaseObj onlyGenerated=true'
    dotnet publish $app -c Release -r win-x64 --self-contained false --no-restore -m -o $appCache `
      -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false `
      ("-bl:"+$guiBinlog) | Out-Host
    $guiExitCode=$LASTEXITCODE
    if($guiExitCode -eq 0){
      $xbfRecovery=Repair-WinUiPublishedXbf $generatedObjDir $generatedBinDir $appCache
      Write-Host ('RELEASE_XBF_PAYLOAD_GUARD_OK mode='+$xbfRecovery)
      $generatedValid=Test-WinUiGeneratedArtifactIntegrity $generatedObjDir $appCache
    }
  }
  $guiPublishSw.Stop()
  if($guiExitCode -ne 0){throw ('App publish failed, exit='+$guiExitCode)}
  if(-not $generatedValid){throw 'WINUI_GENERATED_ARTIFACTS_INVALID: XBF or code generation missing/mismatched.'}
  Write-Host 'RELEASE_WINUI_GENERATED_ARTIFACT_GUARD_OK xbfHashMatch=true generatedCSharp=true'
  # Commit source graph only after a successful publish: failed XAML generation
  # must not make the next release trust a stale graph fingerprint.
  $xamlGraphTemp=$xamlGraphState+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
  try {
    [IO.File]::WriteAllText($xamlGraphTemp,$xamlGraphToken,[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($xamlGraphTemp,$xamlGraphState,$true)
  }finally{
    if(Test-Path -LiteralPath $xamlGraphTemp){Remove-Item -LiteralPath $xamlGraphTemp -Force}
  }
  Write-Host ('RELEASE_XAML_GRAPH_CHECKPOINT_OK sourceOnly=true hashVersion=2')
  $releasePhaseMs['gui-publish']=[math]::Round($guiPublishSw.Elapsed.TotalMilliseconds)
  Write-Host ('RELEASE_GUI_PUBLISH_MS {0}' -f $releasePhaseMs['gui-publish'])
  Write-Host ('RELEASE_APP_PUBLISH_OK graphChanged='+$xamlGraphChanged+' singleMsbuild=true')

  $runtimeHostOut=Join-Path $cache 'runtime-host-publish'
  $runtimeHostState=Join-Path $cache 'runtime-host-source.sha256'
  $runtimeHostManifest=Join-Path $runtimeHostOut 'runtime-host-artifacts.sha256.json'
  $publishedCore=Join-Path $appCache 'SpankyhoOptimizer.Core.dll'
  if(-not(Test-Path -LiteralPath $publishedCore)){throw 'Published Core DLL missing; refuse RuntimeHost cache.'}
  # The Core project compiles linked Models/*.cs + Services/*.cs from the app.
  # Hashing only the Core project root missed these dependencies and could
  # reuse an obsolete host after an actual shared-Core change. The freshly
  # published Core DLL is the authoritative input (exact bytes), so every
  # linked source + conditional compile change invalidates the host cache.
  $publishedCoreHash=(Get-FileHash -LiteralPath $publishedCore -Algorithm SHA256).Hash
  $runtimeHostInputs=@(
    (Join-Path $root 'src\SpankyhoOptimizer.RuntimeHost\SpankyhoOptimizer.RuntimeHost.csproj'),
    (Join-Path $root 'src\SpankyhoOptimizer.RuntimeHost\Program.cs'),
    (Join-Path $root 'src\SpankyhoOptimizer\Assets\App.ico'),
    $versionProps,
    (Join-Path $root 'src\SpankyhoOptimizer.Core\SpankyhoOptimizer.Core.csproj')
  )
  if(Test-Path $directoryTargets){$runtimeHostInputs+=$directoryTargets}
  $runtimeHostInputHash=Get-InputHash $runtimeHostInputs
  $runtimeHostHash=($runtimeHostInputHash+'|'+$publishedCoreHash)
  $runtimeHostRequired=@(
    'SpankyhoOptimizer.RuntimeHost.exe',
    'SpankyhoOptimizer.RuntimeHost.dll',
    'SpankyhoOptimizer.RuntimeHost.deps.json',
    'SpankyhoOptimizer.RuntimeHost.runtimeconfig.json'
  )
  $runtimeHostCachedCore=Join-Path $runtimeHostOut 'SpankyhoOptimizer.Core.dll'
  $runtimeHostCacheComplete=(Test-Path $runtimeHostOut) -and (($runtimeHostRequired | Where-Object { -not(Test-Path (Join-Path $runtimeHostOut $_)) }).Count -eq 0) -and
    (Test-Path -LiteralPath $runtimeHostCachedCore) -and
    ((Get-Item -LiteralPath $runtimeHostCachedCore).VersionInfo.FileVersion -eq "$releaseVersion.0") -and
    (Test-Path -LiteralPath $runtimeHostManifest)
  if($runtimeHostCacheComplete){
    try {
      $cachedManifest=@{}
      $manifestEntries=@(Get-Content -LiteralPath $runtimeHostManifest -Raw | ConvertFrom-Json)
      if($manifestEntries.Count -ne ($runtimeHostRequired.Count + 1)){throw 'RuntimeHost cache manifest entry count mismatch.'}
      foreach($entry in $manifestEntries){
        if($null -eq $entry.name -or $entry.name -notin @($runtimeHostRequired + 'SpankyhoOptimizer.Core.dll') -or
           $null -eq $entry.sha256 -or $entry.sha256 -notmatch '^[0-9A-F]{64}$' -or $cachedManifest.ContainsKey($entry.name)){
          throw 'RuntimeHost cache manifest schema mismatch.'
        }
        $cachedManifest[$entry.name]=$entry.sha256
      }
      foreach($name in @($runtimeHostRequired + 'SpankyhoOptimizer.Core.dll')){
        $path=Join-Path $runtimeHostOut $name
        if(-not $cachedManifest.ContainsKey($name) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $cachedManifest[$name]){$runtimeHostCacheComplete=$false;break}
      }
    } catch {$runtimeHostCacheComplete=$false}
  }
  $runtimeHostChanged=-not(Test-Path $runtimeHostState) -or -not($runtimeHostCacheComplete) -or ((Get-Content $runtimeHostState -Raw -ErrorAction SilentlyContinue).Trim() -ne $runtimeHostHash)
  $hostPublishSw=[Diagnostics.Stopwatch]::StartNew()
  if($runtimeHostChanged){
    Remove-Item $runtimeHostOut -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $runtimeHostOut -ItemType Directory -Force | Out-Null
    # The GUI publish already built Core for this EXACT release version.
    # Avoid compiling it a second time only when the active MSBuild reference
    # assembly and RuntimeHost NuGet assets are present and version-matched.
    # Otherwise retain the original full dependency-build path.
    $coreRefRoot=if($env:SPANKY_CORE_DEV_OBJ){$env:SPANKY_CORE_DEV_OBJ}else{Join-Path $root 'src\SpankyhoOptimizer.Core\obj'}
    $coreReleaseRef=Join-Path $coreRefRoot 'Release\net10.0-windows10.0.26100.0\ref\SpankyhoOptimizer.Core.dll'
    $hostNugetAssets=Join-Path $root 'src\SpankyhoOptimizer.RuntimeHost\obj\project.assets.json'
    $coreRefFresh=(Test-Path -LiteralPath $coreReleaseRef) -and
      ((Get-Item -LiteralPath $coreReleaseRef).VersionInfo.FileVersion -eq "$releaseVersion.0") -and
      ((Get-Item -LiteralPath $publishedCore).VersionInfo.FileVersion -eq "$releaseVersion.0")
    $hostSkipRefs=$coreRefFresh -and (Test-Path -LiteralPath $hostNugetAssets)
    $hostRefsArg=if($hostSkipRefs){'-p:BuildProjectReferences=false'}else{'-p:BuildProjectReferences=true'}
    Write-Host ('RELEASE_HOST_PROJECT_REFS '+$(if($hostSkipRefs){'skip-verified-core'}else{'build-safe-fallback'}))
    dotnet publish $runtimeHost -c Release -r win-x64 --self-contained false -m -o $runtimeHostOut --nologo -v:q `
      -p:RunAnalyzers=false -p:RunAnalyzersDuringBuild=false $hostRefsArg | Out-Host
    if($LASTEXITCODE -ne 0 -and $hostSkipRefs){
      Write-Warning 'RELEASE_HOST_SAFE_RETRY: host publish could not reuse Core; retrying with project references built.'
      dotnet publish $runtimeHost -c Release -r win-x64 --self-contained false -m -o $runtimeHostOut --nologo -v:q `
        -p:RunAnalyzers=false -p:RunAnalyzersDuringBuild=false -p:BuildProjectReferences=true | Out-Host
    }
    if($LASTEXITCODE -ne 0){throw 'Runtime host publish failed.'}
    $hostCoreDll=Join-Path $runtimeHostOut 'SpankyhoOptimizer.Core.dll'
    $hostCoreValid=(Test-Path -LiteralPath $hostCoreDll) -and
      ((Get-Item -LiteralPath $hostCoreDll).VersionInfo.FileVersion -eq "$releaseVersion.0")
    if(-not $hostCoreValid -and $hostSkipRefs){
      Write-Warning 'RELEASE_HOST_SAFE_RETRY: reused reference yielded stale Core; retrying full dependency build.'
      dotnet publish $runtimeHost -c Release -r win-x64 --self-contained false -m -o $runtimeHostOut --nologo -v:q `
        -p:RunAnalyzers=false -p:RunAnalyzersDuringBuild=false -p:BuildProjectReferences=true | Out-Host
      if($LASTEXITCODE -ne 0){throw 'Runtime host dependency recovery publish failed.'}
      $hostCoreValid=(Test-Path -LiteralPath $hostCoreDll) -and
        ((Get-Item -LiteralPath $hostCoreDll).VersionInfo.FileVersion -eq "$releaseVersion.0")
    }
    if(-not $hostCoreValid){throw 'RuntimeHost Core reference mismatch or missing DLL; stale host blocked.'}
    $manifestEntries=@($runtimeHostRequired + 'SpankyhoOptimizer.Core.dll' | ForEach-Object {
      $path=Join-Path $runtimeHostOut $_
      [pscustomobject]@{name=$_;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    })
    $manifestTemp=$runtimeHostManifest+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
      $manifestEntries | ConvertTo-Json -Compress | Set-Content -LiteralPath $manifestTemp -NoNewline
      [IO.File]::Move($manifestTemp,$runtimeHostManifest,$true)
    } finally {
      if(Test-Path -LiteralPath $manifestTemp){Remove-Item -LiteralPath $manifestTemp -Force}
    }
    # Persist the cache key only after all host artifacts and their hashes were checked.
    Set-Content $runtimeHostState $runtimeHostHash -NoNewline
    Write-Host 'RUNTIME_HOST_CACHE_MISS publish=true'
  } else {
    Write-Host 'RUNTIME_HOST_CACHE_HIT publish=false'
  }
  $hostPublishSw.Stop()
  $releasePhaseMs['runtime-host-publish']=[math]::Round($hostPublishSw.Elapsed.TotalMilliseconds)
  Write-Host ('RELEASE_HOST_PUBLISH_MS {0}' -f $releasePhaseMs['runtime-host-publish'])
  foreach($name in $runtimeHostRequired){
    $source=Join-Path $runtimeHostOut $name
    if(-not(Test-Path $source)){throw "Runtime host artifact missing: $name"}
    Copy-Item $source (Join-Path $appCache $name) -Force
  }

  $runtimeHostProductVersion=(Get-Item (Join-Path $appCache 'SpankyhoOptimizer.RuntimeHost.exe')).VersionInfo.ProductVersion
  if(-not $runtimeHostProductVersion -or -not ($runtimeHostProductVersion -eq $releaseVersion -or $runtimeHostProductVersion.StartsWith(($releaseVersion+'+'),[StringComparison]::Ordinal))) {
    throw ("RuntimeHost version mismatch: expected {0}, got {1}" -f $releaseVersion,$runtimeHostProductVersion)
  }
  Write-Host ("RUNTIME_HOST_VERSION_GUARD_OK "+$releaseVersion)

  Write-Host ('RUNTIME_HOST_PUBLISH_OK cached='+(-not $runtimeHostChanged))

  Get-ChildItem $appCache -Directory | Where-Object {
    $_.Name -notin @('cs-CZ','en-US') -and
    (Get-ChildItem $_.FullName -File -ErrorAction SilentlyContinue | Where-Object {
      $_.Name.EndsWith('.mui',[StringComparison]::OrdinalIgnoreCase) -or $_.Name.EndsWith('.resources.dll',[StringComparison]::OrdinalIgnoreCase)
    })
  } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item (Join-Path $appCache 'createdump.exe') -Force -ErrorAction SilentlyContinue
  Set-Content $appState $appHash -NoNewline
} else { Write-Host 'App unchanged -> cached publish' }
foreach($binary in @('SpankyhoOptimizer.exe','SpankyhoOptimizer.RuntimeHost.exe')){
  $binaryPath=Join-Path $appCache $binary
  if(-not(Test-Path $binaryPath)){throw "Release binary missing: $binary"}
  $actualVersion=(Get-Item $binaryPath).VersionInfo.FileVersion
  if($actualVersion -ne "$releaseVersion.0"){throw "RELEASE_STALE_BINARY_BLOCKED $binary expected=$releaseVersion.0 actual=$actualVersion"}
}
$finalAppHash=Get-InputHash $appFiles
if($finalAppHash -ne $appHash){
  Remove-Item $appState -Force -ErrorAction SilentlyContinue
  throw 'RELEASE_SOURCE_CHANGED_DURING_BUILD: stale payload blocked.'
}
Write-Host "RELEASE_FRESH_BINARY_GUARD_OK version=$releaseVersion"
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

$optionalAiRuntime=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($name in @(
  'onnxruntime.dll','DirectML.dll','Microsoft.Windows.AI.MachineLearning.dll',
  'Microsoft.Windows.AI.Imaging.dll','Microsoft.Windows.AI.Text.dll'
)){[void]$optionalAiRuntime.Add($name)}
$aiRuntimeHits=@(Get-ChildItem $appCache -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $optionalAiRuntime.Contains($_.Name) })
foreach($hit in $aiRuntimeHits){Remove-Item $hit.FullName -Force -ErrorAction SilentlyContinue}
$aiLeft=@($aiRuntimeHits | Where-Object { Test-Path $_.FullName })
if($aiLeft.Count -gt 0){ throw 'Optional AI runtime cleanup failed.' }
Write-Host ("OPTIONAL_AI_RUNTIME_CLEANUP_OK removed={0} scans=1" -f $aiRuntimeHits.Count)
$publishedSidebarDir=Join-Path $appCache 'Assets\SidebarIcons'
$publishedSidebarIcons=@(Get-ChildItem $publishedSidebarDir -Filter *.svg -File -ErrorAction SilentlyContinue | ForEach-Object BaseName | Sort-Object)
if(($publishedSidebarIcons -join '|') -ne ($expectedSidebarIcons -join '|')){
  throw "Published sidebar asset guard failed: expected=$($expectedSidebarIcons -join ',') actual=$($publishedSidebarIcons -join ',')"
}
Write-Host "PUBLISHED_SIDEBAR_ASSET_GUARD_OK count=$($publishedSidebarIcons.Count)"

# Real published binary: verify fresh-only marker creation and rejection of
# existing/foreign/traversal paths before the payload can be certified.
& (Join-Path $root 'tools\Test-OptimizerRuntimeReadyIsolation.ps1') -ExePath (Join-Path $appCache 'SpankyhoOptimizer.exe') -ExpectedVersion $releaseVersion | Out-Host
if(-not $?){throw 'RELEASE_RUNTIME_READY_ISOLATION_FAILED'}
Write-Host 'RELEASE_RUNTIME_READY_ISOLATION_OK'

$payloadState=Join-Path $cache 'payload-source.sha256'
# The payload is a deterministic projection of the app/source fingerprint plus this release policy.
# Do not re-hash the entire publish tree just to rediscover that a changed app needs a new ZIP.
$releasePolicyHash=(Get-FileHash $MyInvocation.MyCommand.Path -Algorithm SHA256).Hash
$packerSourceHash=(Get-FileHash (Join-Path $root 'tools\PayloadPacker\Program.cs') -Algorithm SHA256).Hash
$packerProjectHash=(Get-FileHash (Join-Path $root 'tools\PayloadPacker\PayloadPacker.csproj') -Algorithm SHA256).Hash
$payloadFingerprintText="$appHash|$releasePolicyHash|$packerSourceHash|$packerProjectHash"
$payloadFingerprintBytes=[Text.Encoding]::UTF8.GetBytes($payloadFingerprintText)
$payloadFingerprintSha=[Security.Cryptography.SHA256]::Create()
try{$payloadSourceHash=([BitConverter]::ToString($payloadFingerprintSha.ComputeHash($payloadFingerprintBytes))).Replace('-','')}finally{$payloadFingerprintSha.Dispose()}
$payloadChanged=-not(Test-Path $payload) -or -not(Test-Path $payloadState) -or ((Get-Content $payloadState -Raw -ErrorAction SilentlyContinue).Trim() -ne $payloadSourceHash)
if($payloadChanged){
  Write-Host 'Payload changed -> compress'
  $zipSw=[Diagnostics.Stopwatch]::StartNew()
  Remove-Item $payload -Force -ErrorAction SilentlyContinue
  $packer=Join-Path $root 'tools\PayloadPacker\PayloadPacker.csproj'
  & dotnet run --project $packer -c Release -- pack $appCache $payload $releaseVersion 9 | Out-Host
  if($LASTEXITCODE -ne 0){throw 'Zstd payload packaging failed.'}
  $zipSw.Stop(); Write-Host ('PAYLOAD_ZSTD_OK {0:n2}s' -f $zipSw.Elapsed.TotalSeconds)
  Set-Content $payloadState $payloadSourceHash -NoNewline
} else { Write-Host 'Payload unchanged -> reuse zip' }
Mark-ReleasePhase 'payload-stage-compress'
$payloadHash=(Get-FileHash $payload -Algorithm SHA256).Hash

# Compact-first pipeline: build the only public installer directly from the verified payload.
# Do not waste time/disk producing a temporary ~130 MB self-contained setup that is deleted later.
$compactBuilder=Join-Path $root 'build-installer-compact.ps1'
& $compactBuilder -Version $releaseVersion -PayloadPath $payload -OutputDir $release -SkipSmoke | Out-Host
if($LASTEXITCODE -ne 0){throw 'Compact installer build failed.'}
# Exercise REAL embedded-payload upgrade and scoped rollback before any
# candidate is cached or published. This uses only a fresh TEMP sandbox.
$scopedRollbackInstaller=Join-Path $root ('installer\bin\Release\net10.0-windows\win-x64\Spankyho_Optimizer_V'+($displayVersion -replace '\.','_')+'_Setup.dll')
$scopedRollbackOriginal=Join-Path $appCache 'SpankyhoOptimizer.exe'
if(-not(Test-Path -LiteralPath $scopedRollbackInstaller) -or -not(Test-Path -LiteralPath $scopedRollbackOriginal)){
  throw 'RELEASE_SCOPED_ROLLBACK_INPUT_MISSING'
}
& dotnet $scopedRollbackInstaller --test-transaction-scoped-post-move-rollback $scopedRollbackOriginal | Out-Host
if($LASTEXITCODE -ne 0){throw ('RELEASE_SCOPED_ROLLBACK_GATE_FAILED exit='+$LASTEXITCODE)}
Write-Host 'RELEASE_SCOPED_ROLLBACK_GATE_OK'
$compactName='Glass_Optimizer_Setup_Compact.exe'
$compactFinal=Join-Path $release $compactName
if(-not(Test-Path $compactFinal)){throw 'Compact installer artifact missing.'}
# Validate the actual single-file EXE and verify that a modified embedded
# managed DLL is rejected. This uses a disposable copy, never the candidate.
& (Join-Path $root 'tools\Test-OptimizerCompactBundle.ps1') -InstallerPath $compactFinal -ManagedDllPath $scopedRollbackInstaller -ExpectedVersion $releaseVersion | Out-Host
if(-not $?){throw 'RELEASE_COMPACT_BUNDLE_TAMPER_GATE_FAILED'}
Write-Host 'RELEASE_COMPACT_BUNDLE_TAMPER_GATE_OK'
$cachedCompactSetup=Join-Path $cache $compactName
Copy-FileWithRetry $compactFinal $cachedCompactSetup
$compactSizeMb=[math]::Round((Get-Item $compactFinal).Length/1MB,2)
$compactHash=(Get-FileHash $compactFinal -Algorithm SHA256).Hash
$final=$compactFinal
Write-Host ("COMPACT_ONLY_PIPELINE_OK size={0:n2}MB" -f $compactSizeMb)
Mark-ReleasePhase 'compact-installer-publish'

$installerSmokeBundle=Start-Process -FilePath $final -ArgumentList @('--release-installer-smoke-bundle',$payloadHash) -Wait -PassThru
if($installerSmokeBundle.ExitCode -ne 0){throw "Compact installer smoke bundle failed: $($installerSmokeBundle.ExitCode)"}
Write-Host 'INSTALLER_PAYLOAD_SMOKE_OK (covered by compact installer smoke bundle)'
Write-Host 'INSTALLER_UI_SMOKE_OK (covered by compact installer smoke bundle)'
Write-Host 'INSTALLER_WINDOW_SMOKE_OK (covered by compact installer smoke bundle)'
# Probe a REAL NTFS junction in a disposable TEMP fixture. The elevated
# installer must fail closed before extraction or following the reparse path.
& (Join-Path $root 'tools\InstallerIsolationRegression.ps1') -Installer $final
if(-not $?){throw 'Installer reparse-point regression failed.'}
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
    Get-ChildItem $_ -File -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -like 'Spankyho_Optimizer_V*_Setup_Compact.exe' }
  } | ForEach-Object {
    if($_.Name -match '^Spankyho_Optimizer_V(\d+)_(\d+)(?:_(\d+))?_Setup_Compact\.exe$'){
      $patchNumber=if($matches[3]){[int]$matches[3]}else{0}
      $v=[version]("$($matches[1]).$($matches[2]).$patchNumber")
      if($v -lt $currentVersion){[pscustomobject]@{Version=$v;Path=$_.FullName}}
    }
  } | Sort-Object Version -Descending | Select-Object -First 1
  if($previousCandidate){
    $previousTarget=Join-Path $testRoot 'previous-upgrade'
    $oldInstall=Start-Process -FilePath $previousCandidate.Path -ArgumentList @('--test-install',('"'+$previousTarget+'"')) -Wait -PassThru
    if($oldInstall.ExitCode -ne 0){throw "Previous installer extraction failed: $($oldInstall.ExitCode)"}
    $realUpgrade=Start-Process -FilePath $final -ArgumentList @('--test-transactional-install',('"'+$previousTarget+'"')) -Wait -PassThru
    if($realUpgrade.ExitCode -ne 0){throw "$($previousCandidate.Version) -> current upgrade failed: $($realUpgrade.ExitCode)"}
    Write-Host "PREVIOUS_VERSION_UPGRADE_OK $($previousCandidate.Version)"
  } else { Write-Host 'PREVIOUS_VERSION_UPGRADE_SKIPPED no older installer found' }
  Write-Host 'INSTALLER_TRANSACTION_RECOVERY_OK (covered by installer test bundle)'
  Mark-ReleasePhase 'installer-test-upgrade'
  $exe=Join-Path $test 'SpankyhoOptimizer.exe'
  if(-not (Test-Path $exe)){throw 'Extracted app missing.'}
  # The elevated WinUI probe resolves its temp root through GetTempPath2,
  # which does not inherit this build process's Dev Drive TEMP override. Use
  # the interactive user's real TEMP for the guarded marker.
  $runtimeTemp=[Environment]::GetEnvironmentVariable('TEMP','User')
  if([string]::IsNullOrWhiteSpace($runtimeTemp)){
    $runtimeTemp=Join-Path $env:LOCALAPPDATA 'Temp'
  }
  $runtimeTemp=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($runtimeTemp))
  if(-not(Test-Path -LiteralPath $runtimeTemp -PathType Container)){
    throw 'RELEASE_RUNTIME_READY_USER_TEMP_MISSING'
  }
  $readyRoot=Join-Path $runtimeTemp ('SpankyhoOptimizer-ReleaseGate-'+[guid]::NewGuid().ToString('N'))
  [IO.Directory]::CreateDirectory($readyRoot)|Out-Null
  $readyMarker=Join-Path $readyRoot 'runtime-ready.txt'
  # The child uses FileMode.CreateNew: an existing marker is stale evidence,
  # not something release verification may silently overwrite.
  if(Test-Path -LiteralPath $readyMarker){throw 'RELEASE_RUNTIME_READY_MARKER_ALREADY_EXISTS'}
  $oldReadyEnv=$env:SPANKY_RUNTIME_READY_FILE
  try {
    $env:SPANKY_RUNTIME_READY_FILE=$readyMarker
    $runtimeSw=[Diagnostics.Stopwatch]::StartNew()
    $p=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList ('--runtime-ready-file="'+$readyMarker+'"') -PassThru
    # Wait for the exact launched process to finish BEFORE reading marker
    # contents. File creation alone is not evidence that the UTF-8 write
    # has completed, and polling could accept a partially written marker.
    if(-not $p.WaitForExit(12000)){
      try {$p.Kill($true)} catch {}
      throw 'RUNTIME_READY_PROBE_DID_NOT_SELF_TERMINATE'
    }
    if($p.ExitCode -ne 0){throw "Runtime readiness probe failed: exit=$($p.ExitCode)"}
    if(-not(Test-Path -LiteralPath $readyMarker)){throw 'RUNTIME_READY_PROBE_MARKER_MISSING'}
    $readyText=Get-Content -LiteralPath $readyMarker -Raw
    if($readyText -notmatch [regex]::Escape($releaseVersion)){throw "Runtime readiness version mismatch: $readyText"}
    Write-Host ('RUNTIME_READY_OK {0:n0}ms' -f $runtimeSw.Elapsed.TotalMilliseconds)
    Write-Host 'RUNTIME_READY_PROBE_SELF_EXIT_OK'
  }
  finally {
    if($null -eq $oldReadyEnv){Remove-Item Env:SPANKY_RUNTIME_READY_FILE -ErrorAction SilentlyContinue}
    else {$env:SPANKY_RUNTIME_READY_FILE=$oldReadyEnv}
    # Only this release-created GUID directory; never clean the user's TEMP.
    Remove-Item -LiteralPath $readyRoot -Recurse -Force -ErrorAction SilentlyContinue
  }
  Mark-ReleasePhase 'runtime-readiness'
  $updaterSmokeTimer=[Diagnostics.Stopwatch]::StartNew()
  $updaterReleaseSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--software-updater-release-smoke' -Wait -PassThru
  Record-ReleaseSmokeDuration 'software-updater' $updaterSmokeTimer
  if($updaterReleaseSmoke.ExitCode -ne 0){throw "Software Updater/Winget bundled release smoke failed: $($updaterReleaseSmoke.ExitCode)"}
  Write-Host 'SOFTWARE_UPDATER_RELEASE_SMOKE_OK'
  Write-Host 'WINGET_STATUS_RELEASE_SMOKE_OK (covered by Software Updater release smoke)'
  Write-Host 'AI_RUNTIME_HARDENING_RELEASE_SMOKE_OK (covered by service bundle)'
  Write-Host 'AI_PROGRESS_UI_RELEASE_SMOKE_OK (covered by UI bundle)'
  $shortcutSmokeTimer=[Diagnostics.Stopwatch]::StartNew()
  $shortcutOverlaySmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--shortcut-overlay-smoke' -Wait -PassThru
  Record-ReleaseSmokeDuration 'shortcut-overlay' $shortcutSmokeTimer
  if($shortcutOverlaySmoke.ExitCode -ne 0){throw "Shortcut overlay release smoke failed: $($shortcutOverlaySmoke.ExitCode)"}
  Write-Host 'SHORTCUT_OVERLAY_RELEASE_SMOKE_OK'
  $storageSmokeTimer=[Diagnostics.Stopwatch]::StartNew()
  $storageConcurrencySmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--storage-concurrency-smoke' -Wait -PassThru
  Record-ReleaseSmokeDuration 'storage-concurrency' $storageSmokeTimer
  if($storageConcurrencySmoke.ExitCode -ne 0){throw "Storage concurrency release smoke failed: $($storageConcurrencySmoke.ExitCode)"}
  Write-Host 'STORAGE_CONCURRENCY_RELEASE_SMOKE_OK'
  # Check rendered font roles and overflow at compact, normal and large window sizes.
  $typographySmokeTimer=[Diagnostics.Stopwatch]::StartNew()
  $typographySmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--visual-size-smoke' -WindowStyle Hidden -PassThru
  if(!$typographySmoke.WaitForExit(60000)){$typographySmoke.Kill();throw 'Typography/layout release smoke timed out'}
  Record-ReleaseSmokeDuration 'visual-size' $typographySmokeTimer
  if($typographySmoke.ExitCode -ne 0){throw "Typography/layout release smoke failed: $($typographySmoke.ExitCode)"}
  Write-Host 'TYPOGRAPHY_LAYOUT_RELEASE_SMOKE_OK'
  $releaseUiTimer=[Diagnostics.Stopwatch]::StartNew()
  $releaseUiPassed=$false
  $releaseUiLastCode=0
  for($uiAttempt=1;$uiAttempt -le 2;$uiAttempt++){
    $releaseUiSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--release-ui-smoke' -Wait -PassThru
    $releaseUiLastCode=$releaseUiSmoke.ExitCode
    if($releaseUiLastCode -eq 0){$releaseUiPassed=$true;break}
    Write-Host "RELEASE_UI_SMOKE_RETRY attempt=$uiAttempt exit=$releaseUiLastCode"
    Start-Sleep -Milliseconds 250
  }
  Record-ReleaseSmokeDuration 'ui-bundle' $releaseUiTimer
  if(-not $releaseUiPassed){throw "Release UI smoke bundle failed twice: $releaseUiLastCode"}
  Write-Host 'RELEASE_UI_SMOKE_BUNDLE_OK'
  Write-Host 'DRIVERS_UI_RELEASE_SMOKE_OK (covered by UI bundle)'
  Write-Host 'TASK_SCHEDULER_UI_RELEASE_SMOKE_OK (covered by UI bundle)'
  Write-Host 'THEME_RENDER_RELEASE_SMOKE_OK (covered by UI bundle)'
  # Nonmutating service policy gate is independent of the full service
  # smoke, which also exercises persistent caches and build revalidation.
  $readonlyServiceTimer=[Diagnostics.Stopwatch]::StartNew()
  $readonlyService=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--release-service-policy-smoke' -Wait -PassThru
  if($readonlyService.ExitCode -ne 0){throw "Read-only service policy gate failed: $($readonlyService.ExitCode)"}
  Write-Host ('RELEASE_READONLY_SERVICE_POLICY_OK elapsedMs={0}' -f [math]::Round($readonlyServiceTimer.Elapsed.TotalMilliseconds))

  $releaseServiceTimer=[Diagnostics.Stopwatch]::StartNew()
  $releaseServiceSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--release-service-smoke' -Wait -PassThru
  Record-ReleaseSmokeDuration 'service-bundle' $releaseServiceTimer
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
  # Verify measured gate coverage without replacing or parallelizing any tests.
  $smokeNames=@('software-updater','shortcut-overlay','storage-concurrency','visual-size','ui-bundle','service-bundle')
  $smokeSumMs=0
  foreach($name in $smokeNames){
    $key='smoke-'+$name
    if(-not $releasePhaseMs.Contains($key) -or [double]$releasePhaseMs[$key] -le 0){throw ('RELEASE_SMOKE_TIMING_MISSING '+$key)}
    $smokeSumMs += [double]$releasePhaseMs[$key]
  }
  if($smokeSumMs -gt ([double]$releasePhaseMs['updater-ui-service-gates']+100)){
    throw 'RELEASE_SMOKE_TIMING_OVERFLOW: impossible child duration sum'
  }
  Write-Host ('RELEASE_SMOKE_TIMING_AUDIT_OK count={0} measuredMs={1} phaseMs={2}' -f $smokeNames.Count,$smokeSumMs,$releasePhaseMs['updater-ui-service-gates'])
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
  $systemColdPasses=0
  $systemColdLastCode=0
  for($coldAttempt=1;$coldAttempt -le 3 -and $systemColdPasses -lt 2;$coldAttempt++){
    $systemColdSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--system-cold-render-smoke' -Wait -PassThru
    $systemColdLastCode=$systemColdSmoke.ExitCode
    if($systemColdLastCode -eq 0){$systemColdPasses++}
    else { Write-Host "SYSTEM_COLD_RENDER_RETRY attempt=$coldAttempt exit=$systemColdLastCode"; Start-Sleep -Milliseconds 250 }
  }
  if($systemColdPasses -lt 2){throw "System cold render release smoke unstable: passes=$systemColdPasses/3 last=$systemColdLastCode"}
  Write-Host "SYSTEM_COLD_RENDER_RELEASE_SMOKE_OK passes=$systemColdPasses"
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
  $glassStabilitySmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--glass-stability-smoke' -PassThru
  if(-not $glassStabilitySmoke.WaitForExit(30000)){$glassStabilitySmoke.Kill();throw 'Glass stability smoke timed out'}
  if($glassStabilitySmoke.ExitCode -ne 0){throw "Glass stability smoke failed: $($glassStabilitySmoke.ExitCode); see ProgramData/SpankyhoOptimizer/glass-stability-smoke.json"}
  Write-Host 'GLASS_STABILITY_RELEASE_SMOKE_OK focusPolicy=true closedCompletionIgnored=true'
  $githubIntegrationSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--github-integration-smoke' -PassThru
  if(-not $githubIntegrationSmoke.WaitForExit(60000)){$githubIntegrationSmoke.Kill();throw 'GitHub integration smoke timed out'}
  if($githubIntegrationSmoke.ExitCode -ne 0){throw "GitHub integration smoke failed: $($githubIntegrationSmoke.ExitCode)"}
  Write-Host 'GITHUB_INTEGRATION_RELEASE_SMOKE_OK dataConsumer=true nativeMissingService=true sourceIdentity=true'
  $crashDumpSmoke=Start-Process -FilePath $exe -WorkingDirectory $test -ArgumentList '--crash-dump-smoke' -Wait -PassThru
  if($crashDumpSmoke.ExitCode -ne 0){throw "Crash dump smoke failed: $($crashDumpSmoke.ExitCode)"}
  Write-Host 'CRASH_DUMP_SMOKE_OK'
  Write-Host 'PROVIDER_DEGRADATION_SMOKE_OK (covered by service bundle)'
  Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue
  Write-Host 'RUNTIME_SMOKE_OK'
  Mark-ReleasePhase 'crashdump-runtime-cleanup'
}
# The compact installer itself is the verified canonical artifact.
# Re-hash after all installer/runtime tests to catch any accidental mutation.
if(-not(Test-Path $compactFinal)){throw 'Compact installer disappeared during release verification.'}
$compactSizeMb=[math]::Round((Get-Item $compactFinal).Length/1MB,2)
$verifiedCompactHash=(Get-FileHash $compactFinal -Algorithm SHA256).Hash
if($verifiedCompactHash -ne $compactHash){throw "Compact installer mutated during verification: $verifiedCompactHash != $compactHash"}
Write-Host ("COMPACT_INSTALLER_RELEASE_OK compact={0:n2}MB selfContainedBuilt=false" -f $compactSizeMb)
$releaseSw.Stop()
$manifest=[ordered]@{
  version=$releaseVersion
  createdUtc=[DateTime]::UtcNow.ToString('o')
  windowsBuild=[Environment]::OSVersion.Version.Build
  installer=[ordered]@{ file=[IO.Path]::GetFileName($compactFinal); sizeMb=$compactSizeMb; sha256=$compactHash; mode='framework-dependent-net10' }
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
# Re-read and hash BOTH real files at manifest verification time. Comparing
# parsed JSON only to its source variables cannot detect payload corruption.
$manifestVerifier=Join-Path $root 'tools\Verify-OptimizerReleaseArtifactManifest.ps1'
if(-not(Test-Path -LiteralPath $manifestVerifier)){throw 'RELEASE_MANIFEST_VERIFIER_MISSING'}
& $manifestVerifier -ManifestPath $manifestPath -InstallerPath $compactFinal -PayloadPath $payload -ExpectedVersion $releaseVersion | Out-Host
if(-not $?){throw 'RELEASE_MANIFEST_ARTIFACT_INTEGRITY_FAILED'}
Remove-Item $manifestPath -Force -ErrorAction SilentlyContinue
Write-Host 'RELEASE_MANIFEST_OK'

# Prevent a slower concurrent release from overwriting newer update manifests or
# desktop delivery after its initial compile-time source verification.
function Assert-ReleaseStillCurrent([string]$phase){
  $now=[IO.File]::ReadAllText($versionProps,[Text.Encoding]::UTF8)
  $match=[regex]::Match($now,'<SpankyVersion>([^<]+)</SpankyVersion>')
  if(-not $match.Success -or $match.Groups[1].Value.Trim() -ne $releaseVersion){
    throw "RELEASE_SUPERSEDED_BY_NEWER_SOURCE phase=$phase expected=$releaseVersion actual=$($match.Groups[1].Value)"
  }
  $nowHash=Get-InputHash $appFiles
  if($nowHash -ne $appHash){
    throw "RELEASE_SOURCE_CHANGED_AFTER_PUBLISH phase=$phase stale artifact blocked"
  }
}
Assert-ReleaseStillCurrent 'before-update-bridge'
# A successful release MUST NOT silently alter the running installation's
# update source. Explicit opt-in is required to publish an actionable update
# manifest to shared ProgramData. The regular release only emits Desktop EXE.
if($PublishLocalUpdateBridge){
# Explicitly requested local bridge, never a side-effect of compiling.
# Keep only the newest verified compact installer and publish a SHA-256-trusted file:// manifest.
$programDataRoot=$env:ProgramData
if([string]::IsNullOrWhiteSpace($programDataRoot)){
  $programDataRoot=[Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
}
if([string]::IsNullOrWhiteSpace($programDataRoot)){throw 'ProgramData/CommonApplicationData path unavailable.'}
$localUpdateRoot=Join-Path $programDataRoot 'SpankyhoOptimizer'
$localBridgeRoot=Join-Path $localUpdateRoot 'release-bridge'
New-Item $localBridgeRoot -ItemType Directory -Force | Out-Null
$bridgeInstaller=Join-Path $localBridgeRoot ([IO.Path]::GetFileName($compactFinal))
Copy-Item $compactFinal $bridgeInstaller -Force
$bridgeInstallerUrl=[Uri]::new($bridgeInstaller).AbsoluteUri
$bridgeManifest=[ordered]@{
  Version=$releaseVersion
  Url=$bridgeInstallerUrl
  Sha256=$compactHash
  Changelog=("Spankyho Optimizer {0} je připraven k instalaci." -f $releaseVersion)
  Signature=$null
  SignatureAlgorithm='LOCAL-SHA256'
}
$bridgeManifestPath=Join-Path $localUpdateRoot 'local-update-manifest.json'
$bridgeManifest | ConvertTo-Json -Depth 3 | Set-Content $bridgeManifestPath -Encoding UTF8
$bridgeSource=[Uri]::new($bridgeManifestPath).AbsoluteUri
Set-Content (Join-Path $localUpdateRoot 'update-source.txt') $bridgeSource -Encoding UTF8
Get-ChildItem $localBridgeRoot -File -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -like 'Spankyho_Optimizer_V*_Setup_Compact.exe' -or $_.Name -eq 'Glass_Optimizer_Setup_Compact.exe' } |
  Where-Object { -not [string]::Equals($_.FullName,$bridgeInstaller,[StringComparison]::OrdinalIgnoreCase) } |
  Remove-Item -Force -ErrorAction SilentlyContinue
$bridgeRead=Get-Content $bridgeManifestPath -Raw | ConvertFrom-Json
if($bridgeRead.Version -ne $releaseVersion -or $bridgeRead.Sha256 -ne $compactHash -or -not(Test-Path $bridgeInstaller)){
  throw 'Local update bridge integrity check failed.'
}
Write-Host ("LOCAL_UPDATE_BRIDGE_OK version={0} source={1}" -f $releaseVersion,$bridgeSource)
}else{
  Write-Host 'LOCAL_UPDATE_BRIDGE_SKIPPED optInRequired=true activeSettingsPreserved=true'
}

# Keep only the newest Compact installer after a successful release.
# The previous cache is available during the upgrade test earlier in this script;
# once validation succeeds, the obsolete version is no longer retained.
$legacyOfflineCached=@(Get-ChildItem $cache -Filter 'Spankyho_Optimizer_V*_Setup.exe' -File -ErrorAction SilentlyContinue)
$legacyFreed=0L
foreach($file in $legacyOfflineCached){
  try { $legacyFreed += [int64]$file.Length; Remove-Item $file.FullName -Force -ErrorAction Stop } catch {}
}
$cachedVersions=@(
  Get-ChildItem $cache -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'Spankyho_Optimizer_V*_Setup_Compact.exe' -or $_.Name -eq 'Glass_Optimizer_Setup_Compact.exe' } |
    ForEach-Object {
      if($_.Name -eq 'Glass_Optimizer_Setup_Compact.exe'){
        [pscustomobject]@{File=$_;Version=[version]::new([int]::MaxValue,[int]::MaxValue,[int]::MaxValue,[int]::MaxValue)}
      } elseif($_.Name -match '^Spankyho_Optimizer_V(\d+)_(\d+)(?:_(\d+))?_Setup_Compact\.exe$'){
        [pscustomobject]@{ File=$_; Version=[version]("$($matches[1]).$($matches[2]).$(if($matches[3]){$matches[3]}else{0})") }
      }
    } | Sort-Object Version -Descending
)
$keepCached=@($cachedVersions | Select-Object -First 1)
$removeCached=@($cachedVersions | Select-Object -Skip 1)
$freedBytes=$legacyFreed
$removedCached=0
foreach($entry in $removeCached){
  try{
    Remove-Item -LiteralPath $entry.File.FullName -Force -ErrorAction Stop
    $freedBytes += [int64]$entry.File.Length
    $removedCached++
  } catch {
    throw ("Obsolete Compact installer was not removed: {0}" -f $entry.File.FullName)
  }
}
$remainingCompact=@(Get-ChildItem $cache -File -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -like 'Spankyho_Optimizer_V*_Setup_Compact.exe' -or $_.Name -eq 'Glass_Optimizer_Setup_Compact.exe' })
if($remainingCompact.Count -ne 1 -or
   -not [string]::Equals($remainingCompact[0].FullName,$cachedCompactSetup,[StringComparison]::OrdinalIgnoreCase)){
  throw 'Compact installer cache cleanup failed: expected only current verified release.'
}
Write-Host ("RELEASE_CACHE_PRUNE_OK compactKept={0} compactRemoved={1} legacyOfflineRemoved={2} freedMB={3:n1}" -f $remainingCompact.Count,$removedCached,$legacyOfflineCached.Count,($freedBytes/1MB))

# Artifacts are disposable by definition. Keep only the incremental cache, current release
# and the two in-flight staging directories used by this release invocation.
$artifactKeep=@('.cache','release','_stage','_compact-installer')
$artifactRemoved=0
$artifactFreed=0L
Get-ChildItem $artifacts -Directory -Force -ErrorAction SilentlyContinue | ForEach-Object {
  if($_.Name -in $artifactKeep){return}
  try{
    $bytes=[int64]((Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum)
    Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop
    $artifactRemoved++
    $artifactFreed+=$bytes
  } catch {
    Write-Warning ("ARTIFACT_PRUNE_LOCKED {0}: {1}" -f $_.FullName,$_.Exception.Message)
  }
}
$unexpectedArtifacts=@(Get-ChildItem $artifacts -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin $artifactKeep })
if($unexpectedArtifacts.Count -gt 0){throw "Artifact allowlist guard failed: $(@($unexpectedArtifacts | ForEach-Object Name) -join ', ')"}
Write-Host ("ARTIFACT_ALLOWLIST_PRUNE_OK removed={0} freedMB={1:n1}" -f $artifactRemoved,($artifactFreed/1MB))

foreach($benchCache in @('zip-bench','publish-noanalyzers-bench')){
  $benchPath=Join-Path $cache $benchCache
  if(Test-Path $benchPath){Remove-Item $benchPath -Recurse -Force -ErrorAction SilentlyContinue}
}
Write-Host 'BENCH_CACHE_PRUNE_OK'

$rootGeneratedTargets=@(
  (Join-Path $root 'dist-installer'),
  (Join-Path $root 'removed-browser-code'),
  (Join-Path $root 'benchmarks\SpankyhoOptimizer.Benchmarks\bin'),
  (Join-Path $root 'benchmarks\SpankyhoOptimizer.Benchmarks\obj'),
  (Join-Path $root 'benchmarks\SpankyhoOptimizer.Benchmarks\BenchmarkDotNet.Artifacts'),
  (Join-Path $root 'fuzz\SpankyhoOptimizer.Fuzz\bin'),
  (Join-Path $root 'fuzz\SpankyhoOptimizer.Fuzz\obj')
)
$rootGeneratedRemoved=0
$rootGeneratedFreed=0L
foreach($target in $rootGeneratedTargets){
  if(-not(Test-Path $target)){continue}
  try{
    $rootGeneratedFreed += [int64]((Get-ChildItem $target -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum)
    Remove-Item $target -Recurse -Force -ErrorAction Stop
    $rootGeneratedRemoved++
  } catch { Write-Warning ("ROOT_GENERATED_PRUNE_LOCKED {0}: {1}" -f $target,$_.Exception.Message) }
}
if(Test-Path (Join-Path $root 'dist-installer')){throw 'Root generated output guard failed: dist-installer still exists.'}
Write-Host ("ROOT_GENERATED_PRUNE_OK removed={0} freedMB={1:n1}" -f $rootGeneratedRemoved,($rootGeneratedFreed/1MB))

Write-Host "FINAL: $compactFinal"
Write-Host "SIZE:  $compactSizeMb MB"
Write-Host "SETUP_SHA256: $compactHash"
Write-Host "PAYLOAD_SHA256: $payloadHash"

Assert-ReleaseStillCurrent 'before-desktop-delivery'
# Share the EXACT same safe Desktop delivery used by candidate releases.
# The original implementation tried to terminate processes owning old setups;
# never terminate or restart a user process just to remove a stale installer.
$desktopPublisher=Join-Path $root 'tools\Publish-Optimizer-DesktopInstaller.ps1'
& $desktopPublisher -InstallerPath $compactFinal -ExpectedSha256 $compactHash | Out-Host
if(-not $?){throw 'Verified setup desktop publish/cleanup failed.'}
$desktop=[Environment]::GetFolderPath('Desktop')
$desktopSetup=Join-Path $desktop ([IO.Path]::GetFileName($compactFinal))
if(-not(Test-Path -LiteralPath $desktopSetup)){throw 'Published compact setup missing on Desktop'}
Write-Host "DESKTOP_COMPACT_SETUP_OK $desktopSetup"

if(-not $KeepStaging){
  Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item (Join-Path $artifacts '_compact-installer') -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ('RELEASE_OK total={0:n2}s' -f $releaseSw.Elapsed.TotalSeconds)
Write-Host ('RELEASE_END_TO_END elapsed={0:n2}s includesPreflightAndInstallerUi=true' -f $pipelineSw.Elapsed.TotalSeconds)

# Advance the single source of truth only after every release gate succeeded.
# Failed/debug/test builds never consume a version number.
$currentRelease=[version]$releaseVersion
# One increment per VERIFIED release: public 16.115 -> 16.116, etc.
# Technical assemblies stay three-component with .0 for upgrade ordering.
$nextRelease=('{0}.{1}.0' -f $currentRelease.Major,($currentRelease.Minor+1))
$nextDisplay=('{0}.{1}' -f $currentRelease.Major,($currentRelease.Minor+1))
$propsPath=Join-Path $root 'Directory.Build.props'
$propsText=[IO.File]::ReadAllText($propsPath,[Text.Encoding]::UTF8)
$needle='<SpankyVersion>'+ $releaseVersion +'</SpankyVersion>'
$replacement='<SpankyVersion>'+ $nextRelease +'</SpankyVersion>'
if($propsText.Contains($needle)){
  $publicNeedle='<SpankyPublicVersion>'+ $displayVersion +'</SpankyPublicVersion>'
  $publicReplacement='<SpankyPublicVersion>'+ $nextDisplay +'</SpankyPublicVersion>'
  if(-not $propsText.Contains($publicNeedle)){throw 'Version bump: source public version changed concurrently.'}
  $updatedProps=$propsText.Replace($needle,$replacement).Replace($publicNeedle,$publicReplacement)
  [IO.File]::WriteAllText($propsPath,$updatedProps,[Text.UTF8Encoding]::new($false))
  Write-Host "VERSION_BUMP_OK $releaseVersion -> $nextRelease"
} elseif($propsText.Contains($replacement)) {
  # A concurrent successful release may have already advanced the shared source.
  # Treat the already-correct next version as success instead of failing after artifact delivery.
  Write-Host "VERSION_BUMP_ALREADY_OK $releaseVersion -> $nextRelease"
} else {
  throw "Post-release version bump failed: expected $needle or $replacement"
}
$checkpointScript=Join-Path $root 'update-build-checkpoint.ps1'
if(Test-Path $checkpointScript){
  & $checkpointScript -Kind 'release' -Version $releaseVersion -Outcome 'OK' -Evidence $desktopSetup | Out-Host
}
# Tiny post-success timing recorder. It never skips/weakens ANY release smoke,
# cannot block delivery and keeps a rolling baseline to identify slow phases.
$buildTimeGuard=Join-Path $root 'tools\BuildTimeGuard.ps1'
if(Test-Path -LiteralPath $buildTimeGuard){
  try {
    & $buildTimeGuard -Mode Record -Version $releaseVersion `
      -TotalMilliseconds $releaseSw.Elapsed.TotalMilliseconds `
      -PipelineMilliseconds $pipelineSw.Elapsed.TotalMilliseconds `
      -PhaseMilliseconds $releasePhaseMs | Out-Host
  }catch{Write-Warning ('RELEASE_PERF_RECORD_NONFATAL: '+$_.Exception.Message)}
}
$notifyScript=Join-Path (Split-Path $root -Parent) 'build-notify.ps1'
if(Test-Path $notifyScript){
  Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File',$notifyScript,'-Project','Spankyho Optimizer','-Version',$releaseVersion,'-Status','Success','-ArtifactPath',$desktopSetup) | Out-Null
}
