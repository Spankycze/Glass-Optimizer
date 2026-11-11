using System;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// All scenarios exercise the same state machine used by the live WinUI panel.
// No UI launch, powercfg, background polling or Windows configuration changes.
internal static class AiPowerControlsRefreshSmoke
{
    internal static Task RunAsync()
    {
        static void Check(bool condition, string label)
        {
            if (!condition)
                throw new InvalidOperationException("AI_POWER_REFRESH_SMOKE_FAIL: " + label);
        }

        var state = new AiPowerControlsRefreshState();
        object oldHost = new(), newHost = new();

        // 1-4: In-flight requests are deduplicated per cached host/generation.
        Check(!state.DetailsExpanded, "initially collapsed");
        Check(state.TryBegin(oldHost, 10), "first refresh admitted");
        Check(state.IsCurrent(oldHost, 10), "first refresh owns result");
        Check(!state.TryBegin(oldHost, 10), "same generation duplicate refused");

        // 5-9: Navigation or a cache replacement can supersede the old read.
        Check(state.TryBegin(oldHost, 11), "new navigation generation admitted");
        Check(!state.IsCurrent(oldHost, 10), "old result cannot render");
        state.Complete(oldHost, 10);
        Check(state.IsCurrent(oldHost, 11), "late completion cannot release new read");
        Check(state.TryBegin(newHost, 11), "new host supersedes cached host");
        Check(!state.IsCurrent(oldHost, 11), "old host cannot render after replacement");

        // 10-13: The existing detail-expander state survives a refresh.
        state.SetDetailsExpanded(true);
        Check(state.DetailsExpanded, "opened details recorded");
        state.Complete(oldHost, 11);
        Check(state.IsCurrent(newHost, 11) && state.DetailsExpanded,
            "old completion does not reset details");
        state.Complete(newHost, 11);
        Check(!state.IsCurrent(newHost, 11), "new refresh properly released");

        // 14-17: Start/complete further refreshes without resetting details.
        Check(state.TryBegin(newHost, 11), "subsequent refresh starts");
        Check(state.DetailsExpanded, "expanded state retained during refresh");
        state.SetDetailsExpanded(false);
        state.Complete(newHost, 11);
        Check(!state.DetailsExpanded, "collapsed preference persists");

        // 18-21: Duplicate suppression is scoped to active operations only.
        Check(state.TryBegin(newHost, 12), "next generation starts");
        Check(!state.TryBegin(newHost, 12), "next generation duplicate suppressed");
        state.Complete(newHost, 12);
        Check(state.TryBegin(newHost, 12), "completed generation may refresh again");
        state.Complete(newHost, 12);

        // 22: Bad callers cannot acquire a null host.
        bool nullRejected = false;
        try { state.TryBegin(null!, 100); }
        catch (ArgumentNullException) { nullRejected = true; }
        Check(nullRejected, "null host refused");
        // 23-29: Detached WinUI controls must not be pinned after page
        // invalidation, nor overwrite the saved disclosure preference.
        object disclosure = new(), replacement = new();
        state.ActivateDisclosure(disclosure);
        Check(state.IsActiveDisclosure(disclosure), "disclosure active");
        Check(state.TryUpdateDisclosure(disclosure, true), "expanded saved");
        state.RetireDisclosure();
        Check(!state.IsActiveDisclosure(disclosure), "old disclosure retired");
        Check(!state.TryUpdateDisclosure(disclosure, false), "old callback cannot collapse");
        Check(state.DetailsExpanded, "expanded preference retained");
        state.ActivateDisclosure(replacement);
        Check(state.IsActiveDisclosure(replacement), "replacement active");
        Check(!state.IsActiveDisclosure(disclosure), "old not resurrected");
        return Task.CompletedTask;
    }
}
