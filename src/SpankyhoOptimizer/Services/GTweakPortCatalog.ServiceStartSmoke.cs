using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Tests call the production service-start state machine with in-memory
    // fake SCM and receipt operations: Windows services are never modified.
    internal static async Task RunServiceStartMutationSmokeAsync()
    {
        static void Check(bool ok, string name)
        {
            if (!ok)
                throw new InvalidOperationException("SERVICE_START_SMOKE_FAIL: " + name);
        }

        const string name = "DPS";
        var definition = ServiceDisableTweak(name, "Diagnostic Policy Service", TweakRisk.Advanced);
        // 1. Same public service tweak ID, manual activation and explicit restore.
        Check(definition.Id == "gt.svc.DPS" && definition.Section == "components" &&
              !definition.Recommended && definition.RequiresAdmin &&
              definition.RestartRequired && definition.RevertAsync is not null &&
              definition.RevertMode == "Explicit", "catalog metadata");

        // 2. Original startup types survive a durable receipt round-trip.
        Check(ParseServiceStartReceipt(EncodeServiceStartReceipt(name, 2), name) == 2 &&
              ParseServiceStartReceipt(EncodeServiceStartReceipt(name, 3), name) == 3 &&
              ParseServiceStartReceipt(EncodeServiceStartReceipt(name, 4), name) == 4,
              "original types retained");

        // 3. Foreign or malformed receipts and boot drivers fail closed.
        bool invalid = false;
        try { _ = ParseServiceStartReceipt("service-start-original-v1|OtherSvc|2", name); }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "foreign receipt rejected");
        invalid = false;
        try { _ = ParseServiceStartReceipt("service-start-original-v1|DPS|0", name); }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "boot driver receipt rejected");

        int? current = 2, saved = null;
        int captureCount = 0, changeCount = 0, safetyCount = 0;
        Func<CancellationToken, Task<int?>> read = token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(current);
        };
        Func<int, CancellationToken, Task<int>> capture = (observed, token) =>
        {
            token.ThrowIfCancellationRequested();
            captureCount++;
            saved ??= observed;
            return Task.FromResult(saved.Value);
        };
        Func<CancellationToken, Task<int>> load = token =>
        {
            token.ThrowIfCancellationRequested();
            return saved is int original
                ? Task.FromResult(original)
                : Task.FromException<int>(new InvalidOperationException("No durable receipt"));
        };
        Func<int, CancellationToken, Task> change = (desired, token) =>
        {
            token.ThrowIfCancellationRequested();
            changeCount++;
            current = desired;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> safety = token =>
        {
            token.ThrowIfCancellationRequested();
            safetyCount++;
            return Task.CompletedTask;
        };

        // 4. Automatic (2) -> Disabled (4) saves original and changes type.
        await ExecuteServiceStartMutationAsync(true, name, read, capture, load, change, safety,
            CancellationToken.None);
        Check(current == 4 && saved == 2 && changeCount == 1 &&
              captureCount == 1 && safetyCount == 1, "disable Automatic");

        // 5. Repeated ON is idempotent and cannot overwrite saved Automatic.
        await ExecuteServiceStartMutationAsync(true, name, read, capture, load, change, safety,
            CancellationToken.None);
        Check(current == 4 && saved == 2 && changeCount == 1 && captureCount == 2,
            "repeat ON preserves original");

        // 6. OFF restores original Automatic, not a hardcoded Manual.
        await ExecuteServiceStartMutationAsync(false, name, read, capture, load, change, safety,
            CancellationToken.None);
        Check(current == 2 && saved == 2 && changeCount == 2, "restore Automatic");

        // 7. Repeated OFF does not write again.
        await ExecuteServiceStartMutationAsync(false, name, read, capture, load, change, safety,
            CancellationToken.None);
        Check(changeCount == 2 && current == 2, "repeat OFF idempotent");

        // 8. Original Manual (3) -> Disabled -> Manual roundtrip.
        current = 3; saved = null;
        await ExecuteServiceStartMutationAsync(true, name, read, capture, load, change, safety,
            CancellationToken.None);
        await ExecuteServiceStartMutationAsync(false, name, read, capture, load, change, safety,
            CancellationToken.None);
        Check(current == 3 && saved == 3, "restore Manual");

        // 9. Already-disabled service is never enabled on OFF.
        current = 4; saved = null;
        int earlierChanges = changeCount;
        await ExecuteServiceStartMutationAsync(true, name, read, capture, load, change, safety,
            CancellationToken.None);
        await ExecuteServiceStartMutationAsync(false, name, read, capture, load, change, safety,
            CancellationToken.None);
        Check(current == 4 && saved == 4 && changeCount == earlierChanges,
            "originally disabled remains disabled");

        // 10. Missing backup on OFF never writes Manual or anything else.
        saved = null; current = 4;
        bool missing = false;
        earlierChanges = changeCount;
        try
        {
            await ExecuteServiceStartMutationAsync(false, name, read, capture, load, change,
                safety, CancellationToken.None);
        }
        catch (InvalidOperationException) { missing = true; }
        Check(missing && changeCount == earlierChanges && current == 4,
            "missing original fails closed");

        // 11. Service safety refusal occurs BEFORE backup and any SCM writes.
        current = 2; saved = null;
        int earlierCaptures = captureCount;
        bool blocked = false;
        try
        {
            await ExecuteServiceStartMutationAsync(true, name, read, capture, load, change,
                _ => throw new InvalidOperationException("Critical service blocked"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { blocked = true; }
        Check(blocked && captureCount == earlierCaptures &&
              changeCount == earlierChanges && current == 2, "critical service blocked");

        // 12. Persisting the original fails closed before the first SCM write.
        bool backupFault = false;
        try
        {
            await ExecuteServiceStartMutationAsync(true, name, read,
                (_, _) => throw new IOException("Receipt not durable"), load, change, safety,
                CancellationToken.None);
        }
        catch (IOException) { backupFault = true; }
        Check(backupFault && current == 2 && changeCount == earlierChanges,
            "receipt persistence fail closed");

        // 13. Existing receipt and third-party modification must not be overwritten.
        saved = 2; current = 3;
        bool thirdParty = false;
        try
        {
            await ExecuteServiceStartMutationAsync(true, name, read, capture, load, change,
                safety, CancellationToken.None);
        }
        catch (InvalidOperationException) { thirdParty = true; }
        Check(thirdParty && current == 3 && changeCount == earlierChanges,
            "third-party change blocks ON");
        // OFF also refuses to override a third-party Manual start type.
        thirdParty = false;
        try
        {
            await ExecuteServiceStartMutationAsync(false, name, read, capture, load, change,
                safety, CancellationToken.None);
        }
        catch (InvalidOperationException) { thirdParty = true; }
        Check(thirdParty && current == 3 && changeCount == earlierChanges,
            "third-party change blocks OFF");

        // 14. A failed write after changing to Disabled restores Automatic.
        saved = 2; current = 2;
        bool writeFault = false;
        try
        {
            await ExecuteServiceStartMutationAsync(true, name, read, capture, load,
                (desired, _) =>
                {
                    changeCount++;
                    current = desired;
                    if (desired == 4) throw new IOException("Injected SCM failure after change");
                    return Task.CompletedTask;
                }, safety, CancellationToken.None);
        }
        catch (IOException) { writeFault = true; }
        Check(writeFault && current == 2, "partial write rollback");

        // 15. Readback mismatch after successful write also restores the original.
        current = 2; saved = 2;
        int readsAfterMutation = 0;
        bool readbackFault = false;
        try
        {
            await ExecuteServiceStartMutationAsync(true, name,
                ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    readsAfterMutation++;
                    if (readsAfterMutation == 2)
                        return Task.FromResult<int?>(3); // injected wrong SCM readback
                    return Task.FromResult(current);
                },
                capture, load, change, safety, CancellationToken.None);
        }
        catch (IOException) { readbackFault = true; }
        Check(readbackFault && current == 2, "readback fault rollback");

        // 16. Cancellation after setting Disabled restores original (uncancelled).
        current = 2; saved = 2;
        using var afterWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteServiceStartMutationAsync(true, name, read, capture, load,
                (desired, token) =>
                {
                    // Simulate a completed Windows write followed by cancellation,
                    // not a cancellation that aborts before writing.
                    if (desired == 4)
                    {
                        current = 4;
                        afterWrite.Cancel();
                        return Task.CompletedTask;
                    }
                    if (token.IsCancellationRequested)
                        throw new InvalidOperationException("Cancelled token used for rollback");
                    current = desired;
                    return Task.CompletedTask;
                }, safety, afterWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && current == 2, "cancelled enable rollback");

        // 17. Rollback failure surfaces together with the original SCM error.
        current = 2; saved = 2;
        bool combined = false;
        try
        {
            await ExecuteServiceStartMutationAsync(true, name, read, capture, load,
                (desired, _) =>
                {
                    if (desired == 4)
                    {
                        current = 4;
                        throw new IOException("Initial SCM error after write");
                    }
                    // Simulate a rejected rollback before changing the type.
                    throw new UnauthorizedAccessException("Rollback SCM error");
                }, safety, CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            combined = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(combined && current == 4, "compound rollback exception");
        current = 2;

        // 18. Already-cancelled ON/OFF must not capture, write or restore.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        earlierChanges = changeCount;
        earlierCaptures = captureCount;
        bool preOn = false, preOff = false;
        try
        {
            await ExecuteServiceStartMutationAsync(true, name, read, capture, load, change,
                safety, pre.Token);
        }
        catch (OperationCanceledException) { preOn = true; }
        try
        {
            await ExecuteServiceStartMutationAsync(false, name, read, capture, load, change,
                safety, pre.Token);
        }
        catch (OperationCanceledException) { preOff = true; }
        Check(preOn && preOff && changeCount == earlierChanges &&
              captureCount == earlierCaptures, "pre-cancelled requests");

        // 19. Boot/start drivers with Start 0/1 must not be disabled by this UI.
        current = 1; saved = null;
        bool bootBlocked = false;
        try
        {
            await ExecuteServiceStartMutationAsync(true, name, read, capture, load, change,
                safety, CancellationToken.None);
        }
        catch (InvalidOperationException) { bootBlocked = true; }
        Check(bootBlocked && changeCount == earlierChanges &&
              captureCount == earlierCaptures, "boot driver blocked");

        // 20. Nonexistent service must fail without changing other SCM entries.
        current = null; saved = null;
        bool missingService = false;
        try
        {
            await ExecuteServiceStartMutationAsync(true, name, read, capture, load, change,
                safety, CancellationToken.None);
        }
        catch (InvalidOperationException) { missingService = true; }
        Check(missingService && changeCount == earlierChanges,
            "missing Windows service");
    }
}
