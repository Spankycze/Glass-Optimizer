using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// In-memory fault-injection harness. Shares the PRODUCTION recovery decision
/// policy but never opens the registry, COM, ServiceController, or a real file.
/// </summary>
internal static class AiMaintenanceCrashSimulationPolicy
{
    private enum Boundary
    {
        IntentWriteFailed,
        AfterDurableIntent,
        AfterOsMutation,
        CompletionWriteFailed,
        AfterCommitted,
        ExternalManualOverride
    }

    private readonly record struct Outcome(
        bool MayIssueWindowsWrite,
        bool JournalPending,
        string RecoveryDecision,
        int OsMutations,
        int RecoveryMutations);

    private static Outcome Simulate(string kind, Boundary boundary)
    {
        // Simulate previously enabled Windows service/task/driver.
        int originalStart = kind == "service" ? 2 : 3;
        int liveStart = originalStart;
        bool serviceRunning = true;
        bool taskEnabled = true;
        bool persistedIntent = false;
        int changes = 0;

        // 1. Crash/write failure before a DURABLE journal is a strict veto.
        if (boundary == Boundary.IntentWriteFailed)
            return new Outcome(false, false, "veto", 0, 0);

        persistedIntent = true;
        if (boundary != Boundary.AfterDurableIntent)
        {
            // 2. The same simulated Windows mutation as the real agent.
            if (kind == "service") { serviceRunning = false; liveStart = 3; }
            else if (kind == "task") taskEnabled = false;
            else liveStart = 4;
            changes++;
        }

        // 3. Concurrent Windows/manual change after AI write wins.
        if (boundary == Boundary.ExternalManualOverride)
        {
            if (kind == "service") liveStart = 4;
            else if (kind == "task") taskEnabled = true;
            else liveStart = 2;
        }

        // 4. Completion persisted: normal managed lifecycle, no recovery.
        if (boundary == Boundary.AfterCommitted)
        {
            persistedIntent = false;
            return new Outcome(true, persistedIntent, "committed", changes, 0);
        }

        string decision = kind switch
        {
            "service" => AiMaintenanceWriteAheadPolicy.ServiceRecoveryState(
                originalStart, liveStart, serviceRunning),
            "task" => AiMaintenanceWriteAheadPolicy.TaskRecoveryState(true, taskEnabled),
            _ => AiMaintenanceWriteAheadPolicy.DriverRecoveryState(originalStart, liveStart)
        };

        // Crash after intent-before-write: unchanged -> yield, no OS restore.
        // Crash after mutation or completion-save failure: restore once.
        // External concurrent write: relinquish rather than overwrite.
        int recoveryWrites = decision == "restore" ? 1 : 0;
        return new Outcome(true, persistedIntent, decision, changes, recoveryWrites);
    }

    internal static string RunFaultInjectionSmoke()
    {
        int count = 0;
        foreach (string kind in new[] { "service", "task", "driver" })
        {
            foreach (Boundary point in Enum.GetValues<Boundary>())
            {
                Outcome actual = Simulate(kind, point);
                string wanted = point switch
                {
                    Boundary.IntentWriteFailed => "veto",
                    Boundary.AfterDurableIntent => "unchanged",
                    Boundary.AfterOsMutation or Boundary.CompletionWriteFailed => "restore",
                    Boundary.AfterCommitted => "committed",
                    _ => kind == "task" ? "unchanged" : "external"
                };
                int expectedOsChanges = point is Boundary.IntentWriteFailed or
                    Boundary.AfterDurableIntent ? 0 : 1;
                int expectedRecovery = wanted == "restore" ? 1 : 0;
                bool shouldBePending = point is not (
                    Boundary.IntentWriteFailed or Boundary.AfterCommitted);
                if (actual.RecoveryDecision != wanted ||
                    actual.OsMutations != expectedOsChanges ||
                    actual.RecoveryMutations != expectedRecovery ||
                    actual.JournalPending != shouldBePending ||
                    actual.MayIssueWindowsWrite != (point != Boundary.IntentWriteFailed))
                    throw new InvalidOperationException(
                        $"AI in-memory crash injection failed for {kind} / {point}.");
                count++;
            }
        }

        // Service.Stop() can finish just before the separate Start=3 write.
        // The journal must recognize stopped+original Auto start as recoverable.
        if (AiMaintenanceWriteAheadPolicy.ServiceRecoveryState(2, 2, false) != "restore" ||
            AiMaintenanceWriteAheadPolicy.ServiceRecoveryState(2, 4, false) != "external")
            throw new InvalidOperationException(
                "AI partial service stop vs manual startup override classification regressed.");

        return $"AI_CRASH_FAULT_INJECTION_SMOKE_OK cases={count} kinds=3 " +
            "durableIntentVeto=true afterIntent=true afterOs=true " +
            "commitFailure=true afterCommit=true manualOverride=true " +
            "partialServiceStop=true liveWindowsWrites=0";
    }
}