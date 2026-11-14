using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;
using WinRT.Interop;
using Windows.Graphics;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow : Window
{
    private void ThrowIfNavigationStale(int generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration)
            throw new OperationCanceledException(token);
    }

    private void AttachCachedElement(UIElement element, bool deferHostInvalidation = false)
    {
        DependencyObject? currentParent = VisualTreeHelper.GetParent(element);
        if (ReferenceEquals(currentParent, ContentStack))
            return;

        if (currentParent is Panel parent)
        {
            // A direct parent already owns the child; avoid scanning detached hosts.
            parent.Children.Remove(element);
            _detachedNavigationOwners.Remove(element);
        }
        else
        {
            // Detached cached elements are indexed by their previous host so reattach stays O(1).
            if (_detachedNavigationOwners.TryGetValue(element, out var detachedHost))
            {
                detachedHost.Children.Remove(element);
                _detachedNavigationOwners.Remove(element);
            }
            else
            {
                // Fallback for legacy/unindexed hosts.
                foreach (var legacyHost in _detachedNavigationHosts)
                {
                    if (!legacyHost.Children.Contains(element))
                        continue;

                    legacyHost.Children.Remove(element);
                    break;
                }
            }
        }

        ContentStack.Children.Add(element);

        // Cached WinUI elements can retain their previous measure slot after they are detached
        // from a taller page. Force a fresh measure so a short page (notably Overview) cannot
        // inherit the previous section's height and falsely enable scrolling.
        element.InvalidateMeasure();
        element.InvalidateArrange();
        if (!deferHostInvalidation)
        {
            ContentStack.InvalidateMeasure();
            ContentStack.InvalidateArrange();
            _contentLayerHost?.InvalidateMeasure();
            PageHost?.InvalidateMeasure();
        }
    }

    private void QueueDetachedNavigationHostCleanup(StackPanel host)
    {
        if (host.Children.Count == 0)
            return;

        // Hide old, non-interactive content immediately so a hidden heavy page
        // cannot participate in layout/composition while low-priority cleanup
        // incrementally removes its children. Cached roots still reparent safely.
        if (host.Visibility != Visibility.Collapsed)
            host.Visibility = Visibility.Collapsed;
        host.IsHitTestVisible = false;
        // Keep detach O(1): cached elements can reattach through the existing queue fallback.
        // Eagerly indexing every child here made large Startup pages charge their teardown cost
        // to the next section's foreground navigation.
        _detachedNavigationHosts.Enqueue(host);
        ScheduleDetachedNavigationHostCleanup(delayFirstFrame: true);
    }

    // Do not let a continuous stream of navigation events starve native
    // WinUI wrapper cleanup indefinitely. Always keep teardown on the
    // low-priority dispatcher; only bound the initial debounce.
    internal static bool ShouldBeginDetachedHostCleanup(TimeSpan idle, TimeSpan queuedFor, int pendingHosts)
        => idle >= TimeSpan.FromMilliseconds(140)
            || queuedFor >= TimeSpan.FromMilliseconds(420)
            || (pendingHosts >= 4 && queuedFor >= TimeSpan.FromMilliseconds(120));

    private async Task DelayDetachedNavigationHostCleanupAsync(int pendingHostsAtSchedule)
    {
        try
        {
            var queuedAt = System.Diagnostics.Stopwatch.StartNew();
            while (!_windowLifetimeCts.IsCancellationRequested)
            {
                TimeSpan idleFor = DateTime.UtcNow - _lastNavigationUtc;
                if (ShouldBeginDetachedHostCleanup(idleFor, queuedAt.Elapsed, pendingHostsAtSchedule))
                    break;

                TimeSpan remainingQuiet = TimeSpan.FromMilliseconds(140) - idleFor;
                TimeSpan remainingDeadline = TimeSpan.FromMilliseconds(420) - queuedAt.Elapsed;
                TimeSpan wait = remainingQuiet < remainingDeadline ? remainingQuiet : remainingDeadline;
                if (wait < TimeSpan.FromMilliseconds(20))
                    wait = TimeSpan.FromMilliseconds(20);
                await Task.Delay(wait, _windowLifetimeCts.Token).ConfigureAwait(false);
            }
            if (_windowLifetimeCts.IsCancellationRequested) return;

            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                ReleaseDetachedNavigationCleanup();
                // A queued WinUI callback may run after Window.Closed has invalidated native XAML peers.
                if (_windowLifetimeCts.IsCancellationRequested)
                    return;
                ScheduleDetachedNavigationHostCleanup();
            }))
                ReleaseDetachedNavigationCleanup();
        }
        catch (OperationCanceledException)
        {
            ReleaseDetachedNavigationCleanup();
        }
    }

    private void ScheduleDetachedNavigationHostCleanup(bool delayFirstFrame = false)
    {
        if (_windowLifetimeCts.IsCancellationRequested)
            return;
        if (_detachedNavigationHosts.Count == 0 || !TryReserveDetachedNavigationCleanup())
            return;
        if (delayFirstFrame)
        {
            SafeTaskService.Forget(DelayDetachedNavigationHostCleanupAsync(_detachedNavigationHosts.Count), "navigation", "Deferred detached host cleanup");
            return;
        }

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ReleaseDetachedNavigationCleanup();
            // Never dereference native Children after CleanupWindowResources cancels the window lifetime.
            if (_windowLifetimeCts.IsCancellationRequested)
                return;
            if (_detachedNavigationHosts.Count == 0)
                return;

            var host = _detachedNavigationHosts.Peek();
            int removeCount = DetachedNavigationCleanupPolicy.RemoveCount(
                host.Children.Count, _detachedNavigationHosts.Count);
            for (int i = 0; i < removeCount; i++)
            {
                int index = host.Children.Count - 1;
                var child = host.Children[index];
                _detachedNavigationOwners.Remove(child);
                host.Children.RemoveAt(index);
            }

            if (host.Children.Count == 0)
            {
                _detachedNavigationHosts.Dequeue();
                if (_contentLayerHost.Children.Contains(host))
                    _contentLayerHost.Children.Remove(host);
                if (_recycledNavigationHost == null && !ReferenceEquals(host, ContentStack))
                    _recycledNavigationHost = host;
            }

            ScheduleDetachedNavigationHostCleanup();
        }))
            ReleaseDetachedNavigationCleanup();
    }

    private void SchedulePinnedPowerHostIdleDetach()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _powerHostIdleDetachQueued, 1, 0) != 0)
            return;
        SafeTaskService.Forget(DetachPinnedPowerHostAfterQuietAsync(), "navigation", "Power host idle detach");
    }

    private async Task DetachPinnedPowerHostAfterQuietAsync()
    {
        try
        {
            await Task.Delay(PowerHostIdleDetachDelay, _windowLifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            System.Threading.Interlocked.Exchange(ref _powerHostIdleDetachQueued, 0);
            return;
        }

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            System.Threading.Interlocked.Exchange(ref _powerHostIdleDetachQueued, 0);
            if (_windowLifetimeCts.IsCancellationRequested) return;
            var pinned = _persistentPowerHost;
            if (pinned == null || ReferenceEquals(pinned, ContentStack) ||
                pinned.Visibility != Visibility.Collapsed) return;
            if (DateTime.UtcNow - _lastNavigationUtc < PowerHostIdleDetachDelay)
            {
                SchedulePinnedPowerHostIdleDetach();
                return;
            }
            if (_contentLayerHost.Children.Contains(pinned))
                _contentLayerHost.Children.Remove(pinned);
        }))
            System.Threading.Interlocked.Exchange(ref _powerHostIdleDetachQueued, 0);
    }

    // A recycled host that is still attached or re-adopted as a pinned/current host
    // would make Children.Add throw COMException 0x800F1000 (element already has a parent).
    private StackPanel TakeRecycledNavigationHost(out bool recycled)
    {
        var host = _recycledNavigationHost;
        _recycledNavigationHost = null;
        recycled = host != null &&
            host.Parent == null &&
            host.Children.Count == 0 &&
            !ReferenceEquals(host, ContentStack) &&
            !ReferenceEquals(host, _persistentPowerHost) &&
            !ReferenceEquals(host, _persistentSystemHost);
        return recycled ? host! : new StackPanel { Spacing = 0 };
    }

    // Reusing a host formerly collapsed for native cleanup must explicitly
    // restore Visibility. Opacity=1 alone does not make WinUI layout it.
    internal static void ActivateRecycledNavigationHost(StackPanel host)
    {
        if (host.Children.Count != 0)
            throw new InvalidOperationException("Recycled navigation host was not empty.");
        host.Visibility = Visibility.Visible;
        host.IsHitTestVisible = true;
        host.Opacity = 1;
    }

    private void ClearNavigationHostsFast(string previousSection, string nextSection)
    {
        // Power's cache TTL is shorter than the periodic global trim. Drop an
        // expired pinned native tree before any new Power host can replace it.
        if (_persistentPowerHost != null && !IsPowerPageCacheFresh())
            InvalidatePowerPageCache();

        if (string.Equals(previousSection, "system", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(nextSection, "system", StringComparison.OrdinalIgnoreCase) &&
            _systemPageCache is { Count: 1 } && _systemVirtualRowsCache != null &&
            ReferenceEquals(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(_systemPageCache[0]), ContentStack) &&
            ContentStack.Children.Count == 1)
        {
            // Keep the existing cached WinUI tree in the visual layer, but
            // collapsed and not hit-testable. No element detachment takes place.
            var systemHost = ContentStack;
            systemHost.IsHitTestVisible = false;
            systemHost.Visibility = Visibility.Collapsed;
            _persistentSystemHost = systemHost;
            ContentStack = TakeRecycledNavigationHost(out bool recycled);
            if (ContentStack.Children.Count != 0)
                throw new InvalidOperationException("Recycled navigation host was not empty.");
            ContentStack.Visibility = Visibility.Visible;
            ContentStack.IsHitTestVisible = true;
            _contentLayerHost.Children.Add(ContentStack);
            if (recycled) _reusedNavigationHosts++;
        }
        else if (string.Equals(previousSection, "power", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(nextSection, "power", StringComparison.OrdinalIgnoreCase) &&
            CanParkPowerPageCache())
        {
            var powerHost = ContentStack;
            powerHost.IsHitTestVisible = false;
            powerHost.Visibility = Visibility.Collapsed;
            _persistentPowerHost = powerHost;
            // Keep the loaded tree during rapid navigation; only detach the
            // complete host when the user has been idle long enough.
            SchedulePinnedPowerHostIdleDetach();
            ContentStack = TakeRecycledNavigationHost(out bool recycled);
            if (ContentStack.Children.Count != 0)
                throw new InvalidOperationException("Recycled navigation host was not empty.");
            ContentStack.Visibility = Visibility.Visible;
            ContentStack.IsHitTestVisible = true;
            _contentLayerHost.Children.Add(ContentStack);
            if (recycled) _reusedNavigationHosts++;
        }
        else if (ContentStack.Children.Count > 0)
        {
            // Most compact sections have few/lightweight roots, so clear
            // them in place. GPU is the exception: its three/four direct
            // roots contain a deep native stats + driver visual tree, and
            // clearing only those few roots synchronously was observed at
            // 61ms during GPU cancellation navigation. Reuse the existing
            // deferred host-cleanup path for GPU regardless of root count.
            if (ContentStack.Children.Count <= 8 &&
                !string.Equals(previousSection, "gpu", StringComparison.OrdinalIgnoreCase))
            {
                ContentStack.Children.Clear();
                _inPlaceNavigationClears++;
            }
            else
            {
                // Heavy pages keep the deferred first-frame teardown so
                // navigation remains responsive. Reuse a previously cleaned
                // host when possible, never a host that still owns children.
                var oldContent = ContentStack;
                oldContent.IsHitTestVisible = false;
                oldContent.Opacity = 0;

                var replacement = TakeRecycledNavigationHost(out bool recycled);
                ActivateRecycledNavigationHost(replacement);
                _contentLayerHost.Children.Add(replacement);
                ContentStack = replacement;
                if (recycled) _reusedNavigationHosts++;
                QueueDetachedNavigationHostCleanup(oldContent);
            }
        }

        if (TopActions.Children.Count > 0)
            TopActions.Children.Clear();
        if (SubSelectorHost.Children.Count > 0)
            SubSelectorHost.Children.Clear();
        if (SubSelectorHost.Visibility != Visibility.Collapsed)
            SubSelectorHost.Visibility = Visibility.Collapsed;

        if (string.Equals(nextSection, "system", StringComparison.OrdinalIgnoreCase) &&
            _persistentSystemHost != null && _systemPageCache is { Count: 1 } &&
            _systemVirtualRowsCache != null && _systemVirtualMapCache != null)
        {
            // The ordinary navigation path has already emptied or replaced
            // ContentStack. Recycle that empty host, then reveal the existing
            // system visual tree without removing or reattaching its children.
            var unusedHost = ContentStack;
            if (unusedHost.Children.Count != 0)
                throw new InvalidOperationException("Navigation host not empty before pinned system reuse.");
            if (_contentLayerHost.Children.Contains(unusedHost))
                _contentLayerHost.Children.Remove(unusedHost);
            if (_recycledNavigationHost == null)
                _recycledNavigationHost = unusedHost;
            ContentStack = _persistentSystemHost;
            ContentStack.Visibility = Visibility.Visible;
            ContentStack.IsHitTestVisible = true;
        }
        else if (string.Equals(nextSection, "power", StringComparison.OrdinalIgnoreCase) &&
            _persistentPowerHost != null && IsPowerPageCacheFresh())
        {
            var unusedHost = ContentStack;
            if (unusedHost.Children.Count != 0)
                throw new InvalidOperationException("Navigation host not empty before pinned power reuse.");
            // The pinned host itself may be the current (in-place cleared) host; never recycle it.
            if (!ReferenceEquals(unusedHost, _persistentPowerHost))
            {
                if (_contentLayerHost.Children.Contains(unusedHost))
                    _contentLayerHost.Children.Remove(unusedHost);
                if (_recycledNavigationHost == null)
                    _recycledNavigationHost = unusedHost;
            }
            ContentStack = _persistentPowerHost;
            if (!_contentLayerHost.Children.Contains(ContentStack))
                _contentLayerHost.Children.Add(ContentStack);
            ContentStack.Visibility = Visibility.Visible;
            ContentStack.IsHitTestVisible = true;
        }
    }

    // Overview is the first visible section. Reading the full tweak catalog
    // merely to obtain its immutable label/icon can initialize unrelated
    // source indexes on the WinUI dispatcher. Keep the Overview metadata
    // locally until another section actually needs CatalogService.
    private static readonly SectionDefinition StartupOverviewSection = new(
        "overview", "Přehled", "Stav systému a rychlé profily", "\uE80F");
    // System's immutable metadata is needed before its tweak catalog. Using
    // this value-equal record avoids making the UI wait for CatalogService's
    // first static initialization during the navigation shell phase.
    private static readonly SectionDefinition StartupSystemSection = new(
        "system", "Systémové tweaky", "Windows, boot a virtualizace", "\uE770");
}
