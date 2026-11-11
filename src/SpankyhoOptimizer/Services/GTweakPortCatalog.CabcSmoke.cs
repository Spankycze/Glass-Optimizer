using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // All operations are fake delegates, never actual Windows registry writes.
    internal static async Task RunCabcMutationSmokeAsync()
    {
        static void Ensure(bool test, string caseName)
        {
            if (!test) throw new InvalidOperationException("CABC_MUTATION_SMOKE_FAIL: " + caseName);
        }

        // 1. Production catalogue remains optional, explicit and reversible.
        var tweak = AdaptiveBrightnessOff();
        Ensure(tweak.Id == CabcTweakId && tweak.Section == "ui" &&
            !tweak.Recommended && tweak.RequiresAdmin &&
            tweak.RevertAsync != null && tweak.RevertMode == "Explicit" &&
            CabcBackupIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "catalogue wiring");

        int captures = 0, writes = 0;
        var restores = new List<int>();
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
        Func<int, CancellationToken, Task<bool>> restore = (i, ct) =>
        {
            Ensure(!ct.IsCancellationRequested, "cancelled restore");
            restores.Add(i);
            return Task.FromResult(true);
        };
        int[] both = [0, 1];

        // 2. ON: persisted originals must precede one batch mutation.
        await ExecuteCabcMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Ensure(captures == 1 && writes == 1 && restores.Count == 0, "enable");

        // 3. OFF restores both originals; it does not recapture or write defaults.
        await ExecuteCabcMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Ensure(captures == 1 && writes == 1 && restores.SequenceEqual(both),
            "disable restores originals");
        restores.Clear();

        // 4. A partial write error restores both values.
        bool writeError = false;
        try
        {
            await ExecuteCabcMutationAsync(true, capture,
                _ => throw new IOException("injected second write failure"),
                restore, CancellationToken.None);
        }
        catch (IOException) { writeError = true; }
        Ensure(writeError && restores.SequenceEqual(both), "partial write rollback");
        restores.Clear();

        // 5. Failed registry value/kind readback triggers recovery.
        bool verifyError = false;
        try
        {
            await ExecuteCabcMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected readback mismatch"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { verifyError = true; }
        Ensure(verifyError && restores.SequenceEqual(both), "readback rollback");
        restores.Clear();

        // 6. Backup persistence failure prevents any mutation.
        int previousWrites = writes;
        bool backupError = false;
        try
        {
            await ExecuteCabcMutationAsync(true,
                _ => throw new IOException("injected backup failure"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { backupError = true; }
        Ensure(backupError && writes == previousWrites && restores.Count == 0,
            "backup failure");

        // 7. Restore failure must never suppress attempts to restore later keys.
        bool restoreError = false;
        try
        {
            await ExecuteCabcMutationAsync(false, capture, apply, (i, _) =>
            {
                restores.Add(i);
                if (i == 0) throw new IOException("injected restore failure");
                return Task.FromResult(false);
            }, CancellationToken.None);
        }
        catch (AggregateException ex) { restoreError = ex.InnerExceptions.Count == 2; }
        Ensure(restoreError && restores.SequenceEqual(both), "independent restores");
        restores.Clear();

        // 8. Cancellation after a partial write triggers uncancelled rollback.
        using var cancelled = new CancellationTokenSource();
        bool cancellation = false;
        try
        {
            await ExecuteCabcMutationAsync(true, capture, _ =>
            {
                cancelled.Cancel();
                return Task.CompletedTask;
            }, restore, cancelled.Token);
        }
        catch (OperationCanceledException) { cancellation = true; }
        Ensure(cancellation && restores.SequenceEqual(both), "cancelled rollback");
        restores.Clear();

        // 9. Missing backup returns compound failure with the original error.
        bool missingOriginal = false;
        try
        {
            await ExecuteCabcMutationAsync(true, capture,
                _ => throw new IOException("injected write error"),
                (i, _) => Task.FromResult(i != 1), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            missingOriginal = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is AggregateException;
        }
        Ensure(missingOriginal, "missing original detected");

        // 10. Both thrown errors are retained.
        bool bothExceptions = false;
        try
        {
            await ExecuteCabcMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected verification failure"),
                (i, _) => i == 0
                    ? Task.FromException<bool>(new IOException("injected restore error"))
                    : Task.FromResult(true), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            bothExceptions = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is InvalidDataException &&
                ex.InnerExceptions[1] is AggregateException;
        }
        Ensure(bothExceptions, "compound exception reporting");

        // 11. Pre-cancelled ON does not capture or modify anything.
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        int previousCaptures = captures;
        bool preCancelled = false;
        try
        {
            await ExecuteCabcMutationAsync(true, capture, apply, restore,
                alreadyCancelled.Token);
        }
        catch (OperationCanceledException) { preCancelled = true; }
        Ensure(preCancelled && captures == previousCaptures && writes == previousWrites,
            "pre-cancelled enable");

        // 12. Pre-cancelled OFF must not restore any saved original.
        preCancelled = false;
        try
        {
            await ExecuteCabcMutationAsync(false, capture, apply, restore,
                alreadyCancelled.Token);
        }
        catch (OperationCanceledException) { preCancelled = true; }
        Ensure(preCancelled && restores.Count == 0, "pre-cancelled disable");
    }
}
