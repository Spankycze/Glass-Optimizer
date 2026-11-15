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
    private async Task EvaluateServicesAsync(
        AiSystemRuntimeConfig config,
        bool quietEnough,
        bool fullDiscovery,
        AdaptivePowerRuntimeStatus? candidatePowerStatus,
        CancellationToken token)
    {
        bool fastRecoveryOnly = !quietEnough && !fullDiscovery;
        List<string>? recoveryCandidates = null;
        if (fastRecoveryOnly)
        {
            // The foreground fast cycle cannot start a service trial.
            // Observe live only AI-owned, sleeping or write-ahead-pending
            // services, plus independent Wi-Fi/print usage demand sensors.
            for (int i = 0; i < ServiceAllowlist.Length; i++)
            {
                // An absent row cannot own a Windows change. In particular,
                // never CREATE 33 inert journal rows just to skip them here.
                // Static precomputed keys also avoid per-tick string churn.
                string key = ServiceAllowlistKeys[i];
                if (!_items.TryGetValue(key, out AiSystemRuntimeItemState? state))
                    continue;
                if (AiMaintenanceSchedulingPolicy.NeedsFastServiceRuntimeRead(
                        quietEnough, fullDiscovery, state))
                {
                    (recoveryCandidates ??= new List<string>(4)).Add(ServiceAllowlist[i]);
                }
                else
                {
                    AiSystemRuntimeItemState suspended =
                        AiMaintenanceSchedulingPolicy.SuspendUnownedServiceObservation(state);
                    if (!ReferenceEquals(suspended, state))
                        _items[key] = suspended;
                }
            }
        }
        var unreadableServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, ServiceControllerStatus> serviceStates =
            fastRecoveryOnly
                ? GetFastServiceStatusSnapshot(recoveryCandidates, unreadableServices)
                : GetServiceStatusSnapshot(unreadableServices);
        // Extend the live AI inventory without adding unsafe stop permissions.
        // Reuse the one existing service snapshot: no extra WMI/process launch.
        AiPrinterQueueEvidence printEvidence = default;
        bool printEvidenceRead = false;
        void ObserveOptionalFeature(AiOptionalWindowsFeature feature, ServiceControllerStatus status)
        {
                string id = feature.ServiceName;
                AiSystemRuntimeItemState entry = GetState("service", id);
                if (entry.Managed) return;
                AiOptionalFeatureObservation? previous =
                    entry.FeatureFirstObservedUtc is DateTime first &&
                    entry.FeatureLastObservedUtc is DateTime last &&
                    entry.FeatureObservedRunning is bool previousRunning
                        ? new(first, last, entry.FeatureObservationSamples, previousRunning)
                        : null;
                // A paused or transitioning service is NOT an inactivity sample.
                // Treat every non-stopped state as potentially needed; it must
                // break any accumulated long-idle observation window.
                bool running = status != ServiceControllerStatus.Stopped;
                DateTime observedAt = DateTime.UtcNow;
                AiOptionalFeatureObservation next =
                    AiOptionalFeatureObservationPolicy.Next(previous, observedAt, running);
                bool automaticallyEligible = AutomaticServiceCandidates.Contains(id);
                // Count both a completed restart and its StartPending edge.
                // Otherwise a rapid Stopped->StartPending->Running transition
                // could consume the only stopped observation without ever
                // recording actual OS/application demand.
                bool newlyActivated = automaticallyEligible &&
                    AiOptionalFeatureObservationPolicy.WasRecentlyStarted(
                        previous, status, observedAt);
                bool deviceProbe = id.Equals("WlanSvc", StringComparison.OrdinalIgnoreCase) ||
                    feature.Area == "Tiskárny";
                bool deviceProbeDue = deviceProbe &&
                    AiOptionalFeatureProbeCadencePolicy.Due(entry.FeatureLastUsageProbeUtc, observedAt);
                // A confirmed Wi-Fi connection or queued print job must be seen
                // within minutes, even during a ten-minute unchanged-state epoch.
                if (!AiOptionalFeatureProbeCadencePolicy.MustRefresh(previous, next, deviceProbeDue))
                    return;
                bool longStopped = AiOptionalFeatureObservationPolicy.HasLongStoppedObservation(
                    next, observedAt);
                string reason = AiOptionalWindowsFeatureCatalog.ObservationReason(feature, status);
                AiOptionalFeatureUsage observedUsage = newlyActivated
                    ? AiOptionalFeatureUsage.InUse : AiOptionalFeatureUsage.Unknown;
                if (newlyActivated)
                    reason += "; AI: právě spuštěná služba; nový pokus odložen";
                if (id.Equals("WlanSvc", StringComparison.OrdinalIgnoreCase))
                {
                    AiOptionalFeatureUsage wifiEvidence = AiOptionalWirelessUsageProbe.Read();
                    observedUsage = newlyActivated
                        ? AiOptionalFeatureUsage.InUse : wifiEvidence;
                    reason += "; " + AiOptionalWirelessUsageProbe.Describe(observedUsage);
                }
                if (feature.Area == "Tiskárny")
                {
                    if (!printEvidenceRead)
                    {
                        printEvidenceRead = true;
                        bool spoolerRunning = serviceStates.TryGetValue("Spooler", out var spooler) &&
                            spooler == ServiceControllerStatus.Running;
                        printEvidence = AiOptionalPrinterUsageProbe.Read(spoolerRunning);
                    }
                    AiOptionalFeatureUsage printerEvidence =
                        AiOptionalPrinterUsageProbe.Classify(printEvidence);
                    observedUsage = newlyActivated
                        ? AiOptionalFeatureUsage.InUse : printerEvidence;
                    reason += "; " + AiOptionalPrinterUsageProbe.Describe(printEvidence);
                }
                DateTime? lastConfirmedUse = AiOptionalFeatureRecentUsePolicy.LastConfirmedUse(
                    entry.FeatureLastConfirmedUseUtc, observedUsage, observedAt);
                AiOptionalFeatureUsage effectiveUsage = AiOptionalFeatureRecentUsePolicy.Effective(
                    observedUsage, lastConfirmedUse, observedAt);
                if (effectiveUsage == AiOptionalFeatureUsage.InUse &&
                    observedUsage != AiOptionalFeatureUsage.InUse)
                    reason += "; potvrzené využití v posledních 24 h; vyžaduje zachování";
                if (longStopped)
                    reason += "; 6 h bez běhu při kontrolních vzorcích (mezi nimi neověřeno)";
                _items[Key("service", id)] = entry with
                {
                    BlockedReason = automaticallyEligible ? entry.BlockedReason : reason,
                    FeatureFirstObservedUtc = next.FirstUtc,
                    FeatureLastObservedUtc = next.LastUtc,
                    FeatureObservationSamples = next.Samples,
                    FeatureObservedRunning = next.WasRunning,
                    FeatureObservedUsage = effectiveUsage,
                    FeatureLastConfirmedUseUtc = lastConfirmedUse,
                    FeatureLastUsageProbeUtc = deviceProbe ? observedAt : null
                };
        }
        // Observe approved candidates too: an observed Stopped->Running
        // transition is a real OS/app demand signal and vetoes a fresh A/B/A.
        // Previously they were excluded from the optional observation catalog.
        // The monitoring path never grants new service-stop permissions.
        foreach (string candidate in ServiceAllowlist)
        {
            if (serviceStates.TryGetValue(candidate, out ServiceControllerStatus current))
                ObserveOptionalFeature(new AiOptionalWindowsFeature(candidate, "AI kandidáti"), current);
        }
        AiOptionalWindowsFeatureCatalog.ObserveRuntimeStates(
            serviceStates, AutomaticServiceCandidates, ObserveOptionalFeature);
        AiOptionalPerUserWindowsFeatureCatalog.ObserveRuntimeStates(
            serviceStates, AutomaticServiceCandidates, ObserveOptionalFeature);
        // User service instance IDs change between Windows logons. Remove only
        // abandoned read-only observations, never owned trials or rollback data.
        DateTime pruneNow = DateTime.UtcNow;
        // A narrow fast snapshot never means an optional per-user service
        // disappeared. Only the full SCM inventory may authorize pruning.
        if (!fastRecoveryOnly &&
            ShouldRunDeepScan(ref _lastPerUserObservationPruneUtc, pruneNow,
                AiOptionalPerUserObservationRetentionPolicy.CleanupInterval))
            AiOptionalPerUserObservationRetentionPolicy.Prune(_items, serviceStates, pruneNow);

        // Fast active passes have no new service-trial authority: skip
        // sorting and visiting the entire allowlist a second time.
        // Full discovery and quiet mutation windows retain rotated A/B/A
        // opportunity ranking exactly as before.
        int[]? serviceOrder = null;
        string[]? installedServiceCandidates = null;
        if (!fastRecoveryOnly)
        {
            // Use the SCM snapshot ALREADY read for this tick instead of
            // retrying obsolete/optional services which are not registered.
            // The full source list and old journal remain for recovery.
            installedServiceCandidates = WindowsComponentCatalogPolicy.PresentInSnapshot(
                ServiceAllowlist,
                new HashSet<string>(serviceStates.Keys, StringComparer.OrdinalIgnoreCase),
                "service", Environment.OSVersion.Version.Build);
            int serviceStart = _serviceCandidateCursor;
            _serviceCandidateCursor = AiMaintenanceCandidatePolicy.NextCursor(
                serviceStart, installedServiceCandidates.Length);
            serviceOrder = AiMaintenanceOpportunityPolicy.OrderedIndices(
                "service", installedServiceCandidates, serviceStart, _items, DateTime.UtcNow);
        }
        int serviceCount = fastRecoveryOnly
            ? recoveryCandidates?.Count ?? 0 : installedServiceCandidates!.Length;
        for (int offset = 0; offset < serviceCount; offset++)
        {
            string name = fastRecoveryOnly
                ? recoveryCandidates![offset] : installedServiceCandidates![serviceOrder![offset]];
            token.ThrowIfCancellationRequested();
            if (IsCriticalService(name))
            {
                SetBlocked("service", name, "critical denylist");
                continue;
            }

            AiSystemRuntimeItemState state = GetState("service", name);
            if (AiMaintenanceWriteAheadPolicy.IsUnownedPending(state))
            {
                _recoverInterruptedTrials = true;
                SetBlocked("service", name,
                    "pending journal without confirmed AI ownership; no Windows write");
                continue;
            }
            if (AiMaintenanceCandidatePolicy.ShouldDeferForCooldown(state, DateTime.UtcNow))
                continue;
            if (!state.Managed && state.Rollbacks >= 2)
            {
                SetBlocked("service", name, "AI learned protection • opakovaně obnoveno systémem");
                continue;
            }

            try
            {
                if (!serviceStates.TryGetValue(name, out ServiceControllerStatus status))
                {
                    // Missing in a COMPLETE SCM enumeration != a known
                    // installed service whose status could not be read.
                    SetBlocked("service", name, unreadableServices.Contains(name)
                        ? "service status unreadable" : "not installed");
                    continue;
                }

                if (!state.Managed && status != ServiceControllerStatus.Running)
                {
                    ResetObservation("service", name);
                    continue;
                }

                // Skip unnecessary registry ownership probes and controller
                // construction while the PC is interactive or gaming.
                // AI-owned resources still proceed to prompt wake/recovery.
                if (!state.Managed && !quietEnough)
                {
                    ResetObservation("service", name);
                    continue;
                }

                if (!IsWindowsOwnedServiceEntry(name))
                {
                    // Never execute an altered/unverifiable service image,
                    // even to restore it. Retain AI-owned journal evidence
                    // instead of silently losing the recovery obligation.
                    SetBlocked("service", name, state.Managed && state.Sleeping
                        ? "cesta spravované služby se změnila; ruční obnova nutná, AI záznam zachován"
                        : "není potvrzená Windows/System32 komponenta");
                    continue;
                }

                using var controller = new ServiceController(name);
                int managedLiveStart = state.Managed && state.Sleeping
                    ? ReadServiceStartType(name) : -1;
                if (state.Managed && state.Sleeping &&
                    AiMaintenanceServiceStartPolicy.MustRetainOwnerOnUnknownStart(managedLiveStart))
                {
                    SetBlocked("service", name,
                        "live service Start unreadable; AI journal retained for safe retry");
                    continue;
                }

                if (state.Managed && state.Sleeping && status == ServiceControllerStatus.Running)
                {
                    int liveStart = managedLiveStart;
                    string wakeDecision = AiVerifiedServiceHoldPolicy.WakeupDecision(
                        state.OriginalServiceStart, liveStart);
                    if (wakeDecision != "restore-owned")
                    {
                        // Windows has already restarted the service. Do not
                        // overwrite a user/OS Start-mode change just because
                        // our previous Candidate B once owned the service.
                        DateTime releasedAt = DateTime.UtcNow;
                        int learned = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(
                            state.Rollbacks, externallyNeeded: true);
                        _items[Key("service", name)] =
                            AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                                AiMaintenanceOwnershipPolicy.Relinquish(
                                    state, releasedAt,
                                    "service restarted; start mode " + wakeDecision,
                                    TimeSpan.FromHours(12)) with { Rollbacks = learned },
                                externallyNeeded: true);
                        _restoredCount++;
                        _lastAction = $"Service {name}: Windows start preference preserved";
                        AppendAction("service", name, "external service restart",
                            "AI relinquished without Start write: " + wakeDecision);
                        continue;
                    }

                    await RestoreServiceAsync(state, controller,
                        "service needed again", token).ConfigureAwait(false);
                    continue;
                }

                // An externally edited Start type invalidates AI ownership
                // immediately, not only after the verified six-hour hold ends.
                // Never reapply a service stop or repair the user/OS preference.
                if (state.Managed && state.Sleeping && state.PerformanceVerified &&
                    !AiVerifiedServiceHoldPolicy.ServiceStartModeStillOwned(
                        state.OriginalServiceStart, managedLiveStart))
                {
                    DateTime overriddenAt = DateTime.UtcNow;
                    _items[Key("service", name)] =
                        AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                            AiMaintenanceOwnershipPolicy.Relinquish(
                                state, overriddenAt,
                                "Windows/user changed service Start; AI ownership released",
                                TimeSpan.FromHours(12)),
                            externallyNeeded: true);
                    _lastAction = $"Service {name}: external Start preference preserved";
                    AppendAction("service", name, "external override",
                        "verified hold released immediately; no service Start write");
                    continue;
                }

                // Even an A/B/A-verified service stop is temporary. Expire
                // after 6h and re-observe normal Windows use before trying B
                // again; this is not evidence the OS rejected the optimization.
                if (AiVerifiedServiceHoldPolicy.ShouldRelease(state, DateTime.UtcNow))
                {
                    controller.Refresh();
                    int currentStart = ReadServiceStartType(name);
                    if (AiMaintenanceServiceStartPolicy.MustRetainOwnerOnUnknownStart(currentStart))
                    {
                        SetBlocked("service", name,
                            "hold release Start unreadable; original journal retained");
                        continue;
                    }
                    if (!AiVerifiedServiceHoldPolicy.ServiceStartModeStillOwned(
                            state.OriginalServiceStart, currentStart))
                    {
                        // User or OS modified the start mode after our B.
                        // Yield ownership without rewriting their choice.
                        DateTime releasedAt = DateTime.UtcNow;
                        // Revoke journal ownership entirely. An external Start
                        // mode change is not an AI restoration permission; old
                        // measured savings cannot outrank real Windows demand.
                        _items[Key("service", name)] =
                            AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                                AiMaintenanceOwnershipPolicy.Relinquish(
                                    state, releasedAt,
                                    "start mode changed externally; AI yielded",
                                    TimeSpan.FromHours(12)),
                                externallyNeeded: true);
                        _lastAction = $"Service {name}: manual/system start preference preserved";
                        AppendAction("service", name, "external override",
                            "start type changed while verified optimization was held");
                    }
                    else
                    {
                        await RestoreServiceAsync(state, controller,
                            "verified service hold expired", token,
                            externallyNeeded: false).ConfigureAwait(false);
                    }
                    continue;
                }

                // Read-only device demand history is a one-way veto for any
                // future allowlisted optional service. It cannot grant a stop.
                // Managed resources still take their existing wake/recovery path.
                if (!state.Managed && AiOptionalServiceDemandPolicy.HasRecentDemand(
                        state, DateTime.UtcNow))
                {
                    SetBlocked("service", name,
                        "confirmed recent device use; service trial deferred");
                    continue;
                }

                if (!quietEnough || status != ServiceControllerStatus.Running)
                {
                    ResetObservation("service", name);
                    continue;
                }

                if (!controller.CanStop)
                {
                    SetBlocked("service", name, "service nelze bezpečně zastavit");
                    continue;
                }

                bool dependentRunning = HasRunningDependents(controller, name);
                if (dependentRunning)
                {
                    SetBlocked("service", name, "running dependent service");
                    continue;
                }

                DateTime observationNow = DateTime.UtcNow;
                DateTime observed = AiMaintenanceOpportunityPolicy.CandidateObservationStart(
                    observationNow, _quietEpochStartUtc ?? observationNow,
                    state.ObservationSinceUtc, config.ObservationSeconds, "service", state);
                state = state with { ObservationSinceUtc = observed, BlockedReason = string.Empty };
                _items[Key("service", name)] = state;
                if (observationNow - observed < TimeSpan.FromSeconds(AiMaintenanceOpportunityPolicy.CandidateObservationSeconds(
                    config.ObservationSeconds, "service", state, observationNow)))
                    continue;
                if (_maintenanceMutationCommitted)
                    continue;

                double baselineLoad = AiBackgroundPerformancePolicy.MedianLoad(_quietLoadSamples);
                if (!double.IsFinite(baselineLoad) || baselineLoad < 0.75)
                {
                    SetBlocked("service", name, "baseline výkonu není dostatečně stabilní");
                    continue;
                }

                // The 30-second dependent-service cache is useful for
                // observation, but never authoritative at mutation time.
                // Re-evaluate live service state and dependencies immediately
                // before stopping it for the initial measured experiment.
                if (TryGetServiceReapplyBlockReason(name) is string freshServiceBlock)
                {
                    SetBlocked("service", name,
                        "revalidated before AI trial: " + freshServiceBlock);
                    continue;
                }

                int originalStart = ReadServiceStartType(name);
                if (AiMaintenanceServiceStartPolicy.BlockReason(originalStart, null)
                    is string unsupportedStart)
                {
                    SetBlocked("service", name, unsupportedStart);
                    continue;
                }
                bool wasRunning = status == ServiceControllerStatus.Running;
                // The existing smoothness sampler measured this before any
                // service mutation; an absent snapshot stays inconclusive.
                // A service can become unsafe while the synchronous live
                // dependency/readback checks are running. Recheck at the
                // WRITE boundary, using current-process evidence only.
                DateTime trialNow = DateTime.UtcNow;
                AiSystemSmoothnessHealth? trialSnapshot =
                    AiSystemSmoothnessGuardService.ReadAgentSnapshot(trialNow);
                if (!AiMaintenanceBoundaryEvidencePolicy.CanStartNewTrial(
                        candidatePowerStatus, trialNow,
                        AiSystemSmoothnessGuardService.AllowsBackgroundExperiment(
                            trialSnapshot, trialNow), _recoverInterruptedTrials))
                {
                    SetBlocked("service", name,
                        "fresh AI CPU/RAM/disk quiet evidence lost before service mutation");
                    continue;
                }
                double? baselineAvailableRam = AiPassiveMemoryTrialPolicy.FreshAvailableMiB(
                    trialSnapshot, trialNow, null, null);
                // Revalidate recently confirmed device usage at the write
                // boundary; the original candidate scan is not admission proof.
                if (AiOptionalServiceDemandPolicy.HasRecentDemand(
                        GetState("service", name), trialNow))
                {
                    SetBlocked("service", name,
                        "new device-use evidence before service write; trial cancelled");
                    continue;
                }

                // The device/workload gate is conservative, and the live
                // service start/dependency state can change while telemetry
                // is read. Revalidate original ownership immediately before
                // journaling and stopping a service.
                if (TryGetServiceReapplyBlockReason(name, originalStart)
                    is string finalServiceBlock)
                {
                    SetBlocked("service", name,
                        "final service trial gate: " + finalServiceBlock);
                    continue;
                }

                // Journal the original state BEFORE the first OS write. A
                // crash after Stop but before normal persistence is recoverable.
                AiSystemRuntimeItemState intent = state with
                {
                    Managed = true,
                    Sleeping = true,
                    OriginalServiceStart = originalStart,
                    WasRunningBefore = wasRunning,
                    VerificationStartedUtc = DateTime.UtcNow,
                    VerificationPhase = "CandidateB",
                    PerformanceVerified = false,
                    PendingMutation = AiMaintenanceWriteAheadPolicy.ServiceStop
                };
                BeginWriteAheadMutation(state, intent);

                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                if (controller.Status != ServiceControllerStatus.Stopped)
                    throw new InvalidOperationException($"{name} nepotvrdila Stop.");

                if (originalStart == 2)
                    WriteServiceStartType(name, 3);

                // The initial candidate may look stopped while Windows
                // refused the Auto->Demand setting. Never commit a trial
                // unless BOTH SCM status and live Start are confirmed.
                controller.Refresh();
                int candidateStart = ReadServiceStartType(name);
                if (AiMaintenanceRevalidationPolicy.ServiceCandidateReadbackReason(
                        originalStart, candidateStart, controller.Status)
                    is string candidateFailure)
                    throw new InvalidOperationException(
                        "initial Candidate B service readback: " + candidateFailure);

                AiSystemRuntimeItemState completed = MarkTrialStarted(
                    intent with
                    {
                        Managed = true,
                        Sleeping = true,
                        OriginalServiceStart = originalStart,
                        WasRunningBefore = wasRunning
                    },
                    config.ActionCooldownSeconds, baselineLoad, baselineAvailableRam);
                CommitWriteAheadMutation(intent, completed);
                _quietLoadSamples.Clear();
                _maintenanceMutationCommitted = true;
                _lastAction = $"Service {name}: measured background-pressure trial";
                AppendAction("service", name, "stopped", $"measured background pressure; originalStart={originalStart}");
                LogService.Info("ai-system", $"AI_SYSTEM_SERVICE_SLEEP name={name} originalStart={originalStart} measuredPressure=true");
                return;
            }
            catch (Exception ex)
            {
                if (AiMaintenanceWriteAheadPolicy.IsPending(GetState("service", name)))
                    _recoverInterruptedTrials = true;
                SetBlocked("service", name, ex.GetBaseException().Message);
            }
        }
    }
}
