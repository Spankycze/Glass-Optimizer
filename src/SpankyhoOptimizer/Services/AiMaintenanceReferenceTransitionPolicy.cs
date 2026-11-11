using System;
using System.ServiceProcess;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// The B->Reference A restoration is itself a Windows state mutation.
/// Reconfirm ownership and scheduler/SCM state directly before restoring
/// the original reference, not only at the start of the CPU sampling tick.
/// </summary>
internal static class AiMaintenanceReferenceTransitionPolicy
{
    internal static string? ServiceBlockReason(
        int? originalStart, int liveStart, ServiceControllerStatus liveStatus)
    {
        if (originalStart is not (2 or 3) ||
            !AiMaintenanceOwnershipPolicy.ServiceStartStillOwned(
                originalStart, liveStart, "CandidateB"))
            return "service start mode changed since Candidate B";
        if (liveStatus != ServiceControllerStatus.Stopped)
            return "service was started or is transitioning outside Candidate B";
        return null;
    }

    internal static string? TaskBlockReason(TaskRuntimeState live)
    {
        if (live.Running)
            return "task started executing during Candidate B";
        if (live.Enabled)
            return "task was enabled outside Candidate B";
        return null;
    }

    // A successful SCM/COM write call is NOT proof of a completed rollback.
    // The owner journal must be released only after Windows confirms that
    // its original start mode and, if originally running, service status
    // have actually been restored.
    internal static string? ServiceRestoreReadbackReason(
        int? originalStart, int liveStart,
        bool wasRunningBefore, ServiceControllerStatus liveStatus)
    {
        if (originalStart is not (2 or 3) || liveStart != originalStart)
            return "original service start mode was not restored";
        if (wasRunningBefore && liveStatus != ServiceControllerStatus.Running)
            return "service has not returned to the Running state";
        return null;
    }

    internal static string? TaskRestoreReadbackReason(
        bool? originallyEnabled, TaskRuntimeState live) =>
        originallyEnabled == true && live.Enabled
            ? null : "task scheduling was not restored to Enabled=true";
}
