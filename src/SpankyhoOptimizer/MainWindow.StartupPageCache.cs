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

        _startupPageCacheAt = DateTime.UtcNow;
        if (perf.ElapsedMilliseconds >= 3)
            LogService.Info("performance",
                $"STARTUP_CACHE_ATTACH ms={perf.ElapsedMilliseconds} roots={_startupPageCache.Count} syncRoots={syncCount}");
        return true;
    }

    private void CaptureStartupPageCache()
    {
        _startupPageCache = ContentStack.Children.Cast<UIElement>().ToList();
        _startupPageCacheAt = DateTime.UtcNow;
    }

    private void InvalidateStartupPageCache()
    {
        _startupPageCache = null;
        _startupPageCacheAt = default;
        _startupVirtualList = null;
        _startupSystemList = null;
    }
}
