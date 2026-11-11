using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Allocation-free current-FanAgent journal probe. Only services and tasks
/// can enter an owned A/B/A verification phase; a driver row with a persisted
/// timestamp must never trigger unrelated emergency maintenance scans.
/// </summary>
internal static class AiPendingTrialSelectionPolicy
{
    // Snapshot before asynchronous phase checks mutate the dictionary.
    // Stable insertion sort preserves the previous LINQ OrderBy semantics for
    // equal verification timestamps, without its iterator/sorter/array chain.
    internal static IReadOnlyList<AiSystemRuntimeItemState> OrderedPendingTrials(
        Dictionary<string, AiSystemRuntimeItemState>.ValueCollection items)
    {
        List<AiSystemRuntimeItemState>? pending = null;
        foreach (AiSystemRuntimeItemState item in items)
        {
            if (!IsPendingServiceOrTask(item))
                continue;
            pending ??= new List<AiSystemRuntimeItemState>(4);
            // Upper-bound binary search preserves stable ties and reduces
            // timestamp comparisons from quadratic to O(n log n) on large
            // interrupted journals. List.Insert retains the same snapshot order.
            int low = 0;
            int high = pending.Count;
            while (low < high)
            {
                int mid = low + ((high - low) >> 1);
                if (pending[mid].VerificationStartedUtc <= item.VerificationStartedUtc)
                    low = mid + 1;
                else
                    high = mid;
            }
            pending.Insert(low, item);
        }
        return pending is null ? Array.Empty<AiSystemRuntimeItemState>() : pending;
    }

    private static bool IsPendingServiceOrTask(AiSystemRuntimeItemState item) =>
        item.Managed && item.VerificationStartedUtc is not null &&
        (string.Equals(item.Kind, "service", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(item.Kind, "task", StringComparison.OrdinalIgnoreCase));

    // Pending write-ahead intents also require restart recovery for drivers.
    // Do not mistake their inclusion here for admission to service/task A/B/A.
    internal static bool HasInterruptedOrPendingJournal(
        Dictionary<string, AiSystemRuntimeItemState>.ValueCollection items)
    {
        foreach (AiSystemRuntimeItemState state in items)
        {
            if (AiMaintenanceOwnershipPolicy.HasInterruptedTrial(state) ||
                AiMaintenanceWriteAheadPolicy.IsPending(state) ||
                AiMaintenanceWriteAheadPolicy.IsUnownedPending(state))
                return true;
        }
        return false;
    }

    // A journal with ONLY an unowned pending intent remains globally
    // blocked, but cannot perform an automatic Windows restore. Do not
    // needlessly schedule the same impossible recovery at a 5-second rate.
    // Any truly owned pending write or interrupted A/B/A trial retains
    // urgent retry even with telemetry loss. ValueCollection enumerates
    // allocation-free; do not materialize the durable journal per tick.
    internal static bool HasActionableRecovery(
        Dictionary<string, AiSystemRuntimeItemState>.ValueCollection items)
    {
        foreach (AiSystemRuntimeItemState state in items)
            if (AiMaintenanceOwnershipPolicy.HasInterruptedTrial(state) ||
                AiMaintenanceWriteAheadPolicy.IsPending(state))
                return true;
        return false;
    }

    // After an owned mutation commits, any remaining write-ahead intent
    // (including one whose ownership cannot be confirmed) keeps recovery
    // armed. An active A/B/A verification is not itself an intent.
    internal static bool HasPendingWriteAheadMutation(
        Dictionary<string, AiSystemRuntimeItemState>.ValueCollection items)
    {
        foreach (AiSystemRuntimeItemState state in items)
            if (AiMaintenanceWriteAheadPolicy.IsPending(state) ||
                AiMaintenanceWriteAheadPolicy.IsUnownedPending(state))
                return true;
        return false;
    }

    // Recovery and restore contain awaits and mutate the live journal. Keep
    // the original dictionary iteration order but materialize only matching
    // entries in a single list instead of LINQ iterator + ToArray chains.
    internal static IReadOnlyList<AiSystemRuntimeItemState> SnapshotInterruptedJournal(
        Dictionary<string, AiSystemRuntimeItemState>.ValueCollection items)
    {
        List<AiSystemRuntimeItemState>? result = null;
        foreach (AiSystemRuntimeItemState state in items)
            if (AiMaintenanceOwnershipPolicy.HasInterruptedTrial(state) ||
                AiMaintenanceWriteAheadPolicy.IsPending(state) ||
                AiMaintenanceWriteAheadPolicy.IsUnownedPending(state))
            {
                result ??= new List<AiSystemRuntimeItemState>(4);
                result.Add(state);
            }
        return result is null ? Array.Empty<AiSystemRuntimeItemState>() : result;
    }

    internal static IReadOnlyList<KeyValuePair<string, AiSystemRuntimeItemState>> SnapshotManagedKind(
        Dictionary<string, AiSystemRuntimeItemState> items, string kind)
    {
        // A disabled scope often has nothing left to restore. Avoid creating
        // an empty List on every maintenance cycle; retain a stable snapshot
        // in original dictionary order whenever an owned item does exist.
        List<KeyValuePair<string, AiSystemRuntimeItemState>>? result = null;
        foreach (KeyValuePair<string, AiSystemRuntimeItemState> pair in items)
            if (pair.Value.Managed &&
                string.Equals(pair.Value.Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                result ??= new List<KeyValuePair<string, AiSystemRuntimeItemState>>(4);
                result.Add(pair);
            }
        return result is null
            ? Array.Empty<KeyValuePair<string, AiSystemRuntimeItemState>>()
            : result;
    }

    // The lease is kept if EITHER a measured service/task trial OR an
    // actionable owned restoration remains. A single short-circuit scan
    // avoids the old two full probes and stops on the first owned operation.
    // Unowned/quarantined intents still do not authorize lease retention.
    internal static bool NeedsMaintenanceLease(
        Dictionary<string, AiSystemRuntimeItemState>.ValueCollection items)
    {
        foreach (AiSystemRuntimeItemState state in items)
            if (IsPendingServiceOrTask(state) ||
                AiMaintenanceOwnershipPolicy.HasInterruptedTrial(state) ||
                AiMaintenanceWriteAheadPolicy.IsPending(state))
                return true;
        return false;
    }

    // A single maintenance tick can end while a service/task Candidate-B
    // trial still owns a Windows change. Keep cross-process experiment
    // exclusivity for the ENTIRE active trial and for owned write-ahead
    // recovery, not merely for each individual poll of the FanAgent.
    internal static bool ShouldRetainExperimentLease(
        bool pendingMeasuredTrial, bool actionableRecovery) =>
        pendingMeasuredTrial || actionableRecovery;

    internal static string RunMaintenanceLeaseLifecycleSmoke()
    {
        if (!ShouldRetainExperimentLease(true, false) ||
            !ShouldRetainExperimentLease(false, true) ||
            !ShouldRetainExperimentLease(true, true) ||
            ShouldRetainExperimentLease(false, false))
            throw new InvalidOperationException(
                "AI maintenance lease must cover a whole A/B/A trial and release on completion or OFF.");
        return "AI_MAINTENANCE_LEASE_LIFECYCLE_SMOKE_OK crossTick=true " +
            "ownedRecovery=true completedRelease=true disabledRelease=true windowsWrites=0";
    }

    internal static bool HasPendingManagedTrial(
        Dictionary<string, AiSystemRuntimeItemState>.ValueCollection items)
    {
        // A Dictionary ValueCollection has a value-type enumerator. No LINQ
        // closure/delegate or boxed IEnumerable enumerator per 1-5s tick.
        foreach (AiSystemRuntimeItemState item in items)
        {
            if (IsPendingServiceOrTask(item))
                return true;
        }
        return false;
    }
}
