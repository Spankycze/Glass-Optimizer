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
    // Diagnostics for the cold first-System materialization: how many Low
    // priority dispatcher turns were consumed and when the virtual cards
    // actually became available. Read-only instrumentation, no UI effect.
    private int _systemDeferredDispatchTurns;
    private long _systemDeferredQueuedTimestamp;
    private long _systemDeferredReadyTimestamp;

    private bool TryAttachSystemPageCache(CancellationToken token)
    {
        if (_systemPageCache == null || _systemVirtualRowsCache == null || _systemVirtualMapCache == null)
            return false;

        // A System page is many shallow WinUI roots. Invalidate their common
        // hosts only once, as on Overview and GPU, rather than once per root.
        foreach (var child in _systemPageCache)
            AttachCachedElement(child, deferHostInvalidation: true);
        ContentStack.InvalidateMeasure();
        ContentStack.InvalidateArrange();
        _contentLayerHost?.InvalidateMeasure();
        PageHost?.InvalidateMeasure();

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
        int generation = _navigationGeneration;
        if (_systemPageCache == null && _systemDeferredQueuedTimestamp == 0)
        {
            // One cold build may legitimately span two dispatcher turns
            // (resources, then catalog + cards). Keep its original start stamp
            // across that re-queue; only a new cold build starts a new sample.
            _systemDeferredDispatchTurns = 0;
            _systemDeferredReadyTimestamp = 0;
            _systemDeferredQueuedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        // Normal, not Low: the measured cold-start queue behind the deferred
        // titlebar/sidebar/window-setup work starved this content build for
        // ~115-123 ms (Debug, 3 runs). Normal priority keeps the user-requested
        // System cards at ~30-35 ms queue wait while the remaining shell
        // cosmetics still finish a few ms later. The work itself is unchanged.
        bool queued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (ReferenceEquals(_systemVirtualQueuedHost, host))
                _systemVirtualQueuedHost = null;
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration ||
                _section != "system" ||
                !ReferenceEquals(_systemVirtualHost, host) ||
                host.Children.Count > 0)
            {
                // A rejected/stale turn must not clobber the timing of a newer
                // navigation that already queued its own build.
                if (_systemVirtualQueuedHost == null)
                    _systemDeferredQueuedTimestamp = 0;
                return;
            }

            _systemDeferredDispatchTurns++;
            long turnStartTs = System.Diagnostics.Stopwatch.GetTimestamp();

            // Navigation intentionally does not attach this empty host. Do
            // the native XAML tree insertion in this deferred turn immediately
            // before deferred resources/cards, under the same identity and
            // cancellation checks used for subsequent cache capture.
            if (!ContentStack.Children.Contains(host))
                ContentStack.Children.Add(host);

            // Cold first System visit has no warmed templates yet. Warm the
            // cheap template/icon set in this turn, then re-queue the heavy
            // catalog + 26-group card build into its own turn. Keeping those
            // two stages separate avoids one long callback that would block
            // the shell work interleaved right after startup.
            if (!_virtualTweakResourcesPrewarmed)
            {
                var resourceSw = System.Diagnostics.Stopwatch.StartNew();
                PrewarmVirtualTweakResources();
                _virtualTweakResourcesPrewarmed = true;
                LogService.Info("performance",
                    $"SYSTEM_RESOURCE_DEFERRED_PREWARM ms={resourceSw.ElapsedMilliseconds}");
                ScheduleSystemVirtualCards(token);
                return;
            }

            // First-ever System page has no catalog yet. The navigation
            // attaches an empty host immediately and leaves the definition
            // factory for THIS already deferred UI turn, eliminating cold
            // CatalogService type initialization from the first-paint gate.
            // No registry, driver or user setting is read or changed here.
            bool materializedCatalog = false;
            if (ReferenceEquals(_tweaks, EmptyComponentTweaks) &&
                !_tweakCache.ContainsKey("system|"))
            {
                try
                {
                    var definitions = CatalogService.GetTweaks("system");
                    if (token.IsCancellationRequested || generation != _navigationGeneration ||
                        _section != "system" || !ReferenceEquals(_systemVirtualHost, host))
                        return;
                    _tweakCache["system|"] = definitions;
                    _tweaks = GlassSystemTweaks(definitions);
                    materializedCatalog = true;
                }
                catch (Exception ex)
                {
                    LogService.Error("Deferred System catalog materialization failed", ex);
                    return;
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            BuildVirtualTweakCards(_systemVirtualHost);
            _systemDeferredAtRootBuild = GetDeferredVirtualGroupCount("system");
            CaptureSystemPageCache();
            long readyTs = System.Diagnostics.Stopwatch.GetTimestamp();
            _systemDeferredReadyTimestamp = readyTs;
            long queueWaitMs = _systemDeferredQueuedTimestamp == 0
                ? -1
                : (long)System.Diagnostics.Stopwatch.GetElapsedTime(_systemDeferredQueuedTimestamp, turnStartTs).TotalMilliseconds;
            long totalReadyMs = _systemDeferredQueuedTimestamp == 0
                ? -1
                : (long)System.Diagnostics.Stopwatch.GetElapsedTime(_systemDeferredQueuedTimestamp, readyTs).TotalMilliseconds;
            _systemDeferredQueuedTimestamp = 0;
            if (materializedCatalog)
            {
                const string systemCacheKey = "system|";
                bool stale = !_tweakCacheTime.TryGetValue(systemCacheKey, out var detectedAt) ||
                    DateTime.UtcNow - detectedAt > TimeSpan.FromSeconds(90);
                ScheduleRenderFirstBackground("system", systemCacheKey, _tweaks.ToArray(), stale, token);
            }
            LogService.Info("performance",
                $"SYSTEM_VIRTUAL_DEFERRED_ROOT ms={sw.ElapsedMilliseconds} deferred={_systemDeferredAtRootBuild} catalogDeferred={materializedCatalog} turns={_systemDeferredDispatchTurns} queueWaitMs={queueWaitMs} totalReadyMs={totalReadyMs}");
        });
        // A shutting-down dispatcher may reject the callback. Never leave
        // the host marked as queued, otherwise later navigation cannot retry.
        if (!queued && ReferenceEquals(_systemVirtualQueuedHost, host))
            _systemVirtualQueuedHost = null;
    }

    private void CaptureSystemPageCache()
    {
        _systemPageCache = SnapshotChildren(ContentStack);
        _systemVirtualRowsCache = _virtualTweakRows;
        _systemVirtualMapCache = new Dictionary<TweakDefinition, TweakVirtualRowViewModel>(_virtualTweakRowMap);
        _systemPageCacheUsedAt = DateTime.UtcNow;
    }

    private void InvalidateSystemPageCache()
    {
        if (ReferenceEquals(_virtualTweakRows, _systemVirtualRowsCache))
            ClearVirtualTweakRows();

        // Invalidation must also release a hidden pinned XAML subtree, or its
        // native visuals would outlive the cache TTL / memory-pressure trim.
        var pinned = _persistentSystemHost;
        _persistentSystemHost = null;
        if (pinned != null && !ReferenceEquals(pinned, ContentStack))
        {
            if (_contentLayerHost.Children.Contains(pinned))
                _contentLayerHost.Children.Remove(pinned);
            pinned.Children.Clear();
        }

        ClearDeferredVirtualGroups("system");
        _systemPageCache = null;
        _systemVirtualRowsCache = null;
        _systemVirtualMapCache = null;
        _systemVirtualHost = null;
        _systemVirtualQueuedHost = null;
        _systemDeferredAtRootBuild = 0;
        _systemPageCacheUsedAt = default;
        // Timing diagnostics belong to the last real cold build. Do not clear
        // them here: an unrelated invalidation (theme resources after first
        // render) may run between scheduling and the deferred callback and
        // must not erase the measurement of the build that is still pending.
    }

    // Count rendered UI groups, rather than catalog VMs or queued work.
    private int CountAttachedSystemGroups()
    {
        if (_systemVirtualHost?.Children.OfType<StackPanel>().FirstOrDefault() is not StackPanel sectionRoot)
            return 0;
        if (sectionRoot.Children.OfType<Grid>().FirstOrDefault(g => Equals(g.Tag, "glass-groups")) is Grid groupGrid &&
            groupGrid.Children.Count >= 2 &&
            groupGrid.Children[0] is Panel left &&
            groupGrid.Children[1] is Panel right)
            return left.Children.Count + right.Children.Count;
        return Math.Max(0, sectionRoot.Children.Count - 1); // dashboard intro
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
            .Concat(CatalogService.GetGlassSystemSupplementTweaks().Select(t => t.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualIds = _systemVirtualRowsCache?.Select(row => row.Source?.Id ?? "").ToArray();
        if (_systemPageCache == null || actualIds == null || actualIds.Length != expectedIds.Count ||
            !expectedIds.SetEquals(actualIds))
            throw new InvalidOperationException($"System deferred render cache coverage mismatch: expected={expectedIds.Count} actual={actualIds?.Length ?? 0}.");
        int expectedGroups = _darkTheme
            ? _tweaks.Select(GlassSystemGroup).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            : _tweaks.Select(x => x.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int immediatelyRendered = ImmediateVirtualGroupCount("system");
        if (_systemDeferredAtRootBuild != expectedGroups - immediatelyRendered)
            throw new InvalidOperationException(
                $"System first-paint coverage mismatch: groups={expectedGroups} immediate={immediatelyRendered} queued={_systemDeferredAtRootBuild}.");
        int initialAttachedGroups = CountAttachedSystemGroups();
        if (initialAttachedGroups < immediatelyRendered)
            throw new InvalidOperationException(
                $"System first page root is too sparse: expected at least {immediatelyRendered}, found {initialAttachedGroups} groups.");

        for (int i = 0; i < 80 && GetDeferredVirtualGroupCount("system") > 0; i++)
            await System.Threading.Tasks.Task.Delay(25);
        int remaining = GetDeferredVirtualGroupCount("system");
        if (remaining != 0)
            throw new InvalidOperationException($"System deferred virtual groups incomplete: {remaining} remaining.");
        // Models alone do not prove the user can see the settings: verify the
        // attached UI groups once the deferred queue has fully drained.
        int visibleGroups = CountAttachedSystemGroups();
        if (visibleGroups != expectedGroups)
            throw new InvalidOperationException(
                $"System displayed groups mismatch: expected={expectedGroups} actual={visibleGroups}.");
        LogService.Info("performance", $"SYSTEM_GROUP_COVERAGE_SMOKE_OK tweaks={actualIds.Length} groups={visibleGroups} pending={remaining}");
        if (_systemPageCache.Count != 1)
            throw new InvalidOperationException($"System single-root cache mismatch: {_systemPageCache.Count} roots.");

        int capturedDeferredGroups = _systemDeferredAtRootBuild;
        var cachedRoot = _systemPageCache[0];
        var originalHost = ContentStack;
        await ShowSectionAsync("settings");
        if (!ReferenceEquals(_persistentSystemHost, originalHost) ||
            originalHost.Visibility != Visibility.Collapsed ||
            !ReferenceEquals(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cachedRoot), originalHost))
            throw new InvalidOperationException("System cached tree was detached instead of parked.");
        await ShowSectionAsync("system");
        if (!ReferenceEquals(ContentStack, originalHost) ||
            originalHost.Visibility != Visibility.Visible ||
            !ReferenceEquals(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cachedRoot), originalHost))
            throw new InvalidOperationException("System cached tree was not restored in its original host.");
        await ShowSectionAsync("settings");
        InvalidateSystemPageCache();
        if (_persistentSystemHost != null || _contentLayerHost.Children.Contains(originalHost))
            throw new InvalidOperationException("Expired pinned System tree remained in the XAML visual layer.");

        LogService.Info("performance",
            $"SYSTEM_DEFERRED_SMOKE_OK foregroundMs={foregroundMs} deferredAtRoot={capturedDeferredGroups} singleRoot=1 pinnedHost=PASS deferredTurns={_systemDeferredDispatchTurns}");
    }
}

