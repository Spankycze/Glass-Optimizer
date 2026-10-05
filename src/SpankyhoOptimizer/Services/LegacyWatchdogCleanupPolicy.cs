namespace SpankyhoOptimizer.Services;

public static class LegacyWatchdogCleanupPolicy
{
    public static bool ShouldAttempt(bool elevated) => elevated;
}
