using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record AdaptivePowerRuntimeStatus(
    DateTime UpdatedUtc,
    string Mode,
    double LoadPercent,
    string Blocker,
    double? TemperatureC,
    double? ClockMHz,
    double? PackagePowerW,
    bool RealIdle,
    bool GameActive,
    int Epp,
    int Boost,
    string Workload = "Unknown", string EppEvidence = "",
    string TopCpuProcess = "", double TopCpuProcessPercent = 0,
    string ForegroundProcess = "",
    double? OneDriveCpuPercent = null, double? DropboxCpuPercent = null,
    int OneDriveInstanceCount = -1, int DropboxInstanceCount = -1,
    double? SearchIndexerCpuPercent = null, double? SearchProtocolHostCpuPercent = null,
    double? SearchFilterHostCpuPercent = null,
    int SearchIndexerInstanceCount = -1, int SearchProtocolHostInstanceCount = -1,
    int SearchFilterHostInstanceCount = -1,
    int ForegroundProcessId = 0,
    int OneDriveProcessId = 0, int DropboxProcessId = 0,
    int SearchIndexerProcessId = 0, int SearchProtocolHostProcessId = 0,
    int SearchFilterHostProcessId = 0,
    double? OneDriveWorkingSetMiB = null, double? DropboxWorkingSetMiB = null,
    double? SearchIndexerWorkingSetMiB = null,
    double? SearchProtocolHostWorkingSetMiB = null,
    double? SearchFilterHostWorkingSetMiB = null,
    DateTime WorkingSetObservedUtc = default,
    string SamplerSummary = "", DateTime ProcessCpuObservedUtc = default,
    long ForegroundWindowHandle = 0,
    int VerifiedPowerSettingTransitionsThisSession = 0,
    int DeferredOrdinaryTransitionEvaluationsThisSession = 0,
    string EppTrialOpportunityStatus = "",
    double? EpicGamesLauncherCpuPercent = null,
    int EpicGamesLauncherInstanceCount = -1,
    int EpicGamesLauncherProcessId = 0,
    double? EpicGamesLauncherWorkingSetMiB = null,
    double? QBittorrentCpuPercent = null,
    int QBittorrentInstanceCount = -1,
    int QBittorrentProcessId = 0,
    double? QBittorrentWorkingSetMiB = null);

internal readonly record struct AdaptivePowerStatusKey(
    string Mode, bool GameActive, bool RealIdle, int Epp, int Boost, string EppEvidence)
{
    internal static AdaptivePowerStatusKey From(AdaptivePowerRuntimeStatus status) =>
        new(status.Mode, status.GameActive, status.RealIdle,
            status.Epp, status.Boost, status.EppEvidence);
}

public sealed partial class AdaptivePowerRuntimeService
{
    private const string CpuSubgroup = "54533251-82be-4824-96c1-47b60b740d00";
    private const string EppSetting = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    private const string BoostSetting = "be337238-0d82-4146-a960-4f3749d470c7";
    public static string StatusPath => AdaptivePowerRuntimePathPolicy.WritablePath;
    private static readonly AdaptiveRuntimeStatusReadCache<AdaptivePowerRuntimeStatus> StatusReadCache =
        new(AdaptivePowerRuntimePathPolicy.WritablePath, AdaptivePowerRuntimePathPolicy.LegacyPath);

    private enum RuntimeMode
    {
        Unknown,
        Idle,
        Balanced,
        Performance
    }

    private sealed record ProcessLoadSample(
        double TotalPercent, string TopProcess, double TopPercent,
        string ForegroundProcess, bool GameActive,
        double? OneDriveCpuPercent = null, double? DropboxCpuPercent = null,
        int OneDriveInstanceCount = 0, int DropboxInstanceCount = 0,
        double? SearchIndexerCpuPercent = null, double? SearchProtocolHostCpuPercent = null,
        double? SearchFilterHostCpuPercent = null,
        int SearchIndexerInstanceCount = 0, int SearchProtocolHostInstanceCount = 0,
        int SearchFilterHostInstanceCount = 0,
        int ForegroundProcessId = 0,
        int OneDriveProcessId = 0, int DropboxProcessId = 0,
        int SearchIndexerProcessId = 0, int SearchProtocolHostProcessId = 0,
        int SearchFilterHostProcessId = 0,
        double? OneDriveWorkingSetMiB = null, double? DropboxWorkingSetMiB = null,
        double? SearchIndexerWorkingSetMiB = null,
        double? SearchProtocolHostWorkingSetMiB = null,
        double? SearchFilterHostWorkingSetMiB = null,
        DateTime WorkingSetObservedUtc = default,
        bool HasRepresentativeCpuCoverage = false,
        double? EpicGamesLauncherCpuPercent = null,
        int EpicGamesLauncherInstanceCount = 0,
        int EpicGamesLauncherProcessId = 0,
        double? EpicGamesLauncherWorkingSetMiB = null,
        double? QBittorrentCpuPercent = null,
        int QBittorrentInstanceCount = 0,
        int QBittorrentProcessId = 0,
        double? QBittorrentWorkingSetMiB = null);

    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly AdaptiveProcessCpuHistory _processCpuHistory = new();
    private readonly AdaptiveCpuAccessDeniedCache _cpuDeniedCache = new();
    private long _previousProcessSampleTimestamp;
    private RuntimeMode _mode = RuntimeMode.Unknown;
    private int _idleVotes;
    private int _performanceVotes;
    private int _exitPerformanceVotes;
    private int _thermalGuardVotes;
    private readonly AiWorkloadStabilizer _workloadStabilizer = new();
    private readonly AdaptivePowerEppTuner _eppTuner = new();
    private bool _loadInitialized;
    private AiCpuLoadSource _lastCpuLoadSource = AiCpuLoadSource.Unknown;
    private double _smoothedLoad;
    private long _lastApplyStamp; // periodic power readback, monotonic
    private DateTime _lastModeTransitionUtc = DateTime.MinValue;
    private long _lastModeTransitionStamp; // EPP admission, monotonic elapsed
    private DateTime _lastIdleExitUtc = DateTime.MinValue;
    private int _verifiedPowerSettingTransitions;
    private int _deferredOrdinaryTransitions;
    private DateTime _lastLogUtc = DateTime.MinValue;
    private DateTime _lastCoolingLimitLogUtc = DateTime.MinValue;
    private long _lastBaseHealStamp; // baseline repair cadence, monotonic
    private DateTime _lastDisabledRestoreUtc = DateTime.MinValue;
    private bool _wasPowerManaged;
    private DateTime _lastStatusWriteUtc = DateTime.MinValue;
    private DateTime _lastStatusFailureUtc = DateTime.MinValue;
    private DateTime _lastStatusErrorLogUtc = DateTime.MinValue;
    private DateTime _lastProcessSampleUtc = DateTime.MinValue;
    private DateTime _lastProcessSampleMetricUtc = DateTime.MinValue;
    private int _processSnapshotCacheReuses;
    private nint _lastSampledForegroundHwnd;
    private long _lastMetricProcessCpuTicks = -1;
    private AdaptiveProcessSampleMetricWindow _sampleMetrics;
    private string _lastSamplerSummary = "Měření nákladů monitoringu se připravuje";
    private DateTime _lastWorkingSetReadUtc = DateTime.MinValue;
    private readonly Dictionary<int, double?> _workingSetByPid = new(5);
    private int _processNameRefreshCycle;
    private ProcessLoadSample _cachedProcessSample = new(0, "none", 0, string.Empty, false);
    private AdaptivePowerStatusKey? _lastCriticalStatusKey;
    private string _lastBlocker = string.Empty;

    public string LastBlocker => _lastBlocker;
    public string ModeName => _mode.ToString();
    // Same-agent consumers can use this live immutable status without a
    // redundant JSON/file-metadata round-trip on every runtime cycle.
    public AdaptivePowerRuntimeStatus? CurrentStatus { get; private set; }
    // Same tick's immutable configuration; avoids the second file metadata
    // probe in AiSystemResourceManagerService after adaptive power sampling.
    public AiSystemRuntimeConfig? CurrentAiConfig { get; private set; }
    public bool GameActive { get; private set; }
    public bool RealIdle { get; private set; }
    public double LoadPercent { get; private set; }
    public bool CpuLoadKnown { get; private set; }
    public AiWorkloadClass WorkloadClass { get; private set; } = AiWorkloadClass.Unknown;

    public async Task TickAsync(
        CpuTemperatureService.CpuTelemetrySample telemetry,
        int[]? fanRpms,
        bool auto65Enabled,
        bool aiSmartEnabled,
        AiSmartRuntimeDecision aiDecision,
        CancellationToken token)
    {
        // Enumerating all Windows processes and querying native CPU counters
        // can stall the caller; isolate that cold work from the UI thread.
        await _tickGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
        CurrentStatus = null; // never forward an old snapshot after a failed tick
        bool previousCpuLoadKnown = CpuLoadKnown;
        CpuLoadKnown = false;
        CurrentAiConfig = null;
        // Read the active PID once per tick (cheap native call). During a
        // verified idle/background interval we can reuse a recent process
        // snapshot; any foreground switch or fresh CPU spike immediately
        // restores the normal two-second scan cadence.
        DateTime snapshotNow = DateTime.UtcNow;
        // User input, not a boosted Ryzen CPU clock, distinguishes unattended
        // operation. Capture once for BOTH the adaptive process scan and the
        // RealIdle decision, avoiding contradictory evidence in one tick.
        bool userInputIdle = ForegroundProcessProbe.IsUserInputIdleFor(
            TimeSpan.FromSeconds(60));
        TimeSpan scanInterval = AiAdaptiveProcessScanPolicy.Interval(
            WorkloadClass, GameActive, telemetry.LoadPercent,
            WorkloadClass == AiWorkloadClass.Idle && userInputIdle);
        var foregroundIdentity = ForegroundProcessProbe.GetWindowIdentity();
        int foregroundPidNow = foregroundIdentity.ProcessId;
        bool reuseProcessSnapshot = AiAdaptiveProcessScanPolicy.CanReuse(
            _lastProcessSampleUtc, snapshotNow, scanInterval,
            _cachedProcessSample.ForegroundProcessId, foregroundPidNow,
            _lastSampledForegroundHwnd, foregroundIdentity.Handle);
        ProcessLoadSample processSample;
        if (reuseProcessSnapshot)
        {
            _processSnapshotCacheReuses++;
            processSample = _cachedProcessSample;
        }
        else
        {
            processSample = await Task.Run(() => SampleProcessLoad(token, foregroundPidNow), token).ConfigureAwait(false);
            // Do not classify or tune power from a mixed snapshot when the
            // user changes the foreground WHILE native enumeration runs.
            // The next FanAgent tick will rescan, even if the PID is reused.
            if (!AiAdaptiveProcessScanPolicy.SameForegroundDuringScan(
                    foregroundIdentity, ForegroundProcessProbe.GetWindowIdentity()))
            {
                _lastSampledForegroundHwnd = 0;
                WorkloadClass = AiWorkloadClass.Unknown;
                return;
            }
            _lastSampledForegroundHwnd = foregroundIdentity.Handle;
        }
        // Cold/resumed CPU counters cannot inherit the old workload votes/EMA.
        // If BOTH hardware and native CPU evidence are unavailable, do not
        // manufacture an idle 0% sample or authorize any new EPP decision.
        if (_lastProcessSampleUtc == default)
        {
            _loadInitialized = false;
            _workloadStabilizer.ResetEvidence();
        }
        bool cpuLoadKnown = AiCpuLoadSelectionPolicy.TrySelect(
            telemetry.LoadPercent, processSample.TotalPercent,
            processSample.HasRepresentativeCpuCoverage, out double rawLoad,
            out AiCpuLoadSource currentSource);
        if (cpuLoadKnown)
        {
            _smoothedLoad = AiCpuLoadSelectionPolicy.Smooth(
                _smoothedLoad, rawLoad, _loadInitialized,
                _lastCpuLoadSource, currentSource);
            _loadInitialized = true;
        }
        else
        {
            _loadInitialized = false;
            _workloadStabilizer.ResetEvidence();
        }
        _lastCpuLoadSource = currentSource;
        double load = cpuLoadKnown ? Math.Clamp(_smoothedLoad, 0, 100) : double.NaN;
        // Reused snapshots keep the same top-process label; avoid formatting it again.
        if (!cpuLoadKnown) _lastBlocker = "CPU evidence unavailable";
        else if (string.IsNullOrWhiteSpace(processSample.TopProcess)) _lastBlocker = "none";
        else if (!reuseProcessSnapshot || !previousCpuLoadKnown)
            _lastBlocker = $"{processSample.TopProcess} {processSample.TopPercent:0.0}%";

        // Per-process CPU totals are a lower bound when the hardware CPU
        // provider fails. Never infer power-saving IDLE from that estimate.
        bool hardwareCpuTrusted = telemetry.LoadPercent is double hardwareCpu &&
            AiCpuLoadSelectionPolicy.IsValid(hardwareCpu);
        bool realIdle = hardwareCpuTrusted && cpuLoadKnown &&
            AiAdaptiveIdleAdmissionPolicy.IsRealIdle(load, userInputIdle);

        AiWorkloadClass observedWorkload = cpuLoadKnown
            ? AiWorkloadContextPolicy.Classify(
                processSample.GameActive,
                realIdle,
                processSample.ForegroundProcess,
                processSample.TopProcess,
                processSample.TopPercent,
                load)
            : AiWorkloadClass.Unknown;
        AiWorkloadClass workload = cpuLoadKnown
            ? _workloadStabilizer.UpdateDistinct(observedWorkload, _lastProcessSampleUtc)
            : AiWorkloadClass.Unknown;

        GameActive = processSample.GameActive;
        RealIdle = realIdle;
        // Fan control still receives last finite CPU load on an outage;
        // dependent optimization guards get an explicit unknown-evidence flag.
        if (cpuLoadKnown)
            LoadPercent = load;
        CpuLoadKnown = cpuLoadKnown;
        WorkloadClass = workload;

        var runtimeConfig = AiRuntimeScopePolicy.Sanitize(AiSystemResourceManagerService.LoadConfig());
        CurrentAiConfig = runtimeConfig;
        bool powerManaged = runtimeConfig.Enabled && runtimeConfig.PowerPlanEnabled;
        if (!powerManaged)
        {
            // Restore immediately on AI-off, then verify periodically instead
            // of re-reading the ownership JSON on every fan safety tick.
            DateTime nowUtc = DateTime.UtcNow;
            if (ShouldRunDisabledPowerRestore(nowUtc, _lastDisabledRestoreUtc, _wasPowerManaged))
            {
                await RestoreAllAiSettingsAsync(token).ConfigureAwait(false);
                _lastDisabledRestoreUtc = nowUtc; // failures retry on next tick
            }
            _wasPowerManaged = false;
            // Disabled power automation must not retain or accumulate pending
            // mode votes that could influence a later user-enabled session.
            _performanceVotes = 0;
            _idleVotes = 0;
            _exitPerformanceVotes = 0;
            _thermalGuardVotes = 0;
            _mode = RuntimeMode.Unknown;
            _lastApplyStamp = 0;
            _lastModeTransitionUtc = DateTime.MinValue;
            _lastModeTransitionStamp = 0;
            _lastIdleExitUtc = DateTime.MinValue;
            _lastBaseHealStamp = 0;
        }
        else
        {
            _wasPowerManaged = true;
            _lastDisabledRestoreUtc = DateTime.MinValue;
        }

        // AI-off restores owned Windows settings even during telemetry outage.
        // Only defer NEW automatic optimization decisions, never that rollback.
        if (!cpuLoadKnown)
            return;

        RuntimeMode desired = powerManaged
            ? ChooseMode(
                load,
                rawLoad,
                realIdle,
                userInputIdle,
                processSample.GameActive,
                aiSmartEnabled,
                aiDecision,
                telemetry.PackagePowerW,
                telemetry.EffectiveClockMHz,
                workload)
            : RuntimeMode.Unknown;
        bool verifyDue = IsMonotonicPowerRefreshDue(
            _lastApplyStamp, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
            TimeSpan.FromSeconds(30));

        // Measure and learn one reversible CPU EPP value exclusively on our own
        // Spankyho Performance scheme. The original 20 EPP is restored automatically
        // when game/mode/temperature changes, or if A/B/A fails.
        // Query the current Windows power scheme once per FanAgent cycle.
        // An external plan switch is still detected next tick; after an in-cycle
        // powercfg /setactive, re-read before publishing the status.
        bool planActive = TryGetActiveSpankyScheme(out string? tunerGuid) &&
            !string.IsNullOrWhiteSpace(tunerGuid);
        bool tunerPlanActive = powerManaged && planActive;
        bool schemeMayHaveChanged = false;
        // The transition path and status output may need the same Boost key.
        // Reuse only this tick's native read, never one from an older tick.
        bool transitionBoostKnown = false;
        int transitionBoost = -1;
        int actualTunerEpp = -1;
        bool tunerEppKnown = tunerPlanActive &&
            TryReadAcSetting(tunerGuid!, EppSetting, out actualTunerEpp);
        bool eppBalancedStable = AiEppTrialOpportunityPolicy.HasMonotonicStableBalancedWindow(
            desired == RuntimeMode.Balanced, _mode == RuntimeMode.Balanced,
            _lastModeTransitionStamp, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        int learnedEpp = await _eppTuner.TickAsync(
            tunerPlanActive && aiSmartEnabled && AiSystemSmoothnessGuardService.AllowsBackgroundExperiment(),
            desired == RuntimeMode.Balanced,
            processSample.GameActive, realIdle, load, telemetry.Celsius,
            tunerPlanActive ? tunerGuid : null, actualTunerEpp, token,
            stableBalancedMode: eppBalancedStable).ConfigureAwait(false);
        bool adaptiveTransition = desired == RuntimeMode.Balanced &&
            tunerPlanActive && actualTunerEpp != learnedEpp;

        if (powerManaged && (desired != _mode || verifyDue || adaptiveTransition) &&
            tunerPlanActive)
        {
            bool nativeTransitionVerified = false;
            string activeGuid = tunerGuid!;
            var values = GetModeValues(desired);
            if (desired == RuntimeMode.Balanced)
                values = (learnedEpp, values.Boost);
            // Check both values independently; a missing EPP key must not
            // prevent a readable Boost key from being repaired.
            // The EPP register was already read earlier in this same tick for
            // learning. Reuse that exact native snapshot instead of opening
            // the registry key twice. MutateAiSettingAsync still re-reads the
            // current value and the active plan immediately before any write.
            bool eppKnown = tunerEppKnown;
            int currentEpp = actualTunerEpp;
            bool targetEppConfirmed = eppKnown && currentEpp == values.Epp;
            bool boostKnown = TryReadAcSetting(activeGuid, BoostSetting, out int currentBoost);
            transitionBoostKnown = boostKnown;
            transitionBoost = currentBoost;
            if ((eppKnown && currentEpp != values.Epp) ||
                (boostKnown && currentBoost != values.Boost))
            {
                // User-selected AI power management owns EPP autonomously.
                // A stale Suppressed flag must not disable its learned/dynamic
                // target. Other power settings (Boost, base settings) continue
                // to respect independently saved manual overrides.
                var ownership = await LoadAiPowerAsync(token).ConfigureAwait(false);
                bool suppressBoost = ownership.TryGetValue(activeGuid + "|" + BoostSetting, out var boostRow) &&
                    boostRow.Suppressed;
                if (ShouldAttemptPowerMutation(eppKnown, currentEpp, values.Epp,
                        aiEppControlEnabled: true, boostKnown, currentBoost, values.Boost, suppressBoost) &&
                    TryGetActiveSpankyScheme(out string? confirmedGuid) &&
                    string.Equals(confirmedGuid, activeGuid, StringComparison.OrdinalIgnoreCase))
                {
                    // Even a failed transaction may have touched a power
                    // value before rollback; never reuse its old readback.
                    schemeMayHaveChanged = true;
                    bool applied = await ApplyAsync(activeGuid, values.Epp, values.Boost, token)
                        .ConfigureAwait(false);
                    // After any attempted transaction, the pre-write reading
                    // can no longer prove the current mode. Failed writes may
                    // have rolled back after touching EPP or the power scheme.
                    targetEppConfirmed = false;
                    if (applied)
                    {
                        // Report actual verified values, not an intended value
                        // when the other setting has a manual-override guard.
                        bool actualEppReadable =
                            TryReadAcSetting(activeGuid, EppSetting, out int appliedEpp);
                        TryReadAcSetting(activeGuid, BoostSetting, out int appliedBoost);
                        targetEppConfirmed = actualEppReadable && appliedEpp == values.Epp;
                        nativeTransitionVerified = desired != _mode && targetEppConfirmed;
                        LogService.Info(
                            "power",
                            $"ADAPTIVE_POWER mode={desired} load={load:0.0}% raw={rawLoad:0.0}% game={processSample.GameActive} epp={appliedEpp} boost={appliedBoost} blocker={_lastBlocker}");
                    }
                }
            }

            bool modeChanged = desired != _mode;
            // Don't record a hypothetical transition after a failed native
            // write or plan switch. Otherwise future hysteresis/Idle exits
            // and the UI would use a mode Windows never confirmed.
            bool samePlan = !modeChanged ||
                (TryGetActiveSpankyScheme(out string? livePlan) &&
                 string.Equals(livePlan, activeGuid, StringComparison.OrdinalIgnoreCase));
            if (CanCommitObservedMode(modeChanged, targetEppConfirmed, samePlan))
            {
                if (modeChanged)
                {
                    DateTime transitionedUtc = DateTime.UtcNow;
                    if (_mode == RuntimeMode.Idle && desired != RuntimeMode.Idle)
                        _lastIdleExitUtc = transitionedUtc;
                    _lastModeTransitionUtc = transitionedUtc;
                    _lastModeTransitionStamp = Stopwatch.GetTimestamp();
                    if (nativeTransitionVerified)
                        _verifiedPowerSettingTransitions++;
                }
                _mode = desired;
            }
            _lastApplyStamp = Stopwatch.GetTimestamp();
        }

        if (powerManaged && IsMonotonicPowerRefreshDue(
                _lastBaseHealStamp, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                TimeSpan.FromSeconds(30)) &&
            tunerPlanActive &&
            TryGetActiveSpankyScheme(out string? confirmedHealGuid) &&
            string.Equals(confirmedHealGuid, tunerGuid, StringComparison.OrdinalIgnoreCase))
        {
            long baseHealStart = Stopwatch.GetTimestamp();
            int repairedSettings = await EnsureBaseSettingsAsync(tunerGuid!, token).ConfigureAwait(false);
            double baseHealMs = Stopwatch.GetElapsedTime(baseHealStart).TotalMilliseconds;
            if (baseHealMs >= 200)
                LogService.Info("power", $"ADAPTIVE_BASE_HEAL_SLOW ms={baseHealMs:F1} repaired={repairedSettings}");
            // A zero repair count does not prove no native write was attempted:
            // a failed repair may have touched settings before rollback.
            // Always re-read the plan after self-heal to avoid stale EPP/Boost evidence.
            schemeMayHaveChanged = true;
            _lastBaseHealStamp = Stopwatch.GetTimestamp();
        }

        string? statusGuid = tunerGuid;
        bool statusPlanActive = schemeMayHaveChanged
            ? TryGetActiveSpankyScheme(out statusGuid)
            : planActive;
        var currentValues = (Epp: -1, Boost: -1);
        if (statusPlanActive && statusGuid != null)
        {
            // Same scheme and unchanged power settings: use the EPP read from
            // this tick's tuning preflight. Any in-cycle mutation/plan switch
            // still performs a fresh read-back. Write guards are unaffected.
            if (CanReuseEppStatusRead(
                    tunerEppKnown, schemeMayHaveChanged, statusGuid, tunerGuid))
                currentValues.Epp = actualTunerEpp;
            else if (TryReadAcSetting(statusGuid, EppSetting, out int actualEpp))
                currentValues.Epp = actualEpp;
            if (CanReuseBoostStatusRead(transitionBoostKnown, schemeMayHaveChanged,
                    statusGuid, tunerGuid))
                currentValues.Boost = transitionBoost;
            else if (TryReadAcSetting(statusGuid, BoostSetting, out int actualBoost))
                currentValues.Boost = actualBoost;
        }
        var currentStatus = new AdaptivePowerRuntimeStatus(
            DateTime.UtcNow,
            !powerManaged ? "Observation (AI off)" : !statusPlanActive ? "Observation (no Spanky plan)" : _mode == RuntimeMode.Unknown ? "Observation (EPP unverified)" : _mode.ToString(),
            Math.Round(load, 1),
            _lastBlocker,
            telemetry.Celsius,
            telemetry.EffectiveClockMHz,
            telemetry.PackagePowerW,
            realIdle,
            processSample.GameActive,
            currentValues.Epp,
            currentValues.Boost,
            workload.ToString(),
            _eppTuner.StatusText,
            processSample.TopProcess,
            Math.Round(processSample.TopPercent, 2),
            processSample.ForegroundProcess,
            processSample.OneDriveCpuPercent,
            processSample.DropboxCpuPercent,
            processSample.OneDriveInstanceCount,
            processSample.DropboxInstanceCount,
            processSample.SearchIndexerCpuPercent,
            processSample.SearchProtocolHostCpuPercent,
            processSample.SearchFilterHostCpuPercent,
            processSample.SearchIndexerInstanceCount,
            processSample.SearchProtocolHostInstanceCount,
            processSample.SearchFilterHostInstanceCount,
            processSample.ForegroundProcessId,
            processSample.OneDriveProcessId,
            processSample.DropboxProcessId,
            processSample.SearchIndexerProcessId,
            processSample.SearchProtocolHostProcessId,
            processSample.SearchFilterHostProcessId,
            processSample.OneDriveWorkingSetMiB,
            processSample.DropboxWorkingSetMiB,
            processSample.SearchIndexerWorkingSetMiB,
            processSample.SearchProtocolHostWorkingSetMiB,
            processSample.SearchFilterHostWorkingSetMiB,
            processSample.WorkingSetObservedUtc,
            _lastSamplerSummary, _lastProcessSampleUtc,
            _lastSampledForegroundHwnd.ToInt64(),
            _verifiedPowerSettingTransitions, _deferredOrdinaryTransitions,
            _eppTuner.OpportunityStatus,
            processSample.EpicGamesLauncherCpuPercent,
            processSample.EpicGamesLauncherInstanceCount,
            processSample.EpicGamesLauncherProcessId,
            processSample.EpicGamesLauncherWorkingSetMiB,
            processSample.QBittorrentCpuPercent,
            processSample.QBittorrentInstanceCount,
            processSample.QBittorrentProcessId,
            processSample.QBittorrentWorkingSetMiB);
        CurrentStatus = currentStatus;
        MaybeWriteStatus(currentStatus);

        // One wall-clock read governs both throttled log paths for this tick.
        DateTime logNowUtc = DateTime.UtcNow;
        if (LogService.PerformanceDiagnosticsEnabled &&
            logNowUtc - _lastLogUtc > TimeSpan.FromSeconds(30))
        {
            LogService.Info(
                "hardware",
                $"CPU_RUNTIME temp={telemetry.Celsius:0.0}C load={load:0.0}% raw={rawLoad:0.0}% clock={telemetry.EffectiveClockMHz:0}MHz power={telemetry.PackagePowerW:0.0}W realIdle={realIdle} workload={workload} observed={observedWorkload} mode={_mode} blocker={_lastBlocker}");
            _lastLogUtc = logNowUtc;
        }

        if (auto65Enabled &&
            telemetry.Celsius is double temp &&
            temp >= 68 &&
            fanRpms != null &&
            logNowUtc - _lastCoolingLimitLogUtc > TimeSpan.FromMinutes(2))
        {
            int maxFan = 0;
            foreach (int rpm in fanRpms) maxFan = Math.Max(maxFan, rpm);
            if (maxFan >= 1850)
            {
                LogService.Info("lianli",
                    $"AUTO65_COOLING_LIMIT temp={temp:0.0}C maxFan={maxFan}rpm cpuPerfUncapped=true");
                _lastCoolingLimitLogUtc = logNowUtc;
            }
        }
        }
        finally { _tickGate.Release(); }
    }

    // No native power changes: status can share its preflight EPP value only
    // when this tick did not even attempt a mutation and plan identity matches.
}
