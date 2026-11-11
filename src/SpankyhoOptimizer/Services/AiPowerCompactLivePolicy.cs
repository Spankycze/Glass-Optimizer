namespace SpankyhoOptimizer.Services;

// Prevents avoidable WinUI control-tree reconstruction on routine AI status
// refreshes. Genuine control changes and expired visible details rebuild.
internal static class AiPowerCompactLivePolicy
{
    internal static bool CanReuse(
        bool sameHost, bool hasChildren, bool sameToggle,
        bool detailsExpanded, bool detailsLoading, bool detailsLoaded,
        bool detailSnapshotFresh)
    {
        if (!sameHost || !hasChildren || !sameToggle) return false;
        if (!detailsExpanded) return true;
        if (detailsLoading || !detailsLoaded) return true;
        return detailSnapshotFresh;
    }
}
