using System;

namespace SpankyhoOptimizer.Services;

public static class AiDriverAutomationPolicy
{
    public static readonly TimeSpan MinimumObservation = TimeSpan.FromMinutes(30);

    public static bool IsEligibleStartType(int start) => start == 3;

    public static TimeSpan RequiredObservation(int configuredSeconds)
    {
        TimeSpan configured = TimeSpan.FromSeconds(Math.Clamp(configuredSeconds, 0, 3600));
        return configured > MinimumObservation ? configured : MinimumObservation;
    }

    public static string IneligibleReason(int start) => start switch
    {
        0 => "boot-start driver: automatic disable forbidden",
        1 => "system-start driver: automatic disable forbidden",
        2 => "auto-start driver: automatic disable forbidden",
        4 => "driver already disabled",
        _ => "unsupported driver start type"
    };
}
