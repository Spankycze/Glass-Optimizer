using System;
using System.Collections;

namespace SpankyhoOptimizer.Services;

// ManagementObjectSearcher.Get() owns an IDisposable native WMI result set;
// rows need disposing even when a query exits after the first match.
internal static class WmiFirstRowScope
{
    // Some callers need to return the first ManagementObject to a higher layer.
    // The caller then owns that row, while the WMI result collection is closed
    // immediately rather than leaking until finalization.
    internal static TRow? TakeFirst<TRow>(IEnumerable rows, IDisposable owner)
        where TRow : class, IDisposable
    {
        using (owner)
        {
            foreach (object? value in rows)
                if (value is TRow item) return item;
        }
        return null;
    }

    internal static TResult ReadFirst<TRow, TResult>(
        IEnumerable rows, IDisposable owner, Func<TRow, TResult> read, TResult fallback)
        where TRow : class, IDisposable
    {
        using (owner)
        {
            foreach (object? value in rows)
            {
                if (value is not TRow item) continue;
                using (item)
                    return read(item);
            }
        }
        return fallback;
    }
}
