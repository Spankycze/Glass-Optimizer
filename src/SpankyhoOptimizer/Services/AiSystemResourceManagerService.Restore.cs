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
    private async Task RestoreMaintenanceReferenceStateAsync(
        AiSystemRuntimeItemState state,
        CancellationToken token)
    {
        if (state.Kind.Equals("service", StringComparison.OrdinalIgnoreCase))
        {
            RequireFreshServiceIdentityForRestore(state);
            using var controller = new ServiceController(state.Id);
            controller.Refresh();
            int liveStart = ReadServiceStartType(state.Id);
            if (AiMaintenanceReferenceTransitionPolicy.ServiceBlockReason(
                    state.OriginalServiceStart, liveStart, controller.Status)
                is string referenceBlock)
                throw new InvalidOperationException(
                    "reference A safety: " + referenceBlock);
            if (state.OriginalServiceStart is int originalStart && liveStart != originalStart)
                WriteServiceStartType(state.Id, originalStart);

            controller.Refresh();
            if (state.WasRunningBefore && controller.Status == ServiceControllerStatus.Stopped)
            {
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
            }
            controller.Refresh();
            int restoredStart = ReadServiceStartType(state.Id);
            if (AiMaintenanceReferenceTransitionPolicy.ServiceRestoreReadbackReason(
                    state.OriginalServiceStart, restoredStart,
                    state.WasRunningBefore, controller.Status)
                is string referenceRestoreFailure)
                throw new InvalidOperationException(
                    "Reference A service readback: " + referenceRestoreFailure);
        }
        else if (state.Kind.Equals("task", StringComparison.OrdinalIgnoreCase) &&
                 state.OriginalTaskEnabled == true)
        {
            TaskRuntimeState before = TaskSchedulerComService.GetRuntimeState(
                state.Id, token);
            if (AiMaintenanceReferenceTransitionPolicy.TaskBlockReason(before)
                is string referenceBlock)
                throw new InvalidOperationException(
                    "reference A safety: " + referenceBlock);
            TaskSchedulerComService.SetEnabled(state.Id, true, token);
            TaskRuntimeState after = TaskSchedulerComService.GetRuntimeState(
                state.Id, token);
            if (!after.Enabled)
                throw new InvalidOperationException(
                    "Reference A task did not confirm Enabled=true.");
        }

        await Task.CompletedTask;
    }

    private async Task ApplyMaintenanceCandidateStateAsync(
        AiSystemRuntimeItemState state,
        CancellationToken token)
    {
        if (state.Kind.Equals("service", StringComparison.OrdinalIgnoreCase))
        {
            // Reference A may finish while new service dependents start.
            // An earlier eligibility pass is not authority for this Stop.
            if (TryGetServiceReapplyBlockReason(
                    state.Id, state.OriginalServiceStart) is string liveBlock)
                throw new InvalidOperationException(
                    "service reference changed before Candidate B: " + liveBlock);
            using var controller = new ServiceController(state.Id);
            controller.Refresh();
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                if (!controller.CanStop)
                    throw new InvalidOperationException("Service can no longer be stopped safely.");
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
            }
            controller.Refresh();
            if (controller.Status != ServiceControllerStatus.Stopped)
                throw new InvalidOperationException("Candidate B service state did not apply.");

            if (state.OriginalServiceStart == 2)
                WriteServiceStartType(state.Id, 3);

            controller.Refresh();
            int reapplyStart = ReadServiceStartType(state.Id);
            if (AiMaintenanceRevalidationPolicy.ServiceCandidateReadbackReason(
                    state.OriginalServiceStart, reapplyStart, controller.Status)
                is string reapplyFailure)
                throw new InvalidOperationException(
                    "final Candidate B service readback: " + reapplyFailure);
        }
        else if (state.Kind.Equals("task", StringComparison.OrdinalIgnoreCase))
        {
            TaskRuntimeState fresh = TaskSchedulerComService.GetRuntimeState(
                state.Id, token);
            string? schedulerBlock = AiMaintenanceRevalidationPolicy.TaskReapplyBlockReason(
                fresh, DateTime.UtcNow);
            if (schedulerBlock == null)
                schedulerBlock = AiTaskTriggerSafetyPolicy.BlockReason(
                    TaskSchedulerComService.TryGetTaskXml(state.Id),
                    fresh.NextRunUtc, DateTime.UtcNow);
            if (schedulerBlock == null &&
                AiManagedTaskHoldPolicy.SavedScheduleIsDueSoon(
                    state.OriginalTaskNextRunUtc, DateTime.UtcNow))
                schedulerBlock = "original scheduled deadline is due again";
            if (schedulerBlock != null)
                throw new InvalidOperationException(
                    "task scheduler changed before Candidate B: " + schedulerBlock);
            TaskSchedulerComService.SetEnabled(state.Id, false, token);
            TaskRuntimeState after = TaskSchedulerComService.GetRuntimeState(
                state.Id, token);
            if (AiMaintenanceRevalidationPolicy.TaskDisableReadbackReason(
                    fresh, after) is string readbackFailure)
                throw new InvalidOperationException(
                    "final Candidate B task readback: " + readbackFailure);
        }

        await Task.CompletedTask;
    }

    private void RestoreManagedTaskForSchedule(
        AiSystemRuntimeItemState state, string reason, DateTime nowUtc,
        CancellationToken token, bool externallyNeeded = false)
    {
        // A successful background optimization is not permission to suppress
        // scheduled Windows maintenance forever. Only re-enable a task that
        // AI itself originally disabled. Never count release as OS rejection.
        if (state.OriginalTaskEnabled != true)
            throw new InvalidOperationException(
                "Cannot release AI task without proof it was initially enabled.");

        TaskRuntimeState current = TaskSchedulerComService.GetRuntimeState(state.Id, token);
        bool reference = string.Equals(state.VerificationPhase, "ReferenceA",
            StringComparison.OrdinalIgnoreCase);
        if (current.Enabled && !reference)
        {
            // A user/Windows change already happened. Hand ownership back
            // instead of issuing another SetEnabled or claiming it is AI work.
            AbandonExternallyOverriddenTaskTrial(state,
                "external task re-enable detected during scheduled release",
                "CandidateB", true, nowUtc);
            return;
        }

        if (reference && !current.Enabled)
        {
            AbandonExternallyOverriddenTaskTrial(state,
                "task disabled externally during reference A release",
                "ReferenceA", false, nowUtc);
            return;
        }

        if (!current.Enabled)
        {
            TaskSchedulerComService.SetEnabled(state.Id, true, token);
            TaskRuntimeState readback = TaskSchedulerComService.GetRuntimeState(state.Id, token);
            if (!readback.Enabled)
                throw new InvalidOperationException(
                    "Scheduled AI task restore did not confirm Enabled=true.");
        }

        int nextRollbacks = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(
            state.Rollbacks, externallyNeeded);
        TimeSpan cooldown = externallyNeeded
            ? AiMaintenanceCandidatePolicy.RestoreCooldown(nextRollbacks, true)
            : AiManagedTaskHoldPolicy.RecoveryCooldown;
        AiSystemRuntimeItemState released =
            AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                AiMaintenanceOwnershipPolicy.Relinquish(
                    state, nowUtc, "task scheduling restored", cooldown),
                externallyNeeded);
        _items[Key(state.Kind, state.Id)] = released with
        {
            Managed = false,
            Sleeping = false,
            Rollbacks = nextRollbacks,
            PerformanceVerified = false,
            ObservationSinceUtc = null,
            CooldownUntilUtc = nowUtc.Add(cooldown),
            OriginalTaskNextRunUtc = null,
            BaselineLoadPercent = null,
            VerificationStartedUtc = null,
            VerificationLastSampleUtc = null,
            VerificationSamples = 0,
            VerificationLoadTotal = 0,
            VerificationPhase = string.Empty,
            CandidateLoadPercent = null,
            VerificationLoadMin = null,
            VerificationLoadMax = null,
            CandidateLoadMin = null,
            CandidateLoadMax = null,
            BlockedReason = externallyNeeded
                ? "Windows executed disabled task; future scheduling restored"
                : "AI released scheduled task for normal Windows maintenance",
            LastAction = "AI schedule-aware restore: " + reason,
            LastChangedUtc = nowUtc
        };
        _restoredCount++;
        _lastAction = externallyNeeded
            ? $"Task {ShortTask(state.Id)}: live execution restored future scheduling"
            : $"Task {ShortTask(state.Id)}: scheduled maintenance preserved";
        AppendAction("task", state.Id,
            externallyNeeded ? "live-demand restored" : "schedule-aware restored",
            reason);
        LogService.Info("ai-system",
            $"AI_SYSTEM_TASK_SCHEDULE_RESTORE id={state.Id} reason={reason} " +
            $"externallyNeeded={externallyNeeded} learnedRollbacks={nextRollbacks} verificationPreserved=false");
    }

    private async Task RecoverInterruptedMaintenanceTrialsAsync(CancellationToken token)
    {
        IReadOnlyList<AiSystemRuntimeItemState> interrupted =
            AiPendingTrialSelectionPolicy.SnapshotInterruptedJournal(_items.Values);
        for (int index = 0; index < interrupted.Count; index++)
        {
            AiSystemRuntimeItemState state = interrupted[index];
            token.ThrowIfCancellationRequested();
            DateTime now = DateTime.UtcNow;
            string phase = string.IsNullOrWhiteSpace(state.VerificationPhase)
                ? "CandidateB" : state.VerificationPhase;
            try
            {
                // A damaged/imported write-ahead row has no proven AI owner.
                // Preserve the journal for manual recovery; never reinterpret
                // it as an ordinary candidate or touch Windows. Keep the
                // global mutation veto armed across ticks/restarts.
                if (AiMaintenanceWriteAheadPolicy.IsUnownedPending(state))
                {
                    _lastError = "AI incomplete journal: Windows ownership unconfirmed; " +
                        "automatic changes suspended pending manual review";
                    continue;
                }
                if (AiMaintenanceWriteAheadPolicy.IsPending(state))
                {
                    await RecoverWriteAheadIntentAsync(state, now, token).ConfigureAwait(false);
                    continue;
                }

                if (state.Kind.Equals("task", StringComparison.OrdinalIgnoreCase))
                {
                    TaskRuntimeState task = TaskSchedulerComService.GetRuntimeState(state.Id, token);
                    if (AiMaintenanceTrialConsistencyPolicy.TaskMismatch(task, phase) is string reason)
                    {
                        AbandonExternallyOverriddenTaskTrial(state,
                            "restart: " + reason, phase, task.Enabled || task.Running, now);
                        continue;
                    }

                    if (state.OriginalTaskEnabled == true)
                        RestoreManagedTaskForSchedule(state,
                            "abandoned incomplete A/B/A after agent restart", now, token);
                    else
                        _items[Key(state.Kind, state.Id)] =
                            AiMaintenanceOwnershipPolicy.Relinquish(state, now,
                                "unknown original task state after restart",
                                TimeSpan.FromHours(12));
                    continue;
                }

                using var service = new ServiceController(state.Id);
                service.Refresh();
                int actualStart = ReadServiceStartType(state.Id);
                if (AiMaintenanceServiceStartPolicy.MustRetainOwnerOnUnknownStart(actualStart))
                    throw new InvalidOperationException(
                        "restart recovery service Start unreadable; keep durable owner for retry");
                if (!AiMaintenanceOwnershipPolicy.ServiceStartStillOwned(
                        state.OriginalServiceStart, actualStart, phase))
                {
                    _items[Key(state.Kind, state.Id)] =
                        AiMaintenanceOwnershipPolicy.Relinquish(state, now,
                            "external service start override after agent restart",
                            TimeSpan.FromHours(12));
                    continue;
                }

                if (AiMaintenanceTrialConsistencyPolicy.ServiceMismatch(
                        service.Status, actualStart, state.OriginalServiceStart, phase)
                    is string mismatch)
                {
                    AbandonExternallyOverriddenServiceTrial(state, phase,
                        service.Status, actualStart, "restart: " + mismatch, now);
                    continue;
                }

                if (string.Equals(phase, "ReferenceA", StringComparison.OrdinalIgnoreCase))
                {
                    // Original is already running with original start type:
                    // no additional Windows write is necessary.
                    _items[Key(state.Kind, state.Id)] =
                        AiMaintenanceOwnershipPolicy.Relinquish(state, now,
                            "reference A retained after agent restart",
                            TimeSpan.FromHours(12));
                    continue;
                }

                // Candidate B from a previous process has no trustworthy
                // cross-process A/B/A baseline. Return to Windows' original
                // service state without creating a no-benefit learned failure.
                await RestoreMaintenanceTrialAsync(state,
                    "incomplete A/B/A after agent restart", token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _lastError = "AI restart recovery: " + ex.GetBaseException().Message;
                LogService.Error("ai-system",
                    "AI restart recovery failed; retry on next maintenance tick", ex);
            }
        }
        _quietLoadSamples.Clear();
    }

    private async Task RecoverWriteAheadIntentAsync(
        AiSystemRuntimeItemState state, DateTime now, CancellationToken token)
    {
        string kind = state.Kind;
        string key = Key(kind, state.Id);
        if (!AiMaintenanceWriteAheadPolicy.IsExpected(kind, state.PendingMutation))
        {
            _items[key] = AiMaintenanceOwnershipPolicy.Relinquish(state, now,
                "unrecognized durable AI intent; Windows left unchanged", TimeSpan.FromHours(12));
            return;
        }

        if (kind.Equals("task", StringComparison.OrdinalIgnoreCase))
        {
            TaskRuntimeState current = TaskSchedulerComService.GetRuntimeState(state.Id, token);
            string decision = AiMaintenanceWriteAheadPolicy.TaskRecoveryState(
                state.OriginalTaskEnabled, current.Enabled);
            if (decision == "restore")
            {
                RestoreManagedTaskForSchedule(state,
                    "recovered durable task-disable intent after agent restart", now, token);
            }
            else
                _items[key] = AiMaintenanceOwnershipPolicy.Relinquish(state, now,
                    "task write-ahead intent was not applied or externally overridden",
                    TimeSpan.FromHours(12));
            return;
        }

        int actualStart = ReadServiceStartType(state.Id);
        if (AiMaintenanceServiceStartPolicy.MustRetainOwnerOnUnknownStart(actualStart))
            throw new InvalidOperationException(
                "write-ahead recovery Start unreadable; retain original durable owner for retry");
        if (kind.Equals("driver", StringComparison.OrdinalIgnoreCase))
        {
            string decision = AiMaintenanceWriteAheadPolicy.DriverRecoveryState(
                state.OriginalServiceStart, actualStart);
            if (decision == "restore")
            {
                WriteServiceStartType(state.Id, state.OriginalServiceStart!.Value);
                int restoredStart = ReadServiceStartType(state.Id);
                if (AiMaintenanceWriteAheadPolicy.DriverRestoreReadbackReason(
                        state.OriginalServiceStart, restoredStart) is string failure)
                    throw new InvalidOperationException(
                        "AI driver crash recovery readback: " + failure);
            }
            _items[key] = AiMaintenanceOwnershipPolicy.Relinquish(state, now,
                "driver write-ahead intent recovered: " + decision,
                TimeSpan.FromHours(12));
            return;
        }

        using var controller = new ServiceController(state.Id);
        controller.Refresh();
        if (controller.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.Stopped))
            throw new InvalidOperationException("Service is transitioning; retry ownership recovery.");

        string serviceDecision = AiMaintenanceWriteAheadPolicy.ServiceRecoveryState(
            state.OriginalServiceStart, actualStart,
            controller.Status == ServiceControllerStatus.Running);
        if (serviceDecision == "restore")
        {
            // Only the recorded AI-owned stop/mode can be reverted. Never
            // overwrite a start mode that differs from both old and AI mode.
            await RestoreServiceAsync(state, controller,
                "recovered durable service-stop intent", token,
                externallyNeeded: false).ConfigureAwait(false);
        }
        else
            _items[key] = AiMaintenanceOwnershipPolicy.Relinquish(state, now,
                "service write-ahead intent recovered: " + serviceDecision,
                TimeSpan.FromHours(12));
    }

    private async Task RestoreMaintenanceTrialAsync(
        AiSystemRuntimeItemState state,
        string reason,
        CancellationToken token,
        bool measuredNoBenefit = false)
    {
        if (state.Kind.Equals("service", StringComparison.OrdinalIgnoreCase))
        {
            // Restoration does not grant ownership over an external/manual
            // Start-mode change that occurred during the A/B/A experiment.
            int actualStart = ReadServiceStartType(state.Id);
            if (AiMaintenanceServiceStartPolicy.MustRetainOwnerOnUnknownStart(actualStart))
                throw new InvalidOperationException(
                    "A/B/A rollback service Start unreadable; do not discard original ownership");
            if (!AiMaintenanceOwnershipPolicy.ServiceStartStillOwned(
                    state.OriginalServiceStart, actualStart, state.VerificationPhase))
            {
                _items[Key(state.Kind, state.Id)] =
                    AiMaintenanceOwnershipPolicy.Relinquish(state, DateTime.UtcNow,
                        "external service start-mode override; AI yielded",
                        TimeSpan.FromHours(12));
                _lastAction = $"Service {state.Id}: external start mode preserved";
                AppendAction("service", state.Id, "external override",
                    "trial restore skipped after external start-mode change");
                return;
            }
            // A changed ServiceDll or ImagePath must not be launched by
            // an A/B/A rollback merely because the Start value is still ours.
            RequireFreshServiceIdentityForRestore(state);
            using var controller = new ServiceController(state.Id);
            if (state.OriginalServiceStart is int originalStart)
                WriteServiceStartType(state.Id, originalStart);

            controller.Refresh();
            if (state.WasRunningBefore && controller.Status == ServiceControllerStatus.Stopped)
            {
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
            }
            controller.Refresh();
            int restoredStart = ReadServiceStartType(state.Id);
            if (AiMaintenanceReferenceTransitionPolicy.ServiceRestoreReadbackReason(
                    state.OriginalServiceStart, restoredStart,
                    state.WasRunningBefore, controller.Status) is string restoreFailure)
                throw new InvalidOperationException(
                    "AI service rollback readback: " + restoreFailure);
        }
        else if (state.Kind.Equals("task", StringComparison.OrdinalIgnoreCase))
        {
            // An asynchronous rollback must not re-enable a task that was
            // manually disabled during Reference A, or claim ownership over
            // a task Windows already re-enabled during Candidate B.
            TaskRuntimeState liveTask = TaskSchedulerComService.GetRuntimeState(
                state.Id, token);
            if (!AiMaintenanceOwnershipPolicy.TaskEnableStillOwned(
                    state.OriginalTaskEnabled, liveTask.Enabled,
                    state.VerificationPhase))
            {
                _items[Key(state.Kind, state.Id)] =
                    AiMaintenanceOwnershipPolicy.Relinquish(state, DateTime.UtcNow,
                        "task enabled state changed or original state unknown; external choice preserved",
                        TimeSpan.FromHours(12));
                _lastAction = $"Task {ShortTask(state.Id)}: external scheduler choice preserved";
                AppendAction("task", state.Id, "external override",
                    "trial rollback skipped; task enable ownership lost");
                return;
            }
            if (!liveTask.Enabled)
                TaskSchedulerComService.SetEnabled(state.Id, true, token);
            TaskRuntimeState confirmed = TaskSchedulerComService.GetRuntimeState(
                state.Id, token);
            if (AiMaintenanceReferenceTransitionPolicy.TaskRestoreReadbackReason(
                    state.OriginalTaskEnabled, confirmed) is string restoreFailure)
                throw new InvalidOperationException(
                    "AI task rollback readback: " + restoreFailure);
        }

        int failedBenefitTrials = measuredNoBenefit
            ? AiMaintenanceCandidatePolicy.NextFailureCount(state.FailedBenefitTrials)
            : state.FailedBenefitTrials;
        TimeSpan recoveryCooldown = measuredNoBenefit
            ? AiMaintenanceCandidatePolicy.RetryCooldown(failedBenefitTrials)
            : TimeSpan.FromHours(12);
        // The OS state is restored: terminally release the mutation owner
        // and all candidate/reference CPU+RAM samples. Preserve only durable
        // outcome learning; a measured failure invalidates old CPU/RAM wins.
        DateTime completedAt = DateTime.UtcNow;
        AiSystemRuntimeItemState released =
            AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                AiMaintenanceOwnershipPolicy.Relinquish(
                    state, completedAt, "trial rolled back: " + reason,
                    recoveryCooldown),
                measuredNoBenefit);
        _items[Key(state.Kind, state.Id)] = ClearVerifiedTrial(released with
        {
            Managed = false,
            Sleeping = false,
            FailedBenefitTrials = failedBenefitTrials,
            LastVerifiedBenefitAbsolute = measuredNoBenefit
                ? 0 : state.LastVerifiedBenefitAbsolute,
            LastBenefitVerifiedUtc = measuredNoBenefit
                ? null : state.LastBenefitVerifiedUtc,
            LastVerifiedBenefitPercent = measuredNoBenefit
                ? 0 : state.LastVerifiedBenefitPercent,
            BlockedReason = measuredNoBenefit
                ? $"no measured benefit after {failedBenefitTrials} trials; next retry in {recoveryCooldown.TotalHours:0} h"
                : "trial rolled back: " + reason,
            CooldownUntilUtc = DateTime.UtcNow.Add(recoveryCooldown),
            LastAction = "trial rolled back: " + reason,
            LastChangedUtc = DateTime.UtcNow
        });
        _restoredCount++;
        _lastAction = $"{state.Kind} {state.Id}: no measured benefit -> restored";
        AppendAction(state.Kind, state.Id, "trial rollback", reason);
        LogService.Info("ai-system",
            $"AI_SYSTEM_FEEDBACK_REJECT kind={state.Kind} id={state.Id} " +
            $"reason={reason} learnedFailures={failedBenefitTrials} retryHours={recoveryCooldown.TotalHours:0}");
        await Task.CompletedTask;
    }
}
