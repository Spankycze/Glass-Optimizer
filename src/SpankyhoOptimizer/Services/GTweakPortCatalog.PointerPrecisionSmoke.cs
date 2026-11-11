using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // This tests production delegates with fakes; no registry keys or Windows
    // pointer settings are read or modified.
    internal static async Task RunPointerPrecisionMutationSmokeAsync()
    {
        static void Verify(bool ok, string caseName)
        {
            if (!ok) throw new InvalidOperationException("POINTER_PRECISION_SMOKE_FAIL: " + caseName);
        }

        var tweak = PointerPrecisionOff();
        Verify(tweak.Id == PointerPrecisionId && tweak.Section == "system" &&
            tweak.RevertAsync != null && !tweak.RequiresAdmin &&
            !tweak.Recommended && PointerPrecisionNames.Length == 3 &&
            PointerPrecisionNames.Distinct(StringComparer.Ordinal).Count() == 3,
            "catalog binding");

        int captured = 0, writes = 0;
        var restored = new List<int>();
        Func<CancellationToken, Task> capture = _ =>
        {
            captured++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = _ =>
        {
            writes++;
            return Task.CompletedTask;
        };
        Func<int, CancellationToken, Task<bool>> restore = (index, token) =>
        {
            Verify(!token.IsCancellationRequested, "recovery used cancelled token");
            restored.Add(index);
            return Task.FromResult(true);
        };
        int[] all = [0, 1, 2];

        // 1: Enabling captures all originals before a single mutation.
        await ExecutePointerPrecisionMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Verify(captured == 1 && writes == 1 && restored.Count == 0, "enable");

        // 2: Disabling restores all 3 saved original values without writing defaults.
        await ExecutePointerPrecisionMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Verify(captured == 1 && writes == 1 && restored.SequenceEqual(all), "disable");
        restored.Clear();

        // 3: Partial mutation must roll back every value.
        bool writeFailed = false;
        try
        {
            await ExecutePointerPrecisionMutationAsync(true, capture,
                _ => throw new IOException("injected partial write failure"), restore, CancellationToken.None);
        }
        catch (IOException) { writeFailed = true; }
        Verify(writeFailed && restored.SequenceEqual(all), "partial write failure");
        restored.Clear();

        // 4: A failed readback must also roll back.
        bool readbackFailed = false;
        try
        {
            await ExecutePointerPrecisionMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected verification failure"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { readbackFailed = true; }
        Verify(readbackFailed && restored.SequenceEqual(all), "readback failure");
        restored.Clear();

        // 5: Backup failure cannot invoke any mutation.
        int previousWrites = writes;
        bool backupFailed = false;
        try
        {
            await ExecutePointerPrecisionMutationAsync(true,
                _ => throw new IOException("injected backup failure"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { backupFailed = true; }
        Verify(backupFailed && writes == previousWrites && restored.Count == 0,
            "backup failure is fail-closed");

        // 6: A broken restore does not prevent attempts for later entries.
        bool restoreFailed = false;
        try
        {
            await ExecutePointerPrecisionMutationAsync(false, capture, apply, (i, _) =>
            {
                restored.Add(i);
                if (i == 0) throw new IOException("injected first restore failure");
                return Task.FromResult(i != 1);
            }, CancellationToken.None);
        }
        catch (AggregateException ex) { restoreFailed = ex.InnerExceptions.Count == 2; }
        Verify(restoreFailed && restored.SequenceEqual(all), "independent restores");
        restored.Clear();

        // 7: Cancellation after a write must not cancel rollback.
        using var cancellation = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecutePointerPrecisionMutationAsync(true, capture, _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }, restore, cancellation.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Verify(cancelled && restored.SequenceEqual(all), "cancellation recovery");
        restored.Clear();

        // 8: Missing original is reported as a compound failure.
        bool missingOriginal = false;
        try
        {
            await ExecutePointerPrecisionMutationAsync(true, capture,
                _ => throw new IOException("injected write failure"),
                (i, _) => Task.FromResult(i != 2), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            missingOriginal = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is AggregateException;
        }
        Verify(missingOriginal, "missing original backup");

        // 9: Both thrown errors must remain observable.
        bool combined = false;
        try
        {
            await ExecutePointerPrecisionMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected readback failure"),
                (i, _) => i == 0
                    ? Task.FromException<bool>(new IOException("injected rollback failure"))
                    : Task.FromResult(true), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is InvalidDataException &&
                ex.InnerExceptions[1] is AggregateException;
        }
        Verify(combined, "compound failure reporting");

        // 10: An already-cancelled request must not capture or modify anything.
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        int previousCaptures = captured;
        bool preCancelled = false;
        try
        {
            await ExecutePointerPrecisionMutationAsync(true, capture, apply, restore,
                alreadyCancelled.Token);
        }
        catch (OperationCanceledException) { preCancelled = true; }
        Verify(preCancelled && captured == previousCaptures && writes == previousWrites,
            "pre-cancellation");

        // 11: Already-cancelled disable must not access or change originals.
        preCancelled = false;
        try
        {
            await ExecutePointerPrecisionMutationAsync(false, capture, apply, restore,
                alreadyCancelled.Token);
        }
        catch (OperationCanceledException) { preCancelled = true; }
        Verify(preCancelled && restored.Count == 0, "pre-cancelled disable");
    }
}
