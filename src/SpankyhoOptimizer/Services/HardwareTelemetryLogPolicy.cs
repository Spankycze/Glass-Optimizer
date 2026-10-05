using System;

namespace SpankyhoOptimizer.Services;

public static class HardwareTelemetryLogPolicy
{
    public static readonly TimeSpan MissingSensorLogInterval = TimeSpan.FromMinutes(5);

    public static bool ShouldLogMissingSensor(DateTime nowUtc, DateTime lastLogUtc) =>
        lastLogUtc == DateTime.MinValue ||
        nowUtc - lastLogUtc >= MissingSensorLogInterval;
}
