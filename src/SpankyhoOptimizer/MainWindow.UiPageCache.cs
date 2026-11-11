using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _uiPageCache;
    private TweakVirtualRowViewModel[]? _uiVirtualRowsCache;
    private Dictionary<TweakDefinition, TweakVirtualRowViewModel>? _uiVirtualMapCache;
    private DateTime _uiPageCacheUsedAt;
    private bool _uiContextRefreshQueued;

    private bool TryAttachUiPageCache()
    {
        if (_uiPageCache == null || _uiVirtualRowsCache == null || _uiVirtualMapCache == null)
            return false;

        if (!_uiContextRefreshQueued && _contextMenuUiCache is { Busy: false } &&
            DateTime.UtcNow - _contextMenuUiCacheAt > ContextMenuStateFreshness)
        {
            // Registry-backed context menu checks must not block cached-page navigation.
            // Revalidate only after navigation has returned to the UI dispatcher.
            var contextState = _contextMenuUiCache;
            int navigationGeneration = _navigationGeneration;
            _uiContextRefreshQueued = true;
            bool accepted = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _uiContextRefreshQueued = false;
                if (navigationGeneration == _navigationGeneration && _section == "ui" &&
                    ReferenceEquals(_contextMenuUiCache, contextState))
                    ScheduleContextMenuUiStateRefresh(contextState);
            });
            if (!accepted) _uiContextRefreshQueued = false;
        }

        foreach (var child in _uiPageCache)
            AttachCachedElement(child);

        _virtualTweakRows = _uiVirtualRowsCache;
        _virtualTweakRowMap = _uiVirtualMapCache;

        _uiPageCacheUsedAt = DateTime.UtcNow;
        RefreshVirtualTweakRows();
        ScheduleDeferredVirtualGroups("ui");
        return true;
    }

    private void CaptureUiPageCache()
    {
        _uiPageCache = SnapshotChildren(ContentStack);
        _uiVirtualRowsCache = _virtualTweakRows;
        _uiVirtualMapCache = new Dictionary<TweakDefinition, TweakVirtualRowViewModel>(_virtualTweakRowMap);
        _uiPageCacheUsedAt = DateTime.UtcNow;
    }

    internal async System.Threading.Tasks.Task RunUiDeferredRenderSmokeAsync()
    {
        await ShowSectionAsync("ui");
        for (int i = 0; i < 40 && GetDeferredVirtualGroupCount("ui") > 0; i++)
            await System.Threading.Tasks.Task.Delay(50);
        int remaining = GetDeferredVirtualGroupCount("ui");
        if (remaining != 0)
            throw new InvalidOperationException($"UI deferred virtual groups incomplete: {remaining} remaining.");
        int expectedUiTweaks = CatalogService.GetTweaks("ui").Count;
        if (_uiPageCache == null || _uiVirtualRowsCache == null || _uiVirtualRowsCache.Length != expectedUiTweaks)
            throw new InvalidOperationException($"UI deferred render cache coverage mismatch rows={_uiVirtualRowsCache?.Length ?? -1} expected={expectedUiTweaks}.");
        if (_contextMenuUiCache == null)
            throw new InvalidOperationException("UI context-menu cache missing.");

        _contextMenuUiCacheAt = DateTime.UtcNow;
        if (RefreshContextMenuUiStateIfStale(_contextMenuUiCache))
            throw new InvalidOperationException("Fresh context-menu state was redundantly re-read.");

        _contextMenuUiCacheAt = DateTime.UtcNow - ContextMenuStateFreshness - TimeSpan.FromMilliseconds(50);
        if (!RefreshContextMenuUiStateIfStale(_contextMenuUiCache))
            throw new InvalidOperationException("Stale context-menu state was not revalidated.");

        InvalidateUiPageCache();
        if (_uiPageCache != null || _contextMenuUiCache != null)
            throw new InvalidOperationException("UI cache invalidation retained the Context Menu visual tree.");

        LogService.Info("UI_DEFERRED_SMOKE_OK contextMenuFreshness=5s invalidationCoupled=true");
    }

    private void InvalidateUiPageCache()
    {
        if (ReferenceEquals(_virtualTweakRows, _uiVirtualRowsCache))
            ClearVirtualTweakRows();

        ClearDeferredVirtualGroups("ui");
        _uiPageCache = null;
        _uiVirtualRowsCache = null;
        _uiVirtualMapCache = null;
        _uiPageCacheUsedAt = default;

        // Context Menu controls are part of the UI page visual tree and carry click/hover
        // handlers. Keeping their secondary cache alive after the page itself is invalidated
        // retains the detached old tree and can also reattach stale-theme controls later.
        InvalidateContextMenuUiCache();
    }
}

