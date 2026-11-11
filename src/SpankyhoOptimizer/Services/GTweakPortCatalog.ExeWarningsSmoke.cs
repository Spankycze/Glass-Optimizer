using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // All production delegates are replaced with in-memory actions.
    // No Windows registry, security setting or original-state receipt is touched.
    internal static async Task RunExeWarningsMutationSmokeAsync()
    {
        static void Check(bool ok, string scenario)
        {
            if (!ok) throw new InvalidOperationException("EXE_WARNINGS_SMOKE_FAIL: " + scenario);
        }

        // 1. Security-reducing feature must stay advanced, optional and explicit.
        var feature = ExeLaunchWarningsOff();
        Check(feature.Id == ExeWarningsId && feature.Section == "system" &&
              feature.Risk == SpankyhoOptimizer.Models.TweakRisk.Advanced &&
              !feature.Recommended && feature.RequiresAdmin &&
              feature.RevertAsync is not null && feature.RevertMode == "Explicit" &&
              !string.IsNullOrWhiteSpace(feature.Warning),
              "catalog security metadata");

        // 2. Both original registry backup IDs are independent and stable.
        Check(ExeWarningsBackupIds.Length == 2 &&
              ExeWarningsBackupIds[0] == ExeWarningsId + ".lm" &&
              ExeWarningsBackupIds[1] == ExeWarningsId + ".cu",
              "original backup IDs");

        // 3-6. Durable key-presence receipts must distinguish all baselines.
        var both = DecodeExeWarningsReceipt(EncodeExeWarningsReceipt(true, true));
        Check(both.LmExisted && both.CuExisted, "both existed");
        var none = DecodeExeWarningsReceipt(EncodeExeWarningsReceipt(false, false));
        Check(!none.LmExisted && !none.CuExisted, "both absent");
        var lm = DecodeExeWarningsReceipt(EncodeExeWarningsReceipt(true, false));
        Check(lm.LmExisted && !lm.CuExisted, "only LM existed");
        var cu = DecodeExeWarningsReceipt(EncodeExeWarningsReceipt(false, true));
        Check(!cu.LmExisted && cu.CuExisted, "only CU existed");

        // 7-8. Malformed/foreign receipts must not be silently accepted.
        bool invalidReceipt = false;
        try { _ = DecodeExeWarningsReceipt("exe-launch-warnings-v1|wrong|1|0"); }
        catch (InvalidDataException) { invalidReceipt = true; }
        Check(invalidReceipt, "foreign receipt");
        invalidReceipt = false;
        try { _ = DecodeExeWarningsReceipt("exe-launch-warnings-v1|gt.sys.exelaunchwarnings|1|2"); }
        catch (InvalidDataException) { invalidReceipt = true; }
        Check(invalidReceipt, "malformed receipt");

        // 9. Both DWORD targets must agree for enabled state detection.
        Check(IsExeWarningPolicyActive(true, true) &&
              !IsExeWarningPolicyActive(true, false) &&
              !IsExeWarningPolicyActive(false, true) &&
              !IsExeWarningPolicyActive(false, false), "two target detection");

        // 10. Only app-owned key with exactly the expected DWORD can be
        // removed. Even an empty new key may be owned by another program.
        Check(CanDeleteExeWarningsCreatedKey(false, false, 0, false, false) &&
              CanDeleteExeWarningsCreatedKey(false, true, 1, false, true) &&
              !CanDeleteExeWarningsCreatedKey(true, true, 1, false, true) &&
              !CanDeleteExeWarningsCreatedKey(false, true, 0, false, false) &&
              !CanDeleteExeWarningsCreatedKey(false, true, 1, false, false) &&
              !CanDeleteExeWarningsCreatedKey(false, true, 2, false, true) &&
              !CanDeleteExeWarningsCreatedKey(false, true, 1, true, true),
              "non-destructive key ownership");

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
        Func<CancellationToken, Task> restore = token =>
        {
            Check(!token.IsCancellationRequested, "rollback cancelled");
            restores++;
            return Task.CompletedTask;
        };

        // 11. ON persists originals before any write.
        await ExecuteExeWarningsMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Check(captures == 1 && writes == 1 && restores == 0, "enable");

        // 12. OFF restores originals without recapturing or writing defaults.
        await ExecuteExeWarningsMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(captures == 1 && writes == 1 && restores == 1, "disable");

        // 13. Partial second HKCU mutation triggers full rollback.
        bool failed = false;
        try
        {
            await ExecuteExeWarningsMutationAsync(true, capture,
                _ => throw new IOException("injected second-value write error"),
                restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && restores == 2, "partial write");

        // 14. Wrong kind/value readback triggers the same recovery.
        failed = false;
        try
        {
            await ExecuteExeWarningsMutationAsync(true, capture,
                _ => throw new InvalidDataException("wrong HKCU DWORD readback"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { failed = true; }
        Check(failed && restores == 3, "verification failure");

        // 15. Backup/receipt disk errors prevent the first registry write.
        int priorWrites = writes;
        failed = false;
        try
        {
            await ExecuteExeWarningsMutationAsync(true,
                _ => throw new IOException("original backup failed"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && writes == priorWrites && restores == 3, "backup fail closed");

        // 16. OFF with missing original refuses fallback deletion.
        failed = false;
        try
        {
            await ExecuteExeWarningsMutationAsync(false, capture, apply,
                _ => throw new InvalidOperationException("original receipt missing"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && writes == priorWrites, "missing original");

        // 17. Both mutation and restore error causes remain visible.
        bool compound = false;
        try
        {
            await ExecuteExeWarningsMutationAsync(true, capture,
                _ => throw new IOException("apply failed"),
                _ => throw new UnauthorizedAccessException("rollback failed"),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compound = ex.InnerExceptions.Count == 2 &&
                       ex.InnerExceptions[0] is IOException &&
                       ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(compound, "compound failures");

        // 18. Cancel after simulated first write; rollback uses uncancelled token.
        using var afterWrite = new CancellationTokenSource();
        failed = false;
        try
        {
            await ExecuteExeWarningsMutationAsync(true, capture,
                _ =>
                {
                    afterWrite.Cancel();
                    return Task.CompletedTask;
                }, restore, afterWrite.Token);
        }
        catch (OperationCanceledException) { failed = true; }
        Check(failed && restores == 4, "cancelled write recovery");

        // 19-20. Already cancelled ON/OFF are no-ops.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        int oldCaptures = captures, oldRestores = restores;
        bool preOn = false, preOff = false;
        try { await ExecuteExeWarningsMutationAsync(true, capture, apply, restore, pre.Token); }
        catch (OperationCanceledException) { preOn = true; }
        Check(preOn && writes == priorWrites && captures == oldCaptures &&
              restores == oldRestores, "cancelled ON");
        try { await ExecuteExeWarningsMutationAsync(false, capture, apply, restore, pre.Token); }
        catch (OperationCanceledException) { preOff = true; }
        Check(preOff && restores == oldRestores, "cancelled OFF");

        // 21. Two original restore targets are attempted independently.
        List<int> attempted = new();
        bool bothErrors = false;
        try
        {
            await RestoreExeWarningTargetsAsync((index, token) =>
            {
                Check(!token.IsCancellationRequested, "original restore cancelled");
                attempted.Add(index);
                if (index == 0) throw new IOException("LM restore failure");
                throw new InvalidOperationException("CU backup missing");
            });
        }
        catch (AggregateException ex) { bothErrors = ex.InnerExceptions.Count == 2; }
        Check(bothErrors && attempted.SequenceEqual(new[] { 0, 1 }),
            "independent HKLM and HKCU restore");

        // 22. Cancellation after backup but before the first write is safe.
        using var midCapture = new CancellationTokenSource();
        failed = false;
        try
        {
            await ExecuteExeWarningsMutationAsync(true,
                _ =>
                {
                    midCapture.Cancel();
                    return Task.CompletedTask;
                }, apply, restore, midCapture.Token);
        }
        catch (OperationCanceledException) { failed = true; }
        Check(failed && writes == priorWrites, "cancel after capture");
    }
}
