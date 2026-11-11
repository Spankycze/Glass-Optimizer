using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static readonly TimeSpan PowerPageCacheTtl = TimeSpan.FromSeconds(15);
    private List<UIElement>? _powerPageCache;
    private DateTime _powerPageCacheAt;
    private bool _powerPageCacheAttached;
    private Microsoft.UI.Xaml.Controls.StackPanel? _powerActionsHost;
    private Services.PowerPlanSnapshot? _powerActionsSnapshot;
    private Microsoft.UI.Xaml.Controls.StackPanel? _powerSummaryHost;
    private Services.PowerPlanSnapshot? _powerSummarySnapshot;
    private DateTime _aiPowerControlsLastRefreshUtc;
    private static readonly TimeSpan CachedAiPowerRefreshInterval = TimeSpan.FromSeconds(3);

    private static bool ShouldRefreshCachedAiPowerControls(int existingChildren, DateTime lastRefreshUtc, DateTime nowUtc) =>
        existingChildren == 0 || nowUtc - lastRefreshUtc >= CachedAiPowerRefreshInterval;
    private Microsoft.UI.Xaml.Controls.StackPanel? _powerRecommendedHost;
    private Services.PowerSettingState[]? _powerRecommendedSettings;
    private int _powerRecommendedRendered;
    private bool _powerRecommendedQueued;
    private int _powerRecommendedQueuedGeneration = -1;
    private Microsoft.UI.Xaml.Controls.StackPanel? _powerAdvancedHost;
    private Services.PowerSettingState[]? _powerAdvancedSettings;
    private int _powerAdvancedRendered;
    private bool _powerAdvancedQueued;
    private int _powerAdvancedQueuedGeneration = -1;

    private bool IsPowerPageCacheFresh() =>
        _powerPageCache is { Count: > 0 } &&
        DateTime.UtcNow - _powerPageCacheAt <= PowerPageCacheTtl;

    private bool CanParkPowerPageCache()
    {
        if (!IsPowerPageCacheFresh() || _powerPageCache!.Count != ContentStack.Children.Count)
            return false;
        for (int i = 0; i < _powerPageCache.Count; i++)
            if (!ReferenceEquals(_powerPageCache[i], ContentStack.Children[i]))
                return false;
        return true;
    }

    private bool TryAttachPowerPageCache(System.Threading.CancellationToken token)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        if (_powerPageCache == null || DateTime.UtcNow - _powerPageCacheAt > PowerPageCacheTtl)
            return false;

        foreach (var element in _powerPageCache) AttachCachedElement(element);
        _powerPageCacheAttached = true;
        int generation = _navigationGeneration;
        if (_powerSummaryHost is { Children.Count: 0 } && _powerSummarySnapshot != null &&
            _powerActionsHost != null)
            SchedulePowerSummary(_powerSummaryHost, _powerSummarySnapshot, _powerActionsHost, token, generation);
        if (_powerActionsHost is { Children.Count: 0 } && _powerActionsSnapshot != null)
            SchedulePowerActions(_powerActionsHost, _powerActionsSnapshot, token, generation);
        var aiHost = _aiPowerControlsHost;
        // Reattaching the cached page should not launch 10+ file/registry probes
        // per second during fast sidebar navigation. Never throttle a blank panel.
        if (aiHost != null && aiHost.Parent != null &&
            ShouldRefreshCachedAiPowerControls(aiHost.Children.Count, _aiPowerControlsLastRefreshUtc, DateTime.UtcNow))
            SafeTaskService.Forget(LoadAiPowerControlsAsync(aiHost, generation, token),
                "ai-runtime", "Refresh cached AI power controls");
        long attachMs = perf.ElapsedMilliseconds;

        if (_powerRecommendedHost != null &&
            _powerRecommendedSettings != null &&
            _powerRecommendedRendered < _powerRecommendedSettings.Length)
            SchedulePowerRecommendedCards(token);
        if (_powerAdvancedHost != null &&
            _powerAdvancedSettings != null &&
            _powerAdvancedRendered < _powerAdvancedSettings.Length)
            SchedulePowerAdvancedCards(token);
        long scheduleMs = perf.ElapsedMilliseconds - attachMs;

        if (perf.ElapsedMilliseconds >= 3)
            LogService.Info("performance",
                $"POWER_CACHE_ATTACH attachMs={attachMs} scheduleMs={scheduleMs} totalMs={perf.ElapsedMilliseconds}");
        return true;
    }

    private void CapturePowerPageCache()
    {
        _powerPageCache = SnapshotChildren(ContentStack);
        _powerPageCacheAt = DateTime.UtcNow;
        _powerPageCacheAttached = true;
    }

    private void InvalidatePowerPageCache()
    {
        var pinned = _persistentPowerHost;
        _persistentPowerHost = null;
        if (pinned != null && !ReferenceEquals(pinned, ContentStack))
        {
            if (_contentLayerHost.Children.Contains(pinned))
                _contentLayerHost.Children.Remove(pinned);
            pinned.Children.Clear();
        }
        _powerPageCache = null;
        _powerPageCacheAt = default;
        _powerPageCacheAttached = false;
        _powerActionsHost = null;
        _powerActionsSnapshot = null;
        _powerSummaryHost = null;
        _powerSummarySnapshot = null;
        _aiPowerControlsLastRefreshUtc = default;
        _aiPowerDetailsSnapshotCache.Invalidate();
        _aiPowerControlsRefreshState.RetireDisclosure();
        _aiPowerControlsHost = null;
        _aiPowerLiveView = null;
        _powerRecommendedHost = null;
        _powerRecommendedSettings = null;
        _powerRecommendedRendered = 0;
        _powerRecommendedQueued = false;
        _powerRecommendedQueuedGeneration = -1;
        _powerAdvancedHost = null;
        _powerAdvancedSettings = null;
        _powerAdvancedRendered = 0;
        _powerAdvancedQueued = false;
        _powerAdvancedQueuedGeneration = -1;
    }
}
