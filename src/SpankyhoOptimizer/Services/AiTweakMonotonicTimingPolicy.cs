using System;

namespace SpankyhoOptimizer.Services;

// Admission delays use elapsed time, never an adjustable Windows UTC clock.
// Journal and health records still retain wall-clock timestamps for display.
internal static class AiTweakMonotonicTimingPolicy
{
    internal static bool HasElapsed(long since, long now, long frequency, TimeSpan required)
    {
        if (since <= 0 || now < since || frequency <= 0 || required < TimeSpan.Zero)
            return false; // no trusted evidence => do not admit mutation
        return (now - (double)since) / frequency >= required.TotalSeconds;
    }

    internal static bool MayQueue(long last, long now, long frequency) =>
        last == 0 ? now > 0 && frequency > 0 :
        HasElapsed(last, now, frequency, TimeSpan.FromSeconds(5));

    internal static string RunFunctionalSmoke()
    {
        const long frequency = 10_000;
        const long start = 100_000;
        if (HasElapsed(start, start + 49_999, frequency, TimeSpan.FromSeconds(5)) ||
            !HasElapsed(start, start + 50_000, frequency, TimeSpan.FromSeconds(5)) ||
            !HasElapsed(start, start + 36_000_000, frequency, TimeSpan.FromHours(1)) ||
            HasElapsed(start, start - 1, frequency, TimeSpan.Zero) ||
            HasElapsed(0, start, frequency, TimeSpan.Zero) ||
            HasElapsed(start, start, 0, TimeSpan.Zero) ||
            HasElapsed(start, start, frequency, TimeSpan.FromSeconds(-1)) ||
            !HasElapsed(start, start, frequency, TimeSpan.Zero) ||
            !MayQueue(0, start, frequency) ||
            MayQueue(start, start + 49_999, frequency) ||
            !MayQueue(start, start + 50_000, frequency) ||
            MayQueue(start, start - 1, frequency))
            throw new InvalidOperationException("AI tweak monotonic admission/cadence policy regressed.");
        // A Windows NTP jump doesn't touch the monotonic elapsed counters.
        DateTime wall = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        if (wall.AddHours(3) <= wall ||
            HasElapsed(start, start + 2 * frequency, frequency, TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("AI tweak trial admission followed mutable wall clock.");
        return "AI_TWEAK_MONOTONIC_SMOKE_OK observation=true queue5s=true " +
            "timeJumpIgnored=true invalidCountersFailClosed=true windowsWrites=0";
    }
}
