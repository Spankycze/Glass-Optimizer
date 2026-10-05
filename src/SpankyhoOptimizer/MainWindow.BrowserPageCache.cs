using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private Dictionary<string, List<UIElement>>? _browserPageCachesStore;
    private Dictionary<string, DateTime>? _browserPageCacheAtStore;
    private Dictionary<string, long>? _browserPageRevisionStampStore;
    private Dictionary<string, TweakVirtualRowViewModel[]>? _browserVirtualRowsCacheStore;
    private Dictionary<string, Dictionary<TweakDefinition, TweakVirtualRowViewModel>>? _browserVirtualMapCacheStore;

    private Dictionary<string, List<UIElement>> _browserPageCaches =>
        _browserPageCachesStore ??= new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTime> _browserPageCacheAt =>
        _browserPageCacheAtStore ??= new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, long> _browserPageRevisionStamp =>
        _browserPageRevisionStampStore ??= new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TweakVirtualRowViewModel[]> _browserVirtualRowsCache =>
        _browserVirtualRowsCacheStore ??= new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Dictionary<TweakDefinition, TweakVirtualRowViewModel>> _browserVirtualMapCache =>
        _browserVirtualMapCacheStore ??= new(StringComparer.OrdinalIgnoreCase);
    private const int BrowserPageCacheLimit = 7;

    private bool TryAttachBrowserPageCache(string sub)
    {
        if (!_browserPageCaches.TryGetValue(sub, out var cache)) return false;
        _browserPageCacheAt[sub] = DateTime.UtcNow;
        foreach (var item in cache) AttachCachedElement(item);

        if (_browserVirtualRowsCache.TryGetValue(sub, out var virtualRows) &&
            _browserVirtualMapCache.TryGetValue(sub, out var virtualMap))
        {
            _virtualTweakRows = virtualRows;
            _virtualTweakRowMap = virtualMap;
        }

        long stamp = _browserPageRevisionStamp.TryGetValue(sub, out var cachedStamp) ? cachedStamp : long.MinValue;
        if (RefreshCardsIfTweakRevisionChanged(ref stamp))
            _browserPageRevisionStamp[sub] = stamp;
        return true;
    }

    private void CaptureBrowserPageCache(string sub)
    {
        _browserPageCaches[sub] = ContentStack.Children.Cast<UIElement>().ToList();
        _browserPageCacheAt[sub] = DateTime.UtcNow;
        _browserPageRevisionStamp[sub] = CurrentTweakRevisionStamp();
        if (_virtualTweakRows != null)
        {
            _browserVirtualRowsCache[sub] = _virtualTweakRows;
            // Cache owns this map instance. ClearVirtualTweakRows detaches the active page
            // instead of mutating it, so reattach can restore the exact same map by reference.
            _browserVirtualMapCache[sub] = _virtualTweakRowMap;
        }
        else
        {
            _browserVirtualRowsCache.Remove(sub);
            _browserVirtualMapCache.Remove(sub);
        }

        while (_browserPageCaches.Count > BrowserPageCacheLimit)
        {
            string? oldest = _browserPageCacheAt.OrderBy(x => x.Value).Select(x => x.Key).FirstOrDefault();
            if (oldest == null) break;
            _browserPageCaches.Remove(oldest);
            _browserPageCacheAt.Remove(oldest);
            _browserPageRevisionStamp.Remove(oldest);
            _browserVirtualRowsCache.Remove(oldest);
            _browserVirtualMapCache.Remove(oldest);
        }
    }

    private void InvalidateBrowserPageCache(string? sub = null)
    {
        if (string.IsNullOrWhiteSpace(sub))
        {
            _browserPageCaches.Clear();
            _browserPageCacheAt.Clear();
            _browserPageRevisionStamp.Clear();
            _browserVirtualRowsCache.Clear();
            _browserVirtualMapCache.Clear();
        }
        else
        {
            _browserPageCaches.Remove(sub);
            _browserPageCacheAt.Remove(sub);
            _browserPageRevisionStamp.Remove(sub);
            _browserVirtualRowsCache.Remove(sub);
            _browserVirtualMapCache.Remove(sub);
        }
    }

    private void TrimExpiredBrowserPageCaches(DateTime now)
    {
        foreach (string sub in _browserPageCacheAt.Where(x => now - x.Value > TimeSpan.FromMinutes(5)).Select(x => x.Key).ToArray())
            InvalidateBrowserPageCache(sub);
    }

    internal async System.Threading.Tasks.Task RunBrowserCacheSmokeAsync()
    {
        string[] subs = { "Firefox", "Chrome", "Edge", "Brave", "Opera", "Opera GX", "Vivaldi" };
        InvalidateBrowserPageCache();
        var first = System.Diagnostics.Stopwatch.StartNew();
        foreach (string sub in subs)
        {
            _sub = sub;
            await ShowSectionAsync("browsers");

            int tweakCount = CatalogService.GetTweaks("browsers", sub).Count;
            bool hasVirtualRows = _browserVirtualRowsCache.TryGetValue(sub, out var rows);
            bool hasVirtualMap = _browserVirtualMapCache.TryGetValue(sub, out var map);
            if (!hasVirtualRows || !hasVirtualMap || rows!.Length != tweakCount || map!.Count != tweakCount)
                throw new InvalidOperationException($"Browser virtual cache coverage mismatch for {sub}: tweaks={tweakCount} rows={rows?.Length ?? 0} map={map?.Count ?? 0}.");
        }
        first.Stop();
        if (_browserPageCaches.Count != subs.Length)
            throw new InvalidOperationException($"Browser page cache coverage mismatch: {_browserPageCaches.Count}/{subs.Length}.");

        foreach (string sub in subs) _browserPageCacheAt[sub] = DateTime.UtcNow - TimeSpan.FromSeconds(90);
        TrimExpiredBrowserPageCaches(DateTime.UtcNow);
        if (_browserPageCaches.Count != subs.Length)
            throw new InvalidOperationException("Browser page cache expired before the 5-minute last-used TTL.");

        var second = System.Diagnostics.Stopwatch.StartNew();
        foreach (string sub in subs)
        {
            TweakVirtualRowViewModel[]? cachedRows = _browserVirtualRowsCache.TryGetValue(sub, out var rows) ? rows : null;
            Dictionary<TweakDefinition, TweakVirtualRowViewModel>? cachedMap = _browserVirtualMapCache.TryGetValue(sub, out var map) ? map : null;
            _sub = sub;
            // The previous iteration already left us on the browsers section. Force the
            // sub-page navigation path so TryAttachBrowserPageCache(sub) actually runs;
            // same-section fast-path must not turn this cache smoke into a false failure.
            _section = "overview";
            await ShowSectionAsync("browsers");
            if (cachedRows != null && (!ReferenceEquals(_virtualTweakRows, cachedRows) || !ReferenceEquals(_virtualTweakRowMap, cachedMap)))
                throw new InvalidOperationException($"Browser virtual cache was not reattached by reference for {sub}.");
        }
        second.Stop();
        LogService.Info("performance", $"BROWSER_CACHE_SMOKE caches={_browserPageCaches.Count} virtualCaches={_browserVirtualRowsCache.Count} firstMs={first.ElapsedMilliseconds} secondMs={second.ElapsedMilliseconds}");
        LogService.Info("BROWSER_CACHE_SMOKE_OK");
    }
}
