using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // User OFF, failed ON and cancellation share one rollback path. The localized
    // failure text is supplied by the caller so each tweak keeps its own wording.
    internal static async Task ExecuteRollbackMutationAsync(
        bool enable,
        Func<CancellationToken, Task> capture,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task> restore,
        string compoundFailureMessage,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!enable)
        {
            await restore(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await capture(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            await apply(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception operationError)
        {
            try { await restore(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    compoundFailureMessage, operationError, rollbackError);
            }
            throw;
        }
    }

    // Variant for tweaks whose restore reports whether the original backup
    // really existed; a silent false must fail closed instead of pretending success.
    internal static async Task ExecuteRollbackMutationWithBackupGuardAsync(
        bool enable,
        Func<CancellationToken, Task> capture,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task<bool>> restore,
        string missingBackupMessage,
        string compoundFailureMessage,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!enable)
        {
            if (!await restore(CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException(missingBackupMessage);
            return;
        }

        await capture(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            await apply(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception operationError)
        {
            try
            {
                if (!await restore(CancellationToken.None).ConfigureAwait(false))
                    throw new InvalidOperationException(missingBackupMessage);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    compoundFailureMessage, operationError, rollbackError);
            }
            throw;
        }
    }
}
