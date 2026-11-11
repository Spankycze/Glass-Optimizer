using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Pure fault-injection tests of the production transaction boundary.
    // No actual HKLM policy writes or changes to Microsoft Store settings.
    internal static async Task RunStoreUpdatesMutationSmokeAsync()
    {
        static void Check(bool ok, string caseName)
        {
            if (!ok) throw new InvalidOperationException(
                "STORE_UPDATES_SMOKE_FAIL: " + caseName);
        }

        // 1. Security and UI contract: advanced opt-in only, stable ID.
        TweakDefinition tweak = StoreAutoUpdateTweak();
        Check(tweak.Id == StoreUpdatesId && tweak.Section == "system" &&
              tweak.Group == "Microsoft Store" && tweak.Risk == TweakRisk.Advanced &&
              !tweak.Recommended && tweak.RequiresAdmin && !tweak.RestartRequired &&
              tweak.RegistryHive == RegistryHive.LocalMachine &&
              tweak.RegistryPath == StoreUpdatesKey &&
              tweak.RegistryValueName == StoreUpdatesValue &&
              tweak.RegistryEnabledValue == 2 &&
              tweak.RevertAsync is not null && tweak.RevertMode == "OriginalRegistryValue" &&
              !string.IsNullOrWhiteSpace(tweak.Warning),
              "advanced opt-in policy metadata");

        // 2. Never reuse the old generic-factory backup ID.
        Check(StoreUpdatesBackupId == "gt.sys.storeautoupdate.original-v1" &&
              StoreUpdatesBackupId != StoreUpdatesId, "versioned original backup ID");

        // 3-6. Receipt distinguishes absent key, empty key and original DWORD.
        StoreUpdateOriginal absent = DecodeStoreUpdateOriginal(
            EncodeStoreUpdateOriginal(new StoreUpdateOriginal(false, false, 0)));
        Check(!absent.KeyExisted && !absent.ValueExisted, "originally absent key");
        StoreUpdateOriginal empty = DecodeStoreUpdateOriginal(
            EncodeStoreUpdateOriginal(new StoreUpdateOriginal(true, false, 0)));
        Check(empty.KeyExisted && !empty.ValueExisted, "originally absent value");
        StoreUpdateOriginal automatic = DecodeStoreUpdateOriginal(
            EncodeStoreUpdateOriginal(new StoreUpdateOriginal(true, true, 4)));
        Check(automatic.KeyExisted && automatic.ValueExisted && automatic.Value == 4,
              "original automatic updates");
        StoreUpdateOriginal other = DecodeStoreUpdateOriginal(
            EncodeStoreUpdateOriginal(new StoreUpdateOriginal(true, true, 3)));
        Check(other.Value == 3, "other original DWORD");

        // 7-9. Reject foreign, inconsistent, malformed ownership receipts.
        bool malformed = false;
        try { _ = DecodeStoreUpdateOriginal("store-autoupdate-original-v1|foreign|1|1|4"); }
        catch (InvalidDataException) { malformed = true; }
        Check(malformed, "foreign receipt");
        malformed = false;
        try { _ = DecodeStoreUpdateOriginal("store-autoupdate-original-v1|gt.sys.storeautoupdate|0|1|4"); }
        catch (InvalidDataException) { malformed = true; }
        Check(malformed, "impossible original key state");
        malformed = false;
        try { _ = DecodeStoreUpdateOriginal("store-autoupdate-original-v1|gt.sys.storeautoupdate|1|1|NaN"); }
        catch (InvalidDataException) { malformed = true; }
        Check(malformed, "invalid value snapshot");

        // 10. Disabled policy is only exact DWORD=2, not a string or missing.
        Check(IsStoreUpdatePolicyDisabled(true, RegistryValueKind.DWord, 2) &&
              !IsStoreUpdatePolicyDisabled(true, RegistryValueKind.String, "2") &&
              !IsStoreUpdatePolicyDisabled(true, RegistryValueKind.DWord, 4) &&
              !IsStoreUpdatePolicyDisabled(false, RegistryValueKind.None, null),
              "strict policy type and value");

        // 11. No deletion of foreign values/subkeys or non-owned Store keys.
        Check(CanDeleteOwnedStorePolicyKey(false, false, 0, false, false) &&
              CanDeleteOwnedStorePolicyKey(false, true, 1, false, true) &&
              !CanDeleteOwnedStorePolicyKey(true, true, 1, false, true) &&
              !CanDeleteOwnedStorePolicyKey(false, true, 1, true, true) &&
              !CanDeleteOwnedStorePolicyKey(false, true, 2, false, true) &&
              !CanDeleteOwnedStorePolicyKey(false, true, 0, false, false) &&
              !CanDeleteOwnedStorePolicyKey(false, true, 1, false, false),
              "owned key deletion guard");

        // 12. Exact original state comparison is idempotent and detects
        // any externally added key or changed setting.
        Check(StoreOriginalMatches(automatic, new StoreUpdateOriginal(true, true, 4)) &&
              !StoreOriginalMatches(automatic, new StoreUpdateOriginal(true, true, 2)) &&
              !StoreOriginalMatches(absent, new StoreUpdateOriginal(true, false, 0)),
              "original state match");

        int captureCount = 0, writeCount = 0, restoreCount = 0;
        int? current = 4, originalValue = null;
        bool originalStored = false;
        Func<CancellationToken, Task> capture = ct =>
        {
            Check(!ct.IsCancellationRequested, "capture token cancelled");
            captureCount++;
            if (!originalStored)
            {
                originalValue = current;
                originalStored = true;
            }
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = ct =>
        {
            Check(!ct.IsCancellationRequested, "apply token cancelled");
            writeCount++;
            current = 2;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> restore = ct =>
        {
            Check(!ct.IsCancellationRequested, "restore token cancelled");
            restoreCount++;
            if (!originalStored)
                throw new InvalidOperationException("Missing original in fake backup");
            current = originalValue;
            return Task.CompletedTask;
        };

        // 13-14. 4 -> 2 -> 4 without forcing synthetic default 4 on OFF.
        await ExecuteStoreUpdatesMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Check(current == 2 && originalValue == 4 &&
              captureCount == 1 && writeCount == 1 && restoreCount == 0, "enable Store policy");
        await ExecuteStoreUpdatesMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(current == 4 && writeCount == 1 && captureCount == 1 && restoreCount == 1,
              "restore original policy");

        // 15. An originally missing value stays missing on OFF.
        current = null; originalStored = false; originalValue = null;
        await ExecuteStoreUpdatesMutationAsync(true, capture, apply, restore, CancellationToken.None);
        await ExecuteStoreUpdatesMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(current is null && originalValue is null, "originally absent DWORD");

        // 16. Repeated ON retains first original snapshot.
        current = 4; originalStored = false; originalValue = null;
        await ExecuteStoreUpdatesMutationAsync(true, capture, apply, restore, CancellationToken.None);
        await ExecuteStoreUpdatesMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Check(originalValue == 4 && current == 2, "repeat ON original preserved");
        await ExecuteStoreUpdatesMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(current == 4, "repeat original restored");

        // 17. Cannot enable if durable first-original capture fails.
        int writesBefore = writeCount;
        bool failed = false;
        try
        {
            await ExecuteStoreUpdatesMutationAsync(true,
                _ => throw new IOException("failed to persist original policy"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && writeCount == writesBefore, "backup failure blocks mutation");

        // 18. Without a receipt OFF refuses to invent original policy.
        failed = false;
        try
        {
            await ExecuteStoreUpdatesMutationAsync(false, capture, apply,
                _ => throw new InvalidOperationException("original record missing"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && writeCount == writesBefore, "unknown original fails closed");

        // 19. Mutation error after a partial write triggers rollback.
        current = 4; originalValue = null; originalStored = false;
        failed = false;
        try
        {
            await ExecuteStoreUpdatesMutationAsync(true, capture,
                _ =>
                {
                    current = 2;
                    throw new IOException("simulated policy write failure");
                }, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && current == 4, "partial write rolled back");

        // 20. An invalid readback also rolls back.
        current = 4; originalValue = null; originalStored = false;
        failed = false;
        try
        {
            await ExecuteStoreUpdatesMutationAsync(true, capture,
                _ =>
                {
                    current = 2;
                    throw new InvalidDataException("simulated DWORD type mismatch");
                }, restore, CancellationToken.None);
        }
        catch (InvalidDataException) { failed = true; }
        Check(failed && current == 4, "readback mismatch rolled back");

        // 21. Preserve both primary failure and rollback fault.
        bool combined = false;
        try
        {
            await ExecuteStoreUpdatesMutationAsync(true, capture,
                _ => throw new IOException("policy failure"),
                _ => throw new UnauthorizedAccessException("rollback denied"),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(combined, "compound failure");

        // 22. Cancellation AFTER changing a value must restore it without
        // propagating the cancelled token into rollback.
        current = 4; originalValue = null; originalStored = false;
        using var post = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteStoreUpdatesMutationAsync(true, capture,
                _ =>
                {
                    current = 2;
                    post.Cancel();
                    return Task.CompletedTask;
                }, restore, post.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && current == 4, "post-write cancellation rollback");

        // 23-24. Already-cancelled ON/OFF cannot capture, mutate or restore.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        int capturedBefore = captureCount, changedBefore = writeCount,
            restoredBefore = restoreCount;
        bool preOn = false, preOff = false;
        try
        {
            await ExecuteStoreUpdatesMutationAsync(true, capture, apply, restore, pre.Token);
        }
        catch (OperationCanceledException) { preOn = true; }
        try
        {
            await ExecuteStoreUpdatesMutationAsync(false, capture, apply, restore, pre.Token);
        }
        catch (OperationCanceledException) { preOff = true; }
        Check(preOn && preOff && captureCount == capturedBefore &&
              writeCount == changedBefore && restoreCount == restoredBefore,
              "pre-cancelled requests");

        // 25. Cancellation after capture but before apply prevents writing.
        using var during = new CancellationTokenSource();
        cancelled = false;
        try
        {
            await ExecuteStoreUpdatesMutationAsync(true,
                _ =>
                {
                    during.Cancel();
                    return Task.CompletedTask;
                }, apply, restore, during.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && writeCount == changedBefore, "cancel after capture");
    }
}
