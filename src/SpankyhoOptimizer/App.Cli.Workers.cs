using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchWorkersAsync(string[] cli)
    {
        if (HasArg(cli, "--memory-state-read-bench"))
        {
            try
            {
                LogService.Info("performance", MemoryStateService.RunSnapshotReadBenchmark());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("MEMORY_STATE_READ_BENCH_FAIL", ex); Environment.Exit(89); }
            return true;
        }

        if (HasArg(cli, "--activity-top30-bench"))
        {
            try
            {
                LogService.Info("performance", ActivityProcessSnapshotService.RunRankingBenchmark());
                await ActivityProcessSnapshotService.RunCacheSmokeAsync();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("ACTIVITY_TOP30_BENCH_FAIL", ex); Environment.Exit(88); }
            return true;
        }

        if (HasArg(cli, "--startup-row-orphan-bench"))
        {
            try
            {
                LogService.Info("performance",
                    SpankyhoOptimizer.ViewModels.StartupRowViewModel.RunOrphanPathReadOnlyBenchmark());
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("STARTUP_ROW_ORPHAN_BENCH_FAIL", ex); Environment.Exit(87); }
            return true;
        }
        if (HasArg(cli, "--debloat-cache-smoke"))
        {
            try
            {
                await RunDebloatCacheSmokeCoreAsync();
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("DEBLOAT_CACHE_SMOKE_FAIL", ex); Environment.Exit(57); }
            return true;
        }

        if (HasArg(cli, "--task-scheduler-basic-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var tasks = await Task.Run(
                    () => TaskSchedulerComService.GetAll(cts.Token, includeStartupTrigger: false),
                    cts.Token).ConfigureAwait(false);
                sw.Stop();
                if (tasks.Count == 0)
                    throw new InvalidOperationException("Task Scheduler basic inventář je prázdný.");
                if (tasks.Any(x => x.StartupTrigger))
                    throw new InvalidOperationException("Basic Task Scheduler scan neočekávaně materializoval startup trigger.");
                LogService.Info($"TASK_SCHEDULER_BASIC_SMOKE_OK tasks={tasks.Count} ms={sw.ElapsedMilliseconds}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("TASK_SCHEDULER_BASIC_SMOKE_FAIL", ex); Environment.Exit(47); }
            return true;
        }

        if (HasArg(cli, "--task-scheduler-backend-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                var scheduler = new TaskSchedulerService();
                var freshSw = System.Diagnostics.Stopwatch.StartNew();
                var tasks = await scheduler.GetAsync(true, cts.Token);
                freshSw.Stop();
                if (tasks.Count == 0) throw new InvalidOperationException("Task Scheduler inventář je prázdný.");

                TaskSchedulerService.ClearMemoryCacheForSmoke();
                // Cold single-task enabled lookup must not enumerate 266+ tasks.
                var directSw = System.Diagnostics.Stopwatch.StartNew();
                bool directState = await scheduler.GetEnabledAsync(tasks[0].Path, cts.Token);
                directSw.Stop();
                if (directState != tasks[0].Enabled)
                    throw new InvalidOperationException("Single-task COM state differs from fresh inventory.");
                bool nextState = await scheduler.GetEnabledAsync(tasks[0].Path, cts.Token);
                bool burstState = await scheduler.GetEnabledAsync(tasks[0].Path, cts.Token);
                if (nextState != tasks[0].Enabled || burstState != tasks[0].Enabled)
                    throw new InvalidOperationException("Burst-enabled state mismatch.");
                var cacheSw = System.Diagnostics.Stopwatch.StartNew();
                var cached = await scheduler.GetAsync(false, cts.Token);
                cacheSw.Stop();

                if (cached.Count != tasks.Count)
                    throw new InvalidOperationException($"Task Scheduler persistent cache count mismatch fresh={tasks.Count} cached={cached.Count}.");
                if (cacheSw.ElapsedMilliseconds > 150)
                    throw new InvalidOperationException($"Task Scheduler persistent cache is too slow: {cacheSw.ElapsedMilliseconds} ms.");
                if (!TaskSchedulerService.NeedsBackgroundRefresh)
                    throw new InvalidOperationException("Task Scheduler persistent snapshot did not request a background refresh.");

                int scansBeforeRefresh = TaskSchedulerService.ScanCountForDiagnostics;
                var refreshA = scheduler.GetAsync(true, cts.Token);
                var refreshB = scheduler.GetAsync(true, cts.Token);
                var refreshedPair = await Task.WhenAll(refreshA, refreshB);
                int refreshScans = TaskSchedulerService.ScanCountForDiagnostics - scansBeforeRefresh;
                if (refreshScans != 1)
                    throw new InvalidOperationException($"Task Scheduler force-refresh single-flight failed: scans={refreshScans}.");
                if (refreshedPair[0].Count != tasks.Count || refreshedPair[1].Count != tasks.Count)
                    throw new InvalidOperationException("Task Scheduler background refresh count mismatch.");
                if (TaskSchedulerService.NeedsBackgroundRefresh)
                    throw new InvalidOperationException("Task Scheduler background refresh left refreshPending set.");

                LogService.Info($"TASK_SCHEDULER_BACKEND_SMOKE_OK tasks={tasks.Count} freshMs={freshSw.ElapsedMilliseconds} directEnabledMs={directSw.ElapsedMilliseconds} persistentMs={cacheSw.ElapsedMilliseconds} refreshScans={refreshScans}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("TASK_SCHEDULER_BACKEND_SMOKE_FAIL", ex); Environment.Exit(46); }
            return true;
        }
        if (HasArg(cli, "--worker-proxy-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
                var drivers = await DriverInventoryService.GetAsync(true, cts.Token);
                var apps = await InstalledAppService.GetInstalledAsync(cts.Token, true, false);
                var services = await new ServiceCatalogService().GetAsync(true, cts.Token);
                if (drivers.Count == 0 || apps.Count == 0 || services.Count == 0) throw new InvalidOperationException("Worker proxy returned an empty inventory.");
                LogService.Info($"WORKER_PROXY_SMOKE_OK drivers={drivers.Count} apps={apps.Count} services={services.Count}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("WORKER_PROXY_SMOKE_FAIL", ex); Environment.Exit(47); }
            return true;
        }
        if (HasArg(cli, "--worker-stream-smoke"))
        {
            try
            {
                var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long firstMs = -1;
                var ack = await WorkerClientService.RunStreamingAsync<WorkerAck>("stream-smoke", null, TimeSpan.FromSeconds(15), System.Threading.CancellationToken.None,
                    line => { System.Threading.Interlocked.CompareExchange(ref firstMs, sw.ElapsedMilliseconds, -1); lines.Enqueue("O:" + line); },
                    line => { System.Threading.Interlocked.CompareExchange(ref firstMs, sw.ElapsedMilliseconds, -1); lines.Enqueue("E:" + line); });
                sw.Stop();
                string joined = string.Join("|", lines);
                if (!ack.Ok || firstMs < 0 || firstMs >= sw.ElapsedMilliseconds || !joined.Contains("O:STREAM_OUT_1") || !joined.Contains("E:STREAM_ERR_1") || !joined.Contains("O:STREAM_OUT_2"))
                    throw new InvalidOperationException($"stream invalid first={firstMs} total={sw.ElapsedMilliseconds} lines={joined}");
                LogService.Info($"WORKER_STREAM_SMOKE_OK firstMs={firstMs} totalMs={sw.ElapsedMilliseconds} lines={joined}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("WORKER_STREAM_SMOKE_FAIL", ex); Environment.Exit(55); }
            return true;
        }

        return false;
    }
}
