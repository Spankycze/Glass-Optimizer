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
    private Microsoft.UI.Xaml.Controls.StackPanel? _powerRecommendedHost;
    private Services.PowerSettingState[]? _powerRecommendedSettings;
    private int _powerRecommendedRendered;
    private bool _powerRecommendedQueued;
    private Microsoft.UI.Xaml.Controls.StackPanel? _powerAdvancedHost;
    private Services.PowerSettingState[]? _powerAdvancedSettings;
    private int _powerAdvancedRendered;
    private bool _powerAdvancedQueued;

    private bool TryAttachPowerPageCache(System.Threading.CancellationToken token)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        if (_powerPageCache == null || DateTime.UtcNow - _powerPageCacheAt > PowerPageCacheTtl)
            return false;

        foreach (var element in _powerPageCache) AttachCachedElement(element);
        _powerPageCacheAttached = true;
        if (_aiPowerControlsHost != null)
            SafeTaskService.Forget(LoadAiPowerControlsAsync(_aiPowerControlsHost, _navigationGeneration, token),
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
        _powerPageCache = ContentStack.Children.Cast<UIElement>().ToList();
        _powerPageCacheAt = DateTime.UtcNow;
        _powerPageCacheAttached = true;
    }

    private void InvalidatePowerPageCache()
    {
        _powerPageCache = null;
        _powerPageCacheAt = default;
        _powerPageCacheAttached = false;
        _powerActionsHost = null;
        _powerRecommendedHost = null;
        _powerRecommendedSettings = null;
        _powerRecommendedRendered = 0;
        _powerRecommendedQueued = false;
        _powerAdvancedHost = null;
        _powerAdvancedSettings = null;
        _powerAdvancedRendered = 0;
        _powerAdvancedQueued = false;
    }
}
