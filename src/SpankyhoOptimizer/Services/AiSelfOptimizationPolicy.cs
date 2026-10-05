using System;

namespace SpankyhoOptimizer.Services;

public static class AiSelfOptimizationPolicy
{
    public static double ComputePressureScore(
        double cpuEwmaPercent,
        double workingSetEwmaMb,
        int threads,
        int handles,
        double ramGrowthMbPerMinute,
        double handleGrowthPerMinute)
    {
        double basePressure =
            Math.Max(cpuEwmaPercent / 1.6, workingSetEwmaMb / 115d) +
            Math.Max(0, threads - 32) / 32d +
            Math.Max(0, handles - 700) / 1400d;

        double growthPressure =
            Math.Max(0, ramGrowthMbPerMinute - 3) / 18d +
            Math.Max(0, handleGrowthPerMinute - 15) / 250d;

        return Math.Clamp(basePressure + Math.Min(0.75, growthPressure), 0, 10);
    }

    public static bool IsPressured(
        double pressureScore,
        double managedMemoryMb,
        double ramGrowthMbPerMinute,
        double handleGrowthPerMinute) =>
        pressureScore >= 1.15 ||
        managedMemoryMb >= 80 ||
        ramGrowthMbPerMinute >= 12 ||
        handleGrowthPerMinute >= 120;

    public static bool IsCalm(
        double pressureScore,
        double managedMemoryMb,
        double ramGrowthMbPerMinute,
        double handleGrowthPerMinute) =>
        pressureScore <= 0.70 &&
        managedMemoryMb <= 55 &&
        ramGrowthMbPerMinute <= 2 &&
        handleGrowthPerMinute <= 15;

    public static TimeSpan SamplingInterval(AiWorkloadClass workload) =>
        workload switch
        {
            AiWorkloadClass.Game => TimeSpan.FromSeconds(60),
            AiWorkloadClass.Interactive => TimeSpan.FromSeconds(45),
            AiWorkloadClass.Background => TimeSpan.FromSeconds(30),
            AiWorkloadClass.Idle => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromSeconds(60)
        };

    public static int ApplyWorkloadFloor(int intervalSeconds, AiWorkloadClass workload) =>
        workload switch
        {
            AiWorkloadClass.Game => Math.Max(intervalSeconds, 180),
            AiWorkloadClass.Interactive => Math.Max(intervalSeconds, 120),
            AiWorkloadClass.Background => Math.Max(intervalSeconds, 60),
            AiWorkloadClass.Unknown => Math.Max(intervalSeconds, 120),
            _ => intervalSeconds
        };

    public static string State(
        double pressureScore,
        int maintenanceIntervalSeconds,
        double ramGrowthMbPerMinute,
        double handleGrowthPerMinute)
    {
        if (ramGrowthMbPerMinute >= 12 || handleGrowthPerMinute >= 120)
            return "Rostoucí režie";
        if (pressureScore >= 1.15)
            return "Úsporný režim";
        if (maintenanceIntervalSeconds > 60)
            return "Adaptivní úspora";
        return "Normální režim";
    }
}
