using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Pure admission gate for native GPU process priority mutation.
/// Both live foreground and the independently sampled workload must agree.
/// </summary>
internal static class AiGameGpuPriorityPolicy
{
    internal const int Normal = 2;
    internal const int AboveNormal = 3;

    // A GPU boost can distort another component's A/B/A results. Running a
    // trial means restoring any owned boost, not stacking another mutation.
    internal static bool MayEnableForTick(bool enabled, bool experimentActive) =>
        enabled && !experimentActive;

    internal static string RunFunctionalSmoke()
    {
        if (!MayEnableForTick(true, false) ||
            MayEnableForTick(true, true) ||
            MayEnableForTick(false, false) ||
            !MayBoost(true, true, true, true, true, 23456, 23456) ||
            MayBoost(true, true, true, true, true, 23456, 34567) ||
            MayBoost(true, true, true, false, true, 23456, 23456) ||
            MayBoost(true, true, true, true, false, 23456, 23456))
            throw new InvalidOperationException("AI GPU foreground/experiment coordination regressed.");
        _ = RunMonotonicGpuTimingSmoke();
        return "AI_GAME_GPU_COORDINATION_SMOKE_OK foreground=true leaseExclusive=true " +
               "gameIdentity=true writes=0";
    }

    internal static bool MayBoost(
        bool enabled, bool telemetryFresh, bool gameActive, bool gameWorkload,
        bool knownGame, int sampledForegroundPid, int liveForegroundPid) =>
        enabled && telemetryFresh && gameActive && gameWorkload && knownGame &&
        sampledForegroundPid > 4 && liveForegroundPid == sampledForegroundPid;

    // A ProgramData journal is data, not an authority to elevate another
    // process. Reject crafted PIDs, paths and values (especially RealTime).
    internal static bool ValidJournalOwnership(
        int pid, long startedUtcTicks, string? imagePath, int original, int applied)
    {
        if (pid <= 4 || startedUtcTicks <= System.DateTime.UnixEpoch.Ticks ||
            original != Normal || applied != AboveNormal ||
            string.IsNullOrWhiteSpace(imagePath) ||
            imagePath.StartsWith(@"\\", System.StringComparison.Ordinal) ||
            !System.IO.Path.IsPathFullyQualified(imagePath))
            return false;
        try
        {
            string normalized = System.IO.Path.GetFullPath(imagePath);
            string windows = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
            if (normalized.StartsWith(windows.TrimEnd('\\') + "\\",
                    System.StringComparison.OrdinalIgnoreCase))
                return false;
            return System.IO.Path.GetExtension(normalized).Equals(".exe",
                    System.StringComparison.OrdinalIgnoreCase) &&
                AiKnownGameProcessPolicy.IsKnownGame(
                    System.IO.Path.GetFileNameWithoutExtension(normalized));
        }
        catch { return false; }
    }

    // Native D3DKMT readback cadence and error cooldown must use elapsed
    // monotonic ticks. A Windows/NTP clock jump cannot disable verification
    // for minutes or hours while a game still holds AI-owned priority.
    internal static bool ShouldRecheckOwnedMonotonic(
        long nowStamp, long previousStamp, long frequency) =>
        previousStamp <= 0 || nowStamp <= 0 || frequency <= 0 ||
        nowStamp < previousStamp ||
        (nowStamp - (double)previousStamp) / frequency >= 10;

    internal static long NextRetryDeadline(long nowStamp, long frequency, int seconds)
    {
        if (nowStamp <= 0 || frequency <= 0 || seconds <= 0 ||
            (double)frequency * seconds >= long.MaxValue - (double)nowStamp)
            return long.MaxValue;
        return nowStamp + (long)Math.Ceiling((double)frequency * seconds);
    }

    internal static bool IsRetryDue(long nowStamp, long deadlineStamp) =>
        nowStamp > 0 && deadlineStamp >= 0 &&
        (deadlineStamp == 0 || nowStamp >= deadlineStamp);

    internal static string RunMonotonicGpuTimingSmoke()
    {
        const long frequency = 10_000;
        const long started = 100_000;
        if (!ShouldRecheckOwnedMonotonic(started, 0, frequency) ||
            ShouldRecheckOwnedMonotonic(started + 10 * frequency - 1, started, frequency) ||
            !ShouldRecheckOwnedMonotonic(started + 10 * frequency, started, frequency) ||
            !ShouldRecheckOwnedMonotonic(started - 1, started, frequency) ||
            !ShouldRecheckOwnedMonotonic(started, started, 0) ||
            NextRetryDeadline(started, frequency, 30) != started + 30 * frequency ||
            NextRetryDeadline(started, frequency, 60) != started + 60 * frequency ||
            NextRetryDeadline(started, 0, 60) != long.MaxValue ||
            IsRetryDue(started + 30 * frequency - 1, started + 30 * frequency) ||
            !IsRetryDue(started + 30 * frequency, started + 30 * frequency) ||
            IsRetryDue(0, 0) || IsRetryDue(started, -1))
            throw new InvalidOperationException("AI GPU monotonic readback/cooldown timing regressed.");
        return "AI_GPU_MONOTONIC_TIMING_SMOKE_OK recheck10s=true retry30s=true " +
               "cooldown60s=true clockJumpIndependent=true windowsWrites=0";
    }

    // Compatibility for prior tests; the LIVE GPU scheduler uses monotonic
    // timestamps instead of these wall-clock readings.
    internal static bool ShouldRecheckOwned(DateTime nowUtc, DateTime previousUtc) =>
        previousUtc == DateTime.MinValue ||
        nowUtc < previousUtc ||
        nowUtc - previousUtc >= TimeSpan.FromSeconds(10);

    internal static bool MayRestoreOwned(
        bool processIdentityMatches, bool priorityReadable, int currentPriority) =>
        processIdentityMatches && priorityReadable && currentPriority == AboveNormal;
}
