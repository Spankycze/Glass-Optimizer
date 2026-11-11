using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Explicit before-write ownership intent persists across agent crashes.
/// An unknown phase never authorizes reapplying an operating system change.
/// </summary>
internal static class AiMaintenanceWriteAheadPolicy
{
    internal const string ServiceStop = "service-stop";
    internal const string TaskDisable = "task-disable";
    internal const string DriverDisable = "driver-disable";

    internal static bool IsPending(AiSystemRuntimeItemState state) =>
        state.Managed && !string.IsNullOrWhiteSpace(state.PendingMutation);

    // An imported/incomplete intent without confirmed AI ownership is NOT
    // permission to mutate Windows. Keep it durable for manual recovery,
    // rather than accidentally considering it a new experiment candidate.
    internal static bool IsUnownedPending(AiSystemRuntimeItemState state) =>
        !state.Managed && !string.IsNullOrWhiteSpace(state.PendingMutation);

    // A confirmed A/B/A win still changes Windows for a second time.
    // The same durable crash-recovery protocol as initial Candidate B MUST
    // precede this mutation. Reject stale phases or unknown originals.
    internal static AiSystemRuntimeItemState CreateReapplyIntent(
        AiSystemRuntimeItemState reference)
    {
        if (!reference.Managed || reference.Sleeping ||
            reference.VerificationStartedUtc is null ||
            !string.Equals(reference.VerificationPhase, "ReferenceA",
                StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(reference.PendingMutation))
            throw new InvalidOperationException(
                "A/B/A reapply requires an owned, complete Reference A phase.");

        string mutation;
        if (string.Equals(reference.Kind, "service", StringComparison.OrdinalIgnoreCase) &&
            reference.OriginalServiceStart is 2 or 3)
            mutation = ServiceStop;
        else if (string.Equals(reference.Kind, "task", StringComparison.OrdinalIgnoreCase) &&
                 reference.OriginalTaskEnabled == true)
            mutation = TaskDisable;
        else
            throw new InvalidOperationException(
                "A/B/A reapply cannot prove the original Windows state.");

        return reference with
        {
            Sleeping = true,
            VerificationPhase = "CandidateB",
            PendingMutation = mutation
        };
    }

    // Rollback may itself be a two-step Windows write (restore Start mode,
    // then start service). Persist original ownership before the FIRST write,
    // so an agent crash between those steps cannot abandon a stopped service.
    internal static AiSystemRuntimeItemState CreateRollbackIntent(
        AiSystemRuntimeItemState active)
    {
        if (!active.Managed || !string.IsNullOrWhiteSpace(active.PendingMutation))
            throw new InvalidOperationException(
                "AI rollback intent requires exclusive managed Windows ownership.");
        string intent;
        if (string.Equals(active.Kind, "service", StringComparison.OrdinalIgnoreCase) &&
            active.OriginalServiceStart is 2 or 3)
            intent = ServiceStop;
        else if (string.Equals(active.Kind, "task", StringComparison.OrdinalIgnoreCase) &&
                 active.OriginalTaskEnabled == true)
            intent = TaskDisable;
        else
            throw new InvalidOperationException(
                "AI rollback intent cannot prove the original Windows setting.");
        return active with { PendingMutation = intent };
    }

    // Avoid allocating kind.ToLowerInvariant() for each restart intent.
    // Persisted Kind uses the same case-insensitive identity as the owner
    // dictionary; unknown kinds and unknown mutation strings fail closed.
    internal static bool IsExpected(string kind, string? mutation) =>
        (string.Equals(kind, "service", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(mutation, ServiceStop, StringComparison.Ordinal)) ||
        (string.Equals(kind, "task", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(mutation, TaskDisable, StringComparison.Ordinal)) ||
        (string.Equals(kind, "driver", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(mutation, DriverDisable, StringComparison.Ordinal));

    internal static string ServiceRecoveryState(int? original, int actualStart, bool running)
    {
        // Corrupt pre-upgrade journals with boot/system/disabled originals
        // cannot establish AI ownership of a reversible service stop.
        if (original is not (2 or 3))
            return "external";
        int start = original.Value;
        if (actualStart != start && (start != 2 || actualStart != 3))
            return "external";
        if (running && actualStart == start)
            return "unchanged";
        return "restore";
    }

    internal static string DriverRecoveryState(int? original, int actualStart) =>
        // A valid AI driver mutation always began at Demand Start (3).
        // Never recover an invalid old journal by writing Boot/System/Auto.
        original != 3 ? "external" :
        actualStart == 3 ? "unchanged" :
        actualStart == 4 ? "restore" : "external";

    // SCM may return from a registry update before another component
    // replaces that Start mode. Never clear the durable owner journal
    // until a fresh read confirms the original, safely owned Demand mode.
    internal static string? DriverRestoreReadbackReason(
        int? original, int currentStart) =>
        original == 3 && currentStart == 3
            ? null : "AI driver original Demand Start was not restored";

    internal static string? DriverDisableReadbackReason(int currentStart) =>
        currentStart == 4 ? null :
        "AI driver disable was not confirmed as Start=Disabled";

    internal static string TaskRecoveryState(bool? originallyEnabled, bool enabled) =>
        originallyEnabled != true ? "external" :
        enabled ? "unchanged" : "restore";

    internal static string RunFunctionalSmoke()
    {
        var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        AiSystemRuntimeItemState pending = new(
            "service", "Fixture", true, true, "", null, null, 2, null,
            true, 0, "intent", VerificationStartedUtc: now,
            PendingMutation: ServiceStop);
        string json = System.Text.Json.JsonSerializer.Serialize(pending);
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize<AiSystemRuntimeItemState>(json);
        var oldJson = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        oldJson.Remove("PendingMutation");
        var previous = System.Text.Json.JsonSerializer.Deserialize<AiSystemRuntimeItemState>(
            oldJson.ToJsonString());
        if (!IsPending(pending) || !IsExpected("service", pending.PendingMutation) ||
            IsExpected("task", pending.PendingMutation) ||
            IsExpected("service", "unknown") ||
            IsPending(pending with { PendingMutation = "" }) ||
            roundtrip?.PendingMutation != ServiceStop ||
            previous?.PendingMutation != "" ||
            ServiceRecoveryState(2, 2, true) != "unchanged" ||
            ServiceRecoveryState(2, 2, false) != "restore" ||
            ServiceRecoveryState(2, 3, false) != "restore" ||
            ServiceRecoveryState(2, 4, false) != "external" ||
            DriverRecoveryState(3, 3) != "unchanged" ||
            DriverRecoveryState(3, 4) != "restore" ||
            DriverRecoveryState(3, 2) != "external" ||
            TaskRecoveryState(true, true) != "unchanged" ||
            TaskRecoveryState(true, false) != "restore" ||
            TaskRecoveryState(null, false) != "external")
            throw new InvalidOperationException(
                "AI write-ahead intent restart classification or legacy persistence regressed.");
        return "AI_MAINTENANCE_WRITE_AHEAD_SMOKE_OK service=true task=true driver=true " +
            "crashBeforeWrite=true crashAfterWrite=true externalOverride=true " +
            "json=true legacyDefault=true writes=0";
    }
}