using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Tests the exact production delegate-based mutation path without
    // Windows registry, file-receipt, Explorer or user-settings operations.
    internal static async Task RunExplorerHomeMutationSmokeAsync()
    {
        static void Check(bool ok, string scenario)
        {
            if (!ok)
                throw new InvalidOperationException(
                    "EXPLORER_HOME_MUTATION_SMOKE_FAIL: " + scenario);
        }

        // 1. Stable catalogue ID/metadata, no elevation and explicit revert.
        var tweak = ExplorerHomeTweak();
        Check(tweak.Id == ExplorerHomeId && tweak.Section == "ui" &&
              !tweak.Recommended && !tweak.RequiresAdmin &&
              tweak.RestartRequired && tweak.RevertAsync is not null &&
              tweak.RevertMode == "Explicit", "catalog metadata");

        // 2-3. Durable original-presence metadata is unambiguous.
        Check(DecodeExplorerHomeReceipt(EncodeExplorerHomeReceipt(true)),
            "pre-existing key receipt");
        Check(!DecodeExplorerHomeReceipt(EncodeExplorerHomeReceipt(false)),
            "originally absent key receipt");

        // 4. Invalid, mixed-format or missing receipts must fail closed.
        bool badReceipt = false;
        try { _ = DecodeExplorerHomeReceipt("explorer-home-v1|other-id|1"); }
        catch (InvalidDataException) { badReceipt = true; }
        Check(badReceipt, "invalid ownership record");

        // 5. Only an app-owned empty default state can be removed.
        Check(CanRemoveExplorerHomeKey(false, true, true, false, false) &&
              !CanRemoveExplorerHomeKey(true, true, true, false, false) &&
              !CanRemoveExplorerHomeKey(false, false, true, false, false) &&
              !CanRemoveExplorerHomeKey(false, true, false, false, false) &&
              !CanRemoveExplorerHomeKey(false, true, true, true, false) &&
              !CanRemoveExplorerHomeKey(false, true, true, false, true),
              "no external key deletion");

        int saves = 0, writes = 0, restores = 0;
        Func<CancellationToken, Task> capture = _ =>
        {
            saves++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> write = _ =>
        {
            writes++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> restore = token =>
        {
            Check(!token.IsCancellationRequested, "rollback token cancelled");
            restores++;
            return Task.CompletedTask;
        };

        // 6. ON captures original before writing.
        await ExecuteExplorerHomeMutationAsync(
            true, capture, write, restore, CancellationToken.None);
        Check(saves == 1 && writes == 1 && restores == 0, "successful ON");

        // 7. OFF restores saved state only; it doesn't recapture or write.
        await ExecuteExplorerHomeMutationAsync(
            false, capture, write, restore, CancellationToken.None);
        Check(saves == 1 && writes == 1 && restores == 1, "successful OFF");

        // 8. Partial registry failure must attempt rollback.
        bool badWrite = false;
        try
        {
            await ExecuteExplorerHomeMutationAsync(true, capture,
                _ => throw new IOException("injected registry write failure"),
                restore, CancellationToken.None);
        }
        catch (IOException) { badWrite = true; }
        Check(badWrite && restores == 2, "failed write rollback");

        // 9. Missing or wrong DWORD after write also rolls back.
        bool badReadback = false;
        try
        {
            await ExecuteExplorerHomeMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected DWORD mismatch"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { badReadback = true; }
        Check(badReadback && restores == 3, "readback rollback");

        // 10. A failed durable backup must not start a registry write.
        int beforeWrite = writes;
        bool captureFailed = false;
        try
        {
            await ExecuteExplorerHomeMutationAsync(true,
                _ => throw new IOException("injected receipt disk failure"),
                write, restore, CancellationToken.None);
        }
        catch (IOException) { captureFailed = true; }
        Check(captureFailed && writes == beforeWrite && restores == 3,
              "backup persistence fail closed");

        // 11. OFF with a missing receipt never silently deletes user settings.
        bool missingOriginal = false;
        try
        {
            await ExecuteExplorerHomeMutationAsync(false, capture, write,
                _ => throw new InvalidOperationException("no ownership receipt"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { missingOriginal = true; }
        Check(missingOriginal && writes == beforeWrite, "missing receipt OFF");

        // 12. Preserve both original mutation and restore exceptions.
        bool bothFailed = false;
        try
        {
            await ExecuteExplorerHomeMutationAsync(true, capture,
                _ => throw new IOException("injected registry failure"),
                _ => throw new UnauthorizedAccessException("injected restore failure"),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            bothFailed = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(bothFailed, "combined mutation and restore errors");

        // 13. Post-write cancellation always restores using a fresh token.
        using var cancellation = new CancellationTokenSource();
        bool cancelledAfterWrite = false;
        try
        {
            await ExecuteExplorerHomeMutationAsync(true, capture,
                _ =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                }, restore, cancellation.Token);
        }
        catch (OperationCanceledException) { cancelledAfterWrite = true; }
        Check(cancelledAfterWrite && restores == 4, "cancelled mutation restore");

        // 14-15. Pre-cancelled ON/OFF must not touch anything.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        int beforeSave = saves, beforeRestore = restores;
        bool preOn = false, preOff = false;
        try
        {
            await ExecuteExplorerHomeMutationAsync(true, capture, write, restore, pre.Token);
        }
        catch (OperationCanceledException) { preOn = true; }
        Check(preOn && saves == beforeSave &&
              writes == beforeWrite && restores == beforeRestore, "pre-cancel ON");
        try
        {
            await ExecuteExplorerHomeMutationAsync(false, capture, write, restore, pre.Token);
        }
        catch (OperationCanceledException) { preOff = true; }
        Check(preOff && restores == beforeRestore, "pre-cancel OFF");

        // 16. Cancellation after capture but before write doesn't mutate.
        using var afterCapture = new CancellationTokenSource();
        bool cancelledCapture = false;
        try
        {
            await ExecuteExplorerHomeMutationAsync(true,
                _ =>
                {
                    afterCapture.Cancel();
                    return Task.CompletedTask;
                }, write, restore, afterCapture.Token);
        }
        catch (OperationCanceledException) { cancelledCapture = true; }
        Check(cancelledCapture && writes == beforeWrite &&
              restores == beforeRestore, "cancel before write");
    }
}
