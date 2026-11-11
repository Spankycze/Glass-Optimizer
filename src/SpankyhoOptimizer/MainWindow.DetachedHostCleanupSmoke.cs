using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    internal async Task RunDetachedHostCleanupUnderNavigationSmokeAsync()
    {
        if (ShouldBeginDetachedHostCleanup(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(50), 1) ||
            !ShouldBeginDetachedHostCleanup(TimeSpan.FromMilliseconds(140), TimeSpan.Zero, 1) ||
            !ShouldBeginDetachedHostCleanup(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(421), 1) ||
            !ShouldBeginDetachedHostCleanup(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(121), 4))
            throw new InvalidOperationException("Detached host cleanup debounce/deadline policy regressed.");

        // Recycled hosts were collapsed during native cleanup. Test the
        // actual reuse preparation against a detached WinUI host independently
        // of any user page or tweak.
        var recycled = new StackPanel
        {
            Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
            IsHitTestVisible = false,
            Opacity = 0
        };
        ActivateRecycledNavigationHost(recycled);
        if (recycled.Visibility != Microsoft.UI.Xaml.Visibility.Visible ||
            !recycled.IsHitTestVisible || recycled.Opacity != 1)
            throw new InvalidOperationException("Recycled native host remains invisible.");
        recycled.Children.Add(new Border());
        try
        {
            ActivateRecycledNavigationHost(recycled);
            throw new InvalidOperationException("Nonempty recycled host was accepted.");
        }
        catch (InvalidOperationException ex) when (
            ex.Message == "Recycled navigation host was not empty.") { }
        recycled.Children.Clear();

        // An invisible, self-contained test host; never access user tweaks.
        // It is added to the real WinUI panel to exercise native detachment.
        var host = new StackPanel { Opacity = 0, IsHitTestVisible = false };
        for (int i = 0; i < 19; i++) host.Children.Add(new Border());
        _contentLayerHost.Children.Add(host);
        DateTime previousNavigation = _lastNavigationUtc;
        try
        {
            QueueDetachedNavigationHostCleanup(host);
            if (host.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed || host.IsHitTestVisible)
                throw new InvalidOperationException("Detached host still participates in visual layout.");
            // Simulate a user who keeps clicking more often than the 140ms
            // quiet threshold. Before this fix cleanup starved forever.
            for (int i = 0; i < 44; i++)
            {
                _lastNavigationUtc = DateTime.UtcNow;
                await Task.Delay(25);
            }
            if (host.Children.Count != 0 || _contentLayerHost.Children.Contains(host) ||
                _detachedNavigationHosts.Contains(host))
                throw new InvalidOperationException("Native WinUI detached host remained after sustained navigation.");

            // Multiple queued hosts must all drain through the single low-priority callback.
            var extraHosts = new StackPanel[3];
            try
            {
                for (int h = 0; h < extraHosts.Length; h++)
                {
                    var extra = new StackPanel { Opacity = 0, IsHitTestVisible = false };
                    extraHosts[h] = extra;
                    for (int i = 0; i < 9; i++) extra.Children.Add(new Border());
                    _contentLayerHost.Children.Add(extra);
                    QueueDetachedNavigationHostCleanup(extra);
                    if (extra.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed || extra.IsHitTestVisible)
                        throw new InvalidOperationException("Queued heavy host remains rendered.");
                }
                for (int i = 0; i < 80; i++)
                {
                    bool remaining = false;
                    foreach (var extra in extraHosts)
                        remaining |= extra.Children.Count != 0 || _detachedNavigationHosts.Contains(extra);
                    if (!remaining) break;
                    _lastNavigationUtc = DateTime.UtcNow;
                    await Task.Delay(25);
                }
                foreach (var extra in extraHosts)
                    if (extra.Children.Count != 0 || _detachedNavigationHosts.Contains(extra) ||
                        _contentLayerHost.Children.Contains(extra))
                        throw new InvalidOperationException("Multiple detached WinUI hosts remained queued.");
            }
            finally
            {
                // A failed smoke must not retain its synthetic hosts in the native cleanup queue.
                // Preserve any real pending hosts and their relative ordering.
                int pending = _detachedNavigationHosts.Count;
                for (int i = 0; i < pending; i++)
                {
                    var queued = _detachedNavigationHosts.Dequeue();
                    if (Array.IndexOf(extraHosts, queued) < 0)
                        _detachedNavigationHosts.Enqueue(queued);
                }
                foreach (var extra in extraHosts)
                {
                    if (extra == null) continue;
                    if (_contentLayerHost.Children.Contains(extra))
                        _contentLayerHost.Children.Remove(extra);
                    extra.Children.Clear();
                }
            }
            LogService.Info("perf", "NAV_DETACHED_DEADLINE_SMOKE_OK children=19 multiHosts=3 continuousInputMs=1100");
        }
        finally
        {
            _lastNavigationUtc = previousNavigation;
            // Do not leave synthetic UI elements attached if any assertion
            // fails: subsequent smoke gates must start from a clean tree.
            if (_contentLayerHost.Children.Contains(host))
                _contentLayerHost.Children.Remove(host);
            while (_detachedNavigationHosts.Contains(host))
            {
                // The synthetic host normally drains itself through the
                // dispatcher. On failure, remove it from the queue without
                // disturbing the relative order of other pending hosts.
                int n = _detachedNavigationHosts.Count;
                for (int i = 0; i < n; i++)
                {
                    var queued = _detachedNavigationHosts.Dequeue();
                    if (!ReferenceEquals(queued, host))
                        _detachedNavigationHosts.Enqueue(queued);
                }
            }
            host.Children.Clear();
        }
    }
}
