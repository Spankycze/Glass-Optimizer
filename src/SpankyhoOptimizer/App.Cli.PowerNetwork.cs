using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchPowerNetworkAsync(string[] cli)
    {
        if (HasArg(cli, "--bad-tweak-repair-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
                await SystemRepairDiagnosticsService.RepairBadTweaksAsync(cts.Token);
                var report = await SystemRepairDiagnosticsService.ScanAsync(cts.Token);
                var bad = report.FirstOrDefault(x => x.Area == "Škodlivé / legacy tweaky");
                if (bad == null || !bad.Healthy)
                    throw new InvalidOperationException("Bad tweak repair read-back failed: " + bad?.Detail);
                LogService.Info("BAD_TWEAK_REPAIR_SMOKE_OK");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("BAD_TWEAK_REPAIR_SMOKE_FAIL", ex); Environment.Exit(72); }
            return true;
        }

        if (HasArg(cli, "--update-retention-smoke"))
        {
            try
            {
                AppUpdateService.RunRetentionSmoke();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("UPDATE_RETENTION_SMOKE_FAIL", ex); Environment.Exit(71); }
            return true;
        }

        if (HasArg(cli, "--startup-inventory-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                StartupManagerService.InvalidateCache();
                int loadsBefore = StartupManagerService.LoadCountForDiagnostics;
                var first = StartupManagerService.GetAsync(forceRefresh: true, forceTaskSchedulerRefresh: true);
                var second = StartupManagerService.GetAsync(forceRefresh: true, forceTaskSchedulerRefresh: true);
                var pair = await Task.WhenAll(first, second).WaitAsync(cts.Token);
                var items = pair[0];
                int loads = StartupManagerService.LoadCountForDiagnostics - loadsBefore;
                if (loads != 1)
                    throw new InvalidOperationException($"Startup inventory single-flight failed: loads={loads}.");
                if (!ReferenceEquals(pair[0], pair[1]) && !pair[0].SequenceEqual(pair[1]))
                    throw new InvalidOperationException("Startup inventory concurrent results differ.");
                if (items.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
                    throw new InvalidOperationException("Startup inventory contains duplicate IDs.");
                if (items.Any(x => x.SourceKind is not ("Registry" or "StartupFolder" or "ScheduledTask")))
                    throw new InvalidOperationException("Startup inventory contains unknown source type.");

                StartupManagerService.ClearMemoryCacheForSmoke();
                var persistentSw = System.Diagnostics.Stopwatch.StartNew();
                var persisted = StartupManagerService.TryGetFreshSnapshot();
                persistentSw.Stop();
                if (persisted == null || persisted.Count != items.Count)
                    throw new InvalidOperationException("Startup persistent snapshot did not hydrate the inventory.");
                if (!StartupManagerService.NeedsBackgroundRefresh)
                    throw new InvalidOperationException("Startup persistent snapshot did not request a background refresh.");
                if (persistentSw.ElapsedMilliseconds > 100)
                    throw new InvalidOperationException($"Startup persistent snapshot too slow: {persistentSw.ElapsedMilliseconds} ms.");

                // Verify the real asynchronous public entrypoint too. A cold
                // caller must not synchronously read JSON on the UI thread;
                // all consumers share the same restored snapshot and do not
                // enumerate Task Scheduler again.
                StartupManagerService.ClearMemoryCacheForSmoke();
                int loadsBeforeDisk = StartupManagerService.LoadCountForDiagnostics;
                int taskScansBeforeDisk = TaskSchedulerService.ScanCountForDiagnostics;
                var callerSw = System.Diagnostics.Stopwatch.StartNew();
                var diskFlights = Enumerable.Range(0, 8)
                    .Select(_ => StartupManagerService.GetAsync()).ToArray();
                long callerMs = callerSw.ElapsedMilliseconds;
                var diskResults = await Task.WhenAll(diskFlights).WaitAsync(cts.Token);
                if (diskResults.Any(x => x.Count != items.Count ||
                        !ReferenceEquals(x, diskResults[0])) ||
                    StartupManagerService.LoadCountForDiagnostics != loadsBeforeDisk ||
                    TaskSchedulerService.ScanCountForDiagnostics != taskScansBeforeDisk ||
                    !StartupManagerService.NeedsBackgroundRefresh)
                    throw new InvalidOperationException(
                        "Startup async persistent hydration did not share one disk snapshot without task rescans.");
                if (callerMs > 100)
                    throw new InvalidOperationException(
                        $"Startup GetAsync blocked its callers: {callerMs} ms.");
                LogService.Info($"STARTUP_ASYNC_DISK_SMOKE_OK readers=8 callerMs={callerMs} taskRescans=0 loads=0");

                int taskScansBeforeBackground = TaskSchedulerService.ScanCountForDiagnostics;
                var backgroundSw = System.Diagnostics.Stopwatch.StartNew();
                var refreshed = await StartupManagerService.GetAsync(
                    forceRefresh: true,
                    forceTaskSchedulerRefresh: false).WaitAsync(cts.Token);
                backgroundSw.Stop();
                int taskScansAfterBackground = TaskSchedulerService.ScanCountForDiagnostics;
                if (taskScansAfterBackground != taskScansBeforeBackground)
                    throw new InvalidOperationException(
                        $"Startup background refresh unexpectedly rescanned Task Scheduler: before={taskScansBeforeBackground} after={taskScansAfterBackground}.");
                if (refreshed.Count != items.Count)
                    throw new InvalidOperationException(
                        $"Startup background refresh count mismatch: fresh={items.Count} refreshed={refreshed.Count}.");
                if (StartupManagerService.NeedsBackgroundRefresh)
                    throw new InvalidOperationException("Startup background refresh did not clear refresh-pending state.");

                LogService.Info($"STARTUP_INVENTORY_SMOKE_OK total={items.Count} registry={items.Count(x => x.SourceKind == "Registry")} folders={items.Count(x => x.SourceKind == "StartupFolder")} tasks={items.Count(x => x.SourceKind == "ScheduledTask")} loads={loads} persistentMs={persistentSw.ElapsedMilliseconds} backgroundMs={backgroundSw.ElapsedMilliseconds} taskRescans={taskScansAfterBackground - taskScansBeforeBackground}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("STARTUP_INVENTORY_SMOKE_FAIL", ex); Environment.Exit(70); }
            return true;
        }

        if (HasArg(cli, "--drift-watchdog-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90));
                var report = await TweakDriftWatchdogService.CheckAppliedTweaksAsync(cts.Token);
                LogService.Info(AdaptivePowerEppTuner.RunOfflineSmoke());
                LogService.Info(AdaptivePowerRuntimeService.RunAdaptiveStatusHotpathSmoke());
                LogService.Info(AdaptivePowerRuntimeService.RunAdaptiveSharedTickSmoke());
                LogService.Info(AdaptivePowerRuntimeService.RunEppSameTickReuseSmoke());
                LogService.Info(AiSmartRuntimeService.RunWorkloadDictionaryReuseSmoke());
                LogService.Info(HardwareTelemetryBridgeService.RunBridgeIdentityReuseSmoke());
                LogService.Info(HardwareTelemetryBridgeService.RunBridgeRecoveryTaskSmoke());
                LogService.Info(AiSmartRuntimeService.RunAverageRpmNoAllocationSmoke());
                LogService.Info(AdaptivePowerRuntimeService.RunEppStatusReadPolicySmoke());
                LogService.Info(FanProfileService.RunFanCurveAllocationSmoke());
                LogService.Info(AdaptivePowerRuntimeService.RunProcessNameReuseSmoke());
                LogService.Info(AiSelfOptimizationService.RunSameProcessHealthReuseSmoke());
                LogService.Info(AiSelfOptimizationService.RunHealthFingerprintValueSmoke());
                LogService.Info(AdaptivePowerRuntimeService.RunAutonomousEppControlSmoke());
                LogService.Info(ForegroundProcessProbe.RunSnapshotReuseSmoke());
                LogService.Info(await TweakBatchHistoryService.RunOverviewSnapshotCacheSmokeAsync(cts.Token));
                LogService.Info(AiSystemSmoothnessGuardService.RunOfflineSmoke());
                LogService.Info(AiStorageGuardianService.RunOfflineSmoke());
                LogService.Info(StorageOptimizationService.RunTrimParserSmoke());
                LogService.Info(StorageOptimizationService.RunSnapshotMonotonicCacheSmoke());
                LogService.Info($"TWEAK_DRIFT_WATCHDOG_SMOKE_OK checked={report.Checked} drifted={report.Drifted.Length}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("TWEAK_DRIFT_WATCHDOG_SMOKE_FAIL", ex); Environment.Exit(71); }
            return true;
        }

        if (HasArg(cli, "--power-profile-smoke"))
        {
            await RunBackendSmokeAsync("POWER_PROFILE", 31, PerformanceProfilesService.ApplyPerformancePowerProfileAsync);
            return true;
        }

        if (HasArg(cli, "--power-registry-prewarm-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                PowerPlanStateService.InvalidateCache();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool warmed = await PowerPlanStateService.TryPrewarmRegistrySnapshotAsync(cts.Token);
                long warmMs = sw.ElapsedMilliseconds;
                if (!warmed || PowerPlanStateService.TryGetFreshSnapshot() == null)
                    throw new InvalidOperationException("Power registry prewarm did not publish a snapshot.");
                sw.Restart();
                var cached = await PowerPlanStateService.ReadAsync(cts.Token);
                long readMs = sw.ElapsedMilliseconds;
                if (cached.Settings.Count == 0)
                    throw new InvalidOperationException("Power registry prewarm returned an empty settings snapshot.");
                LogService.Info($"POWER_REGISTRY_PREWARM_SMOKE_OK warmMs={warmMs} cachedReadMs={readMs} settings={cached.Settings.Count}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("POWER_REGISTRY_PREWARM_SMOKE_FAIL", ex); Environment.Exit(56); }
            return true;
        }

        if (HasArg(cli, "--power-plan-guard-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var snapshot = await PowerPlanStateService.ReadAsync(cts.Token, forceRefresh: true);
                if (string.IsNullOrWhiteSpace(snapshot.ActiveGuid) ||
                    !snapshot.ActivePlan.Contains("Spankyho Performance", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Power Plan guard smoke vyžaduje aktivní Spankyho Performance profil.");
                await PowerPlanGuardService.EnsureAsync(snapshot.ActiveGuid, cts.Token);
                string? guarded = await PowerPlanGuardService.TryGetConfiguredGuidAsync(cts.Token);
                if (!string.Equals(snapshot.ActiveGuid, guarded, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Power Plan guard GUID mismatch active={snapshot.ActiveGuid} guard={guarded ?? "none"}");
                var guardStatus = await PowerPlanGuardService.ReadStatusAsync(snapshot.ActiveGuid, cts.Token);
                if (!guardStatus.LogonConfigured || !guardStatus.BootConfigured)
                    throw new InvalidOperationException($"Power Plan Boot/Logon guard mismatch boot={guardStatus.BootConfigured} logon={guardStatus.LogonConfigured}");

                await PowerPlanGuardService.DisableAsync(cts.Token);
                if (await PowerPlanGuardService.TryGetConfiguredGuidAsync(cts.Token) != null)
                    throw new InvalidOperationException("Power Plan guard delete smoke failed.");

                await PowerPlanGuardService.EnsureAsync(snapshot.ActiveGuid, cts.Token);
                guarded = await PowerPlanGuardService.TryGetConfiguredGuidAsync(cts.Token);
                if (!string.Equals(snapshot.ActiveGuid, guarded, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Power Plan guard recreate smoke failed.");
                if (!File.Exists(PowerPlanGuardService.GuardScriptPath))
                    throw new InvalidOperationException("Power Plan guard script po recreate chybí.");

                long guardRunCursor = await PowerPlanGuardService.RunNowAsync(
                    snapshot.ActiveGuid, cts.Token);
                await Task.Delay(1200, cts.Token);
                var afterRun = await PowerPlanStateService.ReadAsync(cts.Token, forceRefresh: true);
                if (!string.Equals(afterRun.ActiveGuid, snapshot.ActiveGuid, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Power Plan guard run změnil/neudržel aktivní GUID. expected={snapshot.ActiveGuid} actual={afterRun.ActiveGuid ?? "none"}");
                foreach (string id in new[] { "disk", "display", "sleep", "hibernate" })
                {
                    var setting = afterRun.Settings.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (setting?.IsApplied != true)
                        throw new InvalidOperationException($"Power Plan guard critical read-back selhal: {id}={setting?.CurrentText ?? "missing"}");
                }

                guardStatus = await PowerPlanGuardService.ReadStatusAsync(
                    snapshot.ActiveGuid, cts.Token,
                    minimumStatusRecordOffset: guardRunCursor);
                if (!guardStatus.LogonConfigured || !guardStatus.BootConfigured || !guardStatus.LastRunOk)
                    throw new InvalidOperationException($"Power Plan guard final status selhal boot={guardStatus.BootConfigured} logon={guardStatus.LogonConfigured} last={guardStatus.LastResult}");

                LogService.Info($"POWER_PLAN_GUARD_SMOKE_OK guid={guarded} boot=ok logon=ok deleteRecreate=ok runNow=ok activeReadback=ok criticalNever=4");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("POWER_PLAN_GUARD_SMOKE_FAIL", ex); Environment.Exit(56); }
            return true;
        }

        if (HasArg(cli, "--realtek-summary-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string summary = await WindowsActions.GetRealtekSummaryAsync(cts.Token);
                sw.Stop();
                if (string.IsNullOrWhiteSpace(summary))
                    throw new InvalidOperationException("Realtek summary je prázdné.");
                LogService.Info("network", $"REALTEK_SUMMARY_SMOKE_OK elapsedMs={sw.ElapsedMilliseconds} chars={summary.Length}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("network", "REALTEK_SUMMARY_SMOKE_FAIL", ex);
                Environment.Exit(88);
            }
            return true;
        }

        if (HasArg(cli, "--nic-capability-smoke"))
        {
            try
            {
                PerformanceProfilesService.InvalidateNicCache();
                NativeNicTuningService.PrewarmReadScope();
                NativeNicTuningService.ValidateProviderReadStateClassificationForSmoke();
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var caps = await PerformanceProfilesService.GetNicCapabilitiesAsync(cts.Token);
                sw.Stop();
                string adapter = caps.TryGetValue("Adapter", out var adapterName) ? adapterName : string.Empty;
                if (string.IsNullOrWhiteSpace(adapter))
                    throw new InvalidOperationException("NIC capability smoke nenašel aktivní adaptér.");
                foreach (string key in new[] { "Interrupt Moderation", "Flow Control", "Packet Coalescing", "RSS", "RSC", "Selective Suspend" })
                    if (!caps.ContainsKey(key))
                        throw new InvalidOperationException("NIC capability smoke chybí položka: " + key);

                LogService.Info("nic", $"NIC_CAPABILITY_SMOKE_OK elapsedMs={sw.ElapsedMilliseconds} adapter={adapter} interrupt={caps["Interrupt Moderation"]} flow={caps["Flow Control"]} packet={caps["Packet Coalescing"]} rss={caps["RSS"]} rsc={caps["RSC"]} selective={caps["Selective Suspend"]}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("nic", "NIC_CAPABILITY_SMOKE_FAIL", ex);
                Environment.Exit(63);
            }
            return true;
        }

        if (HasArg(cli, "--nic-smoke"))
        {
            await RunBackendSmokeAsync("NIC", 42, PerformanceProfilesService.ApplyNicPerformanceProfileAsync);
            return true;
        }

        return false;
    }
}
