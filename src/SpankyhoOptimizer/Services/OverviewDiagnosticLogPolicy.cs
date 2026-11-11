using System;

namespace SpankyhoOptimizer.Services;

// Rate-limit only informational Overview diagnostics. The displayed values,
// history merge and subsequent updates must never depend on this policy.
internal static class OverviewDiagnosticLogPolicy
{
    internal static bool ShouldLog(string? previousSignature, string nextSignature,
        DateTime lastLoggedUtc, DateTime nowUtc)
    {
        if (string.Equals(previousSignature, nextSignature, StringComparison.Ordinal))
            return false;
        return lastLoggedUtc == default || nowUtc < lastLoggedUtc ||
            nowUtc - lastLoggedUtc >= TimeSpan.FromSeconds(30);
    }
}
