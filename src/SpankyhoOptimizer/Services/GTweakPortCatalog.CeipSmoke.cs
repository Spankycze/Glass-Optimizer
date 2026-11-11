using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // These cases invoke the SAME production transaction through in-memory
    // callbacks. They never change Windows registry values or local policies.
    internal static async Task RunCeipMutationSmokeAsync()
    {
        static void Check(bool ok, string scenario)
        {
            if (!ok) throw new InvalidOperationException(
                "CEIP_MUTATION_SMOKE_FAIL: " + scenario);
        }

        // 1. Preserve the original public catalogue contract.
        TweakDefinition tweak = CeipTweak();
        Check(tweak.Id == CeipId && tweak.Section == "privacy" &&
              tweak.Group == "Telemetry" && tweak.Recommended &&
              tweak.Risk == TweakRisk.Safe && tweak.RestartRequired &&
              tweak.RequiresAdmin && tweak.RegistryHive == RegistryHive.LocalMachine &&
              tweak.RegistryPath == CeipRegistryPath &&
              tweak.RegistryValueName == CeipValueName &&
              tweak.RegistryEnabledValue == 0 &&
              tweak.RevertAsync is not null &&
              tweak.RevertMode == "OriginalRegistryValue", "catalog");

        // 2. Never reuse a legacy generic DWORD snapshot.
        Check(CeipBackupId == "gt.privacy.ceip.original-v1" &&
              CeipBackupId != CeipId, "versioned original");

        // 3-6. Durably distinguish both types of absence and prior values.
        CeipOriginal absent = DecodeCeipOriginal(
            EncodeCeipOriginal(new CeipOriginal(false, false, 0)));
        Check(!absent.KeyExisted && !absent.ValueExisted, "key absent");
        CeipOriginal missing = DecodeCeipOriginal(
            EncodeCeipOriginal(new CeipOriginal(true, false, 0)));
        Check(missing.KeyExisted && !missing.ValueExisted, "value absent");
        CeipOriginal one = DecodeCeipOriginal(
            EncodeCeipOriginal(new CeipOriginal(true, true, 1)));
        Check(one.KeyExisted && one.ValueExisted && one.Value == 1, "original one");
        CeipOriginal custom = DecodeCeipOriginal(
            EncodeCeipOriginal(new CeipOriginal(true, true, 5)));
        Check(custom.Value == 5, "original custom");

        // 7-9. Reject damaged/foreign or logically impossible receipts.
        bool invalid = false;
        try { _ = DecodeCeipOriginal("ceip-original-v1|foreign|1|1|1"); }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "foreign receipt");
        invalid = false;
        try { _ = DecodeCeipOriginal("ceip-original-v1|gt.privacy.ceip|0|1|1"); }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "impossible absent key");
        invalid = false;
        try { _ = DecodeCeipOriginal("ceip-original-v1|gt.privacy.ceip|1|1|not-a-dword"); }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "invalid DWORD");

        // 10. Match must account for existence and exact original value.
        Check(CeipStatesMatch(one, new CeipOriginal(true, true, 1)) &&
              !CeipStatesMatch(one, new CeipOriginal(true, true, 0)) &&
              !CeipStatesMatch(absent, missing), "exact baseline");

        // 11. Only a new key with our lone DWORD may be deleted.
        Check(CanRemoveOwnedCeipKey(false, false, 0, false, false) &&
              CanRemoveOwnedCeipKey(false, true, 1, false, true) &&
              !CanRemoveOwnedCeipKey(true, true, 1, false, true) &&
              !CanRemoveOwnedCeipKey(false, true, 0, false, false) &&
              !CanRemoveOwnedCeipKey(false, true, 2, false, true) &&
              !CanRemoveOwnedCeipKey(false, true, 1, true, true) &&
              !CanRemoveOwnedCeipKey(false, true, 1, false, false),
              "non-destructive registry cleanup");

        int? current = 1, original = null;
        bool saved = false;
        int saves = 0, writes = 0, restores = 0;
        Func<CancellationToken, Task> save = ct =>
        {
            Check(!ct.IsCancellationRequested, "save token");
            saves++;
            if (!saved) { original = current; saved = true; }
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = ct =>
        {
            Check(!ct.IsCancellationRequested, "write token");
            writes++;
            current = 0;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> restore = ct =>
        {
            Check(!ct.IsCancellationRequested, "restore token");
            restores++;
            if (!saved)
                throw new InvalidOperationException("original backup is missing");
            current = original;
            return Task.CompletedTask;
        };

        // 12-13. An existing CEIPEnable 1 -> 0 -> precisely 1.
        await ExecuteCeipMutationAsync(true, save, apply, restore, CancellationToken.None);
        Check(current == 0 && original == 1 && saves == 1 &&
              writes == 1 && restores == 0, "CEIP OFF");
        await ExecuteCeipMutationAsync(false, save, apply, restore, CancellationToken.None);
        Check(current == 1 && saves == 1 && writes == 1 && restores == 1,
              "CEIP original restored");

        // 14. Restore a truly absent value instead of introducing DWORD 1.
        current = null; original = null; saved = false;
        await ExecuteCeipMutationAsync(true, save, apply, restore, CancellationToken.None);
        await ExecuteCeipMutationAsync(false, save, apply, restore, CancellationToken.None);
        Check(current is null && original is null, "originally missing policy restored");

        // 15. Repeated ON must preserve the first original state.
        current = 5; original = null; saved = false;
        await ExecuteCeipMutationAsync(true, save, apply, restore, CancellationToken.None);
        await ExecuteCeipMutationAsync(true, save, apply, restore, CancellationToken.None);
        Check(current == 0 && original == 5, "repeated ON retains original");
        await ExecuteCeipMutationAsync(false, save, apply, restore, CancellationToken.None);
        Check(current == 5, "repeated ON restores original 5");

        // 16. A missing original must never force DWORD 1 or delete a key.
        int oldWrites = writes;
        bool failed = false;
        try
        {
            await ExecuteCeipMutationAsync(false, save, apply,
                _ => throw new InvalidOperationException("missing first-activation receipt"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && writes == oldWrites, "missing original fails closed");

        // 17. Failed durable backup prevents the first registry mutation.
        failed = false;
        try
        {
            await ExecuteCeipMutationAsync(true,
                _ => throw new IOException("simulated backup disk fault"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && writes == oldWrites, "backup failure prevents write");

        // 18. A partial registry write invokes uncancelled restoration.
        current = 1; original = null; saved = false;
        failed = false;
        try
        {
            await ExecuteCeipMutationAsync(true, save, _ =>
            {
                current = 0;
                throw new IOException("simulated partial write");
            }, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && current == 1, "partial write rollback");

        // 19. Incorrect DWORD kind or readback also triggers rollback.
        current = 1; original = null; saved = false;
        failed = false;
        try
        {
            await ExecuteCeipMutationAsync(true, save, _ =>
            {
                current = 0;
                throw new InvalidDataException("simulated wrong readback");
            }, restore, CancellationToken.None);
        }
        catch (InvalidDataException) { failed = true; }
        Check(failed && current == 1, "readback rollback");

        // 20. Mutation AND rollback errors must both be observable.
        bool both = false;
        try
        {
            await ExecuteCeipMutationAsync(true, save,
                _ => throw new IOException("primary failure"),
                _ => throw new UnauthorizedAccessException("rollback failure"),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            both = ex.InnerExceptions.Count == 2 &&
                   ex.InnerExceptions[0] is IOException &&
                   ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(both, "combined failures");

        // 21. OFF protects a setting changed by another program.
        current = 1;
        failed = false;
        try
        {
            await ExecuteCeipMutationAsync(false, save, apply,
                _ => throw new InvalidOperationException("external edit"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && current == 1, "do not overwrite third-party edit");

        // 22. Cancellation AFTER a write still runs uncancelled rollback.
        current = 1; original = null; saved = false;
        using var afterWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteCeipMutationAsync(true, save, _ =>
            {
                current = 0;
                afterWrite.Cancel();
                return Task.CompletedTask;
            }, restore, afterWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && current == 1, "post-write cancellation rollback");

        // 23-24. Pre-cancelled ON/OFF do not touch any state.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        int beforeSaves = saves, beforeWrites = writes, beforeRestores = restores;
        bool preOn = false, preOff = false;
        try { await ExecuteCeipMutationAsync(true, save, apply, restore, pre.Token); }
        catch (OperationCanceledException) { preOn = true; }
        try { await ExecuteCeipMutationAsync(false, save, apply, restore, pre.Token); }
        catch (OperationCanceledException) { preOff = true; }
        Check(preOn && preOff && saves == beforeSaves && writes == beforeWrites &&
              restores == beforeRestores, "pre-cancelled ON/OFF");

        // 25. Cancellation after backup but before write changes nothing.
        using var afterCapture = new CancellationTokenSource();
        cancelled = false;
        try
        {
            await ExecuteCeipMutationAsync(true, _ =>
            {
                afterCapture.Cancel();
                return Task.CompletedTask;
            }, apply, restore, afterCapture.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && writes == beforeWrites, "cancelled after capture");
    }
}
