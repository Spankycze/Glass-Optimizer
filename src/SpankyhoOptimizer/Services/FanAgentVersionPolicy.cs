using System;

namespace SpankyhoOptimizer.Services;

public static class FanAgentVersionPolicy
{
    public static bool ShouldRequestUpdate(
        string currentVersion,
        string runningVersion)
    {
        if (string.Equals(
            currentVersion,
            runningVersion,
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(runningVersion))
            return true;

        if (!Version.TryParse(currentVersion, out Version? current))
            return false;

        if (!Version.TryParse(runningVersion, out Version? running))
            return true;

        return current > running;
    }
}
