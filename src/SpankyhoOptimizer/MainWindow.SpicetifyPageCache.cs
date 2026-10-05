using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static readonly TimeSpan SpicetifyPageCacheTtl = TimeSpan.FromMinutes(2);
    private List<UIElement>? _spicetifyPageCache;
    private DateTime _spicetifyPageCacheAt;
    private StackPanel? _spicetifyActionsBody;
    private StackPanel? _spicetifyTweaksBody;
    private StackPanel? _spicetifyExtensionsBody;
    private StackPanel? _spicetifyThemeBody;
    private SpicetifyStatus? _spicetifyCachedStatus;
    private bool _spicetifyActionsQueued;
    private bool _spicetifySecondaryQueued;

    private bool TryAttachSpicetifyPageCache(System.Threading.CancellationToken token)
    {
        if (_spicetifyPageCache == null || DateTime.UtcNow - _spicetifyPageCacheAt > SpicetifyPageCacheTtl)
            return false;
        foreach (var element in _spicetifyPageCache)
            AttachCachedElement(element);
        if (_spicetifyCachedStatus != null)
        {
            if (_spicetifyActionsBody != null && _spicetifyActionsBody.Children.Count == 0)
                ScheduleDeferredSpicetifyActions(_spicetifyCachedStatus, token);
            if (_spicetifyTweaksBody != null && _spicetifyTweaksBody.Children.Count == 0)
                ScheduleDeferredSpicetifySecondary(_spicetifyCachedStatus, token);
            else if (_spicetifyExtensionsBody != null && _spicetifyExtensionsBody.Children.Count == 0)
                ScheduleDeferredSpicetifySecondary(_spicetifyCachedStatus, token);
            if (_spicetifyThemeBody != null && _spicetifyThemeBody.Children.Count == 0)
                ScheduleDeferredSpicetifyTheme(_spicetifyCachedStatus, _spicetifyThemeBody, token);
        }
        return true;
    }

    private void CaptureSpicetifyPageCache()
    {
        _spicetifyPageCache = ContentStack.Children.Cast<UIElement>().ToList();
        _spicetifyPageCacheAt = DateTime.UtcNow;
    }

    private void InvalidateSpicetifyPageCache()
    {
        SpicetifyService.InvalidateStatusCache();
        _spicetifyPageCache = null;
        _spicetifyActionsBody = null;
        _spicetifyTweaksBody = null;
        _spicetifyExtensionsBody = null;
        _spicetifyThemeBody = null;
        _spicetifyCachedStatus = null;
        _spicetifyActionsQueued = false;
        _spicetifySecondaryQueued = false;
        _spicetifyPageCacheAt = default;
    }
}

