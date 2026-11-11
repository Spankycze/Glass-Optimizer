using System;
using System.Collections;

namespace SpankyhoOptimizer.Services;

// For WMI queries that can finish on the first matching row (e.g. recent
// restore-point detection), dispose the entire result set and all visited rows.
internal static class WmiMatchingRowScope
{
    internal static bool Any<TRow>(IEnumerable rows, IDisposable owner, Func<TRow, bool> matches)
        where TRow : class, IDisposable
    {
        using (owner)
        {
            foreach (object? value in rows)
            {
                if (value is not TRow row) continue;
                using (row)
                {
                    if (matches(row)) return true;
                }
            }
        }
        return false;
    }
}
