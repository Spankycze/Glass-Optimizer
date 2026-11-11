using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // No registry or user-setting writes: only delegates are executed.
    internal static async Task RunStringTweakMutationSmokeAsync()
    {
        static void Check(bool ok, string scenario)
        {
            if (!ok) throw new InvalidOperationException("STRING_TWEAK_SMOKE_FAIL: " + scenario);
        }

        // 1: The real UI catalogue exposes all three unchanged IDs with
        // reversible behavior and without unnecessary UAC on HKCU targets.
        var options = UiTweaks().Take(3).ToArray();
        string[] expected =
        [
            "gt.ui.menudelay",
            "gt.ui.taskbarpreview",
            "gt.ui.cursorblink"
        ];
        Check(options.Length == 3 &&
              options.Select(x => x.Id).SequenceEqual(expected) &&
              options.All(x => x.RevertAsync != null &&
                               x.RevertMode == "Explicit" &&
                               !x.RequiresAdmin &&
                               !x.Recommended), "catalogue wiring");

        int captures = 0, writes = 0, restores = 0;
        Func<CancellationToken, Task> capture = _ =>
        {
            captures++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> write = _ =>
        {
            writes++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task<bool>> restore = token =>
        {
            Check(!token.IsCancellationRequested, "rollback received cancelled token");
            restores++;
            return Task.FromResult(true);
        };

        // 2: ON captures the original, writes and does not restore.
        await ExecuteStringTweakMutationAsync(true, capture, write, restore, CancellationToken.None);
        Check(captures == 1 && writes == 1 && restores == 0, "enable");

        // 3: OFF restores only, without recapturing or applying defaults.
        await ExecuteStringTweakMutationAsync(false, capture, write, restore, CancellationToken.None);
        Check(captures == 1 && writes == 1 && restores == 1, "disable");

        // 4: Failed write attempts restoration and preserves primary error.
        bool writeError = false;
        try
        {
            await ExecuteStringTweakMutationAsync(true, capture,
                _ => throw new IOException("injected registry write error"),
                restore, CancellationToken.None);
        }
        catch (IOException) { writeError = true; }
        Check(writeError && restores == 2, "write failure rollback");

        // 5: Mismatched registry kind/value is a mutation failure.
        bool verificationError = false;
        try
        {
            await ExecuteStringTweakMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected readback mismatch"),
                restore, CancellationToken.None);
        }
        catch (InvalidDataException) { verificationError = true; }
        Check(verificationError && restores == 3, "readback failure rollback");

        // 6: Failure to persist original must prevent any write.
        int oldWrites = writes;
        bool backupError = false;
        try
        {
            await ExecuteStringTweakMutationAsync(true,
                _ => throw new IOException("injected backup persistence failure"),
                write, restore, CancellationToken.None);
        }
        catch (IOException) { backupError = true; }
        Check(backupError && writes == oldWrites && restores == 3, "fail-closed backup");

        // 7: No saved original on OFF must be an error, never silent deletion.
        bool missingOriginal = false;
        try
        {
            await ExecuteStringTweakMutationAsync(false, capture, write,
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (InvalidOperationException) { missingOriginal = true; }
        Check(missingOriginal && writes == oldWrites, "missing original");

        // 8: Both an original write exception and a restore exception survive.
        bool combined = false;
        try
        {
            await ExecuteStringTweakMutationAsync(true, capture,
                _ => throw new InvalidDataException("injected verification error"),
                _ => Task.FromException<bool>(new UnauthorizedAccessException("injected restore error")),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                       ex.InnerExceptions[0] is InvalidDataException &&
                       ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(combined, "compound exceptions");

        // 9: Missing rollback after mutation also returns both error causes.
        bool failedRecovery = false;
        try
        {
            await ExecuteStringTweakMutationAsync(true, capture,
                _ => throw new IOException("injected partial mutation"),
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            failedRecovery = ex.InnerExceptions.Count == 2 &&
                             ex.InnerExceptions[0] is IOException &&
                             ex.InnerExceptions[1] is InvalidOperationException;
        }
        Check(failedRecovery, "missing rollback after mutation");

        // 10: Cancellation after a simulated write must run uncancelled restore.
        using var midWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteStringTweakMutationAsync(true, capture,
                _ =>
                {
                    midWrite.Cancel();
                    return Task.CompletedTask;
                }, restore, midWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && restores == 4, "cancelled write rollback");

        // 11: A request cancelled before ON does nothing.
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        int oldCaptures = captures, oldRestores = restores;
        bool cancelledBeforeStart = false;
        try
        {
            await ExecuteStringTweakMutationAsync(true, capture, write, restore, preCancelled.Token);
        }
        catch (OperationCanceledException) { cancelledBeforeStart = true; }
        Check(cancelledBeforeStart && captures == oldCaptures &&
              writes == oldWrites && restores == oldRestores, "pre-cancel ON");

        // 12: A pre-cancelled OFF must also leave originals untouched.
        bool cancelledDisable = false;
        try
        {
            await ExecuteStringTweakMutationAsync(false, capture, write, restore, preCancelled.Token);
        }
        catch (OperationCanceledException) { cancelledDisable = true; }
        Check(cancelledDisable && restores == oldRestores, "pre-cancel OFF");

        // 13: Cancellation during capture prevents the first registry write.
        using var afterCapture = new CancellationTokenSource();
        bool captureCancelled = false;
        try
        {
            await ExecuteStringTweakMutationAsync(true,
                _ =>
                {
                    afterCapture.Cancel();
                    return Task.CompletedTask;
                }, write, restore, afterCapture.Token);
        }
        catch (OperationCanceledException) { captureCancelled = true; }
        Check(captureCancelled && writes == oldWrites && restores == oldRestores,
              "cancellation during capture");
    }
}
