using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

internal sealed partial class AiAutomaticProcessPriorityService
{
    internal void Tick(
        bool enabled, AdaptivePowerRuntimeStatus? power,
        AiWorkloadClass workload, DateTime nowUtc,
        AiForegroundWindowResponsePolicy.Snapshot? foregroundResponse = null,
        AiSystemSmoothnessHealth? agentSmoothness = null)
    {
        EnsureLoaded();
        if (_journalFaulted)
        {
            Status = "Procesy: bezpečnostní blokace (nečitelný záznam změn)";
            return;
        }
        if (_recoveryPending)
        {
            // An agent crash may have left an AI-adjusted priority behind.
            // Restore BEFORE attempting any other operation.
            if (!RestoreOwned(nowUtc, "obnova po restartu AI"))
                return;
            _recoveryPending = false;
            _nextEligibleUtc = nowUtc.Add(AiAutomaticProcessPriorityPolicy.Cooldown);
        }

        // AI-off must clear a pending candidate immediately rather than wait
        // for the nominal 25-second evaluation interval. Owned priorities and
        // post-restore references still pass through the rollback logic below.
        if (!enabled && _owned == null && _restoredReference == null)
        {
            // Preserve journal recovery above, but clear trial buffers only on
            // the first disabled idle tick instead of on every FanAgent tick.
            if (!_disabledIdleCleared)
            {
                ResetCandidate();
                _candidateCpuWindow = default;
                Status = "Procesy: automatika vypnutá";
                _disabledIdleCleared = true;
            }
            return;
        }
        _disabledIdleCleared = false;

        bool fresh = AdaptivePowerRuntimeService.IsStatusFresh(power, nowUtc);
        bool active = enabled && fresh;
        // Critical: the 25s nominal sampling throttle must NOT delay restoring
        // a BelowNormal process after a RAM/CPU/disk pressure spike. Reuse the
        // already sampled smoothness snapshot (no extra native telemetry).
        AiSystemSmoothnessHealth? liveSmooth =
            _owned != null || _restoredReference != null
                ? agentSmoothness ?? AiSystemSmoothnessGuardService.ReadAgentSnapshot(nowUtc) : null;
        bool urgentPressure = AiAutomaticPriorityTrialPolicy.UrgentOwnedRollback(
            _owned != null || _restoredReference != null, liveSmooth, nowUtc);
        bool epicGameProtection = AiAutomaticProcessPriorityPolicy.EpicGameProtectionRequired(
            _owned?.Name ?? _restoredReference?.Name, power?.GameActive == true);
        bool shouldCheckNow = (_restoredReference is RestoredReference pendingA2 &&
            (epicGameProtection || urgentPressure || AiAutomaticPriorityTrialPolicy.AbortRestoredReferenceImmediately(
                enabled, fresh, pendingA2.Workload,
                workload, power?.GameActive == true) ||
             !string.Equals(power?.ForegroundProcess, pendingA2.ForegroundProcess,
                 StringComparison.OrdinalIgnoreCase) ||
             AiPriorityProcessIdentityPolicy.IdentityChanged(
                 power, pendingA2.Name, pendingA2.TargetPid,
                 pendingA2.ForegroundProcessId) ||
             AiAutomaticPriorityTrialPolicy.SystemLoadDrifted(
                 power, pendingA2.BaselineSystemCpuPercent))) ||
            _owned != null &&
            (epicGameProtection || urgentPressure ||
             AiPriorityTrialMemoryPolicy.MustCheckOwnedImmediately(
                enabled, fresh, _owned.Name, power?.ForegroundProcess,
                workload, _owned.AppliedUtc, nowUtc) ||
             !AiAutomaticPriorityTrialPolicy.SameWorkload(
                 _owned.Workload, workload, power?.GameActive == true) ||
             !string.Equals(power?.ForegroundProcess, _owned.ForegroundProcess,
                 StringComparison.OrdinalIgnoreCase) ||
             AiPriorityProcessIdentityPolicy.IdentityChanged(power, _owned.Name,
                 _owned.Pid, _owned.ForegroundProcessId) ||
             AiAutomaticPriorityTrialPolicy.SystemLoadDrifted(
                 power, _owned.BaselineSystemCpuPercent));
        TimeSpan cadence = AiAutomaticProcessPriorityPolicy.ProbeCadence(
            _candidateObservations > 0, _owned != null || _restoredReference != null);
        if (!shouldCheckNow && _lastTickUtc != DateTime.MinValue &&
            nowUtc >= _lastTickUtc &&
            nowUtc - _lastTickUtc < cadence)
            return;
        _lastTickUtc = nowUtc;

        if (_owned != null)
        {
            // Evaluated before the throttle: an urgent pressure rollback uses
            // exactly this same snapshot and skips no 25-second intervals.
            bool pressureOrStale = urgentPressure;
            // Windows Search can become foreground while a previously sampled
            // "chrome" window still appears in the immutable CPU snapshot.
            // Check the live single foreground PID/name on each owned Search
            // tick. No full process scan and no new independent timer.
            string? liveForeground = null;
            if (AiAutomaticProcessPriorityPolicy.IsWindowsSearchIndexer(_owned.Name))
            {
                int livePid = ForegroundProcessProbe.GetProcessId();
                liveForeground = AiPriorityTrialMemoryPolicy.CanReuseForegroundName(
                    power?.ForegroundProcessId ?? 0, livePid, power?.ForegroundProcess)
                    ? power!.ForegroundProcess : ForegroundProcessProbe.GetName();
            }
            bool searchUiActive = AiPriorityTrialMemoryPolicy.SearchUserActive(
                _owned.Name, power?.ForegroundProcess, liveForeground);
            AiAutomaticPriorityTrialPolicy.Observation comparable =
                AiAutomaticPriorityTrialPolicy.Comparable(
                    power, nowUtc, _owned.Name, _owned.ForegroundProcess,
                    _owned.BaselineSystemCpuPercent, _owned.AppliedUtc,
                    _owned.Pid, _owned.ForegroundProcessId);
            if (comparable.Comparable &&
                power!.UpdatedUtc > _candidateCpuWindow.LastUpdatedUtc)
            {
                _candidateCpuWindow = AiAutomaticPriorityTrialPolicy.AddDistinct(
                    _candidateCpuWindow, power, _owned.Name);
                _candidateMemoryWindow = AiPriorityProcessMemoryPolicy.AddDistinct(
                    _candidateMemoryWindow, power, _owned.Name);
            }
            bool evidenceDeadline = nowUtc - _owned.AppliedUtc >=
                AiAutomaticPriorityTrialPolicy.EvidenceDeadline &&
                _candidateCpuWindow.Count < AiAutomaticPriorityTrialPolicy.RequiredCandidateSnapshots;
            bool responseRequired = _baselineResponseWindow.Count >=
                AiForegroundWindowResponsePolicy.RequiredSamples;
            bool responseWindowChanged = responseRequired &&
                AiForegroundWindowResponsePolicy.WindowSwitched(
                    foregroundResponse, _owned.ForegroundProcessId,
                    _baselineResponseWindow.WindowHandle, _owned.AppliedUtc, nowUtc);
            if (responseRequired && !responseWindowChanged)
                _candidateResponseWindow = AiForegroundWindowResponsePolicy.AddDistinct(
                    _candidateResponseWindow, foregroundResponse,
                    _owned.ForegroundProcessId, _owned.AppliedUtc, nowUtc);
            bool responseWorkloadChanged = responseRequired &&
                workload != AiWorkloadClass.Interactive;
            bool responseUnstable = responseRequired &&
                _candidateResponseWindow.Count >= AiForegroundWindowResponsePolicy.RequiredSamples &&
                !AiForegroundWindowResponsePolicy.StableWindow(_candidateResponseWindow);
            bool responseRegressed = responseRequired &&
                AiForegroundWindowResponsePolicy.SignificantRegression(
                    _baselineResponseWindow, _candidateResponseWindow);
            bool responseProbeFailed = responseRequired &&
                AiForegroundWindowResponsePolicy.NativeProbeFailedDuringTrial(
                    foregroundResponse, _owned.ForegroundProcessId,
                    _owned.AppliedUtc, nowUtc);
            bool identityChanged = AiPriorityProcessIdentityPolicy.IdentityChanged(
                power, _owned.Name, _owned.Pid, _owned.ForegroundProcessId);
            bool workloadChanged = !AiAutomaticPriorityTrialPolicy.SameWorkload(
                _owned.Workload, workload, power?.GameActive == true);
            bool nonComparable = active && !comparable.Comparable &&
                nowUtc - _owned.AppliedUtc >= AiAutomaticProcessPriorityPolicy.MinimumObservation;
            // B can end early only with enough stable, current target CPU
            // evidence and, where required, matching stable WM_NULL response.
            // The original three-minute hold remains a hard upper bound.
            bool earlyReferenceReady =
                AiAutomaticPriorityTrialPolicy.ReadyForRestoredReference(
                    _candidateCpuWindow, _baselineResponseWindow,
                    _candidateResponseWindow, _owned.AppliedUtc, nowUtc);

            bool restore = epicGameProtection || earlyReferenceReady || !active || identityChanged ||
                workloadChanged ||
                pressureOrStale || searchUiActive ||
                responseRegressed || responseUnstable || responseWindowChanged ||
                responseProbeFailed || responseWorkloadChanged ||
                nonComparable || evidenceDeadline ||
                (responseRequired && nowUtc - _owned.AppliedUtc >=
                    AiAutomaticPriorityTrialPolicy.EvidenceDeadline &&
                 _candidateResponseWindow.Count < AiForegroundWindowResponsePolicy.RequiredSamples) ||
                AiAutomaticProcessPriorityPolicy.ShouldRestore(
                    enabled, workload, _owned.Name, power?.ForegroundProcess,
                    _owned.AppliedUtc, nowUtc) ||
                (workload == AiWorkloadClass.Game && power?.GameActive != true);

            if (restore)
            {
                // An ended game or an unknown/idle context may restore our
                // priority but must never be counted as a valid B-phase result.
                bool trialComplete = active && !epicGameProtection &&
                    !workloadChanged && !identityChanged &&
                    !responseRegressed && !responseUnstable &&
                    !responseWindowChanged && !responseProbeFailed &&
                    !responseWorkloadChanged &&
                    (!responseRequired || _candidateResponseWindow.Count >=
                        AiForegroundWindowResponsePolicy.RequiredSamples) &&
                    !pressureOrStale && !nonComparable &&
                    !evidenceDeadline && comparable.Comparable &&
                    (earlyReferenceReady ||
                     nowUtc >= _owned.AppliedUtc.Add(AiAutomaticProcessPriorityPolicy.MaximumHold)) &&
                    _candidateCpuWindow.Count >=
                        AiAutomaticPriorityTrialPolicy.RequiredCandidateSnapshots;
                bool restored = RestoreOwned(nowUtc,
                    epicGameProtection ? "spuštěna hra, obnovuji prioritu Epic launcheru" :
                    searchUiActive ? "uživatel otevřel vyhledávání Windows" :
                    responseRegressed ? "výrazné zhoršení odezvy okna WM_NULL" :
                    responseUnstable ? "nestabilní odezva okna během porovnání" :
                    responseProbeFailed ? "okno neodpovědělo na omezený WM_NULL test" :
                    responseWindowChanged ? "aktivní okno se změnilo během pokusu" :
                    responseWorkloadChanged ? "přechod na jinou pracovní aktivitu" :
                    identityChanged ? "cílový nebo aktivní proces změnil PID" :
                    workloadChanged ? "změna herní/interaktivní aktivity" :
                    pressureOrStale ? "zhoršené nebo chybějící měření" :
                    evidenceDeadline ? "nedostatek čerstvých vzorků" :
                    nonComparable ? "změna zátěže během pokusu" :
                    "aktuální aktivita již nevyžaduje omezení",
                    trialComplete && !searchUiActive);
                if (restored)
                    _nextEligibleUtc = nowUtc.Add(
                        trialComplete ? TimeSpan.FromMinutes(15) :
                        AiAutomaticProcessPriorityPolicy.Cooldown);
            }
            else
                Status = "Procesy: časově omezené měření " + _owned.Name +
                    $" • vzorky {_candidateCpuWindow.Count}/" +
                    AiAutomaticPriorityTrialPolicy.RequiredCandidateSnapshots +
                    " (přínos dosud neprokázán)";
            return;
        }

        if (_restoredReference is RestoredReference reference)
        {
            if (!enabled)
            {
                _restoredReference = null;
                Status = "Procesy: automatika vypnutá, další měření zrušeno";
                return;
            }
            AiAutomaticPriorityTrialPolicy.Observation observation =
                AiAutomaticPriorityTrialPolicy.Comparable(
                    power, nowUtc, reference.Name, reference.ForegroundProcess,
                    reference.BaselineSystemCpuPercent, reference.StartedUtc,
                    reference.TargetPid, reference.ForegroundProcessId);
            bool responseRequired = reference.BaselineResponse.Count >=
                AiForegroundWindowResponsePolicy.RequiredSamples;
            bool restoredWindowChanged = responseRequired &&
                AiForegroundWindowResponsePolicy.WindowSwitched(
                    foregroundResponse, reference.ForegroundProcessId,
                    reference.BaselineResponse.WindowHandle, reference.StartedUtc, nowUtc);
            bool restoredResponseFailed = responseRequired &&
                AiForegroundWindowResponsePolicy.NativeProbeFailedDuringTrial(
                    foregroundResponse, reference.ForegroundProcessId,
                    reference.StartedUtc, nowUtc);
            bool sameWorkload = AiAutomaticPriorityTrialPolicy.SameWorkload(
                reference.Workload, workload, power?.GameActive == true);
            if (epicGameProtection || !sameWorkload || !observation.Comparable || restoredWindowChanged ||
                restoredResponseFailed ||
                urgentPressure ||
                nowUtc - reference.StartedUtc >= AiAutomaticPriorityTrialPolicy.EvidenceDeadline)
            {
                _restoredReference = null;
                _nextEligibleUtc = nowUtc.AddHours(1);
                LastTrialResult = reference.Name + ": neprůkazné srovnání po obnovení (" +
                    (epicGameProtection ? "spuštěna hra" :
                     !sameWorkload ? "změna pracovní aktivity" :
                     restoredWindowChanged ? "změna HWND" :
                     restoredResponseFailed ? "okno neodpovídá" : "zátěž se změnila") + ")";
                PersistFeedback(reference.Name, LastTrialResult, nowUtc);
                Status = "Procesy: srovnání po obnovení neprůkazné; další pokus odložen";
                return;
            }
            var samples = AiAutomaticPriorityTrialPolicy.AddDistinct(
                reference.ReferenceWindow, power!, reference.Name);
            var restoredMemory = AiPriorityProcessMemoryPolicy.AddDistinct(
                reference.RestoredMemory, power, reference.Name);
            var restoredResponse = reference.BaselineResponse.Count >=
                AiForegroundWindowResponsePolicy.RequiredSamples
                ? AiForegroundWindowResponsePolicy.AddDistinct(
                    reference.RestoredResponse, foregroundResponse,
                    reference.ForegroundProcessId, reference.StartedUtc, nowUtc)
                : default;
            if (responseRequired &&
                restoredResponse.Count >= AiForegroundWindowResponsePolicy.RequiredSamples &&
                !AiForegroundWindowResponsePolicy.StableWindow(restoredResponse))
            {
                _restoredReference = null;
                _nextEligibleUtc = nowUtc.AddHours(1);
                LastTrialResult = reference.Name + ": neprůkazné srovnání (nestabilní odezva)";
                PersistFeedback(reference.Name, LastTrialResult, nowUtc);
                Status = "Procesy: nestabilní referenční odezva, další pokus odložen";
                return;
            }
            if (samples.Count >= AiAutomaticPriorityTrialPolicy.RequiredRestoredSnapshots &&
                (reference.BaselineResponse.Count < AiForegroundWindowResponsePolicy.RequiredSamples ||
                 restoredResponse.Count >= AiForegroundWindowResponsePolicy.RequiredSamples))
            {
                string verdict = AiAutomaticPriorityTrialPolicy.Verdict(
                    reference.BaselineTopCpuPercent, reference.CandidateWindow, samples);
                if (responseRequired)
                {
                    if (AiForegroundWindowResponsePolicy.ComparableAba(
                        reference.BaselineResponse, reference.CandidateResponse, restoredResponse))
                        verdict += "; " + AiForegroundWindowResponsePolicy.Describe(
                            reference.BaselineResponse, reference.CandidateResponse, restoredResponse);
                    else
                        verdict = "neprůkazné: fáze A/B/A neudržely stabilní odezvu stejného okna";
                }
                // Working set is informational only. Scheduler priorities do
                // NOT free RAM; never credit a memory saving from this metric.
                if (reference.BaselineMemory.Count >= AiPriorityProcessMemoryPolicy.RequiredSnapshots)
                    verdict += "; " + AiPriorityProcessMemoryPolicy.Describe(
                        reference.BaselineMemory, reference.CandidateMemory, restoredMemory);
                _restoredReference = null;
                _nextEligibleUtc = nowUtc.AddHours(1);
                LastTrialResult = reference.Name + ": " + verdict;
                PersistFeedback(reference.Name, LastTrialResult, nowUtc);
                Status = "Procesy: " + verdict;
                // One structured, measured A/B/A result per completed trial.
                // Do not manufacture a zero when an optional response or RAM
                // phase lacks comparable samples. Values describe PROXIES,
                // never overall Windows responsiveness or freed physical RAM.
                bool validResponse = responseRequired &&
                    AiForegroundWindowResponsePolicy.ComparableAba(
                        reference.BaselineResponse, reference.CandidateResponse,
                        restoredResponse);
                bool validMemory = AiPriorityProcessMemoryPolicy.ComparableAba(
                    reference.BaselineMemory, reference.CandidateMemory, restoredMemory);
                string responseMetrics = validResponse
                    ? $"A={reference.BaselineResponse.AverageMs:0.##},B=" +
                      $"{reference.CandidateResponse.AverageMs:0.##},A2=" +
                      $"{restoredResponse.AverageMs:0.##}"
                    : "unavailable";
                string memoryMetrics = validMemory
                    ? $"A={reference.BaselineMemory.AverageMiB:0.##},B=" +
                      $"{reference.CandidateMemory.AverageMiB:0.##},A2=" +
                      $"{restoredMemory.AverageMiB:0.##}"
                    : "unavailable";
                LogService.Info("ai-system",
                    $"AI_PROCESS_PRIORITY_TRIAL_RESULT name={reference.Name} " +
                    $"workload={reference.Workload} " +
                    $"phaseBMs={reference.CandidatePhaseMs?.ToString() ?? "unavailable"} " +
                    $"restoredPhaseMs={(nowUtc >= reference.StartedUtc ? (nowUtc - reference.StartedUtc).TotalMilliseconds.ToString("0") : "unavailable")} " +
                    $"cpuSamplesA={reference.BaselineSamples} cpuSamplesB={reference.CandidateWindow.Count} cpuSamplesA2={samples.Count} " +
                    $"baselineTopPp={reference.BaselineTopCpuPercent:0.##} " +
                    $"candidateTopPp={reference.CandidateWindow.Average:0.##} " +
                    $"referenceTopPp={samples.Average:0.##} " +
                    $"windowResponseMs={responseMetrics} workingSetMiB={memoryMetrics} " +
                    $"verdict={verdict}");
            }
            else
            {
                _restoredReference = reference with {
                    ReferenceWindow = samples,
                    RestoredResponse = restoredResponse,
                    RestoredMemory = restoredMemory
                };
                Status = $"Procesy: srovnávám obnovenou prioritu ({samples.Count}/" +
                    AiAutomaticPriorityTrialPolicy.RequiredRestoredSnapshots +
                    " vzorků, bez tvrzení o zrychlení PC)";
            }
            return;
        }

        if (!enabled)
        {
            ResetCandidate();
            _candidateCpuWindow = default;
            Status = "Procesy: automatika vypnutá";
            return;
        }
        // Checked AFTER both owned B and restored A2 branches. Severe wake
        // jitter cannot interrupt rollback, readback, or completed A/B/A proof.
        if (!AiSchedulerAutoEvidenceService.AllowsNewPriorityTrial(nowUtc))
        {
            ResetCandidate();
            Status = "Procesy: nová zkouška priorit odložena kvůli latenci vláken";
            return;
        }
        if (_feedbackFaulted)
        {
            ResetCandidate();
            Status = "Procesy: poslední pokus nelze bezpečně uložit; nové pokusy pozastaveny";
            return;
        }
        if (!fresh)
        {
            ResetCandidate();
            ReportIneligible("chybi aktualni telemetrie", nowUtc);
            return;
        }
        if (nowUtc < _nextEligibleUtc)
        {
            ResetCandidate();
            ReportIneligible("ochranna prodleva po vraceni priority", nowUtc);
            return;
        }

        AiSystemSmoothnessHealth? smooth = AiSystemSmoothnessGuardService.ReadAgentSnapshot(nowUtc);
        // Unknown/pressured machines are not reliable evidence for a new
        // autonomous scheduling mutation. 'Gaming' is expected when a game is
        // protected. A stale status is not permission for an intervention.
        bool safe = smooth != null &&
            AiSystemSmoothnessGuardService.IsFresh(
                smooth.UpdatedUtc, nowUtc, TimeSpan.FromSeconds(90)) &&
            smooth.State is "Normal" or "Gaming";

        // The foreground game/browser often leads the global CPU ranking.
        // Evaluate only the five already sampled allowlisted helpers by their
        // own CPU and exact PIDs; a cooled-down helper must not block another.
        string proposed = AiAutomaticProcessPriorityPolicy.SelectObservedCandidate(
            enabled, workload, power, _feedbackHistory, nowUtc, _candidate,
            out int targetPid, out _);
        if (string.IsNullOrEmpty(proposed))
        {
            ResetCandidate();
            ReportIneligible(
                AiAutomaticProcessPriorityPolicy.ExplainNoCandidate(
                    enabled, workload, power, _feedbackHistory, nowUtc), nowUtc);
            return;
        }
        if (!safe || AiAutomaticPriorityTrialPolicy.DangerousAfterApplying(smooth, nowUtc) ||
            !double.IsFinite(power!.LoadPercent) ||
            power.LoadPercent is < 0 or > 100)
        {
            ResetCandidate();
            ReportIneligible(
                !double.IsFinite(power.LoadPercent) || power.LoadPercent is < 0 or > 100
                    ? "nesrovnatelna celkova zatez CPU"
                    : AiAutomaticPriorityTrialPolicy.NewTrialPressureReason(
                        smooth, nowUtc), nowUtc);
            return;
        }

        // Two independent readings at least 20s apart; a single spike can
        // never change another application's scheduler priority.
        if (!string.Equals(_candidate, proposed, StringComparison.OrdinalIgnoreCase) ||
            _candidateWorkload != workload ||
            !string.Equals(_candidateForeground, power.ForegroundProcess, StringComparison.OrdinalIgnoreCase) ||
            _candidateTargetPid != targetPid ||
            _candidateForegroundPid != power.ForegroundProcessId ||
            nowUtc < _candidateLastUtc ||
            nowUtc - _candidateLastUtc > AiAutomaticProcessPriorityPolicy.MaximumObservationGap ||
            (_candidateObservations > 0 &&
             AiAutomaticPriorityTrialPolicy.LoadDriftExceeded(
                 power.LoadPercent, _candidateBaselineLoadPercent)))
        {
            ResetCandidate();
            _candidate = proposed;
            _candidateForeground = power.ForegroundProcess;
            _candidateWorkload = workload;
            _candidateTargetPid = targetPid;
            _candidateForegroundPid = power.ForegroundProcessId;
            _candidateBaselineLoadPercent = power.LoadPercent;
            _candidateFirstUtc = nowUtc;
        }
        if (_baselineResponseWindow.Count > 0 &&
            AiForegroundWindowResponsePolicy.WindowSwitched(
                foregroundResponse, power.ForegroundProcessId,
                _baselineResponseWindow.WindowHandle, _candidateFirstUtc, nowUtc))
        {
            ResetCandidate();
            Status = "Procesy: aktivní okno se změnilo, sbírám novou referenci";
            return;
        }
        _baselineResponseWindow = AiForegroundWindowResponsePolicy.AddDistinct(
            _baselineResponseWindow, foregroundResponse,
            power.ForegroundProcessId, _candidateFirstUtc, nowUtc);
        if (power.UpdatedUtc > _baselineCpuWindow.LastUpdatedUtc &&
            (_candidateObservations == 0 ||
             nowUtc - _candidateLastUtc >= AiAutomaticProcessPriorityPolicy.MinimumObservation))
        {
            _baselineCpuWindow = AiAutomaticPriorityTrialPolicy.AddDistinct(
                _baselineCpuWindow, power, proposed);
            _baselineMemoryWindow = AiPriorityProcessMemoryPolicy.AddDistinct(
                _baselineMemoryWindow, power, proposed);
            _candidateObservations = _baselineCpuWindow.Count;
            _candidateLastUtc = nowUtc;
        }
        if (_candidateObservations < 2 ||
            !AiAutomaticPriorityTrialPolicy.ValidCpuEvidence(_baselineCpuWindow) ||
            nowUtc - _candidateFirstUtc < AiAutomaticProcessPriorityPolicy.MinimumObservation ||
            _baselineCpuWindow.MaximumTopCpuPercent -
                _baselineCpuWindow.MinimumTopCpuPercent >
                AiAutomaticPriorityTrialPolicy.MaximumTopCpuSpreadPp)
        {
            Status = "Procesy: ověřuji stabilní původní zátěž " + proposed;
            return;
        }

        // For ordinary interactive windows, require two independent bounded
        // WM_NULL samples before changing priorities. Games never receive
        // a WM_NULL and retain the existing CPU/safety-only gates.
        if (workload == AiWorkloadClass.Interactive &&
            _baselineResponseWindow.Count >= AiForegroundWindowResponsePolicy.RequiredSamples &&
            !AiForegroundWindowResponsePolicy.StableWindow(_baselineResponseWindow))
        {
            ResetCandidate();
            Status = "Procesy: nestabilní referenční odezva, nový vzorek bez změn priorit";
            return;
        }
        if (workload == AiWorkloadClass.Interactive &&
            (!AiForegroundWindowResponsePolicy.StableWindow(_baselineResponseWindow) ||
             _baselineResponseWindow.LastUpdatedUtc > nowUtc ||
             nowUtc - _baselineResponseWindow.LastUpdatedUtc >
                 AiForegroundWindowResponsePolicy.MaximumAge))
        {
            Status = "Procesy: čekám na 2 aktuální měření odezvy aktivního okna";
            return;
        }
        var baseline = _baselineCpuWindow;
        var responseBaseline = _baselineResponseWindow;
        var memoryBaseline = _baselineMemoryWindow;
        double baselineLoad = _candidateBaselineLoadPercent;
        ResetCandidate();
        _baselineResponseWindow = responseBaseline;
        _baselineMemoryWindow = memoryBaseline;
        TryLowerBackgroundProcess(proposed, power, baseline, baselineLoad,
            targetPid, workload, nowUtc);
    }
}
