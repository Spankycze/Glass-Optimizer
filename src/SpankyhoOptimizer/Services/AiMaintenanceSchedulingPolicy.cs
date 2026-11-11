using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

public static class AiMaintenanceSchedulingPolicy
{
    private static readonly string[] Kinds = { "service", "task", "driver" };

    internal static int KindCount => Kinds.Length;
    // Read-only SCM, COM and registry discovery also runs while the user
    // works; expensive full scans are deliberately capped at this cadence.
    internal static readonly TimeSpan ActiveReadOnlyDiscoveryCadence = TimeSpan.FromMinutes(15);
    // Low-impact inventory can run more often when the system is genuinely
    // lightly loaded. This cadence never authorizes service/task mutations.
    internal static readonly TimeSpan LightReadOnlyDiscoveryCadence = TimeSpan.FromMinutes(6);
    internal static readonly TimeSpan BusyReadOnlyDiscoveryCadence = TimeSpan.FromMinutes(25);
    internal static readonly TimeSpan HighLoadReadOnlyDiscoveryCadence = TimeSpan.FromMinutes(30);

    internal readonly record struct ActiveObservationDecision(
        bool Allowed, TimeSpan Cadence, string Status);

    // Read-only Windows discovery is useful while the user works. Do not
    // confuse its CPU budget with the much stricter autonomous mutation
    // authorization: the later still needs a genuinely quiet A/B/A window.
    // Distinguish loaded but healthy machines from protected resource pressure.
    internal static ActiveObservationDecision ActiveObservation(
        bool aiEnabled, bool telemetryComparable, bool gameActive,
        double cpuLoadPercent, bool smoothnessSafe)
    {
        if (!aiEnabled) return new(false, TimeSpan.Zero, "INVENTURA: AI vypnutá");
        if (!telemetryComparable || !double.IsFinite(cpuLoadPercent) ||
            cpuLoadPercent < 0 || cpuLoadPercent > 100)
            return new(false, TimeSpan.Zero, "INVENTURA: čekám na aktuální shodná měření");
        if (gameActive)
            return new(false, TimeSpan.Zero, "INVENTURA: během hry pozastavena");
        if (!smoothnessSafe)
            return new(false, TimeSpan.Zero, "INVENTURA: ochrana RAM/CPU/disku");
        if (cpuLoadPercent >= 70)
            return new(false, TimeSpan.Zero, "INVENTURA: vysoké zatížení CPU");
        if (cpuLoadPercent <= 8)
            return new(true, LightReadOnlyDiscoveryCadence,
                "INVENTURA: pouze čtení každých 6 min");
        if (cpuLoadPercent <= 20)
            return new(true, ActiveReadOnlyDiscoveryCadence,
                "INVENTURA: pouze čtení každých 15 min");
        if (cpuLoadPercent <= 50)
            return new(true, BusyReadOnlyDiscoveryCadence,
                "INVENTURA: pouze čtení každých 25 min");
        return new(true, HighLoadReadOnlyDiscoveryCadence,
            "INVENTURA: pouze čtení každých 30 min");
    }

    internal static bool CanObserveDuringActivity(
        bool aiEnabled, bool telemetryComparable, bool gameActive,
        double cpuLoadPercent, bool smoothnessSafe) =>
        ActiveObservation(aiEnabled, telemetryComparable, gameActive,
            cpuLoadPercent, smoothnessSafe).Allowed;

    internal static TimeSpan DiscoveryCadence(
        bool safeMutationWindow, TimeSpan usualInterval,
        TimeSpan readOnlyCadence) =>
        safeMutationWindow ? usualInterval : readOnlyCadence;

    // Legacy callers keep the original low-load observation cadence.
    internal static TimeSpan DiscoveryCadence(
        bool safeMutationWindow, TimeSpan usualInterval) =>
        DiscoveryCadence(safeMutationWindow, usualInterval,
            ActiveReadOnlyDiscoveryCadence);

    internal readonly record struct ScopeDecisionResult(bool Run, bool QuietEnough);

    // An interval-throttled discovery is NOT evidence of user activity.
    // Fast checks must retain/advance existing observations while the
    // independently measured workload is still safe, yet always recover
    // owned resources if the workload turns interactive.
    internal static ScopeDecisionResult ScopeDecision(
        bool hasTrackedCandidate, bool discoveryDue, bool safeMaintenanceWindow) =>
        new(hasTrackedCandidate || discoveryDue, safeMaintenanceWindow);

    // One allocation-free pass over the existing journal instead of three
    // repeated scans of the same resource entries every maintenance cycle.
    internal static (bool Service, bool Task, bool Driver) TrackedScopes(
        Dictionary<string, AiSystemRuntimeItemState>.ValueCollection items) =>
        TrackedScopes(items, true, true, true);

    // Disabled domains are restored by their own scope routines and never
    // need a separate pre-scan to decide whether to discover new candidates.
    internal static (bool Service, bool Task, bool Driver) TrackedScopes(
        Dictionary<string, AiSystemRuntimeItemState>.ValueCollection items,
        bool scanServices, bool scanTasks, bool scanDrivers)
    {
        if (!scanServices && !scanTasks && !scanDrivers)
            return default;
        bool service = false, task = false, driver = false;
        foreach (AiSystemRuntimeItemState item in items)
        {
            // A durable pending mutation must be recovered promptly even if
            // journal restore has not yet marked its row Managed/Sleeping.
            if (!item.Managed && !item.Sleeping &&
                item.ObservationSinceUtc is null &&
                string.IsNullOrEmpty(item.PendingMutation))
                continue;
            if (scanServices && !service &&
                string.Equals(item.Kind, "service", StringComparison.OrdinalIgnoreCase))
                service = true;
            else if (scanTasks && !task &&
                string.Equals(item.Kind, "task", StringComparison.OrdinalIgnoreCase))
                task = true;
            else if (scanDrivers && !driver &&
                string.Equals(item.Kind, "driver", StringComparison.OrdinalIgnoreCase))
                driver = true;
            if ((!scanServices || service) && (!scanTasks || task) &&
                (!scanDrivers || driver))
                break;
        }
        return (service, task, driver);
    }

    // Service fast recovery is read-only, never an eligibility scan for
    // stopping additional Windows services. Keep all durable owners visible.
    internal static bool NeedsFastServiceRuntimeRead(
        bool quietEnough, bool fullDiscovery, AiSystemRuntimeItemState state) =>
        quietEnough || fullDiscovery || state.Managed || state.Sleeping ||
        !string.IsNullOrEmpty(state.PendingMutation);

    // Break the previous quiet candidate observation when the user resumes
    // work, without touching historical demand or rollback evidence.
    internal static AiSystemRuntimeItemState SuspendUnownedServiceObservation(
        AiSystemRuntimeItemState state) =>
        state.ObservationSinceUtc is null ? state :
        state with { ObservationSinceUtc = null };

    // During active sessions no NEW driver trial is allowed. Skip registry
    // enumeration for unowned drivers while preserving an owned driver's
    // immediate device-reattachment and hold-expiry restoration path.
    internal static bool NeedsDriverRegistryRead(
        bool quietEnough, bool managed, bool sleeping = false, bool pendingMutation = false) =>
        quietEnough || managed || sleeping || pendingMutation;

    // An interrupted observation cannot earn quiet-window credit later.
    // Preserve the last explanation while postponing nonessential registry I/O.
    internal static AiSystemRuntimeItemState SuspendUnownedDriverObservation(
        AiSystemRuntimeItemState state) =>
        state.ObservationSinceUtc is null && state.DriverObservationLastUtc is null
            ? state
            : state with { ObservationSinceUtc = null, DriverObservationLastUtc = null };

    // A fast non-quiet maintenance pass cannot initiate a new Task Scheduler
    // experiment. Refresh only currently AI-owned or write-ahead-pending tasks;
    // the periodic deep discovery still reads every approved task.
    internal static bool NeedsFastTaskRuntimeRead(
        bool quietEnough, bool fullDiscovery, AiSystemRuntimeItemState state) =>
        quietEnough || fullDiscovery || state.Managed || state.Sleeping ||
        !string.IsNullOrEmpty(state.PendingMutation);

    // A tracked but unowned quiet-window observation expires on user activity.
    // Do not erase its last explanation or historical performance learning.
    internal static AiSystemRuntimeItemState SuspendUnownedTaskObservation(
        AiSystemRuntimeItemState state) =>
        state.ObservationSinceUtc is null ? state :
        state with { ObservationSinceUtc = null };

    internal static int NextCycle(int cycle) => unchecked(cycle + 1);

    // The cycle counter wraps from int.MaxValue to int.MinValue after long
    // runtimes. Interpreting it as unsigned preserves the rotation without
    // Math.Abs(int.MinValue), which throws OverflowException.
    internal static string KindAt(int cycle, int offset)
    {
        int start = (int)(unchecked((uint)cycle) % (uint)Kinds.Length);
        // Offsets are 0..2 in the agent; keep negative/large API callers
        // equivalent without two extra modulo operations.
        int delta = offset % Kinds.Length;
        if (delta < 0) delta += Kinds.Length;
        int index = start + delta;
        if (index >= Kinds.Length) index -= Kinds.Length;
        return Kinds[index];
    }

    // Preserve the existing public API; runtime tick uses KindAt directly
    // instead of allocating a three-element array on every maintenance pass.
    public static string[] OrderForCycle(int cycle)
    {
        var result = new string[Kinds.Length];
        for (int i = 0; i < Kinds.Length; i++)
            result[i] = KindAt(cycle, i);
        return result;
    }
}
