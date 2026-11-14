param(
    [ValidateSet('Plan','Quick','Verify','Release','SelfTest')][string]$Mode='Quick',
    [string]$Task='Targeted Spankyho Optimizer maintenance',
    [string[]]$Files=@()
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'tools\source-set.ps1')
$fast=Join-Path $root 'build-fast.ps1'
$dev=Join-Path $root 'build-dev.ps1'
$release=Join-Path $root 'build-release.ps1'
$router=Join-Path $root 'dev-task.ps1'
$perf=Join-Path $root 'tools\BuildTimeGuard.ps1'

# Read-only safety evidence: another app or agent may update the installed
# program during a long release. Do not mistake our no-shutdown policy for
# evidence the user's actual RuntimeHost survived external changes.
function Get-DevCycleUserRuntimeState {
    $installed='C:\Program Files\Spankyho Optimizer\SpankyhoOptimizer.exe'
    $version='not-found'
    try {
        if(Test-Path -LiteralPath $installed) {
            $version=[string](Get-Item -LiteralPath $installed).VersionInfo.FileVersion
        }
    } catch {$version='unreadable'}
    $identities=@(Get-Process -Name 'SpankyhoOptimizer.RuntimeHost' -ErrorAction SilentlyContinue |
        ForEach-Object {
            $start='unknown'
            try { $start=$_.StartTime.ToUniversalTime().Ticks.ToString() } catch {}
            ('{0}@{1}' -f $_.Id,$start)
        } | Sort-Object)
    $programData=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'SpankyhoOptimizer'
    function Read-ActiveUpdateHash([string]$name){
        $path=Join-Path $programData $name
        try {
            if(Test-Path -LiteralPath $path -PathType Leaf){return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
            return 'not-found'
        }catch{return 'unreadable'}
    }
    return [pscustomobject]@{
        Version=$version
        RuntimeHostIdentity=($identities -join ',')
        UpdateSourceHash=(Read-ActiveUpdateHash 'update-source.txt')
        UpdateManifestHash=(Read-ActiveUpdateHash 'local-update-manifest.json')
    }
}

if($Mode -eq 'Plan'){
    Write-Host 'DEV_CYCLE_PLAN Quick=incremental Debug build, no app shutdown or installer'
    Write-Host 'DEV_CYCLE_PLAN Verify=optional task-aware focused compile/tests; use only when necessary'
    Write-Host 'DEV_CYCLE_PLAN Release=direct strict complete release; NEVER precompile Verify/Quick on the same unchanged edits'
    Write-Host 'DEV_CYCLE_PLAN SelfTest=read-only workflow and performance policy checks'
    return
}
if($Mode -eq 'SelfTest'){
    $xamlPolicy=Join-Path $root 'tools\WinUiSourceGraphPolicy.ps1'
    $installerIsolationPolicy=Join-Path $root 'tools\InstallerIsolationRegression.ps1'
    $paths=@($fast,$dev,$release,$router,$perf,$xamlPolicy,$installerIsolationPolicy,$MyInvocation.MyCommand.Path)
    foreach($path in $paths){
        if(-not(Test-Path -LiteralPath $path)){throw ('DEV_CYCLE_MISSING_SCRIPT '+$path)}
        $tokens=$null; $errors=$null
        [void][Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
        if(@($errors).Count -ne 0){throw ('DEV_CYCLE_PARSE_FAILURE '+$path+' '+$errors[0].Message)}
    }
    $fastText=[IO.File]::ReadAllText($fast)
    $xamlTargetText=[IO.File]::ReadAllText((Join-Path $root 'build-fast-xaml-reuse.targets'))
    $devText=[IO.File]::ReadAllText($dev)
    $releaseText=[IO.File]::ReadAllText($release)
    $ownText=[IO.File]::ReadAllText($MyInvocation.MyCommand.Path)
    $installerText=[IO.File]::ReadAllText((Join-Path $root 'installer\InstallerMain.cs'))
    $isolationText=[IO.File]::ReadAllText($installerIsolationPolicy)
    if(-not $installerText.Contains('HasNoReparseParents(full)') -or
       -not $installerText.Contains('FileAttributes.ReparsePoint') -or
       -not $releaseText.Contains('tools\InstallerIsolationRegression.ps1') -or
       -not $isolationText.Contains('INSTALLER_ISOLATION_JUNCTION_REJECTED_OK') -or
       -not $isolationText.Contains('New-Item -ItemType Junction') -or
       -not $isolationText.Contains('if($exit -ne 32)')){
        throw 'DEV_CYCLE_INSTALLER_REPARSE_CONTRACT_FAILURE'
    }
    if(-not $fastText.Contains('if(-not $AllowDebugShutdown){return}') -or
       -not $fastText.Contains('FAST_CORE_REFERENCE_REBUILD_REQUIRED') -or
       -not $devText.Contains('DEV_CORE_REFERENCE_REBUILD_REQUIRED') -or
       -not $fastText.Contains('Invoke-StagedDebugBuild') -or
       -not $releaseText.Contains('RELEASE_EXCLUSIVE_LEASE_OK') -or
       -not $releaseText.Contains('RELEASE_UI_SMOKE_BUNDLE_OK') -or
       -not $releaseText.Contains('RELEASE_SERVICE_SMOKE_BUNDLE_OK') -or
       -not $releaseText.Contains('RELEASE_FULL_SMOKE_OK') -or
       -not $releaseText.Contains('INSTALLER_ERROR_UI_REGRESSIONS_OK') -or
       -not $releaseText.Contains('RELEASE_OK total=') -or
       -not $releaseText.Contains('BuildTimeGuard.ps1') -or
       -not $releaseText.Contains("Get-FileHash -LiteralPath `$publishedCore -Algorithm SHA256") -or
       -not $releaseText.Contains("`$runtimeHostHash=(`$runtimeHostInputHash+'|'+`$publishedCoreHash)") -or
       -not $releaseText.Contains('RELEASE_HOST_PROJECT_REFS') -or
       -not $releaseText.Contains('RELEASE_HOST_SAFE_RETRY') -or
       -not $releaseText.Contains('RuntimeHost Core reference mismatch or missing DLL') -or
       -not $releaseText.Contains('RELEASE_GUI_PUBLISH_MS') -or
       -not $releaseText.Contains('RELEASE_HOST_PUBLISH_MS') -or
       -not $releaseText.Contains('LOCAL_UPDATE_BRIDGE_SKIPPED optInRequired=true activeSettingsPreserved=true') -or
       -not $releaseText.Contains('if($PublishLocalUpdateBridge){') -or
       -not $ownText.Contains('DEV_CYCLE_ACTIVE_UPDATE_CHANNEL_CHANGED') -or
       -not $releaseText.Contains('RELEASE_SMOKE_TIMING_AUDIT_OK') -or
       -not $releaseText.Contains('Record-ReleaseSmokeDuration') -or
       -not $releaseText.Contains('smokeSumMs') -or
       -not $releaseText.Contains('RELEASE_XAML_SOURCE_GRAPH_OK') -or
       -not $releaseText.Contains('RELEASE_XAML_PRECOMPILE_POLICY') -or
       -not $releaseText.Contains('RELEASE_XAML_SAFE_RETRY') -or
       -not $releaseText.Contains('RELEASE_WINUI_GENERATED_ARTIFACT_GUARD_OK') -or
       -not $releaseText.Contains('RELEASE_XBF_PAYLOAD_GUARD_OK') -or
       -not $releaseText.Contains('Repair-WinUiPublishedXbf') -or
       -not $releaseText.Contains('Test-WinUiGeneratedArtifactIntegrity') -or
       -not $releaseText.Contains('Test-WinUiReleaseReuseSafe') -or
       -not $releaseText.Contains('SpankyFastXamlReuse=true') -or
       -not $releaseText.Contains('RELEASE_XAML_GRAPH_CHECKPOINT_OK') -or
       -not $releaseText.Contains('source-v2:') -or
       -not $releaseText.Contains('winui-release-latest.binlog') -or
       $releaseText.Contains('SpankyBenchmarkSkipMarkup') -or
       $xamlTargetText.Contains('SpankyBenchmarkSkipMarkup') -or
       $xamlTargetText.Contains('<Page Remove=') -or
       $xamlTargetText.Contains('<ApplicationDefinition Remove=') -or
       -not $ownText.Contains('DEV_CYCLE_USER_RUNTIME_CHANGED_DURING_RELEASE') -or
       -not $ownText.Contains('Get-DevCycleUserRuntimeState') -or
       -not $ownText.Contains('& pwsh -NoProfile -File $release -KeepStaging')){
        throw 'DEV_CYCLE_SAFETY_CONTRACT_FAILURE'
    }
    # Theme performance fix must not discard its real 8-second cache-expiry
    # regression check or regress to replacing identical resource brushes.
    $neutralThemeText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\MainWindow.NeutralUi.cs'))
    $themeSmokeText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\MainWindow.Theme.cs'))
    $themePickerText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\MainWindow.Tools.cs'))
    $themeCacheText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\MainWindow.ThemesPageCache.cs'))
    $appUiSmokeText=(Get-SpankyAppSource $root)
    $glassMotionSmokeText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\MainWindow.GlassSettingsSmoke.cs'))
    $motionServiceText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\Services\UiMotionSettingsService.cs'))
    $contextMenuText=(Get-SpankySourceText (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.ContextMenu.cs'))
    $upstreamServiceText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\Services\UpstreamSourceUpdateService.cs'))
    $contextMenuServiceText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\Services\ContextMenuService.cs'))
    $shortcutServiceText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\Services\ShortcutAppearanceService.cs'))
    if(-not $neutralThemeText.Contains('current.Color.Equals(source.Color)') -or
       -not $themeSmokeText.Contains('THEMES_CACHE_LIFETIME_SMOKE_OK') -or
       -not $themeSmokeText.Contains('THEME_VARIANTS_AND_NAVIGATION_OK') -or
       -not $themeSmokeText.Contains('THEME_PREVIEW_RESELECT_NOOP_OK') -or
       -not $themeSmokeText.Contains('ReferenceEquals(AppTitleBar.Children[0], titlebarBeforeReselect)') -or
       -not $themePickerText.Contains('preference == _themePreference && preference != AppThemePreference.System') -or
       $themePickerText.Contains('BuildThemePreviewCard(') -or
       $themePickerText.Contains('Nastavení motivů Windows') -or
       $themePickerText.Contains('Aktuální motiv:') -or
       -not $themePickerText.Contains('"Glass UI", "Animace rozhraní"') -or
       -not $themePickerText.Contains('suppressProgrammaticMotionToggle') -or
       -not $themePickerText.Contains('BuildVerifiedMotionResetButton(') -or
       -not $themePickerText.Contains('if (!motion.IsEnabled) return;') -or
       -not $themePickerText.Contains('if (!read()) write(true);') -or
       -not $glassMotionSmokeText.Contains('new ButtonAutomationPeer(reset)') -or
       -not $glassMotionSmokeText.Contains('writes != 1 || uiUpdates != 2') -or
       -not $glassMotionSmokeText.Contains('errors != 1 || attemptedWrites != 1') -or
       -not $glassMotionSmokeText.Contains('systemSettingsTouched=false') -or
       -not $glassMotionSmokeText.Contains('GLASS_SETTINGS_SINGLE_FLIGHT_SMOKE_OK') -or
       -not $glassMotionSmokeText.Contains('settingsBody.Children.Count != 3') -or
       -not $glassMotionSmokeText.Contains('accessibleNameStable=true') -or
       -not $themePickerText.Contains('_settingsDeferredRunning') -or
       -not $themePickerText.Contains('RunDeferredSettingsBodyOnceAsync(host, generation)') -or
       -not $themePickerText.Contains('AutomationProperties.SetName(button, "Obnovit výchozí animace")') -or
       $glassMotionSmokeText.Contains('UiMotionSettingsService.SetEnabled') -or
       -not $motionServiceText.Contains('Guid.NewGuid().ToString("N") + ".tmp"') -or
       -not $motionServiceText.Contains('PersistAndVerify(PathName, enabled)') -or
       -not $motionServiceText.Contains('RunIsolatedPersistenceSmoke()') -or
       -not $motionServiceText.Contains('File.ReadAllText(targetPath).Trim()') -or
       -not $appUiSmokeText.Contains('UiMotionSettingsService.RunIsolatedPersistenceSmoke()') -or
       -not $appUiSmokeText.Contains('GLASS_MOTION_RESET_UI_SMOKE", releaseUiMw.RunGlassMotionResetUiSmokeAsync') -or
       -not $themePickerText.Contains('"Obnovit výchozí animace"') -or
       -not $themePickerText.Contains('"glass-motion"') -or
       $themePickerText.Contains('RunLowPrioritySettingsStageAsync("theme-update"') -or
       $themePickerText.Contains('RunLowPrioritySettingsStageAsync("maintenance"') -or
       $themePickerText.Contains('ActionCard("Záloha / Obnova"') -or
       -not $themePickerText.Contains('QuickButton("Záloha / Obnova"') -or
       -not $themePickerText.Contains('if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low') -or
       -not $themePickerText.Contains('_settingsDeferredStage >= 3') -or
       -not ((Get-SpankyMainWindowSource $root).Contains('_themePreference = AppThemePreference.Dark;')) -or
       -not $themeSmokeText.Contains('AppThemeEngine.Resolve(AppThemePreference.Dark, true)') -or
       $themeSmokeText.Contains('AppThemeService.SavePreference(preference)') -or
       -not $themeCacheText.Contains('ThemesPageCacheTtl = TimeSpan.FromSeconds(8)') -or
       -not $themeCacheText.Contains('await Task.Delay(ThemesPageCacheTtl);') -or
       $contextMenuText.Contains('UiMotionSettingsService.SetEnabled') -or
       $contextMenuText.Contains('UpstreamSourceUpdateService.SetAutoCheckEnabled') -or
       $contextMenuText.Contains('check.Set(before)') -or
       -not $contextMenuText.Contains('ContextMenuService.RunIsolatedPersistenceSmoke()') -or
       -not $contextMenuText.Contains('actualUserSettingsTouched=false') -or
       -not $contextMenuServiceText.Contains('RunIsolatedPersistenceSmoke()') -or
       -not $contextMenuServiceText.Contains('CONTEXT_MENU_PERSISTENCE_ISOLATED_SMOKE_OK') -or
       -not $contextMenuServiceText.Contains('ContextMenuSmoke') -or
       -not $upstreamServiceText.Contains('RunIsolatedPersistenceSmoke()') -or
       -not $upstreamServiceText.Contains('UPSTREAM_AUTOCHECK_PERSISTENCE_ISOLATED_SMOKE_OK') -or
       -not $appUiSmokeText.Contains('completedUiGates++') -or
       $appUiSmokeText.Contains('RELEASE_UI_SMOKE_BUNDLE_OK count=23')){
        throw 'DEV_CYCLE_THEME_RESOURCE_OR_LIFETIME_GUARD_FAILURE'
    }
    Write-Host 'DEV_CYCLE_THEME_RESOURCE_GUARD_OK sameColorReuse=true ttl8sPreserved=true gateCountDynamic=true'
    if($appUiSmokeText.Contains('EnsureTransparentIconForSmoke') -or
       $appUiSmokeText.Contains('TransparentIconPathForSmoke') -or
       $appUiSmokeText.Contains('MigrateLegacyShortcutOverlayAsync(cts.Token)') -or
       $appUiSmokeText.Contains('PrepareManagedOverlayForExplorerRestart()') -or
       $shortcutServiceText.Contains('EnsureTransparentIconForSmoke') -or
       -not $appUiSmokeText.Contains('ShortcutAppearanceService.RunIsolatedPersistenceSmokeAsync()') -or
       -not $appUiSmokeText.Contains('ShortcutAppearanceService.ReadRealOverlayFingerprintForSmoke()') -or
       -not $appUiSmokeText.Contains('realStateUnchanged=true realIconWrites=0') -or
       -not $appUiSmokeText.Contains('ShortcutAppearanceService.MigrateLegacyShortcutOverlayAsync(token)') -or
       -not $shortcutServiceText.Contains('RunIsolatedPersistenceSmokeAsync()') -or
       -not $shortcutServiceText.Contains('SHORTCUT_OVERLAY_PERSISTENCE_ISOLATED_SMOKE_OK') -or
       -not $shortcutServiceText.Contains('ShortcutOverlaySmoke-') -or
       -not $shortcutServiceText.Contains('applyShellChanges: false') -or
       -not $shortcutServiceText.Contains('actualUserSettingsTouched=false') -or
       -not $shortcutServiceText.Contains('ReadRealOverlayFingerprintForSmoke') -or
       -not $shortcutServiceText.Contains('if (applyShellChanges) await RefreshShellAsync(token);') -or
       -not $shortcutServiceText.Contains('if (applyShellChanges)') -or
       -not $shortcutServiceText.Contains('EnsureTransparentIconAt(iconPath)')){
        throw 'DEV_CYCLE_SHORTCUT_OVERLAY_ISOLATION_GUARD_FAILURE'
    }
    Write-Host 'DEV_CYCLE_SHORTCUT_OVERLAY_ISOLATION_GUARD_OK realRegistryReadOnly=true shellRefreshSkipped=true'
    $processRunnerText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\Services\ProcessRunner.cs'))
    $windowsActionsText=(Get-SpankySourceText (Join-Path $root 'src\SpankyhoOptimizer\Services\WindowsActions.cs'))
    $hardwareCacheText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\Services\PersistentHardwareCacheService.cs'))
    $aiSelfText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\Services\AiSelfOptimizationService.cs'))
    $aiSystemText=(Get-SpankyAiSystemSource $root)
    $extraSectionsText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\MainWindow.ExtraSections.cs'))
    $defenderPageText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\MainWindow.Defender.cs'))
    $advancedCentersText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\MainWindow.AdvancedCenters.cs'))
    $mainWindowText=(Get-SpankyMainWindowSource $root)
    $spicetifyText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\MainWindow.Spicetify.cs'))
    $fixerCacheText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\MainWindow.FixerPageCache.cs'))
    $powerNetworkText=(Get-SpankyPowerNetworkSource $root)
    $toolsText=[IO.File]::ReadAllText((Join-Path $root 'src\SpankyhoOptimizer\MainWindow.Tools.cs'))
    $contextMenuText=(Get-SpankySourceText (Join-Path $root 'src\SpankyhoOptimizer\MainWindow.ContextMenu.cs'))
    if($processRunnerText.Contains('Environment.SetEnvironmentVariable("PATH"') -or
       -not $processRunnerText.Contains('Child process PATH was not hardened to trusted Windows directories.') -or
       -not $processRunnerText.Contains('HardenEnvironment(probe)') -or
       -not $processRunnerText.Contains('SPANKY_PROCESS_HARDENING_OK')){
        throw 'DEV_CYCLE_PROCESS_RUNNER_ENV_GUARD_FAILURE'
    }
    Write-Host 'DEV_CYCLE_PROCESS_RUNNER_ENV_GUARD_OK globalPathMutation=false childPathHardened=true'
    if($windowsActionsText.Contains('PersistentHardwareCacheService.Remove(MemoryCompressionPersistentKey)') -or
       -not $windowsActionsText.Contains('PersistentHardwareCacheService.RunIsolatedPersistenceSmoke()') -or
       -not $windowsActionsText.Contains('actualUserCacheTouched=false') -or
       -not $hardwareCacheText.Contains('RunIsolatedPersistenceSmoke()') -or
       -not $hardwareCacheText.Contains('SpankyHardwareCacheSmoke-') -or
       -not $hardwareCacheText.Contains('actualUserCacheTouched=false')){
        throw 'DEV_CYCLE_MEMORY_COMPRESSION_CACHE_ISOLATION_GUARD_FAILURE'
    }
    Write-Host 'DEV_CYCLE_MEMORY_COMPRESSION_CACHE_ISOLATION_GUARD_OK realCacheReadOnly=true isolatedTempRoot=true'
    if($aiSelfText.Contains('string temp = HealthPath + ".tmp"') -or
       $aiSystemText.Contains('HealthPath + ".tmp"') -or
       $aiSystemText.Contains('ActionHistoryPath + ".tmp"') -or
       -not $aiSelfText.Contains('AtomicFileService.WriteAllBytesAtomic') -or
       -not $aiSystemText.Contains('AtomicFileService.WriteAllBytesAtomic') -or
       -not $hardwareCacheText.Contains('NewTempPath') -or
       -not $hardwareCacheText.Contains('RemoveStaleTempSiblings')){
        throw 'DEV_CYCLE_CROSS_PROCESS_TEMP_GUARD_FAILURE'
    }
    Write-Host 'DEV_CYCLE_CROSS_PROCESS_TEMP_GUARD_OK uniqueSidecars=true staleReclaim=true aiHealthAndHistory=true'
    if(-not $extraSectionsText.Contains('if (!queued) _appsDeferredQueued = false;') -or
       -not $extraSectionsText.Contains('})) _appsShellQueued = false;') -or
       -not $defenderPageText.Contains('if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>') -or
       -not $defenderPageText.Contains('})) _defenderModulesQueued = false;') -or
       -not $defenderPageText.Contains('})) _defenderActionsQueued = false;') -or
       -not $advancedCentersText.Contains('})) _updateActionsQueued = false;') -or
       -not $mainWindowText.Contains('})) _gpuToolsQueued = false;') -or
       -not $spicetifyText.Contains('})) _spicetifyActionsQueued = false;') -or
       -not $spicetifyText.Contains('})) _spicetifySecondaryQueued = false;') -or
       -not $fixerCacheText.Contains('})) _fixerStaticActionsQueued = false;') -or
       -not $powerNetworkText.Contains('})) _networkAdvancedLoadQueued = false;') -or
       -not $toolsText.Contains('})) _restoreSurfaceBatchQueued = false;') -or
       -not $contextMenuText.Contains('Action? rejected = null') -or
       -not $contextMenuText.Contains('if (!queued) rejected?.Invoke();') -or
       -not $contextMenuText.Contains('rejected: () => advancedQueued = false') -or
       -not $contextMenuText.Contains('rejected: () => maintenanceQueued = false')){
        throw 'DEV_CYCLE_DEFERRED_ENQUEUE_FAILURE_GUARD_FAILURE'
    }
    Write-Host 'DEV_CYCLE_DEFERRED_ENQUEUE_FAILURE_GUARD_OK appsBody=true appsShell=true defenderModules=true defenderActions=true updateActions=true gpuTools=true spicetify=true fixerStatic=true networkAdvanced=true restoreSurface=true contextMenuGroups=true'
    & $perf -Mode SelfTest
    if(-not $?){throw 'DEV_CYCLE_PERF_SMOKE_FAILURE'}
    & $xamlPolicy -SelfTest
    if(-not $?){throw 'DEV_CYCLE_XAML_GRAPH_SMOKE_FAILURE'}
    Write-Host 'DEV_CYCLE_SELFTEST_OK noProcessShutdown=true completeReleaseGates=true noPrecompile=true sourceOnlyXaml=true'
    return
}

if($Mode -eq 'Verify' -and $Files.Count -eq 0){
    throw 'DEV_CYCLE_VERIFY_FILES_REQUIRED: scope targeted verification to the modified source files'
}
$sw=[Diagnostics.Stopwatch]::StartNew()
$result='FAIL'
$lock=$null
$leasePath=Join-Path $root '.ai-state\release-exclusive.lock'
try {
    if($Mode -ne 'Release'){
        # Quick/Verify never race the strict release over WinUI/XAML intermediates.
        # Do not kill other compiler processes or forcibly steal an active lease.
        try {
            $lock=[IO.File]::Open($leasePath,[IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        } catch [System.IO.IOException] {
            throw 'DEV_CYCLE_BUSY: another release/build owns shared intermediates'
        }
    }
    Write-Host ('DEV_CYCLE_START mode={0} noUserProcessRestart=true strictGates={1}' -f
        $Mode,($Mode -eq 'Release'))
    if($Mode -eq 'Verify'){
        # Pass real PowerShell arrays in-process; pwsh -File cannot marshal
        # a multi-value -Files parameter across the command-line boundary.
        & $router -Task $Task -Mode Run -Depth Normal -Files $Files
    }elseif($Mode -eq 'Quick'){
        & pwsh -NoProfile -File $fast
    }else{
        # Capture a read-only snapshot to spot changes by other processes.
        # No service/process control, installation or registry mutation.
        $beforeUserRuntime=Get-DevCycleUserRuntimeState
        Write-Host ('DEV_CYCLE_USER_RUNTIME_BEFORE installed={0} hosts={1}' -f
            $beforeUserRuntime.Version,$beforeUserRuntime.RuntimeHostIdentity)
        # Full release includes a fresh compile and every strict smoke gate.
        # A preceding dev-task compile is intentionally NOT part of this path.
        & pwsh -NoProfile -File $release -KeepStaging
        $afterUserRuntime=Get-DevCycleUserRuntimeState
        if($beforeUserRuntime.Version -ne $afterUserRuntime.Version -or
           $beforeUserRuntime.RuntimeHostIdentity -ne $afterUserRuntime.RuntimeHostIdentity){
            Write-Warning ('DEV_CYCLE_USER_RUNTIME_CHANGED_DURING_RELEASE installed={0}->{1} hosts={2}->{3} cause=undetermined' -f
                $beforeUserRuntime.Version,$afterUserRuntime.Version,
                $beforeUserRuntime.RuntimeHostIdentity,$afterUserRuntime.RuntimeHostIdentity)
        }else{
            Write-Host 'DEV_CYCLE_USER_RUNTIME_UNCHANGED_OBSERVED readOnly=true'
        }
        if($beforeUserRuntime.UpdateSourceHash -ne $afterUserRuntime.UpdateSourceHash -or
           $beforeUserRuntime.UpdateManifestHash -ne $afterUserRuntime.UpdateManifestHash){
            Write-Warning ('DEV_CYCLE_ACTIVE_UPDATE_CHANNEL_CHANGED sourceHash={0}->{1} manifestHash={2}->{3} cause=undetermined' -f
                $beforeUserRuntime.UpdateSourceHash,$afterUserRuntime.UpdateSourceHash,
                $beforeUserRuntime.UpdateManifestHash,$afterUserRuntime.UpdateManifestHash)
        }else{
            Write-Host 'DEV_CYCLE_ACTIVE_UPDATE_CHANNEL_UNCHANGED_OBSERVED readOnly=true'
        }
    }
    $exitCode=$LASTEXITCODE
    if($exitCode -ne 0 -or -not $?){
        throw ('DEV_CYCLE_CHILD_FAILED mode='+$Mode+' exit='+$exitCode)
    }
    $result='PASS'
    Write-Host ('DEV_CYCLE_PASS mode={0} elapsedMs={1:n0}' -f $Mode,$sw.Elapsed.TotalMilliseconds)
}finally{
    if($null -ne $lock){$lock.Dispose()}
    $sw.Stop()
    # Lightweight durable comparison of wall time by mode. This logs only
    # developer build timing, never user process/CPU/RAM/network telemetry.
    try{
        $path=Join-Path $root '.ai-state\dev-cycle-history.jsonl'
        $row=[ordered]@{utc=[DateTime]::UtcNow.ToString('o');mode=$Mode;
            result=$result;elapsedMs=[math]::Round($sw.Elapsed.TotalMilliseconds)}
        Add-Content -LiteralPath $path -Value ($row | ConvertTo-Json -Compress) -Encoding UTF8
        $fi=Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
        if($fi -and $fi.Length -gt 16000){
            $last=@(Get-Content -LiteralPath $path | Select-Object -Last 60)
            [IO.File]::WriteAllLines($path,[string[]]$last,[Text.UTF8Encoding]::new($false))
        }
    }catch{
        Write-Warning ('DEV_CYCLE_HISTORY_NONFATAL '+$_.Exception.Message)
    }
}
