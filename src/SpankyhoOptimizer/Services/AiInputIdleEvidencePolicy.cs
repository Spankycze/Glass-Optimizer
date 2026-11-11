using System;

namespace SpankyhoOptimizer.Services;

/// <summary>Fail-closed interpretation of the 32-bit Win32 last-input tick.
/// SendInput can occasionally report a future or out-of-order tick, which
/// otherwise looks like nearly 49.7 days of inactivity after uint wrap.</summary>
internal static class AiInputIdleEvidencePolicy
{
    private const uint MaxTrustedIdleMs = 24u * 60u * 60u * 1000u;

    internal static bool IsConfirmedIdle(uint nowTick, uint lastInputTick, TimeSpan threshold)
    {
        if (lastInputTick == 0 || threshold <= TimeSpan.Zero ||
            !double.IsFinite(threshold.TotalMilliseconds) ||
            threshold.TotalMilliseconds > MaxTrustedIdleMs)
            return false;

        uint elapsed = unchecked(nowTick - lastInputTick);
        // Wraps in the normal 32-bit Windows uptime cycle remain supported
        // for up to 24h of actual inactivity. An ambiguous huge delta is
        // NOT proof that the user left the computer.
        return elapsed <= MaxTrustedIdleMs && elapsed >= threshold.TotalMilliseconds;
    }
}
