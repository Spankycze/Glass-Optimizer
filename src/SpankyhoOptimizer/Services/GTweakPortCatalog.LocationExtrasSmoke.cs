using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Pure-delegate test of the production operation; no Windows registry writes.
    internal static async Task RunLocationExtrasMutationSmokeAsync()
    {
        static void Check(bool passed, string stage)
        {
            if (!passed) throw new InvalidOperationException("LOCATION_EXTRAS_SMOKE_FAIL: " + stage);
        }

        // 1. Existing catalog entry is wired to reversible original policy backup.
        var tweak = LocationProviderExtras();
        Check(tweak.Id == LocationExtrasId && tweak.Section == "privacy" &&
              !tweak.Recommended && tweak.RequiresAdmin &&
              tweak.RevertAsync is not null && tweak.RevertMode == "Explicit" &&
              LocationExtrasNames.Length == 2 &&
              LocationExtrasNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
              "catalog metadata");

        int captures = 0, writes = 0;
        var restored = new List<int>();
        int[] both = [0, 1];
        Func<CancellationToken, Task> capture = _ =>
        {
            captures++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> write = _ =>
        {
            writes++;
            return Task.CompletedTask;
        };
        Func<int, CancellationToken, Task<bool>> restore = (index, token) =>
        {
            Check(!token.IsCancellationRequested, "rollback cancelled");
            restored.Add(index);
            return Task.FromResult(true);
        };

        // 2. ON stores originals and performs one write operation.
        await ExecuteLocationExtrasMutationAsync(true, capture, write, restore, CancellationToken.None);
        Check(captures == 1 && writes == 1 && restored.Count == 0, "enable");

        // 3. OFF restores both without recapture or new writes.
        await ExecuteLocationExtrasMutationAsync(false, capture, write, restore, CancellationToken.None);
        Check(captures == 1 && writes == 1 && restored.SequenceEqual(both), "disable");
        restored.Clear();

        // 4. Partial registry write failure must restore both originals.
        bool writeFault = false;
        try
        {
            await ExecuteLocationExtrasMutationAsync(true, capture,
                _ => throw new IOException("injected second value write fault"),
                restore, CancellationToken.None);
        }
        catch (IOException) { writeFault = true; }
        Check(writeFault && restored.SequenceEqual(both), "partial write");
        restored.Clear();

        // 5. Bad DWORD kind or readback also requires restoring originals.
        bool readbackFault = false;
        try
        {
            await ExecuteLocationExtrasMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected DWORD readback mismatch"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { readbackFault = true; }
        Check(readbackFault && restored.SequenceEqual(both), "bad readback");
        restored.Clear();

        // 6. Backup persistence fault is fail-closed: never perform a write.
        int priorWrites = writes;
        bool captureFault = false;
        try
        {
            await ExecuteLocationExtrasMutationAsync(true,
                _ => throw new IOException("injected backup write fault"),
                write, restore, CancellationToken.None);
        }
        catch (IOException) { captureFault = true; }
        Check(captureFault && writes == priorWrites && restored.Count == 0, "failed backup");

        // 7. A restore failure must not skip the second value.
        bool incompleteRestore = false;
        try
        {
            await ExecuteLocationExtrasMutationAsync(false, capture, write, (index, _) =>
            {
                restored.Add(index);
                if (index == 0) throw new IOException("injected restore failure");
                return Task.FromResult(false);
            }, CancellationToken.None);
        }
        catch (AggregateException ex) { incompleteRestore = ex.InnerExceptions.Count == 2; }
        Check(incompleteRestore && restored.SequenceEqual(both), "independent restores");
        restored.Clear();

        // 8. Cancellation after a write always triggers uncancelled recovery.
        using var cancelledMidWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteLocationExtrasMutationAsync(true, capture, _ =>
            {
                cancelledMidWrite.Cancel();
                return Task.CompletedTask;
            }, restore, cancelledMidWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && restored.SequenceEqual(both), "cancellation rollback");
        restored.Clear();

        // 9. Mutation error and missing backup both surface together.
        bool combined = false;
        try
        {
            await ExecuteLocationExtrasMutationAsync(true, capture,
                _ => throw new IOException("injected registry fault"),
                (index, _) => Task.FromResult(index != 1), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                       ex.InnerExceptions[0] is IOException &&
                       ex.InnerExceptions[1] is AggregateException;
        }
        Check(combined, "combined errors");

        // 10. A pre-cancelled enable must not capture or write.
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        int priorCaptures = captures;
        bool stopped = false;
        try
        {
            await ExecuteLocationExtrasMutationAsync(true, capture, write, restore,
                preCancelled.Token);
        }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped && captures == priorCaptures && writes == priorWrites,
            "pre-cancelled enable");

        // 11. A pre-cancelled disable must not touch either original.
        stopped = false;
        try
        {
            await ExecuteLocationExtrasMutationAsync(false, capture, write, restore,
                preCancelled.Token);
        }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped && restored.Count == 0, "pre-cancelled disable");
    }
}
