using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _defenderPageCache;
    private DateTime _defenderPageCacheAt;
    private long _defenderPageRevisionStamp;

    private Border? _defenderStaticHero;
    private Grid? _defenderStaticMetrics;
    private bool? _defenderStaticTamper;
    private TextBlock? _defenderTamperValue;
    private TextBlock? _defenderTamperDetail;
    private TextBlock? _defenderModulesValue;
    private TextBlock? _defenderReadBackValue;
    private TextBlock? _defenderActiveValue;

    private bool TryAttachDefenderPageCache()
    {
        if (_defenderPageCache == null || DateTime.UtcNow - _defenderPageCacheAt > TimeSpan.FromSeconds(20))
            return false;
        foreach (var element in _defenderPageCache) AttachCachedElement(element);
        RefreshCardsIfTweakRevisionChanged(ref _defenderPageRevisionStamp);
        return true;
    }

    private void CaptureDefenderPageCache()
    {
        _defenderPageCache = ContentStack.Children.Cast<UIElement>().ToList();
        _defenderPageCacheAt = DateTime.UtcNow;
        _defenderPageRevisionStamp = CurrentTweakRevisionStamp();
    }

    private void InvalidateDefenderPageCache(bool includeStaticShell = false)
    {
        _defenderPageCache = null;
        _defenderPageCacheAt = default;
        _defenderPageRevisionStamp = 0;
        _defenderModulesHost = null;
        _defenderModulesQueued = false;
        _defenderModulesIndex = 0;
        _defenderActionsHost = null;
        _defenderActionsQueued = false;

        if (includeStaticShell)
        {
            _defenderStaticHero = null;
            _defenderStaticMetrics = null;
            _defenderStaticTamper = null;
            _defenderTamperValue = null;
            _defenderTamperDetail = null;
            _defenderModulesValue = null;
            _defenderReadBackValue = null;
            _defenderActiveValue = null;
        }
    }
}

