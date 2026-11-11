using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Exactly the production transaction; injected delegates never touch Windows.
    internal static async Task RunRotatingLockScreenMutationSmokeAsync()
    {
        static void Ensure(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException(
                "ROTATING_LOCK_SCREEN_SMOKE: " + scenario);
        }

        var def = RotatingLockScreenOff();
        Ensure(def.Id == RotatingLockScreenId && def.Section == "privacy" &&
            def.SetAsync != null && def.RevertAsync != null && !def.Recommended,
            "catalog metadata and explicit restore");
        Ensure(RotatingLockScreenNames.Distinct(StringComparer.Ordinal).Count() == 3,
            "distinct registry targets");

        int captures = 0, writes = 0;
        var recovered = new List<int>();
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
        Func<int, CancellationToken, Task<bool>> restore = (i, token) =>
        {
            Ensure(!token.IsCancellationRequested, "recovery token cancelled");
            recovered.Add(i);
            return Task.FromResult(true);
        };

        // 1. Enabling captures once and writes once.
        await ExecuteRotatingLockScreenMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Ensure(captures == 1 && writes == 1 && recovered.Count == 0, "enable");

        // 2. Disabling restores all three, without capturing/writing again.
        await ExecuteRotatingLockScreenMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Ensure(captures == 1 && writes == 1 && recovered.SequenceEqual(new[] { 0, 1, 2 }),
            "disable returns original values");
        recovered.Clear();

        // 3. An error after partially changing Windows restores all targets.
        bool writeFault = false;
        try
        {
            await ExecuteRotatingLockScreenMutationAsync(true, capture,
                _ => throw new IOException("injected second registry write failure"),
                restore, CancellationToken.None);
        }
        catch (IOException) { writeFault = true; }
        Ensure(writeFault && recovered.SequenceEqual(new[] { 0, 1, 2 }), "partial write fault");
        recovered.Clear();

        // 4. A wrong readback must also restore all three originals.
        bool mismatch = false;
        try
        {
            await ExecuteRotatingLockScreenMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected registry readback mismatch"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { mismatch = true; }
        Ensure(mismatch && recovered.SequenceEqual(new[] { 0, 1, 2 }), "readback mismatch");
        recovered.Clear();

        // 5. Failed backup must prevent any Windows mutation or rollback.
        int previousWrites = writes, previousRestores = recovered.Count;
        bool backupFault = false;
        try
        {
            await ExecuteRotatingLockScreenMutationAsync(true,
                _ => throw new IOException("injected backup persistence failure"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { backupFault = true; }
        Ensure(backupFault && writes == previousWrites && recovered.Count == previousRestores,
            "backup persistence fail closed");

        // 6. A failed restore must not prevent restoring the remaining keys.
        bool restoreFault = false;
        try
        {
            await ExecuteRotatingLockScreenMutationAsync(false, capture, apply, (i, _) =>
            {
                recovered.Add(i);
                if (i == 0) throw new IOException("injected first restoration error");
                return Task.FromResult(i != 1);
            }, CancellationToken.None);
        }
        catch (AggregateException ex) { restoreFault = ex.InnerExceptions.Count == 2; }
        Ensure(restoreFault && recovered.SequenceEqual(new[] { 0, 1, 2 }),
            "independent restore failure");
        recovered.Clear();

        // 7. A cancelled operation after a mutation still runs full rollback.
        using var afterMutation = new CancellationTokenSource();
        bool cancellation = false;
        try
        {
            await ExecuteRotatingLockScreenMutationAsync(true, capture, _ =>
            {
                afterMutation.Cancel();
                return Task.CompletedTask;
            }, restore, afterMutation.Token);
        }
        catch (OperationCanceledException) { cancellation = true; }
        Ensure(cancellation && recovered.SequenceEqual(new[] { 0, 1, 2 }), "cancellation rollback");
        recovered.Clear();

        // 8. Keep both original mutation failure and recovery failure.
        bool combinedFault = false;
        try
        {
            await ExecuteRotatingLockScreenMutationAsync(true, capture,
                _ => throw new IOException("injected write error"),
                (i, _) => Task.FromResult(i != 2), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combinedFault = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is AggregateException;
        }
        Ensure(combinedFault, "combined mutation and recovery failure");

        // 9. A pre-cancelled enable does not save or change anything.
        using var beforeStart = new CancellationTokenSource();
        beforeStart.Cancel();
        int originalCaptures = captures, originalWrites = writes;
        bool preCancelled = false;
        try
        {
            await ExecuteRotatingLockScreenMutationAsync(true, capture, apply, restore,
                beforeStart.Token);
        }
        catch (OperationCanceledException) { preCancelled = true; }
        Ensure(preCancelled && captures == originalCaptures &&
            writes == originalWrites && recovered.Count == 0, "pre-cancelled enable");

        // 10. Pre-cancelled disable must not touch stored originals.
        preCancelled = false;
        try
        {
            await ExecuteRotatingLockScreenMutationAsync(false, capture, apply, restore,
                beforeStart.Token);
        }
        catch (OperationCanceledException) { preCancelled = true; }
        Ensure(preCancelled && recovered.Count == 0, "pre-cancelled disable");
    }
}
