using System;
using System.Threading;

namespace SpankyhoOptimizer.Services;

public sealed record AiSharedDecisionContext(
    DateTime UpdatedUtc,
    AiWorkloadClass Workload,
    bool RealIdle,
    bool GameActive,
    double LoadPercent,
    double SelfPressureScore,
    int MaintenanceIntervalSeconds,
    int Confidence,
    bool AllowBackgroundMutation,
    string Reason);

public static class AiSharedDecisionContextService
{
    // Contexts are immutable snapshots: publish once and read without lock contention.
    private static AiSharedDecisionContext? _current;

    public static AiSharedDecisionContext Update(
        AiWorkloadClass workload,
        bool realIdle,
        bool gameActive,
        double loadPercent,
        AiSelfOptimizationHealth? selfHealth,
        DateTime now,
        bool requireFreshSelfHealth = false)
    {
        // Self-health is sampled periodically; an older or future-dated
        // snapshot is not evidence of the current system pressure.
        bool utcTimestamp = now.Kind == DateTimeKind.Utc && now != default;
        bool healthFresh = utcTimestamp && selfHealth != null &&
            selfHealth.UpdatedUtc.Kind == DateTimeKind.Utc &&
            selfHealth.UpdatedUtc <= now &&
            now - selfHealth.UpdatedUtc <= TimeSpan.FromMinutes(5);
        double pressure = healthFresh ? selfHealth!.PressureScore : 0;
        // This producer reports a bounded 0..10 pressure score. A finite but
        // corrupt negative or above-range value must not look like idle load.
        if (!double.IsFinite(pressure) || pressure < 0 || pressure > 10)
            pressure = 10;
        int interval = healthFresh ? selfHealth!.MaintenanceIntervalSeconds : 60;
        // The sampler emits 45..240 seconds. Invalid persisted telemetry
        // must not advertise an unbounded or aggressively short cycle.
        if (healthFresh && (interval < 45 || interval > 240))
            interval = 240;
        int confidence = 100;
        string reason = "stable";
        bool loadValid = double.IsFinite(loadPercent) && loadPercent >= 0 && loadPercent <= 100;

        if (workload is AiWorkloadClass.Game or AiWorkloadClass.Interactive)
        {
            confidence -= 35;
            reason = "interactive workload";
        }
        if (gameActive) { confidence -= 30; reason = "game active"; }
        if (!realIdle) { confidence -= 15; reason = "user activity"; }
        if (loadPercent >= 70) { confidence -= 20; reason = "high load"; }
        else if (loadPercent >= 40) confidence -= 8;
        // AiSelfOptimizationPolicy computes a 0..10 pressure score, NOT 0..100.
        // Its pressured threshold is 1.15; an idle machine under pressure
        // must not pass the >=72 confidence gate for background mutation.
        if (pressure >= 1.15) { confidence -= 35; reason = "self pressure"; }
        else if (pressure >= 0.70) confidence -= 10;
        // Do not describe uncertain workload as stable after pressure adjustments.
        if (workload == AiWorkloadClass.Unknown)
        {
            confidence = 0;
            reason = "unknown workload";
        }

        // Invalid load telemetry is not a valid quiet-window measurement.
        // Never allow mutations merely because comparisons with NaN are false.
        if (!loadValid) { confidence = 0; reason = "invalid load telemetry"; }
        if (!utcTimestamp) { confidence = 0; reason = "invalid decision timestamp"; }
        // A cached health file from another FanAgent lifetime cannot prove
        // that the current optimizer overhead is safe for new mutations.
        if (requireFreshSelfHealth &&
            !AiCurrentSelfHealthEvidencePolicy.HasTrustedOverheadSample(selfHealth, now))
        {
            confidence = 0;
            reason = "current AI self telemetry unavailable";
        }
        confidence = Math.Clamp(confidence, 0, 100);
        // A confirmed idle interval is required for new unattended writes;
        // Background workload observations can continue without that permission.
        // Align the producer and every consumer with Engine 2: read-only
        // background sampling may continue during user activity, but no new
        // unattended mutation is approved without real idle evidence or while
        // system/self pressure is high. The centralized gate rechecks again.
        double roundedLoad = loadValid ? Math.Round(loadPercent, 1) : -1;
        double roundedPressure = Math.Round(pressure, 1);
        // Publish and authorize from the SAME values. E.g. raw 69.96% was
        // previously authorized then rounded to 70.0%, where the Engine 2
        // consumer rejected it. Use fail-closed admission at the boundary.
        bool allow = IsPublishedSnapshotEligible(workload, confidence, realIdle,
            gameActive, roundedLoad, roundedPressure);
        string resolvedReason = allow ? "stable background window" :
            reason == "stable" ? "rounded telemetry admission boundary" : reason;

        // Immutable record reuse on identical FanAgent ticks avoids an
        // allocation every 1-5 seconds. The capped 4s age always leaves
        // generous headroom for AllowsMutation's 15-second freshness gate.
        // Any changed CPU/workload/permission publishes immediately.
        while (true)
        {
            AiSharedDecisionContext? previous = Volatile.Read(ref _current);
            // Parallel producers may finish out of order. An older sample
            // must not replace newer idle/foreground evidence and authorize
            // a different trial. Keep the existing timestamp on clock rollback;
            // the consumer's UTC freshness guard then fails closed.
            if (previous != null && utcTimestamp &&
                previous.UpdatedUtc.Kind == DateTimeKind.Utc &&
                now < previous.UpdatedUtc)
                return previous;
            if (previous != null && utcTimestamp &&
                previous.UpdatedUtc.Kind == DateTimeKind.Utc &&
                now - previous.UpdatedUtc <= TimeSpan.FromSeconds(4) &&
                now >= previous.UpdatedUtc &&
                previous.Workload == workload &&
                previous.RealIdle == realIdle &&
                previous.GameActive == gameActive &&
                previous.LoadPercent == roundedLoad &&
                previous.SelfPressureScore == roundedPressure &&
                previous.MaintenanceIntervalSeconds == interval &&
                previous.Confidence == confidence &&
                previous.AllowBackgroundMutation == allow &&
                string.Equals(previous.Reason, resolvedReason, StringComparison.Ordinal))
                return previous;

            var next = new AiSharedDecisionContext(
                now, workload, realIdle, gameActive, roundedLoad, roundedPressure,
                interval, confidence, allow, resolvedReason);
            // A blind Volatile.Write could undo another producer's more recent
            // publish between its read and write. Retry only on CAS contention.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, next, previous), previous))
                return next;
        }
    }

    // Pure permission check for the exact rounded snapshot consumers see.
    // Values near strict boundaries are never silently advertised as AUTO.
    internal static bool IsPublishedSnapshotEligible(AiWorkloadClass workload,
        int confidence, bool realIdle, bool gameActive,
        double loadPercent, double selfPressure) =>
        confidence >= 72 && confidence <= 100 && realIdle && !gameActive &&
        double.IsFinite(loadPercent) && loadPercent >= 0 && loadPercent < 70 &&
        double.IsFinite(selfPressure) && selfPressure >= 0 && selfPressure < 1.15 &&
        AiSharedWorkloadPolicy.AllowsBackgroundAutomation(workload);

    internal static string RunPublishedPermissionSmoke()
    {
        const AiWorkloadClass idle = AiWorkloadClass.Idle;
        if (!IsPublishedSnapshotEligible(idle, 95, true, false, 69.9, 0.3) ||
            IsPublishedSnapshotEligible(idle, 95, true, false,
                Math.Round(69.96, 1), 0.3) ||
            IsPublishedSnapshotEligible(idle, 95, true, false, 70, 0.3) ||
            IsPublishedSnapshotEligible(idle, 95, true, false, 12, 1.2) ||
            IsPublishedSnapshotEligible(idle, 95, false, false, 12, 0.3) ||
            IsPublishedSnapshotEligible(idle, 95, true, true, 12, 0.3) ||
            IsPublishedSnapshotEligible(idle, 71, true, false, 12, 0.3) ||
            IsPublishedSnapshotEligible(idle, 95, true, false, double.NaN, 0.3) ||
            IsPublishedSnapshotEligible(idle, 95, true, false, 12, double.NaN))
            throw new InvalidOperationException(
                "AI Engine 2 published telemetry/permission boundary regressed.");
        _ = RunPublicationOrderingSmoke();
        return "AI_SHARED_PUBLISHED_ADMISSION_SMOKE_OK boundary70=true " +
            "selfPressure=true staleProducerRejected=true invalidUtcRejected=true writes=0";
    }

    // Synthetic snapshots only. Preserve any prior context if invoked in a
    // host running diagnostics; never touch OS settings or a user journal.
    private static string RunPublicationOrderingSmoke()
    {
        AiSharedDecisionContext? original = Volatile.Read(ref _current);
        DateTime t = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            Volatile.Write(ref _current, null);
            var first = Update(AiWorkloadClass.Idle, true, false, 12, null, t);
            var active = Update(AiWorkloadClass.Game, false, true, 78, null,
                t.AddSeconds(2));
            var lateOld = Update(AiWorkloadClass.Idle, true, false, 12, null,
                t.AddSeconds(1));
            if (!first.AllowBackgroundMutation || active.AllowBackgroundMutation ||
                !ReferenceEquals(lateOld, active) || !ReferenceEquals(Read(), active))
                throw new InvalidOperationException("An out-of-order AI producer overwrote newer foreground evidence.");
            var invalidUtc = Update(AiWorkloadClass.Idle, true, false, 12, null,
                DateTime.SpecifyKind(t.AddSeconds(3), DateTimeKind.Unspecified));
            if (invalidUtc.AllowBackgroundMutation || invalidUtc.Confidence != 0 ||
                AiEngine2DecisionPolicy.Evaluate(invalidUtc, t.AddSeconds(3)).Phase !=
                    AiEngine2Phase.AwaitingTelemetry)
                throw new InvalidOperationException("Non-UTC AI producer authorized a background mutation.");
            var recovered = Update(AiWorkloadClass.Idle, true, false, 12, null,
                t.AddSeconds(4));
            if (!recovered.AllowBackgroundMutation ||
                !ReferenceEquals(Read(), recovered))
                throw new InvalidOperationException("Valid UTC AI producer could not recover publication.");
        }
        finally
        {
            Volatile.Write(ref _current, original);
        }
        return "AI_SHARED_PUBLICATION_ORDER_SMOKE_OK";
    }

    public static AiSharedDecisionContext? Read() => Volatile.Read(ref _current);

    // Unified read-only AI Engine 2.0 decision for diagnostics and future trials.
    internal static AiEngine2Decision ReadEngine2Decision(DateTime nowUtc) =>
        AiEngine2DecisionPolicy.Evaluate(Volatile.Read(ref _current), nowUtc);

    public static bool AllowsMutation(int minimumConfidence = 72)
    {
        // No second, looser mutation gate: manual tweak experiments, service
        // and task A/B/A decisions must all obey the SAME Engine 2 policy,
        // including fresh idle/game evidence and exclusive trial ownership.
        var decision = ReadEngine2Decision(DateTime.UtcNow);
        return decision.Phase == AiEngine2Phase.EligibleForMeasuredTrial &&
            decision.Confidence >= Math.Max(72, minimumConfidence);
    }
}
