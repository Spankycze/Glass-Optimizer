using System;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static class AiPowerDetailsLoadSmoke
{
    internal static Task RunAsync()
    {
        static void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("AI_POWER_DETAILS_SMOKE_FAIL: " + label);
        }

        // 1-7: Hidden or retired panels MUST NOT fetch heavy optional details.
        Check(!AiPowerDetailsLoadPolicy.ShouldLoad(false, true, false, false, true), "collapsed");
        Check(!AiPowerDetailsLoadPolicy.ShouldLoad(false, false, false, false, true), "collapsed retired");
        Check(!AiPowerDetailsLoadPolicy.ShouldLoad(true, false, false, false, true), "retired disclosure");
        Check(!AiPowerDetailsLoadPolicy.ShouldLoad(true, true, true, false, true), "in-flight deduplicated");
        Check(!AiPowerDetailsLoadPolicy.ShouldLoad(true, true, false, true, true), "already loaded");
        Check(!AiPowerDetailsLoadPolicy.ShouldLoad(true, true, false, false, false), "stale navigation");
        Check(!AiPowerDetailsLoadPolicy.ShouldLoad(false, true, true, true, false), "stale collapsed");

        // 8-11: Opening a live panel loads once and allows deliberate retry.
        Check(AiPowerDetailsLoadPolicy.ShouldLoad(true, true, false, false, true), "first expand loads");
        Check(AiPowerDetailsLoadPolicy.ShouldLoad(true, true, false, false, true), "retry after incomplete fetch");
        Check(!AiPowerDetailsLoadPolicy.ShouldLoad(true, true, false, true, true), "completed fetch cached");
        Check(!AiPowerDetailsLoadPolicy.ShouldLoad(true, true, true, true, true), "loading and loaded");

        // 12-19: Only the active disclosure can modify user expanded state.
        var state = new AiPowerControlsRefreshState();
        object first = new(), latest = new();
        Check(!state.IsActiveDisclosure(first), "no active control initially");
        state.ActivateDisclosure(first);
        Check(state.IsActiveDisclosure(first), "first active");
        Check(state.TryUpdateDisclosure(first, true) && state.DetailsExpanded, "expand first");
        state.ActivateDisclosure(latest);
        Check(!state.IsActiveDisclosure(first), "first retired");
        Check(!state.TryUpdateDisclosure(first, false), "old collapse rejected");
        Check(state.DetailsExpanded, "old collapse cannot reset preference");
        Check(state.TryUpdateDisclosure(latest, false), "current collapse accepted");
        Check(!state.DetailsExpanded, "collapsed user preference retained");

        // 20-22: A new refresh still inherits the previous UI preference.
        Check(state.TryUpdateDisclosure(latest, true), "reexpand");
        state.ActivateDisclosure(first);
        Check(state.DetailsExpanded, "replacement inherits open state");
        Check(!state.TryUpdateDisclosure(latest, false) && state.DetailsExpanded,
            "late event after replacement ignored");
        return Task.CompletedTask;
    }
}
