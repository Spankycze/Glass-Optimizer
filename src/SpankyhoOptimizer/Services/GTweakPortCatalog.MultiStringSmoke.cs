using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // All assertions exercise the production transaction with injected fakes.
    // These tests never read/write HKCU or modify shell appearance.
    internal static async Task RunMultiStringMutationSmokeAsync()
    {
        static void Check(bool ok, string scenario)
        {
            if (!ok) throw new InvalidOperationException("MULTISTRING_SMOKE_FAIL: " + scenario);
        }

        // 1. Both existing presets keep stable IDs and expose exact revert.
        var scrollbar = ScrollbarMetricsPreset();
        var caption = WindowMetricsPreset();
        Check(scrollbar.Id == "gt.ui.scrollbarsize" &&
              caption.Id == "gt.ui.windowmetrics" &&
              scrollbar.Section == "ui" && caption.Section == "ui" &&
              !scrollbar.Recommended && !caption.Recommended &&
              scrollbar.RevertAsync is not null && caption.RevertAsync is not null &&
              scrollbar.RevertMode == "Explicit" && caption.RevertMode == "Explicit" &&
              !scrollbar.RequiresAdmin && !caption.RequiresAdmin &&
              scrollbar.RestartRequired && caption.RestartRequired,
              "catalog metadata");

        int backups = 0, writes = 0;
        var restored = new List<int>();
        Func<CancellationToken, Task> capture = _ =>
        {
            backups++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> write = _ =>
        {
            writes++;
            return Task.CompletedTask;
        };
        Func<int, CancellationToken, Task<bool>> restore = (index, token) =>
        {
            Check(!token.IsCancellationRequested, "cancelled restore token");
            restored.Add(index);
            return Task.FromResult(true);
        };
        int[] both = [0, 1];

        // 2. Successful ON captures before a single grouped mutation.
        await ExecuteMultiStringMutationAsync(true, 2, capture, write, restore, CancellationToken.None);
        Check(backups == 1 && writes == 1 && restored.Count == 0, "enable");

        // 3. OFF restores exact originals; no new captures or writes.
        await ExecuteMultiStringMutationAsync(false, 2, capture, write, restore, CancellationToken.None);
        Check(backups == 1 && writes == 1 && restored.SequenceEqual(both), "disable");
        restored.Clear();

        // 4. Failed second write rolls back both originals.
        bool writeError = false;
        try
        {
            await ExecuteMultiStringMutationAsync(true, 2, capture,
                _ => throw new IOException("injected second registry write failure"),
                restore, CancellationToken.None);
        }
        catch (IOException) { writeError = true; }
        Check(writeError && restored.SequenceEqual(both), "partial write rollback");
        restored.Clear();

        // 5. Wrong readback of REG_SZ type/value restores both.
        bool mismatch = false;
        try
        {
            await ExecuteMultiStringMutationAsync(true, 2, capture,
                _ => throw new InvalidDataException("injected readback mismatch"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { mismatch = true; }
        Check(mismatch && restored.SequenceEqual(both), "readback rollback");
        restored.Clear();

        // 6. Backup failure prevents ANY write.
        int oldWrites = writes;
        bool captureFailure = false;
        try
        {
            await ExecuteMultiStringMutationAsync(true, 2,
                _ => throw new IOException("injected backup persistence failure"),
                write, restore, CancellationToken.None);
        }
        catch (IOException) { captureFailure = true; }
        Check(captureFailure && writes == oldWrites && restored.Count == 0,
              "backup must precede first write");

        // 7. Restore all originals even if the first restore throws and second
        // value has no saved backup.
        bool restoreFailure = false;
        try
        {
            await ExecuteMultiStringMutationAsync(false, 2, capture, write,
                (index, _) =>
                {
                    restored.Add(index);
                    if (index == 0) throw new IOException("injected restore failure");
                    return Task.FromResult(false);
                }, CancellationToken.None);
        }
        catch (AggregateException ex) { restoreFailure = ex.InnerExceptions.Count == 2; }
        Check(restoreFailure && restored.SequenceEqual(both), "independent restores");
        restored.Clear();

        // 8. Cancellation after a write triggers uncancelled full rollback.
        using var afterWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteMultiStringMutationAsync(true, 2, capture, _ =>
            {
                afterWrite.Cancel();
                return Task.CompletedTask;
            }, restore, afterWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && restored.SequenceEqual(both), "cancellation rollback");
        restored.Clear();

        // 9. Missing backup after failed mutation surfaces both causes.
        bool missingBackup = false;
        try
        {
            await ExecuteMultiStringMutationAsync(true, 2, capture,
                _ => throw new IOException("injected write error"),
                (i, _) => Task.FromResult(i != 1), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            missingBackup = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is AggregateException;
        }
        Check(missingBackup, "missing rollback backup");

        // 10. Thrown rollback failure retains original readback error.
        bool combined = false;
        try
        {
            await ExecuteMultiStringMutationAsync(true, 2, capture,
                _ => throw new InvalidDataException("injected readback failure"),
                (i, _) => i == 0
                    ? Task.FromException<bool>(new UnauthorizedAccessException("injected restore exception"))
                    : Task.FromResult(true), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is InvalidDataException &&
                ex.InnerExceptions[1] is AggregateException;
        }
        Check(combined, "combined exceptions");

        // 11. Pre-cancelled ON may not capture or mutate Windows.
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        int oldBackups = backups;
        bool preCancelledOn = false;
        try
        {
            await ExecuteMultiStringMutationAsync(true, 2, capture, write, restore, preCancelled.Token);
        }
        catch (OperationCanceledException) { preCancelledOn = true; }
        Check(preCancelledOn && backups == oldBackups && writes == oldWrites &&
              restored.Count == 0, "pre-cancel ON");

        // 12. Pre-cancelled OFF may not change any original.
        bool preCancelledOff = false;
        try
        {
            await ExecuteMultiStringMutationAsync(false, 2, capture, write, restore, preCancelled.Token);
        }
        catch (OperationCanceledException) { preCancelledOff = true; }
        Check(preCancelledOff && restored.Count == 0, "pre-cancel OFF");

        // 13. Cancellation during backup capture prevents mutation.
        using var duringCapture = new CancellationTokenSource();
        bool stoppedAfterCapture = false;
        try
        {
            await ExecuteMultiStringMutationAsync(true, 2,
                _ =>
                {
                    duringCapture.Cancel();
                    return Task.CompletedTask;
                }, write, restore, duringCapture.Token);
        }
        catch (OperationCanceledException) { stoppedAfterCapture = true; }
        Check(stoppedAfterCapture && writes == oldWrites && restored.Count == 0,
              "cancel during capture");

        // 14. Invalid target count rejects before any I/O.
        bool invalidCount = false;
        try
        {
            await ExecuteMultiStringMutationAsync(true, 0, capture, write, restore, CancellationToken.None);
        }
        catch (ArgumentOutOfRangeException) { invalidCount = true; }
        Check(invalidCount && writes == oldWrites && backups == oldBackups,
              "invalid target count");
    }
}
