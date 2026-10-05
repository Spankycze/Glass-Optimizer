using System;

namespace SpankyhoOptimizer.Services;

public static class AiFanTargetStabilityPolicy
{
    public static int Stabilize(
        int currentAppliedRpm,
        int requestedRpm,
        double? temperatureC,
        double predictedTemp10s,
        double anomalyScore,
        double confidence,
        AiWorkloadClass workload)
    {
        int requested = Math.Clamp(
            requestedRpm,
            1620,
            LianLiSlInfinityService.MaximumRpm);

        if (currentAppliedRpm < 0)
            return requested;

        int current = Math.Clamp(
            currentAppliedRpm,
            1620,
            LianLiSlInfinityService.MaximumRpm);

        bool emergency =
            (temperatureC is double temp && temp >= 82) ||
            predictedTemp10s >= 84 ||
            anomalyScore >= 90;

        // Never rate-limit an urgent cooling increase.
        if (emergency && requested > current)
            return requested;

        int upwardStep = confidence switch
        {
            < 45 => 100,
            < 70 => 160,
            _ => 240
        };

        int downwardStep = workload switch
        {
            AiWorkloadClass.Game => 90,
            AiWorkloadClass.Interactive => 110,
            AiWorkloadClass.Background => 150,
            AiWorkloadClass.Idle => 190,
            _ => 120
        };

        bool stillWarm =
            (temperatureC is double live && live >= 70) ||
            predictedTemp10s >= 72 ||
            anomalyScore >= 60;

        if (stillWarm)
            downwardStep = Math.Min(downwardStep, 75);

        int delta = requested - current;
        if (delta > 0)
            return Math.Min(requested, current + upwardStep);
        if (delta < 0)
            return Math.Max(requested, current - downwardStep);
        return current;
    }
}
