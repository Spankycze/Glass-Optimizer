namespace SpankyhoOptimizer.Services;

// Pure gate shared by compact AI Power UI and its offline tests. Detailed
// service/power-setting probes run only after the existing user expands them.
internal static class AiPowerDetailsLoadPolicy
{
    internal static bool ShouldLoad(bool expanded, bool activeDisclosure,
        bool loading, bool loaded, bool currentNavigation) =>
        expanded && activeDisclosure && !loading && !loaded && currentNavigation;
}
