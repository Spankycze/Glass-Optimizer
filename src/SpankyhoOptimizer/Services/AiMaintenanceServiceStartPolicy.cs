namespace SpankyhoOptimizer.Services;

// Only reversible auto/manual Windows services can enter a new AI A/B/A trial.
// Also rejects any user/Windows start-mode change while reference A is live.
internal static class AiMaintenanceServiceStartPolicy
{
    // A missing or malformed SCM Start registry value MUST NOT be mistaken
    // for Demand Start=3. Unknown -1 is rejected by every admission and
    // restore-readback gate rather than giving AI false Windows ownership.
    internal static int NormalizeStartValue(object? raw) =>
        raw is int start && start is >= 0 and <= 4 ? start : -1;

    // Unknown SCM registry data is missing evidence, not proof Windows
    // restored or manually overrode an AI-disabled resource. Keep the
    // durable owner until a valid live mode can be read again.
    internal static bool MustRetainOwnerOnUnknownStart(int liveStart) =>
        liveStart is < 0 or > 4;

    internal static string? BlockReason(int liveStart, int? expectedOriginalStart)
    {
        if (liveStart is not (2 or 3))
            return "service is not automatic or demand-start; trial blocked";
        if (expectedOriginalStart is int original && liveStart != original)
            return "service start mode changed externally during A/B/A";
        return null;
    }
}
