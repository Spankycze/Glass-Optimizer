using System;
using System.Linq;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private DateTime _lastNavigationCacheSweepUtc = DateTime.UtcNow;
    private DateTime _lastNavigationPressureCheckUtc = DateTime.UtcNow;

    private void CleanupForNavigation(string nextSection)
    {
        bool sectionChanged = !string.Equals(_section, nextSection, StringComparison.OrdinalIgnoreCase);
        if (!sectionChanged) return;

        if (UseVirtualTweakRows())
            ClearVirtualTweakRows();
        if (_section == "debloat")
            InstalledAppService.TrimHeavyCache();
        if (_section == "overview" && _overviewPageCache == null)
            ReleaseOverviewGaugeVisuals();

        DateTime now = DateTime.UtcNow;

        // Expiry checks can invalidate several visual trees at once. The idle loop
        // already performs the authoritative one-minute sweep, so keep this only as
        // a sparse safety net during sustained active navigation.
        if (now - _lastNavigationCacheSweepUtc >= TimeSpan.FromSeconds(30))
        {
            _lastNavigationCacheSweepUtc = now;
            TrimExpiredUiPageCaches();
        }

        // Memory pressure is already checked by the idle maintenance loop. Keep
        // normal section clicks free of GC/system-memory probing; only sample here
        // occasionally so sustained active navigation still has a pressure safety net.
        if (now - _lastNavigationPressureCheckUtc >= TimeSpan.FromSeconds(30))
        {
            _lastNavigationPressureCheckUtc = now;
            if (Environment.WorkingSet >= 320L * 1024 * 1024 && MemoryPressureService.IsElevated())
            {
                MemoryPressureService.TrimHeavyCaches(pressureAlreadyConfirmed: true);
                TrimUiPageCachesForMemoryPressure();
            }
        }
    }

    private void TrimExpiredUiPageCaches()
    {
        DateTime now = DateTime.UtcNow;
        if (_overviewPageCache != null && now - _overviewPageCacheUsedAt > TimeSpan.FromMinutes(3)) InvalidateOverviewPageCache();
        if (_powerPageCache != null && now - _powerPageCacheAt > PowerPageCacheTtl) InvalidatePowerPageCache();
        if (_restorePageCache != null && now - _restorePageCacheUsedAt > TimeSpan.FromMinutes(5)) InvalidateRestorePageCache();
        if (_startupPageCache != null && now - _startupPageCacheAt > TimeSpan.FromSeconds(20)) InvalidateStartupPageCache();
        if (_defenderPageCache != null && now - _defenderPageCacheAt > TimeSpan.FromSeconds(20)) InvalidateDefenderPageCache();
        if (_appsPageCache != null && now - _appsUiDataAt > TimeSpan.FromSeconds(60)) InvalidateAppsPageCache();
        if (_debloatPageCache != null && now - _debloatPageCacheAt > TimeSpan.FromSeconds(60)) InvalidateDebloatPageCache();
        if (_fixerPageCache != null && now - _fixerDynamicAt > TimeSpan.FromMinutes(5)) InvalidateFixerPageCache();
        if (_aiPageCache != null && now - _aiPageCacheAt > TimeSpan.FromSeconds(60)) InvalidateAiPageCache();
        if (_settingsPageCache != null && now - _settingsPageCacheAt > TimeSpan.FromMinutes(5)) InvalidateSettingsPageCache();
        if (_networkPageCache != null && now - _networkPageCacheUsedAt > TimeSpan.FromSeconds(60)) InvalidateNetworkPageCache();
        if (_storagePageCache != null && now - _storagePageCacheUsedAt > TimeSpan.FromSeconds(60)) InvalidateStoragePageCache();
        if (_gpuPageCache != null && now - _gpuPageCacheUsedAt > TimeSpan.FromSeconds(60)) InvalidateGpuPageCache();
        if (_updatePageCache != null && now - _updatePageCacheUsedAt > TimeSpan.FromSeconds(60)) InvalidateUpdatePageCache();
        if (_spicetifyPageCache != null && now - _spicetifyPageCacheAt > SpicetifyPageCacheTtl) InvalidateSpicetifyPageCache();
        if (_systemPageCache != null && now - _systemPageCacheUsedAt > TimeSpan.FromMinutes(5)) InvalidateSystemPageCache();
        if (_privacyPageCache != null && now - _privacyPageCacheUsedAt > TimeSpan.FromMinutes(3)) InvalidatePrivacyPageCache();
        if (_uiPageCache != null && now - _uiPageCacheUsedAt > TimeSpan.FromMinutes(3)) InvalidateUiPageCache();
        TrimExpiredBrowserPageCaches(now);
        if (_contextMenuUiCache != null && now - _contextMenuUiCacheAt > TimeSpan.FromSeconds(60)) InvalidateContextMenuUiCache();
        foreach (string sub in _componentsPageCacheAt.Where(x => now - x.Value > TimeSpan.FromSeconds(20)).Select(x => x.Key).ToArray())
            InvalidateComponentsPageCache(sub);
    }

    private async System.Threading.Tasks.Task RunIdleCacheTrimLoopAsync()
    {
        if (_idleCacheTrimCts != null)
            await _idleCacheTrimCts.CancelAsync();
        _idleCacheTrimCts?.Dispose();
        _idleCacheTrimCts = new System.Threading.CancellationTokenSource();
        var token = _idleCacheTrimCts.Token;
        TimeSpan idleThreshold = TimeSpan.FromSeconds(90);
        TimeSpan postTrimInterval = TimeSpan.FromMinutes(1);
        while (!token.IsCancellationRequested)
        {
            TimeSpan idleFor = DateTime.UtcNow - _lastNavigationUtc;
            TimeSpan wait = idleFor >= idleThreshold
                ? postTrimInterval
                : idleThreshold - idleFor;

            if (wait < TimeSpan.FromSeconds(1))
                wait = TimeSpan.FromSeconds(1);

            await System.Threading.Tasks.Task.Delay(wait, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) break;

            // Navigation may have happened while the delay was in flight. Re-check off the
            // UI thread and only enqueue work when the user has actually been idle long enough.
            if (DateTime.UtcNow - _lastNavigationUtc < idleThreshold)
                continue;

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => RunIdleCacheTrimOnce());
        }
    }

    private void RunIdleCacheTrimOnce(bool allowMemoryPressureTrim = true)
    {
        if (DateTime.UtcNow - _lastNavigationUtc < TimeSpan.FromSeconds(90)) return;
        TrimExpiredUiPageCaches();
        if (allowMemoryPressureTrim && MemoryPressureService.IsElevated())
        {
            MemoryPressureService.TrimHeavyCaches(pressureAlreadyConfirmed: true);
            TrimUiPageCachesForMemoryPressure();
        }
    }

    internal void RunIdleCacheTrimSmoke()
    {
        var originalCache = _settingsPageCache;
        var originalCacheAt = _settingsPageCacheAt;
        var originalLastNavigationUtc = _lastNavigationUtc;
        try
        {
            _settingsPageCache = new System.Collections.Generic.List<Microsoft.UI.Xaml.UIElement>
            {
                new Microsoft.UI.Xaml.Controls.Border()
            };
            _settingsPageCacheAt = DateTime.UtcNow;
            _lastNavigationUtc = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            RunIdleCacheTrimOnce(allowMemoryPressureTrim: false);
            if (_settingsPageCache == null)
                throw new InvalidOperationException("Idle trim removed a non-expired settings cache.");

            _settingsPageCacheAt = DateTime.UtcNow - TimeSpan.FromMinutes(6);
            RunIdleCacheTrimOnce(allowMemoryPressureTrim: false);
            if (_settingsPageCache != null)
                throw new InvalidOperationException("Idle trim did not remove an expired settings cache.");

            LogService.Info("IDLE_CACHE_TRIM_SMOKE_OK");
        }
        finally
        {
            _settingsPageCache = originalCache;
            _settingsPageCacheAt = originalCacheAt;
            _lastNavigationUtc = originalLastNavigationUtc;
        }
    }

    private void TrimUiPageCachesForMemoryPressure()
    {
        InvalidateOverviewPageCache();
        InvalidateBrowserPageCache();
        InvalidateComponentsPageCache();
        InvalidateDefenderPageCache(includeStaticShell: true);
        InvalidateFixerPageCache();
        InvalidateGpuPageCache(includeStaticShell: true);
        InvalidateNetworkPageCache();
        InvalidatePowerPageCache();
        InvalidateRestorePageCache();
        InvalidateStartupPageCache();
        InvalidateStoragePageCache(includeStaticShell: true);
        InvalidateUpdatePageCache();
        InvalidateSpicetifyPageCache();
        InvalidateAiPageCache();
        InvalidateAppsPageCache();
        InvalidateDebloatPageCache();
        InvalidateSettingsPageCache();
        InvalidateSystemPageCache();
        InvalidatePrivacyPageCache();
        InvalidateUiPageCache();
        InvalidateMemoryRecommendedPanel();
        InvalidateContextMenuUiCache();
    }

    internal void RunBackingCacheTrimSmoke()
    {
        _debloatRowsCache = new SpankyhoOptimizer.ViewModels.DebloatRowViewModel[1];
        _debloatRowsSnapshot = Array.Empty<InstalledAppInfo>();
        _taskRowsCache = new SpankyhoOptimizer.ViewModels.TaskRowViewModel[1];
        _taskRowsSnapshot = Array.Empty<TaskInfo>();
        _gpuInstalledText = new Microsoft.UI.Xaml.Controls.TextBlock();
        _gpuOfficialText = new Microsoft.UI.Xaml.Controls.TextBlock();
        _gpuCommunityText = new Microsoft.UI.Xaml.Controls.TextBlock();
        _gpuDriverStatus = new Microsoft.UI.Xaml.Controls.TextBlock();
        _gpuUpdateBadge = new Microsoft.UI.Xaml.Controls.TextBlock();
        _gpuToolsCard = new Microsoft.UI.Xaml.Controls.Border();
        _storageToolsCard = new Microsoft.UI.Xaml.Controls.Border();
        _startupVirtualList = new Microsoft.UI.Xaml.Controls.ListView();
        _driverRows = new[] { TweakVirtualRowViewModel.Header("DRIVER") };
        _driverVirtualList = new Microsoft.UI.Xaml.Controls.ListView();
        _componentsContextCard = new Microsoft.UI.Xaml.Controls.Border();
        _memoryRecommendedPanel = new Microsoft.UI.Xaml.Controls.Border();
        _networkAdvancedBody = new Microsoft.UI.Xaml.Controls.StackPanel();

        // Simulate an abandoned deferred virtual batch holding an old visual tree.
        // ClearVirtualTweakRows must release both target Panel and row VM references.
        var deferred = _deferredVirtualStates["system"];
        deferred.RowTemplate = UiResourceService.Get<Microsoft.UI.Xaml.DataTemplate>("TweakVirtualRowTemplate");
        deferred.Groups.Enqueue(new DeferredVirtualGroup(
            new Microsoft.UI.Xaml.Controls.StackPanel(),
            "SMOKE",
            new System.Collections.Generic.List<TweakVirtualRowViewModel> { TweakVirtualRowViewModel.Header("SMOKE") }));
        deferred.BatchQueued = true;
        ClearVirtualTweakRows();
        if (deferred.Groups.Count != 0 || deferred.RowTemplate != null || deferred.BatchQueued)
            throw new InvalidOperationException("Deferred virtual UI references survived ClearVirtualTweakRows.");

        InvalidateDebloatPageCache();
        InvalidateComponentsPageCache();
        InvalidateGpuPageCache(includeStaticShell: true);
        InvalidateStoragePageCache(includeStaticShell: true);
        InvalidateNetworkPageCache();
        InvalidateStartupPageCache();
        InvalidateMemoryRecommendedPanel();
        if (_debloatRowsCache != null || _debloatRowsSnapshot != null || _taskRowsCache != null || _taskRowsSnapshot != null ||
            _gpuInstalledText != null || _gpuOfficialText != null || _gpuCommunityText != null || _gpuDriverStatus != null ||
            _gpuUpdateBadge != null || _gpuToolsCard != null || _gpuStatsShell != null ||
            _gpuNameTile != null || _gpuDriverTile != null || _gpuDriverCenterTile != null || _storageToolsCard != null ||
            _storageStaticHero != null || _storageStaticMetrics != null || _startupVirtualList != null ||
            _driverRows != null || _driverVirtualList != null ||
            _componentsContextCard != null || _memoryRecommendedPanel != null || _networkAdvancedBody != null)
            throw new InvalidOperationException("Backing UI/cache trim failed.");
        LogService.Info("BACKING_CACHE_TRIM_SMOKE_OK");
    }

    private void CleanupWindowResources()
    {
        _liveMetricsCts?.Cancel();
        _liveMetricsCts?.Dispose();
        _liveMetricsCts = null;
        GpuPdhUsageService.Dispose();
        DiskPdhUsageService.Dispose();
        HardwareChangeMonitorService.Stop();
        _idleCacheTrimCts?.Cancel();
        _idleCacheTrimCts?.Dispose();
        _idleCacheTrimCts = null;
        _upstreamUpdateCts?.Cancel();
        _upstreamUpdateCts?.Dispose();
        _upstreamUpdateCts = null;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        foreach (var tweak in _tweakCache.Values.SelectMany(x => x).Distinct())
            tweak.PropertyChanged -= Tweak_PropertyChanged;
        _tweakCache.Clear();
        _subSelectorCache.Clear();
        InvalidateOverviewPageCache(forceRelease: true);
        TrimUiPageCachesForMemoryPressure();
    }
}
