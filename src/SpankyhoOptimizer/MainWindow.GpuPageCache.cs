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
    private StackPanel? _gpuTweakQueuedHost;
    private StackPanel? _gpuDriverCardsHost;
    private StackPanel? _gpuDriverCardsQueuedHost;
    private Border? _gpuStaticHero;
    private StackPanel? _gpuHeroActionsHost;
    private StackPanel? _gpuHeroActionsQueuedHost;
    private StackPanel? _gpuStatsHost;
    private StackPanel? _gpuStatsQueuedHost;
    private StackPanel? _gpuDriverStatusHost;
    private StackPanel? _gpuDriverStatusBody;
    private StackPanel? _gpuDriverStatusQueuedHost;
    private Grid? _gpuStatsShell;
    private Border? _gpuDriverCenterTile;
    private DateTime _gpuPageCacheUsedAt;
    private long _gpuPageRevisionStamp;

    private bool TryAttachGpuPageCache(CancellationToken token)
    {
        if (_gpuPageCache == null) return false;
        // A GPU page consists of a few deep WinUI roots. Invalidate their
        // common hosts only once, as on Overview, rather than once per root.
        foreach (var element in _gpuPageCache)
            AttachCachedElement(element, deferHostInvalidation: true);

        if (_gpuStatsHost is { } statsHost)
        {
            // Repair snapshots taken before a rejected GPU stats callback:
            // the host must live immediately after the hero in BOTH the
            // cached root ordering and the current visual tree, not at the
            // bottom under driver cards after a rapid GPU -> other -> GPU.
            if (!_gpuPageCache.Contains(statsHost))
            {
                int heroIndex = _gpuStaticHero == null ? -1 : _gpuPageCache.IndexOf(_gpuStaticHero);
                _gpuPageCache.Insert(heroIndex >= 0 ? heroIndex + 1 : 0, statsHost);
            }

            if (!ContentStack.Children.Contains(statsHost))
                AttachCachedElement(statsHost, deferHostInvalidation: true);
            int target = 0;
            foreach (var root in _gpuPageCache)
            {
                if (ReferenceEquals(root, statsHost)) break;
                if (ContentStack.Children.Contains(root)) target++;
            }
            int current = ContentStack.Children.IndexOf(statsHost);
            if (current >= 0 && current != target)
            {
                ContentStack.Children.RemoveAt(current);
                ContentStack.Children.Insert(target, statsHost);
            }
            if (statsHost.Children.Count == 0)
                ScheduleGpuStatsShell(statsHost, token, _navigationGeneration);
        }

        ContentStack.InvalidateMeasure();
        ContentStack.InvalidateArrange();
        _contentLayerHost?.InvalidateMeasure();
        PageHost?.InvalidateMeasure();
        if (_gpuDriverStatusHost is { Children.Count: 0 } statusHost &&
            _gpuDriverStatusBody is { } statusBody)
            ScheduleGpuDriverStatusSurface(statusHost, statusBody, token, _navigationGeneration);
        if (_gpuTweakHost is { Children.Count: 0 }) ScheduleGpuTweakCards(token);
        if (_gpuDriverCardsHost is { Children.Count: < 2 }) ScheduleGpuDriverCards(token);
        if (_gpuHeroActionsHost is { Children.Count: 0 }) ScheduleGpuHeroActions(_gpuHeroActionsHost, token);
        RefreshCardsIfTweakRevisionChanged(ref _gpuPageRevisionStamp);
        _gpuPageCacheUsedAt = DateTime.UtcNow;
        return true;
    }

    private void CaptureGpuPageCache()
    {
        _gpuPageCache ??= SnapshotChildren(ContentStack);
        _gpuPageCacheUsedAt = DateTime.UtcNow;
        _gpuPageRevisionStamp = CurrentTweakRevisionStamp();
    }

    private void InvalidateGpuPageCache(bool includeStaticShell = false)
    {
        _gpuPageCache = null;
        _gpuTweakHost = null;
        _gpuTweakQueuedHost = null;
        _gpuDriverCardsHost = null;
        _gpuDriverCardsQueuedHost = null;
        if (includeStaticShell)
        {
            _gpuStaticHero = null;
            _gpuHeroActionsHost = null;
            _gpuHeroActionsQueuedHost = null;
        }
        _gpuStatsHost = null;
        _gpuStatsQueuedHost = null;
        _gpuDriverStatusHost = null;
        _gpuDriverStatusBody = null;
        _gpuDriverStatusQueuedHost = null;
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

