using System;
using System.Collections;

namespace SpankyhoOptimizer.Services;

// WMI query collections and each visited ManagementObject carry native resources.
// Search beyond an empty first row, while releasing rows and the collection on
// success, exhaustion, or an exception.
internal static class WmiFirstNonEmptyScope
{
    internal static string Read<TRow>(IEnumerable rows, IDisposable owner,
        Func<TRow, string?> selector, string fallback) where TRow : class, IDisposable
    {
        using (owner)
        {
            foreach (object? value in rows)
            {
                if (value is not TRow row) continue;
                using (row)
                {
                    string? text = selector(row);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }
        return fallback;
    }
}
