using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Tests production transaction delegates. Does not open or write registry keys.
    internal static async Task RunSettingSyncMutationSmokeAsync()
    {
        static void Assert(bool ok, string caseName)
        {
            if (!ok) throw new InvalidOperationException("SETTING_SYNC_SMOKE_FAILED: " + caseName);
        }

        // 1. Catalog integration and six distinct groups retain the existing ID.
        var definition = SettingSyncOff();
        Assert(definition.Id == SettingSyncTweakId &&
               definition.Section == "privacy" &&
               !definition.Recommended &&
               !definition.RequiresAdmin &&
               definition.RevertAsync is not null &&
               definition.RevertMode == "Explicit" &&
               SettingSyncGroups.Length == 6 &&
               SettingSyncGroups.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 6,
               "catalog wiring");

        int captures = 0, writes = 0;
        var restored = new List<int>();
        int[] all = [0, 1, 2, 3, 4, 5];
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
        Func<int, CancellationToken, Task<bool>> restore = (index, token) =>
        {
            Assert(!token.IsCancellationRequested, "rollback was cancelled");
            restored.Add(index);
            return Task.FromResult(true);
        };

        // 2. Successful enable: one batch capture, one batch mutation.
        await ExecuteSettingSyncMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Assert(captures == 1 && writes == 1 && restored.Count == 0, "enable");

        // 3. OFF: restore all originals and never recapture or write defaults.
        await ExecuteSettingSyncMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Assert(captures == 1 && writes == 1 && restored.SequenceEqual(all), "disable");
        restored.Clear();

        // 4. Any partial write must restore all six originals.
        bool failedWrite = false;
        try
        {
            await ExecuteSettingSyncMutationAsync(true, capture,
                _ => throw new IOException("injected fourth registry write failed"),
                restore, CancellationToken.None);
        }
        catch (IOException) { failedWrite = true; }
        Assert(failedWrite && restored.SequenceEqual(all), "partial write rollback");
        restored.Clear();

        // 5. Failed readback must also recover all groups.
        bool readbackFailure = false;
        try
        {
            await ExecuteSettingSyncMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected registry readback mismatch"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { readbackFailure = true; }
        Assert(readbackFailure && restored.SequenceEqual(all), "readback rollback");
        restored.Clear();

        // 6. A partial/corrupt backup cannot start any writes.
        int writesBeforeBackupFailure = writes;
        bool backupFailure = false;
        try
        {
            await ExecuteSettingSyncMutationAsync(true,
                _ => throw new IOException("injected backup persistence failure"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { backupFailure = true; }
        Assert(backupFailure && writes == writesBeforeBackupFailure &&
               restored.Count == 0, "backup failure prevents write");

        // 7. Recover the remaining groups even if an early restore throws
        // and the next one has no saved original.
        bool partialRestore = false;
        try
        {
            await ExecuteSettingSyncMutationAsync(false, capture, apply, (index, _) =>
            {
                restored.Add(index);
                if (index == 0) throw new IOException("injected restore write failed");
                return Task.FromResult(index != 1);
            }, CancellationToken.None);
        }
        catch (AggregateException ex) { partialRestore = ex.InnerExceptions.Count == 2; }
        Assert(partialRestore && restored.SequenceEqual(all), "independent restoration");
        restored.Clear();

        // 8. Cancellation after partial apply must not cancel rollback.
        using var cancelledMidWrite = new CancellationTokenSource();
        bool cancellationCaught = false;
        try
        {
            await ExecuteSettingSyncMutationAsync(true, capture, _ =>
            {
                cancelledMidWrite.Cancel();
                return Task.CompletedTask;
            }, restore, cancelledMidWrite.Token);
        }
        catch (OperationCanceledException) { cancellationCaught = true; }
        Assert(cancellationCaught && restored.SequenceEqual(all), "cancellation rollback");
        restored.Clear();

        // 9. Mutation and rollback failures are both preserved.
        bool compoundFailure = false;
        try
        {
            await ExecuteSettingSyncMutationAsync(true, capture,
                _ => throw new IOException("injected mutation error"),
                (index, _) => Task.FromResult(index != 2), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compoundFailure = ex.InnerExceptions.Count == 2 &&
                              ex.InnerExceptions[0] is IOException &&
                              ex.InnerExceptions[1] is AggregateException;
        }
        Assert(compoundFailure, "combined error reporting");

        // 10. Pre-cancelled ON must never capture or modify the system.
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        int before = captures;
        bool wasCancelled = false;
        try
        {
            await ExecuteSettingSyncMutationAsync(true, capture, apply, restore,
                alreadyCancelled.Token);
        }
        catch (OperationCanceledException) { wasCancelled = true; }
        Assert(wasCancelled && captures == before && writes == writesBeforeBackupFailure &&
               restored.Count == 0, "pre-cancelled enable");

        // 11. Pre-cancelled OFF must not alter the existing originals.
        wasCancelled = false;
        try
        {
            await ExecuteSettingSyncMutationAsync(false, capture, apply, restore,
                alreadyCancelled.Token);
        }
        catch (OperationCanceledException) { wasCancelled = true; }
        Assert(wasCancelled && restored.Count == 0, "pre-cancelled disable");
    }
}
