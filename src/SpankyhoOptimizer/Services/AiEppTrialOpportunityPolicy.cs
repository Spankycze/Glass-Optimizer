using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Pre-admission for a genuinely comparable Balanced EPP experiment. This is
/// NOT a gate for normal AI power optimization or user foreground workloads.
/// It only avoids beginning a six-minute reversible A/B/A experiment on a
/// short-lived Balanced transition, using the existing FanAgent tick.
/// </summary>
internal static class AiEppTrialOpportunityPolicy
{
    internal static readonly TimeSpan StableBalancedMinimum = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan EarlyInterruptedCooldown = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PreflightSampleGap = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan PreflightMaximumGap = TimeSpan.FromSeconds(35);
    internal static readonly TimeSpan UnstablePreflightCooldown = TimeSpan.FromSeconds(60);

    // The EPP probe can be dormant for longer than the native CPU-counter
    // comparability window. One read then refreshes its baseline, but the old
    // 60-second rejection cooldown made the *next* read stale again forever.
    // Retry a cold baseline after the existing 5-second gap. Unavailable
    // hardware, unstable foreground and all other failures retain 60 seconds.
    internal static TimeSpan FailedPreflightRetryDelay(AiSystemPerformanceSample sample) =>
        !sample.Valid && double.IsNaN(sample.CpuPercent) &&
        string.Equals(sample.Reason,
            "Native CPU counters require a fresh comparable sample",
            StringComparison.Ordinal)
            ? PreflightSampleGap : UnstablePreflightCooldown;

    // Preflight spacing must be measured by elapsed ticks. A wall-clock/NTP
    // jump cannot shorten or indefinitely extend the real 5–35s sample gap.
    internal static bool IsMonotonicPreflightGapValid(long firstStamp, long secondStamp, long frequency) =>
        firstStamp > 0 && frequency > 0 && secondStamp >= firstStamp &&
        (secondStamp - (double)firstStamp) / frequency >= PreflightSampleGap.TotalSeconds &&
        (secondStamp - (double)firstStamp) / frequency <= PreflightMaximumGap.TotalSeconds;

    internal static bool IsPreflightRetryDue(long deadlineStamp, long currentStamp) =>
        currentStamp > 0 && deadlineStamp >= 0 &&
        (deadlineStamp == 0 || currentStamp >= deadlineStamp);

    internal static long PreflightNextDeadline(long currentStamp, long frequency, TimeSpan delay) =>
        currentStamp > 0 && frequency > 0 && delay > TimeSpan.Zero &&
        delay.TotalSeconds * frequency < long.MaxValue - currentStamp
            ? currentStamp + (long)Math.Ceiling(delay.TotalSeconds * frequency)
            : long.MaxValue; // invalid data defers; it never silently admits.

    internal static string RunMonotonicPreflightSmoke()
    {
        const long freq = 10_000;
        const long first = 100_000;
        long gap5 = first + 5 * freq;
        long gap35 = first + 35 * freq;
        if (IsMonotonicPreflightGapValid(first, gap5 - 1, freq) ||
            !IsMonotonicPreflightGapValid(first, gap5, freq) ||
            !IsMonotonicPreflightGapValid(first, gap35, freq) ||
            IsMonotonicPreflightGapValid(first, gap35 + 1, freq) ||
            IsMonotonicPreflightGapValid(first, first - 1, freq) ||
            IsMonotonicPreflightGapValid(0, gap5, freq) ||
            IsMonotonicPreflightGapValid(first, gap5, 0) ||
            IsPreflightRetryDue(gap5, gap5 - 1) ||
            !IsPreflightRetryDue(gap5, gap5) ||
            !IsPreflightRetryDue(0, first) ||
            IsPreflightRetryDue(0, -1) ||
            IsPreflightRetryDue(-1, first) ||
            PreflightNextDeadline(first, freq, PreflightSampleGap) != gap5 ||
            PreflightNextDeadline(first, freq, UnstablePreflightCooldown) != first + 60 * freq ||
            PreflightNextDeadline(first, 0, PreflightSampleGap) != long.MaxValue)
            throw new InvalidOperationException("EPP preflight monotonic clock policy regressed.");
        // Simulate a two-hour wall clock jump between two five-second probes.
        // Their UTC metadata would have failed the old gap check; the actual
        // monotonic five-second gap remains safe and unchanged.
        DateTime beforeUtc = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        DateTime afterClockJump = beforeUtc.AddHours(2).AddSeconds(5);
        if (afterClockJump - beforeUtc <= PreflightMaximumGap ||
            !IsMonotonicPreflightGapValid(first, gap5, freq))
            throw new InvalidOperationException("EPP preflight wall clock isolation regressed.");
        return "AI_EPP_PREFLIGHT_MONOTONIC_SMOKE_OK gap5s=true max35s=true " +
               "retry60s=true timeJumpIndependent=true writes=0";
    }

    // Preflight only measures the current unmodified Balanced setting.
    // Its observations are NEVER admitted to the A1/B/A2 evidence windows.
    // Two trustworthy samples of the same foreground executable/window
    // and comparable work cost are needed before acquiring the trial lease.
    internal static bool ComparablePreflight(
        AiSystemPerformanceSample first, AiSystemPerformanceSample second,
        bool monotonicGapVerified = false)
    {
        if (AiSystemPerformancePolicy.SampleBlocker(first).Length != 0 ||
            AiSystemPerformancePolicy.SampleBlocker(second).Length != 0 ||
            first.Utc.Kind != DateTimeKind.Utc ||
            second.Utc.Kind != DateTimeKind.Utc ||
            (!monotonicGapVerified &&
             (second.Utc < first.Utc + PreflightSampleGap ||
              second.Utc > first.Utc + PreflightMaximumGap)) ||
            !string.Equals(first.WorkloadKey, second.WorkloadKey, StringComparison.Ordinal))
            return false;
        return Math.Abs(second.WorkloadCpuPercent - first.WorkloadCpuPercent) <=
                   Math.Max(1.0, first.WorkloadCpuPercent * 0.20) &&
               Math.Abs(second.CpuPercent - first.CpuPercent) <=
                   Math.Max(4.0, first.CpuPercent * 0.25) &&
               Math.Abs(second.FixedWorkMs - first.FixedWorkMs) <=
                   Math.Max(0.15, first.FixedWorkMs * 0.15);
    }

    // The production EPP admission uses monotonic elapsed time. A Windows
    // time/NTP jump must not make a newly entered Balanced mode instantly
    // eligible, nor prevent a genuinely stable mode from qualifying.
    internal static bool HasMonotonicStableBalancedWindow(
        bool desiredBalanced, bool actualBalanced,
        long transitionStamp, long nowStamp, long frequency) =>
        desiredBalanced && actualBalanced &&
        transitionStamp > 0 && frequency > 0 && nowStamp >= transitionStamp &&
        (nowStamp - (double)transitionStamp) / frequency >= StableBalancedMinimum.TotalSeconds;

    internal static string RunMonotonicBalancedWindowSmoke()
    {
        const long frequency = 10_000;
        const long transition = 100_000;
        long atMinimum = transition + 15 * frequency;
        if (HasMonotonicStableBalancedWindow(true, true, transition, atMinimum - 1, frequency) ||
            !HasMonotonicStableBalancedWindow(true, true, transition, atMinimum, frequency) ||
            HasMonotonicStableBalancedWindow(false, true, transition, atMinimum, frequency) ||
            HasMonotonicStableBalancedWindow(true, false, transition, atMinimum, frequency) ||
            HasMonotonicStableBalancedWindow(true, true, transition, transition - 1, frequency) ||
            HasMonotonicStableBalancedWindow(true, true, 0, atMinimum, frequency) ||
            HasMonotonicStableBalancedWindow(true, true, transition, atMinimum, 0))
            throw new InvalidOperationException("AI Balanced-mode monotonic admission regressed.");
        DateTime beforeUtc = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        DateTime jumped = beforeUtc.AddHours(2);
        if (!HasStableBalancedWindow(true, true, beforeUtc, jumped) ||
            HasMonotonicStableBalancedWindow(true, true, transition,
                transition + 5 * frequency, frequency))
            throw new InvalidOperationException("AI Balanced-mode admission depends on Windows time jump.");
        return "AI_EPP_BALANCED_MONOTONIC_SMOKE_OK exact15s=true " +
               "gameModeVeto=true clockJumpIgnored=true windowsWrites=0";
    }

    // Historical UTC-only helper remains for persisted diagnostics and
    // existing contracts; production admission uses the monotonic helper.
    internal static bool HasStableBalancedWindow(
        bool desiredBalanced, bool actualBalanced,
        DateTime lastTransitionUtc, DateTime nowUtc) =>
        desiredBalanced && actualBalanced &&
        lastTransitionUtc.Kind == DateTimeKind.Utc &&
        nowUtc.Kind == DateTimeKind.Utc &&
        lastTransitionUtc != default &&
        nowUtc >= lastTransitionUtc &&
        nowUtc - lastTransitionUtc >= StableBalancedMinimum;

    internal static string Describe(
        bool enabled, bool balanced, bool gameActive, bool realIdle,
        double cpuPercent, double? temperatureC,
        bool schemeActive, bool stableBalanced,
        bool cooldownActive, bool trialActive)
    {
        if (!schemeActive) return "EPP učení: plán Optimizeru není aktivní";
        if (!enabled) return "EPP učení: AI Smart nebo ochrana prostředků nedovoluje měření";
        if (gameActive) return "EPP učení: probíhá hra, měření chráněno";
        if (!balanced) return "EPP učení: vyčkává na režim Balanced; Performance je aktivní";
        if (realIdle) return "EPP učení: chybí aktivní srovnatelná práce";
        if (!double.IsFinite(cpuPercent) || cpuPercent < 3 || cpuPercent > 35)
            return "EPP učení: aktuální CPU není v měřitelném rozsahu 3–35 %";
        if (temperatureC is not double temp || !double.IsFinite(temp) || temp >= 70)
            return "EPP učení: čeká na bezpečné měření teploty";
        if (trialActive) return "EPP učení: probíhá vratné A/B/A měření";
        if (cooldownActive) return "EPP učení: platí omezená ochranná prodleva";
        if (!stableBalanced) return "EPP učení: ověřuje 15 sekund stabilního Balanced";
        return "EPP učení: připraveno k vratnému A/B/A měření";
    }
}
