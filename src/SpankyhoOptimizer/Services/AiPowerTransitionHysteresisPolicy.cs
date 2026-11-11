using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Anti-oscillation protection for ordinary (not urgent) transitions of
/// the already-owned Spankyho power scheme. Never delays a game, confirmed
/// >=60% foreground CPU demand or thermal shutdown of Performance.
/// The dwell is stateful only in the existing FanAgent, with no new timers.
/// </summary>
internal static class AiPowerTransitionHysteresisPolicy
{
    internal static readonly TimeSpan MinimumPerformanceDwell =
        TimeSpan.FromSeconds(18);
    internal static readonly TimeSpan BalancedRecoveryDwell =
        TimeSpan.FromSeconds(25);
    // A real user-input idle period can flicker when measured CPU crosses
    // the 10% idle threshold. Hold off ordinary reentry to avoid repeatedly
    // invoking powercfg for Idle(30/3) <-> Balanced(20/4) every few seconds.
    internal static readonly TimeSpan IdleReentryDwell =
        TimeSpan.FromSeconds(30);

    private static bool During(DateTime lastChangeUtc, DateTime nowUtc, TimeSpan limit) =>
        lastChangeUtc.Kind == DateTimeKind.Utc &&
        nowUtc.Kind == DateTimeKind.Utc &&
        lastChangeUtc != default && nowUtc >= lastChangeUtc &&
        nowUtc - lastChangeUtc < limit;

    // AI-suggested low CPU alone is not permission to reduce performance
    // while the user is actively typing or moving the mouse. Exiting Idle is
    // never delayed: activity, gaming and thermal safety keep precedence.
    internal static bool AllowIdleEntry(
        bool realIdle, bool aiSuggestedIdle, bool userInputIdle,
        DateTime lastIdleExitUtc, DateTime nowUtc) =>
        (realIdle || (aiSuggestedIdle && userInputIdle)) &&
        !During(lastIdleExitUtc, nowUtc, IdleReentryDwell);

    internal static bool HoldPerformanceForBriefOrdinaryDip(
        DateTime lastChangeUtc, DateTime nowUtc,
        double cpuPercent, bool realIdle) =>
        !realIdle &&
        double.IsFinite(cpuPercent) && cpuPercent >= 20 &&
        cpuPercent <= 100 &&
        During(lastChangeUtc, nowUtc, MinimumPerformanceDwell);

    internal static bool HoldBalancedAfterRecentPerformanceExit(
        DateTime lastChangeUtc, DateTime nowUtc,
        double cpuPercent, double rawCpuPercent,
        bool gameActive, bool urgentPerformance) =>
        !gameActive && !urgentPerformance &&
        double.IsFinite(cpuPercent) && cpuPercent >= 0 && cpuPercent < 60 &&
        double.IsFinite(rawCpuPercent) && rawCpuPercent >= 0 && rawCpuPercent < 60 &&
        During(lastChangeUtc, nowUtc, BalancedRecoveryDwell);
}
