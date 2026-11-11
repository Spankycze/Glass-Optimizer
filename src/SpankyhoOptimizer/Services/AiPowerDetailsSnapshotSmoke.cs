using System;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// Production cache behavior tested without real powercfg, HKLM or user settings.
internal static class AiPowerDetailsSnapshotSmoke
{
    internal static Task RunAsync()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(
                "AI_POWER_DETAILS_CACHE_SMOKE_FAIL: " + name);
        }

        DateTime now = new(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        var cache = new AiPowerDetailsSnapshotCache();
        const string plan = "00000000-0000-0000-0000-000000000001";
        const string other = "00000000-0000-0000-0000-000000000002";
        var snapshot = new AiPowerDetailsSnapshot(plan, "managed=3", "tasks=1", now);

        // 1-5: Empty cache cannot silently synthesize detail states.
        Check(!cache.TryGet(plan, now, out _), "empty cache miss");
        Check(!cache.TryGet(null, now, out _), "empty with missing plan");
        cache.Store(snapshot);
        Check(cache.TryGet(plan, now, out var first) && ReferenceEquals(first, snapshot),
            "immediate cache hit");
        Check(cache.TryGet(plan.ToUpperInvariant(), now, out _),
            "case-insensitive plan GUID");
        Check(cache.TryGet(" " + plan + " ", now, out _),
            "trailing spaces ignored");

        // 6-11: Time-to-live explicit and bounded, including clock reversal.
        Check(cache.TryGet(plan, now.AddSeconds(10), out _), "within ttl");
        Check(cache.TryGet(plan, now.AddSeconds(20), out _), "at ttl boundary");
        Check(!cache.TryGet(plan, now.AddSeconds(20.001), out _), "ttl expired");
        Check(!cache.TryGet(plan, now.AddSeconds(-1), out _), "clock skew invalid");
        Check(!cache.TryGet(plan, now.AddHours(24), out _), "day-old snapshot invalid");
        Check(!cache.TryGet(plan, DateTime.MaxValue, out _), "overflow-safe old entry");

        // 12-15: Cached contents cannot bleed into another active power plan.
        Check(!cache.TryGet(other, now, out _), "changed active plan misses");
        Check(!cache.TryGet("", now, out _), "missing plan misses");
        Check(!cache.TryGet(" ", now, out _), "whitespace plan misses");
        Check(cache.TryGet(plan, now, out var recovered) &&
            recovered!.ManagedSummary == "managed=3" &&
            recovered.SystemSummary == "tasks=1", "original plan retained");

        // 16-19: Explicit invalidation for user-applied power changes.
        cache.Invalidate();
        Check(!cache.TryGet(plan, now, out _), "invalidation removes result");
        cache.Store(snapshot with { PlanGuid = string.Empty });
        Check(!cache.TryGet(plan, now, out _), "do not cache unknown active plan");
        cache.Store(snapshot);
        cache.Store(snapshot with { PlanGuid = other, CapturedUtc = now.AddSeconds(4) });
        Check(!cache.TryGet(plan, now.AddSeconds(5), out _), "replacement plan invalidates old");
        Check(cache.TryGet(other, now.AddSeconds(5), out _),
            "replacement plan available");

        // 20-24: A saved result can satisfy repeated expanded redraws
        // without simulated expensive sampling; keep fresh telemetry elsewhere.
        cache.Invalidate();
        int expensiveReads = 0;
        AiPowerDetailsSnapshot GetDetails(DateTime clock, string activePlan)
        {
            if (cache.TryGet(activePlan, clock, out var hit))
                return hit!;
            expensiveReads++;
            var result = new AiPowerDetailsSnapshot(activePlan,
                $"read={expensiveReads}", "status", clock);
            cache.Store(result);
            return result;
        }
        Check(GetDetails(now, plan).ManagedSummary == "read=1" &&
            expensiveReads == 1, "first expanded details fetched");
        Check(GetDetails(now.AddSeconds(3), plan).ManagedSummary == "read=1" &&
            expensiveReads == 1, "rapid page refresh cached");
        Check(GetDetails(now.AddSeconds(19), plan).ManagedSummary == "read=1" &&
            expensiveReads == 1, "later refresh cached");
        Check(GetDetails(now.AddSeconds(21), plan).ManagedSummary == "read=2" &&
            expensiveReads == 2, "freshness triggers resample");
        Check(GetDetails(now.AddSeconds(22), other).ManagedSummary == "read=3" &&
            expensiveReads == 3, "power plan switch forces refresh");

        // 25: Null snapshot is a programming error, not an empty payload.
        bool nullRejected = false;
        try { cache.Store(null!); }
        catch (ArgumentNullException) { nullRejected = true; }
        Check(nullRejected, "null store rejected");
        return Task.CompletedTask;
    }
}
