using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _debloatPageCache;
    private DateTime _debloatPageCacheAt;

    private bool TryAttachDebloatPageCache()
    {
        if (_debloatPageCache == null || DateTime.UtcNow - _debloatPageCacheAt > TimeSpan.FromSeconds(60))
            return false;
        foreach (var element in _debloatPageCache)
            AttachCachedElement(element);
        return true;
    }

    private void CaptureDebloatPageCache()
    {
        _debloatPageCache = SnapshotChildren(ContentStack);
        _debloatPageCacheAt = DateTime.UtcNow;
    }

    private void InvalidateDebloatPageCache()
    {
        _debloatPageCache = null;
        _debloatRowsCache = null;
        _debloatRowsSnapshot = null;
        _debloatPageCacheAt = default;
    }
}

