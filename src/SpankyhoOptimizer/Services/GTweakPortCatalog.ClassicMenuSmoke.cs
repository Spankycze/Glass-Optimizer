using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Pure production-delegate tests: no registry reads/writes, backup file I/O,
    // process restarts, or Windows settings modifications.
    internal static async Task RunClassicMenuMutationSmokeAsync()
    {
        static void Verify(bool ok, string step)
        {
            if (!ok) throw new InvalidOperationException("CLASSIC_MENU_SMOKE_FAIL: " + step);
        }

        // 1. Catalogue must expose the existing ID, reversible optional toggle.
        var tweak = ClassicContextMenu();
        Verify(tweak.Id == ClassicMenuId && tweak.Section == "ui" &&
               !tweak.Recommended && !tweak.RequiresAdmin &&
               tweak.RestartRequired && tweak.RevertAsync is not null &&
               tweak.RevertMode == "Explicit", "catalogue wiring");

        // 2-4. Receipts distinguish a new CLSID, an existing root, and an existing leaf.
        var first = ParseClassicMenuReceipt(EncodeClassicMenuReceipt(false, false));
        Verify(!first.RootExisted && !first.LeafExisted, "new root receipt");
        var second = ParseClassicMenuReceipt(EncodeClassicMenuReceipt(true, false));
        Verify(second.RootExisted && !second.LeafExisted, "existing root receipt");
        var third = ParseClassicMenuReceipt(EncodeClassicMenuReceipt(true, true));
        Verify(third.RootExisted && third.LeafExisted, "existing leaf receipt");

        // 5. Invalid or inconsistent ownership metadata must fail closed.
        bool invalidReceipt = false;
        try { _ = ParseClassicMenuReceipt("classic-menu-v1|0|1"); }
        catch (InvalidDataException) { invalidReceipt = true; }
        Verify(invalidReceipt, "invalid ownership record");

        // 6. An owned leaf can be removed only with its exact expected contents.
        Verify(CanRemoveClassicMenuLeaf(true, false, false) &&
               !CanRemoveClassicMenuLeaf(false, false, false) &&
               !CanRemoveClassicMenuLeaf(true, true, false) &&
               !CanRemoveClassicMenuLeaf(true, false, true), "safe leaf removal");

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
        Func<CancellationToken, Task> restore = ct =>
        {
            Verify(!ct.IsCancellationRequested, "cancelled rollback token");
            restores++;
            return Task.CompletedTask;
        };

        // 7. ON must capture before attempting to create/write a key.
        await ExecuteClassicMenuMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Verify(captures == 1 && writes == 1 && restores == 0, "enable");

        // 8. OFF restores prior state only, never recaptures or applies.
        await ExecuteClassicMenuMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Verify(captures == 1 && writes == 1 && restores == 1, "disable");

        // 9. A partial registry mutation must restore the original state.
        bool writeError = false;
        try
        {
            await ExecuteClassicMenuMutationAsync(true, capture,
                _ => throw new IOException("simulated registry write failure"),
                restore, CancellationToken.None);
        }
        catch (IOException) { writeError = true; }
        Verify(writeError && restores == 2, "partial write rollback");

        // 10. Failed readback is likewise a partial mutation.
        bool readbackError = false;
        try
        {
            await ExecuteClassicMenuMutationAsync(true, capture,
                _ => throw new InvalidDataException("simulated empty-value readback fault"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { readbackError = true; }
        Verify(readbackError && restores == 3, "readback rollback");

        // 11. Failed original capture/receipt persistence prevents all writes.
        int priorWrites = writes;
        bool captureError = false;
        try
        {
            await ExecuteClassicMenuMutationAsync(true,
                _ => throw new IOException("simulated receipt persistence failure"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { captureError = true; }
        Verify(captureError && writes == priorWrites && restores == 3, "capture fail-closed");

        // 12. Failed OFF restoration propagates without fallback deletion.
        bool restoreError = false;
        try
        {
            await ExecuteClassicMenuMutationAsync(false, capture, apply,
                _ => throw new UnauthorizedAccessException("simulated missing ownership"), CancellationToken.None);
        }
        catch (UnauthorizedAccessException) { restoreError = true; }
        Verify(restoreError && writes == priorWrites, "unsafe delete prevented");

        // 13. Both original mutation and rollback errors are reported.
        bool compound = false;
        try
        {
            await ExecuteClassicMenuMutationAsync(true, capture,
                _ => throw new IOException("simulated failed write"),
                _ => throw new InvalidDataException("simulated corrupted receipt"), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compound = ex.InnerExceptions.Count == 2 &&
                       ex.InnerExceptions[0] is IOException &&
                       ex.InnerExceptions[1] is InvalidDataException;
        }
        Verify(compound, "compound failure");

        // 14. Cancellation after a write uses uncancelled rollback.
        using var midWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteClassicMenuMutationAsync(true, capture, _ =>
            {
                midWrite.Cancel();
                return Task.CompletedTask;
            }, restore, midWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Verify(cancelled && restores == 4, "cancelled write rollback");

        // 15. A pre-cancelled request must not touch state, ON or OFF.
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        int lastCaptures = captures, lastRestores = restores;
        bool preOn = false, preOff = false;
        try
        {
            await ExecuteClassicMenuMutationAsync(true, capture, apply, restore, preCancelled.Token);
        }
        catch (OperationCanceledException) { preOn = true; }
        try
        {
            await ExecuteClassicMenuMutationAsync(false, capture, apply, restore, preCancelled.Token);
        }
        catch (OperationCanceledException) { preOff = true; }
        Verify(preOn && preOff && captures == lastCaptures &&
               writes == priorWrites && restores == lastRestores, "pre-cancelled both");
    }
}
