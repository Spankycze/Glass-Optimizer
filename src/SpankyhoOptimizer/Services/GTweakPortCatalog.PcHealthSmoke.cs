using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // All mutation callbacks use in-memory state. No real HKLM keys,
    // original-state files or Windows components are changed by these tests.
    internal static async Task RunPcHealthMutationSmokeAsync()
    {
        static void Check(bool success, string scenario)
        {
            if (!success)
                throw new InvalidOperationException(
                    "PCHEALTH_MUTATION_SMOKE_FAIL: " + scenario);
        }

        // 1. Stable public ID and registry metadata with explicit restore.
        TweakDefinition feature = PcHealthTweak();
        Check(feature.Id == PcHealthId &&
              feature.Section == "system" && feature.Group == "Windows" &&
              feature.Risk == TweakRisk.Optional && !feature.Recommended &&
              !feature.RestartRequired && feature.RequiresAdmin &&
              feature.RegistryHive == RegistryHive.LocalMachine &&
              feature.RegistryPath == PcHealthPath &&
              feature.RegistryValueName == PcHealthValue &&
              feature.RegistryEnabledValue == 1 &&
              feature.RevertAsync is not null &&
              feature.RevertMode == "OriginalRegistryValue", "catalog");

        // 2. A separately versioned first-original avoids historic backup.
        Check(PcHealthBackupId == "gt.sys.pchealth.original-v1" &&
              PcHealthBackupId != PcHealthId, "versioned backup");

        // 3-6. Durable receipts distinguish absence, zero, and other DWORDs.
        PcHealthOriginal missing = DecodePcHealthOriginal(
            EncodePcHealthOriginal(new PcHealthOriginal(false, false, 0)));
        Check(!missing.KeyExisted && !missing.ValueExisted, "absent key");
        PcHealthOriginal noValue = DecodePcHealthOriginal(
            EncodePcHealthOriginal(new PcHealthOriginal(true, false, 0)));
        Check(noValue.KeyExisted && !noValue.ValueExisted, "absent value");
        PcHealthOriginal zero = DecodePcHealthOriginal(
            EncodePcHealthOriginal(new PcHealthOriginal(true, true, 0)));
        Check(zero.KeyExisted && zero.ValueExisted && zero.Value == 0, "original DWORD 0");
        PcHealthOriginal other = DecodePcHealthOriginal(
            EncodePcHealthOriginal(new PcHealthOriginal(true, true, 3)));
        Check(other.Value == 3, "original DWORD 3");

        // 7-9. Tampered/foreign/impossible receipts never pass validation.
        bool rejected = false;
        try { _ = DecodePcHealthOriginal("pchealth-original-v1|foreign|1|1|0"); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "foreign receipt");
        rejected = false;
        try { _ = DecodePcHealthOriginal("pchealth-original-v1|gt.sys.pchealth|0|1|0"); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "impossible key/value state");
        rejected = false;
        try { _ = DecodePcHealthOriginal("pchealth-original-v1|gt.sys.pchealth|1|1|xyz"); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "malformed DWORD");

        // 10. Equality must include key/value existence and the exact data.
        Check(PcHealthMatches(zero, new PcHealthOriginal(true, true, 0)) &&
              !PcHealthMatches(zero, new PcHealthOriginal(true, true, 1)) &&
              !PcHealthMatches(missing, noValue), "state equality");

        // 11. Refuse deleting originally-existing, populated, or edited keys.
        Check(CanRemoveOwnedPcHealthKey(false, false, 0, false, false) &&
              CanRemoveOwnedPcHealthKey(false, true, 1, false, true) &&
              !CanRemoveOwnedPcHealthKey(true, true, 1, false, true) &&
              !CanRemoveOwnedPcHealthKey(false, true, 0, false, false) &&
              !CanRemoveOwnedPcHealthKey(false, true, 2, false, true) &&
              !CanRemoveOwnedPcHealthKey(false, true, 1, true, true) &&
              !CanRemoveOwnedPcHealthKey(false, true, 1, false, false),
              "safe key ownership");

        int? actual = 0, initial = null;
        bool backedUp = false;
        int captures = 0, writes = 0, restores = 0;
        Func<CancellationToken, Task> capture = token =>
        {
            Check(!token.IsCancellationRequested, "backup cancellation");
            captures++;
            if (!backedUp)
            {
                initial = actual;
                backedUp = true;
            }
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = token =>
        {
            Check(!token.IsCancellationRequested, "apply cancellation");
            writes++;
            actual = 1;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> restore = token =>
        {
            Check(!token.IsCancellationRequested, "restore cancellation");
            restores++;
            if (!backedUp)
                throw new InvalidOperationException("Missing original");
            actual = initial;
            return Task.CompletedTask;
        };

        // 12-13. Original 0 -> 1 -> original 0.
        await ExecutePcHealthMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Check(actual == 1 && initial == 0 && captures == 1 &&
              writes == 1 && restores == 0, "enable");
        await ExecutePcHealthMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(actual == 0 && captures == 1 && writes == 1 && restores == 1, "restore zero");

        // 14. Original value absence remains absent.
        actual = null; initial = null; backedUp = false;
        await ExecutePcHealthMutationAsync(true, capture, apply, restore, CancellationToken.None);
        await ExecutePcHealthMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(actual is null && initial is null, "restore absence");

        // 15. Repeated ON never replaces the first backup baseline.
        actual = 3; initial = null; backedUp = false;
        await ExecutePcHealthMutationAsync(true, capture, apply, restore, CancellationToken.None);
        await ExecutePcHealthMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Check(actual == 1 && initial == 3, "repeat ON original");
        await ExecutePcHealthMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Check(actual == 3, "restore original 3");

        // 16. Capture persistence failure prevents any registry modification.
        int writesBefore = writes;
        bool failed = false;
        try
        {
            await ExecutePcHealthMutationAsync(true,
                _ => throw new IOException("backup storage not writable"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && writes == writesBefore, "failed backup no write");

        // 17. OFF without a trustworthy original must not guess DWORD=0.
        failed = false;
        try
        {
            await ExecutePcHealthMutationAsync(false, capture, apply,
                _ => throw new InvalidOperationException("missing original receipt"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && writes == writesBefore, "missing original fails closed");

        // 18. An incomplete registry write triggers rollback.
        actual = 0; initial = null; backedUp = false;
        failed = false;
        try
        {
            await ExecutePcHealthMutationAsync(true, capture,
                _ =>
                {
                    actual = 1;
                    throw new IOException("injected write fault");
                }, restore, CancellationToken.None);
        }
        catch (IOException) { failed = true; }
        Check(failed && actual == 0, "partial write rollback");

        // 19. Wrong DWORD kind/value readback also triggers rollback.
        actual = 0; initial = null; backedUp = false;
        failed = false;
        try
        {
            await ExecutePcHealthMutationAsync(true, capture,
                _ =>
                {
                    actual = 1;
                    throw new InvalidDataException("wrong readback");
                }, restore, CancellationToken.None);
        }
        catch (InvalidDataException) { failed = true; }
        Check(failed && actual == 0, "readback rollback");

        // 20. Mutation error + rollback error are reported together.
        bool bothFailed = false;
        try
        {
            await ExecutePcHealthMutationAsync(true, capture,
                _ => throw new IOException("mutation failed"),
                _ => throw new UnauthorizedAccessException("restore rejected"),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            bothFailed = ex.InnerExceptions.Count == 2 &&
                         ex.InnerExceptions[0] is IOException &&
                         ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(bothFailed, "compound errors");

        // 21. Post-write cancellation restores with uncancelled token.
        actual = 0; initial = null; backedUp = false;
        using var post = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecutePcHealthMutationAsync(true, capture,
                _ =>
                {
                    actual = 1;
                    post.Cancel();
                    return Task.CompletedTask;
                }, restore, post.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && actual == 0, "post-write cancellation");

        // 22-23. Pre-cancelled ON/OFF and mid-capture cancellation do not write.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        int oldWrites = writes, oldCaptures = captures, oldRestores = restores;
        bool preOn = false, preOff = false;
        try { await ExecutePcHealthMutationAsync(true, capture, apply, restore, pre.Token); }
        catch (OperationCanceledException) { preOn = true; }
        try { await ExecutePcHealthMutationAsync(false, capture, apply, restore, pre.Token); }
        catch (OperationCanceledException) { preOff = true; }
        Check(preOn && preOff && writes == oldWrites &&
              captures == oldCaptures && restores == oldRestores, "pre-cancelled");
        using var captureCancelled = new CancellationTokenSource();
        cancelled = false;
        try
        {
            await ExecutePcHealthMutationAsync(true, _ =>
            {
                captureCancelled.Cancel();
                return Task.CompletedTask;
            }, apply, restore, captureCancelled.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && writes == oldWrites, "mid-capture cancellation");
    }
}
