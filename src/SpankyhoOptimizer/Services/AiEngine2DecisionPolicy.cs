using System;

namespace SpankyhoOptimizer.Services;

// AI Engine 2.0 decision contract: observation is always read-only.
// No browser tuning, networking changes or unsolicited process mutations.
internal enum AiEngine2Phase { AwaitingTelemetry, Observe, EligibleForMeasuredTrial }

internal readonly record struct AiEngine2Decision(
    AiEngine2Phase Phase, string Reason, int Confidence, int NextObservationSeconds);

internal static class AiEngine2DecisionPolicy
{
    internal static AiEngine2Decision Evaluate(AiSharedDecisionContext? context, DateTime nowUtc)
    {
        if (context is null || nowUtc.Kind != DateTimeKind.Utc ||
            context.UpdatedUtc.Kind != DateTimeKind.Utc ||
            context.UpdatedUtc > nowUtc ||
            nowUtc - context.UpdatedUtc > TimeSpan.FromSeconds(15))
            return new(AiEngine2Phase.AwaitingTelemetry, "stale decision context", 0, 60);

        // Treat inconsistent producer confidence as invalid evidence, rather
        // than silently clamping it into an apparently trustworthy decision.
        if (context.Confidence < 0 || context.Confidence > 100 ||
            context.MaintenanceIntervalSeconds < 45 ||
            context.MaintenanceIntervalSeconds > 240)
            return new(AiEngine2Phase.AwaitingTelemetry, "invalid decision metadata", 0, 240);
        int confidence = context.Confidence;
        int interval = Math.Clamp(context.MaintenanceIntervalSeconds, 45, 240);
        if (!double.IsFinite(context.LoadPercent) || context.LoadPercent < 0 ||
            context.LoadPercent > 100 ||
            !double.IsFinite(context.SelfPressureScore) ||
            context.SelfPressureScore < 0 || context.SelfPressureScore > 10)
            return new(AiEngine2Phase.AwaitingTelemetry, "invalid system telemetry", 0, 240);

        // Never accept an inconsistent permission bit from a stale or foreign
        // producer when the reported machine is already under heavy pressure.
        if (context.SelfPressureScore >= 1.15 || context.LoadPercent >= 70)
            return new(AiEngine2Phase.Observe, "system pressure protected", confidence, interval);
        // Protect active foreground work even if a caller supplies a permissive
        // mutation flag: non-idle sessions must remain observation-only.
        if (!context.RealIdle)
            return new(AiEngine2Phase.Observe, "user activity protected", confidence, interval);
        if (context.GameActive || context.Workload is AiWorkloadClass.Game or AiWorkloadClass.Interactive)
            return new(AiEngine2Phase.Observe, "foreground workload protected", confidence, interval);
        // A measured trial must not overlap another experiment. The existing
        // coordinator owns write isolation and rollback for any real mutation.
        if (AiPerformanceExperimentCoordinator.IsActive)
            return new(AiEngine2Phase.Observe, "another experiment active", confidence, interval);
        if (context.AllowBackgroundMutation && confidence >= 72 &&
            AiSharedWorkloadPolicy.AllowsBackgroundAutomation(context.Workload))
            return new(AiEngine2Phase.EligibleForMeasuredTrial,
                "stable measured trial window", confidence, interval);
        return new(AiEngine2Phase.Observe, context.Reason, confidence, interval);
    }

    // No Windows writes: prove that a high-confidence or forged permission
    // flag cannot bypass foreground, pressure, stale-data or confidence vetoes.
    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        var quiet = new AiSharedDecisionContext(now, AiWorkloadClass.Idle,
            true, false, 12, 0.25, 60, 95, true, "stable");
        if (Evaluate(null, now).Phase != AiEngine2Phase.AwaitingTelemetry ||
            Evaluate(quiet with { UpdatedUtc = now.AddSeconds(-16) }, now).Phase != AiEngine2Phase.AwaitingTelemetry ||
            Evaluate(quiet with { UpdatedUtc = now.AddSeconds(1) }, now).Phase != AiEngine2Phase.AwaitingTelemetry ||
            Evaluate(quiet with { RealIdle = false }, now).Phase != AiEngine2Phase.Observe ||
            Evaluate(quiet with { GameActive = true }, now).Phase != AiEngine2Phase.Observe ||
            Evaluate(quiet with { Workload = AiWorkloadClass.Interactive }, now).Phase != AiEngine2Phase.Observe ||
            Evaluate(quiet with { Workload = AiWorkloadClass.Game }, now).Phase != AiEngine2Phase.Observe ||
            Evaluate(quiet with { LoadPercent = 70 }, now).Phase != AiEngine2Phase.Observe ||
            Evaluate(quiet with { SelfPressureScore = 1.15 }, now).Phase != AiEngine2Phase.Observe ||
            Evaluate(quiet with { Confidence = 71 }, now).Phase != AiEngine2Phase.Observe ||
            Evaluate(quiet with { AllowBackgroundMutation = false }, now).Phase != AiEngine2Phase.Observe ||
            Evaluate(quiet with { LoadPercent = double.NaN }, now).Phase != AiEngine2Phase.AwaitingTelemetry ||
            Evaluate(quiet with { MaintenanceIntervalSeconds = 10 }, now).Phase != AiEngine2Phase.AwaitingTelemetry ||
            (!AiPerformanceExperimentCoordinator.IsActive &&
             Evaluate(quiet, now).Phase != AiEngine2Phase.EligibleForMeasuredTrial))
            throw new InvalidOperationException("AI Engine 2 shared decision safety gates regressed.");
        return "AI_ENGINE2_SHARED_DECISION_SMOKE_OK idle=true foregroundProtected=true " +
               "gameProtected=true pressureProtected=true staleRejected=true confidence=true " +
               "exclusiveTrial=true windowsWrites=0";
    }
}
