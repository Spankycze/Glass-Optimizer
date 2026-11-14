using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchPipelineAsync(string[] cli)
    {
        if (HasArg(cli, "--process-callback-smoke"))
        {
            try
            {
                int callbacks = 0;
                var ack = await WorkerClientService.RunStreamingAsync<WorkerAck>("stream-smoke", null, TimeSpan.FromSeconds(15), System.Threading.CancellationToken.None,
                    line => { callbacks++; if (callbacks == 1) throw new InvalidOperationException("Expected callback failure"); },
                    _ => { });
                if (!ack.Ok || callbacks == 0) throw new InvalidOperationException("Callback guard smoke did not receive output.");
                LogService.Info($"PROCESS_CALLBACK_SMOKE_OK callbacks={callbacks}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("PROCESS_CALLBACK_SMOKE_FAIL", ex); Environment.Exit(56); }
            return true;
        }
        if (HasArg(cli, "--process-timeout-smoke"))
        {
            try
            {
                int baselineProcesses = System.Diagnostics.Process.GetProcessesByName("SpankyhoOptimizer").Count(x => x.Id != Environment.ProcessId);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool timedOut = false;
                try
                {
                    await WorkerClientService.RunAsync<WorkerAck>("delay-smoke", null, TimeSpan.FromMilliseconds(450), System.Threading.CancellationToken.None);
                }
                catch (TimeoutException) { timedOut = true; }
                sw.Stop();
                if (!timedOut) throw new InvalidOperationException("Timeout smoke did not time out.");
                if (sw.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException($"Timeout cleanup was too slow: {sw.ElapsedMilliseconds}ms.");
                await Task.Delay(250);
                int afterProcesses = System.Diagnostics.Process.GetProcessesByName("SpankyhoOptimizer").Count(x => x.Id != Environment.ProcessId);
                if (afterProcesses > baselineProcesses) throw new InvalidOperationException($"Timeout smoke leaked process(es): before={baselineProcesses}, after={afterProcesses}.");
                LogService.Info($"PROCESS_TIMEOUT_SMOKE_OK elapsedMs={sw.ElapsedMilliseconds} before={baselineProcesses} after={afterProcesses}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("PROCESS_TIMEOUT_SMOKE_FAIL", ex); Environment.Exit(57); }
            return true;
        }
        if (HasArg(cli, "--process-cancel-smoke"))
        {
            try
            {
                int baselineProcesses = System.Diagnostics.Process.GetProcessesByName("SpankyhoOptimizer").Count(x => x.Id != Environment.ProcessId);
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(350));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool cancelled = false;
                try
                {
                    await WorkerClientService.RunAsync<WorkerAck>("delay-smoke", null, TimeSpan.FromSeconds(15), cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { cancelled = true; }
                sw.Stop();
                if (!cancelled) throw new InvalidOperationException("Cancel smoke did not cancel.");
                if (sw.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException($"Cancel cleanup was too slow: {sw.ElapsedMilliseconds}ms.");
                await Task.Delay(250);
                int afterProcesses = System.Diagnostics.Process.GetProcessesByName("SpankyhoOptimizer").Count(x => x.Id != Environment.ProcessId);
                if (afterProcesses > baselineProcesses) throw new InvalidOperationException($"Cancel smoke leaked process(es): before={baselineProcesses}, after={afterProcesses}.");
                LogService.Info($"PROCESS_CANCEL_SMOKE_OK elapsedMs={sw.ElapsedMilliseconds} before={baselineProcesses} after={afterProcesses}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("PROCESS_CANCEL_SMOKE_FAIL", ex); Environment.Exit(58); }
            return true;
        }
        if (HasArg(cli, "--cache-cleanup-smoke"))
        {
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "SpankyhoCacheSmoke_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "nested"));
                await File.WriteAllBytesAsync(Path.Combine(root, "a.bin"), new byte[32768]);
                await File.WriteAllBytesAsync(Path.Combine(root, "nested", "b.bin"), new byte[16384]);
                var result = await LauncherCacheService.ClearVerifiedAsync(new[] { root });
                if (result.BeforeBytes < 49152 || result.AfterBytes != 0 || result.FailedFiles != 0)
                    throw new InvalidOperationException($"before={result.BeforeBytes} after={result.AfterBytes} failed={result.FailedFiles}");
                try { Directory.Delete(root, true); } catch { }
                LogService.Info($"CACHE_CLEANUP_SMOKE_OK before={result.BeforeBytes} freed={result.FreedBytes} after={result.AfterBytes}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("CACHE_CLEANUP_SMOKE_FAIL", ex); Environment.Exit(50); }
            return true;
        }

        if (HasArg(cli, "--apply-profile-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                string result = await ApplyProfileSmokeService.RunAsync(cts.Token);
                LogService.Info("APPLY_PROFILE_SMOKE_OK | " + result);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("APPLY_PROFILE_SMOKE_FAIL", ex); Environment.Exit(48); }
            return true;
        }

        if (HasArg(cli, "--preflight-warm-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                string result = await ApplyProfileSmokeService.RunPreflightWarmAsync(cts.Token);
                LogService.Info("PREFLIGHT_WARM_SMOKE_OK | " + result);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("PREFLIGHT_WARM_SMOKE_FAIL", ex); Environment.Exit(48); }
            return true;
        }

        if (HasArg(cli, "--startup-warm-apply-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                string result = await ApplyProfileSmokeService.RunStartupWarmApplyAsync(cts.Token);
                LogService.Info("STARTUP_WARM_APPLY_SMOKE_OK | " + result);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("STARTUP_WARM_APPLY_SMOKE_FAIL", ex); Environment.Exit(48); }
            return true;
        }

        if (HasArg(cli, "--bcd-cache-smoke"))
        {
            try
            {
                BcdEditStateService.Invalidate();
                int before = BcdEditStateService.LoadCountForDiagnostics;
                var pair = await Task.WhenAll(BcdEditStateService.GetCurrentAsync(), BcdEditStateService.GetCurrentAsync());
                int loads = BcdEditStateService.LoadCountForDiagnostics - before;
                if (loads != 1 || !string.Equals(pair[0], pair[1], StringComparison.Ordinal))
                    throw new InvalidOperationException($"BCD cache single-flight failed: loads={loads}");

                BcdEditStateService.ClearMemoryCacheForSmoke();
                int beforePersistent = BcdEditStateService.LoadCountForDiagnostics;
                var persistentSw = System.Diagnostics.Stopwatch.StartNew();
                string persisted = await BcdEditStateService.GetCurrentAsync();
                persistentSw.Stop();
                if (!string.Equals(pair[0], persisted, StringComparison.Ordinal))
                    throw new InvalidOperationException("BCD persistent cache returned a different snapshot.");
                if (BcdEditStateService.LoadCountForDiagnostics != beforePersistent)
                    throw new InvalidOperationException("BCD persistent cache unexpectedly launched bcdedit.exe.");

                LogService.Info($"BCD_CACHE_SMOKE_OK loads={loads} persistentMs={persistentSw.ElapsedMilliseconds} chars={pair[0].Length}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("BCD_CACHE_SMOKE_FAIL", ex); Environment.Exit(48); }
            return true;
        }

        if (HasArg(cli, "--bcd-persistent-read-smoke"))
        {
            try
            {
                int before = BcdEditStateService.LoadCountForDiagnostics;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string snapshot = await BcdEditStateService.GetCurrentAsync();
                sw.Stop();
                if (string.IsNullOrWhiteSpace(snapshot))
                    throw new InvalidOperationException("BCD persistent read returned an empty snapshot.");
                if (BcdEditStateService.LoadCountForDiagnostics != before)
                    throw new InvalidOperationException("BCD cross-process persistent cache missed and launched bcdedit.exe.");
                LogService.Info($"BCD_PERSISTENT_PROCESS_SMOKE_OK ms={sw.ElapsedMilliseconds} chars={snapshot.Length}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("BCD_PERSISTENT_PROCESS_SMOKE_FAIL", ex); Environment.Exit(48); }
            return true;
        }

        if (HasArg(cli, "--atomic-apply-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                string result = await AtomicApplySmokeService.RunAsync(cts.Token);
                LogService.Info("ATOMIC_APPLY_SMOKE_OK | " + result);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("ATOMIC_APPLY_SMOKE_FAIL", ex); Environment.Exit(48); }
            return true;
        }
        if (HasArg(cli, "--native-perf-smoke"))
        {
            try
            {
                var r = await NativePerformanceSmokeService.RunAsync();
                LogService.Info($"NATIVE_PERF_SMOKE_OK rust={r.RustAvailable} managedPowerUs={r.ManagedPowerUs:0.000} rustPowerUs={r.RustPowerUs:0.000} managedDevicesMs={r.ManagedDevicesMs:0.00} rustDevicesMs={r.RustDevicesMs:0.00} managedCount={r.ManagedDeviceCount} rustCount={r.RustDeviceCount} managedServiceUs={r.ManagedServiceUs:0.000} rustServiceUs={r.RustServiceUs:0.000} preferPower={r.PreferRustPower} preferDevices={r.PreferRustDevices} preferService={r.PreferRustService}");
                Environment.Exit(r.RustAvailable && r.ManagedDeviceCount == r.RustDeviceCount ? 0 : 49);
            }
            catch (Exception ex) { LogService.Error("NATIVE_PERF_SMOKE_FAIL", ex); Environment.Exit(49); }
            return true;
        }
        if (HasArg(cli, "--update-smoke"))
        {
            try
            {
                var check = await AppUpdateService.CheckAsync();
                if (!check.Configured || !check.Available || check.Manifest == null) throw new InvalidOperationException(check.Message);
                string path = await AppUpdateService.DownloadAsync(check.Manifest);
                if (!File.Exists(path)) throw new FileNotFoundException("Stažený installer nebyl nalezen.", path);
                LogService.Info($"UPDATE_SMOKE_OK | version={check.Manifest.Version} | file={Path.GetFileName(path)}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("UPDATE_SMOKE_FAIL", ex); Environment.Exit(44); }
            return true;
        }

        if (HasArg(cli, "--recommended-detect-smoke"))
        {
            try
            {
                string[] sections = { "system", "gpu", "memory", "storage", "network", "ui", "update", "privacy", "ai", "defender", "services", "drivers", "tasks" };
                var recommended = sections.SelectMany(x => CatalogService.GetTweaks(x))
                    .Where(x => x.Recommended)
                    .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToArray();

                var failures = new System.Collections.Generic.List<string>();
                var inactiveIds = new System.Collections.Generic.List<string>();
                int active = 0;
                foreach (var tweak in recommended)
                {
                    using var detectCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try
                    {
                        bool current = await tweak.DetectAsync(detectCts.Token);
                        if (current) active++;
                        else inactiveIds.Add(tweak.Id);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(tweak.Id + "=" + ex.GetType().Name + ":" + ex.Message);
                    }
                }

                if (failures.Count > 0)
                    throw new InvalidOperationException("Recommended detect failures: " + string.Join(" | ", failures.Take(20)));
                LogService.Info($"RECOMMENDED_DETECT_SMOKE_OK count={recommended.Length} active={active} inactive={recommended.Length - active}");
                if (inactiveIds.Count > 0)
                    LogService.Info("RECOMMENDED_DETECT_INACTIVE " + string.Join(",", inactiveIds));
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("RECOMMENDED_DETECT_SMOKE_FAIL", ex); Environment.Exit(73); }
            return true;
        }

        if (HasArg(cli, "--audit-smoke") || HasArg(cli, "--catalog-audit-smoke"))
        {
            var originalUi = UiStateService.Load();
            var originalTheme = AppThemeService.LoadPreference();
            try
            {
                if (CatalogService.Sections.Any(x => x.Key.Equals("vibrance", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Vibrance section still exists.");
                var system = CatalogService.GetTweaks("system");
                if (system.Any(x => x.Id.Equals("sys.startupdelay", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Startup Delay is still in System tweaks.");
                var privacy = CatalogService.GetTweaks("privacy");
                if (privacy.Any(x => x.Id.StartsWith("sec.", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Privacy still exposes sec.* hardening.");
                var sources = CatalogService.EnumerateTweakSources();
                string[] requiredSources =
                {
                    "system|", "gpu|", "memory|", "storage|", "network|",
                    "components|Služby", "components|Ovladače", "components|Task Scheduler",
                    "ui|", "update|", "privacy|", "ai|", "defender|"
                };
                var sourceKeys = sources
                    .Select(x => x.Section + "|" + (x.Sub ?? string.Empty))
                    .ToArray();
                var duplicateSourceKeys = sourceKeys
                    .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToArray();
                if (duplicateSourceKeys.Length > 0)
                    throw new InvalidOperationException("Duplicate tweak catalog sources: " + string.Join(",", duplicateSourceKeys));

                var missingSources = requiredSources
                    .Where(required => !sourceKeys.Contains(required, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                if (missingSources.Length > 0)
                    throw new InvalidOperationException("Required tweak catalog sources missing: " + string.Join(",", missingSources));

                var emptySources = sources
                    .Select(x => new { Source = x, Tweaks = CatalogService.GetTweaks(x.Section, x.Sub) })
                    .Where(x => x.Tweaks.Count == 0)
                    .Select(x => x.Source.Section + "|" + (x.Source.Sub ?? string.Empty))
                    .ToArray();
                if (emptySources.Length > 0)
                    throw new InvalidOperationException("Tweak catalog sources unexpectedly empty: " + string.Join(",", emptySources));

                var all = sources
                    .SelectMany(x => CatalogService.GetTweaks(x.Section, x.Sub))
                    .ToArray();
                var recommended = all.Where(x => x.Recommended).ToArray();

                var registryTweaks = all.Where(x =>
                        x.RegistryHive.HasValue &&
                        !string.IsNullOrWhiteSpace(x.RegistryPath) &&
                        !string.IsNullOrWhiteSpace(x.RegistryValueName))
                    .ToArray();
                var registryWithoutOriginalRevert = registryTweaks
                    .Where(x => !x.CanRestoreOriginal ||
                                !x.RevertMode.Equals("OriginalRegistryValue", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Id)
                    .ToArray();
                if (registryWithoutOriginalRevert.Length > 0)
                    throw new InvalidOperationException("Registry tweaks without original-value revert: " +
                        string.Join(",", registryWithoutOriginalRevert));

                var nvidiaTelemetry = all.FirstOrDefault(x => x.Id.Equals("gt.privacy.nvtelemetry", StringComparison.OrdinalIgnoreCase));
                var intelTelemetry = all.FirstOrDefault(x => x.Id.Equals("gt.privacy.inteltelemetry", StringComparison.OrdinalIgnoreCase));
                if (nvidiaTelemetry == null || !nvidiaTelemetry.HardwareScope.Equals("nvidia", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("NVIDIA tweak compatibility scope missing.");
                if (intelTelemetry == null || !intelTelemetry.HardwareScope.Equals("intel", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Intel tweak compatibility scope missing.");

                var cleanerScan = await StorageMaintenanceService.ScanSafeCleanupAsync();
                string[] requiredCleanerCategories = { "temp", "d3d", "nvidia", "amd" };
                var cleanerIds = cleanerScan.Categories.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (requiredCleanerCategories.Any(x => !cleanerIds.Contains(x)))
                    throw new InvalidOperationException("Cleaner scan categories are incomplete.");
                if (cleanerScan.TotalBytes != cleanerScan.Categories.Sum(x => x.Bytes))
                    throw new InvalidOperationException("Cleaner scan total does not match category sum.");

                var duplicateIds = all.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
                if (duplicateIds.Length > 0)
                    throw new InvalidOperationException("Duplicate tweak IDs: " + string.Join(",", duplicateIds));

                var unsafeRecommended = recommended.Where(x => x.Risk == Models.TweakRisk.Advanced).Select(x => x.Id).ToArray();
                if (unsafeRecommended.Length > 0)
                    throw new InvalidOperationException("Advanced Recommended: " + string.Join(",", unsafeRecommended));

                var registryConflicts = recommended
                    .Where(x => x.RegistryHive.HasValue && !string.IsNullOrWhiteSpace(x.RegistryPath) &&
                                !string.IsNullOrWhiteSpace(x.RegistryValueName) && x.RegistryEnabledValue.HasValue)
                    .GroupBy(x => $"{x.RegistryHive}|{x.RegistryPath}|{x.RegistryValueName}", StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Select(x => x.RegistryEnabledValue!.Value).Distinct().Count() > 1)
                    .Select(g => g.Key + " => " + string.Join("/", g.Select(x => x.Id + ":" + x.RegistryEnabledValue)))
                    .ToArray();
                if (registryConflicts.Length > 0)
                    throw new InvalidOperationException("Recommended registry conflicts: " + string.Join(" | ", registryConflicts));

                var recommendedIds = recommended.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var declaredConflicts = recommended
                    .Where(x => x.ConflictsWithTweaks.Any(recommendedIds.Contains))
                    .Select(x => x.Id + "<->" + string.Join(",", x.ConflictsWithTweaks.Where(recommendedIds.Contains)))
                    .ToArray();
                if (declaredConflicts.Length > 0)
                    throw new InvalidOperationException("Recommended declared conflicts: " + string.Join(" | ", declaredConflicts));

                var missingRequirements = recommended
                    .Where(x => x.RequiresTweaks.Any(req => !recommendedIds.Contains(req)))
                    .Select(x => x.Id + " requires " + string.Join(",", x.RequiresTweaks.Where(req => !recommendedIds.Contains(req))))
                    .ToArray();
                if (missingRequirements.Length > 0)
                    throw new InvalidOperationException("Recommended missing requirements: " + string.Join(" | ", missingRequirements));
                UiStateService.Save(new UiState("privacy", null, "Recommended", 123.0));
                var roundtrip = UiStateService.Load();
                if (roundtrip.Section != "privacy" || roundtrip.ViewMode != "Recommended" || Math.Abs(roundtrip.ScrollOffset - 123.0) > .01) throw new InvalidOperationException("UI state round-trip failed.");
                AppThemeService.SavePreference(AppThemePreference.Light);
                if (AppThemeService.LoadPreference() != AppThemePreference.Light || AppThemeService.ResolveDark(AppThemePreference.Light)) throw new InvalidOperationException("Light theme round-trip failed.");
                AppThemeService.SavePreference(AppThemePreference.Dark);
                if (AppThemeService.LoadPreference() != AppThemePreference.Dark || !AppThemeService.ResolveDark(AppThemePreference.Dark)) throw new InvalidOperationException("Dark theme round-trip failed.");
                LogService.Info($"AUDIT_SMOKE_OK sections={CatalogService.Sections.Count} auditedTweaks={all.Length} privacy={privacy.Count} defaultTheme={originalTheme}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("AUDIT_SMOKE_FAIL", ex); Environment.Exit(52); }
            finally { UiStateService.Save(originalUi); AppThemeService.SavePreference(originalTheme); }
            return true;
        }

        return false;
    }
}
