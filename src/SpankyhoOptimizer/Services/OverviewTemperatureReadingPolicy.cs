using System;
using System.Linq;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Shared sanity/freshness checks for the visible Overview sensor card.
/// Never fabricate temperature or fan readings when the provider is absent.
/// </summary>
internal static class OverviewTemperatureReadingPolicy
{
    // The privileged bridge can be starting during first paint. Retry twice
    // in the first eight seconds, then retain the existing 16-second cadence.
    // This does not add a timer, process or background sampling thread.
    internal static bool ShouldRead(int activeTick) =>
        activeTick >= 0 && (activeTick is 0 or 2 or 4 || activeTick % 8 == 0);

    internal static double? ValidTemperature(double? temperature) =>
        temperature is { } value && double.IsFinite(value) &&
        value > 5 && value < 125 ? value : null;

    internal static bool IsFresh(DateTime updatedUtc, DateTime nowUtc, TimeSpan maxAge) =>
        updatedUtc <= nowUtc.AddSeconds(2) &&
        nowUtc - updatedUtc >= TimeSpan.FromSeconds(-2) &&
        nowUtc - updatedUtc <= maxAge;

    internal static double? SelectCpu(double? bridge, double? fanAgent) =>
        ValidTemperature(bridge) ?? ValidTemperature(fanAgent);

    internal static int? AverageActiveFanRpm(int[]? rpms)
    {
        if (rpms is not { Length: > 0 }) return null;
        int sum = 0, active = 0;
        foreach (int rpm in rpms)
        {
            if (rpm is <= 0 or > 6000) continue;
            sum += rpm;
            active++;
        }
        return active == 0 ? null : (int)Math.Round((double)sum / active);
    }

    internal static int AvailableSensorCount(double? cpu, double? gpu, double? board) =>
        (ValidTemperature(cpu).HasValue ? 1 : 0) +
        (ValidTemperature(gpu).HasValue ? 1 : 0) +
        (ValidTemperature(board).HasValue ? 1 : 0);
}
