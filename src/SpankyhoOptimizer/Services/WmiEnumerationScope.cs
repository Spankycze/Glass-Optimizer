using System;
using System.Collections;
using System.Threading;

namespace SpankyhoOptimizer.Services;

// WMI result sets and rows hold native resources. A scan may stop at any row
// due to cancellation, device removal or an inaccessible property.
internal static class WmiEnumerationScope
{
    internal static void ForEach<TRow>(IEnumerable rows, IDisposable owner,
        CancellationToken token, Action<TRow> inspect) where TRow : class, IDisposable
    {
        using (owner)
        {
            foreach (object? value in rows)
            {
                if (value is not TRow row) continue;
                using (row)
                {
                    token.ThrowIfCancellationRequested();
                    inspect(row);
                }
            }
        }
    }
}
