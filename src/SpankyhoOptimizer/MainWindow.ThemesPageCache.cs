using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // Short-lived reuse avoids repeatedly constructing two 2048x1152 bitmap
    // thumbnails and the entire Themes visual tree while tabbing between pages.
    // A short TTL also bounds stale data after external Windows wallpaper changes.
    private static readonly TimeSpan ThemesPageCacheTtl = TimeSpan.FromSeconds(8);
    private List<UIElement>? _themesPageCache;
    private DateTime _themesPageCacheAtUtc;
    // Every cache generation owns at most one expiry timer. An older timer
    // must never release a newer Themes visual tree after navigation.
    private int _themesPageCacheEpoch;
    private CancellationTokenSource? _themesCacheExpiryCts;

    private bool TryAttachThemesPageCache()
    {
        if (_themesPageCache is not { Count: > 0 })
            return false;
        DateTime now = DateTime.UtcNow;
        if (!ThemesCacheLifetimePolicy.IsFresh(_themesPageCacheAtUtc, now, ThemesPageCacheTtl))
        {
            InvalidateThemesPageCache();
            return false;
        }

        foreach (UIElement element in _themesPageCache)
            AttachCachedElement(element, deferHostInvalidation: true);
        ContentStack.InvalidateMeasure();
        ContentStack.InvalidateArrange();
        _contentLayerHost?.InvalidateMeasure();
        PageHost?.InvalidateMeasure();
        return true;
    }

    private void CaptureThemesPageCache()
    {
        _themesPageCache = SnapshotChildren(ContentStack);
        _themesPageCacheAtUtc = DateTime.UtcNow;
        // Cancel obsolete deadlines rather than accumulating eight-second Tasks
        // if a wallpaper/theme change forces repeated cache rebuilding.
        _themesCacheExpiryCts?.Cancel();
        _themesCacheExpiryCts?.Dispose();
        _themesCacheExpiryCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
        int epoch = ++_themesPageCacheEpoch;
        SafeTaskService.Forget(ExpireThemesPageCacheAsync(epoch, _themesCacheExpiryCts.Token),
            "themes", "Bounded Themes cache expiry");
    }

    private async Task ExpireThemesPageCacheAsync(int epoch, CancellationToken token)
    {
        try
        {
            await Task.Delay(ThemesPageCacheTtl, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }

        if (token.IsCancellationRequested || _windowLifetimeCts.IsCancellationRequested) return;
        // Native WinUI references must be released on the owning UI thread.
        // This also expires an inactive cache if navigation has stopped entirely.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ExpireThemesCacheForEpoch(epoch));
    }

    private void ExpireThemesCacheForEpoch(int epoch)
    {
        if (!ThemesCacheLifetimePolicy.CanExpire(
            epoch, _themesPageCacheEpoch, _windowLifetimeCts.IsCancellationRequested))
            return;
        InvalidateThemesPageCache();
    }

    private void InvalidateThemesPageCache()
    {
        ++_themesPageCacheEpoch;
        _themesCacheExpiryCts?.Cancel();
        _themesCacheExpiryCts?.Dispose();
        _themesCacheExpiryCts = null;
        _themesPageCache = null;
        _themesPageCacheAtUtc = default;
    }

    internal async System.Threading.Tasks.Task RunThemesPageCacheSmokeAsync()
    {
        // Read-only UIA-style navigation: don't apply a wallpaper or change Windows state.
        InvalidateThemesPageCache();
        await ShowSectionAsync("themes");
        // Both deferred cards and both bounded previews must be visible and
        // decoded before testing cache reuse, not merely present in source.
        bool wallpaperPreviewsReady = false;
        for (int attempt = 0; attempt < 35; attempt++)
        {
            Grid? cards = FindThemeGrid(ContentStack, "ThemeWallpaperCards");
            Grid? hub = FindThemeGrid(ContentStack, "ThemeHubCards");
            if (cards != null && cards.Children.Count == 2 && hub?.Children.Count == 4 && ThemesWallpaperPreviewsReady(cards))
            {
                wallpaperPreviewsReady = true;
                break;
            }
            await Task.Delay(25);
        }
        if (!wallpaperPreviewsReady)
            throw new InvalidOperationException("Deferred Themes hub or wallpaper cards/previews did not materialize.");
        if (_themesPageCache is not { Count: > 0 })
            throw new InvalidOperationException("Themes page was not captured.");
        var firstRoot = _themesPageCache[0];
        CancellationToken firstDeadlineToken = _themesCacheExpiryCts?.Token ??
            throw new InvalidOperationException("Themes deadline not scheduled for newly cached page.");
        await ShowSectionAsync("settings");
        await ShowSectionAsync("themes");
        if (_themesPageCache is not { Count: > 0 } ||
            !ReferenceEquals(_themesPageCache[0], firstRoot) ||
            !ReferenceEquals(ContentStack.Children[0], firstRoot))
            throw new InvalidOperationException("Themes page cache failed to reuse the visual tree.");

        // Repeated clicks on the already active Themes page must not rebuild
        // WinUI wrappers or advance the navigation generation while TTL is fresh.
        int sameSectionGeneration = _navigationGeneration;
        for (int i = 0; i < 30; i++)
            await ShowSectionAsync("themes");
        if (_navigationGeneration != sameSectionGeneration ||
            !ReferenceEquals(ContentStack.Children[0], firstRoot))
            throw new InvalidOperationException("Themes same-section fast path rebuilt the visual tree.");

        // Expire without sleeping or touching any user configuration.
        _themesPageCacheAtUtc = DateTime.UtcNow - ThemesPageCacheTtl - TimeSpan.FromSeconds(1);
        await ShowSectionAsync("settings");
        await ShowSectionAsync("themes");
        if (_themesPageCache is not { Count: > 0 } ||
            ReferenceEquals(_themesPageCache[0], firstRoot))
            throw new InvalidOperationException("Expired Themes page was not rebuilt.");
        if (!firstDeadlineToken.IsCancellationRequested)
            throw new InvalidOperationException("Rebuilding Themes failed to cancel its previous deadline.");

        // A queued expiry from the earlier tree must not invalidate this new one.
        int activeEpoch = _themesPageCacheEpoch;
        ExpireThemesCacheForEpoch(activeEpoch - 1);
        if (_themesPageCache is not { Count: > 0 } ||
            _themesCacheExpiryCts is null ||
            _themesCacheExpiryCts.IsCancellationRequested)
            throw new InvalidOperationException("Older Themes expiry removed or canceled a newer cached tree.");

        // No new navigation: the timeout must still relinquish its WinUI references.
        // Give a busy UI dispatcher a bounded chance to process its low-priority expiry.
        await ShowSectionAsync("settings");
        await Task.Delay(ThemesPageCacheTtl);
        for (int i = 0; i < 20 && _themesPageCache != null; i++)
            await Task.Delay(100);
        if (_themesPageCache != null || _themesCacheExpiryCts != null)
            throw new InvalidOperationException("Inactive Themes cache retained its tree or expiry token after deadline.");

        InvalidateThemesPageCache();
        LogService.Info("theme", "THEMES_PAGE_CACHE_SMOKE_OK hubCards=4 previews=2 decodeWidth=1024");
    }

    private static Grid? FindThemeGrid(DependencyObject root, string name)
    {
        if (root is Grid grid && grid.Name == name) return grid;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            Grid? found = FindThemeGrid(VisualTreeHelper.GetChild(root, i), name);
            if (found != null) return found;
        }
        return null;
    }

    private static bool ThemesWallpaperPreviewsReady(Grid cards)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(cards);
        int ready = 0;
        while (pending.Count != 0)
        {
            DependencyObject element = pending.Pop();
            if (element is Image image && image.Name.StartsWith("ThemeWallpaperPreview_", StringComparison.Ordinal))
            {
                if (image.Source is not BitmapImage bitmap || bitmap.DecodePixelWidth != 1024)
                    return false;
                ready++;
            }
            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++)
                pending.Push(VisualTreeHelper.GetChild(element, i));
        }
        return ready == 2;
    }
}
