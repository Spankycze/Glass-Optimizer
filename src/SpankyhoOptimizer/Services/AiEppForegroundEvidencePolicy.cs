using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Exact HWND/PID continuity for a foreground app CPU family, sampled from
/// the same existing process enumeration. A different window on the same
/// process or a changed process is not a comparable A/B/A workload.
/// </summary>
internal static class AiEppForegroundEvidencePolicy
{
    internal static bool IsComparable(
        (nint Handle, int ProcessId) before,
        (nint Handle, int ProcessId) after,
        bool nativeProcessFound, string? processName,
        double cpuPercent) =>
        before.Handle != 0 && before.ProcessId > 0 &&
        before.Handle == after.Handle && before.ProcessId == after.ProcessId &&
        nativeProcessFound && !string.IsNullOrWhiteSpace(processName) &&
        double.IsFinite(cpuPercent) && cpuPercent >= 0.1 && cpuPercent <= 100;

    internal static string WorkloadKey((nint Handle, int ProcessId) identity,
        string processName) => identity.ProcessId + ":" + processName;
}
