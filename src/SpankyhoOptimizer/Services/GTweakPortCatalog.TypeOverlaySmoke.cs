using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Pure fault-injection test; no real registry reads, writes, or backup mutations.
    internal static async Task RunTypeOverlayMutationSmokeAsync()
    {
        static void Verify(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("TYPE_OVERLAY_SMOKE_FAIL: " + label);
        }

        // The tested production transaction must also be wired into the real catalogue.
        var tweak = SystemInterfaceUiTweaks().Single(x => x.Id == TypeOverlayId);
        Verify(tweak.RegistryHive == Microsoft.Win32.RegistryHive.CurrentUser &&
               tweak.RegistryPath == ExplorerAdvanced &&
               tweak.RegistryValueName == TypeOverlayValueName &&
               tweak.RegistryEnabledValue == 0 &&
               !tweak.RequiresAdmin &&
               tweak.RevertAsync != null &&
               tweak.RevertMode == "OriginalRegistryValue" &&
               tweak.RestartRequired, "catalogue wiring");

        int backups = 0, writes = 0, restores = 0;
        bool? lastValue = null;
        Func<CancellationToken, Task> capture = _ =>
        {
            backups++;
            return Task.CompletedTask;
        };
        Func<bool, CancellationToken, Task> apply = (disable, _) =>
        {
            lastValue = disable;
            writes++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task<bool>> restore = ct =>
        {
            Verify(!ct.IsCancellationRequested, "cancelled restore");
            restores++;
            return Task.FromResult(true);
        };

        // 1. ON sets disabled overlay state.
        await ExecuteTypeOverlayMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Verify(backups == 1 && writes == 1 && lastValue == true && restores == 0, "ON");

        // 2. OFF sets enabled overlay state, not the captured original.
        await ExecuteTypeOverlayMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Verify(backups == 2 && writes == 2 && lastValue == false && restores == 0, "OFF");

        // 3. A fault before a successful backup is fail-closed.
        int priorWrites = writes, priorRestores = restores;
        bool failedBackup = false;
        try
        {
            await ExecuteTypeOverlayMutationAsync(true,
                _ => throw new IOException("injected backup failure"), apply, restore, CancellationToken.None);
        }
        catch (IOException) { failedBackup = true; }
        Verify(failedBackup && writes == priorWrites && restores == priorRestores, "backup failure");

        // 4. Failed ON write restores original state.
        bool failedOn = false;
        try
        {
            await ExecuteTypeOverlayMutationAsync(true, capture,
                (_, _) => throw new IOException("injected ON write fault"), restore, CancellationToken.None);
        }
        catch (IOException) { failedOn = true; }
        Verify(failedOn && restores == 1, "ON recovery");

        // 5. Failed OFF write restores original state.
        bool failedOff = false;
        try
        {
            await ExecuteTypeOverlayMutationAsync(false, capture,
                (_, _) => throw new IOException("injected OFF write fault"), restore, CancellationToken.None);
        }
        catch (IOException) { failedOff = true; }
        Verify(failedOff && restores == 2, "OFF recovery");

        // 6. Failed verification is treated as a partial write.
        bool badReadback = false;
        try
        {
            await ExecuteTypeOverlayMutationAsync(true, capture,
                (_, _) => throw new InvalidDataException("injected readback mismatch"), restore,
                CancellationToken.None);
        }
        catch (InvalidDataException) { badReadback = true; }
        Verify(badReadback && restores == 3, "readback recovery");

        // 7. Cancellation after a write must not cancel recovery.
        using var cts = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteTypeOverlayMutationAsync(true, capture,
                (_, _) =>
                {
                    cts.Cancel();
                    return Task.CompletedTask;
                }, restore, cts.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Verify(cancelled && restores == 4, "cancel recovery");

        // 8. A missing original backup must not be reported as success.
        bool missingOriginal = false;
        try
        {
            await ExecuteTypeOverlayMutationAsync(true, capture,
                (_, _) => throw new IOException("injected write fault"),
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            missingOriginal = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is InvalidOperationException;
        }
        Verify(missingOriginal, "missing backup");

        // 9. A thrown recovery error must preserve both failure causes.
        bool compoundFailure = false;
        try
        {
            await ExecuteTypeOverlayMutationAsync(false, capture,
                (_, _) => throw new InvalidDataException("injected verification fault"),
                _ => Task.FromException<bool>(new IOException("injected restore fault")),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compoundFailure = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is InvalidDataException &&
                ex.InnerExceptions[1] is IOException;
        }
        Verify(compoundFailure, "compound failure");

        // 10. A pre-cancelled request cannot capture, write, or restore.
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        int savedBackups = backups, savedWrites = writes, savedRestores = restores;
        bool preCancelCaught = false;
        try
        {
            await ExecuteTypeOverlayMutationAsync(true, capture, apply, restore, preCancelled.Token);
        }
        catch (OperationCanceledException) { preCancelCaught = true; }
        Verify(preCancelCaught && backups == savedBackups &&
               writes == savedWrites && restores == savedRestores, "pre-cancel");
    }
}
