using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    public async Task RunRegressionSmokeTestAsync(int repeatedNavigationPasses = 3)
    {
        if (repeatedNavigationPasses is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(repeatedNavigationPasses));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = cts.Token;
        LogService.Info("regression", "REGRESSION start");
        ValidateDialogLayoutSmoke();

        for (int pass = 1; pass <= repeatedNavigationPasses; pass++)
        {
            foreach (string key in CatalogService.Sections.Select(x => x.Key))
            {
                token.ThrowIfCancellationRequested();
                if (_subOptions.TryGetValue(key, out var subs))
                {
                    foreach (string sub in subs)
                    {
                        _sub = sub;
                        await ShowSectionAsync(key);
                        EnsureNavigationHealthy(key + "/" + sub);
                        await Task.Yield();
                    }
                }
                else
                {
                    await ShowSectionAsync(key);
                    EnsureNavigationHealthy(key);
                    await Task.Yield();
                }
            }
            // One short settle per complete pass is enough to drain dispatcher work;
            // avoid paying a timer quantum after every individual navigation.
            await Task.Delay(75, token);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            PerformanceTelemetryService.SnapshotMemory("regression-pass-" + pass);
        }
        var services = await new ServiceCatalogService().GetAsync();
        if (services.Count == 0) throw new InvalidOperationException("Regression: seznam služeb je prázdný.");
        LogService.Info("regression", $"Services={services.Count}");

        var tasks = await new TaskSchedulerService().GetBasicAsync();
        if (tasks.Count == 0) throw new InvalidOperationException("Regression: Task Scheduler inventář je prázdný.");
        LogService.Info("regression", $"Tasks={tasks.Count}");

        var gpu = await GpuDriverService.DetectPrimaryGpuAsync(token);
        if (gpu == null) throw new InvalidOperationException("Regression: GPU nebyla detekována.");
        LogService.Info("regression", $"GPU={gpu.Name} driver={gpu.DisplayVersion}");

        LogService.Info("regression", "CHECKPOINT power-before");
        var power = await ProcessRunner.RunAsync("powercfg.exe", "/getactivescheme", TimeSpan.FromSeconds(10), token);
        LogService.Info("regression", "CHECKPOINT power-after");
        if (!power.Success || string.IsNullOrWhiteSpace(power.StdOut))
            throw new InvalidOperationException("Regression: active power plan nelze načíst.");

        await PowerPlanStateService.ReadAsync(token);
        InvalidatePowerPageCache();
        await ShowSectionAsync("power");
        int powerGeneration = _navigationGeneration;
        object? powerRoot = ContentStack.Children.Count > 0 ? ContentStack.Children[0] : null;
        for (int i = 0; i < 100; i++)
            await ShowSectionAsync("power");
        if (_navigationGeneration != powerGeneration || ContentStack.Children.Count == 0 ||
            !ReferenceEquals(ContentStack.Children[0], powerRoot))
            throw new InvalidOperationException("Regression: fresh Power page did not use same-section fast-path.");
        LogService.Info("perf", "POWER_SAME_SECTION_FASTPATH_SMOKE_OK calls=100");

        LogService.Info("regression", "CHECKPOINT nic-before");
        string nic = await PerformanceProfilesService.GetNicPerformanceSummaryAsync(token);
        LogService.Info("regression", "CHECKPOINT nic-after");
        if (string.IsNullOrWhiteSpace(nic)) throw new InvalidOperationException("Regression: NIC summary je prázdné.");

        LogService.Info("regression", "CHECKPOINT exo-before");
        await RunExostenzaInstallPopupAsync(true);
        LogService.Info("regression", "CHECKPOINT exo-after");
        await Task.Delay(60, token);
        LogService.Info("regression", "CHECKPOINT ai-before");
        await RunAiProgressUiSmokeAsync(token);
        LogService.Info("regression", "CHECKPOINT ai-after");
        await Task.Delay(60, token);
        LogService.Info("regression", "CHECKPOINT debloat-before");
        await RunDebloatProgressUiSmokeAsync(token);
        LogService.Info("regression", "CHECKPOINT debloat-after");
        await Task.Delay(60, token);
        PerformanceTelemetryService.SnapshotMemory("regression-final");
        LogService.Info("regression", $"REGRESSION_SMOKE_OK navigationPasses={repeatedNavigationPasses}");
    }

    public async Task RunLeakAndCancellationAuditAsync()
    {
        var baseline = PerformanceTelemetryService.GetSnapshot();
        for (int pass = 0; pass < 4; pass++)
        {
            var a = ShowSectionAsync("network");
            await Task.Delay(8);
            var b = ShowSectionAsync("storage");
            await Task.Delay(8);
            var c = ShowSectionAsync("gpu");
            await Task.WhenAll(a, b, c);
            await ShowSectionAsync("settings");
            EnsureNavigationHealthy("settings");
            await Task.Delay(60);
        }
        await Task.Delay(300);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var final = PerformanceTelemetryService.GetSnapshot();
        long wsDelta = final.WorkingSetMb - baseline.WorkingSetMb;
        long privateDelta = final.PrivateMb - baseline.PrivateMb;
        long managedDelta = final.ManagedMb - baseline.ManagedMb;
        int handleDelta = final.Handles - baseline.Handles;
        LogService.Info("perf", $"LEAK_AUDIT wsDelta={wsDelta}MB privateDelta={privateDelta}MB managedDelta={managedDelta}MB handleDelta={handleDelta}");
        if (wsDelta > 96 || managedDelta > 48 || handleDelta > 250)
            throw new InvalidOperationException($"Leak audit exceeded threshold: ws={wsDelta}MB managed={managedDelta}MB handles={handleDelta}");
    }

    public async Task RunSoakAuditAsync(bool settleStartup = false)
    {
        if (settleStartup)
        {
            await Task.Delay(6500);
            LogService.Info("perf", "SOAK_SETTLE_OK startupDeferredWorkWindowMs=6500");
        }

        string[] sections = { "network", "storage", "gpu", "power", "settings" };
        // Warm WinUI/Composition/native caches before the retention baseline.
        var cold = PerformanceTelemetryService.GetSnapshot();
        for (int warmCycle = 0; warmCycle < 3; warmCycle++)
        {
            foreach (string section in sections)
            {
                await ShowSectionAsync(section);
                await Task.Yield();
            }
        }
        await Task.Delay(450);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var baseline = PerformanceTelemetryService.GetSnapshot();
        LogService.Info("perf", $"SOAK_WARMUP wsDelta={baseline.WorkingSetMb - cold.WorkingSetMb}MB privateDelta={baseline.PrivateMb - cold.PrivateMb}MB managedDelta={baseline.ManagedMb - cold.ManagedMb}MB handleDelta={baseline.Handles - cold.Handles}");
        int navigations = 0;
        var sectionHandleDelta = sections.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        var sectionPrivateDelta = sections.ToDictionary(x => x, _ => 0L, StringComparer.OrdinalIgnoreCase);
        var sectionTotalMs = sections.ToDictionary(x => x, _ => 0.0, StringComparer.OrdinalIgnoreCase);
        var sectionMaxMs = sections.ToDictionary(x => x, _ => 0.0, StringComparer.OrdinalIgnoreCase);
        for (int cycle = 0; cycle < 20; cycle++)
        {
            foreach (string section in sections)
            {
                var beforeSection = PerformanceTelemetryService.GetSnapshot();
                long navigationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                await ShowSectionAsync(section);
                double navigationMs = System.Diagnostics.Stopwatch.GetElapsedTime(navigationStarted).TotalMilliseconds;
                sectionTotalMs[section] += navigationMs;
                sectionMaxMs[section] = Math.Max(sectionMaxMs[section], navigationMs);
                EnsureNavigationHealthy(section);
                navigations++;
                await Task.Yield();
                var afterSection = PerformanceTelemetryService.GetSnapshot();
                sectionHandleDelta[section] += afterSection.Handles - beforeSection.Handles;
                sectionPrivateDelta[section] += afterSection.PrivateMb - beforeSection.PrivateMb;
                if (cycle == 19)
                {
                    LogService.Info("perf", $"SOAK_SECTION section={section} privateDelta={afterSection.PrivateMb - beforeSection.PrivateMb}MB wsDelta={afterSection.WorkingSetMb - beforeSection.WorkingSetMb}MB managedDelta={afterSection.ManagedMb - beforeSection.ManagedMb}MB handleDelta={afterSection.Handles - beforeSection.Handles} cumulativeHandleDelta={sectionHandleDelta[section]} cumulativePrivateDelta={sectionPrivateDelta[section]}MB");
                }
            }
            if (cycle is 0 or 4 or 9 or 14 or 19)
            {
                var checkpoint = PerformanceTelemetryService.GetSnapshot();
                LogService.Info("perf", $"SOAK_CHECK cycle={cycle + 1} ws={checkpoint.WorkingSetMb}MB private={checkpoint.PrivateMb}MB managed={checkpoint.ManagedMb}MB handles={checkpoint.Handles}");
            }
            // Give deferred work a wall-clock opportunity once per cycle rather than after all 5 navigations.
            await Task.Delay(5);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var final = PerformanceTelemetryService.GetSnapshot();
        long ws = final.WorkingSetMb - baseline.WorkingSetMb;
        long privateMb = final.PrivateMb - baseline.PrivateMb;
        long managed = final.ManagedMb - baseline.ManagedMb;
        int handles = final.Handles - baseline.Handles;
        foreach (string section in sections)
            LogService.Info("perf", $"SOAK_NAV_TIMING section={section} runs=20 avgMs={sectionTotalMs[section] / 20.0:F3} maxMs={sectionMaxMs[section]:F3}");
        LogService.Info("perf", $"SOAK_AUDIT navigations={navigations} wsDelta={ws}MB privateDelta={privateMb}MB managedDelta={managed}MB handleDelta={handles}");
        if (ws > 128 || managed > 64 || handles > 300)
            throw new InvalidOperationException($"Soak audit exceeded threshold: nav={navigations} ws={ws} managed={managed} handles={handles}");
    }
    public async Task RunDirtyRefreshCoalescingSmokeAsync()
    {
        Interlocked.Exchange(ref _dirtyRefreshQueued, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
            QueueDirtyStateRefresh();
        sw.Stop();

        if (Volatile.Read(ref _dirtyRefreshQueued) != 1)
            throw new InvalidOperationException("Dirty refresh burst was not coalesced into one queued refresh.");

        await Task.Yield();
        if (Volatile.Read(ref _dirtyRefreshQueued) != 0)
        {
            await Task.Delay(10);
            if (Volatile.Read(ref _dirtyRefreshQueued) != 0)
                throw new InvalidOperationException("Queued dirty refresh did not drain.");
        }

        LogService.Info("perf", $"DIRTY_REFRESH_COALESCE_SMOKE_OK calls=1000 enqueueMs={sw.Elapsed.TotalMilliseconds:F3}");
    }

    public async Task RunUserNavigationNoOpSmokeAsync()
    {
        await ShowSectionAsync("system");
        int mainGeneration = _navigationGeneration;
        // The System page now attaches its first visual root in the existing
        // Low-priority deferred build. Capturing Children[0] before that turn
        // compares a valid empty shell with the later first root and falsely
        // reports a rebuild caused by the active-navigation no-op.
        // Wait for an actual cached and attached root, then verify it remains
        // reference-stable for all 1000 redundant UI clicks.
        for (int i = 0; i < 80 && (_systemPageCache == null ||
             _systemVirtualHost == null || !ContentStack.Children.Contains(_systemVirtualHost)); i++)
            await Task.Delay(25);
        if (_systemPageCache == null || _systemVirtualHost == null ||
            !ContentStack.Children.Contains(_systemVirtualHost))
            throw new InvalidOperationException("System page did not finish attaching its deferred root before active-navigation test.");
        if (_navigationGeneration != mainGeneration)
            throw new InvalidOperationException("System deferred-root materialization changed navigation generation.");
        object? mainRoot = ContentStack.Children.Count > 0 ? ContentStack.Children[0] : null;

        var mainNoOp = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
            await NavigateMainSectionFromUiAsync("system");
        mainNoOp.Stop();

        if (_navigationGeneration != mainGeneration)
            throw new InvalidOperationException("Active main navigation triggered a redundant navigation generation.");
        if (ContentStack.Children.Count == 0 || !ReferenceEquals(ContentStack.Children[0], mainRoot))
            throw new InvalidOperationException("Active main navigation rebuilt the current visual tree.");

        LogService.Info("perf", $"USER_NAV_NOOP_SMOKE_OK main1000Ms={mainNoOp.Elapsed.TotalMilliseconds:F3}");
    }

    public async Task RunNavigation500SoakAuditAsync()
    {
        async Task WaitForHandlePlateauAsync(string phase)
        {
            int last = -1;
            int stableSamples = 0;
            int samples = 0;
            for (; samples < 50; samples++)
            {
                await Task.Delay(100);
                int current = PerformanceTelemetryService.GetSnapshot().Handles;
                if (last >= 0 && Math.Abs(current - last) <= 2)
                    stableSamples++;
                else
                    stableSamples = 0;
                last = current;
                if (stableSamples >= 4)
                    break;
            }
            LogService.Info("perf", $"NAV500_HANDLE_PLATEAU phase={phase} handles={last} samples={samples + 1} stable={stableSamples}");
        }

        string[] sections = { "debloat", "system", "privacy", "ui", "update", "components", "spicetify", "gpu" };
        foreach (string section in sections)
        {
            _sub = null;
            await ShowSectionAsync(section);
            EnsureNavigationHealthy(section);
            await Task.Delay(15);
        }
        // Normalize the retention baseline to the same fully-materialized System page
        // used at the end of the soak. Otherwise deferred System groups created after
        // the final navigation look like leaked native handles even though they are
        // simply the active page's expected WinUI/composition resources.
        _sub = null;
        await ShowSectionAsync("system");
        EnsureNavigationHealthy("system");
        for (int i = 0; i < 80 && GetDeferredVirtualGroupCount("system") > 0; i++)
            await Task.Delay(25);
        await WaitForHandlePlateauAsync("baseline");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await WaitForHandlePlateauAsync("baseline-post-gc");
        var baseline = PerformanceTelemetryService.GetSnapshot();
        var navigationTimings = sections.ToDictionary(x => x, _ => new System.Collections.Generic.List<double>(50), StringComparer.OrdinalIgnoreCase);
        var navigationHandleDelta = sections.ToDictionary(x => x, _ => 0L, StringComparer.OrdinalIgnoreCase);
        int navigations = 0;
        for (int cycle = 0; cycle < 50; cycle++)
        {
            foreach (string section in sections)
            {
                _sub = null;
                int handlesBefore = PerformanceTelemetryService.GetSnapshot().Handles;
                var navSw = System.Diagnostics.Stopwatch.StartNew();
                await ShowSectionAsync(section);
                navSw.Stop();
                navigationTimings[section].Add(navSw.Elapsed.TotalMilliseconds);
                EnsureNavigationHealthy(section);
                navigations++;
                // Yield once so deferred work scheduled by this transition can be attributed
                // to the target section in this diagnostics-only audit.
                await Task.Yield();
                int handlesAfter = PerformanceTelemetryService.GetSnapshot().Handles;
                navigationHandleDelta[section] += handlesAfter - handlesBefore;
            }
            if ((cycle + 1) % 10 == 0)
            {
                var checkpoint = PerformanceTelemetryService.GetSnapshot();
                LogService.Info("perf", $"NAV500_CHECK navigations={navigations} wsDelta={checkpoint.WorkingSetMb - baseline.WorkingSetMb}MB privateDelta={checkpoint.PrivateMb - baseline.PrivateMb}MB managedDelta={checkpoint.ManagedMb - baseline.ManagedMb}MB handleDelta={checkpoint.Handles - baseline.Handles}");
            }
        }
        foreach (var pair in navigationTimings)
        {
            double[] ordered = pair.Value.OrderBy(x => x).ToArray();
            if (ordered.Length == 0) continue;
            double avg = ordered.Average();
            double p95 = ordered[Math.Min(ordered.Length - 1, (int)Math.Floor((ordered.Length - 1) * 0.95))];
            LogService.Info("perf", $"NAV500_SECTION section={pair.Key} avgMs={avg:F2} p95Ms={p95:F2} maxMs={ordered[^1]:F2} n={ordered.Length} handleDeltaSum={navigationHandleDelta[pair.Key]}");
        }
        // Normalize the active page before the retention snapshot. The measured loop ends on GPU,
        // whose active WinUI/composition resources are not a navigation leak and would skew handle delta.
        _sub = null;
        await ShowSectionAsync("system");
        EnsureNavigationHealthy("system");
        var beforeFinalSettle = PerformanceTelemetryService.GetSnapshot();
        for (int i = 0; i < 80 && GetDeferredVirtualGroupCount("system") > 0; i++)
            await Task.Delay(25);
        await WaitForHandlePlateauAsync("final");
        var afterFinalSettle = PerformanceTelemetryService.GetSnapshot();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await WaitForHandlePlateauAsync("final-post-gc");
        var final = PerformanceTelemetryService.GetSnapshot();
        // Repeat the collection without additional navigation. An increase
        // confined to the first post-GC phase should not be misclassified as
        // a per-navigation resource leak.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await WaitForHandlePlateauAsync("final-second-gc");
        var secondGc = PerformanceTelemetryService.GetSnapshot();
        LogService.Info("perf", $"NAV500_GC_REPEAT firstDelta={final.Handles - baseline.Handles} secondDelta={secondGc.Handles - baseline.Handles} extraHandles={secondGc.Handles - final.Handles}");
        LogService.Info("perf", $"NAV500_RETENTION_PHASE beforeSettleHandles={beforeFinalSettle.Handles - baseline.Handles} afterSettleHandles={afterFinalSettle.Handles - baseline.Handles} afterGcHandles={final.Handles - baseline.Handles}");
        long ws = final.WorkingSetMb - baseline.WorkingSetMb;
        long privateMb = final.PrivateMb - baseline.PrivateMb;
        long managed = final.ManagedMb - baseline.ManagedMb;
        int handles = final.Handles - baseline.Handles;
        LogService.Info("perf", $"NAV500_AUDIT navigations={navigations} wsDelta={ws}MB privateDelta={privateMb}MB managedDelta={managed}MB handleDelta={handles}");
        if (ws > 96 || privateMb > 128 || managed > 48 || handles > 300)
            throw new InvalidOperationException($"500-navigation soak exceeded threshold: ws={ws}MB private={privateMb}MB managed={managed}MB handles={handles}");
        LogService.Info("perf", "NAV500_AUDIT_OK");
    }

    public async Task RunNavigationTailLatencySmokeAsync()
    {
        var overviewTimes = new System.Collections.Generic.List<double>(30);
        var powerTimes = new System.Collections.Generic.List<double>(30);

        for (int i = 0; i < 5; i++)
        {
            await ShowSectionAsync("overview");
            await ShowSectionAsync("power");
        }

        using var navigationProcess = System.Diagnostics.Process.GetCurrentProcess();
        long managedBefore = GC.GetTotalAllocatedBytes(precise: false);
        TimeSpan cpuBefore = navigationProcess.TotalProcessorTime;
        long privateBefore = navigationProcess.PrivateMemorySize64;
        long workingBefore = navigationProcess.WorkingSet64;
        int handlesBefore = navigationProcess.HandleCount;
        int threadsBefore = navigationProcess.Threads.Count;
        var navigationBatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 30; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ShowSectionAsync("overview");
            sw.Stop();
            overviewTimes.Add(sw.Elapsed.TotalMilliseconds);

            sw.Restart();
            await ShowSectionAsync("power");
            sw.Stop();
            powerTimes.Add(sw.Elapsed.TotalMilliseconds);
            await Task.Yield();
        }
        navigationBatch.Stop();
        navigationProcess.Refresh();
        long privateDelta = navigationProcess.PrivateMemorySize64 - privateBefore;
        long workingDelta = navigationProcess.WorkingSet64 - workingBefore;
        int handleDelta = navigationProcess.HandleCount - handlesBefore;
        int threadDelta = navigationProcess.Threads.Count - threadsBefore;
        LogService.Info("perf", $"NAV_PROCESS_DELTA_OK privateKiB={privateDelta / 1024d:F1} workingKiB={workingDelta / 1024d:F1} handlesDelta={handleDelta} threadsDelta={threadDelta}");
        long managedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - managedBefore);
        double processorMs = Math.Max(0, (navigationProcess.TotalProcessorTime - cpuBefore).TotalMilliseconds);
        LogService.Info("perf", $"NAV_RESOURCE_SMOKE_OK transitions=60 elapsedMs={navigationBatch.ElapsedMilliseconds} processCpuMs={processorMs:F1} managedAllocatedKiB={managedBytes / 1024d:F1} approxCpuMsPerTransition={processorMs / 60:F2} approxAllocatedKiBPerTransition={managedBytes / 61440d:F2}");

        static (double Avg, double P95, double Max) Stats(System.Collections.Generic.List<double> values)
        {
            double[] ordered = values.OrderBy(x => x).ToArray();
            return (ordered.Average(),
                ordered[Math.Min(ordered.Length - 1, (int)Math.Floor((ordered.Length - 1) * 0.95))],
                ordered[^1]);
        }

        // Navigation no longer re-resets all 21 persistent sidebar visuals on each click.
        // Check them after repeated real section changes, not only at initial construction.
        foreach (var button in _categoryButtons.Values)
        {
            if (!button.IsLoaded) continue;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(button);
            var scale = visual.Scale;
            if (Math.Abs(visual.Opacity - 1f) > 0.005f ||
                Math.Abs(scale.X - 1f) > 0.005f ||
                Math.Abs(scale.Y - 1f) > 0.005f)
                throw new InvalidOperationException("Sidebar composition motion state persisted after navigation.");
        }
        LogService.Info("perf", $"NAV_SIDEBAR_MOTION_STABLE_OK buttons={_categoryButtons.Count}");
        var ov = Stats(overviewTimes);
        var pw = Stats(powerTimes);
        LogService.Info("perf", $"NAV_TAIL_SMOKE_OK overviewAvgMs={ov.Avg:F2} overviewP95Ms={ov.P95:F2} overviewMaxMs={ov.Max:F2} powerAvgMs={pw.Avg:F2} powerP95Ms={pw.P95:F2} powerMaxMs={pw.Max:F2}");

        if (pw.P95 > 120 || pw.Max > 250)
            throw new InvalidOperationException($"Power navigation tail regression: p95={pw.P95}ms max={pw.Max}ms.");
    }



    public async Task RunSingleSectionSoakAuditAsync(string section)
    {
        for (int warm = 0; warm < 5; warm++)
        {
            await ShowSectionAsync(section);
            EnsureNavigationHealthy(section);
            await Task.Delay(40);
        }
        // Single-section soak must baseline after all delayed startup work (including
        // hardware monitor start and Windows build revalidation) has settled.
        await Task.Delay(6500);

        // Power's same-section no-op path has a one-time WinUI/native high-water warm-up.
        // Warm that path before the retention baseline so the audit measures repeated navigation,
        // not first-touch/JIT/native wrapper allocation.
        if (string.Equals(section, "power", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < 500; i++)
                await ShowSectionAsync(section);
            LogService.Info("perf", "POWER_FASTPATH_PREWARM_OK calls=500");
        }

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var baseline = PerformanceTelemetryService.GetSnapshot();
        int baselineNavigationGeneration = _navigationGeneration;
        if (string.Equals(section, "ui", StringComparison.OrdinalIgnoreCase))
        {
            long cacheAgeMs = _uiPageCacheUsedAt == default ? -1 : (long)(DateTime.UtcNow - _uiPageCacheUsedAt).TotalMilliseconds;
            LogService.Info("perf", $"SECTION_SOAK_UI_STATE children={ContentStack.Children.Count} pageCache={_uiPageCache?.Count ?? -1} rows={_uiVirtualRowsCache?.Length ?? -1} map={_uiVirtualMapCache?.Count ?? -1} cacheAgeMs={cacheAgeMs} navGen={baselineNavigationGeneration}");
        }
        LogService.Info("perf", $"SECTION_SOAK_BASELINE section={section} ws={baseline.WorkingSetMb}MB private={baseline.PrivateMb}MB managed={baseline.ManagedMb}MB handles={baseline.Handles}");

        for (int i = 0; i < 500; i++)
        {
            await ShowSectionAsync(section);
            EnsureNavigationHealthy(section);
            await Task.Delay(4);
            if ((i + 1) % 100 == 0)
            {
                var checkpoint = PerformanceTelemetryService.GetSnapshot();
                LogService.Info("perf", $"SECTION_SOAK_CHECK section={section} navigations={i + 1} wsDelta={checkpoint.WorkingSetMb - baseline.WorkingSetMb}MB privateDelta={checkpoint.PrivateMb - baseline.PrivateMb}MB managedDelta={checkpoint.ManagedMb - baseline.ManagedMb}MB handleDelta={checkpoint.Handles - baseline.Handles}");
            }
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var final = PerformanceTelemetryService.GetSnapshot();
        long ws = final.WorkingSetMb - baseline.WorkingSetMb;
        long privateMb = final.PrivateMb - baseline.PrivateMb;
        long managed = final.ManagedMb - baseline.ManagedMb;
        int handles = final.Handles - baseline.Handles;
        LogService.Info("perf", $"SECTION_SOAK section={section} navigations=500 wsDelta={ws}MB privateDelta={privateMb}MB managedDelta={managed}MB handleDelta={handles} navGenDelta={_navigationGeneration - baselineNavigationGeneration}");
        if (ws > 96 || privateMb > 128 || managed > 48 || handles > 500)
            throw new InvalidOperationException($"Section soak exceeded threshold: section={section} ws={ws}MB private={privateMb}MB managed={managed}MB handles={handles}");
    }

    public async Task RunUiCacheResidencyAuditAsync()
    {
        string[] preferred =
        {
            "debloat", "system", "privacy", "ui", "update", "components",
            "spicetify", "network", "storage", "gpu", "power", "settings",
            "startup", "restore", "fixer", "apps"
        };
        var available = CatalogService.Sections.Select(x => x.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] sections = preferred.Where(available.Contains).ToArray();

        await Task.Delay(6500);

        foreach (string section in sections)
        {
            _sub = null;
            await ShowSectionAsync(section);
            EnsureNavigationHealthy(section);
            await Task.Delay(35);
        }

        await Task.Delay(1000);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var populated = PerformanceTelemetryService.GetSnapshot();
        LogService.Info("perf", $"UI_CACHE_RESIDENCY_POPULATED sections={sections.Length} ws={populated.WorkingSetMb}MB private={populated.PrivateMb}MB managed={populated.ManagedMb}MB handles={populated.Handles}");

        TrimUiPageCachesForMemoryPressure();
        await Task.Delay(750);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var trimmed = PerformanceTelemetryService.GetSnapshot();
        LogService.Info("perf", $"UI_CACHE_RESIDENCY_TRIMMED ws={trimmed.WorkingSetMb}MB private={trimmed.PrivateMb}MB managed={trimmed.ManagedMb}MB handles={trimmed.Handles} freedWs={populated.WorkingSetMb - trimmed.WorkingSetMb}MB freedPrivate={populated.PrivateMb - trimmed.PrivateMb}MB freedManaged={populated.ManagedMb - trimmed.ManagedMb}MB freedHandles={populated.Handles - trimmed.Handles}");

        long totalMs = 0;
        long maxMs = 0;
        string slowest = string.Empty;
        foreach (string section in sections)
        {
            _sub = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ShowSectionAsync(section);
            sw.Stop();
            EnsureNavigationHealthy(section);
            totalMs += sw.ElapsedMilliseconds;
            if (sw.ElapsedMilliseconds > maxMs)
            {
                maxMs = sw.ElapsedMilliseconds;
                slowest = section;
            }
            await Task.Delay(15);
        }

        await Task.Delay(500);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var rebuilt = PerformanceTelemetryService.GetSnapshot();
        double avgMs = sections.Length == 0 ? 0 : totalMs / (double)sections.Length;
        LogService.Info("perf", $"UI_CACHE_RESIDENCY_REBUILD sections={sections.Length} avgMs={avgMs:F1} maxMs={maxMs} slowest={slowest} ws={rebuilt.WorkingSetMb}MB private={rebuilt.PrivateMb}MB reboundPrivate={rebuilt.PrivateMb - trimmed.PrivateMb}MB");
        LogService.Info("perf", "UI_CACHE_RESIDENCY_SMOKE_OK");
    }

    public async Task RunCriticalSectionLeakAuditAsync()
    {
        string[] sections = { "debloat", "system", "privacy", "ui", "update", "components", "spicetify" };
        var phase = System.Diagnostics.Stopwatch.StartNew();

        foreach (string section in sections)
        {
            _sub = null;
            await ShowSectionAsync(section);
            EnsureNavigationHealthy(section);
            await Task.Delay(25);
        }
        long initialWarmMs = phase.ElapsedMilliseconds;

        // Critical-leak smoke suppresses delayed build revalidation and all other
        // non-smoke startup background services. A short dispatcher settle is sufficient here.
        await Task.Delay(250);
        long startupSettleMs = phase.ElapsedMilliseconds - initialWarmMs;

        // Mixed cached-page attach/detach has a one-time WinUI/native high-water.
        // Warm the transition graph before taking the retention baseline.
        const int prewarmCycles = 15;
        for (int cycle = 0; cycle < prewarmCycles; cycle++)
        {
            foreach (string section in sections)
            {
                _sub = null;
                await ShowSectionAsync(section);
                EnsureNavigationHealthy(section);
                await Task.Delay(4);
            }
        }
        long prewarmNavMs = phase.ElapsedMilliseconds - initialWarmMs - startupSettleMs;
        await Task.Delay(500);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long baselineSettleGcMs = phase.ElapsedMilliseconds - initialWarmMs - startupSettleMs - prewarmNavMs;
        LogService.Info("perf", $"CRITICAL_MIXED_PREWARM_OK cycles={prewarmCycles} navigations={prewarmCycles * sections.Length}");

        // Sample handle count after the warm baseline and after each navigation cycle.
        // This distinguishes sustained retention from a transient final-cycle spike.
        var baseline = PerformanceTelemetryService.GetSnapshot();
        using var measuredProcess = System.Diagnostics.Process.GetCurrentProcess();
        int peakHandles = baseline.Handles;
        int peakHandleCycle = 0;
        int lastCycleHandles = baseline.Handles;
        string peakSection = "baseline";
        int peakSectionCycle = 0;
        TimeSpan cpuBefore = measuredProcess.TotalProcessorTime;
        int threadsBefore = measuredProcess.Threads.Count;
        int navigations = 0;
        var perSectionIncreases = new int[sections.Length];
        var perSectionNetChange = new int[sections.Length];
        var perSectionLargestIncrease = new int[sections.Length];

        const int measuredCycles = 25;
        for (int cycle = 0; cycle < measuredCycles; cycle++)
        {
            for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                string section = sections[sectionIndex];
                measuredProcess.Refresh();
                int beforeSectionHandles = measuredProcess.HandleCount;
                _sub = null;
                await ShowSectionAsync(section);
                EnsureNavigationHealthy(section);
                navigations++;
                await Task.Delay(8);
                measuredProcess.Refresh();
                int sectionHandles = measuredProcess.HandleCount;
                int sectionChange = sectionHandles - beforeSectionHandles;
                perSectionNetChange[sectionIndex] += sectionChange;
                if (sectionChange > 0) perSectionIncreases[sectionIndex]++;
                if (sectionChange > perSectionLargestIncrease[sectionIndex]) perSectionLargestIncrease[sectionIndex] = sectionChange;
                if (sectionHandles > peakHandles)
                {
                    peakHandles = sectionHandles;
                    peakSection = section;
                    peakSectionCycle = cycle + 1;
                    peakHandleCycle = cycle + 1;
                }
            }
            measuredProcess.Refresh();
            int sampledHandles = measuredProcess.HandleCount;
            lastCycleHandles = sampledHandles;
            if (sampledHandles > peakHandles)
            {
                peakHandles = sampledHandles;
                peakHandleCycle = cycle + 1;
            }
        }
        for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            LogService.Info("perf", $"CRITICAL_LEAK_HANDLE_SECTION section={sections[sectionIndex]} positiveTransitions={perSectionIncreases[sectionIndex]} netChange={perSectionNetChange[sectionIndex]} maxSingleIncrease={perSectionLargestIncrease[sectionIndex]}");
        LogService.Info("perf", $"CRITICAL_LEAK_HANDLE_TREND baseline={baseline.Handles} lastCycle={lastCycleHandles} peak={peakHandles} peakCycle={peakHandleCycle} peakSection={peakSection} peakSectionCycle={peakSectionCycle} peakDelta={peakHandles - baseline.Handles}");
        long measuredNavMs = phase.ElapsedMilliseconds - initialWarmMs - startupSettleMs - prewarmNavMs - baselineSettleGcMs;
        measuredProcess.Refresh();
        double cpuMs = (measuredProcess.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        int threadsAfter = measuredProcess.Threads.Count;
        LogService.Info("perf", $"CRITICAL_LEAK_CPU navigations={navigations} cpuMs={cpuMs:F1} cpuMsPerNav={cpuMs / Math.Max(1, navigations):F2} threadsBefore={threadsBefore} threadsAfter={threadsAfter} threadDelta={threadsAfter - threadsBefore}");

        await Task.Delay(750);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long finalSettleGcMs = phase.ElapsedMilliseconds - initialWarmMs - startupSettleMs - prewarmNavMs - baselineSettleGcMs - measuredNavMs;
        LogService.Info("perf", $"CRITICAL_LEAK_PHASE initialWarmMs={initialWarmMs} startupSettleMs={startupSettleMs} prewarmNavMs={prewarmNavMs} baselineSettleGcMs={baselineSettleGcMs} measuredNavMs={measuredNavMs} finalSettleGcMs={finalSettleGcMs} totalMs={phase.ElapsedMilliseconds}");
        var final = PerformanceTelemetryService.GetSnapshot();
        // Repeat a non-GC settle sample to distinguish handles released later by
        // asynchronous Windows/WinUI work from handles retained at test exit.
        await Task.Delay(250);
        measuredProcess.Refresh();
        int afterQuietSettleHandles = measuredProcess.HandleCount;
        LogService.Info("perf", $"CRITICAL_LEAK_HANDLE_RETENTION baseline={baseline.Handles} peak={peakHandles} afterGc={final.Handles} afterQuietSettle={afterQuietSettleHandles} peakDelta={peakHandles - baseline.Handles} retainedDelta={final.Handles - baseline.Handles} quietRetainedDelta={afterQuietSettleHandles - baseline.Handles} releasedFromPeak={peakHandles - final.Handles} peakSection={peakSection} peakCycle={peakSectionCycle}");

        long ws = final.WorkingSetMb - baseline.WorkingSetMb;
        long privateMb = final.PrivateMb - baseline.PrivateMb;
        long managed = final.ManagedMb - baseline.ManagedMb;
        int handles = final.Handles - baseline.Handles;

        LogService.Info("perf", $"CRITICAL_LEAK_AUDIT navigations={navigations} wsDelta={ws}MB privateDelta={privateMb}MB managedDelta={managed}MB handleDelta={handles}");
        // This is a warm, fully materialized, 175-transition retention test, not
        // a cold-start native allocation budget. Repeated verified baselines
        // stay within +/-2 MiB private, +/-1 MiB WS and one handle. Do not
        // tolerate the former 80/96 MiB growth, which could mask an actual leak.
        if (ws > 24 || privateMb > 24 || managed > 8 || handles > 48)
            throw new InvalidOperationException($"Critical leak audit exceeded threshold: nav={navigations} ws={ws}MB private={privateMb}MB managed={managed}MB handles={handles}");

        LogService.Info("perf", "CRITICAL_LEAK_AUDIT_OK");
    }

    private void EnsureNavigationHealthy(string section)
    {
        if (!string.IsNullOrWhiteSpace(_lastNavigationErrorId))
            throw new InvalidOperationException($"Regression: sekce {section} selhala, ID {_lastNavigationErrorId}.");
    }
}
