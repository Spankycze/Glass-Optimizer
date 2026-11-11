using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

internal readonly record struct ProcessMemoryUsage(string Name, long Bytes);

// Voice system status needs just the three largest process groups. Avoid
// sorting the full process inventory and preserve the first-seen name on ties.
internal static class ProcessMemoryRanking
{
    internal static ProcessMemoryUsage[] TopThree(IEnumerable<ProcessMemoryUsage> readings)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<ProcessMemoryUsage>();
        foreach (ProcessMemoryUsage reading in readings)
        {
            if (reading.Bytes <= 0 || string.IsNullOrEmpty(reading.Name)) continue;
            if (index.TryGetValue(reading.Name, out int position))
            {
                ProcessMemoryUsage previous = groups[position];
                groups[position] = previous with { Bytes = previous.Bytes + reading.Bytes };
            }
            else
            {
                index.Add(reading.Name, groups.Count);
                groups.Add(reading);
            }
        }

        var top = new List<ProcessMemoryUsage>(3);
        foreach (ProcessMemoryUsage group in groups)
        {
            int position = 0;
            while (position < top.Count && top[position].Bytes >= group.Bytes) position++;
            if (position >= 3) continue;
            top.Insert(position, group);
            if (top.Count > 3) top.RemoveAt(3);
        }
        return top.ToArray();
    }
}
