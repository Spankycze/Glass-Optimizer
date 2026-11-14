using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed partial class TweakEngine
{
    private static async Task<bool> RollbackBatchAsync(IEnumerable<RollbackIntent> items,
        RegistryAtomicSnapshot atomicSnapshot, List<string> errors)
    {
        bool restored = true;
        foreach (var item in items.Reverse())
        {
            try
            {
                item.Tweak.Status = "Vracím změnu…";
                // The atomic snapshot only restores a value if it still
                // matches this batch's exact native write. A registry drift
                // throws and MUST NOT fall through to an unconditional worker
                // or module rollback that would overwrite the foreign value.
                bool restoredRaw = atomicSnapshot.TryRestore(item.Tweak, item.Expected);
                bool actual;
                if (!restoredRaw && ShouldUseElevatedWorker(item.Tweak))
                {
                    var workerResult = await WorkerClientService.RunAsync<TweakWorkerResult>(
                        "tweak-set",
                        WorkerPayloads.Encode(new TweakWorkerRequest(item.Tweak.Id, item.Previous)),
                        TimeSpan.FromMinutes(10),
                        CancellationToken.None).ConfigureAwait(false);
                    actual = workerResult.Actual;
                }
                else
                {
                    var module = OptimizerModuleRuntime.From(item.Tweak);
                    if (!restoredRaw)
                        await module.RollbackAsync(item.Previous, CancellationToken.None).ConfigureAwait(false);
                    actual = await module.VerifyAsync(CancellationToken.None).ConfigureAwait(false);
                }
                item.Tweak.Current = actual;
                // A user may have restaged this row during the async native
                // write or rollback. Reconcile Current without erasing Pending.
                if (item.Tweak.Pending == item.Expected)
                    item.Tweak.Pending = actual;
                item.Tweak.IsLoaded = true;
                item.Tweak.LastVerifiedUtc = DateTime.UtcNow;
                item.Tweak.Status = actual == item.Previous ? "Vráceno po chybě" : "Rollback neověřen";
                item.Tweak.ExecutionState = actual == item.Previous
                    ? (actual ? TweakExecutionState.Applied : TweakExecutionState.NotApplied)
                    : TweakExecutionState.Failed;
                if (actual != item.Previous)
                {
                    restored = false;
                    string mismatch = item.Tweak.Name + ": rollback neobnovil předchozí stav.";
                    item.Tweak.LastError = mismatch;
                    errors.Add(mismatch);
                }
                // Journal I/O is not the native restoration. If it fails
                // after successful read-back, preserve the actual rollback
                // outcome and report audit loss separately.
                try
                {
                    await OperationJournalService.WriteAsync(
                        ErrorService.NewCorrelationId(), "rollback",
                        item.Previous ? "enable" : "disable", item.Tweak.Id,
                        actual == item.Previous ? "Verified" : "Mismatch",
                        item.Tweak.Status, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception auditError)
                {
                    string auditMessage = item.Tweak.Name +
                        ": stav rollbacku ověřen, ale auditní záznam se nepodařilo uložit.";
                    errors.Add(auditMessage);
                    item.Tweak.LastError = auditMessage;
                    LogService.Error("rollback", "Rollback audit " + item.Tweak.Id, auditError);
                }
            }
            catch (Exception ex)
            {
                restored = false;
                string failure = item.Tweak.Name + ": rollback selhal — " + ex.Message;
                errors.Add(failure);
                item.Tweak.LastError = failure;
                item.Tweak.ExecutionState = IsElevationException(ex)
                    ? TweakExecutionState.RequiresElevation
                    : TweakExecutionState.Failed;
                item.Tweak.Status = "Rollback selhal • ověřuji skutečný stav";
                LogService.Error("rollback", "Rollback " + item.Tweak.Id, ex);
                await TrySynchronizeActualStateAsync(
                    item.Tweak, "rollback-failure", item.Expected).ConfigureAwait(false);
            }
        }
        return restored;
    }

    private static async Task<(bool Verified, bool Actual)> VerifyWithRetryAsync(IOptimizerModule module, bool initialActual, bool expected, CancellationToken token)
    {
        bool actual = initialActual;
        // Most registry/API changes settle instantly. The final retry only runs on the
        // failure path and avoids false negatives from delayed Windows state propagation.
        int[] delays = { 0, 220, 650, 1450 };
        foreach (int delay in delays)
        {
            if (delay > 0) await Task.Delay(delay, token);
            actual = await module.VerifyAsync(token);
            if (actual == expected) return (true, actual);
        }
        return (false, actual);
    }

    private static async Task TrySynchronizeRestoreStateAsync(
        TweakDefinition tweak, bool previousSelection, string reason)
    {
        // Cancellation can arrive after a privileged worker performed the
        // native write. Use a fresh, bounded read-only verification. Never
        // turn a failed readback into a claim that original state was restored.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            bool actual = await OptimizerModuleRuntime.From(tweak)
                .VerifyAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            bool userRestaged = tweak.Pending != previousSelection;
            tweak.Current = actual;
            if (!userRestaged) tweak.Pending = actual;
            tweak.IsLoaded = true;
            tweak.LastVerifiedUtc = DateTime.UtcNow;
            // A successful state read does not erase the original restore
            // failure: the caller still needs the native-write error.
            if (reason == "restore-original-cancelled")
                tweak.Status = "Zrušeno • skutečný stav znovu ověřen";
            LogService.Info("tweaks",
                $"RESTORE_STATE_RESYNC_OK id={tweak.Id} reason={reason} actual={actual} preservedPending={userRestaged}");
        }
        catch (Exception ex)
        {
            InvalidateDetection(tweak, ex);
            LogService.Error("tweaks",
                $"RESTORE_STATE_RESYNC_FAIL id={tweak.Id} reason={reason}", ex);
        }
    }

    private static async Task RefreshActualAfterFailureAsync(TweakDefinition tweak, bool expected, CancellationToken token)
    {
        try
        {
            bool actual = await OptimizerModuleRuntime.From(tweak).VerifyAsync(token);
            tweak.Current = actual;
            // Keep a newer user-staged choice intact. Rollback will only
            // synchronize Pending if it still matches this apply's intent.
            tweak.IsLoaded = true;
        }
        catch (Exception ex)
        {
            tweak.LastError = ex.Message;
            LogService.Error("tweaks", "Refresh actual after failure " + tweak.Id, ex);
        }
    }

    private static async Task TrySynchronizeActualStateAsync(
        TweakDefinition tweak, string reason, bool? expectedSelection = null)
    {
        // A failed rollback must not stall the entire batch indefinitely
        // while reading a hung Windows API or privileged module. Verify only,
        // with an independent budget after the caller's token was cancelled.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            bool actual = await OptimizerModuleRuntime.From(tweak)
                .VerifyAsync(budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
            tweak.Current = actual;
            if (!expectedSelection.HasValue || tweak.Pending == expectedSelection.Value)
                tweak.Pending = actual;
            tweak.IsLoaded = true;
            tweak.LastVerifiedUtc = DateTime.UtcNow;
            // Read-back is not proof the intended rollback succeeded: the
            // native mutation already failed or was blocked for safety.
            tweak.Status = "Rollback selhal • skutečný stav ověřen";
            LogService.Info("tweaks", $"STATE_RESYNC_OK id={tweak.Id} reason={reason} actual={actual}");
        }
        catch (Exception verifyEx)
        {
            string? rollbackError = tweak.LastError;
            InvalidateDetection(tweak, verifyEx);
            if (!string.IsNullOrEmpty(rollbackError))
                tweak.LastError = rollbackError + "; ověření skutečného stavu selhalo: " + verifyEx.Message;
            tweak.Status = "Rollback selhal • skutečný stav neověřen";
            LogService.Error("tweaks", $"STATE_RESYNC_FAIL id={tweak.Id} reason={reason}", verifyEx);
        }
    }

    private static bool ShouldUseElevatedWorker(TweakDefinition tweak) =>
        tweak.RequiresAdmin && !WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator();
}
