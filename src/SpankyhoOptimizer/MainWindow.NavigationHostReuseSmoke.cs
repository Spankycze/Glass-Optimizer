using System;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    internal async Task RunNavigationHostReuseSmokeAsync()
    {
        await ShowSectionAsync("overview");
        await Task.Delay(100); // allow the deferred overview cards to attach
        var originalHost = ContentStack;
        int originalChildren = originalHost.Children.Count;
        int clearBefore = _inPlaceNavigationClears;

        await ShowSectionAsync("settings");
        if (originalChildren is > 0 and <= 8)
        {
            if (!ReferenceEquals(ContentStack, originalHost))
                throw new InvalidOperationException("Compact navigation allocated a new native host.");
            if (_inPlaceNavigationClears <= clearBefore)
                throw new InvalidOperationException("Compact navigation host did not clear in-place.");
        }

        var settingsHost = ContentStack;
        int settingsChildren = settingsHost.Children.Count;
        await ShowSectionAsync("overview");
        if (settingsChildren is > 0 and <= 8 &&
            !ReferenceEquals(ContentStack, settingsHost))
            throw new InvalidOperationException("Settings navigation allocated a new native host.");

        if (ContentStack.Children.Count == 0)
            throw new InvalidOperationException("Cached Overview lost its content after host reuse.");

        LogService.Info("perf",
            $"NAV_HOST_REUSE_SMOKE_OK overviewChildren={originalChildren} " +
            $"settingsChildren={settingsChildren} compactClears={_inPlaceNavigationClears - clearBefore} " +
            $"recycledHosts={_reusedNavigationHosts} detachedQueue={_detachedNavigationHosts.Count}");
    }
}
