using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Measured benefit does not justify indefinitely stopping Windows services.
/// Verified A/B/A changes are periodically relinquished so Windows can use
/// the service and AI can observe a fresh baseline before reconsidering it.
/// </summary>
internal static class AiVerifiedServiceHoldPolicy
{
    internal static readonly TimeSpan MaximumHold = TimeSpan.FromHours(6);

    internal static bool ShouldRelease(AiSystemRuntimeItemState state, DateTime nowUtc)
    {
        if (!state.Kind.Equals("service", StringComparison.OrdinalIgnoreCase) ||
            !state.Managed || !state.Sleeping || !state.PerformanceVerified)
            return false;

        return state.LastChangedUtc is not DateTime changed ||
               changed > nowUtc || nowUtc - changed >= MaximumHold;
    }

    internal static bool ServiceStartModeStillOwned(
        int? originalStart, int liveStart)
    {
        if (originalStart is not (2 or 3))
            return false;
        int original = originalStart.Value;
        int expected = original == 2 ? 3 : original;
        return expected == liveStart;
    }

    // A service can be restarted externally during an AI-owned hold. The
    // restart proves demand, never ownership of a subsequently edited Start
    // mode. Only our own Start mode may be rolled back to its original value.
    internal static string WakeupDecision(int? originalStart, int liveStart)
    {
        if (originalStart is not (2 or 3))
            return "external";
        if (ServiceStartModeStillOwned(originalStart, liveStart))
            return "restore-owned";
        if (liveStart == originalStart)
            return "already-restored";
        return "external";
    }

    internal static bool RequiresRestoreStartWrite(int? originalStart, int liveStart) =>
        WakeupDecision(originalStart, liveStart) == "restore-owned" &&
        originalStart != liveStart;

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var state = new AiSystemRuntimeItemState(
            "service", "Fixture", true, true, string.Empty, null, null,
            2, null, true, 0, "verified",
            LastChangedUtc: now.AddHours(-5),
            PerformanceVerified: true);
        if (ShouldRelease(state, now) ||
            !ShouldRelease(state with { LastChangedUtc = now.AddHours(-6) }, now) ||
            !ShouldRelease(state with { LastChangedUtc = null }, now) ||
            !ShouldRelease(state with { LastChangedUtc = now.AddMinutes(1) }, now) ||
            ShouldRelease(state with { Kind = "task", LastChangedUtc = null }, now) ||
            ShouldRelease(state with { Managed = false, LastChangedUtc = null }, now) ||
            ShouldRelease(state with { PerformanceVerified = false,
                LastChangedUtc = null }, now) ||
            !ServiceStartModeStillOwned(2, 3) ||
            !ServiceStartModeStillOwned(3, 3) ||
            ServiceStartModeStillOwned(2, 4) ||
            ServiceStartModeStillOwned(null, 3))
            throw new InvalidOperationException(
                "AI verified-service hold expiration or external start-mode guard regressed.");

        return "AI_SERVICE_VERIFIED_HOLD_SMOKE_OK cap6h=true " +
            "unknownLegacyAgeRestores=true manualStartOverridePreserved=true writes=0";
    }
}