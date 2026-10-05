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
        CollectForMeasurement();

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long baselineWs = process.WorkingSet64;
        long baselinePrivate = process.PrivateMemorySize64;
        long baselineManaged = GC.GetTotalMemory(false);
        int baselineHandles = process.HandleCount;
        long peakWs = baselineWs;

        long coldNetwork = await MeasureSectionAsync("network");
        peakWs = Math.Max(peakWs, Process.GetCurrentProcess().WorkingSet64);
        long coldStorage = await MeasureSectionAsync("storage");
        long coldGpu = await MeasureSectionAsync("gpu");
        long coldStartup = await MeasureSectionAsync("startup");
        _sub = "Služby";
        long coldServices = await MeasureSectionAsync("components");
        long coldDefender = await MeasureSectionAsync("defender");
        long coldDebloat = await MeasureSectionAsync("debloat");
        long coldPower = await MeasureSectionAsync("power");

        long warmNetwork = await MeasureSectionAsync("network");
        long warmStorage = await MeasureSectionAsync("storage");
        long warmGpu = await MeasureSectionAsync("gpu");
        long warmStartup = await MeasureSectionAsync("startup");
        _sub = "Služby";
        long warmServices = await MeasureSectionAsync("components");
        long warmDefender = await MeasureSectionAsync("defender");
        long warmDebloat = await MeasureSectionAsync("debloat");
        long warmPower = await MeasureSectionAsync("power");

        long overviewReturnMs = await MeasureSectionAsync("overview");
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
            $"PERF_SMOKE baselineMB={Mb(baselineWs):0.0} peakMB={Mb(peakWs):0.0} retainedMB={Mb(retainedWs):0.0} deltaMB={Mb(wsDelta):0.0} peakChange={comparison.PeakChangePercent:0.0}% retainedChange={comparison.RetainedChangePercent:0.0}%");

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
    }

    private async Task<long> MeasureSectionAsync(string key)
    {
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
