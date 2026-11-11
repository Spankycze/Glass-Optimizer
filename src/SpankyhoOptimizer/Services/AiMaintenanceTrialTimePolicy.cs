using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// A/B/A comparisons must not span a missing timer epoch, an agent pause or
/// a wall-clock correction. Invalid phase timestamps force owned rollback,
/// not a positive performance verdict from unrelated samples.
/// </summary>
internal static class AiMaintenanceTrialTimePolicy
{
    internal static readonly TimeSpan MaximumSampleSilence = TimeSpan.FromMinutes(5);

    internal static string? InvalidPhaseReason(
        DateTime? startedUtc, DateTime? lastSampleUtc, DateTime nowUtc,
        int verificationSamples = 0)
    {
        if (nowUtc.Kind != DateTimeKind.Utc ||
            startedUtc is not DateTime started ||
            started.Kind != DateTimeKind.Utc ||
            started > nowUtc)
            return "missing, future or non-UTC verification start";

        // Persisted journals must never credit existing measurements without
        // the timestamp that proves sampling continuity and phase identity.
        // Similarly a timestamp without its sample is inconsistent evidence.
        if (verificationSamples < 0 ||
            verificationSamples > AiMaintenanceFeedbackPolicy.RequiredCandidateSamples)
            return "A/B/A verification sample count is invalid";
        if (verificationSamples > 0 && lastSampleUtc is null)
            return "A/B/A previous samples have no timestamp";
        if (verificationSamples == 0 && lastSampleUtc is not null)
            return "A/B/A timestamp has no corresponding sample";

        if (nowUtc - started > AiMaintenanceFeedbackPolicy.MaximumVerificationDuration)
            return "A/B/A verification watchdog expired";

        // No first sample for over five minutes is also a stalled trial.
        if (lastSampleUtc is not DateTime last)
            return nowUtc - started > MaximumSampleSilence
                ? "A/B/A first sample did not arrive" : null;
        if (last.Kind != DateTimeKind.Utc || last < started || last > nowUtc)
            return "verification sample outside live phase or wall-clock rollback";

        if (nowUtc - last > MaximumSampleSilence)
            return "lost A/B/A sampling continuity";
        return null;
    }

    // Pure timestamp/journal regression: no OS service, task or registry writes.
    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        DateTime start = now.AddMinutes(-4);
        DateTime recent = now.AddSeconds(-30);

        // Ordinary cold first sample and a normal sampled phase stay valid.
        if (InvalidPhaseReason(start, null, now, 0) != null ||
            InvalidPhaseReason(start, recent, now, 2) != null ||
            InvalidPhaseReason(start, now, now, 3) != null)
            throw new InvalidOperationException("A/B/A valid journal phase was rejected.");

        // Missing sample timestamp previously let two persisted samples be
        // silently credited on the next tick; that would fake an A/B/A win.
        var failures = new (string Label, DateTime? Start, DateTime? Last,
            DateTime Now, int Count)[]
        {
            ("two samples missing time", start, null, now, 2),
            ("one sample missing time", start, null, now, 1),
            ("orphan timestamp", start, recent, now, 0),
            ("negative count", start, recent, now, -1),
            ("too many samples", start, recent, now, 4),
            ("missing start", null, null, now, 0),
            ("future start / backward clock", now.AddSeconds(1), null, now, 0),
            ("non-UTC start", DateTime.SpecifyKind(start,
                DateTimeKind.Local), recent, now, 2),
            ("non-UTC now", start, recent, DateTime.SpecifyKind(now,
                DateTimeKind.Local), 2),
            ("non-UTC sample", start, DateTime.SpecifyKind(recent,
                DateTimeKind.Unspecified), now, 2),
            ("sample before phase", start, start.AddSeconds(-1), now, 2),
            ("sample after now", start, now.AddSeconds(1), now, 2),
            ("missing first sample >5min", now.AddMinutes(-6), null, now, 0),
            ("silent sample >5min", now.AddMinutes(-7),
                now.AddMinutes(-6), now, 1),
            ("expired 30min phase", now.AddMinutes(-31), recent, now, 2)
        };
        foreach (var failure in failures)
        {
            if (InvalidPhaseReason(failure.Start, failure.Last,
                failure.Now, failure.Count) == null)
                throw new InvalidOperationException(
                    "A/B/A damaged journal was admitted: " + failure.Label);
        }

        return "AI_MAINTENANCE_TRIAL_EVIDENCE_SMOKE_OK samplesTimeConsistent=true " +
               "firstSampleWatchdog=true clockDiscontinuityRejected=true writes=0";
    }
}
