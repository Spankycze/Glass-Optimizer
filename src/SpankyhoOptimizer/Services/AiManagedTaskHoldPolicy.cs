using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// A disabled Windows scheduled task must never be suppressed indefinitely
/// merely because one quiet A/B/A trial found a benefit. Keep its pre-disable
/// schedule deadline: disabled tasks may no longer expose NextRunTime via COM.
/// </summary>
internal static class AiManagedTaskHoldPolicy
{
    internal static readonly TimeSpan BeforeScheduledRun = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan MaximumVerifiedHold = TimeSpan.FromHours(2);
    internal static readonly TimeSpan RecoveryCooldown = TimeSpan.FromHours(1);

    internal static bool SavedScheduleIsDueSoon(DateTime? originalNextRunUtc, DateTime nowUtc) =>
        originalNextRunUtc is DateTime next &&
        next <= nowUtc.Add(BeforeScheduledRun);

    internal static string? ReleaseReason(AiSystemRuntimeItemState state, DateTime nowUtc)
    {
        if (!state.Kind.Equals("task", StringComparison.OrdinalIgnoreCase) ||
            !state.Managed || !state.Sleeping)
            return null;

        // An already-due task also needs release. A disabled task can have
        // stale/empty next-run COM metadata: use the original saved deadline.
        if (SavedScheduleIsDueSoon(state.OriginalTaskNextRunUtc, nowUtc))
            return "scheduled run is approaching or overdue";

        if (!state.PerformanceVerified)
            return null; // A/B/A in flight has its own strict 30m watchdog.

        // Successful optimization is bounded too, even for demand-triggered
        // tasks with no NextRunUtc. Old persisted records are released safely.
        if (state.LastChangedUtc is not DateTime lastChanged ||
            lastChanged > nowUtc ||
            nowUtc - lastChanged >= MaximumVerifiedHold)
            return "maximum verified hold elapsed or missing valid hold timestamp";

        return null;
    }

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var test = new AiSystemRuntimeItemState(
            "task", @"\Microsoft\Windows\Fixture", true, true, string.Empty,
            null, null, null, true, false, 0, "verified",
            LastChangedUtc: now.AddMinutes(-20),
            PerformanceVerified: true,
            OriginalTaskNextRunUtc: now.AddMinutes(31));
        if (ReleaseReason(test, now) != null ||
            !SavedScheduleIsDueSoon(now.AddMinutes(29), now) ||
            SavedScheduleIsDueSoon(now.AddMinutes(31), now) ||
            SavedScheduleIsDueSoon(null, now) ||
            ReleaseReason(test with { OriginalTaskNextRunUtc = now.AddMinutes(29) }, now) == null ||
            ReleaseReason(test with { OriginalTaskNextRunUtc = now.AddHours(-3) }, now) == null ||
            ReleaseReason(test with { OriginalTaskNextRunUtc = null }, now) != null ||
            ReleaseReason(test with { OriginalTaskNextRunUtc = null,
                LastChangedUtc = now.AddHours(-2) }, now) == null ||
            ReleaseReason(test with { OriginalTaskNextRunUtc = null,
                LastChangedUtc = null }, now) == null ||
            ReleaseReason(test with { OriginalTaskNextRunUtc = null,
                LastChangedUtc = now.AddMinutes(1) }, now) == null ||
            ReleaseReason(test with { Managed = false,
                OriginalTaskNextRunUtc = now }, now) != null ||
            ReleaseReason(test with { Sleeping = false,
                OriginalTaskNextRunUtc = now }, now) != null ||
            ReleaseReason(test with { PerformanceVerified = false,
                OriginalTaskNextRunUtc = null, LastChangedUtc = now.AddHours(-3) }, now) != null ||
            ReleaseReason(test with { PerformanceVerified = false,
                OriginalTaskNextRunUtc = now.AddMinutes(25) }, now) == null)
            throw new InvalidOperationException(
                "AI-managed task due-time/hold-limit recovery policy regression.");

        // New serialized field survives agent restarts and remains optional
        // for data written by releases before this one.
        string json = System.Text.Json.JsonSerializer.Serialize(test);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AiSystemRuntimeItemState>(json);
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        legacy.Remove("OriginalTaskNextRunUtc");
        var old = System.Text.Json.JsonSerializer.Deserialize<AiSystemRuntimeItemState>(legacy.ToJsonString());
        if (restored?.OriginalTaskNextRunUtc != test.OriginalTaskNextRunUtc ||
            old?.OriginalTaskNextRunUtc != null)
            throw new InvalidOperationException("AI task deadline snapshot persistence is incompatible.");

        return "AI_TASK_VERIFIED_HOLD_SMOKE_OK due30m=true cap2h=true " +
            "legacyRelease=true pendingStillGuarded=true json=true writes=0";
    }
}