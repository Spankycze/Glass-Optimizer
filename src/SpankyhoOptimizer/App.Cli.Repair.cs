using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchRepairAsync(string[] cli)
    {
        if (HasArg(cli, "--process-runner-bench"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                var samples = new System.Collections.Generic.List<long>(20);
                for (int i = 0; i < 20; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = await ProcessRunner.RunAsync(
                        "cmd.exe",
                        "/C echo SPANKY_PROCESS_BENCH",
                        TimeSpan.FromSeconds(5),
                        cts.Token);
                    sw.Stop();
                    if (!result.Success || !result.StdOut.Contains("SPANKY_PROCESS_BENCH", StringComparison.Ordinal))
                        throw new InvalidOperationException("ProcessRunner bench child failed.");
                    samples.Add(sw.ElapsedMilliseconds);
                }

                samples.Sort();
                double median = samples.Count % 2 == 0
                    ? (samples[samples.Count / 2 - 1] + samples[samples.Count / 2]) / 2d
                    : samples[samples.Count / 2];
                long p95 = samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * 0.95) - 1)];
                LogService.Info("process",
                    $"PROCESS_RUNNER_BENCH_OK launches={samples.Count} medianMs={median:0.0} p95Ms={p95} maxMs={samples[^1]}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("process", "PROCESS_RUNNER_BENCH_FAIL", ex);
                Environment.Exit(41);
            }
            return true;
        }

        if (HasArg(cli, "--process-hardening-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await ProcessRunner.RunHardeningSmokeAsync(cts.Token);
                LogService.Info("PROCESS_HARDENING_SMOKE_OK");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("PROCESS_HARDENING_SMOKE_FAIL", ex); Environment.Exit(68); }
            return true;
        }

        if (HasArg(cli, "--write-ahead-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                await WriteAheadTransactionService.RunPersistenceSmokeAsync(cts.Token);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("WRITE_AHEAD_TRANSACTION_SMOKE_FAIL", ex); Environment.Exit(69); }
            return true;
        }

        if (HasArg(cli, "--worker-ipc-output-path-smoke"))
        {
            try
            {
                LogService.Info("regression", WorkerIpcSecurity.RunOutputPathSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("WORKER_IPC_OUTPUT_PATH_SMOKE_FAIL", ex); Environment.Exit(130); }
            return true;
        }

        if (HasArg(cli, "--worker-ipc-cleanup-smoke"))
        {
            try
            {
                WorkerClientService.RunIpcCleanupSmoke();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("WORKER_IPC_CLEANUP_SMOKE_FAIL", ex); Environment.Exit(70); }
            return true;
        }

        if (HasArg(cli, "--network-parallel-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                await NetworkDiagnosticsService.MeasureIndependentConcurrencySmokeAsync(cts.Token);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("NETWORK_PARALLEL_SMOKE_FAIL", ex);
                Environment.Exit(101);
            }
            return true;
        }

        if (HasArg(cli, "--repair-worker-ipc-smoke"))
        {
            try
            {
                WorkerHealthService.RunResolutionOrderingSmoke();
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(95));
                var report = await SystemRepairDiagnosticsService.ScanAsync(cts.Token);
                string[] expected = ["Windows Update", "Microsoft Store", "Explorer", "Network",
                    "Škodlivé / zastaralé úpravy", "Game Bar / Capture", "GPU driver"];
                if (report.Count != expected.Length ||
                    report.Where((item, index) => item.Area != expected[index]).Any())
                    throw new InvalidOperationException("Fixer worker IPC diagnostic payload does not contain seven ordered areas.");
                if (WorkerHealthService.Snapshot().Any(x =>
                    x.Action == "repair-scan" && (x.Failures != 0 || x.LastError != null)))
                    throw new InvalidOperationException(
                        "Successful repair worker did not resolve its previous error state.");
                LogService.Info("fixer", "REPAIR_WORKER_IPC_SMOKE_OK areas=7 response=deserialized resolved=true writes=false");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("REPAIR_WORKER_IPC_SMOKE_FAIL", ex);
                Environment.Exit(102);
            }
            return true;
        }

        if (HasArg(cli, "--repair-scan-parallel-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(95));
                await SystemRepairDiagnosticsService.RunParallelScanCacheSmokeAsync(cts.Token);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("REPAIR_SCAN_PARALLEL_SMOKE_FAIL", ex);
                Environment.Exit(100);
            }
            return true;
        }

        if (HasArg(cli, "--native-service-repair-smoke"))
        {
            try
            {
                SystemRepairDiagnosticsService.VerifyNativeServiceRepairReadOnly();
                NativeNetworkBindingRepairService.VerifyReadOnly();
                LogService.Info("performance", "NATIVE_SERVICE_REPAIR_SMOKE_OK service=MMCSS binding=MSFT_NetAdapterBindingSettingData selection=true methods=Enable/Disable writes=false");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("NATIVE_SERVICE_REPAIR_SMOKE_FAIL", ex); Environment.Exit(93); }
            return true;
        }

        if (HasArg(cli, "--shell-profile-cache-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(50));
                await ShellHealthService.RunProfileCacheSmokeAsync(cts.Token);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SHELL_PROFILE_CACHE_SMOKE_FAIL", ex); Environment.Exit(94); }
            return true;
        }

        if (HasArg(cli, "--shell-profile-disk-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var report = await ShellHealthService.AnalyzeAsync(cts.Token);
                // Allow the async atomic cache commit to finish before the
                // next independent process validates its cold-disk hit.
                await Task.Delay(150, cts.Token);
                LogService.Info("SHELL_PROFILE_DISK_SMOKE_OK profileErrors=" +
                    report.Findings.Count(x => x.Area == "PowerShell profile" && x.Severity == "Error"));
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SHELL_PROFILE_DISK_SMOKE_FAIL", ex); Environment.Exit(101); }
            return true;
        }

        if (HasArg(cli, "--shell-profile-costs-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(55));
                var costs = await ShellHealthService.MeasureKnownProfileCostsAsync(cts.Token);
                if (costs.Count != 6 || costs.Any(x => !double.IsFinite(x.Milliseconds) || x.Milliseconds < 0))
                    throw new InvalidOperationException("Batched PowerShell profile costs returned invalid results.");
                LogService.Info("performance", "SHELL_PROFILE_COSTS_SMOKE_OK count=" + costs.Count +
                    " available=" + costs.Count(x => x.Available) +
                    " values=" + string.Join(",", costs.Select(x => x.Name + ":" + (x.Available ? x.Milliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "NA"))));
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SHELL_PROFILE_COSTS_SMOKE_FAIL", ex); Environment.Exit(92); }
            return true;
        }

        if (HasArg(cli, "--network-transition-batch-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(75));
                await NetworkTransitionStateService.RunBatchParitySmokeAsync(cts.Token);
                LogService.Info("performance",
                    "NETWORK_TRANSITION_BATCH_SMOKE_OK runs=3 reference=5 batched=1 writes=false");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("NETWORK_TRANSITION_BATCH_SMOKE_FAIL", ex);
                Environment.Exit(99);
            }
            return true;
        }

        if (HasArg(cli, "--native-network-binding-smoke"))
        {
            try
            {
                var disabled = SystemRepairDiagnosticsService.GetDisabledActiveNetworkBindings();
                if (disabled.Any(x => !x.StartsWith("Binding ", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Native binding diagnosis returned invalid issue format.");
                LogService.Info("NATIVE_NETWORK_BINDING_SMOKE_OK disabled=" + disabled.Count);
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("NATIVE_NETWORK_BINDING_SMOKE_FAIL", ex); Environment.Exit(91); }
            return true;
        }

        return false;
    }
}
