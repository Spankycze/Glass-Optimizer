using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _restorePageCache;
    private DateTime _restorePageCacheAt;
    private DateTime _restorePageCacheUsedAt;
    private StackPanel? _restoreHistoryHost;
    private StackPanel? _restoreBatchHost;
    private StackPanel? _restoreSurfaceHost;
    private int _restoreSurfaceStage;
    private bool _restoreSurfaceBatchQueued;
    private DateTime _restoreHistoryRefreshAt;
    private int _restoreHistoryRefreshInFlight;

    private bool TryAttachRestorePageCache(CancellationToken token)
    {
        if (_restorePageCache == null || _restoreHistoryHost == null || _restoreBatchHost == null ||
            DateTime.UtcNow - _restorePageCacheUsedAt > TimeSpan.FromMinutes(5)) return false;
        foreach (var element in _restorePageCache) AttachCachedElement(element);
        _restorePageCacheUsedAt = DateTime.UtcNow;
        if (_restoreSurfaceHost != null && _restoreSurfaceStage < 2)
            QueueRestoreSurfaceBatch(_restoreSurfaceHost, _restoreHistoryHost, _restoreBatchHost, _navigationGeneration, token);
        if (DateTime.UtcNow - _restoreHistoryRefreshAt > TimeSpan.FromSeconds(5) &&
            Interlocked.CompareExchange(ref _restoreHistoryRefreshInFlight, 1, 0) == 0)
            SafeTaskService.Forget(RefreshRestoreHistoryCachedAsync(token), "restore", "Restore history refresh");
        return true;
    }

    private void CaptureRestorePageCache()
    {
        _restorePageCache = SnapshotChildren(ContentStack);
        _restorePageCacheAt = DateTime.UtcNow;
        _restorePageCacheUsedAt = _restorePageCacheAt;
    }

    private async System.Threading.Tasks.Task RefreshRestoreHistoryCachedAsync(CancellationToken token)
    {
        int generation = _navigationGeneration;
        StackPanel? historyHost = _restoreHistoryHost;
        StackPanel? batchHost = _restoreBatchHost;
        try
        {
            if (historyHost != null && batchHost != null)
                await LoadRestoreHistoryAsync(historyHost, batchHost, token);

            if (!token.IsCancellationRequested &&
                generation == _navigationGeneration &&
                _section == "restore" &&
                ReferenceEquals(_restoreHistoryHost, historyHost) &&
                ReferenceEquals(_restoreBatchHost, batchHost))
                _restoreHistoryRefreshAt = DateTime.UtcNow;
        }
        finally { Interlocked.Exchange(ref _restoreHistoryRefreshInFlight, 0); }
    }

    internal async System.Threading.Tasks.Task RunRestoreCacheSmokeAsync()
    {
        await ShowSectionAsync("restore");
        for (int i = 0; i < 40 && _restoreSurfaceStage < 2; i++)
            await System.Threading.Tasks.Task.Delay(10);
        if (_restoreSurfaceStage != 2 || _restoreSurfaceHost == null || _restoreSurfaceHost.Children.Count != 2)
            throw new InvalidOperationException($"Restore deferred surfaces incomplete: stage={_restoreSurfaceStage}, children={_restoreSurfaceHost?.Children.Count ?? -1}.");

        var cache = _restorePageCache;
        if (cache == null || cache.Count == 0)
            throw new InvalidOperationException("Restore cache was not captured.");
        await ShowSectionAsync("overview");
        await ShowSectionAsync("restore");
        if (!ReferenceEquals(cache, _restorePageCache) || ContentStack.Children.Count != cache.Count)
            throw new InvalidOperationException("Restore page cache was rebuilt instead of reattached.");
        LogService.Info("RESTORE_CACHE_SMOKE_OK");
    }

    private void InvalidateRestorePageCache()
    {
        _restorePageCache = null;
        _restorePageCacheAt = default;
        _restorePageCacheUsedAt = default;
        _restoreHistoryRefreshAt = default;
        _restoreHistoryHost = null;
        _restoreBatchHost = null;
        _restoreSurfaceHost = null;
        _restoreSurfaceStage = 0;
        _restoreSurfaceBatchQueued = false;
    }
}

