using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private static void InvalidateUiDataCache(string area)
    {
        if (area is "network-live" or "network" or "all")
            SystemInsightService.InvalidateNetwork(dropPersistent: false, invalidateAnalyzer: false);

        if (area is "network" or "all")
            PerformanceProfilesService.InvalidateNicCache();
    }
}