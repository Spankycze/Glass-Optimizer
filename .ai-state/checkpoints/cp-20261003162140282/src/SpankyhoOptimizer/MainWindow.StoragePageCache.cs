using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _storagePageCache;
    private Border? _storageStaticHero;
    private Grid? _storageStaticMetrics;
    private StackPanel? _storageStaticShellHost;
    private bool _storageStaticShellQueued;
    private StackPanel? _storageSnapshotShellHost;
    private bool _storageSnapshotShellQueued;
    private StackPanel? _storageTweakHost;
    private bool _storageTweakQueued;
    private StackPanel? _storageHealthBody;
    private StackPanel? _storageAdvancedBody;
    private DateTime _storagePanelRefreshAt;
    private DateTime _storagePageCacheUsedAt;
    private long _storagePageRevisionStamp;
    private long _storageLastCacheAttachMs;

    private void EnsureStorageStaticShell()
    {
        _storageStaticHero ??= BuildModernSectionHero(
            "storage",
            "Storage",
            "NVMe / SSD stav, TRIM, driver mode a údržba bez zbytečných low-level voleb.",
            "NVMe / SSD",
            "Driver • TRIM • cache • cleanup");

        _storageStaticMetrics ??= BuildDashboardMetricGrid(
            BuildDashboardMetric("TRIM", "Read-back", "Skutečný stav v Storage kartě"),
            BuildDashboardMetric("NVMe", "Driver", "Microsoft / vendor režim"),
            BuildDashboardMetric("Cleanup", "Údržba", "Bez shader cache"),
            BuildDashboardMetric("Tweaky", "Rozbalené", "Ruční volby níže"));
    }

    private void PrewarmStorageStaticShell()
    {
        EnsureStorageStaticShell();
        _storageStaticHero!.Measure(new Windows.Foundation.Size(1100, 190));
        _storageStaticMetrics!.Measure(new Windows.Foundation.Size(1100, 180));
    }

    private void AttachStorageStaticShell(CancellationToken token, out long heroMs, out long metricsMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (_storageStaticHero != null && _storageStaticMetrics != null)
        {
            AttachCachedElement(_storageStaticHero);
            heroMs = sw.ElapsedMilliseconds;
            AttachCachedElement(_storageStaticMetrics);
            metricsMs = sw.ElapsedMilliseconds - heroMs;
            return;
        }

        _storageStaticShellHost = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ContentStack.Children.Add(_storageStaticShellHost);
        ScheduleStorageStaticShell(_storageStaticShellHost, token);

        heroMs = sw.ElapsedMilliseconds;
        metricsMs = 0;
    }

    private void ScheduleStorageStaticShell(StackPanel host, CancellationToken token)
    {
        if (_storageStaticShellQueued || host.Children.Count > 0)
            return;

        _storageStaticShellQueued = true;
        int generation = _navigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _storageStaticShellQueued = false;
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "storage" || !ReferenceEquals(_storageStaticShellHost, host) ||
                host.Children.Count > 0)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            EnsureStorageStaticShell();
            long buildMs = sw.ElapsedMilliseconds;

            host.Children.Add(_storageStaticHero!);
            long heroMs = sw.ElapsedMilliseconds - buildMs;
            host.Children.Add(_storageStaticMetrics!);
            long metricsMs = sw.ElapsedMilliseconds - buildMs - heroMs;

            LogService.Info("performance",
                $"STORAGE_STATIC_SHELL_DEFERRED buildMs={buildMs} heroAttachMs={heroMs} metricsAttachMs={metricsMs} totalMs={sw.ElapsedMilliseconds}");
        });
    }

    private void InvalidateStorageStaticShellCache()
    {
        _storageStaticHero = null;
        _storageStaticMetrics = null;
        _storageStaticShellHost = null;
        _storageStaticShellQueued = false;
    }

    private bool TryAttachStoragePageCache(CancellationToken token)
    {
        if (_storagePageCache == null) return false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var element in _storagePageCache) AttachCachedElement(element);
        long attachMs = sw.ElapsedMilliseconds;

        if (_storageStaticShellHost is { Children.Count: 0 } staticHost) ScheduleStorageStaticShell(staticHost, token);
        if (_storageSnapshotShellHost is { Children.Count: 0 }) ScheduleStorageSnapshotShell(token);
        if (_storageActionsHost is { Children.Count: 0 } actionsHost) ScheduleStorageActions(actionsHost, token);
        if (_storageTweakHost is { Children.Count: 0 }) ScheduleStorageTweakCards(token);
        long scheduleMs = sw.ElapsedMilliseconds - attachMs;

        long refreshStart = sw.ElapsedMilliseconds;
        RefreshCardsIfTweakRevisionChanged(ref _storagePageRevisionStamp);
        long refreshMs = sw.ElapsedMilliseconds - refreshStart;

        _storagePageCacheUsedAt = DateTime.UtcNow;
        bool stalePanels = DateTime.UtcNow - _storagePanelRefreshAt > TimeSpan.FromSeconds(20);
        if (stalePanels)
        {
            _storagePanelRefreshAt = DateTime.UtcNow;
            if (_storageHealthBody != null)
                SafeTaskService.Forget(LoadStorageHealthAsync(_storageHealthBody, token), "storage", "Cached storage health");
            if (_storageAdvancedBody != null)
                SafeTaskService.Forget(LoadStorageAdvancedAsync(_storageAdvancedBody, token), "storage", "Cached storage advanced");
        }

        long totalMs = sw.ElapsedMilliseconds;
        _storageLastCacheAttachMs = totalMs;
        if (totalMs >= 5)
            LogService.Info("performance",
                $"STORAGE_CACHE_ATTACH_PHASE attachMs={attachMs} scheduleMs={scheduleMs} refreshMs={refreshMs} totalMs={totalMs} stalePanels={stalePanels}");
        return true;
    }

    private void ScheduleStorageTweakCards(CancellationToken token)
    {
        if (_storageTweakQueued || _storageTweakHost == null || _storageTweakHost.Children.Count > 0) return;

        var host = _storageTweakHost;
        var rows = _tweaks
            .GroupBy(t => t.Group)
            .SelectMany(group => group.Select((tweak, index) => new
            {
                Group = group.Key,
                GroupCount = group.Count(),
                Tweak = tweak,
                FirstInGroup = index == 0
            }))
            .ToArray();

        if (rows.Length == 0)
            return;

        _storageTweakQueued = true;
        int completed = 0;
        long totalMs = 0;

        void QueueNext(int index)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || _section != "storage" ||
                    !ReferenceEquals(_storageTweakHost, host))
                {
                    _storageTweakQueued = false;
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var row = rows[index];
                if (row.FirstInGroup)
                    host.Children.Add(CreateClassicTweakGroupHeader(row.Group, row.GroupCount));
                host.Children.Add(CreateClassicTweakCard(row.Tweak));
                long batchMs = sw.ElapsedMilliseconds;
                totalMs += batchMs;
                completed++;

                LogService.Info("performance",
                    $"STORAGE_TWEAK_BATCH index={index} ms={batchMs} completed={completed}/{rows.Length}");

                if (index + 1 < rows.Length)
                {
                    QueueNext(index + 1);
                }
                else
                {
                    _storageTweakQueued = false;
                    LogService.Info("performance",
                        $"STORAGE_TWEAKS_DEFERRED totalMs={totalMs} maxPerTick=batched count={rows.Length}");
                }
            });
        }

        QueueNext(0);
    }

    private void CaptureStoragePageCache()
    {
        _storagePageCache ??= ContentStack.Children.Cast<UIElement>().ToList();
        _storagePanelRefreshAt = DateTime.UtcNow;
        _storagePageCacheUsedAt = DateTime.UtcNow;
        _storagePageRevisionStamp = CurrentTweakRevisionStamp();
    }

    private void InvalidateStoragePageCache(bool includeStaticShell = false)
    {
        _storagePageCache = null;

        // A detached StackPanel can keep logical ownership of its children even when
        // VisualTreeHelper reports no parent. Detach reusable static elements before
        // dropping the host reference, otherwise a later reattach can throw 0x800F1000.
        if (_storageStaticShellHost != null)
        {
            if (_storageStaticHero != null && _storageStaticShellHost.Children.Contains(_storageStaticHero))
                _storageStaticShellHost.Children.Remove(_storageStaticHero);
            if (_storageStaticMetrics != null && _storageStaticShellHost.Children.Contains(_storageStaticMetrics))
                _storageStaticShellHost.Children.Remove(_storageStaticMetrics);
        }

        if (includeStaticShell) InvalidateStorageStaticShellCache();
        _storageStaticShellHost = null;
        _storageStaticShellQueued = false;
        _storageSnapshotShellHost = null;
        _storageSnapshotShellQueued = false;
        _storageTweakHost = null;
        _storageTweakQueued = false;
        _storageToolsCard = null;
        _storageActionsHost = null;
        _storageActionsQueuedHost = null;
        _storageToolsRow = null;
        _storageMaintenanceCard = null;
        _storageToolsQueued = false;
        _storageHealthBody = null;
        _storageAdvancedBody = null;
        _storagePanelRefreshAt = default;
        _storagePageCacheUsedAt = default;
        _storagePageRevisionStamp = 0;
        _storageLastCacheAttachMs = 0;
    }

    internal async Task RunStorageToolsCacheSmokeAsync()
    {
        InvalidateStoragePageCache();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("storage");
        long foregroundMs = sw.ElapsedMilliseconds;
        if (_storageToolsCard != null)
            throw new InvalidOperationException("Storage actions were constructed in foreground navigation instead of a deferred batch.");
        if (foregroundMs > 35)
        {
            long firstForegroundMs = foregroundMs;
            await ShowSectionAsync("overview");
            await Task.Delay(100);
            InvalidateStoragePageCache();
            sw.Restart();
            await ShowSectionAsync("storage");
            foregroundMs = sw.ElapsedMilliseconds;
            LogService.Info("performance", $"STORAGE_FOREGROUND_RETRY firstMs={firstForegroundMs} retryMs={foregroundMs}");
            if (foregroundMs > 35)
                throw new InvalidOperationException($"Storage foreground render too slow after retry: first={firstForegroundMs} ms retry={foregroundMs} ms.");
        }

        int expectedTweaks = _tweaks.Count;
        for (int i = 0; i < 80 &&
             (_storageToolsRow == null ||
              _storageActionsHost?.Children.Count != 1 ||
              (_storageSnapshotShellHost?.Children.Count ?? 0) == 0 ||
              CountClassicTweakCards(_storageTweakHost) < expectedTweaks ||
              _storagePageCache == null); i++)
            await Task.Delay(25);

        if (_storageToolsRow == null)
            throw new InvalidOperationException("Storage tools row did not materialize.");
        if ((_storageSnapshotShellHost?.Children.Count ?? 0) == 0)
            throw new InvalidOperationException("Storage snapshot shell did not materialize.");
        int actualTweaks = CountClassicTweakCards(_storageTweakHost);
        if (expectedTweaks != 4 || actualTweaks != expectedTweaks)
            throw new InvalidOperationException($"Storage deferred tweak cards incomplete: expected={expectedTweaks} actual={actualTweaks}.");
        if (_storagePageCache == null)
            throw new InvalidOperationException("Storage page cache was not captured.");

        if (_storageActionsHost?.Children.Count != 1 || _storageToolsCard == null ||
            !_storageActionsHost.Children.Contains(_storageToolsCard))
            throw new InvalidOperationException("Storage deferred actions did not materialize exactly once.");

        var row = _storageToolsRow;
        var toolsCard = _storageToolsCard;
        var snapshotHost = _storageSnapshotShellHost;
        var tweakHost = _storageTweakHost;
        await ShowSectionAsync("overview");
        var attachSw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("storage");
        long navigationMs = attachSw.ElapsedMilliseconds;
        long cacheAttachMs = _storageLastCacheAttachMs;

        bool toolsCardReattached = toolsCard != null && _storageActionsHost != null &&
            ContentStack.Children.Contains(_storageActionsHost) &&
            _storageActionsHost.Children.Contains(toolsCard);
        if (!toolsCardReattached ||
            !ReferenceEquals(_storageToolsRow, row) ||
            !ReferenceEquals(_storageToolsCard, toolsCard) ||
            !ReferenceEquals(_storageSnapshotShellHost, snapshotHost) ||
            !ReferenceEquals(_storageTweakHost, tweakHost))
            throw new InvalidOperationException("Storage cached dashboard/deferred hosts were rebuilt instead of reattached.");
        if ((_storageSnapshotShellHost?.Children.Count ?? 0) == 0 ||
            CountClassicTweakCards(_storageTweakHost) != expectedTweaks)
            throw new InvalidOperationException("Storage cached deferred content was not preserved after reattach.");
        if (cacheAttachMs > 10)
            throw new InvalidOperationException($"Storage cache attach itself too slow: {cacheAttachMs} ms.");
        if (navigationMs > 75)
            throw new InvalidOperationException($"Storage cached navigation unexpectedly slow: {navigationMs} ms.");

        // Supersede a queued batch before the dispatcher can run it, then create
        // a new host. The old callback must neither populate it nor clear its ticket.
        await ShowSectionAsync("overview");
        InvalidateStoragePageCache();
        await ShowSectionAsync("storage");
        var staleActionsHost = _storageActionsHost!;
        await ShowSectionAsync("overview");
        InvalidateStoragePageCache();
        await ShowSectionAsync("storage");
        var freshActionsHost = _storageActionsHost!;
        for (int i = 0; i < 80 && freshActionsHost.Children.Count == 0; i++)
            await Task.Delay(25);
        if (staleActionsHost.Children.Count != 0 || freshActionsHost.Children.Count != 1 ||
            !ReferenceEquals(_storageActionsHost, freshActionsHost) ||
            _storageToolsCard == null || !freshActionsHost.Children.Contains(_storageToolsCard))
            throw new InvalidOperationException("Storage actions stale-load guard or fresh scheduling failed.");

        LogService.Info("performance",
            $"STORAGE_TOOLS_CACHE_SMOKE_OK foregroundMs={foregroundMs} cacheAttachMs={cacheAttachMs} navigationMs={navigationMs} tweaks={actualTweaks} reattached=true deferredActions=true staleActionsGuard=true");
    }

    private async Task RunStorageDeferredStateAfterSettleAsync(string cacheKey, TweakDefinition[] tweaks, bool detect, CancellationToken token, int generation)
    {
        try
        {
            await Task.Delay(75, token);
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "storage") return;
            await BuildStorageDeferredStateAsync(cacheKey, tweaks, detect, token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task BuildStorageDeferredStateAsync(string cacheKey, TweakDefinition[] tweaks, bool detect, CancellationToken token)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        long detectMs = 0, refreshMs = 0, recommendedMs = 0, captureMs = 0;
        try
        {
            if (detect)
            {
                var immediate = tweaks.Where(x => !x.LazyDetect).ToArray();
                if (immediate.Length > 0)
                    await _engine.DetectAsync(immediate, token);
                _tweakCacheTime[cacheKey] = DateTime.UtcNow;
            }
            detectMs = perf.ElapsedMilliseconds;
            token.ThrowIfCancellationRequested();
            if (_section != "storage") return;

            RefreshCards();
            refreshMs = perf.ElapsedMilliseconds - detectMs;

            long recommendedStart = perf.ElapsedMilliseconds;
            await BuildHardwareRecommendedPanelAsync("storage", tweaks, token);
            recommendedMs = perf.ElapsedMilliseconds - recommendedStart;

            long captureStart = perf.ElapsedMilliseconds;
            if (!token.IsCancellationRequested && _section == "storage") CaptureStoragePageCache();
            captureMs = perf.ElapsedMilliseconds - captureStart;

            var lazy = tweaks.Where(x => x.LazyDetect).ToArray();
            if (lazy.Length > 0)
                SafeTaskService.Forget(
                    DetectLazyTweaksAfterSettleAsync("storage", lazy, token, _navigationGeneration),
                    "tweaks",
                    "Deferred lazy Storage detect");

            LogService.Info("performance",
                $"STORAGE_DEFERRED_PHASE detectMs={detectMs} refreshMs={refreshMs} recommendedMs={recommendedMs} captureMs={captureMs} totalMs={perf.ElapsedMilliseconds} detect={detect}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Error("storage", "Deferred storage state failed", ex); }
    }
}

