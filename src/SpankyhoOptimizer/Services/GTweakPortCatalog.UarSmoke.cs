using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Pure tests of the same transaction invoked by the real UAR toggle:
    // no Windows registry, Explorer, service or persisted receipt changes.
    internal static async Task RunUarMutationSmokeAsync()
    {
        static void Assert(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException("UAR_SMOKE_FAIL: " + name);
        }

        // 1. Keep all existing user-visible metadata for the single toggle.
        TweakDefinition definition = UarTweak();
        Assert(definition.Id == UarId && definition.Section == "privacy" &&
            definition.Group == "Activity / Usage" && definition.Recommended &&
            definition.Risk == TweakRisk.Safe && definition.RestartRequired &&
            definition.RequiresAdmin && definition.MinWindowsBuild == 19041 &&
            definition.RegistryHive == RegistryHive.LocalMachine &&
            definition.RegistryPath == UarRegistryPath &&
            definition.RegistryValueName == "DisableUAR" &&
            definition.RegistryEnabledValue == 1 &&
            definition.RevertMode == "OriginalRegistryValue" &&
            definition.RevertAsync is not null, "catalog");

        // 2-5. Capture all possible first-activation key/value shapes.
        UarOriginal missingKey = DecodeUarOriginal(
            EncodeUarOriginal(new UarOriginal(false, false, 0)));
        Assert(!missingKey.KeyExisted && !missingKey.ValueExisted, "absent key");
        UarOriginal missingValue = DecodeUarOriginal(
            EncodeUarOriginal(new UarOriginal(true, false, 0)));
        Assert(missingValue.KeyExisted && !missingValue.ValueExisted, "absent DWORD");
        UarOriginal initialZero = DecodeUarOriginal(
            EncodeUarOriginal(new UarOriginal(true, true, 0)));
        Assert(initialZero.ValueExisted && initialZero.Value == 0, "initial zero");
        UarOriginal initialSeven = DecodeUarOriginal(
            EncodeUarOriginal(new UarOriginal(true, true, 7)));
        Assert(initialSeven.ValueExisted && initialSeven.Value == 7, "initial custom");

        // 6-9. Invalid foreign, impossible and damaged receipts are rejected.
        bool bad = false;
        try { _ = DecodeUarOriginal("uar-original-v1|other|1|1|0"); }
        catch (InvalidDataException) { bad = true; }
        Assert(bad, "foreign receipt");
        bad = false;
        try { _ = DecodeUarOriginal("uar-original-v1|gt.privacy.uar|0|1|0"); }
        catch (InvalidDataException) { bad = true; }
        Assert(bad, "impossible receipt");
        bad = false;
        try { _ = DecodeUarOriginal("uar-original-v1|gt.privacy.uar|1|1|bad"); }
        catch (InvalidDataException) { bad = true; }
        Assert(bad, "bad DWORD");
        bad = false;
        try { _ = EncodeUarOriginal(new UarOriginal(false, true, 0)); }
        catch (InvalidDataException) { bad = true; }
        Assert(bad, "impossible snapshot");

        // 10. Key absence, value absence and original DWORD are distinct.
        Assert(UarMatches(initialZero, new UarOriginal(true, true, 0)) &&
            !UarMatches(initialZero, new UarOriginal(true, true, 1)) &&
            !UarMatches(missingKey, missingValue), "exact state matching");

        // 11. Never delete keys with foreign values/subkeys or an original key.
        Assert(CanRemoveUarOwnedKey(false, false, 0, false, false) &&
            CanRemoveUarOwnedKey(false, true, 1, false, true) &&
            !CanRemoveUarOwnedKey(true, true, 1, false, true) &&
            !CanRemoveUarOwnedKey(false, true, 0, false, false) &&
            !CanRemoveUarOwnedKey(false, true, 2, false, true) &&
            !CanRemoveUarOwnedKey(false, true, 1, true, true) &&
            !CanRemoveUarOwnedKey(false, true, 1, false, false),
            "nonrecursive owned deletion guard");

        int? value = 0, first = null;
        bool saved = false;
        int captures = 0, writes = 0, restores = 0;
        Func<CancellationToken, Task> capture = ct =>
        {
            Assert(!ct.IsCancellationRequested, "capture token");
            captures++;
            if (!saved) { first = value; saved = true; }
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = ct =>
        {
            Assert(!ct.IsCancellationRequested, "write token");
            writes++;
            value = 1;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> restore = ct =>
        {
            Assert(!ct.IsCancellationRequested, "uncancelled restore");
            restores++;
            if (!saved) throw new InvalidOperationException("No saved original");
            value = first;
            return Task.CompletedTask;
        };

        // 12-13. The original zero returns exactly after enable/disable.
        await ExecuteUarMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Assert(value == 1 && first == 0 && captures == 1 &&
            writes == 1 && restores == 0, "enable DWORD 1");
        await ExecuteUarMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Assert(value == 0 && captures == 1 &&
            writes == 1 && restores == 1, "restore original zero");

        // 14. Absent original value is removed, not replaced with a default.
        value = null; first = null; saved = false;
        await ExecuteUarMutationAsync(true, capture, apply, restore, CancellationToken.None);
        await ExecuteUarMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Assert(value is null && first is null, "restore missing original");

        // 15. A nonstandard original value must not be replaced by zero.
        value = 7; first = null; saved = false;
        await ExecuteUarMutationAsync(true, capture, apply, restore, CancellationToken.None);
        await ExecuteUarMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Assert(value == 7 && first == 7, "restore custom original");

        // 16. Repeated ON cannot overwrite the first captured baseline.
        value = 0; first = null; saved = false;
        await ExecuteUarMutationAsync(true, capture, apply, restore, CancellationToken.None);
        await ExecuteUarMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Assert(value == 1 && first == 0, "repeat ON baseline");
        await ExecuteUarMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Assert(value == 0, "repeat ON restored");

        // 17. Missing receipt on OFF fails closed without touching registry.
        bool failed = false;
        int beforeWrites = writes;
        try
        {
            await ExecuteUarMutationAsync(false, capture, apply,
                _ => throw new InvalidDataException("no original"),
                CancellationToken.None);
        }
        catch (InvalidDataException) { failed = true; }
        Assert(failed && writes == beforeWrites, "missing receipt");

        // 18. An atomic receipt storage error prevents any registry write.
        failed = false;
        try
        {
            await ExecuteUarMutationAsync(true,
                _ => throw new IOException("storage error"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Assert(failed && writes == beforeWrites, "capture fault");

        // 19. A partially written policy triggers rollback to the original.
        value = 0; first = null; saved = false; failed = false;
        try
        {
            await ExecuteUarMutationAsync(true, capture, _ =>
            {
                value = 1;
                throw new IOException("partial registry write");
            }, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Assert(failed && value == 0, "partial write rollback");

        // 20. A readback verification failure also triggers rollback.
        value = 0; first = null; saved = false; failed = false;
        try
        {
            await ExecuteUarMutationAsync(true, capture, _ =>
            {
                value = 1;
                throw new InvalidDataException("incorrect DWORD kind");
            }, restore, CancellationToken.None);
        }
        catch (InvalidDataException) { failed = true; }
        Assert(failed && value == 0, "readback fault rollback");

        // 21. Preserve both mutation and rollback errors.
        bool compound = false;
        try
        {
            await ExecuteUarMutationAsync(true, capture,
                _ => throw new IOException("write failed"),
                _ => throw new UnauthorizedAccessException("restore failed"),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compound = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Assert(compound, "combined failures");

        // 22. Never overwrite an external OFF-time edit.
        value = 5; failed = false;
        try
        {
            await ExecuteUarMutationAsync(false, capture, apply,
                _ => throw new InvalidOperationException("foreign edit"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { failed = true; }
        Assert(failed && value == 5, "external modification protection");

        // 23. After-write cancellation still calls uncancelled restore.
        value = 0; first = null; saved = false;
        using var afterWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteUarMutationAsync(true, capture, _ =>
            {
                value = 1;
                afterWrite.Cancel();
                return Task.CompletedTask;
            }, restore, afterWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled && value == 0, "post-write cancel restore");

        // 24. Already cancelled ON and OFF do not capture or write.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        int oldCaptures = captures, oldRestores = restores;
        bool preOn = false, preOff = false;
        try { await ExecuteUarMutationAsync(true, capture, apply, restore, pre.Token); }
        catch (OperationCanceledException) { preOn = true; }
        try { await ExecuteUarMutationAsync(false, capture, apply, restore, pre.Token); }
        catch (OperationCanceledException) { preOff = true; }
        Assert(preOn && preOff && captures == oldCaptures &&
            restores == oldRestores && writes == beforeWrites, "pre-cancelled actions");

        // 25. Cancellation during receipt write prevents policy modification.
        using var afterCapture = new CancellationTokenSource();
        cancelled = false;
        try
        {
            await ExecuteUarMutationAsync(true, _ =>
            {
                afterCapture.Cancel();
                return Task.CompletedTask;
            }, apply, restore, afterCapture.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled && writes == beforeWrites, "capture cancellation");
    }
}
