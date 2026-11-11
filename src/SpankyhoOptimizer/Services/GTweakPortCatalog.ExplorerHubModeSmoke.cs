using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Uses only delegate fixtures; does not read, write or open the real registry.
    internal static async Task RunExplorerHubModeMutationSmokeAsync()
    {
        static void Require(bool valid, string reason)
        {
            if (!valid) throw new InvalidOperationException("EXPLORER_HUBMODE_SMOKE: " + reason);
        }

        int captures = 0, writes = 0, restores = 0;
        Func<CancellationToken, Task> capture = _ =>
        {
            captures++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = _ =>
        {
            writes++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task<bool>> restore = token =>
        {
            Require(!token.IsCancellationRequested, "rollback cancelled");
            restores++;
            return Task.FromResult(true);
        };

        // 1: Successful activation performs only capture + write.
        await ExecuteExplorerHubModeMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Require(captures == 1 && writes == 1 && restores == 0, "activation");

        // 2: Disable uses the stored original without taking another backup.
        await ExecuteExplorerHubModeMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Require(captures == 1 && writes == 1 && restores == 1, "deactivation");

        // 3: A failed write must invoke original-state restore.
        bool writeError = false;
        try
        {
            await ExecuteExplorerHubModeMutationAsync(true, capture,
                _ => throw new IOException("injected write fault"), restore, CancellationToken.None);
        }
        catch (IOException) { writeError = true; }
        Require(writeError && restores == 2, "write failure rollback");

        // 4: A failed post-write readback also invokes restore.
        bool readbackError = false;
        try
        {
            await ExecuteExplorerHubModeMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected readback mismatch"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { readbackError = true; }
        Require(readbackError && restores == 3, "readback failure rollback");

        // 5: Cancellation after a mutation runs rollback with an uncancelled token.
        using var cts = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteExplorerHubModeMutationAsync(true, capture,
                _ =>
                {
                    cts.Cancel();
                    return Task.CompletedTask;
                }, restore, cts.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && restores == 4, "cancelled write rollback");

        // 6: Backup persistence failure must prevent any subsequent write.
        int priorWrites = writes;
        bool backupError = false;
        try
        {
            await ExecuteExplorerHubModeMutationAsync(true,
                _ => throw new IOException("injected backup persistence failure"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { backupError = true; }
        Require(backupError && writes == priorWrites && restores == 4, "backup failure");

        // 7: Disable without an original backup must report failure.
        bool missingBackup = false;
        try
        {
            await ExecuteExplorerHubModeMutationAsync(false, capture, apply,
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (InvalidOperationException) { missingBackup = true; }
        Require(missingBackup && writes == priorWrites, "missing backup");

        // 8: Preserve both mutation and rollback failures.
        bool combinedError = false;
        try
        {
            await ExecuteExplorerHubModeMutationAsync(true, capture,
                _ => throw new IOException("injected mutation failure"),
                _ => Task.FromException<bool>(new IOException("injected rollback failure")),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combinedError = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is IOException;
        }
        Require(combinedError, "aggregate failures");

        // 9: Already-cancelled request must not mutate or restore state.
        using var cancelledBeforeStart = new CancellationTokenSource();
        cancelledBeforeStart.Cancel();
        int priorCaptures = captures, priorRestores = restores;
        bool preCancelled = false;
        try
        {
            await ExecuteExplorerHubModeMutationAsync(true, capture, apply, restore,
                cancelledBeforeStart.Token);
        }
        catch (OperationCanceledException) { preCancelled = true; }
        Require(preCancelled && priorCaptures == captures && priorRestores == restores &&
                priorWrites == writes, "pre-cancel state preservation");
    }
}
