using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // No HKLM access: exercised using fake calls to the exact production transaction.
    internal static async Task RunRealtekIdleMutationSmokeAsync()
    {
        static void Require(bool condition, string scenario)
        {
            if (!condition)
                throw new InvalidOperationException("REALTEK_IDLE_SMOKE_FAIL: " + scenario);
        }

        var definition = RealtekAudioIdlePowerOff();
        Require(definition.Id == RealtekIdleId && definition.Section == "system" &&
            definition.RevertAsync is not null && definition.RequiresAdmin &&
            !definition.Recommended && RealtekIdleNames.Distinct().Count() == 3,
            "catalog wiring");

        int backups = 0;
        int applies = 0;
        var restored = new List<int>();
        Func<CancellationToken, Task> capture = _ =>
        {
            backups++;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = _ =>
        {
            applies++;
            return Task.CompletedTask;
        };
        Func<int, CancellationToken, Task<bool>> restore = (index, ct) =>
        {
            Require(!ct.IsCancellationRequested, "rollback uses cancelled token");
            restored.Add(index);
            return Task.FromResult(true);
        };
        int[] all = [0, 1, 2];

        // 1. Enable is exactly one group backup and one grouped write.
        await ExecuteRealtekIdleMutationAsync(true, capture, apply, restore, CancellationToken.None);
        Require(backups == 1 && applies == 1 && restored.Count == 0, "enable");

        // 2. OFF must work with saved backup paths and no rediscovery/writes.
        await ExecuteRealtekIdleMutationAsync(false, capture, apply, restore, CancellationToken.None);
        Require(backups == 1 && applies == 1 && restored.SequenceEqual(all), "OFF originals");
        restored.Clear();

        // 3. A broken second write restores all three originals.
        bool partialWrite = false;
        try
        {
            await ExecuteRealtekIdleMutationAsync(true, capture,
                _ => throw new IOException("second write fault"), restore, CancellationToken.None);
        }
        catch (IOException) { partialWrite = true; }
        Require(partialWrite && restored.SequenceEqual(all), "partial write rollback");
        restored.Clear();

        // 4. A failed binary readback restores originals.
        bool readback = false;
        try
        {
            await ExecuteRealtekIdleMutationAsync(true, capture,
                _ => throw new InvalidDataException("binary readback mismatch"), restore, CancellationToken.None);
        }
        catch (InvalidDataException) { readback = true; }
        Require(readback && restored.SequenceEqual(all), "readback rollback");
        restored.Clear();

        // 5. Backup failure stops before the very first write.
        int priorApplies = applies, priorRestores = restored.Count;
        bool backupFault = false;
        try
        {
            await ExecuteRealtekIdleMutationAsync(true,
                _ => throw new IOException("backup file not writable"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { backupFault = true; }
        Require(backupFault && priorApplies == applies && priorRestores == restored.Count,
            "backup persist failed closed");

        // 6. OFF attempts all 3 originals even when first fails and second missing.
        bool independent = false;
        try
        {
            await ExecuteRealtekIdleMutationAsync(false, capture, apply, (i, _) =>
            {
                restored.Add(i);
                if (i == 0) throw new IOException("first restore failed");
                return Task.FromResult(i != 1);
            }, CancellationToken.None);
        }
        catch (AggregateException ex) { independent = ex.InnerExceptions.Count == 2; }
        Require(independent && restored.SequenceEqual(all), "independent restoration");
        restored.Clear();

        // 7. A cancellation after the first simulated write still restores all.
        using var mid = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteRealtekIdleMutationAsync(true, capture, _ =>
            {
                mid.Cancel();
                return Task.CompletedTask;
            }, restore, mid.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && restored.SequenceEqual(all), "cancellation rollback");
        restored.Clear();

        // 8. Failure during enable with missing backup surfaces both failures.
        bool missingOriginal = false;
        try
        {
            await ExecuteRealtekIdleMutationAsync(true, capture,
                _ => throw new IOException("write failed"),
                (i, _) => Task.FromResult(i != 2), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            missingOriginal = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is AggregateException;
        }
        Require(missingOriginal, "missing original surfaced");

        // 9. Rollback exception is retained alongside original write error.
        bool compounded = false;
        try
        {
            await ExecuteRealtekIdleMutationAsync(true, capture,
                _ => throw new InvalidDataException("binary mismatch"),
                (i, _) => i == 0
                    ? Task.FromException<bool>(new IOException("restore fault"))
                    : Task.FromResult(true), CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            compounded = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is InvalidDataException &&
                ex.InnerExceptions[1] is AggregateException;
        }
        Require(compounded, "compound errors");

        // 10. Pre-cancelled enable cannot capture or apply changes.
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        int previousCaptures = backups;
        bool preCancel = false;
        try
        {
            await ExecuteRealtekIdleMutationAsync(true, capture, apply, restore, preCancelled.Token);
        }
        catch (OperationCanceledException) { preCancel = true; }
        Require(preCancel && backups == previousCaptures && applies == priorApplies,
            "pre-cancel enable");

        // 11. Pre-cancelled OFF cannot restore or mutate Windows.
        bool preCancelOff = false;
        try
        {
            await ExecuteRealtekIdleMutationAsync(false, capture, apply, restore, preCancelled.Token);
        }
        catch (OperationCanceledException) { preCancelOff = true; }
        Require(preCancelOff && restored.Count == 0, "pre-cancel OFF");
    }
}
