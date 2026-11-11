using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Production transaction and restoration callbacks only. No changes to
    // the real registry, receipt files or running Explorer processes.
    internal static async Task RunOneDriveNavMutationSmokeAsync()
    {
        static void Check(bool ok, string scenario)
        {
            if (!ok)
                throw new InvalidOperationException("ONEDRIVE_NAV_SMOKE_FAIL: " + scenario);
        }

        // 1. Catalogue remains optional/manual-only and explicit reversible.
        var tweak = ExplorerOneDriveNavTweak();
        Check(tweak.Id == OneDriveNavId && tweak.Section == "ui" &&
            !tweak.Recommended && !tweak.RequiresAdmin && tweak.RestartRequired &&
            tweak.RevertMode == "Explicit" && tweak.RevertAsync is not null,
            "catalog metadata");

        // 2. Native + WOW overlay targets and backup IDs must be distinct.
        Check(OneDriveUserPaths.Length == 2 && OneDriveClassesPaths.Length == 2 &&
            OneDriveBackupIds.Length == 2 &&
            OneDriveUserPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 &&
            OneDriveBackupIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 &&
            OneDriveUserPaths.All(x => x.StartsWith(
                @"Software\Classes\", StringComparison.OrdinalIgnoreCase)),
            "two independent HKCU overlays");

        // 3-5. Persisted key existence must roundtrip for both and mixed views.
        var none = DecodeOneDriveNavReceipt(EncodeOneDriveNavReceipt(false, false));
        Check(!none.NativeExisted && !none.WowExisted, "both absent");
        var both = DecodeOneDriveNavReceipt(EncodeOneDriveNavReceipt(true, true));
        Check(both.NativeExisted && both.WowExisted, "both present");
        var mixed = DecodeOneDriveNavReceipt(EncodeOneDriveNavReceipt(true, false));
        Check(mixed.NativeExisted && !mixed.WowExisted, "mixed original");

        // 6. Corrupt or foreign receipts must fail closed.
        bool invalid = false;
        try { _ = DecodeOneDriveNavReceipt("onedrive-nav-v1|other-tweak|1|0"); }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "foreign ownership receipt");

        // 7. Extra values or subkeys disallow removal; only our empty override.
        Check(CanRemoveOneDriveOverride(false, 0, false, false) &&
            CanRemoveOneDriveOverride(false, 1, false, true) &&
            !CanRemoveOneDriveOverride(true, 1, false, true) &&
            !CanRemoveOneDriveOverride(false, 1, false, false) &&
            !CanRemoveOneDriveOverride(false, 2, false, true) &&
            !CanRemoveOneDriveOverride(false, 1, true, true),
            "safe overlay deletion guard");

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
            Check(!ct.IsCancellationRequested, "rollback cancelled");
            restores++;
            return Task.CompletedTask;
        };

        // 8. ON must capture initial originals before applying overrides.
        await ExecuteOneDriveNavMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Check(captures == 1 && writes == 1 && restores == 0, "enable");

        // 9. OFF restores, not synthetic pinned=1 writes or new captures.
        await ExecuteOneDriveNavMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(captures == 1 && writes == 1 && restores == 1, "restore OFF");

        // 10. Partial second-view write requires recovery.
        bool writeError = false;
        try
        {
            await ExecuteOneDriveNavMutationAsync(true, capture,
                _ => throw new IOException("simulated WOW view write fault"),
                restore, CancellationToken.None);
        }
        catch (IOException) { writeError = true; }
        Check(writeError && restores == 2, "partial write rollback");

        // 11. Type/readback verification errors also trigger recovery.
        bool verification = false;
        try
        {
            await ExecuteOneDriveNavMutationAsync(true, capture,
                _ => throw new InvalidDataException("simulated DWORD mismatch"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { verification = true; }
        Check(verification && restores == 3, "readback rollback");

        // 12. Receipt or backup capture failure stops the first registry write.
        int oldWrites = writes;
        bool captureError = false;
        try
        {
            await ExecuteOneDriveNavMutationAsync(true,
                _ => throw new IOException("simulated backup write failure"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { captureError = true; }
        Check(captureError && writes == oldWrites && restores == 3,
            "capture fail closed");

        // 13. OFF without ownership receipt may not replace original keys.
        bool failedOff = false;
        try
        {
            await ExecuteOneDriveNavMutationAsync(false, capture, apply,
                _ => throw new InvalidOperationException("missing receipt"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { failedOff = true; }
        Check(failedOff && writes == oldWrites, "legacy missing receipt");

        // 14. Both original error and failed rollback remain visible.
        bool compound = false;
        try
        {
            await ExecuteOneDriveNavMutationAsync(true, capture,
                _ => throw new IOException("simulated mutation fault"),
                _ => throw new UnauthorizedAccessException("simulated rollback fault"),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compound = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(compound, "combined errors");

        // 15. Cancellation after mutation still restores with uncancelled token.
        using var afterWrite = new CancellationTokenSource();
        bool cancelledAfterWrite = false;
        try
        {
            await ExecuteOneDriveNavMutationAsync(true, capture,
                _ =>
                {
                    afterWrite.Cancel();
                    return Task.CompletedTask;
                }, restore, afterWrite.Token);
        }
        catch (OperationCanceledException) { cancelledAfterWrite = true; }
        Check(cancelledAfterWrite && restores == 4, "cancelled write rollback");

        // 16-17. Already-cancelled ON/OFF do not touch originals or registry.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        int oldCaptures = captures, oldRestores = restores;
        bool preOn = false, preOff = false;
        try
        {
            await ExecuteOneDriveNavMutationAsync(true, capture, apply, restore, pre.Token);
        }
        catch (OperationCanceledException) { preOn = true; }
        Check(preOn && captures == oldCaptures && writes == oldWrites &&
            restores == oldRestores, "pre-cancelled ON");
        try
        {
            await ExecuteOneDriveNavMutationAsync(false, capture, apply, restore, pre.Token);
        }
        catch (OperationCanceledException) { preOff = true; }
        Check(preOff && restores == oldRestores, "pre-cancelled OFF");

        // 18. Cancellation during backup capture prevents the first write.
        using var midCapture = new CancellationTokenSource();
        bool captureCancelled = false;
        try
        {
            await ExecuteOneDriveNavMutationAsync(true,
                _ =>
                {
                    midCapture.Cancel();
                    return Task.CompletedTask;
                }, apply, restore, midCapture.Token);
        }
        catch (OperationCanceledException) { captureCancelled = true; }
        Check(captureCancelled && writes == oldWrites, "cancelled capture");

        // 19. Restore attempts both independent HKCU views even if the first
        // throws and the second is missing, reporting both failures.
        var indices = new List<int>();
        bool bothFailed = false;
        try
        {
            await RestoreOneDriveNavEntriesAsync((i, token) =>
            {
                Check(!token.IsCancellationRequested, "cancelled view restore");
                indices.Add(i);
                if (i == 0)
                    throw new IOException("simulated native-view restore fault");
                throw new InvalidOperationException("simulated WOW backup missing");
            });
        }
        catch (AggregateException ex) { bothFailed = ex.InnerExceptions.Count == 2; }
        Check(bothFailed && indices.SequenceEqual(new[] { 0, 1 }),
            "independent view restoration");
    }
}
