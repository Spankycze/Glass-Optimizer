using System;

namespace SpankyhoOptimizer.Services;

public enum AiWorkloadClass
{
    Unknown,
    Idle,
    Background,
    Interactive,
    Game
}

public static class AiWorkloadContextPolicy
{
    public static AiWorkloadClass Classify(
        bool gameActive,
        bool realIdle,
        string? foregroundProcess,
        string? topCpuProcess,
        double topCpuPercent,
        double totalLoadPercent)
    {
        if (gameActive)
            return AiWorkloadClass.Game;
        if (realIdle)
            return AiWorkloadClass.Idle;

        // Non-finite or out-of-range CPU telemetry is not evidence of
        // background dominance. Fail closed until the next valid sample.
        if (!double.IsFinite(topCpuPercent) || topCpuPercent < 0 || topCpuPercent > 100 ||
            !double.IsFinite(totalLoadPercent) || totalLoadPercent < 0 || totalLoadPercent > 100)
            return AiWorkloadClass.Unknown;

        string foreground = Normalize(foregroundProcess);
        string top = Normalize(topCpuProcess);
        double load = Math.Clamp(totalLoadPercent, 0, 100);
        double topLoad = Math.Clamp(topCpuPercent, 0, 100);

        // Without a confirmed foreground process, even substantial CPU load
        // cannot be attributed safely to background work. Real idle is
        // established independently above.
        if (foreground.Length == 0)
            return AiWorkloadClass.Unknown;

        if (top.Length > 0 &&
            string.Equals(foreground, top, StringComparison.OrdinalIgnoreCase))
            return AiWorkloadClass.Interactive;

        bool backgroundDominates =
            top.Length > 0 &&
            !string.Equals(foreground, top, StringComparison.OrdinalIgnoreCase) &&
            topLoad >= Math.Max(3.0, load * 0.45);

        return backgroundDominates
            ? AiWorkloadClass.Background
            : AiWorkloadClass.Interactive;
    }

    public static bool RequiresConservativePerformanceThreshold(AiWorkloadClass workload) =>
        workload == AiWorkloadClass.Background ||
        workload == AiWorkloadClass.Idle;

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : NormalizeProcessName(value.Trim());

    private static string NormalizeProcessName(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
}
