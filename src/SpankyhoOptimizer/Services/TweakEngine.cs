using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed class TweakEngine
{
    public Task DetectAsync(
        IEnumerable<TweakDefinition> tweaks, CancellationToken token = default,
        bool resetPending = false)
    {
        var items = tweaks.ToArray();
        // Background reads must never discard a user-edited Pending value.
        // Only explicit post-Apply or cancellation reconciliation resets it.
        return OperationCoordinator.RunExclusiveAsync("tweak-engine",
            ct => DetectCoreAsync(items, ct, resetPending), TimeSpan.FromMinutes(5), token);
    }

    private async Task DetectCoreAsync(
        IEnumerable<TweakDefinition> tweaks, CancellationToken token, bool resetPending)
    {
        var items = tweaks as TweakDefinition[] ?? tweaks.ToArray();
        if (items.Length == 0) return;

        HardwareSnapshot? hardwareSnapshot = null;
        if (items.Any(x => !string.Equals(x.HardwareScope, "Any", StringComparison.OrdinalIgnoreCase)))
            hardwareSnapshot = await HardwareSnapshotService.GetAsync(false, token).ConfigureAwait(false);

        // Partition once instead of Where.ToArray + Except(HashSet).ToArray.
        // Exact old Except semantics: custom commands are de-duplicated by
        // TweakDefinition identity, registry rows preserve their original order.
        List<TweakDefinition>? registry = null;
        List<TweakDefinition>? commands = null;
        HashSet<TweakDefinition>? seenCommands = null;
        foreach (TweakDefinition tweak in items)
        {
            if (tweak.RegistryHive.HasValue && tweak.RegistryPath != null &&
                tweak.RegistryValueName != null && tweak.RegistryEnabledValue.HasValue)
            {
                (registry ??= new List<TweakDefinition>()).Add(tweak);
            }
            else if ((seenCommands ??= new HashSet<TweakDefinition>()).Add(tweak))
            {
                (commands ??= new List<TweakDefinition>()).Add(tweak);
            }
        }
        var hives = registry is null
            ? Array.Empty<IGrouping<RegistryHive, TweakDefinition>>()
            : registry.GroupBy(x => x.RegistryHive!.Value).ToArray();

        // Most pages contain only custom OR registry tweaks. Do not launch an
        // empty parallel branch when that domain has no actual work.
        Task registryTask = hives.Length == 0 ? Task.CompletedTask : Parallel.ForEachAsync(
            hives,
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token },
            (hiveGroup, ct) =>
            {
                DetectRegistryHive(hiveGroup.Key, hiveGroup.ToArray(), hardwareSnapshot, ct, resetPending);
                return ValueTask.CompletedTask;
            });

        Task commandTask = commands is null ? Task.CompletedTask : Parallel.ForEachAsync(
            commands,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 3, 6),
                CancellationToken = token
            },
            (tweak, ct) => new ValueTask(DetectOneAsync(tweak, hardwareSnapshot, ct, resetPending)));

        await Task.WhenAll(registryTask, commandTask).ConfigureAwait(false);
    }

    public Task DetectFreshAsync(IEnumerable<TweakDefinition> tweaks, TimeSpan maxAge, CancellationToken token = default)
    {
        DateTime nowUtc = DateTime.UtcNow;
        var cutoff = nowUtc - maxAge;
        // A cached detection timestamp from the future is not fresh: wall
        // clock rollback must force a new native read, not freeze the row.
        // Most refreshes reuse already-verified tweaks. Avoid a LINQ closure,
        // enumerator and empty result array when an indexed page is fresh.
        // Stale records keep the exact original selection/cancellation path.
        if (tweaks is IReadOnlyList<TweakDefinition> list)
        {
            bool fresh = true;
            for (int i = 0; i < list.Count; i++)
            {
                TweakDefinition tweak = list[i];
                if (!tweak.IsLoaded || !tweak.LastVerifiedUtc.HasValue ||
                    tweak.LastVerifiedUtc.Value < cutoff ||
                    tweak.LastVerifiedUtc.Value > nowUtc)
                {
                    fresh = false;
                    break;
                }
            }
            if (fresh) return Task.CompletedTask;
        }
        var stale = tweaks.Where(x => !x.IsLoaded || !x.LastVerifiedUtc.HasValue ||
            x.LastVerifiedUtc.Value < cutoff || x.LastVerifiedUtc.Value > nowUtc).ToArray();
        return stale.Length == 0 ? Task.CompletedTask : DetectAsync(stale, token);
    }

    private static void DetectRegistryHive(
        RegistryHive hive, TweakDefinition[] tweaks, HardwareSnapshot? hardwareSnapshot,
        CancellationToken token, bool resetPending)
    {
        RegistryKey baseKey;
        try { baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64); }
        catch (Exception ex)
        {
            foreach (var tweak in tweaks) InvalidateDetection(tweak, ex);
            return;
        }
        using (baseKey)
        foreach (var pathGroup in tweaks.GroupBy(x => x.RegistryPath!, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (pathGroup.Key.Contains('\0')) throw new ArgumentException("Registry cesta obsahuje neplatný znak.");
                using RegistryKey? key = baseKey.OpenSubKey(pathGroup.Key, writable: false);
                foreach (var tweak in pathGroup)
                {
                    token.ThrowIfCancellationRequested();
                    var compatibility = TweakCompatibilityService.CheckWithSnapshot(tweak, hardwareSnapshot);
                    if (!compatibility.Supported)
                    {
                        tweak.Status = "Nepodporováno • " + compatibility.Reason;
                        tweak.ExecutionState = TweakExecutionState.Unsupported;
                        tweak.IsLoaded = false; tweak.LastVerifiedUtc = null;
                        continue;
                    }
                    tweak.IsBusy = true;
                    try
                    {
                        if (tweak.RegistryValueName!.Contains('\0')) throw new ArgumentException("Název registry hodnoty obsahuje neplatný znak.");
                        object? raw = key?.GetValue(tweak.RegistryValueName!);
                        bool value = raw != null && key!.GetValueKind(tweak.RegistryValueName!) == RegistryValueKind.DWord &&
                            (Convert.ToInt64(raw) == tweak.RegistryEnabledValue!.Value ||
                             (raw is int rawInt && (uint)rawInt == (uint)tweak.RegistryEnabledValue!.Value));
                        bool preserveUserEdit = !resetPending && tweak.IsLoaded && tweak.IsDirty;
                        tweak.Current = value;
                        if (!preserveUserEdit) tweak.Pending = value;
                        tweak.Status = value ? "Aktivní" : "Výchozí / vypnuto";
                        tweak.ExecutionState = value && tweak.ExecutionState == TweakExecutionState.RequiresRestart
                            ? TweakExecutionState.RequiresRestart
                            : value ? TweakExecutionState.Applied : TweakExecutionState.NotApplied;
                        tweak.IsLoaded = true; tweak.LastVerifiedUtc = DateTime.UtcNow; tweak.LastError = null;
                    }
                    catch (Exception ex) { InvalidateDetection(tweak, ex); }
                    finally { tweak.IsBusy = false; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { foreach (var tweak in pathGroup) InvalidateDetection(tweak, ex); }
        }
    }

    private static void InvalidateDetection(TweakDefinition tweak, Exception error)
    {
        tweak.IsLoaded = false;
        tweak.LastVerifiedUtc = null;
        if (error is NotSupportedException)
        {
            tweak.Status = "Nepodporováno • " + error.Message;
            tweak.ExecutionState = TweakExecutionState.Unsupported;
            tweak.LastError = null;
            return;
        }
        if (IsElevationException(error))
        {
            tweak.Status = "Vyžaduje oprávnění správce";
            tweak.ExecutionState = TweakExecutionState.RequiresElevation;
            tweak.LastError = error.Message;
            LogService.Error("tweaks", "Detect " + tweak.Id, error);
            return;
        }
        tweak.Status = "Nelze zjistit";
        tweak.ExecutionState = TweakExecutionState.Failed;
        tweak.LastError = error.Message;
        LogService.Error("tweaks", "Detect " + tweak.Id, error);
    }

    private static bool IsElevationException(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException ||
        ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 or 5 } ||
        ex.Message.Contains("UAC", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("oprávnění správce", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Přístup byl odepřen", StringComparison.OrdinalIgnoreCase);

    private static async Task DetectOneAsync(
        TweakDefinition tweak, HardwareSnapshot? hardwareSnapshot,
        CancellationToken token, bool resetPending)
    {
        var compatibility = TweakCompatibilityService.CheckWithSnapshot(tweak, hardwareSnapshot);
        if (!compatibility.Supported)
        {
            tweak.Status = "Nepodporováno • " + compatibility.Reason;
            tweak.ExecutionState = TweakExecutionState.Unsupported;
            tweak.IsLoaded = false;
            tweak.LastVerifiedUtc = null;
            return;
        }
        tweak.IsBusy = true;
        try
        {
            bool value = await tweak.DetectAsync(token);
            // Evaluate at completion, not dispatch: the user can toggle a
            // visible row while an async native detection is in flight.
            bool preserveUserEdit = !resetPending && tweak.IsLoaded && tweak.IsDirty;
            tweak.Current = value;
            if (!preserveUserEdit) tweak.Pending = value;
            tweak.Status = value ? "Aktivní" : "Výchozí / vypnuto";
            tweak.ExecutionState = value && tweak.ExecutionState == TweakExecutionState.RequiresRestart
                ? TweakExecutionState.RequiresRestart
                : value ? TweakExecutionState.Applied : TweakExecutionState.NotApplied;
            tweak.IsLoaded = true;
            tweak.LastVerifiedUtc = DateTime.UtcNow;
            tweak.LastError = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            InvalidateDetection(tweak, ex);
        }
        finally { tweak.IsBusy = false; }
    }

    // A target is immutable from user confirmation to its individual write.
    // A queued job must never substitute a later Pending value while
    // waiting for the exclusive engine, restore-point or preflight tasks.
    private readonly record struct ApplyIntent(
        TweakDefinition Tweak, bool Previous, bool Expected);

    // Rollback uses the confirmed apply target, not whatever Pending happens
    // to contain after a concurrent edit in the WinUI row.
    private readonly record struct RollbackIntent(
        TweakDefinition Tweak, bool Previous, bool Expected);

    private static ApplyIntent[] CaptureApplyIntents(IEnumerable<TweakDefinition> tweaks) =>
        tweaks.Where(x => x.IsDirty)
            // Collapse the identical UI instance only; two distinct pending
            // definitions sharing an ID must reach preflight as a conflict.
            .Distinct()
            .Select(x => new ApplyIntent(x, x.Current, x.Pending)).ToArray();

    public Task<IReadOnlyList<string>> ApplyAsync(IEnumerable<TweakDefinition> tweaks, CancellationToken token = default)
    {
        ApplyIntent[] intents = CaptureApplyIntents(tweaks);
        return OperationCoordinator.RunExclusiveAsync<IReadOnlyList<string>>(
            "tweak-engine", ct => ApplyCoreAsync(intents, ct, null), TimeSpan.FromMinutes(15), token);
    }

    public Task<IReadOnlyList<string>> ApplyWithProgressAsync(IEnumerable<TweakDefinition> tweaks, IProgress<string> progress, CancellationToken token = default)
    {
        ApplyIntent[] intents = CaptureApplyIntents(tweaks);
        return OperationCoordinator.RunExclusiveAsync<IReadOnlyList<string>>(
            "tweak-engine", ct => ApplyCoreAsync(intents, ct, progress), TimeSpan.FromMinutes(15), token);
    }

    private static bool IntentStillCurrent(ApplyIntent intent) =>
        intent.Tweak.IsLoaded && !intent.Tweak.IsBusy &&
        intent.Tweak.Current == intent.Previous &&
        intent.Tweak.Pending == intent.Expected &&
        intent.Previous != intent.Expected;

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

    private static async Task<bool> RollbackBatchAsync(IEnumerable<RollbackIntent> items,
        RegistryAtomicSnapshot atomicSnapshot, List<string> errors)
    {
        bool restored = true;
        foreach (var item in items.Reverse())
        {
            try
            {
                item.Tweak.Status = "Vracím změnu…";
                // The atomic snapshot only restores a value if it still
                // matches this batch's exact native write. A registry drift
                // throws and MUST NOT fall through to an unconditional worker
                // or module rollback that would overwrite the foreign value.
                bool restoredRaw = atomicSnapshot.TryRestore(item.Tweak, item.Expected);
                bool actual;
                if (!restoredRaw && ShouldUseElevatedWorker(item.Tweak))
                {
                    var workerResult = await WorkerClientService.RunAsync<TweakWorkerResult>(
                        "tweak-set",
                        WorkerPayloads.Encode(new TweakWorkerRequest(item.Tweak.Id, item.Previous)),
                        TimeSpan.FromMinutes(10),
                        CancellationToken.None).ConfigureAwait(false);
                    actual = workerResult.Actual;
                }
                else
                {
                    var module = OptimizerModuleRuntime.From(item.Tweak);
                    if (!restoredRaw)
                        await module.RollbackAsync(item.Previous, CancellationToken.None).ConfigureAwait(false);
                    actual = await module.VerifyAsync(CancellationToken.None).ConfigureAwait(false);
                }
                item.Tweak.Current = actual;
                // A user may have restaged this row during the async native
                // write or rollback. Reconcile Current without erasing Pending.
                if (item.Tweak.Pending == item.Expected)
                    item.Tweak.Pending = actual;
                item.Tweak.IsLoaded = true;
                item.Tweak.LastVerifiedUtc = DateTime.UtcNow;
                item.Tweak.Status = actual == item.Previous ? "Vráceno po chybě" : "Rollback neověřen";
                item.Tweak.ExecutionState = actual == item.Previous
                    ? (actual ? TweakExecutionState.Applied : TweakExecutionState.NotApplied)
                    : TweakExecutionState.Failed;
                if (actual != item.Previous)
                {
                    restored = false;
                    string mismatch = item.Tweak.Name + ": rollback neobnovil předchozí stav.";
                    item.Tweak.LastError = mismatch;
                    errors.Add(mismatch);
                }
                // Journal I/O is not the native restoration. If it fails
                // after successful read-back, preserve the actual rollback
                // outcome and report audit loss separately.
                try
                {
                    await OperationJournalService.WriteAsync(
                        ErrorService.NewCorrelationId(), "rollback",
                        item.Previous ? "enable" : "disable", item.Tweak.Id,
                        actual == item.Previous ? "Verified" : "Mismatch",
                        item.Tweak.Status, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception auditError)
                {
                    string auditMessage = item.Tweak.Name +
                        ": stav rollbacku ověřen, ale auditní záznam se nepodařilo uložit.";
                    errors.Add(auditMessage);
                    item.Tweak.LastError = auditMessage;
                    LogService.Error("rollback", "Rollback audit " + item.Tweak.Id, auditError);
                }
            }
            catch (Exception ex)
            {
                restored = false;
                string failure = item.Tweak.Name + ": rollback selhal — " + ex.Message;
                errors.Add(failure);
                item.Tweak.LastError = failure;
                item.Tweak.ExecutionState = IsElevationException(ex)
                    ? TweakExecutionState.RequiresElevation
                    : TweakExecutionState.Failed;
                item.Tweak.Status = "Rollback selhal • ověřuji skutečný stav";
                LogService.Error("rollback", "Rollback " + item.Tweak.Id, ex);
                await TrySynchronizeActualStateAsync(
                    item.Tweak, "rollback-failure", item.Expected).ConfigureAwait(false);
            }
        }
        return restored;
    }

    private static async Task<(bool Verified, bool Actual)> VerifyWithRetryAsync(IOptimizerModule module, bool initialActual, bool expected, CancellationToken token)
    {
        bool actual = initialActual;
        // Most registry/API changes settle instantly. The final retry only runs on the
        // failure path and avoids false negatives from delayed Windows state propagation.
        int[] delays = { 0, 220, 650, 1450 };
        foreach (int delay in delays)
        {
            if (delay > 0) await Task.Delay(delay, token);
            actual = await module.VerifyAsync(token);
            if (actual == expected) return (true, actual);
        }
        return (false, actual);
    }

    private static async Task TrySynchronizeRestoreStateAsync(
        TweakDefinition tweak, bool previousSelection, string reason)
    {
        // Cancellation can arrive after a privileged worker performed the
        // native write. Use a fresh, bounded read-only verification. Never
        // turn a failed readback into a claim that original state was restored.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            bool actual = await OptimizerModuleRuntime.From(tweak)
                .VerifyAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            bool userRestaged = tweak.Pending != previousSelection;
            tweak.Current = actual;
            if (!userRestaged) tweak.Pending = actual;
            tweak.IsLoaded = true;
            tweak.LastVerifiedUtc = DateTime.UtcNow;
            // A successful state read does not erase the original restore
            // failure: the caller still needs the native-write error.
            if (reason == "restore-original-cancelled")
                tweak.Status = "Zrušeno • skutečný stav znovu ověřen";
            LogService.Info("tweaks",
                $"RESTORE_STATE_RESYNC_OK id={tweak.Id} reason={reason} actual={actual} preservedPending={userRestaged}");
        }
        catch (Exception ex)
        {
            InvalidateDetection(tweak, ex);
            LogService.Error("tweaks",
                $"RESTORE_STATE_RESYNC_FAIL id={tweak.Id} reason={reason}", ex);
        }
    }

    private static async Task RefreshActualAfterFailureAsync(TweakDefinition tweak, bool expected, CancellationToken token)
    {
        try
        {
            bool actual = await OptimizerModuleRuntime.From(tweak).VerifyAsync(token);
            tweak.Current = actual;
            // Keep a newer user-staged choice intact. Rollback will only
            // synchronize Pending if it still matches this apply's intent.
            tweak.IsLoaded = true;
        }
        catch (Exception ex)
        {
            tweak.LastError = ex.Message;
            LogService.Error("tweaks", "Refresh actual after failure " + tweak.Id, ex);
        }
    }

    private static async Task TrySynchronizeActualStateAsync(
        TweakDefinition tweak, string reason, bool? expectedSelection = null)
    {
        // A failed rollback must not stall the entire batch indefinitely
        // while reading a hung Windows API or privileged module. Verify only,
        // with an independent budget after the caller's token was cancelled.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            bool actual = await OptimizerModuleRuntime.From(tweak)
                .VerifyAsync(budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
            tweak.Current = actual;
            if (!expectedSelection.HasValue || tweak.Pending == expectedSelection.Value)
                tweak.Pending = actual;
            tweak.IsLoaded = true;
            tweak.LastVerifiedUtc = DateTime.UtcNow;
            // Read-back is not proof the intended rollback succeeded: the
            // native mutation already failed or was blocked for safety.
            tweak.Status = "Rollback selhal • skutečný stav ověřen";
            LogService.Info("tweaks", $"STATE_RESYNC_OK id={tweak.Id} reason={reason} actual={actual}");
        }
        catch (Exception verifyEx)
        {
            string? rollbackError = tweak.LastError;
            InvalidateDetection(tweak, verifyEx);
            if (!string.IsNullOrEmpty(rollbackError))
                tweak.LastError = rollbackError + "; ověření skutečného stavu selhalo: " + verifyEx.Message;
            tweak.Status = "Rollback selhal • skutečný stav neověřen";
            LogService.Error("tweaks", $"STATE_RESYNC_FAIL id={tweak.Id} reason={reason}", verifyEx);
        }
    }

    private static bool ShouldUseElevatedWorker(TweakDefinition tweak) =>
        tweak.RequiresAdmin && !WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator();
}
