using System;
using System.IO;

namespace SpankyhoOptimizer.Services;

public sealed record PerformanceBaseline(
    string Version, DateTimeOffset SavedUtc,
    double BaselineMb, double PeakMb, double RetainedMb,
    double DeltaMb, long DurationMs);

public sealed record PerformanceBaselineComparison(
    PerformanceBaseline? Previous, PerformanceBaseline Current,
    double PeakChangePercent, double RetainedChangePercent, bool Regression);

public static class PerformanceBaselineService
{
    private static string FileName => Path.Combine(LogService.Root, "performance-baseline.json");

    public static PerformanceBaseline? Load() =>
        SelfHealingJsonService.ReadOrDefault<PerformanceBaseline?>(FileName, () => null, "perf-baseline");

    public static PerformanceBaselineComparison CompareAndSave(PerformanceBaseline current)
    {
        var previous = Load();
        double peakPct = Percent(previous?.PeakMb, current.PeakMb);
        double retainedPct = Percent(previous?.RetainedMb, current.RetainedMb);
        bool regression = previous != null &&
            ((current.PeakMb - previous.PeakMb > 35 && peakPct > 20) ||
             (current.RetainedMb - previous.RetainedMb > 35 && retainedPct > 20));
        SelfHealingJsonService.Write(FileName, current);
        return new(previous, current, peakPct, retainedPct, regression);
    }
    private static double Percent(double? previous, double current)
    {
        if (!previous.HasValue || previous.Value <= 0) return 0;
        return ((current - previous.Value) / previous.Value) * 100.0;
    }
}
