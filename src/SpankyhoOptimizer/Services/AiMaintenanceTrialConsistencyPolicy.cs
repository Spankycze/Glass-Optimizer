using System;
using System.ServiceProcess;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Checks actual phase readback before any next A/B/A performance sample.
/// External state change invalidates attribution and gives Windows/user
/// control back; it must not be counted as measured no-benefit.
/// </summary>
internal static class AiMaintenanceTrialConsistencyPolicy
{
    internal static bool MustRestoreForWorkload(
        AiWorkloadClass workload, bool gameActive) =>
        // A readable CPU counter alone does not prove that a game or user
        // activity is absent. An unknown workload ends the owned trial.
        gameActive || AiSharedWorkloadPolicy.IsInteractionSensitive(workload);

    internal static string? ServiceMismatch(
        ServiceControllerStatus status, int currentStart,
        int? originalStart, string? phase)
    {
        if (!AiMaintenanceRevalidationPolicy.IsRecognizedTrialPhase(phase))
            return "invalid A/B/A service phase";
        if (originalStart is not (2 or 3))
            return "original service start mode missing or unsafe";
        bool reference = string.Equals(phase, "ReferenceA",
            StringComparison.OrdinalIgnoreCase);
        ServiceControllerStatus expected = reference
            ? ServiceControllerStatus.Running
            : ServiceControllerStatus.Stopped;
        if (status != expected)
            return $"service was externally changed from expected {expected} to {status}";

        if (originalStart is int start)
        {
            int expectedStart = !reference && start == 2 ? 3 : start;
            if (currentStart != expectedStart)
                return $"service start type externally changed (expected {expectedStart}, found {currentStart})";
        }

        return null;
    }

    internal static string? TaskMismatch(
        TaskRuntimeState task, string? phase)
    {
        if (!AiMaintenanceRevalidationPolicy.IsRecognizedTrialPhase(phase))
            return "invalid A/B/A task phase";
        if (!AiMaintenanceRevalidationPolicy.MatchesTrialPhase(task.Enabled, phase))
            return "task enable state changed outside AI";
        if (task.Running)
            return string.Equals(phase, "ReferenceA", StringComparison.OrdinalIgnoreCase)
                ? "task is running during reference A; measurement is not comparable"
                : "task started during Candidate B";
        return null;
    }

    // A service/task being reactivated in Candidate B is evidence that
    // Windows or the user needs it. Their disabling it during Reference A
    // is a manual preference, not evidence of failed AI optimization.
    internal static bool IsExternalNeed(string? phase, bool active) =>
        // Only an ACTIVE service during a VERIFIED Candidate B trial is
        // evidence that the user/Windows needs it. Corrupt phase data must
        // not poison adaptive learning or raise rollback counters.
        string.Equals(phase, "CandidateB", StringComparison.OrdinalIgnoreCase) && active;

    internal static string RunFunctionalSmoke()
    {
        var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var task = new TaskRuntimeState(true, false, now.AddHours(4), now.AddHours(-4));
        if (ServiceMismatch(ServiceControllerStatus.Stopped, 3, 2, "CandidateB") != null ||
            ServiceMismatch(ServiceControllerStatus.Running, 2, 2, "ReferenceA") != null ||
            ServiceMismatch(ServiceControllerStatus.Running, 3, 2, "CandidateB") == null ||
            ServiceMismatch(ServiceControllerStatus.Stopped, 2, 2, "CandidateB") == null ||
            ServiceMismatch(ServiceControllerStatus.Stopped, 2, 2, "ReferenceA") == null ||
            ServiceMismatch(ServiceControllerStatus.Running, 3, 2, "ReferenceA") == null ||
            TaskMismatch(task with { Enabled = false }, "CandidateB") != null ||
            TaskMismatch(task, "ReferenceA") != null ||
            TaskMismatch(task, "CandidateB") == null ||
            TaskMismatch(task with { Enabled = false, Running = true }, "CandidateB") == null ||
            !MustRestoreForWorkload(AiWorkloadClass.Interactive, false) ||
            !MustRestoreForWorkload(AiWorkloadClass.Game, false) ||
            !MustRestoreForWorkload(AiWorkloadClass.Idle, true) ||
            MustRestoreForWorkload(AiWorkloadClass.Background, false) ||
            !MustRestoreForWorkload(AiWorkloadClass.Unknown, false) ||
            ServiceMismatch(ServiceControllerStatus.Stopped, 3, 2, null) == null ||
            ServiceMismatch(ServiceControllerStatus.Stopped, 3, 2, "Unknown") == null ||
            TaskMismatch(task with { Enabled = false }, null) == null ||
            TaskMismatch(task with { Enabled = false }, "Unknown") == null ||
            !IsExternalNeed("CandidateB", true) ||
            IsExternalNeed("ReferenceA", true) ||
            IsExternalNeed("CandidateB", false) ||
            IsExternalNeed(null, true) ||
            IsExternalNeed("Unknown", true))
            throw new InvalidOperationException(
                "AI A/B/A phase consistency or interactive-workload restoration regressed.");
        return "AI_ABA_PHASE_CONSISTENCY_SMOKE_OK service=4 task=4 " +
            "interactiveRestore=true backgroundPreserved=true externalNeedAttribution=true readOnly=true writes=0";
    }
}