using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // These fixtures exercise the production transaction only: no registry
    // reads, registry writes, running services or user settings are touched.
    internal static async Task RunFilterKeysShortcutSmokeAsync()
    {
        static void Check(bool ok, string scenario)
        {
            if (!ok) throw new InvalidOperationException("FILTER_KEYS_SHORTCUT_SMOKE: " + scenario);
        }

        var tweak = StickyFilterShortcutsOff();
        Check(tweak.Id == FilterKeysShortcutId &&
              tweak.Section == "ui" && !tweak.Recommended &&
              !tweak.RequiresAdmin && tweak.RevertAsync is not null &&
              tweak.RevertMode == "Explicit", "catalogue wiring");

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
            Check(!token.IsCancellationRequested, "rollback cancelled");
            restores++;
            return Task.FromResult(true);
        };

        // Enable captures once and writes once, without an unnecessary restore.
        await ExecuteFilterKeysShortcutMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Check(captures == 1 && writes == 1 && restores == 0, "successful enable");

        // Disable restores the saved original, not a synthetic default or deletion.
        await ExecuteFilterKeysShortcutMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(captures == 1 && writes == 1 && restores == 1, "restore on disable");

        // An injected registry-write failure must restore the saved original.
        bool failedWrite = false;
        try
        {
            await ExecuteFilterKeysShortcutMutationAsync(true, capture,
                _ => throw new IOException("simulated registry write failure"),
                restore, CancellationToken.None);
        }
        catch (IOException) { failedWrite = true; }
        Check(failedWrite && restores == 2, "failed write rollback");

        // Read-back mismatch follows the same rollback procedure.
        bool badReadback = false;
        try
        {
            await ExecuteFilterKeysShortcutMutationAsync(true, capture,
                _ => throw new InvalidDataException("simulated wrong registry value"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { badReadback = true; }
        Check(badReadback && restores == 3, "readback rollback");

        // A failed backup must never start a mutation.
        int writesBeforeFailure = writes;
        bool badBackup = false;
        try
        {
            await ExecuteFilterKeysShortcutMutationAsync(true,
                _ => throw new IOException("simulated backup storage error"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { badBackup = true; }
        Check(badBackup && writes == writesBeforeFailure && restores == 3, "failed backup");

        // Missing original on OFF is a hard failure, never a silent value delete.
        bool missingOriginal = false;
        try
        {
            await ExecuteFilterKeysShortcutMutationAsync(false, capture, apply,
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (InvalidOperationException) { missingOriginal = true; }
        Check(missingOriginal && writes == writesBeforeFailure, "missing backup off");

        // If rollback fails after a failed write, preserve BOTH errors.
        bool doubleError = false;
        try
        {
            await ExecuteFilterKeysShortcutMutationAsync(true, capture,
                _ => throw new IOException("simulated write failure"),
                _ => Task.FromException<bool>(new UnauthorizedAccessException("simulated rollback error")),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            doubleError = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(doubleError, "combined failure");

        // Missing backup after a failed write must remain observable.
        bool missingRollback = false;
        try
        {
            await ExecuteFilterKeysShortcutMutationAsync(true, capture,
                _ => throw new IOException("simulated failed apply"),
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            missingRollback = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is InvalidOperationException;
        }
        Check(missingRollback, "missing rollback backup");

        // Cancellation after a write runs restore using a new uncancelled token.
        using var cancellation = new CancellationTokenSource();
        bool wasCancelled = false;
        try
        {
            await ExecuteFilterKeysShortcutMutationAsync(true, capture, _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }, restore, cancellation.Token);
        }
        catch (OperationCanceledException) { wasCancelled = true; }
        Check(wasCancelled && restores == 4, "cancellation rollback");

        // Pre-cancelled ON must not capture/write/restore anything.
        using var cancelledBeforeStart = new CancellationTokenSource();
        cancelledBeforeStart.Cancel();
        int priorCaptures = captures, priorRestores = restores;
        bool preCancelOn = false;
        try
        {
            await ExecuteFilterKeysShortcutMutationAsync(true, capture, apply, restore,
                cancelledBeforeStart.Token);
        }
        catch (OperationCanceledException) { preCancelOn = true; }
        Check(preCancelOn && captures == priorCaptures &&
              writes == writesBeforeFailure && restores == priorRestores, "pre-cancel ON");

        // Pre-cancelled OFF must not attempt to restore.
        bool preCancelOff = false;
        try
        {
            await ExecuteFilterKeysShortcutMutationAsync(false, capture, apply, restore,
                cancelledBeforeStart.Token);
        }
        catch (OperationCanceledException) { preCancelOff = true; }
        Check(preCancelOff && restores == priorRestores, "pre-cancel OFF");
    }
}
