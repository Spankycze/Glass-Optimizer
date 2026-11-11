using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Admit an AI-suggested idle power vote only with low, trustworthy load.
/// The OS's independent real-idle signal remains separate. No plan is written here.
/// </summary>
internal static class AiPowerIdleAdmissionPolicy
{
    internal static bool AllowsSuggestedIdle(
        bool preferIdle,
        AiWorkloadClass workload,
        double confidence,
        double anomalyScore,
        double loadPercent,
        double? packagePowerW)
    {
        if (!preferIdle || workload is not (AiWorkloadClass.Idle or AiWorkloadClass.Background) ||
            !double.IsFinite(confidence) || confidence < 70 || confidence > 100 ||
            !double.IsFinite(anomalyScore) || anomalyScore < 0 || anomalyScore >= 35 ||
            !double.IsFinite(loadPercent) || loadPercent < 0 || loadPercent > 15)
            return false;

        // Missing package sensor is normal on many platforms. Corrupt or
        // measured high package power is not evidence of idle.
        if (packagePowerW is double power &&
            (!double.IsFinite(power) || power < 0 || power > 55))
            return false;
        return true;
    }
}
