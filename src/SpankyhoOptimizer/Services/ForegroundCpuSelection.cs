using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

internal readonly record struct ForegroundCpuSelectionResult(string WorkloadKey, double CpuPercent);

// Maintain the two highest CPU consumers in a single pass instead of sorting
// every sampled process. Strict comparisons preserve LINQ's stable tie order.
internal static class ForegroundCpuSelection
{
    internal static ForegroundCpuSelectionResult Select(
        IReadOnlyList<(int Id, string Name, double Cpu)> loads,
        IReadOnlyCollection<string> observedComponents)
    {
        (int Id, string Name, double Cpu) first = default, second = default;
        bool hasFirst = false, hasSecond = false;
        foreach (var item in loads)
        {
            if (!(item.Cpu >= 0.1) || item.Name == "Idle" ||
                item.Name.Contains("Spanky", StringComparison.OrdinalIgnoreCase))
                continue;
            bool observed = false;
            foreach (string component in observedComponents)
            {
                if (string.Equals(item.Name, component, StringComparison.OrdinalIgnoreCase))
                {
                    observed = true;
                    break;
                }
            }
            if (observed) continue;
            if (!hasFirst || item.Cpu > first.Cpu)
            {
                if (hasFirst) { second = first; hasSecond = true; }
                first = item;
                hasFirst = true;
            }
            else if (!hasSecond || item.Cpu > second.Cpu)
            {
                second = item;
                hasSecond = true;
            }
        }
        if (!hasFirst) return new(string.Empty, 0);
        if (!hasSecond) return new(first.Id + ":" + first.Name, first.Cpu);
        string key = first.Id <= second.Id
            ? first.Id + ":" + first.Name + "|" + second.Id + ":" + second.Name
            : second.Id + ":" + second.Name + "|" + first.Id + ":" + first.Name;
        return new(key, first.Cpu + second.Cpu);
    }
}
