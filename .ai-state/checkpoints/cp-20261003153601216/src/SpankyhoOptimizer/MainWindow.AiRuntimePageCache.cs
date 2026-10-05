using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _aiRuntimePageCache;
    private DateTime _aiRuntimePageCacheAtUtc;

    private bool TryAttachAiRuntimePageCache()
    {
        // Interactive Voice AI controls must always be rebuilt fresh.
        // Reattaching cached WinUI input controls can leave stale focus/hit-test state.
        InvalidateAiRuntimePageCache();
        return false;
    }

    private void CaptureAiRuntimePageCache()
    {
        _aiRuntimePageCache = ContentStack.Children.Cast<UIElement>().ToList();
        _aiRuntimePageCacheAtUtc = DateTime.UtcNow;
    }

    private void InvalidateAiRuntimePageCache()
    {
        _aiRuntimePageCache = null;
        _aiRuntimePageCacheAtUtc = DateTime.MinValue;
    }

    private Task RefreshAiRuntimePageAsync()
    {
        InvalidateAiRuntimePageCache();
        return ShowSectionAsync("ai-runtime");
    }
}

public sealed partial class MainWindow
{
    internal async Task RunAiRuntimePageCacheSmokeAsync()
    {
        InvalidateAiRuntimePageCache();
        await ShowSectionAsync("ai-runtime");
        var first = _aiRuntimePageCache
            ?? throw new InvalidOperationException("AI Runtime page snapshot was not captured.");

        await ShowSectionAsync("overview");
        await ShowSectionAsync("ai-runtime");

        var second = _aiRuntimePageCache
            ?? throw new InvalidOperationException("AI Runtime fresh page snapshot was not captured.");

        if (ReferenceEquals(first, second))
            throw new InvalidOperationException("AI Runtime reused cached interactive controls; fresh rebuild is required.");

        LogService.Info("performance", "AI_RUNTIME_PAGE_FRESH_REBUILD_SMOKE_OK interactiveControlsFresh=true cacheReuse=false");
    }
}
