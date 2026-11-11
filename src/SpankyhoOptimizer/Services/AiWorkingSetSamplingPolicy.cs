using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Throttle passive native WorkingSet64 queries independently of frequent CPU
/// process snapshots. A reused value must retain its original observation
/// timestamp; it can NEVER count as another A/B/A RAM observation.
/// </summary>
internal static class AiWorkingSetSamplingPolicy
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(20);

    internal static bool ShouldRefresh(DateTime lastUtc, DateTime nowUtc) =>
        lastUtc == default ||
        nowUtc < lastUtc ||
        nowUtc - lastUtc >= RefreshInterval;

    internal static bool MayReadUniqueInstance(int instanceCount) =>
        instanceCount == 1;

    internal static bool IsFresh(DateTime measuredUtc, DateTime snapshotUtc) =>
        measuredUtc != default &&
        measuredUtc <= snapshotUtc &&
        snapshotUtc - measuredUtc <= MaximumAge;

    internal static string RunFunctionalSmoke()
    {
        var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        if (!MayReadUniqueInstance(1) ||
            MayReadUniqueInstance(0) ||
            MayReadUniqueInstance(2) ||
            MayReadUniqueInstance(-1) ||
            !ShouldRefresh(default, now) ||
            ShouldRefresh(now, now.AddSeconds(7)) ||
            !ShouldRefresh(now, now.AddSeconds(8)) ||
            !ShouldRefresh(now, now.AddSeconds(-1)) ||
            !IsFresh(now.AddSeconds(-8), now) ||
            IsFresh(now.AddSeconds(-21), now) ||
            IsFresh(now.AddSeconds(1), now) ||
            IsFresh(default, now))
            throw new InvalidOperationException(
                "Native RAM read cadence accepted stale, future, or duplicate samples.");
        return "AI_WORKING_SET_CADENCE_SMOKE_OK minIntervalMs=8000 " +
            "maxSampleAgeMs=20000 timeRollbackRefresh=true " +
            "cachedReadingNeverIndependent=true multiInstanceSkipsNativeRam=true writes=0";
    }
}
