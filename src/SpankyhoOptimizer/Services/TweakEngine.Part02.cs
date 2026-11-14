using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed partial class TweakEngine
{
    private static async Task<IReadOnlyList<string>> ApplyCoreAsync(
        ApplyIntent[] intents, CancellationToken token, IProgress<string>? progress)
    {
        TweakDefinition[] dirty = intents.Select(x => x.Tweak).ToArray();
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        long preflightMs = 0, backupMs = 0, snapshotMs = 0, txBeginMs = 0, backendMs = 0, verifyMs = 0, journalMs = 0, historyMs = 0, txCommitMs = 0;
        var errors = new List<string>();
        // Native-write outcomes must not be reclassified as failed merely
        // because optional audit/history persistence fails afterwards.
        var auditWarnings = new List<string>();
        // Verified native changes remain applied if their audit write fails,
        // but no later item may be applied without a reliable audit trail.
        bool auditStopped = false;
        bool deferredRestart = false;
        bool rolledBack = false;
        bool rollbackSucceeded = true;

        foreach (TweakDefinition blocked in dirty.Where(AiAutomaticMutationGuard.Blocks))
        {
            string reason = AiAutomaticMutationGuard.Reason(blocked);
            blocked.Status = "Ruční správa";
            blocked.ExecutionState = TweakExecutionState.Failed;
            blocked.LastError = reason;
            errors.Add(blocked.Name + ": " + reason);
            LogService.Info("ai-guard", $"BLOCKED automatic mutation id={blocked.Id} section={blocked.Section} group={blocked.Group}");
        }
        if (errors.Count != 0)
            return errors;

        // Even before preflight, reject a queue that no longer represents
        // the values the user selected. Nothing has been written yet.
        if (intents.Any(x => !IntentStillCurrent(x)))
            return new[] { "Výběr tweaků se mezitím změnil. Zkontroluj a potvrď jej znovu." };

        // Fail before capturing backups or opening a registry transaction if
        // distinct UI rows contain duplicate IDs or conflicting native
        // targets. The full hardware/version preflight remains asynchronous.
        var batchConflicts = TweakPreflightService.FindBatchConflicts(dirty, token);
        if (batchConflicts.Any(x => x.Severity.Equals("Block", StringComparison.OrdinalIgnoreCase)))
            return batchConflicts.Where(x => x.Severity.Equals("Block", StringComparison.OrdinalIgnoreCase))
                .Select(x => $"{x.Name}: {x.Message}").ToArray();

        // Do not write a backup for tweaks already known to be unsupported
        // on this Windows build or missing a required OS capability.
        var staticBlocks = TweakPreflightService.FindStaticCompatibilityBlocks(dirty, token);
        if (staticBlocks.Count != 0)
        {
            foreach (var block in staticBlocks)
            {
                var matched = dirty.FirstOrDefault(x => string.Equals(x.Id, block.TweakId, StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                {
                    matched.Status = "Nepodporováno • " + block.Message;
                    matched.ExecutionState = TweakExecutionState.Unsupported;
                    matched.LastError = block.Message;
                }
            }
            return staticBlocks.Select(x => $"{x.Name}: {x.Message}").ToArray();
        }

        // Missing dependencies and incompatible selected values are known
        // without native I/O. Never persist backups for a rejected batch.
        var dependencyBlocks = TweakPreflightService.FindDependencyBlocks(dirty, token);
        if (dependencyBlocks.Count != 0)
            return dependencyBlocks.Select(x => $"{x.Name}: {x.Message}").ToArray();

        progress?.Report("Preflight");
        long phaseStart = totalSw.ElapsedMilliseconds;
        // A dynamic hardware/compatibility rejection must not persist any
        // original-value backups for a batch that will never be applied.
        // Serialize these phases instead of overlapping backup disk I/O
        // with full preflight: fail-closed admission takes priority here.
        var preflight = await TweakPreflightService.ValidateAsync(dirty, token).ConfigureAwait(false);
        preflightMs = totalSw.ElapsedMilliseconds - phaseStart;
        foreach (var warning in preflight.Issues.Where(x => x.Severity.Equals("Warn", StringComparison.OrdinalIgnoreCase)))
            LogService.Info("preflight", $"{warning.TweakId}: {warning.Message}");
        if (!preflight.CanApply)
        {
            foreach (var block in preflight.Issues.Where(x => x.Severity.Equals("Block", StringComparison.OrdinalIgnoreCase)))
            {
                var matched = dirty.FirstOrDefault(x => string.Equals(x.Id, block.TweakId, StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                {
                    bool unsupported = block.Message.Contains("Windows build", StringComparison.OrdinalIgnoreCase) ||
                        block.Message.Contains("compatibility", StringComparison.OrdinalIgnoreCase) ||
                        block.Message.Contains("Hardware scope", StringComparison.OrdinalIgnoreCase) ||
                        block.Message.Contains("provider není", StringComparison.OrdinalIgnoreCase);
                    matched.ExecutionState = unsupported ? TweakExecutionState.Unsupported : TweakExecutionState.Failed;
                    if (unsupported) matched.Status = "Nepodporováno • " + block.Message;
                    matched.LastError = block.Message;
                }
                errors.Add($"{block.Name}: {block.Message}");
            }
            return errors;
        }

        // Avoid even backup persistence if the user restaged a selection
        // while the asynchronous full preflight was running.
        if (intents.Any(x => !IntentStillCurrent(x)))
            return new[] { "Během přípravy se změnila některá hodnota. Dávka nebyla spuštěna." };

        progress?.Report("Backup");
        phaseStart = totalSw.ElapsedMilliseconds;
        await TweakFactory.Backups.SaveOriginalBatchAsync(dirty, token).ConfigureAwait(false);
        backupMs = totalSw.ElapsedMilliseconds - phaseStart;
        progress?.Report("Snapshot");
        // A selection may also change during backup I/O. Never open a native
        // transaction for intents that no longer match the confirmed values.
        if (intents.Any(x => !IntentStillCurrent(x)))
            return new[] { "Během přípravy se změnila některá hodnota. Dávka nebyla spuštěna." };
        phaseStart = totalSw.ElapsedMilliseconds;
        var atomicSnapshot = RegistryAtomicSnapshot.Capture(dirty);
        snapshotMs = totalSw.ElapsedMilliseconds - phaseStart;
        phaseStart = totalSw.ElapsedMilliseconds;
        var transaction = dirty.Length > 0
            ? await WriteAheadTransactionService.BeginItemsAsync(
                intents.Select(x => new WriteAheadTweakItem(
                    x.Tweak.Id, x.Tweak.Name, x.Tweak.Section,
                    x.Previous, x.Expected, x.Tweak.TechnicalTarget)),
                token).ConfigureAwait(false)
            : null;
        txBeginMs = totalSw.ElapsedMilliseconds - phaseStart;
        using var registryBatch = RegistryBatchScope.Begin();
        var applied = new List<RollbackIntent>();
        var batchSnapshot = intents.Select(x =>
            new TweakBatchItem(x.Tweak.Id, x.Tweak.Name, x.Tweak.Section,
                x.Previous, x.Expected)).ToArray();
        progress?.Report("Apply");
        foreach (ApplyIntent intent in intents)
        {
            TweakDefinition tweak = intent.Tweak;
            if (!IntentStillCurrent(intent))
            {
                // A prior item, user interaction or a separate native writer
                // changed this still-unapplied target. Never write a stale
                // target. Roll back only the changes this batch already owns.
                errors.Add(tweak.Name +
                    ": výchozí nebo požadovaná hodnota se mezitím změnila; dávka byla zastavena.");
                if (applied.Count != 0)
                {
                    rolledBack = true;
                    rollbackSucceeded = await RollbackBatchAsync(
                        applied, atomicSnapshot, errors).ConfigureAwait(false);
                }
                break;
            }
            bool expected = intent.Expected;
            bool previous = intent.Previous;
            tweak.IsBusy = true;
            tweak.LastError = null;
            string id = ErrorService.NewCorrelationId();
            using var scope = LogService.BeginScope("tweak:" + tweak.Id, id);
            try
            {
                token.ThrowIfCancellationRequested();
                // Admin/version/hardware compatibility was already validated atomically by preflight.
                var module = OptimizerModuleRuntime.From(tweak);
                tweak.Status = "Aplikuji…";
                phaseStart = totalSw.ElapsedMilliseconds;
                bool elevatedWorker = ShouldUseElevatedWorker(tweak);
                bool workerActual = tweak.Current;
                if (elevatedWorker)
                {
                    var workerResult = await WorkerClientService.RunAsync<TweakWorkerResult>(
                        "tweak-set",
                        WorkerPayloads.Encode(new TweakWorkerRequest(tweak.Id, expected)),
                        TimeSpan.FromMinutes(10),
                        token).ConfigureAwait(false);
                    workerActual = workerResult.Actual;
                }
                else
                {
                    await module.ApplyAsync(expected, token).ConfigureAwait(false);
                }
                backendMs += totalSw.ElapsedMilliseconds - phaseStart;
                progress?.Report("Verify");
                tweak.Status = "Ověřuji…";
                phaseStart = totalSw.ElapsedMilliseconds;
                var (verified, actual) = elevatedWorker
                    ? (workerActual == expected, workerActual)
                    : await VerifyWithRetryAsync(module, tweak.Current, expected, token).ConfigureAwait(false);
                verifyMs += totalSw.ElapsedMilliseconds - phaseStart;
                tweak.Current = actual;
                tweak.LastVerifiedUtc = DateTime.UtcNow;
                if (!verified)
                {
                    // Native verification can outlast a newly staged UI choice;
                    // leave Pending untouched rather than discarding it.
                    tweak.Status = tweak.RestartRequired ? "Čeká na restart" : "Neověřeno";
                    tweak.ExecutionState = tweak.RestartRequired
                        ? TweakExecutionState.RequiresRestart
                        : TweakExecutionState.Failed;
                    if (!tweak.RestartRequired)
                    {
                        string msg = tweak.Name + ": Windows změnu po aplikaci nepotvrdil.";
                        tweak.LastError = msg;
                        errors.Add(msg);
                        rolledBack = true;
                        rollbackSucceeded = await RollbackBatchAsync(
                            applied.Append(new RollbackIntent(tweak, previous, expected)),
                            atomicSnapshot, errors).ConfigureAwait(false);
                        break;
                    }
                    deferredRestart = true;
                    errors.Add(tweak.Name + ": Změna čeká na restart; Windows aktuální stav nepotvrdil.");
                    applied.Add(new RollbackIntent(tweak, previous, expected));
                    continue;
                }
                if (tweak.Pending == expected) tweak.Pending = actual;
                tweak.Status = tweak.RestartRequired ? "Ověřeno • restart" : "Ověřeno";
                tweak.ExecutionState = tweak.RestartRequired
                    ? TweakExecutionState.RequiresRestart
                    : actual ? TweakExecutionState.Applied : TweakExecutionState.NotApplied;
                applied.Add(new RollbackIntent(tweak, previous, expected));
                LogService.Info("tweaks", $"Applied {tweak.Id}={actual}");
                phaseStart = totalSw.ElapsedMilliseconds;
                try
                {
                    await OperationJournalService.WriteAsync(id, "tweak",
                        expected ? "enable" : "disable", tweak.Id,
                        "Verified", tweak.Status, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception auditError)
                {
                    // The actual Windows mutation was already verified.
                    // Do NOT reach the outer catch/rollback for audit I/O.
                    string warning = tweak.Name +
                        ": změna byla ověřena, ale auditní záznam se nepodařilo uložit.";
                    auditWarnings.Add(warning);
                    auditStopped = true;
                    tweak.LastError = warning;
                    tweak.Status = "Ověřeno • audit chybí";
                    LogService.Error("tweaks", "Apply verified audit " + tweak.Id,
                        auditError);
                }
                journalMs += totalSw.ElapsedMilliseconds - phaseStart;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                errors.Add(tweak.Name + ": operace byla zrušena, vracím dávku.");
                await RefreshActualAfterFailureAsync(tweak, expected, CancellationToken.None);
                var cancelRollbackTargets = tweak.IsLoaded && tweak.Current == previous
                    ? (IEnumerable<RollbackIntent>)applied
                    : applied.Append(new RollbackIntent(tweak, previous, expected));
                if (tweak.IsLoaded && tweak.Current == previous)
                {
                    tweak.Status = tweak.Current ? "Aktivní" : "Výchozí / vypnuto";
                    tweak.ExecutionState = tweak.Current ? TweakExecutionState.Applied : TweakExecutionState.NotApplied;
                }
                rolledBack = true;
                rollbackSucceeded = await RollbackBatchAsync(cancelRollbackTargets, atomicSnapshot, errors);
                if (transaction != null)
                {
                    try
                    {
                        await WriteAheadTransactionService.MarkAsync(transaction,
                            rollbackSucceeded ? "RolledBack" : "RollbackFailed",
                            "Apply cancelled").ConfigureAwait(false);
                    }
                    catch (Exception journalError)
                    {
                        // Preserve the original user cancellation. An audit
                        // failure is logged but must not hide a partial native
                        // rollback or replace OperationCanceledException.
                        LogService.Error("tweaks",
                            "Cancelled apply transaction journal " + tweak.Id,
                            journalError);
                    }
                }
                throw;
            }
            catch (Exception ex)
            {
                bool requiresElevation = IsElevationException(ex);
                tweak.LastError = ex.Message;
                tweak.ExecutionState = requiresElevation
                    ? TweakExecutionState.RequiresElevation
                    : TweakExecutionState.Failed;
                tweak.Status = requiresElevation
                    ? "Vyžaduje oprávnění správce • " + id
                    : "Selhalo • " + id;
                errors.Add(tweak.Name + ": " + ex.Message + " [" + id + "]");
                LogService.Error("tweaks", "Apply " + tweak.Id + " id=" + id, ex);
                try { await OperationJournalService.WriteAsync(id, "tweak", expected ? "enable" : "disable", tweak.Id, requiresElevation ? "RequiresElevation" : "Failed", ex.Message, CancellationToken.None); } catch { }
                await RefreshActualAfterFailureAsync(tweak, expected, token);
                var failRollbackTargets = tweak.IsLoaded && tweak.Current == previous
                    ? (IEnumerable<RollbackIntent>)applied
                    : applied.Append(new RollbackIntent(tweak, previous, expected));
                rolledBack = true;
                rollbackSucceeded = await RollbackBatchAsync(failRollbackTargets, atomicSnapshot, errors);
                break;
            }
            finally { tweak.IsBusy = false; }
            // Keep the verified native result, but do not write another tweak
            // after its audit record became unavailable. History contains
            // all requested intents, so a stopped partial batch must skip it.
            if (auditStopped) break;
        }
        if (errors.Count == 0 && !auditStopped) progress?.Report("Commit");
        if (errors.Count == 0 && !auditStopped && batchSnapshot.Length > 0)
        {
            string historyArea = dirty.FirstOrDefault()?.Section ?? "tweaks";
            if (!historyArea.Equals("smoke", StringComparison.OrdinalIgnoreCase))
            {
                phaseStart = totalSw.ElapsedMilliseconds;
                try
                {
                    await TweakBatchHistoryService.AddAsync(
                        historyArea, batchSnapshot, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception historyError)
                {
                    // History is auxiliary. Complete write-ahead ownership
                    // even when the optional UI history cannot be saved.
                    auditWarnings.Add(
                        "Změny Windows byly provedeny, ale historii dávky nelze uložit.");
                    LogService.Error("tweaks", "Apply batch history " + historyArea,
                        historyError);
                }
                historyMs = totalSw.ElapsedMilliseconds - phaseStart;
            }
        }
        if (transaction != null)
        {
            phaseStart = totalSw.ElapsedMilliseconds;
            try
            {
                // Audit loss leaves an incomplete batch requiring manual
                // reconciliation, NOT a fully committed transaction. Prior
                // verified writes remain untouched; do not falsely roll back.
                await WriteAheadTransactionService.MarkAsync(transaction,
                    auditStopped ? "Failed" : errors.Count == 0 ? "Committed" : rolledBack
                        ? (rollbackSucceeded ? "RolledBack" : "RollbackFailed")
                        : deferredRestart ? "PendingRestart" : "Failed",
                    auditStopped ? string.Join(" | ", auditWarnings)
                        : errors.Count == 0 ? "Verified apply" : string.Join(" | ", errors))
                    .ConfigureAwait(false);
            }
            catch (Exception transactionError)
            {
                // Never claim a successful write-ahead finalization when it
                // failed. Native changes must not be re-run from here.
                auditWarnings.Add(
                    "Změny Windows byly provedeny nebo vráceny, ale stav transakčního záznamu nebyl uložen; vyžaduje ověření.");
                LogService.Error("tweaks", "Apply transaction finalization",
                    transactionError);
            }
            txCommitMs = totalSw.ElapsedMilliseconds - phaseStart;
        }
        LogService.Info("performance", $"APPLY_PHASE count={dirty.Length} errors={errors.Count} auditWarnings={auditWarnings.Count} preflightMs={preflightMs} backupMs={backupMs} snapshotMs={snapshotMs} txBeginMs={txBeginMs} backendMs={backendMs} verifyMs={verifyMs} journalMs={journalMs} historyMs={historyMs} txCommitMs={txCommitMs} totalMs={totalSw.ElapsedMilliseconds}");
        if (auditWarnings.Count != 0) errors.AddRange(auditWarnings);
        return errors;
    }

    // Restore-original uses the same queued engine as Apply. Freeze the
    // caller's selection before waiting for the exclusive coordinator; the
    // AI recovery path can pass a not-yet-detected definition, so IsLoaded
    // is compared with its snapshot rather than required to be true.
    private readonly record struct RestoreIntent(
        TweakDefinition Tweak, bool Current, bool Pending, bool IsLoaded);

    private static bool RestoreIntentStillCurrent(RestoreIntent intent) =>
        !intent.Tweak.IsBusy &&
        intent.Tweak.Current == intent.Current &&
        intent.Tweak.Pending == intent.Pending &&
        intent.Tweak.IsLoaded == intent.IsLoaded;

    public Task<IReadOnlyList<string>> RestoreOriginalAsync(IEnumerable<TweakDefinition> tweaks, CancellationToken token = default)
    {
        // Do not silently drop a selected tweak with no original-value
        // revert: the batch must explicitly reject it before any native write.
        RestoreIntent[] intents = tweaks
            // Deduplicate repeated references, not distinct definitions
            // with the same ID; restoration must reject those explicitly.
            .Distinct()
            .Select(x => new RestoreIntent(x, x.Current, x.Pending, x.IsLoaded)).ToArray();
        return OperationCoordinator.RunExclusiveAsync<IReadOnlyList<string>>(
            "tweak-engine",
            ct => RestoreOriginalCoreAsync(intents, ct),
            TimeSpan.FromMinutes(10),
            token);
    }

    private static async Task<IReadOnlyList<string>> RestoreOriginalCoreAsync(RestoreIntent[] intents, CancellationToken token)
    {
        var errors = new List<string>();
        // Fail closed before restoring anything: the original-value backup
        // belongs to an ID, so two different definitions sharing an ID
        // cannot safely restore each other's Windows state.
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRegistryTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RestoreIntent intent in intents)
        {
            token.ThrowIfCancellationRequested();
            TweakDefinition candidate = intent.Tweak;
            if (string.IsNullOrWhiteSpace(candidate.Id) || !seenIds.Add(candidate.Id))
                return new[] { "Obnova zablokována: duplicitní nebo prázdná identita tweaku." };
            if (candidate.RegistryHive is { } hive &&
                !string.IsNullOrWhiteSpace(candidate.RegistryPath) &&
                !string.IsNullOrWhiteSpace(candidate.RegistryValueName) &&
                !seenRegistryTargets.Add(
                    $"{(int)hive}|{candidate.RegistryPath.TrimEnd('\\')}|{candidate.RegistryValueName}"))
                return new[] { "Obnova zablokována: více tweaků míří na stejnou hodnotu registru." };
            // Validate the WHOLE batch, not just the next item. Otherwise a
            // later unsupported restore could strand already-restored items.
            if (candidate.RevertAsync == null)
                return new[] { candidate.Name +
                    ": obnova zablokována: explicitní original-value revert není dostupný." };
        }
        // Reject an already-stale later item before restoring an earlier
        // item. Keep the per-item recheck below for changes during I/O.
        if (intents.Any(intent => !RestoreIntentStillCurrent(intent)))
            return new[] { "Obnova zablokována: výběr nebo stav některého tweaku se během čekání změnil." };

        bool auditFailed = false;
        bool restoreFailed = false;
        foreach (RestoreIntent intent in intents)
        {
            token.ThrowIfCancellationRequested();
            TweakDefinition tweak = intent.Tweak;
            if (!RestoreIntentStillCurrent(intent))
            {
                errors.Add(tweak.Name +
                    ": stav nebo výběr se během čekání změnil, obnova nebyla provedena.");
                break;
            }
            var module = OptimizerModuleRuntime.From(tweak);
            if (!module.CanRestoreOriginal)
            {
                errors.Add(tweak.Name + ": explicitní original-value revert není dostupný.");
                break;
            }

            tweak.IsBusy = true;
            string correlationId = ErrorService.NewCorrelationId();
            using var scope = LogService.BeginScope("tweak-revert:" + tweak.Id, correlationId);
            try
            {
                tweak.Status = "Vracím původní hodnotu…";
                // A loaded UI row can drift while waiting for the coordinator.
                // Fail closed if Windows changed externally, even if the
                // in-memory choice still matches the originally queued intent.
                // AI-owned restored definitions without prior UI detection
                // keep their separate exact-signature ownership checks.
                if (intent.IsLoaded)
                {
                    bool before = await module.VerifyAsync(token).ConfigureAwait(false);
                    if (before != intent.Current ||
                        tweak.Current != intent.Current ||
                        tweak.Pending != intent.Pending)
                        throw new InvalidOperationException(
                            "Skutečný stav Windows nebo výběr se změnil, obnova byla zablokována.");
                }
                bool actual;
                if (ShouldUseElevatedWorker(tweak))
                {
                    var workerResult = await WorkerClientService.RunAsync<TweakWorkerResult>(
                        "tweak-restore",
                        WorkerPayloads.Encode(new TweakWorkerRequest(tweak.Id, tweak.Current)),
                        TimeSpan.FromMinutes(10),
                        token).ConfigureAwait(false);
                    actual = workerResult.Actual;
                }
                else
                {
                    await module.RestoreOriginalAsync(token).ConfigureAwait(false);
                    actual = await module.VerifyAsync(token).ConfigureAwait(false);
                }
                // Do not erase a Pending choice made in the UI while the
                // native restoration was in flight.
                bool userRestaged = tweak.Pending != intent.Pending;
                tweak.Current = actual;
                if (!userRestaged) tweak.Pending = actual;
                tweak.LastVerifiedUtc = DateTime.UtcNow;
                tweak.LastError = null;
                tweak.IsLoaded = true;
                tweak.ExecutionState = tweak.RestartRequired
                    ? TweakExecutionState.RequiresRestart
                    : actual ? TweakExecutionState.Applied : TweakExecutionState.NotApplied;
                tweak.Status = userRestaged
                    ? "Původní hodnota obnovena • nový výběr zachován"
                    : "Původní hodnota obnovena";
                // A failed audit file write must not turn a verified native
                // original-value restoration into a fictitious restore error.
                try
                {
                    await OperationJournalService.WriteAsync(
                        correlationId,
                        "tweak-revert-original",
                        "restore-original",
                        tweak.Id,
                        "Verified",
                        tweak.Status,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception auditError)
                {
                    string auditMessage = tweak.Name +
                        ": původní hodnota obnovena, ale auditní záznam nelze uložit.";
                    errors.Add(auditMessage);
                    tweak.LastError = auditMessage;
                    auditFailed = true;
                    LogService.Error("tweaks",
                        "Restore original audit " + tweak.Id, auditError);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The worker/native write may have completed before the
                // cancellation arrived. Read the real state without the
                // cancelled token; preserve any newer user's Pending choice.
                await TrySynchronizeRestoreStateAsync(
                    tweak, intent.Pending, "restore-original-cancelled").ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                // A failed native restore or its uncertain read-back must not
                // be followed by additional destructive restores in this batch.
                restoreFailed = true;
                bool requiresElevation = IsElevationException(ex);
                tweak.LastError = ex.Message;
                tweak.ExecutionState = requiresElevation
                    ? TweakExecutionState.RequiresElevation
                    : TweakExecutionState.Failed;
                tweak.Status = (requiresElevation ? "Vyžaduje oprávnění správce • " : "Obnova selhala • ") + correlationId;
                errors.Add(tweak.Name + ": " + ex.Message + " [" + correlationId + "]");
                LogService.Error("tweaks", "Restore original " + tweak.Id + " id=" + correlationId, ex);
                await TrySynchronizeRestoreStateAsync(
                    tweak, intent.Pending, "restore-original-failure").ConfigureAwait(false);
                try
                {
                    await OperationJournalService.WriteAsync(
                        correlationId,
                        "tweak-revert-original",
                        "restore-original",
                        tweak.Id,
                        requiresElevation ? "RequiresElevation" : "Failed",
                        ex.Message,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
            finally
            {
                tweak.IsBusy = false;
            }
            // Stop after any failed/uncertain restore or missing audit, rather
            // than applying the rest of the batch to an inconsistent system.
            if (auditFailed || restoreFailed) break;
        }
        return errors;
    }
}
