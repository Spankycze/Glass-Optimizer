using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _gpuPageCache;
    private StackPanel? _gpuTweakHost;
    private bool _gpuTweakQueued;
    private StackPanel? _gpuDriverCardsHost;
    private bool _gpuDriverCardsQueued;
    private Border? _gpuStaticHero;
    private StackPanel? _gpuHeroActionsHost;
    private bool _gpuHeroActionsQueued;
    private StackPanel? _gpuStatsHost;
    private bool _gpuStatsQueued;
    private Grid? _gpuStatsShell;
    private Border? _gpuDriverCenterTile;
    private DateTime _gpuPageCacheUsedAt;
    private long _gpuPageRevisionStamp;

    private bool TryAttachGpuPageCache(CancellationToken token)
    {
        if (_gpuPageCache == null) return false;
        foreach (var element in _gpuPageCache) AttachCachedElement(element);
        if (_gpuTweakHost is { Children.Count: 0 }) ScheduleGpuTweakCards(token);
        if (_gpuDriverCardsHost is { Children.Count: 0 }) ScheduleGpuDriverCards(token);
        if (_gpuHeroActionsHost is { Children.Count: 0 }) ScheduleGpuHeroActions(_gpuHeroActionsHost, token);
        if (_gpuStatsHost is { Children.Count: 0 }) ScheduleGpuStatsShell(_gpuStatsHost, token);
        RefreshCardsIfTweakRevisionChanged(ref _gpuPageRevisionStamp);
        _gpuPageCacheUsedAt = DateTime.UtcNow;
        return true;
    }

    private void CaptureGpuPageCache()
    {
        _gpuPageCache ??= ContentStack.Children.Cast<UIElement>().ToList();
        _gpuPageCacheUsedAt = DateTime.UtcNow;
        _gpuPageRevisionStamp = CurrentTweakRevisionStamp();
    }

    private void InvalidateGpuPageCache(bool includeStaticShell = false)
    {
        _gpuPageCache = null;
        _gpuTweakHost = null;
        _gpuTweakQueued = false;
        _gpuDriverCardsHost = null;
        _gpuDriverCardsQueued = false;
        if (includeStaticShell)
        {
            _gpuStaticHero = null;
            _gpuHeroActionsHost = null;
            _gpuHeroActionsQueued = false;
        }
        _gpuStatsHost = null;
        _gpuStatsQueued = false;
        _gpuNameText = null;
        _gpuInstalledText = null;
        _gpuOfficialText = null;
        _gpuCommunityText = null;
        _gpuDriverStatus = null;
        _gpuUpdateBadge = null;
        _gpuExostenzaCard = null;
        _gpuToolsCard = null;
        if (includeStaticShell)
        {
            _gpuStatsShell = null;
            _gpuNameTile = null;
            _gpuDriverTile = null;
            _gpuDriverCenterTile = null;
        }
        _gpuPageCacheUsedAt = default;
        _gpuPageRevisionStamp = 0;
    }
}

