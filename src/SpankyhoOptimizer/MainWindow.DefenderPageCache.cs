using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _defenderPageCache;
    private DateTime _defenderPageCacheAt;
    private long _defenderPageRevisionStamp;
    private bool? _defenderPageCacheTamper;

    private Border? _defenderStaticHero;
    private Border? _defenderStaticStatus;
    private bool? _defenderStaticTamper;
    private TextBlock? _defenderStatusSummary;
    private readonly Dictionary<string, TextBlock> _defenderStatusRows = new(StringComparer.Ordinal);

    private Border? _defenderModulesCard;
    private Border? _defenderActionsCard;

    private bool TryAttachDefenderPageCache(bool tamper)
    {
        if (_defenderPageCache == null || DateTime.UtcNow - _defenderPageCacheAt > TimeSpan.FromSeconds(20))
            return false;
        // Cached actions were built for a specific Tamper Protection state and a
        // specific read-back revision; attaching either stale variant would show
        // wrong status rows or a disable action that can no longer work.
        if (_defenderPageCacheTamper != tamper ||
            _defenderPageRevisionStamp != CurrentTweakRevisionStamp())
        {
            InvalidateDefenderPageCache();
            return false;
        }
        foreach (var element in _defenderPageCache) AttachCachedElement(element);
        return true;
    }

    private void CaptureDefenderPageCache(bool tamper)
    {
        _defenderPageCache = SnapshotChildren(ContentStack);
        _defenderPageCacheAt = DateTime.UtcNow;
        _defenderPageRevisionStamp = CurrentTweakRevisionStamp();
        _defenderPageCacheTamper = tamper;
    }

    private void InvalidateDefenderPageCache(bool includeStaticShell = false)
    {
        _defenderPageCache = null;
        _defenderPageCacheAt = default;
        _defenderPageRevisionStamp = 0;
        _defenderPageCacheTamper = null;
        _defenderModulesHost = null;
        _defenderModulesQueued = false;
        _defenderModulesIndex = 0;
        _defenderActionsHost = null;
        _defenderActionsQueued = false;
        _defenderModulesCard = null;
        _defenderActionsCard = null;

        if (includeStaticShell)
        {
            _defenderStaticHero = null;
            _defenderStaticStatus = null;
            _defenderStaticTamper = null;
            _defenderStatusSummary = null;
            _defenderStatusRows.Clear();
        }
    }
}
