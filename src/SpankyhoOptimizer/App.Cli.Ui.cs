using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchUiAsync(string[] cli, long windowActivatedMs)
    {
        if (HasArg(cli, "--powershell-center-smoke") && _window is MainWindow powerShellCenterMw)
        {
            try
            {
                await powerShellCenterMw.RunPowerShellCenterSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("POWERSHELL_CENTER_SMOKE_FAIL", ex);
                Environment.Exit(74);
            }
            return true;
        }

        if (HasArg(cli, "--live-metrics-lifecycle-smoke") && _window is MainWindow liveMetricsMw)
        {
            try { await liveMetricsMw.RunLiveMetricsLifecycleSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("LIVE_METRICS_LIFECYCLE_SMOKE_FAIL", ex); Environment.Exit(73); }
            return true;
        }
        if (HasArg(cli, "--overview-status-smoke") && _window is MainWindow overviewStatusMw)
        {
            try { await Task.Delay(500); await overviewStatusMw.RunOverviewStatusSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("OVERVIEW_STATUS_SMOKE_FAIL", ex); Environment.Exit(75); }
            return true;
        }
        if (HasArg(cli, "--activity-ui-smoke") && _window is MainWindow activityUiMw)
        {
            try
            {
                await Task.Delay(500);
                await activityUiMw.RunActivityUiPopulationSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("activity", "ACTIVITY_UI_POPULATION_SMOKE_FAIL", ex);
                Environment.Exit(105);
            }
            return true;
        }
        if (HasArg(cli, "--navigation-cache-smoke") && _window is MainWindow navigationMw)
        {
            try
            {
                await Task.Delay(500);
                for (int i = 0; i < 12; i++) await navigationMw.RunOverviewCacheSmokeAsync();
                LogService.Info("NAVIGATION_CACHE_REPEAT_SMOKE_OK samples=12");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("NAVIGATION_CACHE_REPEAT_SMOKE_FAIL", ex); Environment.Exit(73); }
            return true;
        }
        if (HasArg(cli, "--system-pinned-host-smoke") && _window is MainWindow pinnedSystemMw)
        {
            try
            {
                await Task.Delay(500);
                await pinnedSystemMw.RunSystemDeferredRenderSmokeAsync();
                LogService.Info("performance", "SYSTEM_PINNED_HOST_SMOKE_OK");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("performance", "SYSTEM_PINNED_HOST_SMOKE_FAIL", ex);
                Environment.Exit(76);
            }
            return true;
        }
        if (HasArg(cli, "--storage-advisor-lazy-smoke") && _window is MainWindow lazyStorageMw)
        {
            try
            {
                await lazyStorageMw.RunStorageAdvisorLazySmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("storage", "STORAGE_ADVISOR_LAZY_SMOKE_FAIL", ex);
                Environment.Exit(93);
            }
            return true;
        }
        if (HasArg(cli, "--context-menu-lazy-smoke") && _window is MainWindow lazyContextMenuMw)
        {
            try
            {
                await lazyContextMenuMw.RunContextMenuLazyRenderSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("ui", "CONTEXT_MENU_LAZY_RENDER_SMOKE_FAIL", ex);
                Environment.Exit(92);
            }
            return true;
        }
        if (HasArg(cli, "--release-ui-smoke") && _window is MainWindow releaseUiMw)
        {
            try
            {
                int completedUiGates = 0;
                async Task RunGateAsync(string gate, Func<Task> action)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await action();
                    completedUiGates++;
                    LogService.Info("release-ui-smoke", $"{gate}_OK ms={sw.ElapsedMilliseconds}");
                }

                // Allow the startup XAML work to settle before measuring cache navigation.
                await Task.Delay(500);
                await RunGateAsync("DRIVERS_UI_SMOKE", releaseUiMw.RunDriversUiSmokeAsync);
                await RunGateAsync("TASK_SCHEDULER_UI_SMOKE", releaseUiMw.RunTaskSchedulerUiSmokeAsync);
                await RunGateAsync("STARTUP_RENDER_SMOKE", releaseUiMw.RunStartupRenderSmokeAsync);
                await RunGateAsync("ACTIVITY_SNAPSHOT_SMOKE", async () =>
                {
                    LogService.Info("performance", ActivityProcessSnapshotService.RunRankingBenchmark());
                    await ActivityProcessSnapshotService.RunCacheSmokeAsync();
                });
                await RunGateAsync("ACTIVITY_UI_POPULATION_SMOKE", releaseUiMw.RunActivityUiPopulationSmokeAsync);
                await RunGateAsync("POWER_RENDER_SMOKE", releaseUiMw.RunPowerRenderSmokeAsync);
                await RunGateAsync("NAV_TAIL_SMOKE", releaseUiMw.RunNavigationTailLatencySmokeAsync);
                await RunGateAsync("NAV_HOST_REUSE_SMOKE", releaseUiMw.RunNavigationHostReuseSmokeAsync);
                await RunGateAsync("FIXER_HERO_LIFECYCLE_SMOKE", releaseUiMw.RunFixerDeferredHeroSmokeAsync);
                await RunGateAsync("NAV_DETACHED_DEADLINE_SMOKE", releaseUiMw.RunDetachedHostCleanupUnderNavigationSmokeAsync);
                await RunGateAsync("AI_PROGRESS_UI_SMOKE", () => releaseUiMw.RunAiProgressUiSmokeAsync());
                await RunGateAsync("AI_RUNTIME_PAGE_CACHE_SMOKE", releaseUiMw.RunAiRuntimePageCacheSmokeAsync);
                await RunGateAsync("NIC_UI_SMOKE", releaseUiMw.RunNicUiSmokeAsync);
                await RunGateAsync("GPU_DEFERRED_RENDER_SMOKE", releaseUiMw.RunGpuDeferredRenderSmokeAsync);
                await RunGateAsync("USB_DEFERRED_RENDER_SMOKE", releaseUiMw.RunUsbDeferredRenderSmokeAsync);
                await RunGateAsync("DEFENDER_DEFERRED_ACTIONS_SMOKE", releaseUiMw.RunDefenderDeferredActionsSmokeAsync);
                await RunGateAsync("APPS_DEFERRED_BODY_SMOKE", releaseUiMw.RunAppsDeferredBodySmokeAsync);
                await RunGateAsync("COMMUNITY_CURSOR_POPUP_SMOKE", () =>
                {
                    LogService.Info("release-ui-smoke", releaseUiMw.RunCommunityCursorPopupLifecycleSmoke());
                    return Task.CompletedTask;
                });
                await RunGateAsync("COMMUNITY_CURSOR_LAYOUT_SMOKE", releaseUiMw.RunCommunityCursorLayoutSmokeAsync);
                await RunGateAsync("CURSOR_WATCHDOG_SMOKE", () =>
                {
                    LogService.Info("release-ui-smoke", CursorThemeService.RunWatchdogNonMutatingSmoke());
                    return Task.CompletedTask;
                });
                await RunGateAsync("STORAGE_TOOLS_CACHE_SMOKE", releaseUiMw.RunStorageToolsCacheSmokeAsync);
                await RunGateAsync("OVERVIEW_CACHE_SMOKE", releaseUiMw.RunOverviewCacheSmokeAsync);
                await RunGateAsync("OVERVIEW_STATUS_SMOKE", releaseUiMw.RunOverviewStatusSmokeAsync);
                await RunGateAsync("OVERVIEW_GAUGE_HOTPATH_SMOKE", () => releaseUiMw.RunOverviewGaugeHotpathSmokeAsync());
                await RunGateAsync("LIVE_METRICS_LIFECYCLE_SMOKE", releaseUiMw.RunLiveMetricsLifecycleSmokeAsync);
                await RunGateAsync("OVERVIEW_ACTIVE_TWEAK_MERGE_SMOKE", () =>
                {
                    LogService.Info("release-ui-smoke", MainWindow.RunOverviewActiveTweakMergeSmoke());
                    return Task.CompletedTask;
                });
                await RunGateAsync("GPU_DEFERRED_TWEAK_SMOKE", releaseUiMw.RunGpuDeferredTweakSmokeAsync);
                await RunGateAsync("GPU_STALE_CACHE_REUSE_SMOKE", releaseUiMw.RunGpuStaleCacheNavigationSmokeAsync);
                await RunGateAsync("STORAGE_STALE_CACHE_REUSE_SMOKE", releaseUiMw.RunStorageStaleCacheNavigationSmokeAsync);
                await RunGateAsync("PROGRESS_PREDICATE_SMOKE", releaseUiMw.RunProgressPredicateSmokeAsync);
                await RunGateAsync("POWERSHELL_CENTER_SMOKE", releaseUiMw.RunPowerShellCenterSmokeAsync);
                // Real WinUI Invoke on the production reset button factory, but
                // with an isolated in-memory setting; never touch user motion.txt.
                await RunGateAsync("GLASS_MOTION_RESET_UI_SMOKE", releaseUiMw.RunGlassMotionResetUiSmokeAsync);
                // Theme render runs last because it intentionally rebuilds shell/page caches.
                await RunGateAsync("THEME_RENDER_SMOKE", releaseUiMw.RunThemeRenderSmokeAsync);
                LogService.Info("release-ui-smoke", $"RELEASE_UI_SMOKE_BUNDLE_OK count={completedUiGates}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("release-ui-smoke", "RELEASE_UI_SMOKE_BUNDLE_FAIL", ex);
                Environment.Exit(73);
            }
            return true;
        }

        if (HasArg(cli, "--progress-lifecycle-smoke") && _window is MainWindow progressLifecycleMw)
        {
            try
            {
                await progressLifecycleMw.RunProgressPredicateSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "PROGRESS_LIFECYCLE_SMOKE_FAIL", ex);
                Environment.Exit(74);
            }
            return true;
        }

        if (HasArg(cli, "--system-cold-render-smoke") && _window is MainWindow systemColdMw)
        {
            try { await systemColdMw.RunSystemColdRenderSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("SYSTEM_COLD_RENDER_SMOKE_FAIL", ex); Environment.Exit(57); }
            return true;
        }

        if (HasArg(cli, "--virtual-resource-prewarm-smoke") && _window is MainWindow virtualPrewarmMw)
        {
            try { await virtualPrewarmMw.RunVirtualResourcePrewarmSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("VIRTUAL_RESOURCE_PREWARM_SMOKE_FAIL", ex); Environment.Exit(55); }
            return true;
        }

        if (HasArg(cli, "--resource-cache-smoke-bundle") && _window is MainWindow resourceCacheMw)
        {
            try
            {
                var bundleSw = System.Diagnostics.Stopwatch.StartNew();
                await resourceCacheMw.RunVirtualResourcePrewarmSmokeAsync();
                LogService.Info("resource-cache-smoke", $"VIRTUAL_RESOURCE_PREWARM_SMOKE_OK ms={bundleSw.ElapsedMilliseconds}");
                bundleSw.Restart();
                await RunDebloatCacheSmokeCoreAsync();
                LogService.Info("resource-cache-smoke", $"DEBLOAT_CACHE_SMOKE_OK ms={bundleSw.ElapsedMilliseconds}");
                LogService.Info("resource-cache-smoke", "RESOURCE_CACHE_SMOKE_BUNDLE_OK count=2");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("resource-cache-smoke", "RESOURCE_CACHE_SMOKE_BUNDLE_FAIL", ex);
                Environment.Exit(76);
            }
            return true;
        }

        if (HasArg(cli, "--theme-render-smoke") && _window is MainWindow themeMw)
        {
            try { await themeMw.RunThemeRenderSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("THEME_RENDER_SMOKE_FAIL", ex); Environment.Exit(54); }
            return true;
        }

        if (HasArg(cli, "--visual-layout-smoke") && _window is MainWindow visualMw)
        {
            try { await visualMw.RunVisualLayoutSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("VISUAL_LAYOUT_SMOKE_FAIL", ex); Environment.Exit(77); }
            return true;
        }

        if (HasArg(cli, "--community-cursor-layout-smoke") && _window is MainWindow cursorLayoutMw)
        {
            try { await cursorLayoutMw.RunCommunityCursorLayoutSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("COMMUNITY_CURSOR_LAYOUT_SMOKE_FAIL", ex); Environment.Exit(78); }
            return true;
        }
        if (HasArg(cli, "--visual-capture-smoke") && _window is MainWindow captureMw)
        {
            try { await captureMw.RunVisualCaptureSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("VISUAL_CAPTURE_SMOKE_FAIL", ex); Environment.Exit(78); }
            return true;
        }

        if (HasArg(cli, "--visual-size-smoke") && _window is MainWindow sizeMw)
        {
            try { await sizeMw.RunVisualSizeMatrixSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("VISUAL_SIZE_SMOKE_FAIL", ex); Environment.Exit(79); }
            return true;
        }

        if (HasArg(cli, "--page-scroll-input-smoke") && _window is MainWindow pageScrollMw)
        {
            try { await pageScrollMw.RunPageScrollInputSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("PAGE_SCROLL_INPUT_SMOKE_FAIL", ex); Environment.Exit(84); }
            return true;
        }

        if (HasArg(cli, "--keyboard-accessibility-smoke") && _window is MainWindow keyboardMw)
        {
            try { await keyboardMw.RunKeyboardAccessibilitySmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("KEYBOARD_ACCESSIBILITY_SMOKE_FAIL", ex); Environment.Exit(80); }
            return true;
        }

        if (HasArg(cli, "--keyboard-activation-smoke") && _window is MainWindow activationMw)
        {
            try
            {
                activationMw.Activate();
                await Task.Delay(100);
                await activationMw.RunKeyboardActivationSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("KEYBOARD_ACTIVATION_SMOKE_FAIL", ex); Environment.Exit(81); }
            return true;
        }

        if (HasArg(cli, "--section-tab-smoke") && _window is MainWindow sectionTabMw)
        {
            try { await sectionTabMw.RunSectionTabTraversalSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("SECTION_TAB_SMOKE_FAIL", ex); Environment.Exit(82); }
            return true;
        }

        if (HasArg(cli, "--navigation-race-smoke") && _window is MainWindow navigationRaceMw)
        {
            try { await navigationRaceMw.RunNavigationRaceSmokeAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("NAVIGATION_RACE_SMOKE_FAIL", ex); Environment.Exit(83); }
            return true;
        }

        if (HasArg(cli, "--storage-stale-cache-smoke") && _window is MainWindow storageStaleMw)
        {
            try
            {
                await Task.Delay(250);
                await storageStaleMw.RunStorageStaleCacheNavigationSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("STORAGE_STALE_CACHE_SMOKE_FAIL", ex); Environment.Exit(99); }
            return true;
        }

        if (HasArg(cli, "--gpu-stale-cache-smoke") && _window is MainWindow gpuStaleMw)
        {
            try
            {
                await Task.Delay(250);
                await gpuStaleMw.RunGpuStaleCacheNavigationSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("GPU_STALE_CACHE_SMOKE_FAIL", ex); Environment.Exit(98); }
            return true;
        }

        if (HasArg(cli, "--debloat-dialog-filter-smoke") && _window is MainWindow debloatDialogMw)
        {
            try
            {
                await debloatDialogMw.RunDebloatDialogFilterSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("DEBLOAT_DIALOG_FILTER_SMOKE_FAIL", ex);
                Environment.Exit(95);
            }
            return true;
        }

        if (HasArg(cli, "--gpu-deferred-render-smoke") && _window is MainWindow gpuDeferredMw)
        {
            try
            {
                await Task.Delay(250);
                await gpuDeferredMw.RunGpuDeferredRenderSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("GPU_DEFERRED_RENDER_SMOKE_FAIL", ex); Environment.Exit(56); }
            return true;
        }

        if (HasArg(cli, "--process-hardening-smoke"))
        {
            try
            {
                await ProcessRunner.RunHardeningSmokeAsync();
                LogService.Info("PROCESS_HARDENING_SMOKE_OK");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("PROCESS_HARDENING_SMOKE_FAIL", ex);
                Environment.Exit(68);
            }
            return true;
        }

        if (HasArg(cli, "--full-smoke") && _window is MainWindow fullMw)
        {
            try
            {
                int navigationFailuresAtStart = fullMw.NavigationFailureCount;
                if (navigationFailuresAtStart != 0)
                    throw new InvalidOperationException($"Full smoke started with {navigationFailuresAtStart} navigation failure(s).");
                if (windowActivatedMs > 1500) throw new InvalidOperationException($"Startup audit failed: Window.Activated at {windowActivatedMs} ms.");
                LogService.Info("regression", fullMw.RunChangelogPagingPolicySmoke());
                LogService.Info("regression", TweakBatchHistoryService.RunPartialRestoreHistorySmoke());
                LogService.Info("regression", await LogTailService.RunTailBoundarySmokeAsync());
                LogService.Info("regression", WallpaperThemeService.RunWallpaperRollbackSmoke());
                LogService.Info("regression", UserFeatureProfileService.RunFeatureProfileFailClosedSmoke());
                LogService.Info("regression", FanProfileService.RunFanHeartbeatInputSmoke());
                LogService.Info("regression", SafePresetProfileService.RunFailClosedPresetSmoke());
                LogService.Info("regression", AppUninstallService.RunCleanupIdentitySmoke());
                LogService.Info("regression", UpstreamSourceUpdateService.RunInterruptedSnapshotRecoverySmoke());
                LogService.Info("regression", await AiCloudProfileService.RunCloudAuthEpochSmokeAsync());
                LogService.Info("regression", TweakDriftWatchdogService.RunDriftIncompleteSmoke());
                LogService.Info("regression", GoogleDriveOAuthService.RunSignOutPersistenceSafetySmoke());
                LogService.Info("regression", UserFeatureProfileService.RunMigrationSafetySmoke());
                LogService.Info("regression", SelfHealingJsonService.RunQuarantinePreservationSmoke());
                LogService.Info("regression", await NativeSystemProbeService.RunStorageTemperatureGenerationSmokeAsync());
                LogService.Info("regression", CursorCommunityPackService.RunCommunityCursorAtomicSmoke());
                LogService.Info("regression", await ProcessRunner.RunProcessQueueDeadlineSmokeAsync());
                LogService.Info("regression", WingetUpdateService.RunPackageIdSafetySmoke());
                LogService.Info("regression", await OperationCoordinator.RunQueueDeadlineSmokeAsync());
                LogService.Info("regression", GpuDriverService.RunResumeIntegritySmoke());
                LogService.Info("regression", ArchiveSafetyService.RunArchiveEntrySmoke());
                LogService.Info("regression", ArchiveSafetyService.RunExtractedTreeSmoke());
                LogService.Info("regression", FileSecurityService.RunTrustedSignerIdentitySmoke());
                LogService.Info("regression", SpankyExtensionService.RunManifestValidationSmoke());
                LogService.Info("regression", await AtomicFileService.RunAtomicReplacementSmokeAsync());
                LogService.Info("regression", LauncherProcessMatcher.RunStartupMatchingSmoke());
                LogService.Info("regression", HardwareTelemetryBridgeService.RunBridgeRecoveryTaskSmoke());
                LogService.Info("regression", HardwareTelemetryBridgeService.RunBridgeSnapshotAtomicRetrySmoke());
                LogService.Info("regression", GpuNvapiUsageService.RunInteropPolicySmoke());
                LogService.Info("regression", DiskPdhUsageService.RunRetryPolicySmoke());
                LogService.Info("regression", BufferbloatDiagnosticService.RunPolicySmoke());
                LogService.Info("regression", SelectedFixerChecksService.RunPolicySmoke());
                LogService.Info("regression", FirmwareMemoryReadinessService.RunPolicySmoke());
                LogService.Info("regression", GameReadyCompatibilityService.RunPolicySmoke());
                LogService.Info("regression", WindowsDefaultGuardService.RunPolicySmoke());
                LogService.Info("regression", WindowsDefaultGuardService.RunJournalContentionSmoke());
                LogService.Info("regression", BluetoothAdapterHealthService.RunPolicySmoke());
                LogService.Info($"STARTUP_AUDIT_OK activatedMs={windowActivatedMs}");
                await Task.Delay(350);
                LogService.Info("FULL_SMOKE_STARTUP_OK");
                await WatchdogService.RunAsync("watchdog-complete-smoke", async ct => await Task.Delay(5, ct), TimeSpan.FromSeconds(1));
                bool watchdogTimedOut = false;
                try
                {
                    await WatchdogService.RunAsync("watchdog-timeout-smoke", async ct => await Task.Delay(200, ct), TimeSpan.FromMilliseconds(20), logTimeoutAsError: false);
                }
                catch (TimeoutException) { watchdogTimedOut = true; }
                if (!watchdogTimedOut) throw new InvalidOperationException("Watchdog timeout smoke did not time out.");
                using (var watchdogCancel = new System.Threading.CancellationTokenSource())
                {
                    watchdogCancel.CancelAfter(20);
                    bool externalCancelled = false;
                    try
                    {
                        await WatchdogService.RunAsync("watchdog-cancel-smoke", async ct => await Task.Delay(200, ct), TimeSpan.FromSeconds(1), watchdogCancel.Token);
                    }
                    catch (OperationCanceledException) { externalCancelled = true; }
                    if (!externalCancelled) throw new InvalidOperationException("Watchdog external cancellation smoke did not cancel.");
                }
                LogService.Info("WATCHDOG_SMOKE_OK");
                var fullPhase = System.Diagnostics.Stopwatch.StartNew();
                long fullPhaseLast = 0;
                void FullPhase(string name)
                {
                    long now = fullPhase.ElapsedMilliseconds;
                    LogService.Info("full-smoke", $"FULL_SMOKE_PHASE {name}={now - fullPhaseLast}ms total={now}ms");
                    fullPhaseLast = now;
                }
                await ProcessRunner.RunHardeningSmokeAsync();
                LogService.Info("PROCESS_HARDENING_SMOKE_OK");
                // Real isolated atomic file I/O; no real motion.txt preference changes.
                LogService.Info("regression", UiMotionSettingsService.RunIsolatedPersistenceSmoke());
                FullPhase("process-hardening");
                await fullMw.RunDirtyRefreshCoalescingSmokeAsync();
                await fullMw.RunSectionSmokeTestAsync();
                await fullMw.RunDeferredWindowSetupSmokeAsync();
                await fullMw.RunDeferredTitlebarSmokeAsync();
                await fullMw.RunSidebarDeferredSmokeAsync();
                await fullMw.RunHeroActionDeferredSmokeAsync();
                await fullMw.RunUserNavigationNoOpSmokeAsync();
                await fullMw.RunSystemDeferredRenderSmokeAsync();
                await fullMw.RunPrivacyDeferredRenderSmokeAsync();
                await fullMw.RunPrivacyRevisionRefreshSmokeAsync();
                await fullMw.RunUiDeferredRenderSmokeAsync();
                await fullMw.RunToggleStateConsistencySmokeAsync();
                await fullMw.RunImmediateToggleBackendSmokeAsync();
                await fullMw.RunContextMenuUiSmokeAsync();
                await fullMw.RunUpdateVirtualCacheSmokeAsync();
                await fullMw.RunNetworkDeferredRenderSmokeAsync();
                LogService.Info(MainWindow.RunDnsApplyPlanSmoke());
                LogService.Info(DnsProfileService.RunReadbackGuardSmoke());
                LogService.Info(StartupManagerService.RunDeltaSmoke());
                LogService.Info(StartupManagerService.RunRegistryMoveCollisionSmoke());
                await fullMw.RunMtuReadbackSmokeAsync();
                await fullMw.RunMtuProbeSmokeAsync();
                await fullMw.RunSpicetifyDeferredRenderSmokeAsync();
                await fullMw.RunRestoreCacheSmokeAsync();
                await fullMw.RunPageCacheLifetimeSmokeAsync();
                await fullMw.RunOptimizationPlanSmokeAsync();
                FullPhase("navigation-render-cache-smokes");
                // Section smoke immediately above already completed one full catalog pass.
                // Keep standalone regression at 3 passes; Full Smoke adds two more for 3 total passes.
                await fullMw.RunRegressionSmokeTestAsync(repeatedNavigationPasses: 2);
                await fullMw.RunPerformanceSmokeAsync();
                FullPhase("regression-performance");
                fullMw.RunBackingCacheTrimSmoke();
                fullMw.RunIdleCacheTrimSmoke();
                FullPhase("cache-trim");
                await fullMw.RunLeakAndCancellationAuditAsync();
                FullPhase("leak-cancellation");
                await fullMw.RunSoakAuditAsync();
                FullPhase("soak-audit");
                var batchErrors = await WorkerClientService.RunAsync<string[]>("service-set-batch",
                    WorkerPayloads.Encode(new ServiceBatchRequest(Array.Empty<ServiceChangeRequest>())), TimeSpan.FromSeconds(20));
                if (batchErrors.Length != 0) throw new InvalidOperationException("Empty service batch returned errors.");
                LogService.Info("SERVICE_BATCH_WORKER_SMOKE_OK");
                var taskBatchErrors = await new TaskSchedulerService().SetEnabledBatchAsync(Array.Empty<(string Path, bool Enabled)>());
                if (taskBatchErrors.Length != 0) throw new InvalidOperationException("Empty task batch returned errors.");
                LogService.Info("TASK_BATCH_COM_SMOKE_OK");
                TaskSchedulerService.RunCacheMergeSmoke();
                await TaskSchedulerService.RunBasicCachePreservationSmokeAsync();
                PowerPlanGuardService.RunArgumentSmoke();
                var mutationAudit = await NonTweakMutationAuditService.RunAsync();
                LogService.Info($"NON_TWEAK_MUTATION_AUDIT audited={mutationAudit.Audited} failed={mutationAudit.Failed} {string.Join("; ", mutationAudit.Details)}");
                if (mutationAudit.Failed > 0) throw new InvalidOperationException("Non-tweak mutation audit failed: " + string.Join(" | ", mutationAudit.Details.Where(x => x.Contains("=FAIL", StringComparison.OrdinalIgnoreCase))));
                var stateAudit = await StateAccuracyAuditService.RunAsync();
                LogService.Info($"STATE_ACCURACY_AUDIT audited={stateAudit.Audited} unsupported={stateAudit.Unsupported} failed={stateAudit.Failed}");
                if (stateAudit.Failed > 0) throw new InvalidOperationException("State accuracy audit failed: " + string.Join(" | ", stateAudit.Issues.Take(8)));
                var compat = PlatformCompatibilityAuditService.Run();
                LogService.Info("COMPAT_AUDIT " + compat.Summary);
                if (!compat.FutureBuildSafe) throw new InvalidOperationException("Platform compatibility audit failed: " + compat.Summary);
                if (!compat.StandardCimv2 || !compat.MmAgent) LogService.Info("COMPAT_DEGRADED providers missing; affected features stay Unsupported instead of failing navigation.");
                FullPhase("worker-state-compat-audits");
                int navigationFailuresAtEnd = fullMw.NavigationFailureCount;
                if (navigationFailuresAtEnd != navigationFailuresAtStart)
                    throw new InvalidOperationException(
                        $"Full smoke observed hidden navigation failures: start={navigationFailuresAtStart} end={navigationFailuresAtEnd}.");
                LogService.Info("FULL_SMOKE_NAVIGATION_FAILURE_GUARD_OK count=0");
                LogService.Info("FULL_SMOKE_OK");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("FULL_SMOKE_FAIL", ex); Environment.Exit(51); }
            return true;
        }
        if (HasArg(cli, "--startup-smoke"))
        {
            await Task.Delay(2500);
            LogService.Info($"STARTUP_SMOKE_OK activatedMs={windowActivatedMs}");
            Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--power-preview") && _window is MainWindow powerPreviewMw)
        {
            await Task.Delay(350);
            await powerPreviewMw.ShowPowerPreviewAsync();
            return true;
        }

        if (HasArg(cli, "--power-ui-smoke") && _window is MainWindow powerMw)
        {
            await Task.Delay(250);
            await powerMw.RunPowerUiSmokeAsync();
            Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--nic-ui-smoke") && _window is MainWindow nicMw)
        {
            await Task.Delay(500);
            await nicMw.RunNicUiSmokeAsync();
            Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--mtu-smoke") && _window is MainWindow mtuMw)
        {
            await Task.Delay(250);
            await mtuMw.RunMtuReadbackSmokeAsync();
            Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--mtu-probe-smoke") && _window is MainWindow mtuProbeMw)
        {
            await Task.Delay(250);
            await mtuProbeMw.RunMtuProbeSmokeAsync();
            Environment.Exit(0);
            return true;
        }

        return false;
    }
}
