using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private static async Task<int> RunReleaseServiceSmokeBundleAsync()
    {
        static async Task RunAsync(string gate, Func<Task> action)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await action();
            sw.Stop();
            LogService.Info("release-smoke", $"{gate}_OK ms={sw.ElapsedMilliseconds}");
        }

        try
        {
            await RunAsync("MACHINE_STABLE_MEMORY_CACHE_SMOKE", () =>
            {
                PersistentHardwareCacheService.RunMachineStableMemoryCacheSmoke();
                return Task.CompletedTask;
            });

            await RunAsync("HARDWARE_IDENTITY_SMOKE", async () =>
            {
                var fresh = await SystemInfoService.GetSnapshotAsync(forceRefresh: true);
                var cached = await SystemInfoService.GetSnapshotAsync(forceRefresh: false);
                if (string.IsNullOrWhiteSpace(fresh.Cpu) || fresh.Cpu.Contains("neuvedeno", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("CPU identity is missing.");
                if (string.IsNullOrWhiteSpace(fresh.Motherboard) || fresh.Motherboard.Contains("neuvedena", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Motherboard identity is missing.");
                if (string.IsNullOrWhiteSpace(fresh.RamHardware) || !fresh.RamHardware.Contains("GB", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("RAM hardware identity is missing.");
                if (!string.Equals(fresh.Cpu, cached.Cpu, StringComparison.Ordinal) ||
                    !string.Equals(fresh.Motherboard, cached.Motherboard, StringComparison.Ordinal) ||
                    !string.Equals(fresh.RamHardware, cached.RamHardware, StringComparison.Ordinal))
                    throw new InvalidOperationException("Hardware identity cache mismatch.");
            });

            await RunAsync("SERVICE_RECOMMENDED_FAST_READ_SMOKE",
                ServiceCatalogService.RunRecommendedStartModesSmokeAsync);

            await RunAsync("STORAGE_PROBE_SMOKE", async () =>
            {
                var disks = await Task.Run(NativeSystemProbeService.ReadStorage);
                if (disks.Count == 0 || disks.Any(x => string.IsNullOrWhiteSpace(x.Name)))
                    throw new InvalidOperationException("Storage probe returned invalid disk inventory.");
                if (disks.All(x => string.IsNullOrWhiteSpace(x.DriverProvider) || string.IsNullOrWhiteSpace(x.DriverVersion)))
                    throw new InvalidOperationException("Storage probe returned no verified disk driver metadata.");
                NativeSystemProbeService.InvalidateStorageCache(preserveStableDriverMetadata: true);
                var refreshed = await Task.Run(NativeSystemProbeService.ReadStorage);
                if (refreshed.Count != disks.Count)
                    throw new InvalidOperationException("Storage refresh changed disk count unexpectedly.");
            });

            await RunAsync("STORAGE_TEMPERATURE_STALE_CACHE_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                await NativeSystemProbeService.RunStorageTemperatureStaleCacheSmokeAsync(cts.Token);
            });

            await RunAsync("WRITE_AHEAD_TRANSACTION_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await WriteAheadTransactionService.RunPersistenceSmokeAsync(cts.Token);
            });

            await RunAsync("WORKER_IPC_OUTPUT_PATH_SMOKE", () =>
            {
                LogService.Info("regression", WorkerIpcSecurity.RunOutputPathSmoke());
                return Task.CompletedTask;
            });

            await RunAsync("WORKER_IPC_CLEANUP_SMOKE", () =>
            {
                WorkerClientService.RunIpcCleanupSmoke();
                return Task.CompletedTask;
            });

            await RunAsync("UPDATE_RETENTION_SMOKE", () =>
            {
                AppUpdateService.RunRetentionSmoke();
                return Task.CompletedTask;
            });

            await RunAsync("STARTUP_INVENTORY_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                StartupManagerService.InvalidateCache();
                int before = StartupManagerService.LoadCountForDiagnostics;
                var first = StartupManagerService.GetAsync(forceRefresh: true, forceTaskSchedulerRefresh: true);
                var second = StartupManagerService.GetAsync(forceRefresh: true, forceTaskSchedulerRefresh: true);
                var pair = await Task.WhenAll(first, second).WaitAsync(cts.Token);
                int loads = StartupManagerService.LoadCountForDiagnostics - before;
                if (loads != 1)
                    throw new InvalidOperationException($"Startup inventory single-flight failed: loads={loads}.");
                var items = pair[0];
                if (items.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
                    throw new InvalidOperationException("Startup inventory contains duplicate IDs.");
            });

            await RunAsync("TWEAK_DRIFT_WATCHDOG_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90));
                _ = await TweakDriftWatchdogService.CheckAppliedTweaksAsync(cts.Token);
            });

            await RunAsync("WINDOWS_BUILD_REVALIDATION_SMOKE", () =>
            {
                var state = WindowsBuildRevalidationService.CheckAndUpdate();
                int current = Environment.OSVersion.Version.Build;
                if (state.CurrentBuild != current)
                    throw new InvalidOperationException($"Windows build revalidation mismatch: state={state.CurrentBuild} current={current}.");
                return Task.CompletedTask;
            });

            await RunAsync("NIC_CAPABILITY_SMOKE", async () =>
            {
                NativeNicTuningService.ValidateProviderReadStateClassificationForSmoke();
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                var caps = await PerformanceProfilesService.GetNicCapabilitiesAsync(cts.Token);
                string adapter = caps.TryGetValue("Adapter", out var adapterName) ? adapterName : string.Empty;
                if (string.IsNullOrWhiteSpace(adapter))
                    throw new InvalidOperationException("NIC capability smoke did not find an active adapter.");
                foreach (string key in new[] { "Interrupt Moderation", "Flow Control", "Packet Coalescing", "RSS", "RSC", "Selective Suspend" })
                    if (!caps.ContainsKey(key))
                        throw new InvalidOperationException("NIC capability smoke missing: " + key);
            });

            await RunAsync("NETWORK_TRANSITION_SINGLEFLIGHT_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await NetworkTransitionStateService.RunSingleFlightSmokeAsync(cts.Token);
            });

            await RunAsync("AI_CLOUD_UPLOAD_DEDUP_SMOKE", () =>
            {
                DateTime nowUtc = DateTime.UtcNow;
                if (!AiCloudProfileService.CanReuseRecentUpload("payload", "payload", "authA", "authA",
                        nowUtc.AddMinutes(-1), nowUtc))
                    throw new InvalidOperationException("Recent matching cloud upload should be reusable.");
                if (AiCloudProfileService.CanReuseRecentUpload("payload", "payload", "authA", "authB",
                        nowUtc.AddMinutes(-1), nowUtc))
                    throw new InvalidOperationException("Cloud upload cache reused a different OAuth session.");
                if (AiCloudProfileService.CanReuseRecentUpload("payload", "changed", "authA", "authA",
                        nowUtc.AddMinutes(-1), nowUtc))
                    throw new InvalidOperationException("Cloud upload cache reused changed content.");
                if (AiCloudProfileService.CanReuseRecentUpload("payload", "payload", "authA", "authA",
                        nowUtc.AddMinutes(-4), nowUtc))
                    throw new InvalidOperationException("Cloud upload cache should expire.");
                if (AiCloudProfileService.CanReuseRecentUpload("payload", "payload", "authA", "authA",
                        nowUtc.AddMinutes(1), nowUtc))
                    throw new InvalidOperationException("Future-dated cloud upload cache should not be reusable.");
                return Task.CompletedTask;
            });
            await RunAsync("USER_SCOPED_CACHE_CLOCK_SKEW_SMOKE", () =>
            {
                string key = "release-cache-clock-skew-" + Guid.NewGuid().ToString("N");
                try
                {
                    UserScopedCacheService.Write(key, "healthy");
                    if (!UserScopedCacheService.TryRead<string>(key, TimeSpan.FromMinutes(10), out var initial) ||
                        initial != "healthy")
                        throw new InvalidOperationException("User-scoped cache initial write/read mismatch.");

                    UserScopedCacheService.BackdateForSmoke(key, TimeSpan.FromMinutes(-5));
                    if (UserScopedCacheService.TryRead<string>(key, TimeSpan.FromMinutes(10), out _))
                        throw new InvalidOperationException("Future-dated user cache should not be accepted.");

                    UserScopedCacheService.BackdateForSmoke(key, TimeSpan.FromMinutes(11));
                    if (UserScopedCacheService.TryRead<string>(key, TimeSpan.FromMinutes(10), out _))
                        throw new InvalidOperationException("Expired user cache should not be accepted.");
                }
                finally
                {
                    UserScopedCacheService.Remove(key);
                }
                return Task.CompletedTask;
            });
            await RunAsync("MEMORY_COMPRESSION_CACHE_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await WindowsActions.RunMemoryCompressionCacheSmokeAsync(cts.Token);
            });

            await RunAsync("MEMORY_NATIVE_STATE_SMOKE", async () =>
            {
                LogService.Info("performance", MemoryStateService.RunSnapshotReadBenchmark());
                await MemoryStateService.RunWarmFirstPaintSmokeAsync();
            });

            await RunAsync("HARDWARE_MEMORY_TRIM_CACHE_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                await HardwareSnapshotService.RunMemoryTrimPersistentCacheSmokeAsync(cts.Token);
            });

            await RunAsync("GHOST_USB_CACHE_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await GhostUsbService.RunPersistentCacheSmokeAsync(cts.Token);
            });

            await RunAsync("USB_SELECTIVE_SUSPEND_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                bool? before = await WindowsActions.GetUsbSelectiveSuspendDisabledAsync(cts.Token);
                if (!before.HasValue)
                    throw new InvalidOperationException("USB Selective Suspend AC state could not be read.");
                // Release smoke must never write a live power plan, even with
                // an unchanged value. Confirm repeated native read-back only.
                bool? after = await WindowsActions.GetUsbSelectiveSuspendDisabledAsync(cts.Token);
                if (after != before)
                    throw new InvalidOperationException($"USB Selective Suspend read-only readback mismatch: before={before} after={after}.");
                LogService.Info("release-smoke", $"USB_SELECTIVE_SUSPEND_STATE disabled={after.Value} readOnly=true");
            });

            await RunAsync("FIREWALL_STATE_CACHE_SMOKE", () =>
            {
                FirewallStateService.RunPersistentCacheSmoke();
                return Task.CompletedTask;
            });

            await RunAsync("AUTHENTICODE_NATIVE_SMOKE", () =>
            {
                _ = AuthenticodeVerifier.RunSmoke();
                return Task.CompletedTask;
            });

            await RunAsync("AI_RUNTIME_HARDENING_SMOKE", RunAiRuntimeHardeningSmokeCoreAsync);

            await RunAsync("STARTUP_WARM_APPLY_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                string result = await ApplyProfileSmokeService.RunStartupWarmApplyAsync(cts.Token);
                LogService.Info("release-smoke", "STARTUP_WARM_APPLY_SMOKE_OK | " + result);
            });

            await RunAsync("SPICETIFY_STATUS_SMOKE", RunSpicetifyStatusSmokeCoreAsync);

            await RunAsync("PROVIDER_DEGRADATION_SMOKE", () =>
            {
                var mem = CatalogService.GetTweaks("memory").First(x => x.Id.Equals("mem.compression", StringComparison.OrdinalIgnoreCase));
                var caps = new WindowsCapabilitySnapshot(Environment.OSVersion.Version.Build, "smoke", true, false, true, true, true, new[] { "PS_MMAgent" });
                var check = TweakCompatibilityService.CheckStatic(mem, caps);
                if (check.Supported)
                    throw new InvalidOperationException("Provider degradation smoke expected Unsupported.");
                return Task.CompletedTask;
            });

            await RunAsync("OPTIMIZATION_ADVISOR_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(35));
                await OptimizationAdvisorSmokeService.RunAsync(cts.Token);
            });

            await RunAsync("DNS_BENCHMARK_SMOKE", async () =>
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
                var results = await DnsBenchmarkService.RunSmokeAsync(cts.Token);
                var alive = results.Where(x => x.IsAlive).ToArray();
                if (results.Count < 8)
                    throw new InvalidOperationException($"DNS benchmark returned too few smoke resolvers: {results.Count}.");
                if (alive.Length == 0)
                {
                    // The test machine may explicitly deny direct port-53
                    // sockets while ordinary OS name resolution still works.
                    // Never change its DNS/firewall just to pass a release.
                    // Only a NATIVE AccessDenied verdict, not timeout or a
                    // resolver regression, permits an honest Unsupported.
                    bool blockedByHostPolicy = false;
                    try
                    {
                        using var check = new System.Net.Sockets.TcpClient();
                        using var deadline = new System.Threading.CancellationTokenSource(
                            TimeSpan.FromSeconds(2));
                        await check.ConnectAsync(System.Net.IPAddress.Parse("1.1.1.1"),
                            53, deadline.Token);
                    }
                    catch (System.Net.Sockets.SocketException ex)
                        when (ex.SocketErrorCode ==
                            System.Net.Sockets.SocketError.AccessDenied)
                    {
                        blockedByHostPolicy = true;
                    }
                    catch (OperationCanceledException) { /* not an access-denied verdict */ }
                    catch (System.Net.Sockets.SocketException) { /* not access denied */ }
                    if (!blockedByHostPolicy)
                        throw new InvalidOperationException(
                            "DNS benchmark received no resolver responses without confirmed host access denial.");
                    LogService.Info("release-smoke",
                        $"DNS_BENCHMARK_SMOKE_UNSUPPORTED reason=host-denies-public-tcp53 " +
                        $"catalog={results.Count} alive=0 resolver-performance-unverified=true");
                }
                else
                {
                    if (alive.Any(x => x.AverageMs <= 0 || x.Successful <= 0))
                        throw new InvalidOperationException("DNS benchmark returned invalid latency data.");
                    LogService.Info("release-smoke",
                        $"DNS_BENCHMARK_SMOKE_OK total={results.Count} alive={alive.Length} best={alive[0].Name} avgMs={alive[0].AverageMs:0.0}");
                }
            });

            await RunAsync("DATA_ROOT_RECOGNITION_SMOKE", () =>
            {
                LogService.Info("release-smoke", DataRootCleanupService.RunRecognitionSmoke());
                // Pure synthetic denial/cancellation checks; no actual MMDevices
                // keys or saved audio ownership journal are touched by smoke.
                LogService.Info("release-smoke",
                    AudioEnhancementsTweakService.RunPermissionFlowSmoke());
                return Task.CompletedTask;
            });

            await RunAsync("FAN_AGENT_JSON_CONTEXT_SMOKE", () =>
            {
                LogService.Info("release-smoke",
                    FanAgentJsonBenchmark.RunCompatibilityAndPerformanceSmoke());
                return Task.CompletedTask;
            });
            await RunAsync("HARDWARE_TELEMETRY_READ_CACHE_SMOKE", () =>
            {
                LogService.Info("release-smoke",
                    HardwareTelemetrySnapshotReadCache.RunAtomicFreshnessSmoke());
                return Task.CompletedTask;
            });
            await RunAsync("HARDWARE_BRIDGE_MUTEX_THREAD_AFFINITY_SMOKE", () =>
            {
                LogService.Info("release-smoke",
                    HardwareTelemetryBridgeService.RunBridgeMutexThreadAffinitySmoke());
                return Task.CompletedTask;
            });
            await RunAsync("HARDWARE_BRIDGE_MUTEX_CANCEL_SMOKE", () =>
            {
                LogService.Info("release-smoke",
                    HardwareTelemetryBridgeService.RunBridgeMutexCancellationSmoke());
                return Task.CompletedTask;
            });
            await RunAsync("HARDWARE_BRIDGE_DIRECTORY_REUSE_SMOKE", () =>
            {
                LogService.Info("release-smoke",
                    HardwareTelemetryBridgeService.RunBridgeDirectoryHotpathSmoke());
                return Task.CompletedTask;
            });
            await RunAsync("FAN_TICK_PHASE_METRICS_SMOKE", () =>
            {
                LogService.Info("release-smoke", FanAgentStageMetrics.RunPolicySmoke());
                return Task.CompletedTask;
            });

            await RunAsync("FAN_TRAY_DEDUP_SMOKE", () =>
            {
                LogService.Info("release-smoke", FanTrayIconService.RunNotificationDedupSmoke());
                LogService.Info("release-smoke", FanTrayIconService.RunHoverGdiResourceSmoke());
                LogService.Info("release-smoke", FanTrayIconService.RunHoverGlassOpacitySmoke());
                // Assert the native GDI handle count stabilizes after lazy
                // initialization, rather than merely allowing small growth.
                LogService.Info("release-smoke", FanTrayIconService.RunHoverGdiResourceSmoke(
                    steadyState: true));
                LogService.Info("release-smoke", FanProfileService.RunTrayCompositionBenchmark());
                return Task.CompletedTask;
            });



            // Passive RuntimeHost resource sampler was removed. This bundle
            // still verifies the functional services and their safe readbacks.
            LogService.Info("release-smoke", "RELEASE_SERVICE_SMOKE_BUNDLE_OK count=29");
            return 0;
        }
        catch (Exception ex)
        {
            LogService.Error("release-smoke", "RELEASE_SERVICE_SMOKE_BUNDLE_FAIL", ex);
            return 72;
        }
    }

    private static bool HasArg(string[] args, string value) =>
        Array.Exists(args, x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));

    private static async Task RunBackendSmokeAsync(string name, int failureCode,
        Func<System.Threading.CancellationToken, Task<string>> action)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
            string result = await action(cts.Token);
            LogService.Info(name + "_SMOKE_OK | " + result.Replace(Environment.NewLine, " | "));
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            LogService.Error(name + "_SMOKE_FAIL", ex);
            Environment.Exit(failureCode);
        }
    }
}
