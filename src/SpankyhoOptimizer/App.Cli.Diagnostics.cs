using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchDiagnosticsAsync(string[] cli)
    {
        if (HasArg(cli, "--windows-default-guard-smoke"))
        {
            try
            {
                LogService.Info("regression", WindowsDefaultGuardService.RunPolicySmoke());
                LogService.Info("regression", WindowsDefaultGuardService.RunJournalContentionSmoke());
                LogService.Info("regression", "DEFAULT_GUARD_TARGETED_SMOKE_OK liveRegistryWrites=0 nicChanges=0");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "DEFAULT_GUARD_TARGETED_SMOKE_FAIL", ex);
                Environment.Exit(157);
            }
            return true;
        }

        if (HasArg(cli, "--github-integration-smoke"))
        {
            try { await GithubIntegrationSmoke.RunAsync(); Environment.Exit(0); }
            catch (Exception ex) { LogService.Error("GITHUB_INTEGRATION_SMOKE_FAIL", ex); Environment.Exit(56); }
            return true;
        }
        if (HasArg(cli, "--refresh-github-sources") || HasArg(cli, "--probe-github-sources"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
                if (HasArg(cli, "--refresh-github-sources"))
                {
                    string[] ids = cli.Where(x => x.StartsWith("--source=", StringComparison.Ordinal)).Select(x => x[9..]).ToArray();
                    if (ids.Length == 0) ids = new[] { "winutil" };
                    var statuses = await UpstreamSourceUpdateService.UpdateSourcesAsync(ids, cts.Token);
                    await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(LogService.Root, "github-refresh-result.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { statuses, activeWinUtilData = WinUtilServicePolicy.ActiveCommit,
                            nativeWinUtilReview = WinUtilServicePolicy.ReviewedBehaviorCommit })).ConfigureAwait(false);
                    if (statuses.Count == 0 || statuses.Any(x => x.Error != null)) throw new InvalidOperationException("GitHub source/adapter update failed; see github-refresh-result.json");
                }
                else
                {
                    await UpstreamSourceUpdateService.ProbeUpdatesAsync(true, cts.Token);
                    await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(LogService.Root, "github-probe-result.json"),
                        System.Text.Json.JsonSerializer.Serialize(UpstreamSourceUpdateService.ReadCachedStatuses())).ConfigureAwait(false);
                }
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("GITHUB_EXPLICIT_REFRESH_FAIL", ex); Environment.Exit(56); }
            return true;
        }

        if (HasArg(cli, "--tray-glass-smoke"))
        {
            try
            {
                LogService.Info("release-smoke",
                    FanTrayIconService.RunHoverGlassOpacitySmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("release-smoke", "TRAY_GLASS_SMOKE_FAIL", ex);
                Environment.Exit(73);
            }
            return true;
        }

        if (HasArg(cli, "--tray-render-capture"))
        {
            try
            {
                string captureDir = System.IO.Path.Combine(AppContext.BaseDirectory,
                    "tray-render-captures");
                LogService.Info("release-smoke",
                    FanTrayIconService.RunHoverGdiResourceSmoke(captureDir) +
                    " folder=" + captureDir);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("release-smoke", "TRAY_RENDER_CAPTURE_FAIL", ex);
                Environment.Exit(72);
            }
            return true;
        }

        if (HasArg(cli, "--release-service-policy-smoke"))
        {
            // Pure synthetic policy checks; never invoke the stateful full
            // service smoke when only isolated release validation is wanted.
            Environment.Exit(RunReadOnlyServicePolicySmoke());
            return true;
        }

        if (HasArg(cli, "--release-service-smoke"))
        {
            int code = await RunReleaseServiceSmokeBundleAsync();
            Environment.Exit(code);
            return true;
        }

        if (HasArg(cli, "--crashloop-guard-smoke"))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _ = CrashLoopGuardService.BeginStartup(AppVersion);
                long firstBeginMs = sw.ElapsedMilliseconds;
                sw.Restart();
                CrashLoopGuardService.CompleteStartup(AppVersion);
                long firstCompleteMs = sw.ElapsedMilliseconds;

                sw.Restart();
                _ = CrashLoopGuardService.BeginStartup(AppVersion);
                long hotBeginMs = sw.ElapsedMilliseconds;
                sw.Restart();
                CrashLoopGuardService.CompleteStartup(AppVersion);
                long hotCompleteMs = sw.ElapsedMilliseconds;

                LogService.Info(
                    $"CRASHLOOP_GUARD_SMOKE_OK firstBeginMs={firstBeginMs} firstCompleteMs={firstCompleteMs} hotBeginMs={hotBeginMs} hotCompleteMs={hotCompleteMs}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("CRASHLOOP_GUARD_SMOKE_FAIL", ex);
                Environment.Exit(67);
            }
            return true;
        }

        if (HasArg(cli, "--crash-dump-smoke"))
        {
            string? dump = CrashDumpService.TryWrite("smoke");
            if (string.IsNullOrWhiteSpace(dump) || !File.Exists(dump) || new FileInfo(dump).Length < 4096)
            {
                LogService.Error("CRASH_DUMP_SMOKE_FAIL");
                Environment.Exit(53);
                return true;
            }
            long bytes = new FileInfo(dump).Length;
            try { File.Delete(dump); } catch { }
            LogService.Info($"CRASH_DUMP_SMOKE_OK bytes={bytes}");
            Environment.Exit(0);
            return true;
        }

        if (HasArg(cli, "--hardware-identity-smoke"))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var fresh = await SystemInfoService.GetSnapshotAsync(forceRefresh: true);
                long freshMs = sw.ElapsedMilliseconds;
                sw.Restart();
                var cached = await SystemInfoService.GetSnapshotAsync(forceRefresh: false);
                long cachedMs = sw.ElapsedMilliseconds;

                if (string.IsNullOrWhiteSpace(fresh.Cpu) || fresh.Cpu.Contains("neuvedeno", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("CPU identity is missing.");
                if (string.IsNullOrWhiteSpace(fresh.Motherboard) || fresh.Motherboard.Contains("neuvedena", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Motherboard identity is missing.");
                if (string.IsNullOrWhiteSpace(fresh.RamHardware) || fresh.RamHardware.Contains("neuveden", StringComparison.OrdinalIgnoreCase) ||
                    !fresh.RamHardware.Contains("GB", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("RAM hardware identity is missing.");
                if (!string.Equals(fresh.Cpu, cached.Cpu, StringComparison.Ordinal) ||
                    !string.Equals(fresh.Motherboard, cached.Motherboard, StringComparison.Ordinal) ||
                    !string.Equals(fresh.RamHardware, cached.RamHardware, StringComparison.Ordinal))
                    throw new InvalidOperationException("Hardware identity cache mismatch.");
                if (cachedMs > 20)
                    throw new InvalidOperationException($"Hardware identity cached lookup is too slow: {cachedMs} ms.");

                // Measure the hot cached-snapshot request in isolation, avoiding UI startup costs.
                var cachedTask = SystemInfoService.GetSnapshotAsync();
                if (!ReferenceEquals(cachedTask, HardwareSnapshotService.GetOverviewAsync()))
                    throw new InvalidOperationException("Overview cached wrapper allocates or replaces the completed Task.");
                for (int i = 0; i < 100; i++)
                    _ = HardwareSnapshotService.GetOverviewAsync(); // Exclude JIT warmup
                // No additional async wrapper work for concurrent cached users.
                var readerTasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
                {
                    for (int i = 0; i < 1000; i++)
                        if (!ReferenceEquals(cachedTask, HardwareSnapshotService.GetOverviewAsync()))
                            throw new InvalidOperationException("Concurrent overview task publication was inconsistent.");
                })).ToArray();
                await Task.WhenAll(readerTasks);
                long wrapperAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long wrapperStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < 10000; i++)
                    if (!ReferenceEquals(cachedTask, HardwareSnapshotService.GetOverviewAsync()))
                        throw new InvalidOperationException("Overview cached wrapper lost snapshot task identity.");
                long wrapperBytes = GC.GetAllocatedBytesForCurrentThread() - wrapperAllocatedBefore;
                double wrapperMs = System.Diagnostics.Stopwatch.GetElapsedTime(wrapperStarted).TotalMilliseconds;
                if (wrapperBytes > 0)
                    throw new InvalidOperationException($"Cached overview wrapper allocated {wrapperBytes} bytes across 10000 requests.");
                LogService.Info($"OVERVIEW_TASK_WRAPPER_CACHE_SMOKE_OK iterations=10000 ms={wrapperMs:F3} allocatedBytes={wrapperBytes}");
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var cacheBench = System.Diagnostics.Stopwatch.StartNew();
                // Keep this loop free of exception construction on the successful path.
                for (int i = 0; i < 10000; i++)
                {
                    if (!ReferenceEquals(cachedTask, SystemInfoService.GetSnapshotAsync()))
                        throw new InvalidOperationException("Cached overview snapshot creates a new Task.");
                }
                cacheBench.Stop();
                long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                // Control probes separate snapshot-cache overhead from the clock and task caller.
                long clockStart = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++) _ = DateTime.UtcNow;
                long clockBytes = GC.GetAllocatedBytesForCurrentThread() - clockStart;
                long directStart = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++)
                    if (SystemInfoService.TryGetFreshSnapshot() is null)
                        throw new InvalidOperationException("Overview snapshot cache expired unexpectedly.");
                long directBytes = GC.GetAllocatedBytesForCurrentThread() - directStart;
                long lockBaselineStart = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++)
                    _ = SystemInfoService.GetSnapshotAsync(forceRefresh: false);
                long cacheOnlyBytes = GC.GetAllocatedBytesForCurrentThread() - lockBaselineStart;
                LogService.Info($"OVERVIEW_CACHE_BENCH_CONTROL_OK iterations=10000 cacheOnlyBytes={cacheOnlyBytes} perCall={cacheOnlyBytes / 10000d:F3}");
                LogService.Info($"OVERVIEW_CACHE_BENCH_OK iterations=10000 totalMs={cacheBench.Elapsed.TotalMilliseconds:F2} allocatedBytes={allocatedBytes} bytesPerCall={allocatedBytes / 10000d:F3} clockBytes={clockBytes} directCacheBytes={directBytes}");

                LogService.Info($"HARDWARE_IDENTITY_SMOKE_OK freshMs={freshMs} cachedMs={cachedMs} cpu={fresh.Cpu} board={fresh.Motherboard} ram={fresh.RamHardware}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("HARDWARE_IDENTITY_SMOKE_FAIL", ex); Environment.Exit(65); }
            return true;
        }


        if (HasArg(cli, "--provider-degradation-smoke"))
        {
            var mem = CatalogService.GetTweaks("memory").First(x => x.Id.Equals("mem.compression", StringComparison.OrdinalIgnoreCase));
            var caps = new WindowsCapabilitySnapshot(Environment.OSVersion.Version.Build, "smoke", true, false, true, true, true, new[] { "PS_MMAgent" });
            var check = TweakCompatibilityService.CheckStatic(mem, caps);
            if (check.Supported)
            {
                LogService.Error("PROVIDER_DEGRADATION_SMOKE_FAIL");
                Environment.Exit(54);
                return true;
            }
            LogService.Info("PROVIDER_DEGRADATION_SMOKE_OK reason=" + check.Reason);
            Environment.Exit(0);
            return true;
        }

        // Pure in-memory cache-flight lifetime regression. Unlike the GPU
        // detection smoke this does not read/write/invalidate the machine cache.
        if (HasArg(cli, "--gpu-persistent-flight-smoke"))
        {
            try
            {
                LogService.Info(GpuDriverService.RunGpuPersistentWarmFlightPolicySmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("GPU_PERSISTENT_FLIGHT_POLICY_SMOKE_FAIL", ex);
                Environment.Exit(67);
            }
            return true;
        }

        if (HasArg(cli, "--gpu-detection-smoke"))
        {
            try
            {
                var coldSw = System.Diagnostics.Stopwatch.StartNew();
                var cold = await GpuDriverService.DetectPrimaryGpuAsync(forceRefresh: true);
                if (cold == null) throw new InvalidOperationException("GPU not detected on cold refresh.");
                long coldMs = coldSw.ElapsedMilliseconds;

                var warmSw = System.Diagnostics.Stopwatch.StartNew();
                var warm = await GpuDriverService.DetectPrimaryGpuAsync(forceRefresh: true);
                if (warm == null) throw new InvalidOperationException("GPU not detected on warm refresh.");
                long warmMs = warmSw.ElapsedMilliseconds;

                // Read-only driver-cache invalidation regression: a PnP event
                // must discard BOTH memory and persisted state; a subsequent
                // fresh scan should recreate exactly one current snapshot.
                long oldGeneration = GpuDriverService.DetectionCacheGeneration;
                GpuDriverService.InvalidateDetectionCache();
                if (GpuDriverService.DetectionCacheGeneration != oldGeneration + 1)
                    throw new InvalidOperationException("GPU PnP invalidation generation did not change.");
                var postPnP = await GpuDriverService.TryWarmPersistentDetectionCacheAsync();
                if (postPnP != null)
                    throw new InvalidOperationException("GPU invalidation reused stale persisted GPU information.");
                var refreshed = await GpuDriverService.DetectPrimaryGpuAsync(forceRefresh: true);
                var cached = await GpuDriverService.DetectPrimaryGpuAsync();
                if (refreshed == null || !ReferenceEquals(refreshed, cached))
                    throw new InvalidOperationException("GPU fresh scan was not published as the active cache.");

                LogService.Info($"GPU_DETECTION_SMOKE_OK coldMs={coldMs} warmMs={warmMs} gpu={warm.Name} version={warm.DisplayVersion} pnpGenerationChanged=true staleDiskRejected=true");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("GPU_DETECTION_SMOKE_FAIL", ex);
                Environment.Exit(67);
            }
            return true;
        }

        if (HasArg(cli, "--gpu-driver-smoke"))
        {
            try
            {
                var gpu = await GpuDriverService.DetectPrimaryGpuAsync();
                if (gpu == null) throw new InvalidOperationException("GPU not detected");
                var official = await GpuDriverService.CheckOfficialAsync(gpu);
                var ex = await GpuDriverService.CheckExostenzaAsync();
                if (ex != null) ex = await GpuDriverService.ResolveExostenzaDownloadAsync(ex);
                LogService.Info("GPU_DRIVER_SMOKE_OK | " + gpu.Name + " | installed=" + gpu.DisplayVersion +
                    " | official=" + (official?.Version ?? "n/a") + " | exostenza=" + (ex?.Version ?? "n/a") +
                    " | direct=" + (string.IsNullOrWhiteSpace(ex?.DownloadUrl) ? "no" : "yes"));
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("GPU_DRIVER_SMOKE_FAIL", ex); Environment.Exit(41); }
            return true;
        }

        if (HasArg(cli, "--single-instance-smoke"))
        {
            try
            {
                LogService.Info(RunSingleInstanceMutexSmoke());
                LogService.Info(await RunSingleInstanceHandoffWatcherSmokeAsync());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SINGLE_INSTANCE_SMOKE_FAIL", ex); Environment.Exit(41); }
            return true;
        }

        if (HasArg(cli, "--ai-cloud-auth-epoch-smoke"))
        {
            try
            {
                LogService.Info("regression", await AiCloudProfileService.RunCloudAuthEpochSmokeAsync());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "AI_CLOUD_AUTH_EPOCH_SMOKE_FAIL", ex);
                Environment.Exit(128);
            }
            return true;
        }

        if (HasArg(cli, "--tweak-drift-incomplete-smoke"))
        {
            try
            {
                LogService.Info("regression", TweakDriftWatchdogService.RunDriftIncompleteSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "TWEAK_DRIFT_INCOMPLETE_SMOKE_FAIL", ex);
                Environment.Exit(127);
            }
            return true;
        }

        if (HasArg(cli, "--google-signout-persistence-smoke"))
        {
            try
            {
                LogService.Info("regression", GoogleDriveOAuthService.RunSignOutPersistenceSafetySmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "GOOGLE_SIGNOUT_PERSISTENCE_SMOKE_FAIL", ex);
                Environment.Exit(125);
            }
            return true;
        }

        if (HasArg(cli, "--data-root-cleanup-safety-smoke"))
        {
            try
            {
                LogService.Info("regression", DataRootCleanupService.RunRecognitionSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "DATA_ROOT_CLEANUP_SAFETY_SMOKE_FAIL", ex);
                Environment.Exit(124);
            }
            return true;
        }

        if (HasArg(cli, "--tweak-partial-restore-history-smoke"))
        {
            try
            {
                LogService.Info("regression", TweakBatchHistoryService.RunPartialRestoreHistorySmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "TWEAK_PARTIAL_RESTORE_HISTORY_SMOKE_FAIL", ex);
                Environment.Exit(123);
            }
            return true;
        }

        if (HasArg(cli, "--log-tail-boundary-smoke"))
        {
            try
            {
                LogService.Info("regression", await LogTailService.RunTailBoundarySmokeAsync());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "LOG_TAIL_BOUNDARY_SMOKE_FAIL", ex);
                Environment.Exit(122);
            }
            return true;
        }

        if (HasArg(cli, "--wallpaper-transaction-smoke"))
        {
            try
            {
                LogService.Info("regression", WallpaperThemeService.RunWallpaperRollbackSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "WALLPAPER_TRANSACTION_SMOKE_FAIL", ex);
                Environment.Exit(121);
            }
            return true;
        }

        if (HasArg(cli, "--user-feature-fail-closed-smoke"))
        {
            try
            {
                LogService.Info("regression", UserFeatureProfileService.RunFeatureProfileFailClosedSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "USER_FEATURE_FAIL_CLOSED_SMOKE_FAIL", ex);
                Environment.Exit(120);
            }
            return true;
        }

        if (HasArg(cli, "--safe-preset-fail-closed-smoke"))
        {
            try
            {
                LogService.Info("regression", SafePresetProfileService.RunFailClosedPresetSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "SAFE_PRESET_FAIL_CLOSED_SMOKE_FAIL", ex);
                Environment.Exit(119);
            }
            return true;
        }

        if (HasArg(cli, "--app-uninstall-identity-smoke"))
        {
            try
            {
                LogService.Info("regression", AppUninstallService.RunCleanupIdentitySmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "APP_UNINSTALL_IDENTITY_SMOKE_FAIL", ex);
                Environment.Exit(118);
            }
            return true;
        }

        if (HasArg(cli, "--upstream-snapshot-recovery-smoke"))
        {
            try
            {
                LogService.Info("regression", UpstreamSourceUpdateService.RunInterruptedSnapshotRecoverySmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "UPSTREAM_SNAPSHOT_RECOVERY_SMOKE_FAIL", ex);
                Environment.Exit(117);
            }
            return true;
        }

        if (HasArg(cli, "--feature-profile-migration-smoke"))
        {
            try
            {
                LogService.Info("regression", UserFeatureProfileService.RunMigrationSafetySmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "FEATURE_PROFILE_MIGRATION_SMOKE_FAIL", ex);
                Environment.Exit(116);
            }
            return true;
        }

        if (HasArg(cli, "--json-quarantine-preservation-smoke"))
        {
            try
            {
                LogService.Info("regression", SelfHealingJsonService.RunQuarantinePreservationSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "JSON_QUARANTINE_PRESERVATION_SMOKE_FAIL", ex);
                Environment.Exit(115);
            }
            return true;
        }

        if (HasArg(cli, "--storage-temperature-generation-smoke"))
        {
            try
            {
                LogService.Info("regression", await NativeSystemProbeService.RunStorageTemperatureGenerationSmokeAsync());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "STORAGE_TEMPERATURE_GENERATION_SMOKE_FAIL", ex);
                Environment.Exit(114);
            }
            return true;
        }

        if (HasArg(cli, "--community-cursor-atomic-smoke"))
        {
            try
            {
                LogService.Info("regression", CursorCommunityPackService.RunCommunityCursorAtomicSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "COMMUNITY_CURSOR_ATOMIC_SMOKE_FAIL", ex);
                Environment.Exit(113);
            }
            return true;
        }

        return false;
    }
}
