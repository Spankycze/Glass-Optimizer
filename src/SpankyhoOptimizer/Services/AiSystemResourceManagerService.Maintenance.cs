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
    private async Task<bool> EvaluatePendingMaintenanceFeedbackAsync(
        bool comparableQuietSample,
        bool stopUnsafeTrials,
        double loadPercent,
        DateTime nowUtc,
        AiSystemSmoothnessHealth? agentSmoothness,
        CancellationToken token)
    {
        // A single stable ordered snapshot survives async phase changes
        // without LINQ's iterator, sorter and intermediate array allocations.
        IReadOnlyList<AiSystemRuntimeItemState> pending =
            AiPendingTrialSelectionPolicy.OrderedPendingTrials(_items.Values);

        if (pending.Count == 0)
            return false;

        for (int index = 0; index < pending.Count; index++)
        {
            AiSystemRuntimeItemState state = pending[index];
            token.ThrowIfCancellationRequested();
            try
            {
                // Always inspect the actual phase BEFORE timeout, workload
                // rollback or adding samples. External changes outrank our
                // cached ownership journal; never overwrite user/OS choices
                // or treat the experiment as causal performance evidence.
                string livePhase = string.IsNullOrWhiteSpace(state.VerificationPhase)
                    ? "CandidateB" : state.VerificationPhase;
                if (state.Kind.Equals("service", StringComparison.OrdinalIgnoreCase))
                {
                    using var service = new ServiceController(state.Id);
                    service.Refresh();
                    int liveStart = ReadServiceStartType(state.Id);
                    if (AiMaintenanceServiceStartPolicy.MustRetainOwnerOnUnknownStart(liveStart))
                        throw new InvalidOperationException(
                            "A/B/A service Start unreadable; retain ownership until live SCM evidence returns");
                    string? external = AiMaintenanceTrialConsistencyPolicy.ServiceMismatch(
                        service.Status, liveStart, state.OriginalServiceStart, livePhase);
                    if (external != null)
                    {
                        AbandonExternallyOverriddenServiceTrial(
                            state, livePhase, service.Status, liveStart, external, nowUtc);
                        _quietLoadSamples.Clear();
                        continue;
                    }
                }
                else if (state.Kind.Equals("task", StringComparison.OrdinalIgnoreCase))
                {
                    TaskRuntimeState liveTask = TaskSchedulerComService.GetRuntimeState(state.Id, token);
                    // Disabled tasks can still be launched on demand by
                    // Windows. Live execution is proof the task is needed;
                    // restore our disabled scheduling before yielding.
                    if (AiManagedTaskDemandPolicy.RequiresImmediateRestore(state, liveTask))
                    {
                        RestoreManagedTaskForSchedule(state,
                            "task executed despite AI disable", nowUtc, token,
                            externallyNeeded: true);
                        _quietLoadSamples.Clear();
                        continue;
                    }
                    string? external = AiMaintenanceTrialConsistencyPolicy.TaskMismatch(
                        liveTask, livePhase);
                    if (external != null)
                    {
                        AbandonExternallyOverriddenTaskTrial(state, external, livePhase,
                            liveTask.Enabled || liveTask.Running, nowUtc);
                        _quietLoadSamples.Clear();
                        continue;
                    }
                }

                if (state.Kind.Equals("task", StringComparison.OrdinalIgnoreCase) &&
                    AiManagedTaskHoldPolicy.ReleaseReason(state, nowUtc) is string dueReason)
                {
                    // Even when the task has no new COM NextRunTime while
                    // disabled, its saved pre-disable deadline is authoritative.
                    // This is an AI-owned planned release, not a failed trial.
                    RestoreManagedTaskForSchedule(state, dueReason, nowUtc, token);
                    _quietLoadSamples.Clear();
                    continue;
                }

                if (AiMaintenanceTrialTimePolicy.InvalidPhaseReason(
                        state.VerificationStartedUtc, state.VerificationLastSampleUtc,
                        nowUtc, state.VerificationSamples) is string phaseTimeReason)
                {
                    await RestoreMaintenanceTrialAsync(
                        state, "A/B/A time evidence invalid: " + phaseTimeReason,
                        token).ConfigureAwait(false);
                    _quietLoadSamples.Clear();
                    continue;
                }

                // An active game or genuine CPU/RAM pressure must not leave a
                // service/task candidate in its experimental state for 30 minutes.
                if (stopUnsafeTrials)
                {
                    await RestoreMaintenanceTrialAsync(state,
                        "A/B/A stopped: unsafe or missing CPU/RAM evidence", token).ConfigureAwait(false);
                    _quietLoadSamples.Clear();
                    continue;
                }

                if (!comparableQuietSample)
                    continue;

                if (state.VerificationLastSampleUtc is DateTime lastSample &&
                    nowUtc - lastSample < AiBackgroundPerformancePolicy.MinimumSampleGap)
                    continue;

                string phase = string.IsNullOrWhiteSpace(state.VerificationPhase)
                    ? "CandidateB"
                    : state.VerificationPhase;

                // Reuse FanAgent's 30-second physical-memory snapshot.
                // Never count a repeated, stale or pre-phase reading as a
                // second RAM observation. CPU A/B/A remains independent.
                // The same FanAgent's immutable 30s physical-memory sample
                // was captured at tick entry. A disk fallback may belong to
                // an earlier process and is not valid A/B/A attribution.
                AiSystemSmoothnessHealth? memoryHealth = agentSmoothness;
                double? availableRamMiB = AiPassiveMemoryTrialPolicy.FreshAvailableMiB(
                    memoryHealth, nowUtc, state.VerificationLastMemorySnapshotUtc,
                    state.VerificationStartedUtc);
                AiSystemRuntimeItemState sampled = state with
                {
                    VerificationMemorySamples = state.VerificationMemorySamples +
                        (availableRamMiB.HasValue ? 1 : 0),
                    VerificationMemoryTotalMiB = state.VerificationMemoryTotalMiB +
                        availableRamMiB.GetValueOrDefault(),
                    VerificationMemoryMinMiB = availableRamMiB is double ramMin
                        ? Math.Min(state.VerificationMemoryMinMiB ?? ramMin, ramMin)
                        : state.VerificationMemoryMinMiB,
                    VerificationMemoryMaxMiB = availableRamMiB is double ramMax
                        ? Math.Max(state.VerificationMemoryMaxMiB ?? ramMax, ramMax)
                        : state.VerificationMemoryMaxMiB,
                    VerificationLastMemorySnapshotUtc = availableRamMiB.HasValue
                        ? memoryHealth!.UpdatedUtc : state.VerificationLastMemorySnapshotUtc,
                    VerificationLastSampleUtc = nowUtc,
                    VerificationSamples = state.VerificationSamples + 1,
                    VerificationLoadTotal = state.VerificationLoadTotal + loadPercent,
                    VerificationLoadMin = state.VerificationLoadMin is double previousMin
                        ? Math.Min(previousMin, loadPercent)
                        : loadPercent,
                    VerificationLoadMax = state.VerificationLoadMax is double previousMax
                        ? Math.Max(previousMax, loadPercent)
                        : loadPercent,
                    LastAction = $"{phase} feedback {state.VerificationSamples + 1}/{AiMaintenanceFeedbackPolicy.RequiredCandidateSamples}"
                };
                _items[Key(state.Kind, state.Id)] = sampled;

                if (phase.Equals("CandidateB", StringComparison.OrdinalIgnoreCase))
                {
                    AiMaintenanceFeedbackVerdict candidateVerdict = AiMaintenanceFeedbackPolicy.Evaluate(
                        sampled.BaselineLoadPercent ?? double.NaN,
                        sampled.VerificationSamples,
                        sampled.VerificationLoadTotal,
                        sampled.VerificationLoadMin,
                        sampled.VerificationLoadMax);

                    if (!candidateVerdict.Ready)
                        continue;

                    // Candidate B is only meaningful if the task is STILL disabled.
                    // A user or Windows re-enable invalidates the measurement;
                    // do not claim AI benefit or fight the external decision.
                    if (sampled.Kind.Equals("task", StringComparison.OrdinalIgnoreCase))
                    {
                        TaskRuntimeState liveCandidate =
                            TaskSchedulerComService.GetRuntimeState(sampled.Id, token);
                        if (!AiMaintenanceRevalidationPolicy.MatchesTrialPhase(
                                liveCandidate.Enabled, "CandidateB"))
                        {
                            AbandonExternallyOverriddenTaskTrial(sampled,
                                "external task re-enable during Candidate B", "CandidateB", true, nowUtc);
                            _quietLoadSamples.Clear();
                            continue;
                        }
                    }

                    if (!candidateVerdict.Accepted)
                    {
                        await RestoreMaintenanceTrialAsync(sampled, candidateVerdict.Reason, token,
                            AiMaintenanceCandidatePolicy.IsMeasuredNoBenefit(candidateVerdict)).ConfigureAwait(false);
                        _quietLoadSamples.Clear();
                        continue;
                    }

                    double candidateLoad = sampled.VerificationLoadTotal / sampled.VerificationSamples;
                    await RestoreMaintenanceReferenceStateAsync(sampled, token).ConfigureAwait(false);
                    _items[Key(state.Kind, state.Id)] = sampled with
                    {
                        Sleeping = false,
                        VerificationPhase = "ReferenceA",
                        CandidateLoadPercent = candidateLoad,
                        VerificationStartedUtc = nowUtc,
                        VerificationLastSampleUtc = null,
                        VerificationSamples = 0,
                        VerificationLoadTotal = 0,
                        CandidateLoadMin = sampled.VerificationLoadMin,
                        CandidateLoadMax = sampled.VerificationLoadMax,
                        CandidateMemorySamples = sampled.VerificationMemorySamples,
                        CandidateAvailableRamMiB = sampled.VerificationMemorySamples ==
                            AiPassiveMemoryTrialPolicy.RequiredPhaseSamples
                            ? sampled.VerificationMemoryTotalMiB / sampled.VerificationMemorySamples
                            : null,
                        CandidateMemoryMinMiB = sampled.VerificationMemoryMinMiB,
                        CandidateMemoryMaxMiB = sampled.VerificationMemoryMaxMiB,
                        VerificationMemorySamples = 0,
                        VerificationMemoryTotalMiB = 0,
                        VerificationMemoryMinMiB = null,
                        VerificationMemoryMaxMiB = null,
                        VerificationLastMemorySnapshotUtc = null,
                        VerificationLoadMin = null,
                        VerificationLoadMax = null,
                        LastAction = "candidate promising; original restored for reference A",
                        LastChangedUtc = nowUtc
                    };
                    _lastAction = $"{state.Kind} {state.Id}: measuring restored reference A";
                    AppendAction(state.Kind, state.Id, "reference A",
                        $"candidate={candidateLoad:0.00}% baseline={sampled.BaselineLoadPercent:0.00}%");
                    LogService.Info("ai-system",
                        $"AI_SYSTEM_ABA_REFERENCE kind={state.Kind} id={state.Id} candidate={candidateLoad:0.00}");
                    _quietLoadSamples.Clear();
                    continue;
                }

                AiMaintenanceFeedbackVerdict verdict = AiMaintenanceFeedbackPolicy.EvaluateAba(
                    sampled.BaselineLoadPercent ?? double.NaN,
                    sampled.CandidateLoadPercent ?? double.NaN,
                    sampled.VerificationSamples,
                    sampled.VerificationLoadTotal,
                    sampled.CandidateLoadMin,
                    sampled.CandidateLoadMax,
                    sampled.VerificationLoadMin,
                    sampled.VerificationLoadMax);

                if (!verdict.Ready)
                    continue;

                // A/B/A confidence is invalid when Windows or the user changed
                // the restored reference A outside AI. Preserve that external
                // choice; never automatically overwrite it with a new B.
                if (sampled.Kind.Equals("task", StringComparison.OrdinalIgnoreCase))
                {
                    TaskRuntimeState liveReference =
                        TaskSchedulerComService.GetRuntimeState(sampled.Id, token);
                    if (!AiMaintenanceRevalidationPolicy.MatchesTrialPhase(
                            liveReference.Enabled, "ReferenceA"))
                    {
                        AbandonExternallyOverriddenTaskTrial(sampled,
                            "task disabled externally during reference A", "ReferenceA", false, nowUtc);
                        _quietLoadSamples.Clear();
                        continue;
                    }
                    if (verdict.Accepted)
                    {
                        string? blocked = AiMaintenanceRevalidationPolicy.TaskReapplyBlockReason(
                            liveReference, nowUtc);
                        if (blocked == null &&
                            AiManagedTaskHoldPolicy.SavedScheduleIsDueSoon(
                                sampled.OriginalTaskNextRunUtc, nowUtc))
                            blocked = "saved pre-disable scheduled run is now due";
                        if (blocked != null)
                        {
                            await RestoreMaintenanceTrialAsync(sampled,
                                "final scheduler safety check: " + blocked, token).ConfigureAwait(false);
                            _quietLoadSamples.Clear();
                            continue;
                        }
                    }
                }

                AiPassiveMemoryTrialPolicy.Evidence ramEvidence =
                    AiPassiveMemoryTrialPolicy.Evaluate(
                        sampled.BaselineAvailableRamMiB,
                        sampled.CandidateMemorySamples,
                        sampled.CandidateAvailableRamMiB is double candidateRam
                            ? candidateRam * sampled.CandidateMemorySamples : double.NaN,
                        sampled.CandidateMemoryMinMiB, sampled.CandidateMemoryMaxMiB,
                        sampled.VerificationMemorySamples, sampled.VerificationMemoryTotalMiB,
                        sampled.VerificationMemoryMinMiB, sampled.VerificationMemoryMaxMiB);

                if (verdict.Accepted)
                {
                    // CPU improvement alone must not keep a Windows resource
                    // disabled when stable, fresh 3+3 RAM A/B/A observations
                    // prove an offsetting material memory regression. Missing
                    // or noisy RAM data remain inconclusive, never fabricated.
                    AiPassiveMemoryTrialPolicy.Regression memoryRegression =
                        AiPassiveMemoryTrialPolicy.EvaluateRegression(
                            sampled.BaselineAvailableRamMiB,
                            sampled.CandidateMemorySamples,
                            sampled.CandidateAvailableRamMiB is double candidateRamForRisk
                                ? candidateRamForRisk * sampled.CandidateMemorySamples
                                : double.NaN,
                            sampled.CandidateMemoryMinMiB, sampled.CandidateMemoryMaxMiB,
                            sampled.VerificationMemorySamples, sampled.VerificationMemoryTotalMiB,
                            sampled.VerificationMemoryMinMiB, sampled.VerificationMemoryMaxMiB);
                    if (AiPassiveMemoryTrialPolicy.OutweighsVerifiedCpuGain(
                            verdict, memoryRegression))
                    {
                        await RestoreMaintenanceTrialAsync(sampled,
                            $"CPU benefit outweighed by confirmed available RAM loss {memoryRegression.LostMiB:0.#} MiB",
                            token, measuredNoBenefit: true).ConfigureAwait(false);
                        _quietLoadSamples.Clear();
                        continue;
                    }

                    // A/B/A proof describes earlier quiet CPU samples, not
                    // a current grant to stop Windows resources. Re-read both
                    // power/workload and same-agent smoothness immediately
                    // before the SECOND mutation as well as the first.
                    DateTime reapplyNow = DateTime.UtcNow;
                    AdaptivePowerRuntimeStatus? reapplyPower =
                        AdaptivePowerRuntimeService.ReadStatus();
                    AiSystemSmoothnessHealth? reapplyHealth =
                        AiSystemSmoothnessGuardService.ReadAgentSnapshot(reapplyNow);
                    if (!AiMaintenanceBoundaryEvidencePolicy.CanStartNewTrial(
                            reapplyPower, reapplyNow,
                            AiSystemSmoothnessGuardService.AllowsBackgroundExperiment(
                                reapplyHealth, reapplyNow), _recoverInterruptedTrials))
                    {
                        await RestoreMaintenanceTrialAsync(sampled,
                            "A/B/A verified but foreground/game or safety evidence changed before reapply",
                            token).ConfigureAwait(false);
                        _quietLoadSamples.Clear();
                        continue;
                    }

                    // Re-check service ownership, its live running state and
                    // *current* dependent services before a second Stop.
                    // These conditions can change during A/B/A collection.
                    if (sampled.Kind.Equals("service", StringComparison.OrdinalIgnoreCase) &&
                        TryGetServiceReapplyBlockReason(sampled.Id, sampled.OriginalServiceStart)
                            is string serviceBlock)
                    {
                        await RestoreMaintenanceTrialAsync(sampled,
                            "final service safety check: " + serviceBlock, token).ConfigureAwait(false);
                        _quietLoadSamples.Clear();
                        continue;
                    }
                    // Journal the SECOND Candidate-B write just like the first:
                    // a process crash after Stop/Disable must retain enough
                    // durable ownership data to restore Windows on restart.
                    AiSystemRuntimeItemState reapplyIntent =
                        AiMaintenanceWriteAheadPolicy.CreateReapplyIntent(sampled);
                    BeginWriteAheadMutation(sampled, reapplyIntent);
                    await ApplyMaintenanceCandidateStateAsync(sampled, token).ConfigureAwait(false);
                    AiSystemRuntimeItemState completedReapply = sampled with
                    {
                        Sleeping = true,
                        BaselineLoadPercent = null,
                        VerificationStartedUtc = null,
                        VerificationLastSampleUtc = null,
                        VerificationSamples = 0,
                        VerificationLoadTotal = 0,
                        PerformanceVerified = true,
                        FailedBenefitTrials = 0,
                        LastVerifiedBenefitAbsolute = verdict.ImprovementAbsolute,
                        LastVerifiedBenefitPercent = verdict.ImprovementPercent,
                        LastVerifiedAvailableRamBenefitMiB = ramEvidence.Confirmed
                            ? ramEvidence.BenefitMiB : null,
                        LastBenefitVerifiedUtc = nowUtc,
                        BaselineAvailableRamMiB = null,
                        VerificationMemorySamples = 0,
                        VerificationMemoryTotalMiB = 0,
                        VerificationMemoryMinMiB = null,
                        VerificationMemoryMaxMiB = null,
                        VerificationLastMemorySnapshotUtc = null,
                        CandidateMemorySamples = 0,
                        CandidateAvailableRamMiB = null,
                        CandidateMemoryMinMiB = null,
                        CandidateMemoryMaxMiB = null,
                        VerificationPhase = string.Empty,
                        CandidateLoadPercent = null,
                        VerificationLoadMin = null,
                        VerificationLoadMax = null,
                        CandidateLoadMin = null,
                        CandidateLoadMax = null,
                        LastAction = "A/B/A verified: " + verdict.Reason,
                        LastChangedUtc = nowUtc
                    };
                    CommitWriteAheadMutation(reapplyIntent, completedReapply);
                    _lastAction = $"{state.Kind} {state.Id}: A/B/A benefit confirmed";
                    AppendAction(state.Kind, state.Id, "verified A/B/A",
                        verdict.Reason + (ramEvidence.Confirmed
                            ? $"; passive available RAM +{ramEvidence.BenefitMiB:0.#} MiB vs restored reference"
                            : "; RAM evidence inconclusive: " + ramEvidence.Reason));
                    LogService.Info("ai-system",
                        $"AI_SYSTEM_FEEDBACK_ACCEPT_ABA kind={state.Kind} id={state.Id} improvement={verdict.ImprovementPercent:0.0}% " +
                        $"ramObservationMiB={(ramEvidence.Confirmed ? ramEvidence.BenefitMiB.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "inconclusive")}");
                }
                else
                {
                    await RestoreMaintenanceTrialAsync(sampled, verdict.Reason, token,
                        AiMaintenanceCandidatePolicy.IsMeasuredNoBenefit(verdict)).ConfigureAwait(false);
                }

                _quietLoadSamples.Clear();
            }
            catch (Exception ex)
            {
                _lastError = $"Feedback {state.Kind}/{state.Id}: {ex.GetBaseException().Message}";
                LogService.Error("ai-system", "AI maintenance feedback failed", ex);
            }
        }

        return true;
    }

    private void AbandonExternallyOverriddenServiceTrial(
        AiSystemRuntimeItemState state, string phase,
        ServiceControllerStatus liveStatus, int liveStart,
        string reason, DateTime nowUtc)
    {
        // If AI changed Automatic (2) to Demand (3) for Candidate B and
        // Windows restarted it, repair only AI's own start-mode change.
        // Do not start/stop the externally controlled service. If its mode
        // differs from the AI-applied value, leave the user/OS choice intact.
        if (phase.Equals("CandidateB", StringComparison.OrdinalIgnoreCase) &&
            state.OriginalServiceStart == 2 && liveStart == 3)
        {
            // The service was externally reactivated. Undo ONLY AI's own
            // Automatic->Demand write, confirming ownership immediately
            // before the mutation and readback before relinquishing journal.
            int actualMode = ReadServiceStartType(state.Id);
            if (actualMode == 3)
            {
                WriteServiceStartType(state.Id, 2);
                if (AiMaintenanceReferenceTransitionPolicy.ServiceRestoreReadbackReason(
                        state.OriginalServiceStart, ReadServiceStartType(state.Id),
                        false, liveStatus) is string wakeFailure)
                    throw new InvalidOperationException(
                        "external service wake restore readback: " + wakeFailure);
            }
            else if (actualMode != 2)
                throw new InvalidOperationException(
                    "external service start mode changed during AI wake restoration");
        }

        bool osNeeded = AiMaintenanceTrialConsistencyPolicy.IsExternalNeed(
            phase, liveStatus == ServiceControllerStatus.Running);
        int learnedRollbacks = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(
            state.Rollbacks, osNeeded);
        _items[Key(state.Kind, state.Id)] = ClearVerifiedTrial(
            AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                AiMaintenanceOwnershipPolicy.Relinquish(
                    state, nowUtc,
                    "external service change during A/B/A; AI yielded",
                    TimeSpan.FromHours(12)),
                osNeeded) with
        {
            Rollbacks = learnedRollbacks,
            BlockedReason = "external service change during A/B/A; AI yielded",
            CooldownUntilUtc = nowUtc.AddHours(12),
            LastAction = reason,
            LastChangedUtc = nowUtc
        });
        _lastAction = $"Service {state.Id}: AI yielded to external change";
        AppendAction("service", state.Id, "external override", reason);
        LogService.Info("ai-system",
            $"AI_SYSTEM_TRIAL_EXTERNAL_SERVICE_OVERRIDE id={state.Id} phase={phase} " +
            $"status={liveStatus} learnedRollbacks={learnedRollbacks} reason={reason}");
    }

    private void AbandonExternallyOverriddenTaskTrial(
        AiSystemRuntimeItemState state, string reason, string phase,
        bool active, DateTime nowUtc)
    {
        // No Task Scheduler write: external/manual decisions are authoritative.
        // The AI must stop claiming ownership or verified performance.
        bool externallyNeeded = AiMaintenanceTrialConsistencyPolicy.IsExternalNeed(
            phase, active);
        _items[Key(state.Kind, state.Id)] = ClearVerifiedTrial(
            AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                AiMaintenanceOwnershipPolicy.Relinquish(
                    state, nowUtc,
                    "external override during A/B/A; AI will not reapply",
                    TimeSpan.FromHours(12)),
                externallyNeeded) with
        {
            Rollbacks = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(
                state.Rollbacks, externallyNeeded),
            BlockedReason = "external override during A/B/A; AI will not reapply",
            CooldownUntilUtc = nowUtc.AddHours(12),
            LastAction = reason,
            LastChangedUtc = nowUtc
        });
        _lastAction = $"Task {ShortTask(state.Id)}: AI yielded to external override";
        AppendAction("task", state.Id, "external override", reason);
        LogService.Info("ai-system", $"AI_SYSTEM_TRIAL_EXTERNAL_OVERRIDE id={state.Id} reason={reason}");
    }

    private string? TryGetServiceReapplyBlockReason(
        string name, int? expectedOriginalStart = null)
    {
        if (IsCriticalService(name)) return "critical Windows service";
        // The 10-minute ownership cache is observation-only. Immediately
        // before an AI-owned service stop, verify its identity directly from
        // HKLM again; an old positive result must never authorize a write.
        if (!IsWindowsOwnedServiceEntry(name, requireFresh: true))
            return "ownership could not be verified";
        if (AiMaintenanceServiceStartPolicy.BlockReason(
                ReadServiceStartType(name), expectedOriginalStart) is string startModeBlock)
            return startModeBlock;
        using var controller = new ServiceController(name);
        controller.Refresh();
        if (controller.Status != ServiceControllerStatus.Running)
            return "reference service is no longer running";
        if (!controller.CanStop)
            return "service is no longer safely stoppable";
        // Direct probe, not 30-second cache: dependencies may be started by
        // the OS during A/B/A. Unknown statuses are blocked too.
        if (AiDependentServiceProbe.HasRunningOrUnknown(
                controller.DependentServices,
                dependent => dependent.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending))
            return "service now has active/unknown dependents";
        return null;
    }
}
