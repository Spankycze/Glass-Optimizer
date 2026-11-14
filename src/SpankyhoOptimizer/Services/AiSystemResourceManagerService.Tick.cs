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
    public async Task TickAsync(
        bool realIdle,
        double loadPercent,
        CancellationToken token,
        AdaptivePowerRuntimeStatus? currentPowerStatus = null,
        AiSystemRuntimeConfig? currentAgentConfig = null,
        bool cpuEvidenceAvailable = true)
    {
        // Reuse same FanAgent-cycle snapshot, avoiding a second filesystem
        // metadata check and lock. If adaptive sampling failed, read normally.
        AiSystemRuntimeConfig config = currentAgentConfig ??
            AiRuntimeScopePolicy.Sanitize(LoadConfig());
        DateTime now = DateTime.UtcNow;
        // The FanAgent already produced this immutable snapshot moments ago.
        // Avoid re-opening the atomic status JSON on every 1–5s agent tick.
        AdaptivePowerRuntimeStatus? powerStatus =
            !cpuEvidenceAvailable ? null :
            AdaptivePowerRuntimeService.IsStatusFresh(currentPowerStatus, now)
                ? currentPowerStatus
                : AdaptivePowerRuntimeService.ReadStatus();
        bool powerStatusFresh = AdaptivePowerRuntimeService.IsStatusFresh(powerStatus, now);
        bool gameActive = powerStatusFresh && powerStatus?.GameActive == true;
        // The FanAgent CPU estimate and the rounded adaptive-power status
        // must describe the SAME live tick. A recent but unrelated persisted
        // status cannot justify collecting proof or retaining an A/B/A trial.
        bool powerTelemetryComparable = powerStatusFresh &&
            cpuEvidenceAvailable &&
            AiMaintenanceTelemetryAgreementPolicy.Matches(
                powerStatus, loadPercent, realIdle, gameActive, now);
        AiWorkloadClass workload = AiMaintenanceCpuAdmissionPolicy.Workload(
            cpuEvidenceAvailable, powerStatus, realIdle, loadPercent, now);
        // Learn usage habits only from live, comparable evidence.
        if (cpuEvidenceAvailable && powerStatusFresh)
            _forecast.Record(DateTime.Now, !realIdle, gameActive);

        if (!_journalLoadHealthy)
        {
            // Never reinterpret a corrupt/unreadable previous owner snapshot
            // as an empty first-install state. Keep its original file intact.
            // Independently owned process priorities may still be restored.
            _quietLoadSamples.Clear();
            _processPriority.Tick(false, null, AiWorkloadClass.Unknown, now);
            WriteHealth(config, loadPercent, false, realIdle);
            return;
        }

        // A real, user-authorized service opt-out is independent of A/B/A idle
        // experiments: indexing otherwise keeps running while the PC is used.
        // Consent is in admin-owned AI config or user-scoped HKCU; no UI added.
        // FanAgent's existing elevated task owns the mutation and read-back.
        bool searchAutomationEnabled = config.Enabled && config.ServicesEnabled &&
            (config.DisableWindowsSearchIndexing ||
             _searchOptimization.UserRequestsDisable());
        string? searchAction = _searchOptimization.Tick(searchAutomationEnabled);
        if (searchAction != null)
        {
            _lastAction = searchAction;
            AppendAction("service", "WSearch",
                searchAction.Contains("restored", StringComparison.OrdinalIgnoreCase)
                    ? "AI restore" : "automatic opt-out", searchAction);
        }
        if (!string.IsNullOrEmpty(_searchOptimization.LastError))
            _lastError = "Windows Search: " + _searchOptimization.LastError;
        else if (_lastError.StartsWith("Windows Search: ", StringComparison.Ordinal))
            _lastError = string.Empty;

        // Geolocation is a USER-disabled Windows feature only when both
        // machine and current-user privacy stores positively say Deny.
        // Existing elevated FanAgent owns only lfsvc, with original
        // Manual/Running state journaled and restored on consent change.
        string? locationAction = _locationOptimization.Tick(
            config.Enabled && config.ServicesEnabled);
        if (locationAction != null)
        {
            _lastAction = locationAction;
            string kind = locationAction.Contains("restored",
                StringComparison.OrdinalIgnoreCase) ? "AI restore" :
                locationAction.Contains("external",
                    StringComparison.OrdinalIgnoreCase) ? "external override" :
                "automatic opt-out";
            AppendAction("service", "lfsvc", kind, locationAction);
        }
        if (!string.IsNullOrEmpty(_locationOptimization.LastError))
            _lastError = "Geolocation: " + _locationOptimization.LastError;
        else if (_lastError.StartsWith("Geolocation: ", StringComparison.Ordinal))
            _lastError = string.Empty;

        // A user explicitly opted out of Bluetooth on this PC. Only the
        // native Support Service is eligible, with original SCM mode saved,
        // an active Bluetooth peripheral guard and live stopped/disabled
        // read-back. No PnP radio/driver or unrelated service is changed.
        string? bluetoothAction = _bluetoothOptimization.Tick(
            config.Enabled && config.ServicesEnabled &&
            _bluetoothOptimization.UserRequestsDisable());
        if (bluetoothAction != null)
        {
            _lastAction = bluetoothAction;
            string kind = bluetoothAction.Contains("restored",
                StringComparison.OrdinalIgnoreCase) ? "AI restore" :
                bluetoothAction.Contains("external",
                    StringComparison.OrdinalIgnoreCase) ? "external override" :
                "automatic opt-out";
            AppendAction("service", "bthserv", kind, bluetoothAction);
        }
        if (!string.IsNullOrEmpty(_bluetoothOptimization.LastError))
            _lastError = "Bluetooth: " + _bluetoothOptimization.LastError;
        else if (_lastError.StartsWith("Bluetooth: ", StringComparison.Ordinal))
            _lastError = string.Empty;

        // User opted in to Device Manager optimization as well as Bluetooth.
        // Only the positively identified unused Intel BT USB radio is
        // actionable; no network, HID, display, disk or kernel driver changes.
        string? pnpAction = await _unusedPnpDevices.TickAsync(
            config.Enabled && config.OptionalDevicesEnabled && config.ServicesEnabled &&
            _bluetoothOptimization.UserRequestsDisable(),
            _bluetoothOptimization.IsManaged, token).ConfigureAwait(false);
        if (pnpAction != null)
        {
            _lastAction = pnpAction;
            string kind = pnpAction.Contains("enabled", StringComparison.OrdinalIgnoreCase)
                ? "AI restore" :
                pnpAction.Contains("manually", StringComparison.OrdinalIgnoreCase)
                    ? "external override" :
                pnpAction.Contains("recovered", StringComparison.OrdinalIgnoreCase)
                    ? "ownership recovered" : "automatic opt-out";
            AppendAction("device", "Intel USB Bluetooth", kind, pnpAction);
        }
        if (!string.IsNullOrEmpty(_unusedPnpDevices.LastError))
            _lastError = "Optional PnP: " + _unusedPnpDevices.LastError;
        else if (_lastError.StartsWith("Optional PnP: ", StringComparison.Ordinal))
            _lastError = string.Empty;

        // When AI owns the stopped Windows Search service, also disable
        // its still-enabled native maintenance schedule. A separate durable
        // journal restores the exact task when the search opt-out ends.
        string? maintenanceTaskAction = _searchMaintenanceTask.Tick(
            searchAutomationEnabled && config.ScheduledTasksEnabled &&
            _searchOptimization.IsManaged, token);
        if (maintenanceTaskAction != null)
        {
            _lastAction = maintenanceTaskAction;
            string kind = maintenanceTaskAction.Contains("restored",
                StringComparison.OrdinalIgnoreCase) ? "AI restore" :
                maintenanceTaskAction.Contains("recovered",
                    StringComparison.OrdinalIgnoreCase) ? "ownership recovered" :
                maintenanceTaskAction.Contains("manually",
                    StringComparison.OrdinalIgnoreCase) ? "external override" :
                "automatic opt-out";
            AppendAction("task", @"\Microsoft\Windows\Shell\IndexerAutomaticMaintenance",
                kind, maintenanceTaskAction);
        }
        if (!string.IsNullOrEmpty(_searchMaintenanceTask.LastError))
            _lastError = "Windows Search task: " + _searchMaintenanceTask.LastError;
        else if (_lastError.StartsWith("Windows Search task: ", StringComparison.Ordinal))
            _lastError = string.Empty;

        // One immutable agent telemetry epoch is shared by the foreground
        // response A/B/A admission and the subsequent service/task/driver
        // quiet-window gates, including recovery and RAM readback.
        AiSystemSmoothnessHealth? smoothnessSnapshot =
            AiSystemSmoothnessGuardService.ReadAgentSnapshot(now);
        // Independent per-process evidence; no user-selectable Gaming/Work/
        // Stream profile or background process table. Reuses existing
        // adaptive workload + cached smoothness telemetry and restores on OFF.
        bool aiProcessSchedulingAllowed =
            AiMaintenancePowerTelemetryPolicy.MayScheduleProcessTrial(
                powerTelemetryComparable,
                cpuEvidenceAvailable && config.Enabled && config.SelfOptimizationEnabled &&
                config.AutomaticProcessPriorityEnabled && !_recoverInterruptedTrials && !AiPerformanceExperimentCoordinator.IsActive);
        // No native GUI ping when none of the five allowlisted background
        // processes can qualify. Keep collecting during an owned or restored
        // A/B/A phase even when the candidate entered its cooldown.
        // Passing false also invalidates a previous pending WM_NULL result;
        // it never touches the user's foreground window.
        bool responseNeeded = _processPriority.NeedsForegroundResponse(
            aiProcessSchedulingAllowed, powerStatus, workload, now, smoothnessSnapshot);
        _foregroundResponse.QueueIfDue(responseNeeded,
            powerStatus, workload, now);
        _processPriority.Tick(aiProcessSchedulingAllowed,
            powerStatus, workload, now, _foregroundResponse.ReadCurrent(), smoothnessSnapshot);

        // Foreground process A/B/A scheduling runs BEFORE expensive
        // Task Scheduler COM and full Windows inventory work on this tick.
        // Defer the read-only inventory during actual gaming or pressure;
        // the last completed inventory remains valid for display.
        bool inventoryPressure = gameActive ||
            smoothnessSnapshot is { State: not "Normal" } ||
            _processPriority.IsMeasurementInFlight ||
            AiPerformanceExperimentCoordinator.IsActive;
        await _fullInventory.TickAsync(config.Enabled, inventoryPressure, token)
            .ConfigureAwait(false);

        // Optional tasks retain their independent consent, WAL and native
        // read-back, but cannot block this tick's foreground scheduling.
        var taskPreference = _optionalTasks.ReadUserPreference();
        bool taskAutomationEnabled = config.Enabled && config.ScheduledTasksEnabled;
        string? taskAction = _optionalTasks.Tick(
            taskAutomationEnabled && taskPreference.KeepOwned,
            taskAutomationEnabled && taskPreference.AllowNewWrites && !inventoryPressure
                ? TaskAllowlist : Array.Empty<string>(), token);
        if (taskAction != null)
        {
            _lastAction = taskAction;
            string taskActionKind = taskAction.StartsWith("Scheduled tasks restored:",
                StringComparison.Ordinal) ? "AI restore" :
                taskAction.StartsWith("Scheduled task state recovered:",
                    StringComparison.Ordinal) ? "ownership recovered" : "automatic opt-out";
            AppendAction("task", "optional Windows task", taskActionKind, taskAction);
        }
        if (!string.IsNullOrEmpty(_optionalTasks.LastError))
            _lastError = "Optional task: " + _optionalTasks.LastError;
        else if (_lastError.StartsWith("Optional task: ", StringComparison.Ordinal))
            _lastError = string.Empty;

        bool maintenanceCompatible =
            AiSharedWorkloadPolicy.AllowsBackgroundAutomation(workload);
        bool quietCpuComparable = cpuEvidenceAvailable &&
            AiBackgroundPerformancePolicy.IsComparableQuietSample(
                gameActive, realIdle, loadPercent, workload);
        bool smoothnessAllowed = maintenanceCompatible && quietCpuComparable &&
            AiSystemSmoothnessGuardService.AllowsBackgroundExperiment(smoothnessSnapshot, now);
        bool comparableQuietSample =
            AiMaintenancePowerTelemetryPolicy.MayCollectQuietEvidence(
                powerTelemetryComparable,
                maintenanceCompatible && quietCpuComparable && smoothnessAllowed);

        // Run only the existing actionable SSD safety/repair guardian in a
        // compatible quiet window. The unused Windows baseline report used
        // to schedule expensive read-only WMI/registry work here.
        if (AiMaintenanceCpuAdmissionPolicy.MayStartOptionalWork(
                config.Enabled, _recoverInterruptedTrials) &&
            comparableQuietSample && loadPercent <= 20)
            AiStorageGuardianService.ScheduleIfDue(token);

        // Only a truly user-idle interval earns mutation evidence. Read-only
        // background sampling may run while active, but it cannot carry quiet
        // credit into a later unattended service/task A/B/A trial.
        // Interrupted-recovery epochs are always independent.
        bool accrueQuietEvidence = AiMaintenanceCpuAdmissionPolicy.MayAccrueQuietEvidence(
            comparableQuietSample, realIdle, _recoverInterruptedTrials);
        AiBackgroundPerformancePolicy.AddSample(_quietLoadSamples,
            accrueQuietEvidence, loadPercent, now);
        // Observation credit never crosses user input, missed sampling
        // intervals, or backwards OS clock adjustments.
        _quietEpochStartUtc = AiMaintenanceOpportunityPolicy.NextQuietEpochStart(
            _quietEpochStartUtc, _quietLoadSamples, accrueQuietEvidence, now);
        // Do not queue new optional Windows tuning from a cached last-known
        // CPU reading. AI-off still queues restoration of owned tweaks.
        if (AiMaintenancePowerTelemetryPolicy.MayQueueTweaks(
                powerTelemetryComparable, cpuEvidenceAvailable, config.Enabled,
                _recoverInterruptedTrials))
            _tweakRuntime.QueueTick(config, realIdle, loadPercent, gameActive, token);

        // Automatic THIRD-PARTY APP usage learning: only noncritical helper
        // services, optional agent tasks and HKCU startup entries. No new
        // schedulers/timers, no process kills and no app restarts. Restoration
        // on actual app use or AI-off is independent of new-trial admission.
        // Process-absence observation does not require a 60-minute run of
        // CPU <=25%. Those normal interactive CPU bursts are unrelated to
        // whether Epic/Discord/etc. are actually running. Observe ONLY
        // when the smoothness snapshot is fresh, normal and non-game;
        // a native startup write still requires <=25% CPU. No service/task
        // stop or priority trial uses the broader observation-only scope.
        bool mayObserveDormantAppStartup =
            config.Enabled && config.SelfOptimizationEnabled &&
            !_recoverInterruptedTrials && !gameActive &&
            !AiPerformanceExperimentCoordinator.IsActive &&
            !_processPriority.IsMeasurementInFlight &&
            smoothnessSnapshot is {
                State: "Normal", GameActive: false,
                CpuLoadPercent: >= 0 and <= 65,
                FreeRamGiB: >= 1.5
            } &&
            AiSystemSmoothnessGuardService.IsFresh(
                smoothnessSnapshot.UpdatedUtc, now, TimeSpan.FromSeconds(90));
        await _thirdPartyIdle.TickAsync(config,
            canOptimizeNow: comparableQuietSample && realIdle && loadPercent <= 25 &&
                !gameActive && !_recoverInterruptedTrials &&
                !AiPerformanceExperimentCoordinator.IsActive,
            gameActive, token, telemetry: smoothnessSnapshot,
            allowInactiveAppStartup: mayObserveDormantAppStartup &&
                smoothnessSnapshot!.CpuLoadPercent <= 25,
            allowInactiveAppObservation: mayObserveDormantAppStartup)
            .ConfigureAwait(false);
        // An active A/B/A service/task experiment must retain its exclusive
        // cross-process lease across periodic ticks. Do not confuse OUR
        // maintenance lease with another host's or another subsystem's trial.
        if (config.Enabled && AiPerformanceExperimentCoordinator.IsActive &&
            !AiPerformanceExperimentCoordinator.IsOwnedBy("maintenance"))
            return;
        int maintenanceIntervalSeconds = _selfOptimizer.Sample(
            config.Enabled && config.SelfOptimizationEnabled, workload);
        // Only reschedule OWN nonessential work. No OS-wide profile switch.
        maintenanceIntervalSeconds = AiAutomaticWorkloadSafetyPolicy.MaintenanceIntervalSeconds(
            maintenanceIntervalSeconds, workload,
            smoothnessSnapshot, now);
        // Adaptive scheduling: if learned activity is highly predictable,
        // check less often during known busy periods to save CPU.
        var stats = _forecast.GetStatistics();
        if (stats.LearnedHours >= 24 && _forecast.ExpectsActivitySoon(DateTime.Now))
        {
            // During learned busy hours, stretch interval to reduce overhead
            maintenanceIntervalSeconds = Math.Max(maintenanceIntervalSeconds, Math.Min(maintenanceIntervalSeconds * 2, 240));
        }
        // This FanAgent's self-health is authoritative for autonomous
        // mutations. A disk snapshot from a previous agent process is not
        // trustworthy performance evidence and costs a metadata read on each tick.
        AiSelfOptimizationHealth? selfHealth =
            AiSelfOptimizationService.IsSameProcessHealthFresh(_selfOptimizer.CurrentHealth, now)
                ? _selfOptimizer.CurrentHealth : null;
        AiSharedDecisionContext decisionContext = AiSharedDecisionContextService.Update(
            workload,
            realIdle,
            gameActive,
            loadPercent,
            selfHealth,
            now,
            requireFreshSelfHealth: config.Enabled && config.SelfOptimizationEnabled);
        AiEngine2Decision engine2Decision = AiEngine2DecisionPolicy.Evaluate(decisionContext, now);
        // The status and the actual service/task A/B/A admission must use
        // the same user-idle and Engine 2 eligibility decision.
        bool decisionAllowed = AiMaintenanceOpportunityPolicy.AllowsMeasuredTrial(
            decisionContext, engine2Decision);
        // The regular tick needs the managed A/B/A admission decision;
        // interruption recovery is rare and retains its separate safety
        // probe. Do not add work to normal healthy agent ticks.
        bool pendingManagedTrial =
            AiPendingTrialSelectionPolicy.HasPendingManagedTrial(_items.Values);
        // One decision governs both urgent timing and eventual restoration.
        // Losing valid smoothness or becoming interactive must not wait
        // for a normal 45-240s maintenance interval.
        bool stopUnsafeTrials = AiMaintenancePowerTelemetryPolicy.StopOwnedTrial(
            powerTelemetryComparable,
            AiMaintenanceCpuAdmissionPolicy.MustRestoreTrial(
                cpuEvidenceAvailable, workload, gameActive, smoothnessSnapshot, now,
                realIdle));
        // Existing Candidate-B service/task experiments need quick, owned
        // recovery after total CPU telemetry loss. Normal maintenance keeps
        // its longer cadence; an urgent rollback retries at most every 5s.
        bool pendingUnsafeTrial = stopUnsafeTrials && pendingManagedTrial;
        // Quarantined unowned journal intents veto every NEW mutation but
        // cannot be automatically restored. Only owned interrupted work or
        // a failed persistence checkpoint needs the 5-second recovery path.
        // Avoid repeatedly snapshotting an unresolvable journal every 5s.
        bool urgentRecovery = _recoverInterruptedTrials &&
            (_maintenanceWriteFaulted ||
             AiPendingTrialSelectionPolicy.HasActionableRecovery(_items.Values));
        TimeSpan effectiveInterval = AiMaintenanceCpuAdmissionPolicy.TickInterval(
            maintenanceIntervalSeconds, !stopUnsafeTrials, pendingUnsafeTrial,
            urgentRecovery);
        if (IsWithinMonotonicInterval(_lastTickStamp,
                Stopwatch.GetTimestamp(), Stopwatch.Frequency, effectiveInterval))
            return;
        bool maintenanceLease = config.Enabled;
        if (maintenanceLease && !AiPerformanceExperimentCoordinator.TryAcquire("maintenance"))
            return;
        _lastTickStamp = Stopwatch.GetTimestamp();
        try
        {
            _maintenanceWriteFaulted = false;
            // An agent restart loses the measurement continuity between
            // A/B/A samples. Never reuse incomplete samples persisted by a
            // previous process to claim a verified Windows optimization.
            if (_recoverInterruptedTrials)
            {
                await RecoverInterruptedMaintenanceTrialsAsync(token).ConfigureAwait(false);
                _recoverInterruptedTrials =
                    AiPendingTrialSelectionPolicy.HasInterruptedOrPendingJournal(_items.Values);
                // A recovery checkpoint with an already durable identical
                // journal is successful. Prepare/serialize it only once:
                // the old pre-check duplicated sorting and JSON serialization.
                if (!PersistItems(writeAhead: true, unchangedIsSuccess: true))
                {
                    _recoverInterruptedTrials = true;
                    _maintenanceWriteFaulted = true;
                }
                if (_recoverInterruptedTrials)
                {
                    // A failed readback/recovery must never fall through to
                    // ordinary A/B/A sampling or fresh Windows mutations.
                    WriteHealth(config, loadPercent, false, realIdle);
                    return;
                }
            }

            if (!config.Enabled)
            {
                // OFF is also a journal mutation: restoring owned service,
                // task or driver state changes ownership evidence. Persist
                // the outcome before returning, otherwise a later restart
                // could still claim that already-restored resources are owned.
                try
                {
                    await RestoreAllAsync(token, includePerformanceTrials: false).ConfigureAwait(false);
                }
                finally
                {
                    // Even cancellation or an unexpected exception can
                    // follow one completed restore. Never lose its journal.
                    if (!PersistItems(unchangedIsSuccess: true))
                    {
                        _maintenanceWriteFaulted = true;
                        _lastError = "AI OFF obnova: stav vlastnictví se nepodařilo uložit.";
                        LogService.Info("ai-system", _lastError);
                    }
                    WriteHealth(config, loadPercent, false, realIdle);
                }
                return;
            }

            if (config.SecurityEnabled)
                await _securityRuntime.TickAsync(true, token).ConfigureAwait(false);
            else
                await _securityRuntime.RestoreAllAsync(token).ConfigureAwait(false);

            // A/B/A is a quiet-maintenance experiment, not a reason to
            // keep a service disabled while the user has become active.
            // Interactive/game transitions abort and restore promptly;
            // background load retains the current sample-quality guard.
            bool hadPendingFeedback = pendingManagedTrial &&
                await EvaluatePendingMaintenanceFeedbackAsync(
                    comparableQuietSample,
                    stopUnsafeTrials,
                    loadPercent,
                    now,
                    smoothnessSnapshot,
                    token).ConfigureAwait(false);

            // Never admit a second Windows mutation in a tick which began
            // with an owned A/B/A trial. Evaluate costly sample stability only
            // after cadence/lease admission, completed feedback, and ALL other
            // strict security/telemetry/user-idle gates. This short-circuit
            // cannot authorize a trial on missing evidence.
            // Predictive scheduling: a trial needs a calm A/B/A run. Do not
            // start one when the user is usually active/gaming this or next hour.
            bool safeMaintenanceWindow = !pendingManagedTrial && !hadPendingFeedback &&
                powerTelemetryComparable && comparableQuietSample &&
                decisionAllowed &&
                !_forecast.ExpectsActivitySoon(DateTime.Now) &&
                AiBackgroundPerformancePolicy.HasStableMeasuredPressure(_quietLoadSamples);
            _maintenanceMutationCommitted = pendingManagedTrial || hadPendingFeedback;

            // The emergency CPU-outage path exists to RESTORE an owned
            // Candidate-B experiment. Once handled, skip unrelated discovery
            // and driver/service/task enumeration in this same 5s cycle.
            if (AiMaintenanceCpuAdmissionPolicy.RecoveryOnlyWithoutDiscovery(
                    !stopUnsafeTrials, pendingUnsafeTrial,
                    config.ServicesEnabled, config.ScheduledTasksEnabled,
                    config.OptionalDevicesEnabled))
            {
                WriteHealth(config, loadPercent, false, realIdle);
                return;
            }

            // Keep OBSERVING real service/task/driver usage during active
            // sessions, without starting a new A/B/A or driver disable.
            // Reuse already captured power + FanAgent smoothness evidence;
            // the slow 15-minute cadence prevents heavy foreground probing.
            AiMaintenanceSchedulingPolicy.ActiveObservationDecision observation =
                AiMaintenanceSchedulingPolicy.ActiveObservation(
                    config.Enabled, powerTelemetryComparable, gameActive,
                    loadPercent,
                    AiAutomaticPriorityTrialPolicy.CanStartProbeBaseline(
                        smoothnessSnapshot, now));
            bool allowActiveObservation = observation.Allowed;

            var trackedScopes = AiMaintenanceSchedulingPolicy.TrackedScopes(
                _items.Values, config.ServicesEnabled, config.ScheduledTasksEnabled,
                config.OptionalDevicesEnabled);
            bool serviceFast = trackedScopes.Service;
            bool taskFast = trackedScopes.Task;
            bool driverFast = trackedScopes.Driver;

            async Task ScanServicesScopeAsync()
            {
                if (!config.ServicesEnabled)
                {
                    await RestoreKindAsync("service", token).ConfigureAwait(false);
                    return;
                }

                TimeSpan interval = AiMaintenanceOpportunityPolicy.DiscoveryInterval(
                    "service", _lastServiceScanMs);
                bool deepScan = (safeMaintenanceWindow || allowActiveObservation) &&
                    ShouldRunMonotonicDeepScan(ref _lastServiceDeepScanStamp,
                        Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                        AiMaintenanceSchedulingPolicy.DiscoveryCadence(
                            safeMaintenanceWindow, interval, observation.Cadence));
                var scope = AiMaintenanceSchedulingPolicy.ScopeDecision(
                    serviceFast, deepScan, safeMaintenanceWindow);
                if (!scope.Run) return;

                long started = Stopwatch.GetTimestamp();
                await EvaluateServicesAsync(config, scope.QuietEnough,
                    deepScan, powerStatus, token).ConfigureAwait(false);
                _lastServiceScanMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            async Task ScanTasksScopeAsync()
            {
                if (!config.ScheduledTasksEnabled)
                {
                    await RestoreKindAsync("task", token).ConfigureAwait(false);
                    return;
                }

                TimeSpan interval = AiMaintenanceOpportunityPolicy.DiscoveryInterval(
                    "task", _lastTaskScanMs);
                bool deepScan = (safeMaintenanceWindow || allowActiveObservation) &&
                    ShouldRunMonotonicDeepScan(ref _lastTaskDeepScanStamp,
                        Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                        AiMaintenanceSchedulingPolicy.DiscoveryCadence(
                            safeMaintenanceWindow, interval, observation.Cadence));
                var scope = AiMaintenanceSchedulingPolicy.ScopeDecision(
                    taskFast, deepScan, safeMaintenanceWindow);
                if (!scope.Run) return;

                long started = Stopwatch.GetTimestamp();
                await EvaluateTasksAsync(config, scope.QuietEnough,
                    deepScan, powerStatus, token).ConfigureAwait(false);
                _lastTaskScanMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            async Task ScanDriversScopeAsync()
            {
                if (!config.OptionalDevicesEnabled)
                {
                    await RestoreKindAsync("driver", token).ConfigureAwait(false);
                    return;
                }

                TimeSpan interval = AdaptiveScanInterval(TimeSpan.FromMinutes(5), _lastDriverScanMs);
                bool deepScan = (safeMaintenanceWindow || allowActiveObservation) &&
                    ShouldRunMonotonicDeepScan(ref _lastDriverDeepScanStamp,
                        Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                        AiMaintenanceSchedulingPolicy.DiscoveryCadence(
                            safeMaintenanceWindow, interval, observation.Cadence));
                var scope = AiMaintenanceSchedulingPolicy.ScopeDecision(
                    driverFast, deepScan, safeMaintenanceWindow);
                if (!scope.Run) return;

                long started = Stopwatch.GetTimestamp();
                EvaluateSafeDrivers(config, quietEnough: scope.QuietEnough, powerStatus);
                _lastDriverScanMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            int cycle = _maintenanceKindCursor;
            _maintenanceKindCursor = AiMaintenanceSchedulingPolicy.NextCycle(cycle);
            for (int kindIndex = 0; kindIndex < AiMaintenanceSchedulingPolicy.KindCount; kindIndex++)
            {
                string kind = AiMaintenanceSchedulingPolicy.KindAt(cycle, kindIndex);
                if (kind == "service")
                    await ScanServicesScopeAsync().ConfigureAwait(false);
                else if (kind == "task")
                    await ScanTasksScopeAsync().ConfigureAwait(false);
                else
                    await ScanDriversScopeAsync().ConfigureAwait(false);
            }

            bool stateChanged = PersistItems();
            if (config.CloudSyncEnabled &&
                AiCloudProfilePolicy.ShouldBackgroundSync(workload) &&
                AiAutomaticWorkloadSafetyPolicy.CanUploadOwnCloudProfile(
                    workload, smoothnessSnapshot, DateTime.UtcNow) &&
                (stateChanged ||
                 !IsWithinMonotonicInterval(_lastCloudSyncStamp,
                     Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                     TimeSpan.FromMinutes(15))))
            {
                AiCloudProfileService.SyncBestEffort(config, _items.Values);
                _lastCloudSyncStamp = Stopwatch.GetTimestamp();
            }
            WriteHealth(config, loadPercent, false, realIdle);
        }
        catch (Exception ex)
        {
            _lastError = ex.GetBaseException().Message;
            LogService.Error("ai-system", "AI System Runtime tick failed", ex);
            WriteHealth(config, loadPercent, false, realIdle);
        }
        finally
        {
            // Releasing after EVERY tick creates a race while Candidate B
            // is still held across ticks. Keep the OS-exclusive file lease
            // until the owned measured trial and actionable write-ahead
            // recovery are complete. OFF must ALSO retain an existing lease
            // if the attempted restore failed and an owned change remains.
            // Restoration above may have changed _items during awaits:
            // inspect CURRENT ownership, never pre-await admission results.
            // Lease retention is an OR decision, so a single short-circuit
            // scan skips the redundant second dictionary traversal.
            if (!AiPendingTrialSelectionPolicy.NeedsMaintenanceLease(_items.Values))
                AiPerformanceExperimentCoordinator.Release("maintenance");
        }
    }

    // Dedicated CPU-priority OFF path: restore only AI-owned process state,
    // without touching services, tasks, drivers or the power plan.
}
