using System;

namespace SpankyhoOptimizer.Services;

// Per-window, UI-thread-only refresh ownership. Cached navigation must not
// schedule duplicate snapshots or let a stale read replace newer controls.
// Also remembers the user's existing detail disclosure preference.
internal sealed class AiPowerControlsRefreshState
{
    private object? _host;
    private int _generation;
    private WeakReference<object>? _activeDisclosure;

    internal bool DetailsExpanded { get; private set; }

    internal void SetDetailsExpanded(bool expanded) => DetailsExpanded = expanded;
    internal void ActivateDisclosure(object disclosure)
    {
        ArgumentNullException.ThrowIfNull(disclosure);
        _activeDisclosure = new WeakReference<object>(disclosure);
    }

    internal bool IsActiveDisclosure(object disclosure) =>
        _activeDisclosure is not null &&
        _activeDisclosure.TryGetTarget(out object? active) &&
        ReferenceEquals(active, disclosure);

    // Explicit cache invalidation must not retain a discarded WinUI control.
    // Its expanded/collapsed preference is separate and survives navigation.
    internal void RetireDisclosure() => _activeDisclosure = null;

    internal bool TryUpdateDisclosure(object disclosure, bool expanded)
    {
        if (!IsActiveDisclosure(disclosure)) return false;
        DetailsExpanded = expanded;
        return true;
    }

    internal bool TryBegin(object host, int generation)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (ReferenceEquals(_host, host) && _generation == generation)
            return false;

        _host = host;
        _generation = generation;
        return true;
    }

    internal bool IsCurrent(object host, int generation) =>
        ReferenceEquals(_host, host) && _generation == generation;

    internal void Complete(object host, int generation)
    {
        // Completion of an older, cancelled navigation may NOT end the newer
        // owner's refresh or reset its user's disclosure preference.
        if (!IsCurrent(host, generation)) return;
        _host = null;
        _generation = 0;
    }
}
