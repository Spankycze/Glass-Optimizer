using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // No Windows registry, filesystem receipt or Explorer operations occur.
    // Transaction delegates are the same ones used in production.
    internal static async Task RunThisPcFolderMutationSmokeAsync()
    {
        static void Assert(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException("THIS_PC_FOLDERS_SMOKE_FAIL: " + name);
        }

        string[] expected =
        [
            "gt.ui.folder3d", "gt.ui.folderdesktop", "gt.ui.folderdownloads",
            "gt.ui.folderdocuments", "gt.ui.folderpictures",
            "gt.ui.foldermusic", "gt.ui.foldervideos"
        ];
        var definitions = UiTweaks()
            .Where(t => expected.Contains(t.Id, StringComparer.Ordinal)).ToArray();
        // 1. Preserve each original public switch and explicit undo.
        Assert(definitions.Length == 7 &&
               definitions.Select(t => t.Id).SequenceEqual(expected) &&
               definitions.All(t =>
                   t.Section == "ui" && t.Group == "Tento počítač" &&
                   !t.Recommended && t.RequiresAdmin && t.RestartRequired &&
                   t.RevertAsync is not null && t.RevertMode == "Explicit"),
               "seven existing options");

        const string id = "gt.ui.folderdownloads";
        const string clsid = "{088e3905-0323-4b02-9826-5d99428e115f}";

        // 2, 3. Receipts explicitly distinguish original presence or absence.
        Assert(ParseThisPcReceipt(EncodeThisPcReceipt(id, clsid, true), id, clsid),
            "receipt originally present");
        Assert(!ParseThisPcReceipt(EncodeThisPcReceipt(id, clsid, false), id, clsid),
            "receipt originally absent");

        // 4, 5. Invalid ownership and other CLSIDs are never accepted.
        bool invalid = false;
        try { _ = ParseThisPcReceipt("thispc-namespace-v1|x|y|2", id, clsid); }
        catch (InvalidDataException) { invalid = true; }
        Assert(invalid, "invalid receipt");
        invalid = false;
        try
        {
            _ = ParseThisPcReceipt(
                EncodeThisPcReceipt(id, "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", true),
                id, clsid);
        }
        catch (InvalidDataException) { invalid = true; }
        Assert(invalid, "receipt wrong CLSID");

        // 6. Only absent or empty namespace entries can be removed.
        Assert(CanDeleteThisPcNamespaceKey(false, false, false) &&
               CanDeleteThisPcNamespaceKey(true, false, false) &&
               !CanDeleteThisPcNamespaceKey(true, true, false) &&
               !CanDeleteThisPcNamespaceKey(true, false, true) &&
               !CanDeleteThisPcNamespaceKey(true, true, true),
               "no deletion of populated namespaces");

        int captured = 0, removals = 0, restores = 0;
        Func<CancellationToken, Task> capture = _ =>
        {
            captured++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> remove = _ =>
        {
            removals++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> restore = ct =>
        {
            Assert(!ct.IsCancellationRequested, "rollback cancellation token");
            restores++;
            return Task.CompletedTask;
        };

        // 7. ON first captures, then attempts safe key removal.
        await ExecuteThisPcFolderMutationAsync(
            true, capture, remove, restore, CancellationToken.None);
        Assert(captured == 1 && removals == 1 && restores == 0, "ON");

        // 8. OFF restores only, never replaces original with a new empty key.
        await ExecuteThisPcFolderMutationAsync(
            false, capture, remove, restore, CancellationToken.None);
        Assert(captured == 1 && removals == 1 && restores == 1, "OFF");

        // 9. A rejected deletion must attempt restoration and report the error.
        bool failedRemove = false;
        try
        {
            await ExecuteThisPcFolderMutationAsync(true, capture,
                _ => throw new IOException("simulated deletion failure"),
                restore, CancellationToken.None);
        }
        catch (IOException) { failedRemove = true; }
        Assert(failedRemove && restores == 2, "failed removal recovery");

        // 10. Receipt capture failure does not delete anything.
        int removalsBefore = removals;
        bool failedCapture = false;
        try
        {
            await ExecuteThisPcFolderMutationAsync(true,
                _ => throw new InvalidDataException("simulated old receipt fault"),
                remove, restore, CancellationToken.None);
        }
        catch (InvalidDataException) { failedCapture = true; }
        Assert(failedCapture && removals == removalsBefore && restores == 2,
               "bad receipt fails closed");

        // 11. Missing receipt on OFF never creates a guessed folder.
        bool failedOff = false;
        try
        {
            await ExecuteThisPcFolderMutationAsync(false, capture, remove,
                _ => throw new InvalidOperationException("simulated missing original"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { failedOff = true; }
        Assert(failedOff && removals == removalsBefore, "legacy OFF fail closed");

        // 12. Mutation and rollback exceptions remain visible together.
        bool combined = false;
        try
        {
            await ExecuteThisPcFolderMutationAsync(true, capture,
                _ => throw new IOException("simulated deletion fault"),
                _ => throw new UnauthorizedAccessException("simulated restore fault"),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                       ex.InnerExceptions[0] is IOException &&
                       ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Assert(combined, "compound failure");

        // 13. Cancellation after a simulated delete still attempts uncancelled recovery.
        using var cancelled = new CancellationTokenSource();
        bool cancelledWrite = false;
        try
        {
            await ExecuteThisPcFolderMutationAsync(true, capture,
                _ =>
                {
                    cancelled.Cancel();
                    return Task.CompletedTask;
                }, restore, cancelled.Token);
        }
        catch (OperationCanceledException) { cancelledWrite = true; }
        Assert(cancelledWrite && restores == 3, "post-delete cancellation recovery");

        // 14. Pre-cancelled ON does not touch any saved state.
        using var beforeStart = new CancellationTokenSource();
        beforeStart.Cancel();
        int captureBefore = captured, restoredBefore = restores;
        bool preOn = false;
        try
        {
            await ExecuteThisPcFolderMutationAsync(
                true, capture, remove, restore, beforeStart.Token);
        }
        catch (OperationCanceledException) { preOn = true; }
        Assert(preOn && captured == captureBefore &&
               removals == removalsBefore && restores == restoredBefore,
               "pre-cancelled ON");

        // 15. Pre-cancelled OFF does not create missing entries.
        bool preOff = false;
        try
        {
            await ExecuteThisPcFolderMutationAsync(
                false, capture, remove, restore, beforeStart.Token);
        }
        catch (OperationCanceledException) { preOff = true; }
        Assert(preOff && restores == restoredBefore, "pre-cancelled OFF");

        // 16. Cancellation after capture but before deletion must not remove entries.
        using var duringCapture = new CancellationTokenSource();
        bool cancelledCapture = false;
        try
        {
            await ExecuteThisPcFolderMutationAsync(true,
                _ =>
                {
                    duringCapture.Cancel();
                    return Task.CompletedTask;
                }, remove, restore, duringCapture.Token);
        }
        catch (OperationCanceledException) { cancelledCapture = true; }
        Assert(cancelledCapture && removals == removalsBefore, "cancelled capture");
    }
}
