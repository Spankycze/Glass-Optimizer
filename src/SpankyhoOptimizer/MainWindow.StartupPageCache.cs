using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static readonly TimeSpan StartupPageCacheTtl = TimeSpan.FromSeconds(12);
    private List<UIElement>? _startupPageCache;
    private DateTime _startupPageCacheAt;
    private Action? _startupSystemRowsLoad;

    private bool TryAttachStartupPageCache()
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        if (_startupPageCache == null ||
            _startupPageCache.Count == 0 ||
            DateTime.UtcNow - _startupPageCacheAt > StartupPageCacheTtl)
            return false;

        const int synchronousRoots = 3;
        int syncCount = Math.Min(synchronousRoots, _startupPageCache.Count);
        for (int i = 0; i < syncCount; i++)
            AttachCachedElement(_startupPageCache[i]);

        if (syncCount < _startupPageCache.Count)
        {
            int generation = _navigationGeneration;
            var deferredRoots = _startupPageCache.Skip(syncCount).ToArray();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (generation != _navigationGeneration || _section != "startup") return;
                var deferredPerf = System.Diagnostics.Stopwatch.StartNew();
                foreach (var element in deferredRoots)
                    AttachCachedElement(element);
                LogService.Info("performance",
                    $"STARTUP_CACHE_ATTACH_DEFERRED ms={deferredPerf.ElapsedMilliseconds} roots={deferredRoots.Length}");
            });
        }

        // A queued load can be canceled when the user switches sections;
        // a cached, already-open expander would not raise Expanding again.
        if (_startupSystemExpander is { IsExpanded: true } && _startupSystemList == null)
            _startupSystemRowsLoad?.Invoke();
        if (_backgroundActivityExpander is { IsExpanded: true })
            _backgroundActivityResume?.Invoke();
        _startupPageCacheAt = DateTime.UtcNow;
        if (perf.ElapsedMilliseconds >= 3)
            LogService.Info("performance",
                $"STARTUP_CACHE_ATTACH ms={perf.ElapsedMilliseconds} roots={_startupPageCache.Count} syncRoots={syncCount}");
        return true;
    }

    private void CaptureStartupPageCache()
    {
        _startupPageCache = SnapshotChildren(ContentStack);
        _startupPageCacheAt = DateTime.UtcNow;
    }

    private void InvalidateStartupPageCache()
    {
        _startupPageCache = null;
        _startupPageCacheAt = default;
        _startupVirtualList = null;
        _startupSystemList = null;
        _startupSystemExpander = null;
        _startupSystemRowsLoad = null;
        _backgroundActivityExpander = null;
        _backgroundActivityResume = null;
        _backgroundActivityLoadedAt = default;
        _backgroundActivityLoadedHost = null;
        _backgroundActivityLoadingGeneration = -1;
        _backgroundActivityLoadingHost = null;
    }
}
