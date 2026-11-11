using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    public async Task RunPerformanceSmokeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = cts.Token;
        var totalSw = Stopwatch.StartNew();
        await ShowSectionAsync("overview");
        await Task.Delay(250, token);

        // Warm the full navigation wrapper/state-machine completion path on an unmeasured
        // lightweight section so tiered-JIT promotion is not charged to a measured cold page.
        for (int i = 0; i < 2; i++)
        {
            await ShowSectionAsync("activity");
            await ShowSectionAsync("overview");
        }

        CollectForMeasurement();

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long baselineWs = process.WorkingSet64;
        long baselinePrivate = process.PrivateMemorySize64;
        long baselineManaged = GC.GetTotalMemory(false);
        int baselineHandles = process.HandleCount;
        long peakWs = baselineWs;
        TimeSpan baselineCpu = process.TotalProcessorTime;
        long baselineAllocated = GC.GetTotalAllocatedBytes(precise: false);

        // Record memory after every section, not only the first network page.
        // Sampling is outside the timed navigation section. This is a sampled
        // peak; a shorter transient spike still needs an external profiler.
        async Task<long> MeasureTrackedSectionAsync(string key)
        {
            long ms = await MeasureSectionAsync(key);
            process.Refresh();
            peakWs = Math.Max(peakWs, process.WorkingSet64);
            return ms;
        }

        LogService.Info("performance", "PERF_SMOKE_PHASE cold-network-start");
        long coldNetwork = await MeasureTrackedSectionAsync("network");
        LogService.Info("performance", $"PERF_SMOKE_PHASE cold-network-done ms={coldNetwork}");
        long coldStorage = await MeasureTrackedSectionAsync("storage");
        LogService.Info("performance", $"PERF_SMOKE_PHASE cold-storage-done ms={coldStorage}");
        long coldGpu = await MeasureTrackedSectionAsync("gpu");
        LogService.Info("performance", $"PERF_SMOKE_PHASE cold-gpu-done ms={coldGpu}");
        long coldStartup = await MeasureTrackedSectionAsync("startup");
        LogService.Info("performance", $"PERF_SMOKE_PHASE cold-startup-done ms={coldStartup}");
        _sub = "Služby";
        long coldServices = await MeasureTrackedSectionAsync("components");
        LogService.Info("performance", $"PERF_SMOKE_PHASE cold-services-done ms={coldServices}");
        _sub = null;
        long coldDefender = await MeasureTrackedSectionAsync("defender");
        LogService.Info("performance", $"PERF_SMOKE_PHASE cold-defender-done ms={coldDefender}");
        long coldDebloat = await MeasureTrackedSectionAsync("debloat");
        LogService.Info("performance", $"PERF_SMOKE_PHASE cold-debloat-done ms={coldDebloat}");
        long coldPower = await MeasureTrackedSectionAsync("power");
        LogService.Info("performance", $"PERF_SMOKE_PHASE cold-power-done ms={coldPower}");

        long warmNetwork = await MeasureTrackedSectionAsync("network");
        LogService.Info("performance", $"PERF_SMOKE_PHASE warm-network-done ms={warmNetwork}");
        long warmStorage = await MeasureTrackedSectionAsync("storage");
        LogService.Info("performance", $"PERF_SMOKE_PHASE warm-storage-done ms={warmStorage}");
        long warmGpu = await MeasureTrackedSectionAsync("gpu");
        LogService.Info("performance", $"PERF_SMOKE_PHASE warm-gpu-done ms={warmGpu}");
        long warmStartup = await MeasureTrackedSectionAsync("startup");
        LogService.Info("performance", $"PERF_SMOKE_PHASE warm-startup-done ms={warmStartup}");
        _sub = "Služby";
        long warmServices = await MeasureTrackedSectionAsync("components");
        LogService.Info("performance", $"PERF_SMOKE_PHASE warm-services-done ms={warmServices}");
        _sub = null;
        long warmDefender = await MeasureTrackedSectionAsync("defender");
        LogService.Info("performance", $"PERF_SMOKE_PHASE warm-defender-done ms={warmDefender}");
        long warmDebloat = await MeasureTrackedSectionAsync("debloat");
        LogService.Info("performance", $"PERF_SMOKE_PHASE warm-debloat-done ms={warmDebloat}");
        long warmPower = await MeasureTrackedSectionAsync("power");
        LogService.Info("performance", $"PERF_SMOKE_PHASE warm-power-done ms={warmPower}");

        long overviewReturnMs = await MeasureTrackedSectionAsync("overview");
        MemoryPressureService.TrimHeavyCaches();
        await Task.Delay(200, token);
        CollectForMeasurement();

        process.Refresh();
        long retainedWs = process.WorkingSet64;
        long retainedPrivate = process.PrivateMemorySize64;
        long retainedManaged = GC.GetTotalMemory(false);
        int retainedHandles = process.HandleCount;
        long wsDelta = retainedWs - baselineWs;
        long privateDelta = retainedPrivate - baselinePrivate;
        long managedDelta = retainedManaged - baselineManaged;
        int handleDelta = retainedHandles - baselineHandles;
        double processCpuMs = (process.TotalProcessorTime - baselineCpu).TotalMilliseconds;
        long processAllocatedKb = Math.Max(0L, GC.GetTotalAllocatedBytes(precise: false) - baselineAllocated) / 1024;

        totalSw.Stop();
        var current = new PerformanceBaseline(AppVersionForDiagnostics(), DateTimeOffset.UtcNow, Mb(baselineWs), Mb(peakWs), Mb(retainedWs), Mb(wsDelta), totalSw.ElapsedMilliseconds);
        var comparison = PerformanceBaselineService.CompareAndSave(current);

        LogService.Info("performance",
            $"PERF_COLD networkMs={coldNetwork} storageMs={coldStorage} gpuMs={coldGpu} startupMs={coldStartup} servicesMs={coldServices} defenderMs={coldDefender} debloatMs={coldDebloat} powerMs={coldPower}");
        LogService.Info("performance",
            $"PERF_WARM networkMs={warmNetwork} storageMs={warmStorage} gpuMs={warmGpu} startupMs={warmStartup} servicesMs={warmServices} defenderMs={warmDefender} debloatMs={warmDebloat} powerMs={warmPower} overviewMs={overviewReturnMs}");
        LogService.Info("performance",
            $"PERF_MEMORY baselineWsMB={Mb(baselineWs):0.0} retainedWsMB={Mb(retainedWs):0.0} wsDeltaMB={Mb(wsDelta):0.0} baselinePrivateMB={Mb(baselinePrivate):0.0} retainedPrivateMB={Mb(retainedPrivate):0.0} privateDeltaMB={Mb(privateDelta):0.0} managedDeltaMB={Mb(managedDelta):0.0} handleDelta={handleDelta}");
        LogService.Info("performance",
            $"PERF_SMOKE baselineMB={Mb(baselineWs):0.0} peakMB={Mb(peakWs):0.0} retainedMB={Mb(retainedWs):0.0} deltaMB={Mb(wsDelta):0.0} peakChange={comparison.PeakChangePercent:0.0}% retainedChange={comparison.RetainedChangePercent:0.0}% cpuMs={processCpuMs:0.00} allocatedKB={processAllocatedKb} peakType=section-sampled");

        foreach (var item in new (string Name, long Ms)[]
        {
            ("network", coldNetwork), ("storage", coldStorage), ("gpu", coldGpu), ("startup", coldStartup),
            ("services", coldServices), ("defender", coldDefender), ("debloat", coldDebloat), ("power", coldPower)
        })
        {
            if (item.Ms > 30)
                throw new InvalidOperationException($"Performance smoke: cold {item.Name} {item.Ms} ms exceeded 30 ms UI budget.");
        }

        foreach (var item in new (string Name, long Ms)[]
        {
            ("network", warmNetwork), ("storage", warmStorage), ("gpu", warmGpu), ("startup", warmStartup),
            ("services", warmServices), ("defender", warmDefender), ("debloat", warmDebloat), ("power", warmPower)
        })
        {
            if (item.Ms > 10)
                LogService.Info("performance", $"PERF_BUDGET_WARN warm {item.Name}={item.Ms}ms budget=10ms");
        }

        if (comparison.Regression)
            LogService.Info("performance", "PERF_BASELINE_TREND previous baseline differs materially; current-run leak guards decide pass/fail.");

        if (overviewReturnMs > 20)
            throw new InvalidOperationException($"Performance smoke: Overview return {overviewReturnMs} ms exceeded 20 ms fast-path budget.");
        if (peakWs > 700L * 1024 * 1024)
            throw new InvalidOperationException($"Performance smoke: peak working set {Mb(peakWs):0} MB je příliš vysoký.");
        // First visits intentionally populate persistent WinUI/page caches, so retained
        // private bytes/handles here are high-water telemetry, not a leak verdict.
        // Linear-growth leak enforcement belongs to the repeated navigation soak audit.
        if (privateDelta > 180L * 1024 * 1024)
            throw new InvalidOperationException($"Performance smoke: po prvním cold passu zůstalo extrémních +{Mb(privateDelta):0} MB private bytes.");
    }

    internal async Task RunTraceWorkloadAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (string key in new[] { "overview", "network", "storage", "gpu", "startup", "components", "defender", "debloat", "power" })
        {
            if (key == "components") _sub = "Služby";
            await ShowSectionAsync(key);
            await Task.Delay(40, cts.Token);
        }
        await ShowSectionAsync("overview");
        await Task.Delay(250, cts.Token);
        LogService.Info("performance", "PERF_TRACE_WORKLOAD_OK");
    }

    public async Task RunIdleCpuAuditAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = cts.Token;
        var process = Process.GetCurrentProcess();

        async Task<double> MeasureCpuAsync(int milliseconds)
        {
            process.Refresh();
            double start = process.TotalProcessorTime.TotalMilliseconds;
            await Task.Delay(milliseconds, token);
            process.Refresh();
            return process.TotalProcessorTime.TotalMilliseconds - start;
        }

        await ShowSectionAsync("overview");
        await Task.Delay(3500, token);

        // Separate the one-time PDH cold start from recurring idle CPU cost.
        _liveMetricsDiagnosticSuppressSlow = true;
        var coldGpuSw = Stopwatch.StartNew();
        _ = ReadGpuUsage();
        coldGpuSw.Stop();
        long coldGpuInitMs = coldGpuSw.ElapsedMilliseconds;
        await Task.Delay(500, token);

        // Let post-startup background jobs (hardware monitor/build revalidation) finish
        // before A/B windows, otherwise the first window compares startup work with idle.
        await Task.Delay(4500, token);

        _liveMetricsDiagnosticSuppressSlow = false;
        _liveMetricsDiagnosticSuppressUi = false;
        double fullMs = await MeasureCpuAsync(10000);

        _liveMetricsDiagnosticSuppressSlow = true;
        await Task.Delay(500, token);
        double noSlowMs = await MeasureCpuAsync(10000);

        _liveMetricsDiagnosticSuppressUi = true;
        await Task.Delay(500, token);
        double samplingOnlyMs = await MeasureCpuAsync(10000);

        if (_liveMetricsCts != null)
            await _liveMetricsCts.CancelAsync();
        await Task.Delay(500, token);
        double pausedMs = await MeasureCpuAsync(10000);

        await ShowSectionAsync("settings");
        await Task.Delay(500, token);
        double staticPagePausedMs = await MeasureCpuAsync(10000);

        _liveMetricsDiagnosticSuppressSlow = false;
        _liveMetricsDiagnosticSuppressUi = false;

        LogService.Info("performance",
            $"IDLE_CPU_AB_AUDIT coldGpuInitMs={coldGpuInitMs} full10sMs={fullMs:F1} noSlow10sMs={noSlowMs:F1} samplingOnly10sMs={samplingOnlyMs:F1} paused10sMs={pausedMs:F1} staticPagePaused10sMs={staticPagePausedMs:F1} slowCostMs={(fullMs - noSlowMs):F1} uiCostMs={(noSlowMs - samplingOnlyMs):F1} samplingCostMs={(samplingOnlyMs - pausedMs):F1} overviewVisualCostMs={(pausedMs - staticPagePausedMs):F1}");

        // Runtime footprint regression: after a complete active telemetry cycle,
        // a valid native NVAPI reading must not silently load the much larger
        // NVML library. This runs ONLY in --idle-cpu-smoke, never in normal UI.
        double? nativeLoad = GpuNvapiUsageService.TryRead();
        double? nativeTemp = GpuNvapiUsageService.TryReadTemperature();
        process.Refresh();
        bool nvmlLoaded = false;
        int moduleCount = 0;
        foreach (ProcessModule module in process.Modules)
        {
            moduleCount++;
            if (module.ModuleName.Equals("nvml.dll", StringComparison.OrdinalIgnoreCase))
                nvmlLoaded = true;
        }
        LogService.Info("performance",
            $"GPU_NVAPI_RUNTIME_FOOTPRINT load={nativeLoad?.ToString("F1") ?? "NA"} " +
            $"tempC={nativeTemp?.ToString("F1") ?? "NA"} nvmlLoaded={nvmlLoaded} " +
            $"moduleCount={moduleCount} privateMiB={process.PrivateMemorySize64 / 1048576d:F2}");
        // A different GPU can correctly fall back to NVML. Enforce this
        // only if BOTH NVIDIA native readings are supported and valid.
        if (nativeLoad.HasValue && nativeTemp.HasValue && nvmlLoaded)
            throw new InvalidOperationException(
                "NVAPI readings succeeded but costly NVML unexpectedly loaded during idle.");
        // The CLI smoke terminates the process immediately after this method
        // returns; allow the existing asynchronous log writer to flush evidence.
        await Task.Delay(600, token);
    }

    private async Task<long> MeasureSectionAsync(string key)
    {
        // Let low-priority work from the previous section drain before timing the next
        // section. Rapid-navigation contention is covered separately by the navigation
        // soak; this metric is intentionally the isolated foreground render budget.
        await Task.Delay(20);
        var sw = Stopwatch.StartNew();
        await ShowSectionAsync(key);
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static void CollectForMeasurement()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static double Mb(long bytes) => bytes / 1048576d;
    private static string AppVersionForDiagnostics() => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
