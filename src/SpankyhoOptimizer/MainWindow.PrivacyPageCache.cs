using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _privacyPageCache;
    private TweakVirtualRowViewModel[]? _privacyVirtualRowsCache;
    private Dictionary<TweakDefinition, TweakVirtualRowViewModel>? _privacyVirtualMapCache;
    private DateTime _privacyPageCacheUsedAt;

    private bool TryAttachPrivacyPageCache()
    {
        if (_privacyPageCache == null || _privacyVirtualRowsCache == null || _privacyVirtualMapCache == null)
            return false;

        foreach (var child in _privacyPageCache)
            AttachCachedElement(child);

        _virtualTweakRows = _privacyVirtualRowsCache;
        _virtualTweakRowMap = _privacyVirtualMapCache;

        _privacyPageCacheUsedAt = DateTime.UtcNow;
        RefreshVirtualTweakRows();
        ScheduleDeferredVirtualGroups("privacy");
        return true;
    }

    private void CapturePrivacyPageCache()
    {
        _privacyPageCache = ContentStack.Children.Cast<UIElement>().ToList();
        _privacyVirtualRowsCache = _virtualTweakRows;
        _privacyVirtualMapCache = new Dictionary<TweakDefinition, TweakVirtualRowViewModel>(_virtualTweakRowMap);
        _privacyPageCacheUsedAt = DateTime.UtcNow;
    }

    internal async System.Threading.Tasks.Task RunPrivacyDeferredRenderSmokeAsync()
    {
        await ShowSectionAsync("privacy");
        for (int i = 0; i < 40 && GetDeferredVirtualGroupCount("privacy") > 0; i++)
            await System.Threading.Tasks.Task.Delay(50);
        int remaining = GetDeferredVirtualGroupCount("privacy");
        if (remaining != 0)
            throw new InvalidOperationException($"Privacy deferred virtual groups incomplete: {remaining} remaining.");
        if (_privacyPageCache == null || _privacyVirtualRowsCache == null || _privacyVirtualRowsCache.Length != 68)
            throw new InvalidOperationException("Privacy deferred render cache coverage mismatch.");

        var cachedMap = _privacyVirtualMapCache;

        await ShowSectionAsync("system");
        await ShowSectionAsync("privacy");
        var currentCache = _privacyVirtualMapCache ??
            throw new InvalidOperationException("Privacy virtual map cache missing after re-entry.");
        if (!ReferenceEquals(_virtualTweakRowMap, currentCache))
            throw new InvalidOperationException("Privacy virtual map was not reattached from the current cache by reference.");
        bool originalCacheReused = ReferenceEquals(currentCache, cachedMap);

        LogService.Info("PRIVACY_DEFERRED_SMOKE_OK cache=" + (originalCacheReused ? "reused" : "rebuilt"));
    }

    internal async System.Threading.Tasks.Task RunPrivacyRevisionRefreshSmokeAsync()
    {
        await ShowSectionAsync("privacy");
        if (_privacyVirtualMapCache == null)
            throw new InvalidOperationException("Privacy revision smoke requires a populated virtual map cache.");

        var source = _privacyVirtualMapCache.Keys.FirstOrDefault(x => x.IsLoaded) ?? _privacyVirtualMapCache.Keys.FirstOrDefault();
        if (source == null || !_privacyVirtualMapCache.TryGetValue(source, out var row))
            throw new InvalidOperationException("Privacy revision smoke could not resolve a tweak row.");

        bool originalPending = source.Pending;
        int beforeRevision = source.Revision;
        try
        {
            source.Pending = !originalPending;
            if (source.Revision <= beforeRevision)
                throw new InvalidOperationException("Tweak revision did not advance after Pending changed.");
            RefreshVirtualTweakRows();
            if (row.Pending != source.Pending)
                throw new InvalidOperationException("Virtual row did not refresh after tweak revision changed.");
        }
        finally
        {
            source.Pending = originalPending;
            RefreshVirtualTweakRows();
        }

        if (row.Pending != originalPending)
            throw new InvalidOperationException("Virtual row did not restore the original Pending state.");
        LogService.Info("PRIVACY_REVISION_REFRESH_SMOKE_OK");
    }

    private void InvalidatePrivacyPageCache()
    {
        if (ReferenceEquals(_virtualTweakRows, _privacyVirtualRowsCache))
            ClearVirtualTweakRows();

        ClearDeferredVirtualGroups("privacy");
        _privacyPageCache = null;
        _privacyVirtualRowsCache = null;
        _privacyVirtualMapCache = null;
        _privacyPageCacheUsedAt = default;
    }
}

