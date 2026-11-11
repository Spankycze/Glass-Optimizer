using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed partial class RegistryBackupStore
{
    // Production batch boundary: an independent failure must not prevent
    // restoring later backups, and an incomplete batch must never report success.
    internal static async Task<int> ExecuteRestoreAllTransactionAsync(
        IReadOnlyList<string> ids,
        Func<string, CancellationToken, Task<bool>> restoreOne,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(restoreOne);
        token.ThrowIfCancellationRequested();

        var failures = new List<Exception>();
        int restored = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(id))
            {
                failures.Add(new InvalidOperationException(
                    "Záloha obsahuje prázdný identifikátor tweaku."));
                continue;
            }
            // A duplicate cannot result in restoring the same target twice.
            if (!seen.Add(id))
            {
                failures.Add(new InvalidOperationException(
                    "Duplicitní identifikátor registry zálohy: " + id));
                continue;
            }
            try
            {
                if (!await restoreOne(id, token).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Původní registry záloha již není dostupná: " + id);
                restored++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Genuine user cancellation stops the rest of the batch.
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    "Obnova registry tweaku selhala: " + id, ex));
            }
        }

        if (failures.Count > 0)
            throw new AggregateException(
                $"Registry obnova není úplná: úspěšně {restored} z {seen.Count} unikátních položek, " +
                $"chyb {failures.Count}. Neúspěšné položky zůstaly k ručnímu řešení.",
                failures);
        return restored;
    }
}
