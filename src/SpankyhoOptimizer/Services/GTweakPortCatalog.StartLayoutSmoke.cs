using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // This tests production transaction delegates and pure state logic.
    // No real HKCU/HKLM read/write is executed by these fixtures.
    internal static async Task RunStartLayoutMutationSmokeAsync()
    {
        static void Check(bool pass, string scenario)
        {
            if (!pass) throw new InvalidOperationException(
                "START_LAYOUT_MUTATION_SMOKE_FAILED: " + scenario);
        }

        // 1: An incomplete Start policy is not a successfully applied tweak.
        Check(!AreStartLayoutTargetsApplied(false, false) &&
              !AreStartLayoutTargetsApplied(false, true) &&
              !AreStartLayoutTargetsApplied(true, false) &&
              AreStartLayoutTargetsApplied(true, true), "both targets required");

        // 2: Production catalogue keeps its original ID and settings contract.
        var tweak = StartMenuLayoutTweak();
        Check(tweak.Id == "gt.ui.startlayout" && tweak.Section == "ui" &&
              !tweak.Recommended && tweak.RequiresAdmin && tweak.RestartRequired &&
              tweak.RevertAsync is not null && tweak.RevertMode == "Explicit",
              "catalogue binding");

        int saved = 0, writes = 0;
        List<int> restores = new();
        Func<CancellationToken, Task> capture = _ =>
        {
            saved++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> write = _ =>
        {
            writes++;
            return Task.CompletedTask;
        };
        Func<int, CancellationToken, Task<bool>> restore = (index, token) =>
        {
            Check(!token.IsCancellationRequested, "recovery cancelled");
            restores.Add(index);
            return Task.FromResult(true);
        };
        int[] both = [0, 1];

        // 3: ON stores original states before writing.
        await ExecuteStartLayoutMutationAsync(true, capture, write, restore, CancellationToken.None);
        Check(saved == 1 && writes == 1 && restores.Count == 0, "enable");

        // 4: OFF only restores both originals.
        await ExecuteStartLayoutMutationAsync(false, capture, write, restore, CancellationToken.None);
        Check(saved == 1 && writes == 1 && restores.SequenceEqual(both), "disable");
        restores.Clear();

        // 5: A fault in the second write restores both hives.
        bool writeFault = false;
        try
        {
            await ExecuteStartLayoutMutationAsync(true, capture,
                _ => throw new IOException("injected HKLM write failure"),
                restore, CancellationToken.None);
        }
        catch (IOException) { writeFault = true; }
        Check(writeFault && restores.SequenceEqual(both), "partial write rollback");
        restores.Clear();

        // 6: Incorrect type or value readback also rolls back both.
        bool readback = false;
        try
        {
            await ExecuteStartLayoutMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected policy readback mismatch"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { readback = true; }
        Check(readback && restores.SequenceEqual(both), "readback rollback");
        restores.Clear();

        // 7: Backup failure prevents any registry changes.
        int oldWrites = writes;
        bool backupFault = false;
        try
        {
            await ExecuteStartLayoutMutationAsync(true,
                _ => throw new IOException("injected backup failure"),
                write, restore, CancellationToken.None);
        }
        catch (IOException) { backupFault = true; }
        Check(backupFault && writes == oldWrites && restores.Count == 0, "fail closed backup");

        // 8: Restore both independently even if first throws and second is missing.
        bool partialRestore = false;
        try
        {
            await ExecuteStartLayoutMutationAsync(false, capture, write,
                (i, _) =>
                {
                    restores.Add(i);
                    if (i == 0) throw new IOException("injected HKCU restoration error");
                    return Task.FromResult(false);
                }, CancellationToken.None);
        }
        catch (AggregateException ex) { partialRestore = ex.InnerExceptions.Count == 2; }
        Check(partialRestore && restores.SequenceEqual(both), "independent restores");
        restores.Clear();

        // 9: Cancellation after HKCU mutation restores both, uncancelled.
        using var midMutation = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteStartLayoutMutationAsync(true, capture, _ =>
            {
                midMutation.Cancel();
                return Task.CompletedTask;
            }, restore, midMutation.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && restores.SequenceEqual(both), "cancel after write");
        restores.Clear();

        // 10: Both mutation and missing-backup errors are surfaced together.
        bool missing = false;
        try
        {
            await ExecuteStartLayoutMutationAsync(true, capture,
                _ => throw new IOException("injected write error"),
                (i, _) => Task.FromResult(i == 0), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            missing = ex.InnerExceptions.Count == 2 &&
                      ex.InnerExceptions[0] is IOException &&
                      ex.InnerExceptions[1] is AggregateException;
        }
        Check(missing, "missing backup reporting");

        // 11: A thrown rollback failure preserves the mutation error.
        bool combined = false;
        try
        {
            await ExecuteStartLayoutMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected verify failure"),
                (i, _) => i == 0
                    ? Task.FromException<bool>(new UnauthorizedAccessException("injected HKCU restore failure"))
                    : Task.FromResult(true), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                       ex.InnerExceptions[0] is InvalidDataException &&
                       ex.InnerExceptions[1] is AggregateException;
        }
        Check(combined, "combined exceptions");

        // 12: Pre-cancelled ON never writes/captures/restores.
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        int oldSaved = saved;
        bool preCancel = false;
        try
        {
            await ExecuteStartLayoutMutationAsync(true, capture, write, restore, alreadyCancelled.Token);
        }
        catch (OperationCanceledException) { preCancel = true; }
        Check(preCancel && saved == oldSaved && writes == oldWrites &&
              restores.Count == 0, "pre-cancelled ON");

        // 13: Pre-cancelled OFF never restores.
        preCancel = false;
        try
        {
            await ExecuteStartLayoutMutationAsync(false, capture, write, restore, alreadyCancelled.Token);
        }
        catch (OperationCanceledException) { preCancel = true; }
        Check(preCancel && restores.Count == 0, "pre-cancelled OFF");

        // 14: Cancellation immediately after backup cannot start mutation.
        using var afterCapture = new CancellationTokenSource();
        bool captureCancel = false;
        try
        {
            await ExecuteStartLayoutMutationAsync(true, _ =>
            {
                afterCapture.Cancel();
                return Task.CompletedTask;
            }, write, restore, afterCapture.Token);
        }
        catch (OperationCanceledException) { captureCancel = true; }
        Check(captureCancel && writes == oldWrites && restores.Count == 0,
              "cancel during capture");
    }
}
