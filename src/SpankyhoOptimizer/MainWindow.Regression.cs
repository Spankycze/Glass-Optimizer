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
        for (int cycle = 0; cycle < 20; cycle++)
        {
            foreach (string section in sections)
            {
                var beforeSection = PerformanceTelemetryService.GetSnapshot();
                await ShowSectionAsync(section);
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
        object? mainRoot = ContentStack.Children.Count > 0 ? ContentStack.Children[0] : null;

        var mainNoOp = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
            await NavigateMainSectionFromUiAsync("system");
        mainNoOp.Stop();

        if (_navigationGeneration != mainGeneration)
            throw new InvalidOperationException("Active main navigation triggered a redundant navigation generation.");
        if (ContentStack.Children.Count == 0 || !ReferenceEquals(ContentStack.Children[0], mainRoot))
            throw new InvalidOperationException("Active main navigation rebuilt the current visual tree.");

        _sub = "Firefox";
        await ShowSectionAsync("browsers");
        int subGeneration = _navigationGeneration;
        object? subRoot = ContentStack.Children.Count > 0 ? ContentStack.Children[0] : null;

        for (int i = 0; i < 1000; i++)
            await NavigateSubSectionFromUiAsync("browsers", "Firefox");

        if (_navigationGeneration != subGeneration)
            throw new InvalidOperationException("Active sub-navigation triggered a redundant navigation generation.");
        bool subRootStable = ContentStack.Children.Count > 0 && ReferenceEquals(ContentStack.Children[0], subRoot);

        await NavigateSubSectionFromUiAsync("browsers", "Chrome");
        if (_navigationGeneration != subGeneration + 1 || !string.Equals(_sub, "Chrome", StringComparison.Ordinal))
            throw new InvalidOperationException("Different sub-navigation was incorrectly skipped.");

        LogService.Info("perf", $"USER_NAV_NOOP_SMOKE_OK main1000Ms={mainNoOp.Elapsed.TotalMilliseconds:F3} subRootStable={subRootStable}");
    }

    public async Task RunNavigation500SoakAuditAsync()
    {
        string[] sections = { "debloat", "system", "privacy", "ui", "update", "browsers", "components", "spicetify", "gpu" };
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
        await Task.Delay(250);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var baseline = PerformanceTelemetryService.GetSnapshot();
        var navigationTimings = sections.ToDictionary(x => x, _ => new System.Collections.Generic.List<long>(50), StringComparer.OrdinalIgnoreCase);
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
                navigationTimings[section].Add(navSw.ElapsedMilliseconds);
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
            long[] ordered = pair.Value.OrderBy(x => x).ToArray();
            if (ordered.Length == 0) continue;
            double avg = ordered.Average();
            long p95 = ordered[Math.Min(ordered.Length - 1, (int)Math.Floor((ordered.Length - 1) * 0.95))];
            LogService.Info("perf", $"NAV500_SECTION section={pair.Key} avgMs={avg:F2} p95Ms={p95} maxMs={ordered[^1]} n={ordered.Length} handleDeltaSum={navigationHandleDelta[pair.Key]}");
        }
        // Normalize the active page before the retention snapshot. The measured loop ends on GPU,
        // whose active WinUI/composition resources are not a navigation leak and would skew handle delta.
        _sub = null;
        await ShowSectionAsync("system");
        EnsureNavigationHealthy("system");
        var beforeFinalSettle = PerformanceTelemetryService.GetSnapshot();
        for (int i = 0; i < 80 && GetDeferredVirtualGroupCount("system") > 0; i++)
            await Task.Delay(25);
        await Task.Delay(250);
        var afterFinalSettle = PerformanceTelemetryService.GetSnapshot();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var final = PerformanceTelemetryService.GetSnapshot();
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
        var overviewTimes = new System.Collections.Generic.List<long>(30);
        var powerTimes = new System.Collections.Generic.List<long>(30);

        for (int i = 0; i < 5; i++)
        {
            await ShowSectionAsync("overview");
            await ShowSectionAsync("power");
        }

        for (int i = 0; i < 30; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ShowSectionAsync("overview");
            sw.Stop();
            overviewTimes.Add(sw.ElapsedMilliseconds);

            sw.Restart();
            await ShowSectionAsync("power");
            sw.Stop();
            powerTimes.Add(sw.ElapsedMilliseconds);
            await Task.Yield();
        }

        static (double Avg, long P95, long Max) Stats(System.Collections.Generic.List<long> values)
        {
            long[] ordered = values.OrderBy(x => x).ToArray();
            return (ordered.Average(),
                ordered[Math.Min(ordered.Length - 1, (int)Math.Floor((ordered.Length - 1) * 0.95))],
                ordered[^1]);
        }

        var ov = Stats(overviewTimes);
        var pw = Stats(powerTimes);
        LogService.Info("perf", $"NAV_TAIL_SMOKE_OK overviewAvgMs={ov.Avg:F2} overviewP95Ms={ov.P95} overviewMaxMs={ov.Max} powerAvgMs={pw.Avg:F2} powerP95Ms={pw.P95} powerMaxMs={pw.Max}");

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
            "debloat", "system", "privacy", "ui", "update", "browsers", "components",
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
        string[] sections = { "debloat", "system", "privacy", "ui", "update", "browsers", "components", "spicetify" };
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

        var baseline = PerformanceTelemetryService.GetSnapshot();
        int navigations = 0;

        const int measuredCycles = 25;
        for (int cycle = 0; cycle < measuredCycles; cycle++)
        {
            foreach (string section in sections)
            {
                _sub = null;
                await ShowSectionAsync(section);
                EnsureNavigationHealthy(section);
                navigations++;
                await Task.Delay(8);
            }
        }
        long measuredNavMs = phase.ElapsedMilliseconds - initialWarmMs - startupSettleMs - prewarmNavMs - baselineSettleGcMs;

        await Task.Delay(750);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long finalSettleGcMs = phase.ElapsedMilliseconds - initialWarmMs - startupSettleMs - prewarmNavMs - baselineSettleGcMs - measuredNavMs;
        LogService.Info("perf", $"CRITICAL_LEAK_PHASE initialWarmMs={initialWarmMs} startupSettleMs={startupSettleMs} prewarmNavMs={prewarmNavMs} baselineSettleGcMs={baselineSettleGcMs} measuredNavMs={measuredNavMs} finalSettleGcMs={finalSettleGcMs} totalMs={phase.ElapsedMilliseconds}");
        var final = PerformanceTelemetryService.GetSnapshot();

        long ws = final.WorkingSetMb - baseline.WorkingSetMb;
        long privateMb = final.PrivateMb - baseline.PrivateMb;
        long managed = final.ManagedMb - baseline.ManagedMb;
        int handles = final.Handles - baseline.Handles;

        LogService.Info("perf", $"CRITICAL_LEAK_AUDIT navigations={navigations} wsDelta={ws}MB privateDelta={privateMb}MB managedDelta={managed}MB handleDelta={handles}");
        if (ws > 80 || privateMb > 96 || managed > 32 || handles > 150)
            throw new InvalidOperationException($"Critical leak audit exceeded threshold: nav={navigations} ws={ws}MB private={privateMb}MB managed={managed}MB handles={handles}");

        LogService.Info("perf", "CRITICAL_LEAK_AUDIT_OK");
    }

    private void EnsureNavigationHealthy(string section)
    {
        if (!string.IsNullOrWhiteSpace(_lastNavigationErrorId))
            throw new InvalidOperationException($"Regression: sekce {section} selhala, ID {_lastNavigationErrorId}.");
    }
}
