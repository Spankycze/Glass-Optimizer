using System;

namespace SpankyhoOptimizer.Services;

public static class AiMaintenanceSchedulingPolicy
{
    private static readonly string[] Kinds = { "service", "task", "driver" };

    public static string[] OrderForCycle(int cycle)
    {
        int start = Math.Abs(cycle) % Kinds.Length;
        var result = new string[Kinds.Length];
        for (int i = 0; i < Kinds.Length; i++)
            result[i] = Kinds[(start + i) % Kinds.Length];
        return result;
    }
}
