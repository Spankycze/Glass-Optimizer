namespace SpankyhoOptimizer.Services;

// Keep native WinUI tear-down bounded to a single low-priority UI turn.
// If several abandoned hosts have accumulated, drain 16 instead of eight
// children per turn to reduce the lifetime of hidden native visual trees.
internal static class DetachedNavigationCleanupPolicy
{
    internal static int RemoveCount(int childCount, int pendingHosts)
    {
        if (childCount <= 0) return 0;
        int budget = pendingHosts >= 2 ? 16 : 8;
        return System.Math.Min(childCount, budget);
    }
}
