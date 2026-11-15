using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static class RegistryBatchCommit
{
    // Writes a batch of registry values and, when verification fails, restores
    // every attempted entry newest-first. A failed rollback is reported together
    // with the primary failure instead of masking it.
    internal static void Run<TChange, TSaved>(
        IReadOnlyList<TChange> changes,
        Func<TChange, TSaved> read,
        Func<TSaved, bool> existed,
        Func<TSaved, object?> value,
        Func<TSaved, RegistryValueKind> kind,
        Action<TChange> write,
        Func<TChange, object?, RegistryValueKind, TChange> withOriginal,
        Action<TChange> remove,
        Action verify,
        Action restoreSystem,
        string aggregateMessage)
    {
        var original = new TSaved[changes.Count];
        // Snapshot every field before the first write, including registry kinds
        // and the fact that a value was absent in the user's original state.
        for (int i = 0; i < changes.Count; i++)
            original[i] = read(changes[i]);

        int attempted = 0;
        try
        {
            for (int i = 0; i < changes.Count; i++)
            {
                attempted = i + 1; // a native setter may mutate before throwing
                write(changes[i]);
            }
            verify();
        }
        catch (Exception primary)
        {
            var errors = new List<Exception>();
            for (int i = attempted - 1; i >= 0; i--)
            {
                try
                {
                    if (existed(original[i]))
                        write(withOriginal(changes[i], value(original[i]), kind(original[i])));
                    else
                        remove(changes[i]);
                }
                catch (Exception rollbackError) { errors.Add(rollbackError); }
            }
            if (attempted > 0)
            {
                try { restoreSystem(); }
                catch (Exception rollbackError) { errors.Add(rollbackError); }
            }
            if (errors.Count != 0)
            {
                errors.Insert(0, primary);
                throw new AggregateException(aggregateMessage, errors);
            }
            throw;
        }
    }
}
