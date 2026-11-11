using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Reduces repeated native Process.GetProcesses/TotalProcessorTime calls when
/// Windows is demonstrably quiet. Foreground changes and live CPU pressure
/// always bypass the extended cadence; no background scheduler is added.
/// </summary>
internal static class AiAdaptiveProcessScanPolicy
{
    internal static TimeSpan Interval(AiWorkloadClass workload, bool gameActive,
        double? liveCpuPercent, bool userActuallyAway = false)
    {
        // An active workload or a new system-wide load spike needs fresh PID
        // attribution, regardless of any previously classified idle context.
        if (gameActive || workload is AiWorkloadClass.Game or
            AiWorkloadClass.Unknown || liveCpuPercent is not double load ||
            !double.IsFinite(load) || load >= 25 || load < 0)
            return TimeSpan.FromSeconds(2);
        // The normal 1.5s FanAgent cadence previously scanned every third
        // tick for stable interactive foreground windows, despite <25% live
        // CPU and no game. Four-second spacing cuts those native sweeps from
        // 20 to ~14 per minute while still sampling immediately on a new
        // foreground PID/HWND or a live hardware CPU spike. Game/unknown/high
        // load continue on the existing fast path.
        if (workload == AiWorkloadClass.Interactive)
            return TimeSpan.FromSeconds(4);
        // Only extend to 15s after 60s WITHOUT USER INPUT; low CPU alone
        // is insufficient evidence that a user is away from the computer.
        return workload == AiWorkloadClass.Idle
            ? TimeSpan.FromSeconds(userActuallyAway ? 15 : 5)
            : TimeSpan.FromSeconds(3);
    }

    // Native TotalProcessorTime is monotonic. Its wall-time denominator must
    // also be monotonic: DateTime.UtcNow may jump after NTP corrections,
    // sleep or manual clock edits and must never fabricate 100% CPU pressure.
    internal static double CpuWindowSeconds(long previousTimestamp, long currentTimestamp,
        long frequency)
    {
        if (previousTimestamp <= 0 || currentTimestamp <= previousTimestamp || frequency <= 0)
            return 0.25;
        return Math.Max(0.25, ((double)currentTimestamp - previousTimestamp) / frequency);
    }

    // After suspend/resume, restart, or a broken counter window, old process
    // CPU totals are not comparable to the current native enumeration.
    // A fresh baseline must be captured before reporting new CPU evidence.
    internal static bool CpuWindowComparable(long previousTimestamp,
        long currentTimestamp, long frequency) =>
        previousTimestamp > 0 && currentTimestamp > previousTimestamp && frequency > 0 &&
        ((double)currentTimestamp - previousTimestamp) / frequency <= 60d;

    // A Process.GetProcesses sweep can cross a user task/window transition.
    // Never apply an EPP/mode decision from a mixed-foreground snapshot.
    internal static bool SameForegroundDuringScan(
        (nint Handle, int ProcessId) before,
        (nint Handle, int ProcessId) after) =>
        before.Handle != 0 && before.ProcessId > 0 &&
        before.Handle == after.Handle && before.ProcessId == after.ProcessId;

    internal static bool CanReuse(DateTime lastSampleUtc, DateTime nowUtc,
        TimeSpan interval, int previousForegroundPid, int currentForegroundPid,
        nint previousWindowHandle, nint currentWindowHandle) =>
        previousWindowHandle != 0 && currentWindowHandle != 0 &&
        previousWindowHandle == currentWindowHandle &&
        CanReuse(lastSampleUtc, nowUtc, interval,
            previousForegroundPid, currentForegroundPid);

    internal static bool CanReuse(DateTime lastSampleUtc, DateTime nowUtc,
        TimeSpan interval, int previousForegroundPid, int currentForegroundPid) =>
        lastSampleUtc != default && nowUtc >= lastSampleUtc &&
        nowUtc - lastSampleUtc < interval &&
        previousForegroundPid > 0 && currentForegroundPid > 0 &&
        previousForegroundPid == currentForegroundPid;
}
