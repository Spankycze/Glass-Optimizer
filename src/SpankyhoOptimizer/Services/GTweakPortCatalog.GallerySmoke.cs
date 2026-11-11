using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Production transaction and helpers tested with fake registry delegates.
    // No actual HKLM reads/writes, Explorer restarts or receipt files used.
    internal static async Task RunGalleryMutationSmokeAsync()
    {
        static void Check(bool yes, string test)
        {
            if (!yes) throw new InvalidOperationException("GALLERY_SMOKE_FAIL: " + test);
        }
        // 1. Stable ID, opt-in classification, privilege and revert.
        var item = ExplorerGalleryTweak();
        Check(item.Id == GalleryId && item.Section == "ui" &&
              !item.Recommended && item.RequiresAdmin &&
              item.RestartRequired && item.RevertAsync is not null &&
              item.RevertMode == "Explicit", "catalog wiring");

        // 2. Exactly two distinct native/WOW namespaces in HKLM.
        Check(GalleryPaths.Length == 2 &&
            GalleryPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 &&
            GalleryPaths.All(x => x.EndsWith(GalleryClsid, StringComparison.OrdinalIgnoreCase)),
            "both gallery views");

        // 3-6. Durable receipts preserve original missing/present combinations.
        var none = DecodeGalleryReceipt(EncodeGalleryReceipt(false, false));
        Check(!none.Native && !none.Wow, "original both absent");
        var both = DecodeGalleryReceipt(EncodeGalleryReceipt(true, true));
        Check(both.Native && both.Wow, "original both present");
        var native = DecodeGalleryReceipt(EncodeGalleryReceipt(true, false));
        Check(native.Native && !native.Wow, "original native only");
        var wow = DecodeGalleryReceipt(EncodeGalleryReceipt(false, true));
        Check(!wow.Native && wow.Wow, "original wow only");

        // 7-8. Invalid and unrelated ownership records fail closed.
        bool bad = false;
        try { _ = DecodeGalleryReceipt("gallery-namespace-v1|other|1|1"); }
        catch (InvalidDataException) { bad = true; }
        Check(bad, "receipt unrelated feature");
        bad = false;
        try { _ = DecodeGalleryReceipt("gallery-namespace-v1|gt.ui.gallery|0|2"); }
        catch (InvalidDataException) { bad = true; }
        Check(bad, "receipt invalid boolean");

        // 9. Only original, empty namespaces are eligible for deletion.
        Check(CanDeleteGalleryNamespace(true, true, false, false) &&
            CanDeleteGalleryNamespace(false, false, false, false) &&
            !CanDeleteGalleryNamespace(false, true, false, false) &&
            !CanDeleteGalleryNamespace(true, true, true, false) &&
            !CanDeleteGalleryNamespace(true, true, false, true),
            "preserve external registry content");

        int captured = 0, applied = 0, restored = 0;
        Func<CancellationToken, Task> capture = _ =>
        {
            captured++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = _ =>
        {
            applied++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> restore = token =>
        {
            Check(!token.IsCancellationRequested, "cancelled recovery token");
            restored++;
            return Task.CompletedTask;
        };
        // 10. ON saves original before deleting any registry key.
        await ExecuteGalleryMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Check(captured == 1 && applied == 1 && restored == 0, "ON");

        // 11. OFF restores only, never recaptures or recreates absent entries.
        await ExecuteGalleryMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(captured == 1 && applied == 1 && restored == 1, "OFF");

        // 12. The second deletion failing rolls back earlier removals.
        bool failed = false;
        try
        {
            await ExecuteGalleryMutationAsync(true, capture,
                _ => throw new IOException("simulated second view delete failure"),
                restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && restored == 2, "partial deletion recovery");

        // 13. Receipt persistence error stops before any deletion.
        int oldApplied = applied;
        failed = false;
        try
        {
            await ExecuteGalleryMutationAsync(true,
                _ => throw new IOException("simulated durable receipt failure"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && applied == oldApplied && restored == 2, "backup before deletion");

        // 14. OFF lacking a trustworthy receipt never recreates guessed keys.
        failed = false;
        try
        {
            await ExecuteGalleryMutationAsync(false, capture, apply,
                _ => throw new InvalidDataException("simulated missing ownership"),
                CancellationToken.None);
        }
        catch (InvalidDataException) { failed = true; }
        Check(failed && applied == oldApplied, "no legacy forced restore");

        // 15. Preserve both mutation and rollback exceptions.
        bool combined = false;
        try
        {
            await ExecuteGalleryMutationAsync(true, capture,
                _ => throw new IOException("simulated delete error"),
                _ => throw new UnauthorizedAccessException("simulated restore error"),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(combined, "compound exception");

        // 16. Mid-deletion cancellation runs rollback with uncancelled token.
        using var postDelete = new CancellationTokenSource();
        failed = false;
        try
        {
            await ExecuteGalleryMutationAsync(true, capture,
                _ =>
                {
                    postDelete.Cancel();
                    return Task.CompletedTask;
                }, restore, postDelete.Token);
        }
        catch (OperationCanceledException) { failed = true; }
        Check(failed && restored == 3, "cancellation rollback");

        // 17-18. Already-cancelled requests cannot touch any state.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        int oldCaptured = captured, oldRestored = restored;
        bool preOn = false, preOff = false;
        try
        {
            await ExecuteGalleryMutationAsync(true, capture, apply, restore, pre.Token);
        }
        catch (OperationCanceledException) { preOn = true; }
        Check(preOn && captured == oldCaptured && applied == oldApplied &&
            restored == oldRestored, "pre-cancel ON");
        try
        {
            await ExecuteGalleryMutationAsync(false, capture, apply, restore, pre.Token);
        }
        catch (OperationCanceledException) { preOff = true; }
        Check(preOff && restored == oldRestored, "pre-cancel OFF");

        // 19. Cancellation following backup prevents a deletion.
        using var cancelledAfterBackup = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteGalleryMutationAsync(true,
                _ =>
                {
                    cancelledAfterBackup.Cancel();
                    return Task.CompletedTask;
                }, apply, restore, cancelledAfterBackup.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && applied == oldApplied, "backup cancellation");

        // 20. Restore both namespace views even if the first restore fails.
        List<int> attempts = new();
        bool twoErrors = false;
        try
        {
            await RestoreGalleryViewsAsync((i, token) =>
            {
                Check(!token.IsCancellationRequested, "view restore cancelled");
                attempts.Add(i);
                if (i == 0)
                    throw new IOException("simulated native restore failure");
                throw new InvalidOperationException("simulated WOW receipt failure");
            });
        }
        catch (AggregateException ex) { twoErrors = ex.InnerExceptions.Count == 2; }
        Check(twoErrors && attempts.SequenceEqual(new[] { 0, 1 }),
            "independent native WOW restore");
    }
}
