using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

internal readonly record struct ProcessCpuUsage(string Name, double Percent);

// Stream over process CPU readings, group process instances case-insensitively,
// and keep only the top three without sorting every running application.
internal static class ProcessCpuRanking
{
    internal static ProcessCpuUsage[] TopThree(IEnumerable<ProcessCpuUsage> readings)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<ProcessCpuUsage>();
        foreach (ProcessCpuUsage reading in readings)
        {
            if (reading.Percent <= 0.1 || !double.IsFinite(reading.Percent) ||
                string.IsNullOrEmpty(reading.Name)) continue;
            if (index.TryGetValue(reading.Name, out int position))
            {
                ProcessCpuUsage previous = groups[position];
                groups[position] = previous with { Percent = previous.Percent + reading.Percent };
            }
            else
            {
                index.Add(reading.Name, groups.Count);
                groups.Add(reading);
            }
        }
        var top = new List<ProcessCpuUsage>(3);
        foreach (ProcessCpuUsage group in groups)
        {
            int pos = 0;
            while (pos < top.Count && top[pos].Percent >= group.Percent) pos++;
            if (pos >= 3) continue;
            top.Insert(pos, group);
            if (top.Count > 3) top.RemoveAt(3);
        }
        return top.ToArray();
    }
}
