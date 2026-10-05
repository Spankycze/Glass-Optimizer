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

        string foreground = Normalize(foregroundProcess);
        string top = Normalize(topCpuProcess);
        double load = Math.Clamp(totalLoadPercent, 0, 100);
        double topLoad = Math.Clamp(topCpuPercent, 0, 100);

        if (foreground.Length == 0)
            return load <= 5 ? AiWorkloadClass.Idle : AiWorkloadClass.Background;

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
            : value.Trim().Replace(".exe", string.Empty, StringComparison.OrdinalIgnoreCase);
}
