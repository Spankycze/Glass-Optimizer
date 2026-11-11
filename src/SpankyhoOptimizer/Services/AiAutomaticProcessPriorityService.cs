using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Narrow, autonomous per-process CPU scheduling for allowlisted background
/// sync and noncritical Windows Search indexing helpers. Never changes network
/// stack, service settings, memory working sets, games, browsers, Windows core,
/// Windows Update, Defender, audio or display process priorities.
/// Single owner, persisted before mutation, confirmed by PID+start time+path,
/// restored on workload change/AI-off/agent restart or after a short hold.
/// </summary>
internal sealed class AiAutomaticProcessPriorityService
{
    private sealed record PriorityOwnership(
        int Pid, long StartUtcTicks, string Name, string ImagePath, DateTime AppliedUtc,
        string ForegroundProcess = "", double BaselineTopCpuPercent = 0,
        double BaselineSystemCpuPercent = 0, int BaselineSamples = 0,
        int ForegroundProcessId = 0,
        AiWorkloadClass Workload = AiWorkloadClass.Unknown);

    private static readonly string JournalPath =
        Path.Combine(LogService.Root, "ai-process-priority-ownership.json");
    private static readonly string FeedbackPath =
        Path.Combine(LogService.Root, "ai-process-priority-last-trial.json");
    private PriorityOwnership? _owned;
    private AiPriorityTrialMemoryPolicy.Checkpoint? _lastCheckpoint;
    private AiPriorityTrialMemoryPolicy.History _feedbackHistory =
        AiPriorityTrialMemoryPolicy.Empty;
    private bool _feedbackFaulted;
    private bool _loaded;
    private bool _disabledIdleCleared;
    private bool _recoveryPending;
    private bool _journalFaulted;
    private DateTime _lastEligibilityLogUtc = DateTime.MinValue;
    private DateTime _lastTickUtc = DateTime.MinValue;
    private DateTime _nextEligibleUtc = DateTime.MinValue;
    private string _candidate = string.Empty;
    private DateTime _candidateFirstUtc = DateTime.MinValue;
    private DateTime _candidateLastUtc = DateTime.MinValue;
    private int _candidateObservations;
    private double _candidateBaselineLoadPercent;
    private string _candidateForeground = string.Empty;
    private AiWorkloadClass _candidateWorkload = AiWorkloadClass.Unknown;
    private int _candidateTargetPid;
    private int _candidateForegroundPid;
    private AiAutomaticPriorityTrialPolicy.CpuWindow _baselineCpuWindow;
    private AiAutomaticPriorityTrialPolicy.CpuWindow _candidateCpuWindow;
    private AiPriorityProcessMemoryPolicy.Window _baselineMemoryWindow;
    private AiPriorityProcessMemoryPolicy.Window _candidateMemoryWindow;
    private AiForegroundWindowResponsePolicy.Window _baselineResponseWindow;
    private AiForegroundWindowResponsePolicy.Window _candidateResponseWindow;
    private readonly record struct RestoredReference(
        string Name, string ForegroundProcess, double BaselineTopCpuPercent,
        double BaselineSystemCpuPercent,
        AiAutomaticPriorityTrialPolicy.CpuWindow CandidateWindow,
        DateTime StartedUtc,
        AiAutomaticPriorityTrialPolicy.CpuWindow ReferenceWindow,
        int TargetPid = 0, int ForegroundProcessId = 0,
        AiForegroundWindowResponsePolicy.Window BaselineResponse = default,
        AiForegroundWindowResponsePolicy.Window CandidateResponse = default,
        AiForegroundWindowResponsePolicy.Window RestoredResponse = default,
        AiPriorityProcessMemoryPolicy.Window BaselineMemory = default,
        AiPriorityProcessMemoryPolicy.Window CandidateMemory = default,
        AiPriorityProcessMemoryPolicy.Window RestoredMemory = default,
        AiWorkloadClass Workload = AiWorkloadClass.Unknown,
        long? CandidatePhaseMs = null, int BaselineSamples = 0);
    private RestoredReference? _restoredReference;

    internal string Status { get; private set; } = "AI sleduje jednotlivé procesy; bez změn priorit";
    internal string LastTrialResult { get; private set; } = "zatím bez ověřeného srovnání";
    // These counters represent ACTUAL native readback-confirmed changes
    // made by this agent lifetime, not fabricated whole-PC CPU/RAM savings.
    private int _successfulPriorityApplies;
    private int _successfulPriorityRestores;
    internal int ActiveOwnedPriorityCount => _owned is null ? 0 : 1;
    // Do not let this agent's own full Windows/TaskScheduler inventory
    // contaminate A1, B or restored A2 CPU/foreground-response evidence.
    internal bool IsMeasurementInFlight => _candidateObservations > 0 ||
        _owned != null || _restoredReference != null;
    internal int SuccessfulPriorityAppliesThisSession => _successfulPriorityApplies;
    internal int SuccessfulPriorityRestoresThisSession => _successfulPriorityRestores;

    // Evaluated before QueueIfDue on the existing FanAgent tick. Do not
    // measure a random user's active window in the absence of a trial or
    // even one trusted background CPU candidate.
    internal bool NeedsForegroundResponse(
        bool enabled, AdaptivePowerRuntimeStatus? power,
        AiWorkloadClass workload, DateTime nowUtc,
        AiSystemSmoothnessHealth? smoothnessSnapshot)
    {
        // QueueIfDue precedes Tick's recovery/checkpoint loading. Do not
        // admit a new window probe until we know persisted ownership and
        // prior failed trials. Existing B/A2 evidence always takes priority.
        if (!_loaded || _journalFaulted || _feedbackFaulted || _recoveryPending)
            return false;
        bool inFlight = _owned != null || _restoredReference != null;
        if (!AiAutomaticProcessPriorityPolicy.NeedsForegroundResponse(
                enabled, workload, power, _feedbackHistory, nowUtc,
                _candidate, _owned != null, _restoredReference != null))
            return false;
        if (inFlight)
            return true;
        // Wake-jitter evidence must only gate NEW trials; never discard a
        // B/A2 trial's matching foreground response measurements.
        if (!AiSchedulerAutoEvidenceService.AllowsNewPriorityTrial(nowUtc))
            return false;
        if (_nextEligibleUtc > nowUtc)
            return false;
        return AiAutomaticPriorityTrialPolicy.CanStartProbeBaseline(
            smoothnessSnapshot, nowUtc);
    }

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

    private void TryLowerBackgroundProcess(
        string processName, AdaptivePowerRuntimeStatus power,
        AiAutomaticPriorityTrialPolicy.CpuWindow baseline,
        double baselineLoadPercent, int expectedTargetPid,
        AiWorkloadClass workload, DateTime nowUtc)
    {
        // At most one exact-name candidate, examined only after two stable
        // observations. No all-process polling loop, wildcard or process kill.
        Process[] candidates;
        try { candidates = Process.GetProcessesByName(processName); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            ReportIneligible("nativni overeni kandidata selhalo", nowUtc);
            return;
        }
        try
        {
            if (candidates.Length != 1)
            {
                ReportIneligible("cilovy proces nema prave jednu instanci", nowUtc);
                return;
            }
            Process process = candidates[0];
            var foreground = ForegroundProcessProbe.GetWindowIdentity();
            int liveForegroundPid = foreground.ProcessId;
            if (!AiAutomaticPriorityTrialPolicy.MayStartNativePriorityMutation(
                    power, nowUtc, liveForegroundPid, foreground.Handle.ToInt64()))
            {
                ReportIneligible("CPU/aktivni okno: neaktualni nebo zmenena identita", nowUtc);
                return;
            }
            if (process.HasExited ||
                process.Id != expectedTargetPid ||
                power.ForegroundProcessId <= 0 ||
                liveForegroundPid != power.ForegroundProcessId ||
                process.Id == Environment.ProcessId ||
                process.Id == liveForegroundPid)
            {
                ReportIneligible("zmena identity ciloveho nebo aktivniho procesu", nowUtc);
                return;
            }
            if (process.PriorityClass != ProcessPriorityClass.Normal)
            {
                // Respect third-party/user priority changes. Never overwrite.
                ReportIneligible("cilovy proces uz nema prioritu Normal", nowUtc);
                return;
            }

            DateTime startUtc = process.StartTime.ToUniversalTime();
            string? image = process.MainModule?.FileName;
            if (!AiAutomaticProcessPriorityPolicy.TrustedExecutable(processName, image))
            {
                ReportIneligible("neoverena instalacni cesta procesu", nowUtc);
                return;
            }
            if (AiAutomaticProcessPriorityPolicy.IsWindowsSearchIndexer(processName))
            {
                int livePid = liveForegroundPid;
                string liveName = AiPriorityTrialMemoryPolicy.CanReuseForegroundName(
                    power.ForegroundProcessId, livePid, power.ForegroundProcess)
                    ? power.ForegroundProcess : ForegroundProcessProbe.GetName();
                if (AiPriorityTrialMemoryPolicy.SearchUserActive(processName,
                    power.ForegroundProcess, liveName))
                {
                    ReportIneligible("aktivni vyhledavani Windows ma prednost", nowUtc);
                    return;
                }
            }

            // Native process/window state can change while retrieving the
            // trusted executable and checking Windows Search. Recheck just
            // before writing the ownership journal, not only at method entry.
            // This prevents applying a stale trial after the user switches
            // tasks or another tool edits this process priority.
            var beforeCommitForeground = ForegroundProcessProbe.GetWindowIdentity();
            if (!AiAutomaticPriorityTrialPolicy.MayStartNativePriorityMutation(
                    power, DateTime.UtcNow, beforeCommitForeground.ProcessId,
                    beforeCommitForeground.Handle.ToInt64()) ||
                process.HasExited ||
                process.Id != expectedTargetPid ||
                process.StartTime.ToUniversalTime().Ticks != startUtc.Ticks ||
                !string.Equals(process.MainModule?.FileName, image,
                    StringComparison.OrdinalIgnoreCase) ||
                process.PriorityClass != ProcessPriorityClass.Normal)
            {
                ReportIneligible("zmeneny proces nebo okno pred zapisem priority", DateTime.UtcNow);
                return;
            }

            var proposed = new PriorityOwnership(
                process.Id, startUtc.Ticks, processName, image!, nowUtc,
                power.ForegroundProcess, baseline.Average,
                baselineLoadPercent, baseline.Count, power.ForegroundProcessId,
                workload);
            // Durable ownership BEFORE touching a non-owned Windows process.
            AtomicFileService.WriteText(JournalPath, JsonSerializer.Serialize(proposed));
            _owned = proposed;
            _candidateCpuWindow = default;
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
                process.Refresh();
                if (process.PriorityClass != ProcessPriorityClass.BelowNormal)
                    throw new InvalidOperationException("Native priority readback did not match.");
                _successfulPriorityApplies++;
                Status = "Procesy: dočasně snížená priorita " + processName;
                LogService.Info("ai-system",
                    $"AI_PROCESS_PRIORITY_APPLY name={processName} pid={process.Id} from=Normal to=BelowNormal");
            }
            catch
            {
                RestoreOwned(nowUtc, "nepotvrzený zápis priority");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            Win32Exception or UnauthorizedAccessException or IOException or
            NotSupportedException or ArgumentException)
        {
            // Never hammer native protected Windows process APIs on failure.
            // No unchecked escalation, service permissions or UAC bypass.
            // If journal persistence failed, no priority was modified.
            // If it succeeded, next tick will try to restore the owner.
            _journalFaulted = _owned == null && File.Exists(JournalPath);
            _nextEligibleUtc = nowUtc.AddMinutes(15);
            Status = "Procesy: změna nepovolena či neověřena; 15min ochranná prodleva";
        }
        finally
        {
            foreach (Process candidate in candidates) candidate.Dispose();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            // File.Exists can hide permission errors. Read directly and treat
            // only a definitely absent journal as an empty ownership state.
            // Decode persisted ownership directly from UTF-8 bytes while
            // retaining the original Unicode BOM fallback and all fail-closed
            // trust checks before any process-priority change.
            _owned = ObservedJsonSnapshotCache<PriorityOwnership>.ReadSnapshotFromFile(JournalPath) ??
                throw new InvalidDataException("Empty priority ownership journal");
            if (_owned.Pid <= 0 || _owned.StartUtcTicks <= 0 ||
                !AiAutomaticProcessPriorityPolicy.TrustedExecutable(
                    _owned.Name, _owned.ImagePath))
                throw new InvalidDataException("Untrusted priority ownership journal");
            _recoveryPending = true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException)
        {
            _journalFaulted = true;
            LogService.Error("ai-system", "AI process priority journal cannot be safely recovered", ex);
        }

        // Only one compact result and cooldown; no high-frequency disk logs.
        // A malformed/unreadable checkpoint does NOT prevent restoration of
        // our existing process priority, but disables NEW scheduling changes.
        try
        {
            string json = File.ReadAllText(FeedbackPath);
            DateTime now = DateTime.UtcNow;
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid AI priority feedback object");
            if (document.RootElement.TryGetProperty("Items", out _))
            {
                var parsed = JsonSerializer.Deserialize<AiPriorityTrialMemoryPolicy.History>(json);
                if (!AiPriorityTrialMemoryPolicy.ValidateHistory(parsed, now))
                    throw new InvalidDataException("Invalid AI priority feedback history");
                _feedbackHistory = parsed!;
            }
            else
            {
                // Backward-compatible one-entry 1075–1079 JSON format.
                var legacy = JsonSerializer.Deserialize<AiPriorityTrialMemoryPolicy.Checkpoint>(json);
                if (!AiPriorityTrialMemoryPolicy.Accept(legacy, now) &&
                    !AiPriorityTrialMemoryPolicy.IsExpired(legacy, now))
                    throw new InvalidDataException("Invalid legacy AI priority feedback");
                _feedbackHistory = new AiPriorityTrialMemoryPolicy.History(
                    legacy is null ? Array.Empty<AiPriorityTrialMemoryPolicy.Checkpoint>() :
                    new[] { legacy });
            }
            _lastCheckpoint = AiPriorityTrialMemoryPolicy.Latest(_feedbackHistory);
            if (_lastCheckpoint != null &&
                AiPriorityTrialMemoryPolicy.Accept(_lastCheckpoint, now))
                LastTrialResult = _lastCheckpoint.Result;
            // Old checkpoints may expire without disabling subsequent safe
            // experiments. Only bad/unreadable JSON must fail closed.
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException)
        {
            _feedbackFaulted = true;
            LogService.Error("ai-system",
                "AI process priority last-trial feedback unavailable; new trials blocked", ex);
        }
    }

    private void PersistFeedback(string target, string message, DateTime nowUtc)
    {
        if (_feedbackFaulted || !AiAutomaticProcessPriorityPolicy.IsAllowlisted(target))
            return;
        try
        {
            var updated = AiPriorityTrialMemoryPolicy.Remember(
                _feedbackHistory, target, message, nowUtc);
            // Write first. Only then expose new cooldown in memory; fail-closed
            // on I/O errors instead of pretending the checkpoint was saved.
            AtomicFileService.WriteText(FeedbackPath, JsonSerializer.Serialize(updated));
            _feedbackHistory = updated;
            _lastCheckpoint = AiPriorityTrialMemoryPolicy.Latest(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException)
        {
            _feedbackFaulted = true;
            LogService.Error("ai-system",
                "Could not persist AI priority trial memory; new trials blocked", ex);
        }
    }

    private bool RestoreOwned(DateTime nowUtc, string reason, bool prepareReference = false)
    {
        if (_owned == null) return true;
        PriorityOwnership old = _owned;
        bool actuallyRestored = false;
        try
        {
            using Process process = Process.GetProcessById(old.Pid);
            if (!process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == old.StartUtcTicks &&
                string.Equals(process.MainModule?.FileName, old.ImagePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                // User/external changes always win. Only return OUR exact
                // BelowNormal mutation to the original Normal priority.
                if (process.PriorityClass == ProcessPriorityClass.BelowNormal)
                {
                    process.PriorityClass = ProcessPriorityClass.Normal;
                    process.Refresh();
                    if (process.PriorityClass != ProcessPriorityClass.Normal)
                        return false;
                    actuallyRestored = true;
                    _successfulPriorityRestores++;
                }
            }
        }
        catch (ArgumentException) { /* Exited or PID recycled: never touch. */ }
        catch (InvalidOperationException) { /* Process exited. */ }
        catch (Exception ex) when (ex is Win32Exception or
            UnauthorizedAccessException or NotSupportedException)
        {
            Status = "Procesy: obnovení priority čeká na ověření vlastníka";
            return false;
        }
        try
        {
            File.Delete(JournalPath);
            _owned = null;
            if (actuallyRestored && prepareReference &&
                _candidateCpuWindow.Count >=
                    AiAutomaticPriorityTrialPolicy.RequiredCandidateSnapshots &&
                old.BaselineTopCpuPercent >=
                    AiAutomaticProcessPriorityPolicy.MinimumBackgroundCpuPercent)
            {
                _restoredReference = new RestoredReference(
                    old.Name, old.ForegroundProcess, old.BaselineTopCpuPercent,
                    old.BaselineSystemCpuPercent, _candidateCpuWindow, nowUtc, default,
                    old.Pid, old.ForegroundProcessId,
                    _baselineResponseWindow, _candidateResponseWindow, default,
                    _baselineMemoryWindow, _candidateMemoryWindow, default,
                    old.Workload,
                    nowUtc >= old.AppliedUtc
                        ? (long)(nowUtc - old.AppliedUtc).TotalMilliseconds
                        : null, old.BaselineSamples);
                Status = "Procesy: původní priorita obnovena; ověřuji referenční měření";
            }
            else
            {
                _restoredReference = null;
                LastTrialResult = old.Name + ": " + reason + "; výkonový přínos neprokázán";
                PersistFeedback(old.Name, LastTrialResult, nowUtc);
                Status = "Procesy: obnoven původní stav (" + reason +
                    "); výkonový přínos zatím neprokázán";
            }
            _candidateCpuWindow = default;
            _baselineMemoryWindow = default;
            _candidateMemoryWindow = default;
            _baselineResponseWindow = default;
            _candidateResponseWindow = default;
            LogService.Info("ai-system",
                $"AI_PROCESS_PRIORITY_RESTORE name={old.Name} pid={old.Pid} " +
                $"actuallyRestored={actuallyRestored} referencePending={_restoredReference != null} " +
                $"holdMs={(nowUtc >= old.AppliedUtc ? ((long)(nowUtc - old.AppliedUtc).TotalMilliseconds).ToString() : "unavailable")} " +
                $"reason={reason}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Procesy: nelze potvrdit zrušení záznamu, další změny blokovány";
            _journalFaulted = true;
            LogService.Error("ai-system", "AI process priority journal removal failed", ex);
            return false;
        }
    }

    // Passive explanation of a failed eligibility gate. At most one line
    // per ten minutes, even if the target ranking changes every sampler tick.
    // No extra process scans, registry writes, timers or priority operations.
    private void ReportIneligible(string reason, DateTime nowUtc)
    {
        Status = "Procesy: bez zásahu (" + reason + ")";
        if (_lastEligibilityLogUtc != DateTime.MinValue &&
            (nowUtc < _lastEligibilityLogUtc ||
             nowUtc - _lastEligibilityLogUtc < TimeSpan.FromMinutes(10)))
            return;
        _lastEligibilityLogUtc = nowUtc;
        LogService.Info("ai-system", "AI_PROCESS_PRIORITY_GATE reason=" + reason);
    }

    private void ResetCandidate()
    {
        _candidate = string.Empty;
        _candidateFirstUtc = DateTime.MinValue;
        _candidateLastUtc = DateTime.MinValue;
        _candidateObservations = 0;
        _candidateForeground = string.Empty;
        _candidateWorkload = AiWorkloadClass.Unknown;
        _candidateTargetPid = 0;
        _candidateForegroundPid = 0;
        _candidateBaselineLoadPercent = 0;
        _baselineCpuWindow = default;
        _baselineMemoryWindow = default;
        _candidateMemoryWindow = default;
        _baselineResponseWindow = default;
        _candidateResponseWindow = default;
    }
}
