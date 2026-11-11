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
    // Coalesce duplicate asynchronous row refreshes within one navigation generation.
    private int _driverCacheRefreshQueuedGeneration = -1;

    private Dictionary<string, List<UIElement>> _componentsPageCache =>
        _componentsPageCacheStore ??= new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTime> _componentsPageCacheAt =>
        _componentsPageCacheAtStore ??= new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, long> _componentsPageRevisionStamp =>
        _componentsPageRevisionStampStore ??= new(StringComparer.OrdinalIgnoreCase);

    private bool TryAttachComponentsPageCache(string sub, System.Threading.CancellationToken token)
    {
        if (!_componentsPageCache.TryGetValue(sub, out var cached)) return false;
        var age = DateTime.UtcNow - (_componentsPageCacheAt.TryGetValue(sub, out var at) ? at : DateTime.MinValue);
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(20))
        {
            // Expired subpages must not stay rooted in the cache dictionary while
            // a fresh page is constructed (especially when switching between subpages).
            InvalidateComponentsPageCache(sub);
            return false;
        }

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
            {
                int generation = _navigationGeneration;
                if (System.Threading.Interlocked.Exchange(ref _driverCacheRefreshQueuedGeneration, generation) != generation)
                {
                    SafeTaskService.Forget(
                        RefreshDriverCacheAfterAttachAsync(sub, generation, token),
                        "drivers",
                        "Driver cache batched refresh");
                }
            }
        }
        return true;
    }

    private async System.Threading.Tasks.Task RefreshDriverCacheAfterAttachAsync(
        string sub,
        int generation,
        System.Threading.CancellationToken token)
    {
        try
        {
            int refreshed = await RefreshDriverTweakRowsAsync(token);
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration ||
                !string.Equals(_section, "components", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_sub, sub, StringComparison.Ordinal))
                return;

            long stamp = CurrentDriverRevisionStamp();
            if (_componentsPageCache.ContainsKey(sub))
                _componentsPageRevisionStamp[sub] = stamp;
            LogService.Info("drivers", $"DRIVER_CACHE_REFRESH_OK rows={_driverRows?.Length ?? 0} refreshed={refreshed} stamp={stamp}");
        }
        catch (System.OperationCanceledException) { }
        finally
        {
            System.Threading.Interlocked.CompareExchange(ref _driverCacheRefreshQueuedGeneration, -1, generation);
        }
    }

    private void CaptureComponentsPageCache(string sub)
    {
        _componentsPageCache[sub] = SnapshotChildren(ContentStack);
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

