using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record WriteAheadTweakItem(string Id, string Name, string Section, bool Previous, bool Target);
public sealed record WriteAheadTransaction(string Id, DateTimeOffset CreatedUtc, string State, WriteAheadTweakItem[] Items, string? Detail = null);

public static class WriteAheadTransactionService
{
    private static string Root => Path.Combine(LogService.Root, "transactions");
    private static string PathFor(string id) => Path.Combine(Root, id + ".json");
    private const int MaxTerminalHistory = 48;
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromDays(14);
    private static readonly TimeSpan RecoveryFailureRetention = TimeSpan.FromDays(30);
    private static int _pruneQueued;

    private static void QueuePrune()
    {
        if (Interlocked.Exchange(ref _pruneQueued, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try { PruneTerminalHistory(); }
            finally { Volatile.Write(ref _pruneQueued, 0); }
        });
    }

    private static void PruneTerminalHistory()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var now = DateTimeOffset.UtcNow;
            var terminal = new List<(string File, WriteAheadTransaction Tx)>();
            foreach (string file in Directory.EnumerateFiles(Root, "*.json"))
            {
                try
                {
                    var tx = JsonSerializer.Deserialize<WriteAheadTransaction>(File.ReadAllText(file));
                    if (tx == null || string.Equals(tx.State, "Pending", StringComparison.OrdinalIgnoreCase))
                        continue;
                    terminal.Add((file, tx));
                }
                catch { }
            }

            var ordered = terminal.OrderByDescending(x => x.Tx.CreatedUtc).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                var entry = ordered[i];
                TimeSpan retention = string.Equals(entry.Tx.State, "RecoveryFailed", StringComparison.OrdinalIgnoreCase)
                    ? RecoveryFailureRetention
                    : TerminalRetention;
                bool overCount = i >= MaxTerminalHistory;
                bool expired = now - entry.Tx.CreatedUtc > retention;
                if (!overCount && !expired) continue;
                try { File.Delete(entry.File); } catch { }
            }
        }
        catch (Exception ex) { LogService.Error("transaction", "Transaction history prune failed", ex); }
    }

    public static async Task<WriteAheadTransaction> BeginAsync(IEnumerable<TweakDefinition> tweaks, CancellationToken token = default)
    {
        // Keep retention pruning off the latency-critical Apply path.
        // Terminal history is pruned after MarkAsync and during recovery/startup scans.
        var items = tweaks.Select(x => new WriteAheadTweakItem(x.Id, x.Name, x.Section, x.Current, x.Pending)).ToArray();
        var tx = new WriteAheadTransaction(ErrorService.NewCorrelationId(), DateTimeOffset.UtcNow, "Pending", items);
        Directory.CreateDirectory(Root);
        await AtomicFileService.WriteTextAsync(PathFor(tx.Id), JsonSerializer.Serialize(tx), token).ConfigureAwait(false);
        LogService.Info("transaction", $"WRITE_AHEAD_BEGIN id={tx.Id} items={items.Length}");
        return tx;
    }
    public static async Task MarkAsync(WriteAheadTransaction tx, string state, string? detail = null)
    {
        var updated = tx with { State = state, Detail = detail };
        Directory.CreateDirectory(Root);
        await AtomicFileService.WriteTextAsync(PathFor(tx.Id), JsonSerializer.Serialize(updated)).ConfigureAwait(false);
        LogService.Info("transaction", $"WRITE_AHEAD_{state.ToUpperInvariant()} id={tx.Id} items={tx.Items.Length}");
        if (!string.Equals(state, "Pending", StringComparison.OrdinalIgnoreCase))
            QueuePrune();
    }

    public static WriteAheadTransaction[] Pending()
    {
        try
        {
            PruneTerminalHistory();
            if (!Directory.Exists(Root)) return Array.Empty<WriteAheadTransaction>();
            return Directory.EnumerateFiles(Root, "*.json")
                .Select(file => { try { return JsonSerializer.Deserialize<WriteAheadTransaction>(File.ReadAllText(file)); } catch { return null; } })
                .Where(x => x?.State == "Pending")
                .Cast<WriteAheadTransaction>()
                .OrderBy(x => x.CreatedUtc)
                .ToArray();
        }
        catch { return Array.Empty<WriteAheadTransaction>(); }
    }

    public static async Task<int> RecoverPendingAsync(CancellationToken token = default)
    {
        var pending = Pending();
        if (pending.Length == 0) return 0;
        var definitions = SystemStateSnapshotService.BuildAllTweaks()
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        int recovered = 0;
        foreach (var tx in pending)
        {
            var errors = new List<string>();
            foreach (var item in tx.Items.Reverse())
            {
                token.ThrowIfCancellationRequested();
                if (!definitions.TryGetValue(item.Id, out var tweak))
                {
                    errors.Add(item.Id + ": definice nenalezena");
                    continue;
                }
                try
                {
                    await tweak.SetAsync(item.Previous, token).ConfigureAwait(false);
                    bool actual = await (tweak.VerifyAsync ?? tweak.DetectAsync)(token).ConfigureAwait(false);
                    if (actual != item.Previous) errors.Add(item.Id + ": read-back mismatch");
                }
                catch (Exception ex) { errors.Add(item.Id + ": " + ex.Message); }
            }

            if (errors.Count == 0)
            {
                await MarkAsync(tx, "Recovered", "Recovered after interrupted apply").ConfigureAwait(false);
                recovered++;
            }
            else
            {
                await MarkAsync(tx, "RecoveryFailed", string.Join(" | ", errors)).ConfigureAwait(false);
                LogService.Error("transaction", $"WRITE_AHEAD_RECOVERY_FAIL id={tx.Id} errors={errors.Count}");
            }
        }
        return recovered;
    }

    internal static async Task RunPersistenceSmokeAsync(CancellationToken token = default)
    {
        Directory.CreateDirectory(Root);
        string id = "smoke-" + Guid.NewGuid().ToString("N");
        var tx = new WriteAheadTransaction(id, DateTimeOffset.UtcNow, "Pending", Array.Empty<WriteAheadTweakItem>());
        string path = PathFor(id);
        try
        {
            await AtomicFileService.WriteTextAsync(path, JsonSerializer.Serialize(tx), token).ConfigureAwait(false);
            if (!Pending().Any(x => x.Id == id))
                throw new InvalidOperationException("Write-ahead Pending transaction was not persisted.");
            await MarkAsync(tx, "Committed", "smoke").ConfigureAwait(false);
            if (Pending().Any(x => x.Id == id))
                throw new InvalidOperationException("Committed transaction remained pending.");

            string oldDoneId = "retention-done-" + Guid.NewGuid().ToString("N");
            string oldPendingId = "retention-pending-" + Guid.NewGuid().ToString("N");
            string oldDonePath = PathFor(oldDoneId);
            string oldPendingPath = PathFor(oldPendingId);
            var oldUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(45);
            try
            {
                var oldDone = new WriteAheadTransaction(oldDoneId, oldUtc, "Committed", Array.Empty<WriteAheadTweakItem>());
                var oldPending = new WriteAheadTransaction(oldPendingId, oldUtc, "Pending", Array.Empty<WriteAheadTweakItem>());
                await AtomicFileService.WriteTextAsync(oldDonePath, JsonSerializer.Serialize(oldDone), token).ConfigureAwait(false);
                await AtomicFileService.WriteTextAsync(oldPendingPath, JsonSerializer.Serialize(oldPending), token).ConfigureAwait(false);

                var pendingAfterPrune = Pending();
                if (File.Exists(oldDonePath))
                    throw new InvalidOperationException("Expired terminal transaction was not pruned.");
                if (!File.Exists(oldPendingPath) || !pendingAfterPrune.Any(x => x.Id == oldPendingId))
                    throw new InvalidOperationException("Pending recovery transaction was incorrectly pruned.");
            }
            finally
            {
                try { File.Delete(oldDonePath); } catch { }
                try { File.Delete(oldPendingPath); } catch { }
            }

            LogService.Info("transaction", "WRITE_AHEAD_TRANSACTION_SMOKE_OK retention=verified");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
