using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchStorageAsync(string[] cli)
    {
        if (HasArg(cli, "--trusted-signer-identity-smoke"))
        {
            try
            {
                LogService.Info("regression", FileSecurityService.RunTrustedSignerIdentitySmoke());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("TRUSTED_SIGNER_IDENTITY_SMOKE_FAIL", ex); Environment.Exit(131); }
            return true;
        }

        if (HasArg(cli, "--archive-entry-smoke"))
        {
            try
            {
                LogService.Info("regression", ArchiveSafetyService.RunArchiveEntrySmoke());
                LogService.Info("regression", ArchiveSafetyService.RunExtractedTreeSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("ARCHIVE_ENTRY_SMOKE_FAIL", ex);
                Environment.Exit(108);
            }
            return true;
        }

        if (HasArg(cli, "--launcher-startup-match-smoke"))
        {
            try
            {
                LogService.Info("regression", LauncherProcessMatcher.RunStartupMatchingSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("LAUNCHER_STARTUP_MATCH_SMOKE_FAIL", ex); Environment.Exit(134); }
            return true;
        }

        if (HasArg(cli, "--atomic-file-smoke"))
        {
            try
            {
                LogService.Info(await AtomicFileService.RunAtomicReplacementSmokeAsync());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("ATOMIC_REPLACEMENT_SMOKE_FAIL", ex);
                Environment.Exit(107);
            }
            return true;
        }

        if (HasArg(cli, "--plugin-manifest-smoke"))
        {
            try
            {
                LogService.Info(SpankyExtensionService.RunManifestValidationSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("PLUGIN_MANIFEST_VALIDATION_SMOKE_FAIL", ex);
                Environment.Exit(106);
            }
            return true;
        }

        if (HasArg(cli, "--storage-concurrency-smoke"))
        {
            try
            {
                LogService.Info(await StorageOptimizationService.RunScheduledOptimizeCancellationSmokeAsync());
                int start = StorageOptimizationService.ActualProbeCountForDiagnostics;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var readers = Enumerable.Range(0, 12)
                    .Select(_ => StorageOptimizationService.ReadAsync()).ToArray();
                var results = await Task.WhenAll(readers);
                sw.Stop();
                int probes = StorageOptimizationService.ActualProbeCountForDiagnostics - start;
                if (probes != 1)
                    throw new InvalidOperationException($"Storage parallel reads launched {probes} probes instead of 1.");
                if (results.Any(x => x != results[0]))
                    throw new InvalidOperationException("Storage concurrent snapshot mismatch.");
                var cachedSw = System.Diagnostics.Stopwatch.StartNew();
                await StorageOptimizationService.ReadAsync();
                cachedSw.Stop();
                int cachedProbes = StorageOptimizationService.ActualProbeCountForDiagnostics - start;
                if (cachedProbes != 1)
                    throw new InvalidOperationException("Storage cache unexpectedly launched an extra probe.");
                LogService.Info(await StorageMutationGate.RunOfflineSmokeAsync());
                LogService.Info(await StorageOptimizationService.RunRollbackFaultInjectionSmokeAsync());
                LogService.Info($"STORAGE_CONCURRENCY_SMOKE_OK readers=12 probes={probes} concurrentMs={sw.ElapsedMilliseconds} cacheMs={cachedSw.ElapsedMilliseconds}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("STORAGE_CONCURRENCY_SMOKE_FAIL", ex); Environment.Exit(88); }
            return true;
        }
        if (HasArg(cli, "--storage-probe-smoke"))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var disks = await Task.Run(NativeSystemProbeService.ReadStorage);
                if (disks.Count == 0)
                    throw new InvalidOperationException("Storage probe returned no disks.");
                if (disks.Any(x => string.IsNullOrWhiteSpace(x.Name)))
                    throw new InvalidOperationException("Storage probe returned a disk without a name.");
                if (disks.All(x =>
                        string.IsNullOrWhiteSpace(x.DriverProvider) ||
                        string.IsNullOrWhiteSpace(x.DriverVersion)))
                    throw new InvalidOperationException("Storage probe returned no verified disk driver metadata.");
                long coldMs = sw.ElapsedMilliseconds;
                NativeSystemProbeService.InvalidateStorageCache(preserveStableDriverMetadata: true);
                var refreshSw = System.Diagnostics.Stopwatch.StartNew();
                var refreshed = await Task.Run(NativeSystemProbeService.ReadStorage);
                if (refreshed.Count != disks.Count)
                    throw new InvalidOperationException("Storage dynamic refresh changed disk count unexpectedly.");
                LogService.Info(
                    $"STORAGE_PROBE_SMOKE_OK count={disks.Count} coldMs={coldMs} refreshMs={refreshSw.ElapsedMilliseconds} " +
                    $"drivers={string.Join(";", disks.Select(x => x.DriverProvider + " " + x.DriverVersion).Distinct())}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("STORAGE_PROBE_SMOKE_FAIL", ex);
                Environment.Exit(66);
            }
            return true;
        }
        if (HasArg(cli, "--service-targets-read-smoke"))
        {
            try
            {
                await ServiceCatalogService.RunRecommendedStartModesSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SERVICE_TARGETS_READ_SMOKE_FAIL", ex); Environment.Exit(96); }
            return true;
        }

        return false;
    }
}
