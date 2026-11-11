using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public static partial class TweakFactory
{
    // No real registry opens/writes, no Windows processes stopped or restarted.
    internal static async Task RunDwordMutationSmokeAsync()
    {
        static void Check(bool ok, string scenario)
        {
            if (!ok) throw new InvalidOperationException(
                "GENERIC_DWORD_SMOKE_FAIL: " + scenario);
        }

        // 1. Both old public API shapes retain their original metadata.
        TweakDefinition policy = Dword(
            "smoke.policy", "privacy", "Telemetry", "Policy", "Test",
            RegistryHive.LocalMachine, @"SOFTWARE\Policies\Smoke", "On", 0, null,
            true, true, TweakRisk.Safe, true, "Nízký", null, 19041, "Any", true);
        Check(policy.Id == "smoke.policy" && policy.Recommended &&
              policy.RestartRequired && policy.RequiresAdmin &&
              policy.RegistryHive == RegistryHive.LocalMachine &&
              policy.RegistryValueName == "On" &&
              policy.RegistryEnabledValue == 0 &&
              policy.RevertMode == "OriginalRegistryValue" &&
              policy.SetAsync is not null && policy.RevertAsync is not null,
              "legacy overload metadata");

        // 2. The old explicitly-disabled-value signature is still present.
        TweakDefinition appearance = Dword(
            "smoke.appearance", "ui", "Vzhled", "Theme", "Test",
            RegistryHive.CurrentUser, @"Software\Smoke", "Theme", 1, 0,
            false, false, TweakRisk.Optional, false,
            "Nízký", null, 19041, "Any", false);
        Check(appearance.Id == "smoke.appearance" && !appearance.RequiresAdmin &&
              appearance.RegistryEnabledValue == 1 && !appearance.Recommended &&
              appearance.RegistryPath == @"Software\Smoke",
              "explicit-disabled-value overload metadata");

        int? current = 1, original = null;
        bool hasOriginal = false;
        int captureCount = 0, writeCount = 0, restoreCount = 0;
        Func<CancellationToken, Task> capture = ct =>
        {
            Check(!ct.IsCancellationRequested, "capture token");
            captureCount++;
            if (!hasOriginal)
            {
                original = current;
                hasOriginal = true;
            }
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = ct =>
        {
            Check(!ct.IsCancellationRequested, "apply token");
            writeCount++;
            current = 0;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task<bool>> restore = ct =>
        {
            Check(!ct.IsCancellationRequested, "restore token");
            restoreCount++;
            if (!hasOriginal) return Task.FromResult(false);
            current = original;
            return Task.FromResult(true);
        };

        // 3. Ordinary DWORD 1 -> 0 -> 1.
        await ExecuteDwordMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Check(current == 0 && original == 1 && captureCount == 1 &&
              writeCount == 1 && restoreCount == 0, "enable");
        await ExecuteDwordMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(current == 1 && original == 1 && captureCount == 1 &&
              writeCount == 1 && restoreCount == 1, "restore one");

        // 4. ORIGINAL ALREADY ENABLED: restoration MUST NOT delete DWORD 0.
        current = 0; original = null; hasOriginal = false;
        await ExecuteDwordMutationAsync(true, capture, apply, restore, CancellationToken.None);
        await ExecuteDwordMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(current == 0 && original == 0, "originally enabled must remain 0");

        // 5. Original absent DWORD restored as absent, not an invented default.
        current = null; original = null; hasOriginal = false;
        await ExecuteDwordMutationAsync(true, capture, apply, restore, CancellationToken.None);
        await ExecuteDwordMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(current is null && original is null, "original absent restored");

        // 6. Custom original 7, not disabledValue fallback 1/0.
        current = 7; original = null; hasOriginal = false;
        await ExecuteDwordMutationAsync(true, capture, apply, restore, CancellationToken.None);
        await ExecuteDwordMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(current == 7 && original == 7, "custom original restored");

        // 7. Repeated enable cannot replace initial original in the store.
        current = 7; original = null; hasOriginal = false;
        await ExecuteDwordMutationAsync(true, capture, apply, restore, CancellationToken.None);
        await ExecuteDwordMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Check(current == 0 && original == 7, "repeat ON first baseline");
        await ExecuteDwordMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(current == 7, "repeat ON then OFF");

        // 8. OFF never calls capture, regardless of saved state.
        int priorCapture = captureCount;
        await ExecuteDwordMutationAsync(false,
            _ => throw new IOException("OFF attempted to overwrite the backup"),
            apply, restore, CancellationToken.None);
        Check(captureCount == priorCapture, "OFF cannot recapture original");

        // 9. OFF missing backup refuses to apply guessed disabledValue.
        int beforeWrites = writeCount;
        bool failed = false;
        try
        {
            await ExecuteDwordMutationAsync(false, capture, apply,
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && writeCount == beforeWrites, "missing original fails closed");

        // 10. A failed capture prevents the first registry write.
        failed = false;
        try
        {
            await ExecuteDwordMutationAsync(true,
                _ => throw new IOException("durable backup unavailable"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && writeCount == beforeWrites, "failed backup prevents write");

        // 11. Partial mutation triggers rollback of the same original.
        current = 7; original = null; hasOriginal = false;
        failed = false;
        try
        {
            await ExecuteDwordMutationAsync(true, capture, _ =>
            {
                current = 0;
                throw new IOException("simulated partial write");
            }, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && current == 7, "partial write rollback");

        // 12. Readback mismatch must also roll back.
        current = 7; original = null; hasOriginal = false;
        failed = false;
        try
        {
            await ExecuteDwordMutationAsync(true, capture, _ =>
            {
                current = 0;
                throw new InvalidDataException("simulated wrong DWORD kind");
            }, restore, CancellationToken.None);
        }
        catch (InvalidDataException) { failed = true; }
        Check(failed && current == 7, "readback mismatch rollback");

        // 13. If rollback finds no backup, report both original and recovery.
        bool compound = false;
        try
        {
            await ExecuteDwordMutationAsync(true, capture,
                _ => throw new IOException("failed write"),
                _ => Task.FromResult(false), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compound = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is InvalidOperationException;
        }
        Check(compound, "backup missing during rollback");

        // 14. If rollback itself throws, both failures survive.
        compound = false;
        try
        {
            await ExecuteDwordMutationAsync(true, capture,
                _ => throw new IOException("primary failure"),
                _ => throw new UnauthorizedAccessException("rollback failed"),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compound = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(compound, "compound mutation and rollback");

        // 15. OFF restore itself throwing is never treated as success.
        failed = false;
        try
        {
            await ExecuteDwordMutationAsync(false, capture, apply,
                _ => throw new InvalidDataException("corrupted backup"),
                CancellationToken.None);
        }
        catch (InvalidDataException) { failed = true; }
        Check(failed, "corrupt original fails closed");

        // 16. A successfully applied write followed by cancellation rolls back.
        current = 7; original = null; hasOriginal = false;
        using var afterWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteDwordMutationAsync(true, capture, _ =>
            {
                current = 0;
                afterWrite.Cancel();
                return Task.CompletedTask;
            }, restore, afterWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && current == 7, "post-write cancellation rollback");

        // 17-18. Pre-cancelled ON/OFF perform no action.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        int beforeCaptures = captureCount, beforeRestores = restoreCount;
        bool preOn = false, preOff = false;
        try { await ExecuteDwordMutationAsync(true, capture, apply, restore, pre.Token); }
        catch (OperationCanceledException) { preOn = true; }
        try { await ExecuteDwordMutationAsync(false, capture, apply, restore, pre.Token); }
        catch (OperationCanceledException) { preOff = true; }
        Check(preOn && preOff && captureCount == beforeCaptures &&
              writeCount == beforeWrites && restoreCount == beforeRestores,
              "pre-cancelled actions");

        // 19. Cancellation after capture but before application writes nothing.
        using var afterCapture = new CancellationTokenSource();
        cancelled = false;
        try
        {
            await ExecuteDwordMutationAsync(true, _ =>
            {
                afterCapture.Cancel();
                return Task.CompletedTask;
            }, apply, restore, afterCapture.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && writeCount == beforeWrites, "capture cancellation");
    }
}
