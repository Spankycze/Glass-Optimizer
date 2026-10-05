using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed class TweakEngine
{
    public async Task DetectAsync(IEnumerable<TweakDefinition> tweaks, CancellationToken token = default)
    {
        var items = tweaks as TweakDefinition[] ?? tweaks.ToArray();
        if (items.Length == 0) return;

        HardwareSnapshot? hardwareSnapshot = null;
        if (items.Any(x => !string.Equals(x.HardwareScope, "Any", StringComparison.OrdinalIgnoreCase)))
            hardwareSnapshot = await HardwareSnapshotService.GetAsync(false, token).ConfigureAwait(false);

        var registry = items.Where(x => x.RegistryHive.HasValue && x.RegistryPath != null && x.RegistryValueName != null && x.RegistryEnabledValue.HasValue).ToArray();
        var commands = items.Except(registry).ToArray();
        var hives = registry.GroupBy(x => x.RegistryHive!.Value).ToArray();

        var registryTask = Parallel.ForEachAsync(
            hives,
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token },
            (hiveGroup, ct) =>
            {
                DetectRegistryHive(hiveGroup.Key, hiveGroup.ToArray(), hardwareSnapshot, ct);
                return ValueTask.CompletedTask;
            });

        int commandParallelism = Math.Clamp(Environment.ProcessorCount / 2, 3, 6);
        var detectSw = System.Diagnostics.Stopwatch.StartNew();
        var commandTask = Parallel.ForEachAsync(
            commands,
            new ParallelOptions { MaxDegreeOfParallelism = commandParallelism, CancellationToken = token },
            async (tweak, ct) =>
            {
                using var perf = PerformanceTelemetryService.Measure("tweak", "detect:" + tweak.Id);
                await DetectOneAsync(tweak, hardwareSnapshot, ct).ConfigureAwait(false);
            });

        await Task.WhenAll(registryTask, commandTask).ConfigureAwait(false);
        if (detectSw.ElapsedMilliseconds >= 40)
            LogService.Info("performance",
                $"TWEAK_DETECT_BATCH count={items.Length} registry={registry.Length} custom={commands.Length} customParallel={commandParallelism} totalMs={detectSw.ElapsedMilliseconds}");
    }

    public Task DetectFreshAsync(IEnumerable<TweakDefinition> tweaks, TimeSpan maxAge, CancellationToken token = default)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var stale = tweaks.Where(x => !x.IsLoaded || !x.LastVerifiedUtc.HasValue || x.LastVerifiedUtc.Value < cutoff).ToArray();
        return stale.Length == 0 ? Task.CompletedTask : DetectAsync(stale, token);
    }

    private static void DetectRegistryHive(RegistryHive hive, TweakDefinition[] tweaks, HardwareSnapshot? hardwareSnapshot, CancellationToken token)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        foreach (var pathGroup in tweaks.GroupBy(x => x.RegistryPath!, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            using RegistryKey? key = baseKey.OpenSubKey(pathGroup.Key, writable: false);
            foreach (var tweak in pathGroup)
            {
                token.ThrowIfCancellationRequested();
                var compatibility = TweakCompatibilityService.CheckWithSnapshot(tweak, hardwareSnapshot);
                if (!compatibility.Supported) { tweak.Status = "Nepodporováno • " + compatibility.Reason; tweak.IsLoaded = false; continue; }
                tweak.IsBusy = true;
                try
                {
                    object? raw = key?.GetValue(tweak.RegistryValueName!);
                    bool value = raw != null && Convert.ToInt64(raw) == tweak.RegistryEnabledValue!.Value;
                    tweak.Current = value; tweak.Pending = value; tweak.Status = value ? "Aktivní" : "Výchozí / vypnuto";
                    tweak.IsLoaded = true; tweak.LastVerifiedUtc = DateTime.UtcNow; tweak.LastError = null;
                }
                catch (Exception ex)
                {
                    tweak.Status = "Nelze zjistit"; tweak.LastError = ex.Message; LogService.Error("tweaks", "Batch detect " + tweak.Id, ex);
                }
                finally { tweak.IsBusy = false; }
            }
        }
    }

    private static async Task DetectOneAsync(TweakDefinition tweak, HardwareSnapshot? hardwareSnapshot, CancellationToken token)
    {
        var compatibility = TweakCompatibilityService.CheckWithSnapshot(tweak, hardwareSnapshot);
        if (!compatibility.Supported)
        {
            tweak.Status = "Nepodporováno • " + compatibility.Reason;
            tweak.IsLoaded = false;
            return;
        }
        tweak.IsBusy = true;
        try
        {
            bool value = await tweak.DetectAsync(token);
            tweak.Current = value;
            tweak.Pending = value;
            tweak.Status = value ? "Aktivní" : "Výchozí / vypnuto";
            tweak.IsLoaded = true;
            tweak.LastVerifiedUtc = DateTime.UtcNow;
            tweak.LastError = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            tweak.Status = "Nelze zjistit";
            tweak.LastError = ex.Message;
            LogService.Error("tweaks", "Detect " + tweak.Id, ex);
        }
        finally { tweak.IsBusy = false; }
    }

    public Task<IReadOnlyList<string>> ApplyAsync(IEnumerable<TweakDefinition> tweaks, CancellationToken token = default)
    {
        var items = tweaks.Where(x => x.IsDirty).ToArray();
        return OperationCoordinator.RunExclusiveAsync<IReadOnlyList<string>>(
            "tweak-engine", ct => ApplyCoreAsync(items, ct, null), TimeSpan.FromMinutes(15), token);
    }

    public Task<IReadOnlyList<string>> ApplyWithProgressAsync(IEnumerable<TweakDefinition> tweaks, IProgress<string> progress, CancellationToken token = default)
    {
        var items = tweaks.Where(x => x.IsDirty).ToArray();
        return OperationCoordinator.RunExclusiveAsync<IReadOnlyList<string>>(
            "tweak-engine", ct => ApplyCoreAsync(items, ct, progress), TimeSpan.FromMinutes(15), token);
    }

    private static async Task<IReadOnlyList<string>> ApplyCoreAsync(TweakDefinition[] dirty, CancellationToken token, IProgress<string>? progress)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        long preflightMs = 0, backupMs = 0, snapshotMs = 0, txBeginMs = 0, backendMs = 0, verifyMs = 0, journalMs = 0, historyMs = 0, txCommitMs = 0;
        var errors = new List<string>();

        foreach (TweakDefinition blocked in dirty.Where(AiAutomaticMutationGuard.Blocks))
        {
            string reason = AiAutomaticMutationGuard.Reason(blocked);
            blocked.Status = "Ruční správa";
            blocked.LastError = reason;
            errors.Add(blocked.Name + ": " + reason);
            LogService.Info("ai-guard", $"BLOCKED automatic mutation id={blocked.Id} section={blocked.Section} group={blocked.Group}");
        }
        if (errors.Count != 0)
            return errors;

        progress?.Report("Preflight");
        long phaseStart = totalSw.ElapsedMilliseconds;
        long backupStart = phaseStart;
        // Backup capture is independent from compatibility preflight, so overlap both cold-path operations.
        var backupTask = TweakFactory.Backups.SaveOriginalBatchAsync(dirty, token);
        var preflight = await TweakPreflightService.ValidateAsync(dirty, token);
        preflightMs = totalSw.ElapsedMilliseconds - phaseStart;
        foreach (var warning in preflight.Issues.Where(x => x.Severity.Equals("Warn", StringComparison.OrdinalIgnoreCase)))
            LogService.Info("preflight", $"{warning.TweakId}: {warning.Message}");
        if (!preflight.CanApply)
        {
            try { await backupTask; } catch (OperationCanceledException) { throw; }
            backupMs = totalSw.ElapsedMilliseconds - backupStart;
            errors.AddRange(preflight.Issues.Where(x => x.Severity.Equals("Block", StringComparison.OrdinalIgnoreCase))
                .Select(x => $"{x.Name}: {x.Message}"));
            return errors;
        }

        progress?.Report("Snapshot");
        await backupTask;
        backupMs = totalSw.ElapsedMilliseconds - backupStart;
        phaseStart = totalSw.ElapsedMilliseconds;
        var atomicSnapshot = RegistryAtomicSnapshot.Capture(dirty);
        snapshotMs = totalSw.ElapsedMilliseconds - phaseStart;
        phaseStart = totalSw.ElapsedMilliseconds;
        var transaction = dirty.Length > 0
            ? await WriteAheadTransactionService.BeginAsync(dirty, token).ConfigureAwait(false)
            : null;
        txBeginMs = totalSw.ElapsedMilliseconds - phaseStart;
        using var registryBatch = RegistryBatchScope.Begin();
        var applied = new List<(TweakDefinition Tweak, bool Previous)>();
        var batchSnapshot = dirty.Select(x => new TweakBatchItem(x.Id, x.Name, x.Section, x.Current, x.Pending)).ToArray();
        progress?.Report("Apply");
        foreach (var tweak in dirty)
        {
            token.ThrowIfCancellationRequested();
            bool expected = tweak.Pending;
            bool previous = tweak.Current;
            tweak.IsBusy = true;
            tweak.LastError = null;
            string id = ErrorService.NewCorrelationId();
            using var scope = LogService.BeginScope("tweak:" + tweak.Id, id);
            try
            {
                // Admin/version/hardware compatibility was already validated atomically by preflight.
                var module = OptimizerModuleRuntime.From(tweak);
                tweak.Status = "Aplikuji…";
                phaseStart = totalSw.ElapsedMilliseconds;
                await module.ApplyAsync(expected, token);
                backendMs += totalSw.ElapsedMilliseconds - phaseStart;
                progress?.Report("Verify");
                tweak.Status = "Ověřuji…";
                phaseStart = totalSw.ElapsedMilliseconds;
                var (verified, actual) = await VerifyWithRetryAsync(module, tweak.Current, expected, token);
                verifyMs += totalSw.ElapsedMilliseconds - phaseStart;
                tweak.Current = actual;
                tweak.LastVerifiedUtc = DateTime.UtcNow;
                if (!verified)
                {
                    tweak.Pending = expected;
                    tweak.Status = tweak.RestartRequired ? "Čeká na restart" : "Neověřeno";
                    if (!tweak.RestartRequired)
                    {
                        string msg = tweak.Name + ": read-back nepotvrdil změnu.";
                        tweak.LastError = msg;
                        errors.Add(msg);
                        await RollbackBatchAsync(applied.Append((tweak, previous)), atomicSnapshot, errors);
                        break;
                    }
                    continue;
                }
                tweak.Pending = actual;
                tweak.Status = tweak.RestartRequired ? "Ověřeno • restart" : "Ověřeno";
                applied.Add((tweak, previous));
                LogService.Info("tweaks", $"Applied {tweak.Id}={actual}");
                phaseStart = totalSw.ElapsedMilliseconds;
                await OperationJournalService.WriteAsync(id, "tweak", expected ? "enable" : "disable", tweak.Id, "Verified", tweak.Status, CancellationToken.None);
                journalMs += totalSw.ElapsedMilliseconds - phaseStart;
            }
            catch (OperationCanceledException)
            {
                errors.Add(tweak.Name + ": operace byla zrušena, vracím dávku.");
                await RollbackBatchAsync(applied.Append((tweak, previous)), atomicSnapshot, errors);
                if (transaction != null)
                    await WriteAheadTransactionService.MarkAsync(transaction, "RolledBack", "Apply cancelled").ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                tweak.LastError = ex.Message;
                tweak.Status = "Selhalo • " + id;
                errors.Add(tweak.Name + ": " + ex.Message + " [" + id + "]");
                LogService.Error("tweaks", "Apply " + tweak.Id + " id=" + id, ex);
                try { await OperationJournalService.WriteAsync(id, "tweak", expected ? "enable" : "disable", tweak.Id, "Failed", ex.Message, CancellationToken.None); } catch { }
                await RefreshActualAfterFailureAsync(tweak, expected, token);
                await RollbackBatchAsync(applied.Append((tweak, previous)), atomicSnapshot, errors);
                break;
            }
            finally { tweak.IsBusy = false; }
        }
        if (errors.Count == 0) progress?.Report("Commit");
        if (errors.Count == 0 && batchSnapshot.Length > 0)
        {
            string historyArea = dirty.FirstOrDefault()?.Section ?? "tweaks";
            if (!historyArea.Equals("smoke", StringComparison.OrdinalIgnoreCase))
            {
                phaseStart = totalSw.ElapsedMilliseconds;
                await TweakBatchHistoryService.AddAsync(historyArea, batchSnapshot, CancellationToken.None);
                historyMs = totalSw.ElapsedMilliseconds - phaseStart;
            }
        }
        if (transaction != null)
        {
            phaseStart = totalSw.ElapsedMilliseconds;
            await WriteAheadTransactionService.MarkAsync(transaction,
                errors.Count == 0 ? "Committed" : "RolledBack",
                errors.Count == 0 ? "Verified apply" : string.Join(" | ", errors)).ConfigureAwait(false);
            txCommitMs = totalSw.ElapsedMilliseconds - phaseStart;
        }
        LogService.Info("performance", $"APPLY_PHASE count={dirty.Length} errors={errors.Count} preflightMs={preflightMs} backupMs={backupMs} snapshotMs={snapshotMs} txBeginMs={txBeginMs} backendMs={backendMs} verifyMs={verifyMs} journalMs={journalMs} historyMs={historyMs} txCommitMs={txCommitMs} totalMs={totalSw.ElapsedMilliseconds}");
        return errors;
    }

    public Task<IReadOnlyList<string>> RestoreOriginalAsync(IEnumerable<TweakDefinition> tweaks, CancellationToken token = default)
    {
        var items = tweaks.Where(x => x.RevertAsync != null).ToArray();
        return OperationCoordinator.RunExclusiveAsync<IReadOnlyList<string>>(
            "tweak-engine-revert-original",
            ct => RestoreOriginalCoreAsync(items, ct),
            TimeSpan.FromMinutes(10),
            token);
    }

    private static async Task<IReadOnlyList<string>> RestoreOriginalCoreAsync(TweakDefinition[] items, CancellationToken token)
    {
        var errors = new List<string>();
        foreach (var tweak in items)
        {
            token.ThrowIfCancellationRequested();
            var module = OptimizerModuleRuntime.From(tweak);
            if (!module.CanRestoreOriginal)
            {
                errors.Add(tweak.Name + ": explicitní original-value revert není dostupný.");
                continue;
            }

            tweak.IsBusy = true;
            string correlationId = ErrorService.NewCorrelationId();
            using var scope = LogService.BeginScope("tweak-revert:" + tweak.Id, correlationId);
            try
            {
                tweak.Status = "Vracím původní hodnotu…";
                await module.RestoreOriginalAsync(token).ConfigureAwait(false);
                bool actual = await module.VerifyAsync(token).ConfigureAwait(false);
                tweak.Current = actual;
                tweak.Pending = actual;
                tweak.LastVerifiedUtc = DateTime.UtcNow;
                tweak.LastError = null;
                tweak.Status = "Původní hodnota obnovena";
                await OperationJournalService.WriteAsync(
                    correlationId,
                    "tweak-revert-original",
                    "restore-original",
                    tweak.Id,
                    "Verified",
                    tweak.Status,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                tweak.LastError = ex.Message;
                tweak.Status = "Obnova selhala • " + correlationId;
                errors.Add(tweak.Name + ": " + ex.Message + " [" + correlationId + "]");
                LogService.Error("tweaks", "Restore original " + tweak.Id + " id=" + correlationId, ex);
                await TrySynchronizeActualStateAsync(tweak, "restore-original-failure").ConfigureAwait(false);
                try
                {
                    await OperationJournalService.WriteAsync(
                        correlationId,
                        "tweak-revert-original",
                        "restore-original",
                        tweak.Id,
                        "Failed",
                        ex.Message,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
            finally
            {
                tweak.IsBusy = false;
            }
        }
        return errors;
    }

    private static async Task RollbackBatchAsync(IEnumerable<(TweakDefinition Tweak, bool Previous)> items,
        RegistryAtomicSnapshot atomicSnapshot, List<string> errors)
    {
        foreach (var item in items.Reverse())
        {
            try
            {
                item.Tweak.Status = "Vracím změnu…";
                bool restoredRaw = atomicSnapshot.TryRestore(item.Tweak);
                if (!restoredRaw)
                    await OptimizerModuleRuntime.From(item.Tweak).RollbackAsync(item.Previous, CancellationToken.None);
                bool actual = await OptimizerModuleRuntime.From(item.Tweak).VerifyAsync(CancellationToken.None);
                item.Tweak.Current = actual;
                item.Tweak.Pending = actual;
                item.Tweak.Status = actual == item.Previous ? "Vráceno po chybě" : "Rollback neověřen";
                await OperationJournalService.WriteAsync(ErrorService.NewCorrelationId(), "rollback",
                    item.Previous ? "enable" : "disable", item.Tweak.Id,
                    actual == item.Previous ? "Verified" : "Mismatch", item.Tweak.Status, CancellationToken.None);
            }
            catch (Exception ex)
            {
                errors.Add(item.Tweak.Name + ": rollback selhal — " + ex.Message);
                LogService.Error("rollback", "Rollback " + item.Tweak.Id, ex);
                await TrySynchronizeActualStateAsync(item.Tweak, "rollback-failure").ConfigureAwait(false);
            }
        }
    }

    private static async Task<(bool Verified, bool Actual)> VerifyWithRetryAsync(IOptimizerModule module, bool initialActual, bool expected, CancellationToken token)
    {
        bool actual = initialActual;
        int[] delays = { 0, 250, 700 };
        foreach (int delay in delays)
        {
            if (delay > 0) await Task.Delay(delay, token);
            actual = await module.VerifyAsync(token);
            if (actual == expected) return (true, actual);
        }
        return (false, actual);
    }

    private static async Task RefreshActualAfterFailureAsync(TweakDefinition tweak, bool expected, CancellationToken token)
    {
        try
        {
            bool actual = await OptimizerModuleRuntime.From(tweak).VerifyAsync(token);
            tweak.Current = actual;
            // Keep the user's requested value only until the batch rollback runs. Rollback
            // always resynchronizes Pending to the verified Windows state.
            tweak.Pending = expected;
            tweak.IsLoaded = true;
        }
        catch (Exception ex)
        {
            tweak.LastError = ex.Message;
            LogService.Error("tweaks", "Refresh actual after failure " + tweak.Id, ex);
        }
    }

    private static async Task TrySynchronizeActualStateAsync(TweakDefinition tweak, string reason)
    {
        try
        {
            bool actual = await OptimizerModuleRuntime.From(tweak).VerifyAsync(CancellationToken.None).ConfigureAwait(false);
            tweak.Current = actual;
            tweak.Pending = actual;
            tweak.IsLoaded = true;
            tweak.LastVerifiedUtc = DateTime.UtcNow;
            LogService.Info("tweaks", $"STATE_RESYNC_OK id={tweak.Id} reason={reason} actual={actual}");
        }
        catch (Exception verifyEx)
        {
            LogService.Error("tweaks", $"STATE_RESYNC_FAIL id={tweak.Id} reason={reason}", verifyEx);
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
