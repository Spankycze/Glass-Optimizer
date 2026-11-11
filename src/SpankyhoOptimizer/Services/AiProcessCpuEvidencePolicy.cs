using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// A process with no comparable CPU counter is UNKNOWN, not observed at 0%.
/// An exact single process with a valid unchanged counter may truly be idle.
/// </summary>
internal static class AiProcessCpuEvidencePolicy
{
    // A counter that decreases or grows beyond all available logical CPU
    // seconds is inconsistent with a single surviving PID. Ignore the delta;
    // keep the latest counter as the following scan's baseline.
    internal static bool HasComparableCounter(bool readable, bool previousSeen,
        TimeSpan previousCpu, TimeSpan currentCpu, double windowSeconds,
        int logicalProcessors)
    {
        if (!readable || !previousSeen || logicalProcessors <= 0 ||
            !double.IsFinite(windowSeconds) || windowSeconds <= 0 ||
            currentCpu < previousCpu)
            return false;
        double delta = (currentCpu - previousCpu).TotalSeconds;
        return delta >= 0 &&
            delta <= windowSeconds * logicalProcessors * 1.05d + 0.25d;
    }

    // Native process totals undercount Windows-wide CPU when too many process
    // counters are denied/unavailable. Permit an aggregate hardware fallback
    // only after a representative majority of enumerated PIDs is comparable.
    // This is an admission heuristic, NOT proof that missing PIDs are idle.
    internal static bool HasRepresentativeCoverage(int comparableCount, int processCount) =>
        processCount >= 8 && comparableCount >= 8 &&
        comparableCount <= processCount &&
        (long)comparableCount * 4 >= (long)processCount * 3;

    // If the user's actual foreground process is unreadable, aggregate CPU
    // is not sufficient evidence for an automatic Windows power choice,
    // regardless of how many other background processes are readable.
    internal static bool HasRepresentativeForegroundCoverage(
        int comparableCount, int processCount, bool foregroundComparable) =>
        foregroundComparable && HasRepresentativeCoverage(comparableCount, processCount);

    internal static double? Percent(int instances, bool counterComparable,
        double consumedCpuSeconds, double windowSeconds, int logicalProcessors)
    {
        if (instances != 1 || !counterComparable || logicalProcessors <= 0 ||
            !double.IsFinite(consumedCpuSeconds) || consumedCpuSeconds < 0 ||
            !double.IsFinite(windowSeconds) || windowSeconds <= 0)
            return null;
        return Math.Round(Math.Clamp(
            consumedCpuSeconds / windowSeconds * 100d / logicalProcessors, 0d, 100d), 2);
    }
}
