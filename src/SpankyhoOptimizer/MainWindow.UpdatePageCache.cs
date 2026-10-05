using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _updatePageCache;
    private TweakVirtualRowViewModel[]? _updateVirtualRowsCache;
    private Dictionary<TweakDefinition, TweakVirtualRowViewModel>? _updateVirtualMapCache;
    private StackPanel? _updateStatusBody;
    private Button? _updatePauseButton;
    private StackPanel? _updateActionsHost;
    private int _updateActionsRendered;
    private bool _updateActionsQueued;
    private int _updateActionsGeneration;
    private bool _updatePausedState;
    private DateTime _updateStatusUiAt;
    private DateTime _updatePageCacheUsedAt;
    private bool _updateStatusLoadPending;

    private bool TryAttachUpdatePageCache(CancellationToken token)
    {
        if (_updatePageCache == null || _updateVirtualRowsCache == null || _updateVirtualMapCache == null) return false;
        foreach (var item in _updatePageCache) AttachCachedElement(item);

        _virtualTweakRows = _updateVirtualRowsCache;
        _virtualTweakRowMap = _updateVirtualMapCache;
        RefreshVirtualTweakRows();

        _updatePageCacheUsedAt = DateTime.UtcNow;
        if (_updateActionsHost != null && _updateActionsRendered < 3)
            ScheduleWindowsUpdateActions(token, restartQueue: true);
        if (_updateStatusBody != null && _updatePauseButton != null && !_updateStatusLoadPending && DateTime.UtcNow - _updateStatusUiAt > TimeSpan.FromSeconds(10))
            SafeTaskService.Forget(LoadWindowsUpdateCenterAsync(_updateStatusBody, _updatePauseButton, CancellationToken.None), "update", "Cached Windows Update status");
        return true;
    }

    private void CaptureUpdatePageCache()
    {
        _updatePageCache = ContentStack.Children.Cast<UIElement>().ToList();
        _updateVirtualRowsCache = _virtualTweakRows;
        _updateVirtualMapCache = new Dictionary<TweakDefinition, TweakVirtualRowViewModel>(_virtualTweakRowMap);
        _updatePageCacheUsedAt = DateTime.UtcNow;
    }

    private void InvalidateUpdatePageCache()
    {
        if (ReferenceEquals(_virtualTweakRows, _updateVirtualRowsCache))
            ClearVirtualTweakRows();
        _updatePageCache = null;
        _updateVirtualRowsCache = null;
        _updateVirtualMapCache = null;
        _updateStatusBody = null;
        _updatePauseButton = null;
        _updateActionsHost = null;
        _updateActionsRendered = 0;
        _updateActionsQueued = false;
        _updateActionsGeneration++;
        _updateStatusUiAt = default;
        _updatePageCacheUsedAt = default;
        _updateStatusLoadPending = false;
    }

    internal async System.Threading.Tasks.Task RunUpdateVirtualCacheSmokeAsync()
    {
        await ShowSectionAsync("update");
        if (_updatePageCache == null || _updateVirtualRowsCache == null || _updateVirtualRowsCache.Length != 7 || _updateVirtualMapCache == null || _updateVirtualMapCache.Count != 7)
            throw new InvalidOperationException("Update virtual cache coverage mismatch after capture.");

        var cachedRows = _updateVirtualRowsCache;
        var cachedMap = _updateVirtualMapCache;
        await ShowSectionAsync("overview");
        if (_virtualTweakRows != null)
            throw new InvalidOperationException("Update virtual rows were not cleared after leaving section.");

        await ShowSectionAsync("update");
        for (int i = 0; i < 40 && (_updateActionsHost == null || _updateActionsHost.Children.Count < 3); i++)
            await System.Threading.Tasks.Task.Delay(25);
        if (_updateActionsHost?.Children.Count != 3 || _updateActionsRendered != 3 || _updatePauseButton == null)
            throw new InvalidOperationException(
                $"Update deferred actions incomplete: children={_updateActionsHost?.Children.Count ?? 0} rendered={_updateActionsRendered} pause={_updatePauseButton != null}.");
        if (!ReferenceEquals(_virtualTweakRows, cachedRows) || !ReferenceEquals(_virtualTweakRowMap, cachedMap) || _virtualTweakRowMap.Count != 7)
            throw new InvalidOperationException("Update virtual cache was not restored by reference on re-entry.");

        RefreshVirtualTweakRows();
        LogService.Info("UPDATE_VIRTUAL_CACHE_SMOKE_OK");
    }
}

