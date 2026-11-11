using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Exact production transaction; all dependencies are simulated.
    internal static async Task RunDesktopLabelShadowMutationSmokeAsync()
    {
        static void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("DESKTOP_SHADOW_TEST_FAILED: " + label);
        }

        int captured = 0;
        int restored = 0;
        int writes = 0;
        bool? lastValue = null;
        Func<CancellationToken, Task> capture = _ =>
        {
            captured++;
            return Task.CompletedTask;
        };
        Func<bool, CancellationToken, Task> write = (state, _) =>
        {
            writes++;
            lastValue = state;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task<bool>> restore = ct =>
        {
            Check(!ct.IsCancellationRequested, "cancelled rollback token");
            restored++;
            return Task.FromResult(true);
        };

        // 1. Turn on explicitly.
        await ExecuteDesktopLabelShadowMutationAsync(true, capture, write, restore, CancellationToken.None);
        Check(captured == 1 && writes == 1 && lastValue == true && restored == 0, "enable");

        // 2. Turn off explicitly, rather than restoring an originally-enabled setting.
        await ExecuteDesktopLabelShadowMutationAsync(false, capture, write, restore, CancellationToken.None);
        Check(captured == 2 && writes == 2 && lastValue == false && restored == 0, "disable");

        // 3. Partial enable write restores the saved original.
        bool onFailed = false;
        try
        {
            await ExecuteDesktopLabelShadowMutationAsync(true, capture,
                (_, _) => throw new IOException("injected on-write fault"),
                restore, CancellationToken.None);
        }
        catch (IOException) { onFailed = true; }
        Check(onFailed && restored == 1, "enable write rollback");

        // 4. Partial disable write restores the saved original.
        bool offFailed = false;
        try
        {
            await ExecuteDesktopLabelShadowMutationAsync(false, capture,
                (_, _) => throw new IOException("injected off-write fault"),
                restore, CancellationToken.None);
        }
        catch (IOException) { offFailed = true; }
        Check(offFailed && restored == 2, "disable write rollback");

        // 5. Failed post-write readback restores the original.
        bool mismatch = false;
        try
        {
            await ExecuteDesktopLabelShadowMutationAsync(true, capture,
                (_, _) => throw new InvalidDataException("injected readback mismatch"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { mismatch = true; }
        Check(mismatch && restored == 3, "failed readback");

        // 6. Cancellation during a write still executes uncancelled rollback.
        using var cts = new CancellationTokenSource();
        bool wasCancelled = false;
        try
        {
            await ExecuteDesktopLabelShadowMutationAsync(true, capture,
                (_, _) =>
                {
                    cts.Cancel();
                    return Task.CompletedTask;
                }, restore, cts.Token);
        }
        catch (OperationCanceledException) { wasCancelled = true; }
        Check(wasCancelled && restored == 4, "cancellation rollback");

        // 7. Failed backup cannot proceed to any registry write.
        int priorWrites = writes;
        bool backupFailed = false;
        try
        {
            await ExecuteDesktopLabelShadowMutationAsync(true,
                _ => throw new IOException("injected backup failure"),
                write, restore, CancellationToken.None);
        }
        catch (IOException) { backupFailed = true; }
        Check(backupFailed && writes == priorWrites && restored == 4, "backup fail closed");

        // 8. Missing original backup must not be reported as restored.
        bool missingBackup = false;
        try
        {
            await ExecuteDesktopLabelShadowMutationAsync(true, capture,
                (_, _) => throw new IOException("injected write fault"),
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            missingBackup = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is InvalidOperationException;
        }
        Check(missingBackup, "missing rollback backup");

        // 9. Preserve both the original error and a thrown rollback exception.
        bool compoundFailure = false;
        try
        {
            await ExecuteDesktopLabelShadowMutationAsync(false, capture,
                (_, _) => throw new InvalidDataException("injected verification failure"),
                _ => Task.FromException<bool>(new IOException("injected restore failure")),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compoundFailure = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is InvalidDataException &&
                ex.InnerExceptions[1] is IOException;
        }
        Check(compoundFailure, "combined errors");

        // 10. Request cancelled before start never captures/writes/restores.
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        int priorCaptures = captured, priorRestores = restored;
        bool preCancelThrown = false;
        try
        {
            await ExecuteDesktopLabelShadowMutationAsync(true, capture, write,
                restore, preCancelled.Token);
        }
        catch (OperationCanceledException) { preCancelThrown = true; }
        Check(preCancelThrown && captured == priorCaptures && restored == priorRestores
              && writes == priorWrites, "pre-cancel leaves state intact");
    }
}
