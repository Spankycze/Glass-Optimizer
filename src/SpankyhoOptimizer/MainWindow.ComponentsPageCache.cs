using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Dictionary<string, List<UIElement>>? _componentsPageCacheStore;
    private Dictionary<string, DateTime>? _componentsPageCacheAtStore;
    private Dictionary<string, long>? _componentsPageRevisionStampStore;

    private Dictionary<string, List<UIElement>> _componentsPageCache =>
        _componentsPageCacheStore ??= new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTime> _componentsPageCacheAt =>
        _componentsPageCacheAtStore ??= new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, long> _componentsPageRevisionStamp =>
        _componentsPageRevisionStampStore ??= new(StringComparer.OrdinalIgnoreCase);

    private bool TryAttachComponentsPageCache(string sub, System.Threading.CancellationToken token)
    {
        if (!_componentsPageCache.TryGetValue(sub, out var cached) ||
            !_componentsPageCacheAt.TryGetValue(sub, out var at) ||
            DateTime.UtcNow - at > TimeSpan.FromSeconds(20)) return false;

        if (string.Equals(sub, "Task Scheduler", StringComparison.OrdinalIgnoreCase) &&
            TaskSchedulerService.NeedsBasicBackgroundRefresh)
        {
            InvalidateComponentsPageCache(sub);
            return false;
        }

        foreach (var element in cached) AttachCachedElement(element);
        if (string.Equals(sub, "Ovladače", StringComparison.OrdinalIgnoreCase))
        {
            long stamp = _componentsPageRevisionStamp.TryGetValue(sub, out var cachedStamp) ? cachedStamp : long.MinValue;
            long current = CurrentDriverRevisionStamp();
            if (current != stamp)
                SafeTaskService.Forget(RefreshDriverCacheAfterAttachAsync(sub, token), "drivers", "Driver cache batched refresh");
        }
        return true;
    }

    private async System.Threading.Tasks.Task RefreshDriverCacheAfterAttachAsync(string sub, System.Threading.CancellationToken token)
    {
        try
        {
            int refreshed = await RefreshDriverTweakRowsAsync(token);
            token.ThrowIfCancellationRequested();
            if (!string.Equals(_section, "components", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_sub, sub, StringComparison.Ordinal))
                return;

            long stamp = CurrentDriverRevisionStamp();
            if (_componentsPageCache.ContainsKey(sub))
                _componentsPageRevisionStamp[sub] = stamp;
            LogService.Info("drivers", $"DRIVER_CACHE_REFRESH_OK rows={_driverRows?.Length ?? 0} refreshed={refreshed} stamp={stamp}");
        }
        catch (System.OperationCanceledException) { }
    }

    private void CaptureComponentsPageCache(string sub)
    {
        _componentsPageCache[sub] = ContentStack.Children.Cast<UIElement>().ToList();
        _componentsPageCacheAt[sub] = DateTime.UtcNow;
        _componentsPageRevisionStamp[sub] = string.Equals(sub, "Ovladače", StringComparison.OrdinalIgnoreCase)
            ? CurrentDriverRevisionStamp()
            : CurrentTweakRevisionStamp();
    }

    private void InvalidateComponentsPageCache(string? sub = null)
    {
        if (string.IsNullOrWhiteSpace(sub))
        {
            _componentsPageCache.Clear();
            _componentsPageCacheAt.Clear();
            _componentsPageRevisionStamp.Clear();
            _taskRowsCache = null;
            _taskRowsSnapshot = null;
            _driverRows = null;
            _driverVirtualList = null;
            _componentsContextCard = null;
            return;
        }
        _componentsPageCache.Remove(sub);
        _componentsPageCacheAt.Remove(sub);
        _componentsPageRevisionStamp.Remove(sub);
        if (string.Equals(sub, "Task Scheduler", StringComparison.OrdinalIgnoreCase))
        {
            _taskRowsCache = null;
            _taskRowsSnapshot = null;
        }
        else if (string.Equals(sub, "Ovladače", StringComparison.OrdinalIgnoreCase))
        {
            // Page/ListView lifetime is short, but the 25 lightweight row VMs can be reused
            // while they still point at the exact same catalog TweakDefinition instances.
            // Full Components invalidation still releases them for theme/memory/apply changes.
            _driverVirtualList = null;
        }
    }
}

