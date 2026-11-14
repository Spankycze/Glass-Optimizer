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

internal sealed partial class AiAutomaticProcessPriorityService
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
}
