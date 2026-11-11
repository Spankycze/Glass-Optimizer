using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// "Real idle" is a quiet *user input* interval with admissible CPU telemetry.
/// CPU frequency and package watts may remain boosted even if the user is
/// away; they are not evidence of keyboard/mouse activity.
/// </summary>
internal static class AiAdaptiveIdleAdmissionPolicy
{
    internal static bool IsRealIdle(double cpuPercent, bool userInputIdle) =>
        userInputIdle && double.IsFinite(cpuPercent) &&
        cpuPercent >= 0 && cpuPercent <= 10;
}
