using System;

namespace SpankyhoOptimizer.Services;

internal static class FanRpmReadbackPolicy
{
    // Do not allocate a filtered array/LINQ iterator in the fan agent's 1-5s loop.
    internal static bool TryAverageActive(int[]? rpms, out double average)
    {
        average = 0;
        if (rpms is null) return false;
        long sum = 0;
        int count = 0;
        foreach (int rpm in rpms)
        {
            if (rpm <= 100) continue;
            sum += rpm;
            count++;
        }
        if (count == 0) return false;
        average = (double)sum / count;
        return true;
    }
}
