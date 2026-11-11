using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

// TechnicalTarget is retained in new journals to reject an ID reused for a
// different Windows setting after a catalog upgrade. Old journals remain
// readable and are still subject to conservative native-state admission.
public sealed record WriteAheadTweakItem(
    string Id, string Name, string Section, bool Previous, bool Target,
    string? TechnicalTarget = null);
public sealed record WriteAheadTransaction(string Id, DateTimeOffset CreatedUtc, string State, WriteAheadTweakItem[] Items, string? Detail = null);
public sealed record WriteAheadRecoveryAttention(
    int Pending, int RecoveryFailed, int RollbackFailed,
    int Failed, int PendingRestart, int Unreadable)
{
    public int Total => Pending + RecoveryFailed + RollbackFailed +
        Failed + PendingRestart + Unreadable;
}

public static class WriteAheadTransactionService
{
    private static string Root => Path.Combine(LogService.Root, "transactions");
    private static string PathFor(string id) => Path.Combine(Root, id + ".json");
    private const int MaxTerminalHistory = 48;
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromDays(14);
    private static int _pruneQueued;

    // Only fully verified terminal outcomes are eligible for automatic
    // retention pruning. A failed/incomplete recovery record is evidence
    // for human reconciliation and must never be removed by a count cap.
    internal static bool CanPruneTransactionState(string? state) =>
        state is "Committed" or "RolledBack" or "Recovered";

    internal static bool NeedsManualTransactionReview(string? state) =>
        state is "RecoveryFailed" or "RollbackFailed" or "Failed" or
            "PendingRestart";

    // Pure, production-used retention decision. The ordinal counts ONLY
    // successful terminal records; unresolved records never consume the cap.
    internal static bool MayPruneTransaction(
        WriteAheadTransaction tx, int successfulOrdinal, DateTimeOffset now) =>
        CanPruneTransactionState(tx.State) &&
        (successfulOrdinal >= MaxTerminalHistory ||
         now - tx.CreatedUtc > TerminalRetention);

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
                    var tx = ParseTransactionDocument(
                        Path.GetFileName(file), File.ReadAllText(file));
                    if (!CanPruneTransactionState(tx.State))
                        continue;
                    terminal.Add((file, tx));
                }
                catch { }
            }

            var ordered = terminal.OrderByDescending(x => x.Tx.CreatedUtc).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                var entry = ordered[i];
                if (!MayPruneTransaction(entry.Tx, i, now)) continue;
                try { File.Delete(entry.File); } catch { }
            }
        }
        catch (Exception ex) { LogService.Error("transaction", "Transaction history prune failed", ex); }
    }

    public static Task<WriteAheadTransaction> BeginAsync(
        IEnumerable<TweakDefinition> tweaks, CancellationToken token = default) =>
        BeginItemsAsync(tweaks.Select(x => new WriteAheadTweakItem(
            x.Id, x.Name, x.Section, x.Current, x.Pending,
            x.TechnicalTarget)), token);

    // The engine passes the user-confirmed immutable intent snapshot here.
    // Never read mutable Current/Pending again after waiting for preflight.
    internal static async Task<WriteAheadTransaction> BeginItemsAsync(
        IEnumerable<WriteAheadTweakItem> frozenItems, CancellationToken token = default)
    {
        // Keep retention pruning off the latency-critical Apply path.
        // Terminal history is pruned after MarkAsync and during recovery/startup scans.
        var items = frozenItems.ToArray();
        var tx = new WriteAheadTransaction(ErrorService.NewCorrelationId(), DateTimeOffset.UtcNow, "Pending", items);
        Directory.CreateDirectory(Root);
        await AtomicFileService.WriteTextAsync(PathFor(tx.Id), JsonSerializer.Serialize(tx), token).ConfigureAwait(false);
        LogService.Info("transaction", $"WRITE_AHEAD_BEGIN id={tx.Id} items={items.Length}");
        return tx;
    }
    // Do not allow an old completion callback to replace a newer journal
    // decision. The same lock file is shared by all cooperative processes
    // across the entire verify/write/readback sequence.
    internal static bool MayFinalizeTransaction(
        WriteAheadTransaction expected, WriteAheadTransaction current,
        string requestedState, string? requestedDetail)
    {
        if (!string.Equals(expected.Id, current.Id, StringComparison.OrdinalIgnoreCase) ||
            expected.CreatedUtc != current.CreatedUtc ||
            expected.Items is null || current.Items is null ||
            !expected.Items.SequenceEqual(current.Items))
            return false;
        return requestedState is not ("Pending" or "") &&
            current.State == "Pending" && expected.State == "Pending" &&
            string.Equals(current.Detail, expected.Detail, StringComparison.Ordinal);
    }

    internal static bool IsSameFinalization(
        WriteAheadTransaction expected, WriteAheadTransaction current,
        string requestedState, string? requestedDetail) =>
        string.Equals(expected.Id, current.Id, StringComparison.OrdinalIgnoreCase) &&
        expected.CreatedUtc == current.CreatedUtc &&
        expected.Items is not null && current.Items is not null &&
        expected.Items.SequenceEqual(current.Items) &&
        expected.State == "Pending" &&
        current.State == requestedState &&
        string.Equals(current.Detail, requestedDetail, StringComparison.Ordinal);

    private static async Task<FileStream> AcquireJournalGateAsync(
        string journalPath, CancellationToken token)
    {
        // One stable lock per transaction directory avoids accumulating a
        // permanent sidecar for every batch. Never unlink this shared gate:
        // a competing process might otherwise obtain a different file lock.
        string lockPath = Path.Combine(
            Path.GetDirectoryName(journalPath) ??
                throw new InvalidOperationException("Journal lock directory missing."),
            ".write-ahead.lock");
        long deadline = Environment.TickCount64 + 8000;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
    }

    public static async Task MarkAsync(
        WriteAheadTransaction tx, string state, string? detail = null)
    {
        if (state is not ("Committed" or "RolledBack" or "RollbackFailed" or
            "PendingRestart" or "Failed" or "Recovered" or "RecoveryFailed"))
            throw new ArgumentOutOfRangeException(nameof(state),
                "Transakci lze finalizovat pouze známým koncovým stavem.");
        string path = PathFor(tx.Id);
        await FinalizeJournalFileAsync(path, tx, state, detail,
            CancellationToken.None).ConfigureAwait(false);
        LogService.Info("transaction",
            $"WRITE_AHEAD_{state.ToUpperInvariant()} id={tx.Id} items={tx.Items.Length}");
        QueuePrune();
    }

    // Isolatable production file finalizer: tests can exercise real atomic
    // persistence in a private fixture directory, never the user journal.
    internal static async Task FinalizeJournalFileAsync(
        string path, WriteAheadTransaction tx, string state,
        string? detail, CancellationToken token)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await using (await AcquireJournalGateAsync(path, token)
            .ConfigureAwait(false))
        {
            if (!File.Exists(path))
                throw new InvalidDataException(
                    "Původní transakční journal chybí: " + tx.Id);
            var current = ParseTransactionDocument(
                Path.GetFileName(path), await File.ReadAllTextAsync(path, token)
                    .ConfigureAwait(false));
            if (!MayFinalizeTransaction(tx, current, state, detail))
            {
                if (IsSameFinalization(tx, current, state, detail))
                    return; // Idempotent retry: do not rewrite stable outcome.
                throw new InvalidDataException(
                    "Transakční journal se od zahájení změnil; starý výsledek se nesmí přepsat: " +
                    tx.Id);
            }
            var updated = tx with { State = state, Detail = detail };
            await AtomicFileService.WriteTextAsync(
                path, JsonSerializer.Serialize(updated), token)
                .ConfigureAwait(false);
            var confirmed = ParseTransactionDocument(
                Path.GetFileName(path), await File.ReadAllTextAsync(path, token)
                    .ConfigureAwait(false));
            if (!IsSameFinalization(tx, confirmed, state, detail))
                throw new InvalidDataException(
                    "Finální stav transakčního journalu nebyl potvrzen zpětným čtením: " +
                    tx.Id);
        }
    }

    // Validate before attempting ANY recovery writes. A malformed JSON can
    // represent an interrupted mutation; treating it as "no pending work"
    // would silently hide a possibly altered Windows setting.
    internal static WriteAheadTransaction ParseTransactionDocument(
        string fileName, string json)
    {
        WriteAheadTransaction? tx;
        try { tx = JsonSerializer.Deserialize<WriteAheadTransaction>(json); }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Transakční záznam má neplatný JSON.", ex);
        }
        if (tx is null ||
            !string.Equals(Path.GetFileNameWithoutExtension(fileName),
                tx.Id, StringComparison.OrdinalIgnoreCase) ||
            tx.Items is null || tx.Items.Length > 512 ||
            tx.Items.Any(x => x is null ||
                string.IsNullOrWhiteSpace(x.Id) ||
                string.IsNullOrWhiteSpace(x.Name) ||
                string.IsNullOrWhiteSpace(x.Section)) ||
            tx.State is not ("Pending" or "Committed" or "RolledBack" or
                "RollbackFailed" or "PendingRestart" or "Failed" or
                "Recovered" or "RecoveryFailed"))
            throw new InvalidDataException(
                "Transakční záznam má neplatný identifikátor, stav nebo položky.");
        return tx;
    }

    public static WriteAheadTransaction[] Pending()
    {
        PruneTerminalHistory();
        if (!Directory.Exists(Root)) return Array.Empty<WriteAheadTransaction>();
        var pending = new List<WriteAheadTransaction>();
        int unreadable = 0;
        foreach (string file in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                var tx = ParseTransactionDocument(
                    Path.GetFileName(file), File.ReadAllText(file));
                if (tx.State == "Pending") pending.Add(tx);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or
                UnauthorizedAccessException or JsonException or ArgumentException)
            {
                unreadable++;
                LogService.Error("transaction",
                    "WRITE_AHEAD_JOURNAL_UNREADABLE file=" +
                    Path.GetFileName(file) + " (no automatic writes)", ex);
            }
        }
        if (unreadable != 0)
            throw new InvalidDataException(
                "Nelze ověřit " + unreadable +
                " transakčních záznamů. Automatická obnova byla bezpečně zastavena.");
        return pending.OrderBy(x => x.CreatedUtc).ToArray();
    }

    // Read-only startup visibility: do not conceal unresolved operations
    // just because there are no more Pending records to attempt.
    // Read corrupt metadata as an alert, never as permission to recover.
    public static WriteAheadRecoveryAttention ReadRecoveryAttention()
    {
        if (!Directory.Exists(Root))
            return new WriteAheadRecoveryAttention(0, 0, 0, 0, 0, 0);
        int pending = 0, recoveryFailed = 0, rollbackFailed = 0;
        int failed = 0, pendingRestart = 0, unreadable = 0;
        foreach (string file in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                string state = ParseTransactionDocument(
                    Path.GetFileName(file), File.ReadAllText(file)).State;
                switch (state)
                {
                    case "Pending": pending++; break;
                    case "RecoveryFailed": recoveryFailed++; break;
                    case "RollbackFailed": rollbackFailed++; break;
                    case "Failed": failed++; break;
                    case "PendingRestart": pendingRestart++; break;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or
                UnauthorizedAccessException or JsonException or ArgumentException)
            {
                unreadable++;
                LogService.Error("transaction",
                    "WRITE_AHEAD_ATTENTION_UNREADABLE file=" +
                    Path.GetFileName(file), ex);
            }
        }
        return new WriteAheadRecoveryAttention(
            pending, recoveryFailed, rollbackFailed,
            failed, pendingRestart, unreadable);
    }

    internal enum RecoveryAdmission
    {
        AlreadyOriginal,
        RestoreExactOwnedDword,
        PreserveUnownedValue
    }

    // A Pending journal only proves intent; it does NOT prove that the native
    // write ever started. Restore only when our exact DWORD-on signature is
    // visible, otherwise leave Windows untouched for manual reconciliation.
    internal static RecoveryAdmission PlanRecovery(
        WriteAheadTweakItem item, TweakDefinition tweak,
        bool currentEnabled, bool exactAppliedDword)
    {
        if (!string.Equals(item.Id, tweak.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(item.Name, tweak.Name, StringComparison.Ordinal) ||
            !string.Equals(item.Section, tweak.Section, StringComparison.Ordinal) ||
            (item.TechnicalTarget is not null &&
             !string.Equals(item.TechnicalTarget, tweak.TechnicalTarget, StringComparison.Ordinal)))
            return RecoveryAdmission.PreserveUnownedValue;

        if (currentEnabled == item.Previous)
            return RecoveryAdmission.AlreadyOriginal;

        // Legacy Pending journals lack a stable technical target fingerprint.
        // They can be acknowledged when already restored, but may never
        // authorize a NEW registry write to a possibly repurposed tweak ID.
        if (string.IsNullOrWhiteSpace(item.TechnicalTarget))
            return RecoveryAdmission.PreserveUnownedValue;

        if (!item.Previous && item.Target && currentEnabled &&
            exactAppliedDword &&
            tweak.SupportsSafeCrashRecovery &&
            tweak.RegistryHive.HasValue &&
            tweak.RegistryPath is not null &&
            tweak.RegistryValueName is not null &&
            tweak.RegistryEnabledValue is >= 0 and <= uint.MaxValue &&
            string.Equals(tweak.RevertMode, "OriginalRegistryValue", StringComparison.Ordinal))
            return RecoveryAdmission.RestoreExactOwnedDword;

        // OFF, non-registry and unknown signatures cannot prove ownership.
        return RecoveryAdmission.PreserveUnownedValue;
    }

    private static bool HasExactAppliedDword(TweakDefinition tweak)
    {
        if (!tweak.RegistryHive.HasValue ||
            string.IsNullOrEmpty(tweak.RegistryPath) ||
            string.IsNullOrEmpty(tweak.RegistryValueName) ||
            !tweak.RegistryEnabledValue.HasValue)
            return false;
        using var root = RegistryKey.OpenBaseKey(tweak.RegistryHive.Value, RegistryView.Registry64);
        using var key = root.OpenSubKey(tweak.RegistryPath, writable: false);
        if (key is null || !key.GetValueNames().Contains(
                tweak.RegistryValueName, StringComparer.OrdinalIgnoreCase))
            return false;
        return key.GetValueKind(tweak.RegistryValueName) == RegistryValueKind.DWord &&
            key.GetValue(tweak.RegistryValueName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is int current &&
            unchecked((uint)current) == tweak.RegistryEnabledValue.Value;
    }

    // A Boolean detection is not proof that a DWORD registry value has
    // returned to its backed-up kind/content/absence and parent-key state.
    // Unknown/custom providers remain manual-recovery only.
    private static Task<bool> HasExactBackedUpOriginalAsync(
        TweakDefinition tweak, CancellationToken token)
    {
        if (!tweak.SupportsSafeCrashRecovery ||
            !tweak.RegistryHive.HasValue ||
            string.IsNullOrWhiteSpace(tweak.RegistryPath) ||
            string.IsNullOrWhiteSpace(tweak.RegistryValueName))
            return Task.FromResult(false);
        return TweakFactory.Backups.IsOriginalStateRestoredAsync(
            tweak.Id, tweak.RegistryHive.Value, tweak.RegistryPath,
            tweak.RegistryValueName, token);
    }

    // A later journal for the same tweak can represent a user-approved
    // change after the old Pending batch. Restoring that old snapshot would
    // then undo newer intent, even when the raw DWORD signature happens to
    // match. Two still-Pending batches for the same tweak are also ambiguous.
    internal static string? FindRecoveryJournalConflict(
        WriteAheadTransaction tx, IReadOnlyList<WriteAheadTransaction> all)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tx.Items)
        {
            if (!ids.Add(item.Id))
                return "Dávka obsahuje stejný tweak vícekrát: " + item.Id;
        }
        foreach (var other in all)
        {
            if (string.Equals(other.Id, tx.Id, StringComparison.OrdinalIgnoreCase))
                continue;
            // Even an older unresolved FAILED journal can contain a
            // partially applied write. Its ownership is ambiguous until
            // manually reconciled, unlike an older verified terminal row.
            if (other.State != "Pending" &&
                !NeedsManualTransactionReview(other.State) &&
                other.CreatedUtc < tx.CreatedUtc)
                continue;
            foreach (var item in other.Items)
                if (ids.Contains(item.Id))
                    return "Jiná nedokončená nebo novější transakce měnila tweak " +
                        item.Id + "; automatické vrácení staršího stavu není bezpečné.";
        }
        return null;
    }

    private static WriteAheadTransaction[] ReadJournalRecoverySnapshot()
    {
        if (!Directory.Exists(Root))
            return Array.Empty<WriteAheadTransaction>();
        var all = new List<WriteAheadTransaction>();
        // Fail closed on even ONE unreadable record. Never consider a partial
        // filesystem listing authoritative enough to roll back Windows.
        foreach (string file in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                all.Add(ParseTransactionDocument(
                    Path.GetFileName(file), File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or
                UnauthorizedAccessException or JsonException or ArgumentException)
            {
                throw new InvalidDataException(
                    "Nelze ověřit transakční historii pro bezpečnou obnovu: " +
                    Path.GetFileName(file), ex);
            }
        }
        return all.ToArray();
    }

    internal static bool MatchesPendingRecoverySnapshot(
        IReadOnlyList<WriteAheadTransaction> pending,
        IReadOnlyList<WriteAheadTransaction> all)
    {
        var stillPending = all.Where(t => t.State == "Pending").ToArray();
        if (pending.Count != stillPending.Length) return false;
        foreach (var tx in pending)
        {
            var matches = stillPending.Where(x =>
                string.Equals(x.Id, tx.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1 ||
                matches[0].CreatedUtc != tx.CreatedUtc ||
                !matches[0].Items.SequenceEqual(tx.Items))
                return false;
        }
        return true;
    }

    public static async Task<int> RecoverPendingAsync(CancellationToken token = default)
    {
        var pending = Pending();
        if (pending.Length == 0) return 0;
        var all = ReadJournalRecoverySnapshot();
        if (!MatchesPendingRecoverySnapshot(pending, all))
            throw new InvalidDataException(
                "Transakční journal se během přípravy obnovy změnil; " +
                "bezpečná automatická obnova byla zastavena.");
        var definitions = SystemStateSnapshotService.BuildAllTweaks()
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        // Conflicted batches are marked as RecoveryFailed without invoking
        // the Windows provider. Other independent batches remain recoverable.
        int recovered = 0;
        foreach (var tx in pending)
        {
            token.ThrowIfCancellationRequested();
            string? conflict = FindRecoveryJournalConflict(tx, all);
            if (conflict is not null)
            {
                await MarkAsync(tx, "RecoveryFailed", conflict)
                    .ConfigureAwait(false);
                LogService.Error("transaction",
                    "WRITE_AHEAD_RECOVERY_CONFLICT id=" + tx.Id + " " + conflict);
                continue;
            }
            recovered += await RecoverTransactionsAsync(
                new[] { tx }, definitions,
                static (t, ct) => (t.VerifyAsync ?? t.DetectAsync)(ct),
                static t => HasExactAppliedDword(t),
                static (t, ct) => HasExactBackedUpOriginalAsync(t, ct),
                static (t, enabled, ct) => t.SetAsync(enabled, ct),
                static (tx1, state, detail, ct) => MarkAsync(tx1, state, detail),
                token).ConfigureAwait(false);
        }
        return recovered;
    }

    // Shared production transaction finalizer. Journal failure cannot be
    // reported as a completed recovery, and a replay can safely verify an
    // already restored original before persisting the final outcome.
    internal static async Task<int> RecoverTransactionsAsync(
        IReadOnlyList<WriteAheadTransaction> pending,
        IReadOnlyDictionary<string, TweakDefinition> definitions,
        Func<TweakDefinition, CancellationToken, Task<bool>> readCurrent,
        Func<TweakDefinition, bool> matchesApplied,
        Func<TweakDefinition, CancellationToken, Task<bool>> matchesOriginal,
        Func<TweakDefinition, bool, CancellationToken, Task> restore,
        Func<WriteAheadTransaction, string, string?, CancellationToken, Task> persist,
        CancellationToken token)
    {
        int recovered = 0;
        foreach (var tx in pending)
        {
            token.ThrowIfCancellationRequested();
            var errors = await RecoverOneTransactionAsync(
                tx, definitions, readCurrent, matchesApplied,
                matchesOriginal, restore, token).ConfigureAwait(false);
            string state = errors.Count == 0 ? "Recovered" : "RecoveryFailed";
            string detail = errors.Count == 0
                ? "Verified after interrupted apply" : string.Join(" | ", errors);

            try
            {
                token.ThrowIfCancellationRequested();
                await persist(tx, state, detail, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The Pending record must remain retryable if its final state
                // could not be durably recorded. Do not count it as recovered.
                LogService.Error("transaction",
                    $"WRITE_AHEAD_FINALIZE_FAIL id={tx.Id} intended={state}", ex);
                throw;
            }
            if (errors.Count == 0) recovered++;
            else
                LogService.Error("transaction",
                    $"WRITE_AHEAD_RECOVERY_FAIL id={tx.Id} errors={errors.Count}");
        }
        return recovered;
    }

    // The production coordinator and isolated tests use the exact same
    // recovery flow. Its dependencies are explicit: fake providers can
    // simulate a power loss, registry drift, failed restore or cancellation
    // without ever opening the Windows registry.
    internal static async Task<List<string>> RecoverOneTransactionAsync(
        WriteAheadTransaction tx,
        IReadOnlyDictionary<string, TweakDefinition> definitions,
        Func<TweakDefinition, CancellationToken, Task<bool>> readCurrent,
        Func<TweakDefinition, bool> matchesApplied,
        Func<TweakDefinition, CancellationToken, Task<bool>> matchesOriginal,
        Func<TweakDefinition, bool, CancellationToken, Task> restore,
        CancellationToken token)
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
                bool before = await readCurrent(tweak, token).ConfigureAwait(false);
                bool exactApplied = !item.Previous && item.Target && before &&
                    matchesApplied(tweak);
                RecoveryAdmission plan = PlanRecovery(item, tweak, before, exactApplied);
                switch (plan)
                {
                    case RecoveryAdmission.AlreadyOriginal:
                        // A Boolean OFF is not proof of the exact first-original.
                        if (!await matchesOriginal(tweak, token).ConfigureAwait(false))
                            errors.Add(item.Id +
                                ": logický stav souhlasí, ale původní registr a záloha se neshodují; automatické potvrzení odmítnuto.");
                        continue;
                    case RecoveryAdmission.PreserveUnownedValue:
                        errors.Add(item.Id +
                            ": stav nebo cíl se od posledního běhu změnil; " +
                            "automatická obnova odmítnuta, Windows ponechán beze změny.");
                        continue;
                    case RecoveryAdmission.RestoreExactOwnedDword:
                        token.ThrowIfCancellationRequested();
                        // Native provider also checks the applied signature
                        // immediately before mutation. Never bypass ownership.
                        if (!matchesApplied(tweak))
                            throw new InvalidOperationException(
                                "Registry hodnota se před obnovou změnila.");
                        await restore(tweak, item.Previous, token).ConfigureAwait(false);
                        bool actual = await readCurrent(tweak, token).ConfigureAwait(false);
                        if (actual != item.Previous ||
                            !await matchesOriginal(tweak, token).ConfigureAwait(false))
                            errors.Add(item.Id +
                                ": původní registry hodnota nebo její typ nebyly obnoveny podle zálohy.");
                        break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(item.Id + ": " + ex.GetBaseException().Message);
            }
        }
        return errors;
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
