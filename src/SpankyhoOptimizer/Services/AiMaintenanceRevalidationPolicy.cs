using System;
using System.ServiceProcess;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Read-only, last-moment safety policy for AI-owned experimental Windows tasks.
/// A successful load A/B/A comparison does not authorize changing a task whose
/// live scheduler state has changed since the candidate observation.
/// </summary>
internal static class AiMaintenanceRevalidationPolicy
{
    // A Stop and an optional Automatic(2)->Demand(3) write form ONE
    // experiment. Until both readbacks match the claimed Candidate B state,
    // the durable write-ahead owner must not be marked complete.
    internal static string? ServiceCandidateReadbackReason(
        int? originalStart, int observedStart,
        ServiceControllerStatus observedStatus) =>
        originalStart is not (2 or 3)
            ? "AI service candidate original Start is not Automatic/Demand" :
        observedStatus != ServiceControllerStatus.Stopped
            ? "AI service candidate did not remain stopped" :
        observedStart != 3
            ? "AI service candidate did not confirm Demand Start" :
        null;

    internal static string? TaskReapplyBlockReason(TaskRuntimeState task, DateTime nowUtc)
    {
        if (!task.Enabled)
            return "task was disabled outside AI during reference A";
        if (task.Running)
            return "task is now running";

        if (task.LastRunUtc is DateTime last &&
            (last > nowUtc || nowUtc - last <= TimeSpan.FromHours(2)))
            return "task was run recently or its last run is in the future";

        // Never disable a scheduled task with an overdue deadline: Windows
        // may legitimately launch it as soon as scheduling conditions permit.
        if (task.NextRunUtc is DateTime next &&
            next <= nowUtc.AddMinutes(30))
            return "scheduled task is overdue or due within 30 minutes";

        return null;
    }

    // A task can start AND finish between SetEnabled(false) and readback.
    // Enabled=false / Running=false alone would count that polluted A/B/A
    // measurement as a valid gain. Compare durable Task Scheduler LastRunTime
    // on BOTH sides; unknown/lost or non-UTC evidence fails closed.
    internal static string? TaskDisableReadbackReason(
        TaskRuntimeState before, TaskRuntimeState after)
    {
        if (after.Enabled)
            return "Task Scheduler still reports Enabled=true after AI disable";
        if (after.Running)
            return "task began running while AI disabled scheduling";
        if (before.LastRunUtc is DateTime previous && previous.Kind != DateTimeKind.Utc ||
            after.LastRunUtc is DateTime current && current.Kind != DateTimeKind.Utc)
            return "task last-run timestamp is not UTC";
        if (before.LastRunUtc != after.LastRunUtc)
            return "task executed or LastRunTime changed during AI disable";
        return null;
    }

    internal static bool IsRecognizedTrialPhase(string? phase) =>
        string.Equals(phase, "ReferenceA", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(phase, "CandidateB", StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesTrialPhase(
        bool enabled, string? phase) =>
        string.Equals(phase, "ReferenceA", StringComparison.OrdinalIgnoreCase)
            ? enabled
            : string.Equals(phase, "CandidateB", StringComparison.OrdinalIgnoreCase) && !enabled;

    internal static string RunDecisionSmoke()
    {
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        TaskRuntimeState good = new(true, false, now.AddHours(3), now.AddHours(-3));
        TaskRuntimeState disabled = good with { Enabled = false };
        TaskRuntimeState neverRun = good with { LastRunUtc = null };
        if (TaskDisableReadbackReason(good, disabled) != null ||
            TaskDisableReadbackReason(neverRun,
                neverRun with { Enabled = false }) != null ||
            TaskDisableReadbackReason(good, disabled with { Enabled = true }) == null ||
            TaskDisableReadbackReason(good, disabled with { Running = true }) == null ||
            // A task completed in the SetEnabled/GetRuntimeState race and is
            // no longer Running; the changed LastRunTime must still abort B.
            TaskDisableReadbackReason(good,
                disabled with { LastRunUtc = now.AddSeconds(-1) }) == null ||
            TaskDisableReadbackReason(neverRun,
                disabled with { LastRunUtc = now.AddSeconds(-1) }) == null ||
            TaskDisableReadbackReason(good,
                disabled with { LastRunUtc = null }) == null ||
            TaskDisableReadbackReason(good,
                disabled with { LastRunUtc = now.AddHours(-4) }) == null ||
            TaskDisableReadbackReason(good,
                disabled with { LastRunUtc = DateTime.SpecifyKind(
                    now.AddHours(-3), DateTimeKind.Local) }) == null ||
            TaskReapplyBlockReason(good, now) != null ||
            TaskReapplyBlockReason(good with { Running = true }, now) == null ||
            TaskReapplyBlockReason(good with { Enabled = false }, now) == null ||
            TaskReapplyBlockReason(good with { LastRunUtc = now.AddMinutes(-20) }, now) == null ||
            TaskReapplyBlockReason(good with { LastRunUtc = now.AddMinutes(10) }, now) == null ||
            TaskReapplyBlockReason(good with { NextRunUtc = now.AddMinutes(29) }, now) == null ||
            TaskReapplyBlockReason(good with { NextRunUtc = now.AddMinutes(-2) }, now) == null ||
            TaskReapplyBlockReason(good with { NextRunUtc = now.AddHours(-4) }, now) == null ||
            TaskReapplyBlockReason(good with { NextRunUtc = now.AddMinutes(31) }, now) != null ||
            MatchesTrialPhase(true, "CandidateB") ||
            !MatchesTrialPhase(false, "CandidateB") ||
            !MatchesTrialPhase(true, "ReferenceA") ||
            MatchesTrialPhase(false, "ReferenceA") ||
            MatchesTrialPhase(false, null) ||
            MatchesTrialPhase(false, string.Empty) ||
            MatchesTrialPhase(false, "Reference") ||
            MatchesTrialPhase(false, "Candidate") ||
            MatchesTrialPhase(false, "Unknown") ||
            IsRecognizedTrialPhase(null) ||
            IsRecognizedTrialPhase("Candidate") ||
            !IsRecognizedTrialPhase("ReferenceA") ||
            !IsRecognizedTrialPhase("CandidateB"))
            throw new InvalidOperationException("AI Task A/B/A live-state admission policy regressed.");

        return "AI_MAINTENANCE_REVALIDATION_SMOKE_OK lastRun=2h " +
            "nextRun=30m futureClock=true runningGuard=true phaseOverride=true " +
            "readOnly=true writes=0";
    }
}