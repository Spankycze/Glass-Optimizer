using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchTestsAsync(string[] cli)
    {
        if (HasArg(cli, "--process-queue-deadline-smoke"))
        {
            try
            {
                LogService.Info("regression", await ProcessRunner.RunProcessQueueDeadlineSmokeAsync());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "PROCESS_QUEUE_DEADLINE_SMOKE_FAIL", ex);
                Environment.Exit(112);
            }
            return true;
        }

        if (HasArg(cli, "--winget-id-safety-smoke"))
        {
            try
            {
                LogService.Info("winget", WingetUpdateService.RunPackageIdSafetySmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("winget", "WINGET_PACKAGE_ID_SAFETY_SMOKE_FAIL", ex);
                Environment.Exit(111);
            }
            return true;
        }

        if (HasArg(cli, "--operation-queue-deadline-smoke"))
        {
            try
            {
                LogService.Info("regression", await OperationCoordinator.RunQueueDeadlineSmokeAsync());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("regression", "OPERATION_QUEUE_DEADLINE_SMOKE_FAIL", ex);
                Environment.Exit(110);
            }
            return true;
        }

        if (HasArg(cli, "--gpu-resume-integrity-smoke"))
        {
            try
            {
                LogService.Info("drivers", GpuDriverService.RunResumeIntegritySmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("drivers", "GPU_RESUME_INTEGRITY_SMOKE_FAIL", ex);
                Environment.Exit(109);
            }
            return true;
        }

        if (HasArg(cli, "--exostenza-cache-reuse-smoke"))
        {
            try
            {
                LogService.Info(GpuDriverService.RunExostenzaCacheReuseSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("EXOSTENZA_CACHE_REUSE_SMOKE_FAIL", ex); Environment.Exit(41); }
            return true;
        }

        if (HasArg(cli, "--shell-setup-idempotency-smoke"))
        {
            try
            {
                LogService.Info(PowerShellCenterService.RunProfileIdempotencySmoke());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SHELL_SETUP_IDEMPOTENCY_SMOKE_FAIL", ex); Environment.Exit(41); }
            return true;
        }

        if (HasArg(cli, "--startup-delta-smoke"))
        {
            try
            {
                LogService.Info(StartupManagerService.RunDeltaSmoke());
                LogService.Info(StartupManagerService.RunRegistryMoveCollisionSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("STARTUP_DELTA_SMOKE_FAIL", ex); Environment.Exit(41); }
            return true;
        }

        if (HasArg(cli, "--dns-readback-guard-smoke"))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string result = DnsProfileService.RunReadbackGuardSmoke();
                sw.Stop();
                LogService.Info(result + $" elapsedMs={sw.ElapsedMilliseconds}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("DNS_READBACK_GUARD_SMOKE_FAIL", ex); Environment.Exit(41); }
            return true;
        }

        if (HasArg(cli, "--dns-benchmark-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var results = await DnsBenchmarkService.RunSmokeAsync(cts.Token);
                var alive = results.Where(x => x.IsAlive).ToArray();
                if (results.Count < 8) throw new InvalidOperationException($"DNS benchmark returned too few smoke resolvers: {results.Count}.");
                if (alive.Length == 0) throw new InvalidOperationException("DNS benchmark received no resolver responses.");
                if (alive.Any(x => x.AverageMs <= 0 || x.Successful <= 0)) throw new InvalidOperationException("DNS benchmark returned invalid latency data.");
                LogService.Info($"DNS_BENCHMARK_SMOKE_OK total={results.Count} alive={alive.Length} best={alive[0].Name} avgMs={alive[0].AverageMs:0.0} elapsedMs={sw.ElapsedMilliseconds}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("DNS_BENCHMARK_SMOKE_FAIL", ex); Environment.Exit(74); }
            return true;
        }

        if (HasArg(cli, "--dns-catalog-update-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await DnsResolverCatalogService.UpdateAsync(cts.Token);
                if (result.Count < 300 || result.UdpTcp < 100 || result.Doh < 50 || result.Dot < 30)
                    throw new InvalidOperationException($"DNS catalog update returned suspicious counts: {result}.");
                LogService.Info($"DNS_CATALOG_UPDATE_SMOKE_OK total={result.Count} udp={result.UdpTcp} doh={result.Doh} dot={result.Dot} doq={result.Doq} elapsedMs={sw.ElapsedMilliseconds}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("DNS_CATALOG_UPDATE_SMOKE_FAIL", ex); Environment.Exit(75); }
            return true;
        }

        return false;
    }
}
