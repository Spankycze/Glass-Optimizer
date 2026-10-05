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
    private List<UIElement>? _systemPageCache;
    private TweakVirtualRowViewModel[]? _systemVirtualRowsCache;
    private Dictionary<TweakDefinition, TweakVirtualRowViewModel>? _systemVirtualMapCache;
    private StackPanel? _systemVirtualHost;
    private StackPanel? _systemVirtualQueuedHost;
    private int _systemDeferredAtRootBuild;
    private DateTime _systemPageCacheUsedAt;

    private bool TryAttachSystemPageCache(CancellationToken token)
    {
        if (_systemPageCache == null || _systemVirtualRowsCache == null || _systemVirtualMapCache == null)
            return false;

        foreach (var child in _systemPageCache)
            AttachCachedElement(child);

        _virtualTweakRows = _systemVirtualRowsCache;
        _virtualTweakRowMap = _systemVirtualMapCache;

        _systemPageCacheUsedAt = DateTime.UtcNow;
        RefreshVirtualTweakRows();
        if (_systemVirtualHost is { Children.Count: 0 }) ScheduleSystemVirtualCards(token);
        ScheduleDeferredVirtualGroups("system");
        return true;
    }

    private void ScheduleSystemVirtualCards(CancellationToken token)
    {
        var host = _systemVirtualHost;
        if (host == null || host.Children.Count > 0 || ReferenceEquals(_systemVirtualQueuedHost, host)) return;
        _systemVirtualQueuedHost = host;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(_systemVirtualQueuedHost, host))
                _systemVirtualQueuedHost = null;
            if (token.IsCancellationRequested || _section != "system" || !ReferenceEquals(_systemVirtualHost, host) || host.Children.Count > 0) return;

            if (!_virtualTweakResourcesPrewarmed)
            {
                var resourceSw = System.Diagnostics.Stopwatch.StartNew();
                PrewarmVirtualTweakResources();
                _virtualTweakResourcesPrewarmed = true;
                LogService.Info("performance", $"SYSTEM_RESOURCE_DEFERRED_PREWARM ms={resourceSw.ElapsedMilliseconds}");
                ScheduleSystemVirtualCards(token);
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            BuildVirtualTweakCards(_systemVirtualHost);
            _systemDeferredAtRootBuild = GetDeferredVirtualGroupCount("system");
            CaptureSystemPageCache();
            LogService.Info("performance",
                $"SYSTEM_VIRTUAL_DEFERRED_ROOT ms={sw.ElapsedMilliseconds} deferred={_systemDeferredAtRootBuild}");
        });
    }

    private void CaptureSystemPageCache()
    {
        _systemPageCache = ContentStack.Children.Cast<UIElement>().ToList();
        _systemVirtualRowsCache = _virtualTweakRows;
        _systemVirtualMapCache = new Dictionary<TweakDefinition, TweakVirtualRowViewModel>(_virtualTweakRowMap);
        _systemPageCacheUsedAt = DateTime.UtcNow;
    }

    private void InvalidateSystemPageCache()
    {
        if (ReferenceEquals(_virtualTweakRows, _systemVirtualRowsCache))
            ClearVirtualTweakRows();

        ClearDeferredVirtualGroups("system");
        _systemPageCache = null;
        _systemVirtualRowsCache = null;
        _systemVirtualMapCache = null;
        _systemVirtualHost = null;
        _systemVirtualQueuedHost = null;
        _systemDeferredAtRootBuild = 0;
        _systemPageCacheUsedAt = default;
    }

    internal async System.Threading.Tasks.Task RunSystemDeferredRenderSmokeAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("system");
        long foregroundMs = sw.ElapsedMilliseconds;
        if (foregroundMs > 35)
            throw new InvalidOperationException($"System render-first foreground too slow: {foregroundMs} ms.");

        for (int i = 0; i < 80 && _systemPageCache == null; i++)
            await System.Threading.Tasks.Task.Delay(25);
        var expectedIds = CatalogService.GetTweaks("system").Select(t => t.Id)
            .Concat(new[] { "ui.taskanim", "ui.transparency", "privacy.telemetry", "privacy.adid", "privacy.tailored", "privacy.activity" })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualIds = _systemVirtualRowsCache?.Select(row => row.Source?.Id ?? "").ToArray();
        if (_systemPageCache == null || actualIds == null || actualIds.Length != expectedIds.Count ||
            !expectedIds.SetEquals(actualIds))
            throw new InvalidOperationException($"System deferred render cache coverage mismatch: expected={expectedIds.Count} actual={actualIds?.Length ?? 0}.");
        if (_systemDeferredAtRootBuild < 20)
            throw new InvalidOperationException($"System deferred root queued too little work: {_systemDeferredAtRootBuild} groups.");

        for (int i = 0; i < 80 && GetDeferredVirtualGroupCount("system") > 0; i++)
            await System.Threading.Tasks.Task.Delay(25);
        int remaining = GetDeferredVirtualGroupCount("system");
        if (remaining != 0)
            throw new InvalidOperationException($"System deferred virtual groups incomplete: {remaining} remaining.");
        if (_systemPageCache.Count != 1)
            throw new InvalidOperationException($"System single-root cache mismatch: {_systemPageCache.Count} roots.");

        LogService.Info("performance",
            $"SYSTEM_DEFERRED_SMOKE_OK foregroundMs={foregroundMs} deferredAtRoot={_systemDeferredAtRootBuild} singleRoot=1");
    }
}

