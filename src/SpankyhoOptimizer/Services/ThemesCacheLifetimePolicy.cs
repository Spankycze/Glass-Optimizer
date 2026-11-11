using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Pure policy for the short-lived Themes visual cache; keep decisions
/// independent of WinUI dispatcher scheduling so boundary cases are unit-testable.
/// </summary>
internal static class ThemesCacheLifetimePolicy
{
    internal static bool IsFresh(DateTime capturedUtc, DateTime nowUtc, TimeSpan lifetime)
    {
        if (capturedUtc == default || lifetime <= TimeSpan.Zero)
            return false;
        TimeSpan age = nowUtc - capturedUtc;
        // A backward clock jump must not retain stale UI indefinitely.
        return age >= TimeSpan.Zero && age < lifetime;
    }

    internal static bool CanExpire(int callbackEpoch, int currentEpoch, bool windowClosing)
        => !windowClosing && callbackEpoch == currentEpoch;
}
