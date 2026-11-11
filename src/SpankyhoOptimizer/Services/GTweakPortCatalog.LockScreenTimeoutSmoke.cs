using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // These scenarios use the production transaction with only fake callbacks.
    // There are no HKLM writes and no changes to Windows power settings.
    internal static async Task RunLockScreenTimeoutMutationSmokeAsync()
    {
        static void Check(bool condition, string label)
        {
            if (!condition)
                throw new InvalidOperationException("LOCKSCREEN_TIMEOUT_SMOKE_FAIL: " + label);
        }

        // 1: Stable public toggle identity, metadata and original-value revert.
        TweakDefinition tweak = LockScreenTimeoutTweak();
        Check(tweak.Id == LockScreenTimeoutId &&
              tweak.Section == "system" && tweak.Group == "Napájení" &&
              tweak.Risk == TweakRisk.Optional &&
              !tweak.Recommended && !tweak.RestartRequired &&
              tweak.RequiresAdmin && tweak.RevertAsync is not null &&
              tweak.RevertMode == "OriginalRegistryValue" &&
              tweak.RegistryHive == RegistryHive.LocalMachine &&
              tweak.RegistryPath == LockScreenTimeoutKey &&
              tweak.RegistryValueName == "Attributes" &&
              tweak.RegistryEnabledValue == 2, "catalog contract");

        int? original = null;
        int? current = 1;
        bool backedUp = false;
        int captures = 0, writes = 0, restores = 0;
        Func<CancellationToken, Task> capture = token =>
        {
            Check(!token.IsCancellationRequested, "capture cancellation");
            captures++;
            if (!backedUp)
            {
                original = current;
                backedUp = true;
            }
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = token =>
        {
            Check(!token.IsCancellationRequested, "write cancellation");
            writes++;
            current = 2;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task<bool>> restore = token =>
        {
            Check(!token.IsCancellationRequested, "restore cancellation");
            restores++;
            if (!backedUp) return Task.FromResult(false);
            current = original;
            return Task.FromResult(true);
        };

        // 2-3: Attributes 1 -> 2 -> 1; OFF doesn't recapture or set hardcoded 1.
        await ExecuteLockScreenTimeoutMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        Check(current == 2 && original == 1 && captures == 1 &&
              writes == 1 && restores == 0, "expose setting");
        await ExecuteLockScreenTimeoutMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current == 1 && captures == 1 && writes == 1 && restores == 1,
            "restore original 1");

        // 4: An original 0 remains 0, not a synthetic default.
        current = 0; backedUp = false; original = null;
        await ExecuteLockScreenTimeoutMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        await ExecuteLockScreenTimeoutMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current == 0 && original == 0, "preserve original 0");

        // 5: An original already set to 2 stays 2 on OFF.
        current = 2; backedUp = false; original = null;
        await ExecuteLockScreenTimeoutMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        await ExecuteLockScreenTimeoutMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current == 2 && original == 2, "originally exposed remains exposed");

        // 6: An absent Attributes value stays absent on OFF.
        current = null; backedUp = false; original = null;
        await ExecuteLockScreenTimeoutMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        await ExecuteLockScreenTimeoutMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current is null && original is null, "restore originally absent value");

        // 7: Repeated ON doesn't replace the first original.
        current = 1; backedUp = false; original = null;
        await ExecuteLockScreenTimeoutMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        await ExecuteLockScreenTimeoutMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        Check(original == 1 && current == 2, "first original preserved");
        await ExecuteLockScreenTimeoutMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current == 1, "repeat original restoration");

        // 8: OFF without a saved original refuses guessed changes.
        int oldWrites = writes;
        bool threw = false;
        try
        {
            await ExecuteLockScreenTimeoutMutationAsync(false, capture, apply,
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (InvalidOperationException) { threw = true; }
        Check(threw && writes == oldWrites, "no backup fails closed");

        // 9: Capture failure must stop the first HKLM write.
        threw = false;
        try
        {
            await ExecuteLockScreenTimeoutMutationAsync(true,
                _ => throw new IOException("simulated backup disk error"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { threw = true; }
        Check(threw && writes == oldWrites, "capture failure");

        // 10: Partial write fault restores original.
        current = 1; backedUp = false; original = null;
        threw = false;
        try
        {
            await ExecuteLockScreenTimeoutMutationAsync(true, capture,
                _ =>
                {
                    current = 2;
                    throw new IOException("simulated HKLM write fault");
                }, restore, CancellationToken.None);
        }
        catch (IOException) { threw = true; }
        Check(threw && current == 1, "partial write rollback");

        // 11: Wrong DWORD readback restores original.
        current = 1; backedUp = false; original = null;
        threw = false;
        try
        {
            await ExecuteLockScreenTimeoutMutationAsync(true, capture,
                _ =>
                {
                    current = 2;
                    throw new InvalidDataException("simulated registry kind mismatch");
                }, restore, CancellationToken.None);
        }
        catch (InvalidDataException) { threw = true; }
        Check(threw && current == 1, "readback mismatch rollback");

        // 12: Failed mutation + missing rollback are reported together.
        bool compound = false;
        try
        {
            await ExecuteLockScreenTimeoutMutationAsync(true, capture,
                _ => throw new IOException("mutation error"),
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compound = ex.InnerExceptions.Count == 2 &&
                       ex.InnerExceptions[0] is IOException &&
                       ex.InnerExceptions[1] is InvalidOperationException;
        }
        Check(compound, "missing rollback compound error");

        // 13: Failed mutation + thrown rollback preserve both causes.
        compound = false;
        try
        {
            await ExecuteLockScreenTimeoutMutationAsync(true, capture,
                _ => throw new InvalidDataException("verify fault"),
                _ => Task.FromException<bool>(
                    new UnauthorizedAccessException("rollback rejected")),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compound = ex.InnerExceptions.Count == 2 &&
                       ex.InnerExceptions[0] is InvalidDataException &&
                       ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(compound, "thrown rollback compound error");

        // 14: OFF must respect an external edit instead of overwriting it.
        current = 1;
        threw = false;
        try
        {
            await ExecuteLockScreenTimeoutMutationAsync(false, capture, apply,
                _ => throw new InvalidOperationException("another app changed Attributes"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { threw = true; }
        Check(threw && current == 1, "external power policy edit preserved");

        // 15: Cancellation AFTER mutation attempts uncancelled restore.
        current = 1; backedUp = false; original = null;
        using var afterWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteLockScreenTimeoutMutationAsync(true, capture,
                _ =>
                {
                    current = 2;
                    afterWrite.Cancel();
                    return Task.CompletedTask;
                }, restore, afterWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && current == 1, "post-write cancellation rollback");

        // 16-17: Pre-cancelled ON and OFF are no-ops.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        int previousWrites = writes, previousCaptures = captures, previousRestores = restores;
        bool preOn = false, preOff = false;
        try
        {
            await ExecuteLockScreenTimeoutMutationAsync(true, capture, apply, restore, pre.Token);
        }
        catch (OperationCanceledException) { preOn = true; }
        try
        {
            await ExecuteLockScreenTimeoutMutationAsync(false, capture, apply, restore, pre.Token);
        }
        catch (OperationCanceledException) { preOff = true; }
        Check(preOn && preOff && writes == previousWrites &&
              captures == previousCaptures && restores == previousRestores,
              "pre-cancelled commands");

        // 18: Cancellation during capture prevents the first write.
        using var cancelledCapture = new CancellationTokenSource();
        cancelled = false;
        try
        {
            await ExecuteLockScreenTimeoutMutationAsync(true,
                _ =>
                {
                    cancelledCapture.Cancel();
                    return Task.CompletedTask;
                }, apply, restore, cancelledCapture.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && writes == previousWrites, "capture cancellation");
    }
}
