using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed partial class AiSystemResourceManagerService
{
    private async Task EvaluateTasksAsync(
        AiSystemRuntimeConfig config,
        bool quietEnough,
        bool fullDiscovery,
        AdaptivePowerRuntimeStatus? candidatePowerStatus,
        CancellationToken token)
    {
        DateTime nowUtc = DateTime.UtcNow;
        // Allocate the COM request list only after a qualifying task is
        // found. A fast foreground check with no AI owner needs zero COM.
        List<string>? candidates = null;
        bool fastRecoveryOnly = !quietEnough && !fullDiscovery;
        for (int i = 0; i < TaskAllowlist.Length; i++)
        {
            string path = TaskAllowlist[i];
            AiSystemRuntimeItemState state;
            if (fastRecoveryOnly)
            {
                // No recorded owner/observation/pending mutation: don't create
                // a dummy journal row or a freshly allocated task|path key.
                if (!_items.TryGetValue(TaskAllowlistKeys[i], out state))
                    continue;
            }
            else
                state = GetState("task", path);
            if (AiMaintenanceWriteAheadPolicy.IsUnownedPending(state))
            {
                _recoverInterruptedTrials = true;
                SetBlocked("task", path,
                    "pending journal without confirmed AI ownership; no Windows write");
                continue;
            }
            if (AiMaintenanceCandidatePolicy.ShouldDeferForCooldown(state, nowUtc))
                continue;
            if (!state.Managed && state.Rollbacks >= 2)
                continue;
            if (!AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(
                    quietEnough, fullDiscovery, state))
            {
                var suspended =
                    AiMaintenanceSchedulingPolicy.SuspendUnownedTaskObservation(state);
                if (!ReferenceEquals(suspended, state))
                    _items[TaskAllowlistKeys[i]] = suspended;
                continue;
            }
            (candidates ??= new List<string>(
                fastRecoveryOnly ? 4 : TaskAllowlist.Length)).Add(path);
        }

        if (candidates is null)
            return;

        IReadOnlyDictionary<string, TaskRuntimeState> taskStates;
        var unreadableTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            taskStates = TaskSchedulerComService.GetRuntimeStates(candidates, token, unreadableTasks);
        }
        catch (Exception ex)
        {
            _lastError = "Task Scheduler batch snapshot: " + ex.GetBaseException().Message;
            LogService.Error("ai-system", "AI System Runtime task batch snapshot failed", ex);
            return;
        }

        // Fast foreground recovery must never build/rank the full 28-task
        // opportunity array merely to check one owned Task Scheduler row.
        // Full discovery and quiet-window A/B/A preserve measured ranking.
        int[]? taskOrder = null;
        string[]? installedTaskCandidates = null;
        if (!fastRecoveryOnly)
        {
            // Task Scheduler COM, not a hardcoded 2019/2020 task list,
            // decides what can be considered for a NEW experiment. The
            // original IDs stay available for owned/pending recovery.
            installedTaskCandidates = WindowsComponentCatalogPolicy.PresentInSnapshot(
                TaskAllowlist,
                new HashSet<string>(taskStates.Keys, StringComparer.OrdinalIgnoreCase));
            int taskStart = _taskCandidateCursor;
            _taskCandidateCursor = AiMaintenanceCandidatePolicy.NextCursor(
                taskStart, installedTaskCandidates.Length);
            taskOrder = AiMaintenanceOpportunityPolicy.OrderedIndices(
                "task", installedTaskCandidates, taskStart, _items, DateTime.UtcNow);
        }
        int candidateCount = fastRecoveryOnly ? candidates.Count : installedTaskCandidates!.Length;
        for (int offset = 0; offset < candidateCount; offset++)
        {
            string path = fastRecoveryOnly
                ? candidates[offset] : installedTaskCandidates![taskOrder![offset]];
            token.ThrowIfCancellationRequested();
            if (!path.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase))
            {
                SetBlocked("task", path, "není Windows systémový task");
                continue;
            }

            AiSystemRuntimeItemState state = GetState("task", path);
            if (AiMaintenanceCandidatePolicy.ShouldDeferForCooldown(state, DateTime.UtcNow))
                continue;
            if (!state.Managed && state.Rollbacks >= 2)
            {
                SetBlocked("task", path, "AI learned protection • opakovaně obnoveno systémem");
                continue;
            }

            // Never label unqueried fast-scan candidates as not installed.
            // Only owned/pending entries were sent to Task Scheduler COM.
            if (!AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(
                    quietEnough, fullDiscovery, state))
                continue;
            if (!taskStates.TryGetValue(path, out TaskRuntimeState taskState))
            {
                // Absence from Task Scheduler and failure to read a task are
                // distinct outcomes; never claim absent on an access error.
                SetBlocked("task", path, unreadableTasks.Contains(path)
                    ? "task status unreadable" : "not installed");
                continue;
            }
            bool enabled = taskState.Enabled;

            if (state.Managed && state.Sleeping && enabled)
            {
                int rollbacks = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(
                    state.Rollbacks, externallyNeeded: true);
                // An external task re-enable disproves a presently safe
                // disabled hold; old measured savings must not boost its
                // ranking when cooldown expires.
                _items[Key("task", path)] =
                    AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                        AiMaintenanceOwnershipPolicy.Relinquish(
                            state, DateTime.UtcNow,
                            "external task enable; AI yielded",
                            rollbacks >= 2 ? TimeSpan.FromHours(6)
                                : TimeSpan.FromMinutes(30)),
                        externallyNeeded: true) with
                {
                    Managed = false,
                    Sleeping = false,
                    Rollbacks = rollbacks,
                    BlockedReason = rollbacks >= 2 ? "opakovaně obnoveno systémem / pravděpodobně potřebné" : string.Empty,
                    CooldownUntilUtc = DateTime.UtcNow.Add(rollbacks >= 2 ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(30)),
                    LastAction = "externally restored / assumed needed",
                    LastChangedUtc = DateTime.UtcNow
                };
                _restoredCount++;
                _lastAction = $"Task {ShortTask(path)}: needed again";
                AppendAction("task", path, "restored externally", rollbacks >= 2
                    ? "opakovaně obnoveno systémem"
                    : "systém task znovu povolil");
                continue;
            }

            // A previously disabled task running on demand is a genuine
            // demand signal even if COM still reports Enabled=false.
            if (AiManagedTaskDemandPolicy.RequiresImmediateRestore(state, taskState))
            {
                RestoreManagedTaskForSchedule(state,
                    "task executed while AI-disabled", DateTime.UtcNow, token,
                    externallyNeeded: true);
                continue;
            }

            if (state.Managed && state.Sleeping && !enabled &&
                AiManagedTaskHoldPolicy.ReleaseReason(state, DateTime.UtcNow) is string releaseReason)
            {
                RestoreManagedTaskForSchedule(state, releaseReason, DateTime.UtcNow, token);
                continue;
            }

            if (!quietEnough || !enabled)
            {
                ResetObservation("task", path);
                continue;
            }

            // Use the same policy at initial eligibility and final A/B/A
            // readback. A task that just became overdue, or has future
            // last-run metadata, must not be disabled on stale assumptions.
            string? unsafeTask = AiMaintenanceRevalidationPolicy.TaskReapplyBlockReason(
                taskState, nowUtc);
            if (unsafeTask != null)
            {
                SetBlocked("task", path, "task is active or scheduled: " + unsafeTask);
                continue;
            }

            DateTime observationNow = DateTime.UtcNow;
            DateTime observed = AiMaintenanceOpportunityPolicy.CandidateObservationStart(
                observationNow, _quietEpochStartUtc ?? observationNow,
                state.ObservationSinceUtc, config.ObservationSeconds, "task", state);
            state = state with { ObservationSinceUtc = observed, BlockedReason = string.Empty };
            _items[Key("task", path)] = state;
            if (observationNow - observed < TimeSpan.FromSeconds(AiMaintenanceOpportunityPolicy.CandidateObservationSeconds(
                    config.ObservationSeconds, "task", state, observationNow)))
                continue;
            if (_maintenanceMutationCommitted)
                continue;

            double baselineLoad = AiBackgroundPerformancePolicy.MedianLoad(_quietLoadSamples);
            if (!double.IsFinite(baselineLoad) || baselineLoad < 0.75)
            {
                SetBlocked("task", path, "baseline výkonu není dostatečně stabilní");
                continue;
            }

            // Scheduled task state can change between batch COM snapshot and
            // the observation deadline. Probe live state at the mutation
            // boundary; never disable a task that started or is now due.
            TaskRuntimeState freshTask = TaskSchedulerComService.GetRuntimeState(path, token);
            string? finalTaskBlock = AiMaintenanceRevalidationPolicy.TaskReapplyBlockReason(
                freshTask, DateTime.UtcNow);
            if (finalTaskBlock != null)
            {
                SetBlocked("task", path, "revalidated before AI trial: " + finalTaskBlock);
                continue;
            }

            // Batch COM + per-task readback may be slow. Do not use old
            // file telemetry to allow a new task mutation.
            DateTime trialNow = DateTime.UtcNow;
            AiSystemSmoothnessHealth? trialSnapshot =
                AiSystemSmoothnessGuardService.ReadAgentSnapshot(trialNow);
            if (!AiMaintenanceBoundaryEvidencePolicy.CanStartNewTrial(
                    candidatePowerStatus, trialNow,
                    AiSystemSmoothnessGuardService.AllowsBackgroundExperiment(
                        trialSnapshot, trialNow), _recoverInterruptedTrials))
            {
                SetBlocked("task", path,
                    "fresh AI CPU/RAM/disk quiet evidence lost before task mutation");
                continue;
            }
            double? baselineAvailableRam = AiPassiveMemoryTrialPolicy.FreshAvailableMiB(
                trialSnapshot, trialNow, null, null);
            // An event/logon/idle-triggered task may have no meaningful
            // NextRunTime. Never disable it based on a quiet clock gap.
            if (AiTaskTriggerSafetyPolicy.BlockReason(
                    TaskSchedulerComService.TryGetTaskXml(path),
                    freshTask.NextRunUtc, DateTime.UtcNow) is string triggerBlock)
            {
                SetBlocked("task", path, "task trigger safety: " + triggerBlock);
                continue;
            }

            AiSystemRuntimeItemState intent = state with
            {
                Managed = true,
                Sleeping = true,
                OriginalTaskEnabled = true,
                OriginalTaskNextRunUtc = freshTask.NextRunUtc,
                VerificationStartedUtc = DateTime.UtcNow,
                VerificationPhase = "CandidateB",
                PerformanceVerified = false,
                PendingMutation = AiMaintenanceWriteAheadPolicy.TaskDisable
            };
            BeginWriteAheadMutation(state, intent);

            TaskSchedulerComService.SetEnabled(path, false, token);
            TaskRuntimeState afterDisable =
                TaskSchedulerComService.GetRuntimeState(path, token);
            if (AiMaintenanceRevalidationPolicy.TaskDisableReadbackReason(
                    freshTask, afterDisable) is string readbackFailure)
                throw new InvalidOperationException(
                    "initial Candidate B task readback: " + readbackFailure);

            AiSystemRuntimeItemState completed = intent with
            {
                Managed = true,
                Sleeping = true,
                OriginalTaskEnabled = true,
                OriginalTaskNextRunUtc = freshTask.NextRunUtc,
                ObservationSinceUtc = null,
                CooldownUntilUtc = DateTime.UtcNow.AddSeconds(config.ActionCooldownSeconds),
                LastAction = "candidate trial: waiting for measured feedback",
                LastChangedUtc = DateTime.UtcNow,
                BaselineLoadPercent = baselineLoad,
                BaselineAvailableRamMiB = baselineAvailableRam,
                VerificationMemorySamples = 0,
                VerificationMemoryTotalMiB = 0,
                VerificationMemoryMinMiB = null,
                VerificationMemoryMaxMiB = null,
                VerificationLastMemorySnapshotUtc = null,
                CandidateMemorySamples = 0,
                CandidateAvailableRamMiB = null,
                CandidateMemoryMinMiB = null,
                CandidateMemoryMaxMiB = null,
                VerificationStartedUtc = DateTime.UtcNow,
                VerificationLastSampleUtc = null,
                VerificationSamples = 0,
                VerificationLoadTotal = 0,
                PerformanceVerified = false,
                VerificationPhase = "CandidateB",
                CandidateLoadPercent = null,
                VerificationLoadMin = null,
                VerificationLoadMax = null,
                CandidateLoadMin = null,
                CandidateLoadMax = null
            };
            CommitWriteAheadMutation(intent, completed);
            _quietLoadSamples.Clear();
            _maintenanceMutationCommitted = true;
            _lastAction = $"Task {ShortTask(path)}: measured background-pressure trial";
            AppendAction("task", path, "disabled", "stable measured background pressure");
            LogService.Info("ai-system", $"AI_SYSTEM_TASK_SLEEP path={path} measuredPressure=true");
            return;
        }

        await Task.CompletedTask;
    }

    private async Task RestoreAllAsync(CancellationToken token, bool includePerformanceTrials = true)
    {
        if (includePerformanceTrials)
            await _tweakRuntime.RestoreAllAsync(token).ConfigureAwait(false);
        await _securityRuntime.RestoreAllAsync(token).ConfigureAwait(false);
        await AdaptivePowerRuntimeService.RestoreAllAiSettingsAsync(token).ConfigureAwait(false);
        await RestoreKindAsync("service", token).ConfigureAwait(false);
        await RestoreKindAsync("task", token).ConfigureAwait(false);
        await RestoreKindAsync("driver", token).ConfigureAwait(false);
        _lastAction = "restore all";
    }

    private async Task RestoreKindAsync(string kind, CancellationToken token)
    {
        IReadOnlyList<KeyValuePair<string, AiSystemRuntimeItemState>> owned =
            AiPendingTrialSelectionPolicy.SnapshotManagedKind(_items, kind);
        for (int index = 0; index < owned.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var pair = owned[index];
            var state = pair.Value;
            try
            {
                if (kind == "service")
                {
                    int liveStart = ReadServiceStartType(state.Id);
                    if (!AiMaintenanceOwnershipPolicy.ServiceStartStillOwned(
                            state.OriginalServiceStart, liveStart, state.VerificationPhase))
                    {
                        _items[pair.Key] = AiMaintenanceOwnershipPolicy.Relinquish(state,
                            DateTime.UtcNow, "external service start mode preserved during restore",
                            TimeSpan.FromHours(12));
                        AppendAction("service", state.Id, "external override", "restore all yielded");
                        continue;
                    }

                    using var controller = new ServiceController(state.Id);
                    await RestoreServiceAsync(state, controller, "restore all", token,
                        externallyNeeded: false).ConfigureAwait(false);
                }
                else if (kind == "task")
                {
                    if (state.OriginalTaskEnabled != true)
                    {
                        _items[pair.Key] = AiMaintenanceOwnershipPolicy.Relinquish(state,
                            DateTime.UtcNow, "task original enabled state not AI-owned",
                            TimeSpan.FromHours(12));
                        continue;
                    }

                    TaskRuntimeState current = TaskSchedulerComService.GetRuntimeState(state.Id, token);
                    if (current.Enabled)
                    {
                        _items[pair.Key] = AiMaintenanceOwnershipPolicy.Relinquish(state,
                            DateTime.UtcNow, "task already enabled outside AI", TimeSpan.FromHours(12));
                        continue;
                    }

                    TaskSchedulerComService.SetEnabled(state.Id, true, token);
                    if (!TaskSchedulerComService.GetRuntimeState(state.Id, token).Enabled)
                        throw new InvalidOperationException("AI task restore readback did not confirm enabled.");

                    _items[pair.Key] = AiMaintenanceOwnershipPolicy.Relinquish(state,
                        DateTime.UtcNow, "restored by AI", TimeSpan.FromMinutes(10));
                    _restoredCount++;
                    AppendAction("task", state.Id, "restored", "restore all");
                }
                else if (kind == "driver")
                {
                    int liveStart = ReadServiceStartType(state.Id);
                    if (!AiMaintenanceOwnershipPolicy.DriverStartStillOwned(
                            state.OriginalServiceStart, liveStart))
                    {
                        string reason = AiMaintenanceOwnershipPolicy.DriverAlreadyRestored(
                            state.OriginalServiceStart, liveStart)
                            ? "driver already restored outside AI"
                            : "external driver start mode preserved during restore";
                        _items[pair.Key] = AiMaintenanceOwnershipPolicy.Relinquish(state,
                            DateTime.UtcNow, reason, TimeSpan.FromHours(12));
                        AppendAction("driver", state.Id, "external override", reason);
                        continue;
                    }

                    // Restore all runs outside the candidate observation loop;
                    // ownership of Start=4 alone does not prove this is still
                    // the SAME optional Windows driver that AI disabled.
                    DriverRegistrySnapshot restoreDriver = ReadDriverRegistrySnapshot(state.Id);
                    if (!CanRestoreDriverProvenance(
                            restoreDriver.Installed, restoreDriver.WindowsOwned,
                            restoreDriver.Type, restoreDriver.Start, liveStart))
                    {
                        SetBlocked("driver", state.Id,
                            "obnova ovladače blokována: identita se změnila; ruční kontrola nutná, AI záznam zachován");
                        continue;
                    }
                    WriteServiceStartType(state.Id, state.OriginalServiceStart!.Value);
                    int confirmedStart = ReadServiceStartType(state.Id);
                    if (AiMaintenanceWriteAheadPolicy.DriverRestoreReadbackReason(
                            state.OriginalServiceStart, confirmedStart) is string driverFailure)
                        throw new InvalidOperationException(
                            "restore all driver readback: " + driverFailure);
                    _items[pair.Key] = AiMaintenanceOwnershipPolicy.Relinquish(state,
                        DateTime.UtcNow, "driver start restored", TimeSpan.FromMinutes(10));
                    _restoredCount++;
                    AppendAction("driver", state.Id, "restored", "restore all");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // A cancelled restore is incomplete, never an implicit
                // success. The enclosing user action persists partial
                // progress in its finally block before reporting failure.
                throw;
            }
            catch (Exception ex)
            {
                _lastError = ex.GetBaseException().Message;
                LogService.Error("ai-system",
                    $"Restore {kind}/{state.Id} failed; ownership retained", ex);
            }
        }
    }

    // Every path that may re-enable a previously AI-owned service (manual
    // Restore all, crash recovery, A/B/A rollback and Reference A) must check
    // CURRENT image and ServiceDll provenance, not the cached candidate gate.
    // If provenance changed, keep the durable journal for manual recovery.
    private void RequireFreshServiceIdentityForRestore(AiSystemRuntimeItemState state)
    {
        if (IsWindowsOwnedServiceEntry(state.Id, requireFresh: true)) return;
        const string reason = "obnova blokována: změněná/neověřená cesta služby nebo ServiceDll; ruční obnova nutná, AI záznam zachován";
        SetBlocked("service", state.Id, reason);
        throw new InvalidOperationException(reason);
    }

    internal static bool CanRestoreDriverProvenance(
        bool installed, bool windowsOwned, int type, int snapshotStart, int liveStart) =>
        installed && windowsOwned &&
        AiDriverAutomationPolicy.IsEligibleDriverType(type) &&
        snapshotStart == liveStart && liveStart == 4;

    private async Task RestoreServiceAsync(
        AiSystemRuntimeItemState state,
        ServiceController controller,
        string reason,
        CancellationToken token,
        bool externallyNeeded = true)
    {
        // A stop/restart rollback must never launch a now-untrusted image.
        RequireFreshServiceIdentityForRestore(state);
        // Final readback even on recovery paths: a concurrent manual or OS
        // Start-type override must never be reverted by an old AI journal.
        int liveStart = ReadServiceStartType(state.Id);
        string restoreMode = AiVerifiedServiceHoldPolicy.WakeupDecision(
            state.OriginalServiceStart, liveStart);
        if (restoreMode == "external")
            throw new InvalidOperationException(
                "service start mode no longer owned by AI during restore");
        if (AiVerifiedServiceHoldPolicy.RequiresRestoreStartWrite(
                state.OriginalServiceStart, liveStart))
            WriteServiceStartType(state.Id, state.OriginalServiceStart!.Value);

        controller.Refresh();
        if (state.WasRunningBefore && controller.Status == ServiceControllerStatus.Stopped)
        {
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
        }

        // RestoreAll, service wakeup and crash recovery ALL use this method.
        // A successful SCM command is not proof that Windows retained the
        // original Start mode or that a previously running service restarted.
        controller.Refresh();
        int confirmedOriginalStart = ReadServiceStartType(state.Id);
        if (AiMaintenanceReferenceTransitionPolicy.ServiceRestoreReadbackReason(
                state.OriginalServiceStart, confirmedOriginalStart,
                state.WasRunningBefore, controller.Status)
            is string restoreReadbackFailure)
            throw new InvalidOperationException(
                "AI common service restore readback: " + restoreReadbackFailure);

        int rollbacks = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(state.Rollbacks, externallyNeeded);
        TimeSpan nextCooldown = AiMaintenanceCandidatePolicy.RestoreCooldown(rollbacks, externallyNeeded);
        DateTime restoredAt = DateTime.UtcNow;
        AiSystemRuntimeItemState releasedEvidence =
            AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                AiMaintenanceOwnershipPolicy.Relinquish(
                    state, restoredAt, "restored: " + reason, nextCooldown),
                externallyNeeded);
        _items[Key("service", state.Id)] = releasedEvidence with
        {
            Rollbacks = rollbacks,
            BlockedReason = externallyNeeded && rollbacks >= 2 ? "opakovaně obnoveno systémem / pravděpodobně potřebné" : string.Empty,
            CooldownUntilUtc = DateTime.UtcNow.Add(nextCooldown),
            LastAction = "restored: " + reason,
            LastChangedUtc = DateTime.UtcNow
        };
        _restoredCount++;
        _lastAction = $"Service {state.Id}: restored";
        AppendAction("service", state.Id, "restored", reason);
        LogService.Info("ai-system", $"AI_SYSTEM_SERVICE_RESTORE name={state.Id} reason={reason}");
        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
    }

    internal static bool IsCriticalService(string name)
    {
        if (CriticalServiceDenylist.Contains(name)) return true;
        // Windows service identities are normally ASCII. Compare the
        // denylist fragments without allocating a lowercase copy or LINQ
        // predicate on every candidate admission check. Preserve exact
        // legacy invariant-lowercase semantics for non-ASCII imports.
        if (name.AsSpan().IndexOfAnyExceptInRange('\0', '\x7f') < 0)
        {
            foreach (string fragment in CriticalServiceFragments)
                if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        string lower = name.ToLowerInvariant();
        return CriticalServiceFragments.Any(lower.Contains);
    }

    // A fast foreground pass only needs current managed-service status and
    // the short-cadence Wi-Fi/print demand evidence. The periodic discovery
    // still uses GetServices for the complete SCM + per-user inventory.
    private static readonly string[] FastDemandServiceNames =
    {
        "WlanSvc", "Spooler", "PrintNotify",
        "PrintDeviceConfigurationService", "PrintScanBrokerService"
    };
}
