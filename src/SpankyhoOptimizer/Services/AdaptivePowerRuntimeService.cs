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

public sealed class AdaptivePowerRuntimeService
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
    internal static bool CanReuseEppStatusRead(bool readKnown, bool mutationAttempted,
        string? statusGuid, string? preflightGuid) =>
        readKnown && !mutationAttempted &&
        !string.IsNullOrWhiteSpace(statusGuid) &&
        string.Equals(statusGuid, preflightGuid, StringComparison.OrdinalIgnoreCase);

    // Same native preflight/plan/mutation safety contract applies to Boost.
    // The value MUST be read again after any attempted power change.
    internal static bool CanReuseBoostStatusRead(bool readKnown, bool mutationAttempted,
        string? statusGuid, string? preflightGuid) =>
        CanReuseEppStatusRead(readKnown, mutationAttempted, statusGuid, preflightGuid);

    internal static string RunEppStatusReadPolicySmoke()
    {
        const string plan = "a1b2-c3d4";
        if (!CanReuseEppStatusRead(true, false, plan, plan.ToUpperInvariant()) ||
            CanReuseEppStatusRead(false, false, plan, plan) ||
            CanReuseEppStatusRead(true, true, plan, plan) ||
            CanReuseEppStatusRead(true, false, plan, "other-plan") ||
            CanReuseEppStatusRead(true, false, "", plan) ||
            CanReuseEppStatusRead(true, false, plan, null))
            throw new InvalidOperationException("EPP status read-after-mutation/plan switch safety regression.");
        return "ADAPTIVE_EPP_STATUS_REUSE_SMOKE_OK unchangedPlanReuse=true " +
            "failedWriteForcesRefresh=true planChangeForcesRefresh=true unknownForcesRefresh=true";
    }

    public static bool IsStatusFresh(
        AdaptivePowerRuntimeStatus? status,
        DateTime nowUtc,
        TimeSpan? maximumAge = null)
    {
        if (status == null || status.UpdatedUtc == DateTime.MinValue ||
            status.UpdatedUtc.Kind != DateTimeKind.Utc ||
            nowUtc.Kind != DateTimeKind.Utc)
            return false;
        if (status.UpdatedUtc > nowUtc.AddSeconds(5))
            return false;

        TimeSpan age = nowUtc - status.UpdatedUtc;
        return age >= TimeSpan.Zero &&
               age <= (maximumAge ?? TimeSpan.FromSeconds(20));
    }

    public static AdaptivePowerRuntimeStatus? ReadStatus()
    {
        try
        {
            string path = AdaptivePowerRuntimePathPolicy.ResolveLoadPath();
            return StatusReadCache.Read(path);
        }
        catch
        {
            return null;
        }
    }

    // The foreground app needs immediate mode/game/EPP transitions, not a
    // new atomic JSON file every time the busiest process name fluctuates.
    internal static bool ShouldWriteAdaptiveStatus(AdaptivePowerStatusKey? previousCriticalKey,
        AdaptivePowerStatusKey nextCriticalKey, DateTime lastWriteUtc, DateTime nowUtc) =>
        previousCriticalKey != nextCriticalKey || nowUtc < lastWriteUtc ||
        nowUtc - lastWriteUtc >= TimeSpan.FromSeconds(5);

    internal static string RunAdaptiveStatusHotpathSmoke()
    {
        // Test current/legacy source selection without any disk writes.
        _ = AdaptivePowerRuntimePathPolicy.RunFunctionalSmoke();
        // A jittering blocker alone must not trigger 120 atomic file writes.
        // A mode or game-state change must still publish immediately.
        DateTime start = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        var steady = new AdaptivePowerStatusKey("Balanced", false, false, 20, 4, "Stable");
        AdaptivePowerStatusKey? current = null;
        DateTime last = DateTime.MinValue;
        int writes = 0;
        for (int i = 0; i < 120; i++)
        {
            DateTime now = start.AddSeconds(i);
            if (!ShouldWriteAdaptiveStatus(current, steady, last, now)) continue;
            current = steady;
            last = now;
            writes++;
        }
        if (writes != 24 ||
            ShouldWriteAdaptiveStatus(steady, steady, start, start.AddSeconds(4)) ||
            !ShouldWriteAdaptiveStatus(steady, steady, start, start.AddSeconds(5)) ||
            !ShouldWriteAdaptiveStatus(steady,
                new AdaptivePowerStatusKey("Performance", true, false, 12, 4, "Stable"),
                start, start.AddSeconds(1)) ||
            !ShouldWriteAdaptiveStatus(steady, steady, start, start.AddSeconds(-1)))
            throw new InvalidOperationException("Adaptive status I/O pacing or critical-transition regression.");
        // Read-only same-host comparison. No settings/file mutations.
        const int samples = 240;
        var status = ReadStatus();
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long clock = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++) _ = ReadStatus();
        double diskMs = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        long diskBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        clock = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++)
            _ = IsStatusFresh(status, DateTime.UtcNow);
        double memoryMs = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        long memoryBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        return $"ADAPTIVE_STATUS_IO_SMOKE_OK jitterWritesOld=120 jitterWritesNew={writes} " +
            $"criticalImmediate=true samples={samples} fileReadMs={diskMs:F3} " +
            $"sameAgentMs={memoryMs:F3} fileReadAllocB={diskBytes} sameAgentAllocB={memoryBytes} " +
            $"statusPresent={status != null}";
    }

    // Read-only baseline comparison on the same machine: old duplicate
    // scheme/config metadata reads vs one snapshot shared within one tick.
    internal static string RunAdaptiveSharedTickSmoke()
    {
        const int cycles = 180;
        var config = AiSystemResourceManagerService.LoadConfig();
        bool active = TryGetActiveSpankyScheme(out string? guid);
        if (config == null || (active && string.IsNullOrWhiteSpace(guid)))
            throw new InvalidOperationException("Power-plan/config query returned invalid state.");
        for (int i = 0; i < 8; i++) _ = TryGetActiveSpankyScheme(out _);

        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long clock = Stopwatch.GetTimestamp();
        for (int i = 0; i < cycles; i++)
        {
            _ = TryGetActiveSpankyScheme(out _);
            _ = TryGetActiveSpankyScheme(out _);
            _ = TryGetActiveSpankyScheme(out _);
            _ = AiSystemResourceManagerService.LoadConfig();
            _ = AiSystemResourceManagerService.LoadConfig();
        }
        double oldMs = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

        beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        clock = Stopwatch.GetTimestamp();
        for (int i = 0; i < cycles; i++)
        {
            bool once = TryGetActiveSpankyScheme(out string? plan);
            var sameTick = AiSystemResourceManagerService.LoadConfig();
            // Consumers use the same immutable snapshot, not another disk
            // metadata query. An actual power mutation still revalidates.
            bool reused = once && !string.IsNullOrWhiteSpace(plan);
            _ = reused;
            _ = sameTick.PowerPlanEnabled;
        }
        double newMs = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        long newBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

        return $"ADAPTIVE_SHARED_TICK_SMOKE_OK cycles={cycles} " +
            $"originalPlanQueries={cycles * 3} optimizedPlanQueries={cycles} " +
            $"originalConfigReads={cycles * 2} optimizedConfigReads={cycles} " +
            $"originalMs={oldMs:F3} optimizedMs={newMs:F3} " +
            $"originalAllocatedB={oldBytes} optimizedAllocatedB={newBytes} " +
            $"spankyPlanActive={active}";
    }

    // A/B of the exact read-only Windows registry operation eliminated from
    // the normal AI EPP tick. Power mutation still performs its own verified
    // re-read under AiPowerGate, and disabled AI never touches live settings.
    internal static string RunEppSameTickReuseSmoke()
    {
        if (!TryGetActiveSpankyScheme(out string? guid) || string.IsNullOrWhiteSpace(guid))
            return "ADAPTIVE_EPP_REGISTRY_REUSE_SMOKE_OK skipped=no-active-spanky-plan";

        const int cycles = 256;
        bool readable = TryReadAcSetting(guid, EppSetting, out _);
        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < cycles; i++)
        {
            _ = TryReadAcSetting(guid, EppSetting, out _);
            _ = TryReadAcSetting(guid, EppSetting, out _);
        }
        double repeatedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long repeatedBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;

        startBytes = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < cycles; i++)
        {
            bool known = TryReadAcSetting(guid, EppSetting, out int first);
            // Identical native snapshot is reused by the mode-selection
            // preflight; fresh read-back is still required before writes.
            int reused = first;
            _ = known && reused >= 0;
        }
        double reusedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long reusedBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
        return $"ADAPTIVE_EPP_REGISTRY_REUSE_SMOKE_OK cycles={cycles} " +
            $"previousReads={cycles * 2} reusedReads={cycles} " +
            $"readable={readable} beforeMs={repeatedMs:F3} afterMs={reusedMs:F3} " +
            $"beforeAllocatedB={repeatedBytes} afterAllocatedB={reusedBytes} " +
            "mutations=0";
    }

    private void MaybeWriteStatus(AdaptivePowerRuntimeStatus status)
    {
        AdaptivePowerStatusKey criticalKey = AdaptivePowerStatusKey.From(status);
        DateTime now = DateTime.UtcNow;
        if (!ShouldWriteAdaptiveStatus(_lastCriticalStatusKey, criticalKey, _lastStatusWriteUtc, now))
            return;
        if (!IsStatusWriteRetryDue(now, _lastStatusFailureUtc))
            return;

        try
        {
            AtomicFileService.WriteText(
                StatusPath,
                JsonSerializer.Serialize(status));
            _lastStatusWriteUtc = now;
            _lastStatusFailureUtc = DateTime.MinValue;
            _lastCriticalStatusKey = criticalKey;
        }
        catch (Exception ex)
        {
            _lastStatusFailureUtc = now;
            if (IsStatusWriteErrorLogDue(now, _lastStatusErrorLogUtc))
            {
                _lastStatusErrorLogUtc = now;
                LogService.Error("power", "Adaptive runtime status write failed", ex);
            }
        }
    }

    internal static bool IsStatusWriteRetryDue(
        DateTime nowUtc,
        DateTime lastFailureUtc) =>
        lastFailureUtc == DateTime.MinValue || nowUtc < lastFailureUtc ||
        nowUtc - lastFailureUtc >= TimeSpan.FromSeconds(30);

    internal static bool IsStatusWriteErrorLogDue(DateTime nowUtc, DateTime lastLogUtc) =>
        lastLogUtc == DateTime.MinValue || nowUtc < lastLogUtc ||
        nowUtc - lastLogUtc >= TimeSpan.FromMinutes(5);

    private RuntimeMode ChooseMode(
        double load,
        double rawLoad,
        bool realIdle,
        bool userInputIdle,
        bool gameActive,
        bool aiSmartEnabled,
        AiSmartRuntimeDecision aiDecision,
        double? packagePowerW,
        double? clockMHz,
        AiWorkloadClass workload)
    {
        load = Math.Clamp(load, 0, 100);
        rawLoad = Math.Clamp(rawLoad, 0, 100);

        if (gameActive)
        {
            // Gaming bypasses ordinary thermal-vote evaluation. Discard the
            // previous non-gaming streak so it cannot affect the first
            // post-game evaluation without fresh thermal evidence.
            _thermalGuardVotes = 0;
            _performanceVotes = 3;
            _idleVotes = 0;
            _exitPerformanceVotes = 0;
            return RuntimeMode.Performance;
        }

        bool thermalGuard =
            aiSmartEnabled &&
            AiPowerDemandPolicy.ShouldThermalGuard(
                gameActive,
                aiDecision.PredictedTemp10s,
                Math.Max(load, rawLoad),
                aiDecision.Confidence);

        _thermalGuardVotes = thermalGuard
            ? Math.Min(3, _thermalGuardVotes + 1)
            : Math.Max(0, _thermalGuardVotes - 1);

        if (_thermalGuardVotes >= 2)
        {
            _performanceVotes = 0;
            _idleVotes = 0;
            _exitPerformanceVotes = 0;
            return RuntimeMode.Balanced;
        }

        // A single thermal-warning epoch must not build Performance entry
        // votes that can immediately override the safety decision next tick.
        if (thermalGuard)
            _performanceVotes = 0;

        bool aiPerformanceSignal =
            aiSmartEnabled &&
            AiPowerDemandPolicy.HasPerformanceDemand(
                gameActive,
                Math.Max(load, rawLoad),
                packagePowerW,
                clockMHz,
                aiDecision.AnomalyScore,
                workload,
                aiDecision.Confidence) &&
            !thermalGuard;

        bool aiIdleSignal =
            aiSmartEnabled &&
            AiPowerIdleAdmissionPolicy.AllowsSuggestedIdle(
                aiDecision.PreferIdle, workload,
                aiDecision.Confidence, aiDecision.AnomalyScore,
                Math.Max(load, rawLoad), packagePowerW);

        bool conservativeWorkload =
            AiWorkloadContextPolicy.RequiresConservativePerformanceThreshold(workload);

        if (aiPerformanceSignal)
        {
            int voteGain = AiPowerTransitionPolicy.PerformanceVoteGain(
                true,
                workload,
                aiDecision.Confidence,
                Math.Max(load, rawLoad),
                packagePowerW);
            _performanceVotes = Math.Min(3, _performanceVotes + voteGain);
        }
        else if (!conservativeWorkload && !thermalGuard &&
                 (load >= 35 || rawLoad >= 60))
        {
            // A verified short CPU burst can precede the smoothed-load EMA.
            // React without waiting for averaging, but retain two-vote entry,
            // conservative-workload admission and the thermal safety gate.
            double demandLoad = Math.Max(load, rawLoad);
            _performanceVotes = Math.Min(3, _performanceVotes +
                AiPowerTransitionPolicy.FallbackInteractiveVoteGain(workload, demandLoad));
        }
        else
            _performanceVotes = 0;

        // A delayed idle classification must never accumulate votes while
        // the same CPU epoch reports a fresh heavy burst. The fast raw-load
        // Performance admission and Idle admission must use compatible evidence.
        if (AiPowerTransitionHysteresisPolicy.AllowIdleEntry(
                realIdle, aiIdleSignal, userInputIdle,
                _lastIdleExitUtc, DateTime.UtcNow) && rawLoad < 35 &&
            !thermalGuard && !aiPerformanceSignal && _performanceVotes == 0)
            _idleVotes++;
        else
            _idleVotes = 0;

        if (_mode == RuntimeMode.Performance)
        {
            bool powerCoolEnough =
                !packagePowerW.HasValue ||
                packagePowerW.Value < 70;

            // Do not exit Performance on a low EMA while the same validated
            // tick still observes a heavy raw CPU burst. Avoid EPP oscillation
            // without removing the existing sustained-exit hysteresis.
            bool sustainedExitSignal =
                !aiPerformanceSignal && rawLoad < 35 &&
                ((load < 30 && powerCoolEnough) ||
                 (load < 20 && rawLoad < 20));

            if (sustainedExitSignal)
                _exitPerformanceVotes++;
            else
                _exitPerformanceVotes = 0;

            if (_exitPerformanceVotes < 4)
                return RuntimeMode.Performance;
            if (AiPowerTransitionHysteresisPolicy.HoldPerformanceForBriefOrdinaryDip(
                    _lastModeTransitionUtc, DateTime.UtcNow, load, realIdle))
            {
                _deferredOrdinaryTransitions++;
                return RuntimeMode.Performance;
            }

            _exitPerformanceVotes = 0;
        }
        else
        {
            _exitPerformanceVotes = 0;
        }

        if (_performanceVotes >= 2)
        {
            // The same interactive 25..40% load used to flip EPP20->12
            // within 2-3 seconds after Balanced exit. Delay ONLY ordinary
            // re-entries: games, >=60% bursts, thermal safety and strong
            // 3-vote urgent demand remain immediate.
            // Three accumulated ordinary votes are NOT an urgent request.
            // Only a genuine per-tick urgent signal (game, measured burst
            // >=60%, or high CPU-package power) can skip recovery dwell.
            bool urgentOnThisTick = Math.Max(load, rawLoad) >= 60 ||
                (aiPerformanceSignal &&
                 AiPowerTransitionPolicy.PerformanceVoteGain(
                     true, workload, aiDecision.Confidence,
                     Math.Max(load, rawLoad), packagePowerW) >= 3);
            if (_mode == RuntimeMode.Balanced &&
                AiPowerTransitionHysteresisPolicy.HoldBalancedAfterRecentPerformanceExit(
                    _lastModeTransitionUtc, DateTime.UtcNow,
                    load, rawLoad, gameActive, urgentOnThisTick))
            {
                _deferredOrdinaryTransitions++;
                return RuntimeMode.Balanced;
            }
            return RuntimeMode.Performance;
        }

        if (_idleVotes >= 4)
            return RuntimeMode.Idle;

        if (_mode == RuntimeMode.Idle && realIdle)
            return RuntimeMode.Idle;

        return RuntimeMode.Balanced;
    }

    private static (int Epp, int Boost) GetModeValues(RuntimeMode mode)
    {
        return mode switch
        {
            RuntimeMode.Idle => (30, 3),
            RuntimeMode.Performance => (12, 4),
            _ => (20, 4)
        };
    }

    // Shared catalog avoids allocating a new 12-entry tuple array per self-heal pass.
    private static readonly (string Setting, int Value, string Name)[] BasePowerSettings =
        {
            ("893dee8e-2bef-41e0-89c6-b55d0929964c", 5, "cpu-min"),
            ("bc5038f7-23e0-4960-96da-33abaf5935ec", 100, "cpu-max"),
            ("8baa4a8a-14c6-4451-8e8b-14bdbd197537", 1, "cppc"),
            ("5d76a2ca-e8c0-402f-a133-2158492d58ad", 0, "cpu-idle"),
            ("94d3a615-a899-4ac5-ae2b-e4d8f634367f", 1, "cooling"),
            ("0cc5b647-c1df-4637-891a-dec35c318583", 25, "core-min"),
            ("ea062031-0e34-4ff1-9b6d-eb1059334028", 100, "core-max"),
            ("465e1f50-b610-473a-ab58-00d1077dc418", 2, "perf-inc-policy"),
            ("40fbefc7-2e9d-4d25-a185-0cfd8574bac6", 0, "perf-dec-policy"),
            ("06cadf0e-64ed-448a-8927-ce7bf90eb35d", 10, "perf-inc-threshold"),
            ("12a0ab44-fe28-4fa9-b3bd-4b64f44960a6", 8, "perf-dec-threshold"),
            ("4d2b0152-7d5c-498b-88e2-34345392a2c5", 15, "perf-check")
        };

    private static async Task<int> EnsureBaseSettingsAsync(string guid, CancellationToken token)
    {
        // The entire 12-setting self-heal, ownership journal and final plan
        // refresh must be one exclusive transaction. Otherwise the UI or
        // another RuntimeHost could restore EPP/Boost halfway through it.
        return await RunBasePowerRepairTransactionAsync(
            AiPowerPath,
            async ct =>
            {
                int repaired = 0;
                foreach (var item in BasePowerSettings)
                {
                    ct.ThrowIfCancellationRequested();
                    if (TryReadAcSetting(guid, item.Setting, out int current) &&
                        current == item.Value)
                        continue;

                    if (await MutateAiSettingAsync(guid, item.Setting, item.Value, ct)
                            .ConfigureAwait(false))
                    {
                        repaired++;
                        LogService.Info("power",
                            $"ADAPTIVE_SELF_HEAL setting={item.Name} value={item.Value}");
                    }
                }
                return repaired;
            },
            async ct =>
            {
                var refresh = await PrivilegedProcessRunner.RunAsync(
                    "powercfg.exe", $"/setactive {guid}",
                    TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                if (!refresh.Success)
                    throw new InvalidOperationException("AI base profile refresh failed");
            },
            token,
            stillActive: () =>
                TryGetActiveSpankyScheme(out string? currentPlan) &&
                string.Equals(currentPlan, guid, StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
    }

    // Testable coordination boundary. The same cross-process and in-process
    // ownership locks must cover each repair AND the /setactive refresh.
    internal static Task<int> RunBasePowerRepairTransactionAsync(
        string ownershipPath,
        Func<CancellationToken, Task<int>> repairs,
        Func<CancellationToken, Task> refresh,
        CancellationToken token = default,
        Func<bool>? stillActive = null)
    {
        ArgumentNullException.ThrowIfNull(repairs);
        ArgumentNullException.ThrowIfNull(refresh);
        return RunAiPowerTransactionAsync(ownershipPath, async ct =>
        {
            ct.ThrowIfCancellationRequested();
            // The caller checked its plan before waiting for ownership. Check
            // again inside the lease, or stale queued repairs may run later.
            if (stillActive is not null && !stillActive()) return 0;
            int repaired = await repairs(ct).ConfigureAwait(false);
            if (repaired > 0)
            {
                ct.ThrowIfCancellationRequested();
                // A user-selected plan switch must never be undone by this
                // delayed self-heal's /setactive refresh.
                if (stillActive is not null && !stillActive()) return 0;
                await refresh(ct).ConfigureAwait(false);
            }
            return repaired;
        }, token);
    }

    public sealed record ManagedPowerSetting(string Name, int Original, int Current);

    // A compact, read-only view of settings *actually owned and read-back verified*
    // by the independent FanAgent, not a generic list of recommended catalog values.
    public static IReadOnlyList<ManagedPowerSetting> ReadManagedSettings(string? activeGuid)
    {
        if (string.IsNullOrWhiteSpace(activeGuid)) return Array.Empty<ManagedPowerSetting>();
        try
        {
            if (!File.Exists(AiPowerPath)) return Array.Empty<ManagedPowerSetting>();
            var rows = JsonSerializer.Deserialize<Dictionary<string, AiPowerOwnership>>(
                File.ReadAllText(AiPowerPath));
            if (rows == null) return Array.Empty<ManagedPowerSetting>();
            var result = new List<ManagedPowerSetting>();
            foreach (var item in rows.Values)
            {
                if (!item.Managed || item.Suppressed ||
                    !string.Equals(item.Guid, activeGuid, StringComparison.OrdinalIgnoreCase) ||
                    !TryReadAcSetting(item.Guid, item.Setting, out int actual) ||
                    actual != item.Applied) continue;
                string name = item.Setting.ToLowerInvariant() switch
                {
                    EppSetting => "CPU EPP",
                    BoostSetting => "CPU Boost",
                    "893dee8e-2bef-41e0-89c6-b55d0929964c" => "CPU min %",
                    "bc5038f7-23e0-4960-96da-33abaf5935ec" => "CPU max %",
                    "8baa4a8a-14c6-4451-8e8b-14bdbd197537" => "CPPC",
                    "5d76a2ca-e8c0-402f-a133-2158492d58ad" => "CPU idle",
                    "94d3a615-a899-4ac5-ae2b-e4d8f634367f" => "Chlazení",
                    "0cc5b647-c1df-4637-891a-dec35c318583" => "Core parking min %",
                    "ea062031-0e34-4ff1-9b6d-eb1059334028" => "Core parking max %",
                    "465e1f50-b610-473a-ab58-00d1077dc418" => "CPU perf increase",
                    "40fbefc7-2e9d-4d25-a185-0cfd8574bac6" => "CPU perf decrease",
                    "06cadf0e-64ed-448a-8927-ce7bf90eb35d" => "CPU increase threshold",
                    "12a0ab44-fe28-4fa9-b3bd-4b64f44960a6" => "CPU decrease threshold",
                    "4d2b0152-7d5c-498b-88e2-34345392a2c5" => "CPU perf check",
                    _ => "CPU " + item.Setting[..8]
                };
                result.Add(new(name, item.Original, actual));
            }
            return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex)
        {
            LogService.Error("power", "AI-managed power settings read failed", ex);
            return Array.Empty<ManagedPowerSetting>();
        }
    }

    private sealed record AiPowerOwnership(string Guid, string Setting, int Original, int Applied, bool Managed, bool Suppressed);
    private static readonly SemaphoreSlim AiPowerGate = new(1, 1);
    private static string AiPowerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpankyhoOptimizer", "ai-power-ownership.json");

    private static async Task<Dictionary<string, AiPowerOwnership>> LoadAiPowerAsync(CancellationToken token)
    {
        if (!File.Exists(AiPowerPath)) return new(StringComparer.OrdinalIgnoreCase);
        // Corruption must fail closed: never overwrite unknown originals.
        return JsonSerializer.Deserialize<Dictionary<string, AiPowerOwnership>>(
            await File.ReadAllTextAsync(AiPowerPath, token).ConfigureAwait(false))
            ?? throw new InvalidOperationException("AI power ownership snapshot invalid");
    }
    private static Task SaveAiPowerAsync(Dictionary<string, AiPowerOwnership> rows, CancellationToken token) =>
        AtomicFileService.WriteTextAsync(AiPowerPath, JsonSerializer.Serialize(rows), token);

    private static async Task SetVerifiedPowerAsync(string guid, string setting, int value, CancellationToken token)
    {
        var write = await PrivilegedProcessRunner.RunAsync("powercfg.exe",
            $"/setacvalueindex {guid} {CpuSubgroup} {setting} {value}", TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
        if (!write.Success || !TryReadAcSetting(guid, setting, out int actual) || actual != value)
            throw new InvalidOperationException("AI power read-back failed: " + setting + " " + write.StdErr);
    }

    // Only called within RunAiPowerTransactionAsync. Ownership must not be
    // released between EPP, Boost and /setactive: another UI/RuntimeHost
    // restore would otherwise observe and revert a half-applied profile.
    private static async Task<bool> MutateAiSettingAsync(string guid, string setting, int value,
        CancellationToken token, bool aiEppControlEnabled = false)
    {
            if (!TryReadAcSetting(guid, setting, out int current))
                return false; // missing setting has no verifiable original
            var rows = await LoadAiPowerAsync(token).ConfigureAwait(false);
            string key = guid + "|" + setting;
            rows.TryGetValue(key, out var previous);
            bool autonomousEpp = aiEppControlEnabled &&
                string.Equals(setting, EppSetting, StringComparison.OrdinalIgnoreCase);
            if (previous?.Suppressed == true && !autonomousEpp) return false;
            if (previous?.Managed == true && current != previous.Applied && !autonomousEpp)
            {
                rows[key] = previous with { Managed = false, Suppressed = true };
                await SaveAiPowerAsync(rows, token).ConfigureAwait(false);
                LogService.Info("power", "AI_POWER_MANUAL_OVERRIDE preserved " + key);
                return false;
            }
            if (current == value) return false;
            // When AI retakes an EPP that was previously overridden, the
            // *current* value becomes the rollback baseline. Otherwise an
            // already owned, unmodified setting keeps its original baseline.
            int original = ChooseOwnershipOriginal(previous, current, autonomousEpp);
            var row = new AiPowerOwnership(guid, setting,
                original, value, true, false);
            rows[key] = row;
            await SaveAiPowerAsync(rows, token).ConfigureAwait(false); // durable original before mutation
            try
            {
                await SetVerifiedPowerAsync(guid, setting, value, token).ConfigureAwait(false);
                return true;
            }
            catch
            {
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await SetVerifiedPowerAsync(guid, setting, current, rollback.Token).ConfigureAwait(false);
                if (previous == null) rows.Remove(key); else rows[key] = previous;
                await SaveAiPowerAsync(rows, rollback.Token).ConfigureAwait(false);
                throw;
            }
    }

    // Both lock layers cover the entire two-setting native transaction.
    // The in-process semaphore is always acquired first (matching Restore),
    // which prevents circular waits with the cross-process file lease.
    internal static async Task<T> RunAiPowerTransactionAsync<T>(
        string ownershipPath, Func<CancellationToken, Task<T>> operation,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        long gateStart = Stopwatch.GetTimestamp();
        await AiPowerGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            double localWaitMs = Stopwatch.GetElapsedTime(gateStart).TotalMilliseconds;
            long leaseStart = Stopwatch.GetTimestamp();
            using var ownershipLease = await AiPowerOwnershipFileGate.AcquireAsync(
                ownershipPath, token).ConfigureAwait(false);
            double leaseWaitMs = Stopwatch.GetElapsedTime(leaseStart).TotalMilliseconds;
            if (localWaitMs >= 200 || leaseWaitMs >= 200)
                LogService.Info("power", $"AI_POWER_GATE_SLOW localWaitMs={localWaitMs:F1} leaseWaitMs={leaseWaitMs:F1}");
            return await operation(token).ConfigureAwait(false);
        }
        finally { AiPowerGate.Release(); }
    }

    public static async Task RestoreAllAiSettingsAsync(CancellationToken token = default)
    {
        await RunAiPowerTransactionAsync(AiPowerPath, async ct =>
        {
            await RestoreAllAiSettingsCoreAsync(ct).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    // Called with BOTH gates held, including from the failed-write
    // compensation path. Never acquire either lock recursively here.
    private static async Task RestoreAllAiSettingsCoreAsync(CancellationToken token)
    {
            var rows = await LoadAiPowerAsync(token).ConfigureAwait(false);
            var changedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in rows.Where(x => x.Value.Managed).ToArray())
            {
                token.ThrowIfCancellationRequested();
                var row = pair.Value;
                if (!TryReadAcSetting(row.Guid, row.Setting, out int current)) continue;
                if (current == row.Original)
                    rows[pair.Key] = row with { Managed = false };
                else if (current != row.Applied)
                {
                    rows[pair.Key] = row with { Managed = false, Suppressed = true };
                    LogService.Info("power", "AI_POWER_MANUAL_OVERRIDE preserved " + pair.Key);
                }
                else
                {
                    await SetVerifiedPowerAsync(row.Guid, row.Setting, row.Original, token).ConfigureAwait(false);
                    rows[pair.Key] = row with { Managed = false };
                    changedSchemes.Add(row.Guid);
                }
                await SaveAiPowerAsync(rows, CancellationToken.None).ConfigureAwait(false);
            }
            // Refresh only the already-active Spanky scheme; never activate an old/inactive scheme.
            if (TryGetActiveSpankyScheme(out var active) && active != null && changedSchemes.Contains(active))
            {
                var refresh = await PrivilegedProcessRunner.RunAsync("powercfg.exe", "/setactive " + active,
                    TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
                if (!refresh.Success) throw new InvalidOperationException("AI power restore refresh failed");
            }
    }

    private static int ChooseOwnershipOriginal(AiPowerOwnership? previous, int current, bool autonomousEpp) =>
        previous?.Managed == true && (current == previous.Applied || !autonomousEpp)
            ? previous.Original
            : current;

    // Restart, re-enable/disable and clock rollback all force a restore check.
    // After a successful check while disabled, verify again within 30 seconds.
    internal static bool ShouldRunDisabledPowerRestore(DateTime nowUtc, DateTime lastRestoreUtc,
        bool previouslyManaged) =>
        previouslyManaged || lastRestoreUtc == DateTime.MinValue ||
        nowUtc < lastRestoreUtc ||
        nowUtc - lastRestoreUtc >= TimeSpan.FromSeconds(30);

    // Both routine 30s power-setting rechecks and baseline self-heals are
    // elapsed-time gates. An NTP/Windows clock adjustment must not postpone
    // a needed readback or trigger extra native work in a single interval.
    internal static bool IsMonotonicPowerRefreshDue(
        long previousStamp, long nowStamp, long frequency, TimeSpan interval) =>
        nowStamp > 0 && frequency > 0 && interval > TimeSpan.Zero &&
        (previousStamp == 0 || (previousStamp > 0 && nowStamp >= previousStamp &&
            (nowStamp - (double)previousStamp) / frequency > interval.TotalSeconds));

    internal static string RunMonotonicPowerRefreshSmoke()
    {
        const long frequency = 10_000;
        const long first = 100_000;
        long boundary = first + 30 * frequency;
        if (!IsMonotonicPowerRefreshDue(0, first, frequency, TimeSpan.FromSeconds(30)) ||
            IsMonotonicPowerRefreshDue(first, boundary, frequency, TimeSpan.FromSeconds(30)) ||
            !IsMonotonicPowerRefreshDue(first, boundary + 1, frequency, TimeSpan.FromSeconds(30)) ||
            IsMonotonicPowerRefreshDue(first, first - 1, frequency, TimeSpan.FromSeconds(30)) ||
            IsMonotonicPowerRefreshDue(-1, boundary + 1, frequency, TimeSpan.FromSeconds(30)) ||
            IsMonotonicPowerRefreshDue(first, boundary + 1, 0, TimeSpan.FromSeconds(30)) ||
            IsMonotonicPowerRefreshDue(first, boundary + 1, frequency, TimeSpan.Zero) ||
            IsMonotonicPowerRefreshDue(0, -1, frequency, TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("AI power readback monotonic cadence regressed.");
        DateTime utc = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        if (utc.AddHours(-2) >= utc ||
            IsMonotonicPowerRefreshDue(first, first + frequency * 5,
                frequency, TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("AI power readback depends on Windows wall-clock adjustments.");
        return "AI_POWER_MONOTONIC_READBACK_SMOKE_OK interval30s=true " +
               "clockJumpIgnored=true noExtraNativeReads=true windowsWrites=0";
    }

    // While AI power management is enabled, its adaptive EPP target has
    // explicit user authorization even if an older ownership record says
    // Suppressed. Other settings still honor manual-override suppression.
    // Only observed native EPP may advance the mode state and hysteresis
    // clocks. A failed write, unreadable value or external plan change cannot
    // be represented as a successful transition in UI or future decisions.
    internal static bool CanCommitObservedMode(
        bool modeChanged, bool targetEppConfirmed, bool sameActivePlan) =>
        !modeChanged || (targetEppConfirmed && sameActivePlan);

    internal static bool ShouldAttemptPowerMutation(
        bool eppKnown, int currentEpp, int desiredEpp, bool aiEppControlEnabled,
        bool boostKnown, int currentBoost, int desiredBoost, bool boostSuppressed) =>
        (eppKnown && aiEppControlEnabled && currentEpp != desiredEpp) ||
        (boostKnown && !boostSuppressed && currentBoost != desiredBoost);

    // Read-only functional regression: while the user has enabled AI power
    // management, EPP automatically follows AI even if a stale ownership
    // record was suppressed by an earlier external change. No OS writes.
    internal static string RunAutonomousEppControlSmoke()
    {
        const int cycles = 120;
        // 120 simulated 2s FanAgent ticks while AI power management is off:
        // initial restore plus periodic verification, never suppressed forever.
        DateTime initial = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        DateTime lastRestore = DateTime.MinValue;
        int disabledRestoreChecks = 0;
        for (int tick = 0; tick < cycles; tick++)
        {
            DateTime now = initial.AddSeconds(tick * 2);
            if (!ShouldRunDisabledPowerRestore(now, lastRestore, false)) continue;
            disabledRestoreChecks++;
            lastRestore = now;
        }
        if (disabledRestoreChecks != 8 ||
            !ShouldRunDisabledPowerRestore(initial, DateTime.MinValue, false) ||
            !ShouldRunDisabledPowerRestore(initial.AddSeconds(2), initial, true) ||
            ShouldRunDisabledPowerRestore(initial.AddSeconds(29), initial, false) ||
            !ShouldRunDisabledPowerRestore(initial.AddSeconds(30), initial, false) ||
            !ShouldRunDisabledPowerRestore(initial.AddSeconds(-1), initial, false))
            throw new InvalidOperationException("Disabled AI power restore pacing regression.");
        int attempts = 0;
        for (int i = 0; i < cycles; i++)
        {
            if (ShouldAttemptPowerMutation(
                    true, 20, 12, true, true, 4, 4, false)) attempts++;
        }
        var staleEpp = new AiPowerOwnership("test", EppSetting, 20, 12, false, true);
        var activeEpp = new AiPowerOwnership("test", EppSetting, 30, 12, true, false);
        if (attempts != cycles ||
            ChooseOwnershipOriginal(staleEpp, 20, true) != 20 ||
            ChooseOwnershipOriginal(activeEpp, 12, true) != 30 ||
            ChooseOwnershipOriginal(activeEpp, 18, true) != 18 ||
            ShouldAttemptPowerMutation(true, 20, 12, false, true, 4, 4, false) ||
            !ShouldAttemptPowerMutation(true, 20, 12, true, true, 3, 4, false) ||
            !ShouldAttemptPowerMutation(false, 0, 12, false, true, 3, 4, false) ||
            ShouldAttemptPowerMutation(false, 0, 12, false, false, 0, 4, false) ||
            ShouldAttemptPowerMutation(true, 12, 12, true, true, 4, 4, false) ||
            !ShouldAttemptPowerMutation(true, 20, 12, true, true, 3, 4, true) ||
            ShouldAttemptPowerMutation(true, 12, 12, true, true, 3, 4, true))
            throw new InvalidOperationException("Autonomous EPP ownership/Boost isolation regression.");
        return $"ADAPTIVE_EPP_AI_CONTROL_SMOKE_OK cycles={cycles} " +
            $"aiEnabledMismatchAttempts={attempts} aiDisabledAttempts=0 " +
            $"aiDisabledRestoreChecks={disabledRestoreChecks} overSeconds=240 " +
            "staleEppSuppression=ignored boostManualOverride=preserved " +
            "unchangedEpp=skip unreadableFailClosed=true";
    }

    private static Task<bool> ApplyAsync(string guid, int epp, int boost, CancellationToken token) =>
        RunPlanScopedPowerMutationAsync(
            AiPowerPath, guid,
            () => TryGetActiveSpankyScheme(out string? active) ? active : null,
            async ct =>
            {
                bool changed = await MutateAiSettingAsync(
                    guid, EppSetting, epp, ct, aiEppControlEnabled: true).ConfigureAwait(false);
                changed |= await MutateAiSettingAsync(guid, BoostSetting, boost, ct).ConfigureAwait(false);
                return changed;
            },
            async ct =>
            {
                var refresh = await PrivilegedProcessRunner.RunAsync(
                    "powercfg.exe", $"/setactive {guid}",
                    TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                if (!refresh.Success)
                    throw new InvalidOperationException("AI adaptive refresh failed");
            },
            RestoreAllAiSettingsCoreAsync,
            token);

    // Both checks happen WITHIN the exclusive ownership transaction. The
    // caller checked the plan before waiting for the lease, but the user may
    // have selected another Windows scheme while the queue was blocked.
    // Never force /setactive back to the stale plan. If the plan switches
    // after native writes, compensate before releasing the lease.
    internal static Task<bool> RunPlanScopedPowerMutationAsync(
        string ownershipPath,
        string expectedPlan,
        Func<string?> currentActivePlan,
        Func<CancellationToken, Task<bool>> mutate,
        Func<CancellationToken, Task> refresh,
        Func<CancellationToken, Task> rollback,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPlan);
        ArgumentNullException.ThrowIfNull(currentActivePlan);
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(refresh);
        return RunPowerMutationWithRollbackAsync(
            ownershipPath,
            async ct =>
            {
                if (!string.Equals(currentActivePlan(), expectedPlan, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!await mutate(ct).ConfigureAwait(false))
                    return false;
                if (!string.Equals(currentActivePlan(), expectedPlan, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Active Windows power plan switched during AI mutation");
                await refresh(ct).ConfigureAwait(false);
                return true;
            },
            rollback, token);
    }

    // Any compensation must run before we release the ownership lease.
    // Otherwise another host could commit a valid change between the failed
    // apply and our rollback, which would then silently revert their work.
    // Failure to acquire the lease never invokes rollback.
    internal static Task<T> RunPowerMutationWithRollbackAsync<T>(
        string ownershipPath,
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task> rollback,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(rollback);
        return RunAiPowerTransactionAsync(ownershipPath, async ct =>
        {
            try { return await operation(ct).ConfigureAwait(false); }
            catch
            {
                using var compensation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await rollback(compensation.Token).ConfigureAwait(false);
                throw;
            }
        }, token);
    }

    // Legacy contract used by existing read-only regression tests.
    internal static bool ShouldRestoreAfterApplyFailure(bool transactionEntered) =>
        transactionEntered;

    private static bool TryGetActiveSpankyScheme(out string? activeGuid)
    {
        activeGuid = null;
        const string root = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

        try
        {
            using RegistryKey? schemes = Registry.LocalMachine.OpenSubKey(root);
            if (schemes == null) return false;

            activeGuid = schemes.GetValue("ActivePowerScheme")?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(activeGuid)) return false;

            using RegistryKey? active = schemes.OpenSubKey(activeGuid);
            string name = active?.GetValue("FriendlyName")?.ToString() ?? string.Empty;
            return name.Contains("Spankyho Performance", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            activeGuid = null;
            return false;
        }
    }

    private static bool TryReadAcSetting(string guid, string setting, out int value)
    {
        value = 0;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{guid}\{CpuSubgroup}\{setting}");
            object? raw = key?.GetValue("ACSettingIndex");
            if (raw == null) return false;
            value = Convert.ToInt32(raw);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Process names are immutable for a running PID. Reuse an existing name
    // on ordinary CPU-delta samples, but detect recycled PID resets and force
    // periodic full refresh. New PIDs (including newly launched games) always
    // resolve their actual process name immediately.
    internal static bool CanReuseProcessName(TimeSpan currentCpu,
        AdaptiveProcessCpuSample previous, bool forceRefresh) =>
        !forceRefresh && !string.IsNullOrEmpty(previous.Name) && currentCpu >= previous.Cpu;

    // Read-only native snapshot benchmark: old every-PID ProcessName vs
    // same snapshots with previous-cycle name reuse. No process manipulation.
    internal static string RunProcessNameReuseSmoke()
    {
        var synthetic = new AdaptiveProcessCpuSample(TimeSpan.FromSeconds(4), "DiabloImmortal");
        if (!CanReuseProcessName(TimeSpan.FromSeconds(5), synthetic, false) ||
            CanReuseProcessName(TimeSpan.FromSeconds(3), synthetic, false) ||
            CanReuseProcessName(TimeSpan.FromSeconds(5), synthetic, true) ||
            CanReuseProcessName(TimeSpan.FromSeconds(5),
                new AdaptiveProcessCpuSample(TimeSpan.Zero, ""), false))
            throw new InvalidOperationException("PID name-cache reuse/refresh policy regressed.");

        const int rounds = 16;
        static (double Ms, long Bytes, int NativeReads, int Reused, int Snapshots)
            Measure(bool cache)
        {
            var previous = new Dictionary<int, AdaptiveProcessCpuSample>(256);
            var next = new Dictionary<int, AdaptiveProcessCpuSample>(256);
            int nativeReads = 0, reused = 0, snapshots = 0;
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int round = 0; round < rounds; round++)
            {
                next.Clear();
                Process[] processes = Process.GetProcesses();
                try
                {
                    snapshots++;
                    foreach (Process process in processes)
                    {
                        try
                        {
                            int pid = process.Id;
                            TimeSpan cpu = process.TotalProcessorTime;
                            bool found = previous.TryGetValue(pid, out var prior);
                            bool reuse = cache && found &&
                                CanReuseProcessName(cpu, prior, (round & 3) == 3);
                            string name = reuse ? prior.Name : process.ProcessName;
                            if (reuse) reused++; else nativeReads++;
                            next[pid] = new AdaptiveProcessCpuSample(cpu, name);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or
                            System.ComponentModel.Win32Exception or NotSupportedException or ArgumentException) { }
                    }
                }
                finally { ProcessSnapshotCleanup.DisposeAll(processes); }
                (previous, next) = (next, previous);
            }
            return (Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - beforeBytes,
                nativeReads, reused, snapshots);
        }
        var before = Measure(false);
        var after = Measure(true);
        if (after.Reused <= 0 || after.NativeReads >= before.NativeReads ||
            after.Snapshots != rounds)
            throw new InvalidOperationException("Process name cache did not reduce native accesses.");
        return $"ADAPTIVE_PROCESS_NAME_REUSE_SMOKE_OK rounds={rounds} " +
            $"beforeNameReads={before.NativeReads} afterNameReads={after.NativeReads} " +
            $"reused={after.Reused} beforeMs={before.Ms:F3} afterMs={after.Ms:F3} " +
            $"beforeAllocatedB={before.Bytes} afterAllocatedB={after.Bytes} " +
            "newPid=fresh recycledPid=fresh periodicRefresh=4";
    }

    private static double? ReadWorkingSetMiB(Process process)
    {
        // Exactly the five already-identifiable allowlisted processes in the
        // existing CPU snapshot. Read-only, no EmptyWorkingSet, handle scans,
        // process restarts, extra enumeration or claim of permanent RAM savings.
        try
        {
            long bytes = process.WorkingSet64;
            return bytes >= 0 ? Math.Round(bytes / 1048576d, 2) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException or
            ArgumentException or UnauthorizedAccessException)
        {
            return null; // Missing/denied is unknown, not zero RAM.
        }
    }

    private double? ReadCachedWorkingSetMiB(
        Process process, bool refresh, bool sameProcessCpuHistory,
        ref int nativeReads, ref int cacheHits)
    {
        int pid = process.Id;
        if (refresh)
        {
            double? value = ReadWorkingSetMiB(process);
            nativeReads++;
            if (_workingSetByPid.Count < 8)
                _workingSetByPid[pid] = value;
            return value;
        }
        if (sameProcessCpuHistory &&
            _workingSetByPid.TryGetValue(pid, out double? cached))
        {
            cacheHits++;
            return cached;
        }
        // A new PID or reset CPU counter must not inherit stale cached RAM.
        return null;
    }

    private ProcessLoadSample SampleProcessLoad(CancellationToken token, int foregroundPid)
    {
        token.ThrowIfCancellationRequested();
        DateTime now = DateTime.UtcNow;
        // TickAsync is serialized by _tickGate and has already validated
        // snapshot age, foreground PID, and HWND. Never reuse again here:
        // a changed HWND with the same PID would otherwise be invisible.
        bool metricDue = now - _lastProcessSampleMetricUtc >= TimeSpan.FromMinutes(1);
        using Process? ownMetrics = metricDue ? Process.GetCurrentProcess() : null;
        long cpuBeforeTicks = metricDue
            ? ownMetrics!.TotalProcessorTime.Ticks : 0;
        long sampleStart = Stopwatch.GetTimestamp();
        long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        bool refreshWorkingSet = AiWorkingSetSamplingPolicy.ShouldRefresh(
            _lastWorkingSetReadUtc, now);
        if (refreshWorkingSet)
            _workingSetByPid.Clear();
        int nativeWorkingSetReads = 0, cachedWorkingSetReads = 0;
        // Match the active window against the Process[] already needed for game
        // detection and CPU deltas. Avoid an extra OpenProcess on normal ticks.
        string foregroundFromSnapshot = string.Empty;
        bool foregroundFound = false;
        bool foregroundCpuComparable = false;
        // CPU deltas use monotonic Stopwatch time; NTP/manual Windows clock changes
        // must not inflate process percentages or affect AI workload decisions.
        bool comparableCpuWindow = AiAdaptiveProcessScanPolicy.CpuWindowComparable(
            _previousProcessSampleTimestamp, sampleStart, Stopwatch.Frequency);
        double elapsed = AiAdaptiveProcessScanPolicy.CpuWindowSeconds(
            _previousProcessSampleTimestamp, sampleStart, Stopwatch.Frequency);
        int logical = Math.Max(1, Environment.ProcessorCount);
        _processCpuHistory.BeginCycle();
        _cpuDeniedCache.BeginCycle(now);
        int nativeCpuReadAttempts = 0;
        int comparableCpuCounters = 0;
        int invalidCpuCounterDeltas = 0;
        // PID/name cache is refreshed in full every fourth scan. Newly seen
        // PIDs and CPU-counter resets always fetch their name immediately.
        bool refreshProcessNames = (++_processNameRefreshCycle & 3) == 0;
        int reusedProcessNames = 0;
        int nativeProcessNames = 0;
        double totalCpuSeconds = 0;
        double topCpuSeconds = 0;
        double oneDriveCpuSeconds = 0;
        double dropboxCpuSeconds = 0;
        double epicCpuSeconds = 0;
        double qbtCpuSeconds = 0;
        double searchIndexerCpuSeconds = 0;
        double searchProtocolCpuSeconds = 0;
        double searchFilterCpuSeconds = 0;
        // Unknown CPU counter evidence must not masquerade as measured 0%.
        bool oneDriveCpuComparable = false, dropboxCpuComparable = false;
        bool epicCpuComparable = false;
        bool qbtCpuComparable = false;
        bool searchIndexerCpuComparable = false, searchProtocolCpuComparable = false;
        bool searchFilterCpuComparable = false;
        int oneDriveInstanceCount = 0;
        int dropboxInstanceCount = 0;
        int epicInstanceCount = 0;
        int epicPid = 0;
        int qbtInstanceCount = 0;
        int qbtPid = 0;
        int searchIndexerInstanceCount = 0;
        int searchProtocolInstanceCount = 0;
        int searchFilterInstanceCount = 0;
        int oneDrivePid = 0, dropboxPid = 0;
        int searchIndexerPid = 0, searchProtocolPid = 0, searchFilterPid = 0;
        // Capture existing Process wrappers without extra enumeration. Read
        // WorkingSet64 only AFTER unique-instance counts are known, BEFORE
        // their existing snapshot-wide DisposeAll() cleanup.
        Process? oneDriveMemoryTarget = null, dropboxMemoryTarget = null;
        Process? epicMemoryTarget = null;
        Process? qbtMemoryTarget = null;
        Process? searchIndexerMemoryTarget = null;
        Process? searchProtocolMemoryTarget = null, searchFilterMemoryTarget = null;
        bool oneDriveMemoryReusable = false, dropboxMemoryReusable = false;
        bool epicMemoryReusable = false;
        bool qbtMemoryReusable = false;
        bool searchIndexerMemoryReusable = false;
        bool searchProtocolMemoryReusable = false, searchFilterMemoryReusable = false;
        int duplicateWorkingSetCandidates = 0;
        double? oneDriveWorkingSetMiB = null, dropboxWorkingSetMiB = null;
        double? epicWorkingSetMiB = null;
        double? qbtWorkingSetMiB = null;
        double? searchIndexerWorkingSetMiB = null;
        double? searchProtocolWorkingSetMiB = null, searchFilterWorkingSetMiB = null;
        string topProcess = string.Empty;
        bool gameActive = false;

        // Process.GetProcesses returns native process wrappers for the entire
        // snapshot. Dispose ALL of them even if a read or future cancellation
        // interrupts iteration before the last entry is visited.
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    int processId = process.Id;
                    // A long sleep or invalid monotonic window needs a fresh
                    // baseline, not a fabricated process CPU delta. Avoid even
                    // looking up old PID/name state on this recovery scan.
                    AdaptiveProcessCpuSample previous = default;
                    bool previouslySeen = comparableCpuWindow &&
                        _processCpuHistory.TryGetPrevious(processId, out previous);
                    // Native CPU access is denied for many protected Windows
                    // processes. Resolve names FIRST so denied processes can
                    // still be classified as games and foreground immediately.
                    // For previously measured PIDs, reuse the name except
                    // on foreground/periodic refresh. CPU resets below force
                    // an immediate fresh name resolution (PID recycle).
                    bool reuseName = processId != foregroundPid && previouslySeen &&
                        !refreshProcessNames &&
                        !string.IsNullOrEmpty(previous.Name);
                    string name = reuseName ? previous.Name : process.ProcessName;
                    TimeSpan cpu = default;
                    bool cpuKnown = false;
                    if (!_cpuDeniedCache.ShouldSkip(processId, name, foregroundPid))
                    {
                        nativeCpuReadAttempts++;
                        try
                        {
                            cpu = process.TotalProcessorTime;
                            cpuKnown = true;
                        }
                        catch (System.ComponentModel.Win32Exception ex)
                        {
                            // Only access-denied(5) may be negatively cached.
                            // Other Win32 failures remain UNKNOWN, not zero.
                            if (ex.NativeErrorCode == 5)
                                _cpuDeniedCache.RecordDenied(processId, name, foregroundPid);
                        }
                        catch (InvalidOperationException) { /* exited process: unknown CPU */ }
                        catch (NotSupportedException) { /* unavailable CPU property */ }
                    }
                    // Preserve strict PID/name identity even when the native
                    // CPU read fails. ProcessName is much cheaper than denied
                    // CPU queries and remains readable for most system PIDs.
                    if (!cpuKnown && reuseName)
                    {
                        name = process.ProcessName;
                        reuseName = false;
                    }
                    // Preserve the old strict PID/CPU-counter identity logic:
                    // a decreasing CPU counter makes cached name unsafe.
                    if (cpuKnown && reuseName && cpu < previous.Cpu)
                    {
                        name = process.ProcessName;
                        reuseName = false;
                    }
                    bool canReuseMemory = AiProcessCpuEvidencePolicy.HasComparableCounter(
                        cpuKnown, previouslySeen, previous.Cpu, cpu, elapsed, logical);
                    if (canReuseMemory)
                        comparableCpuCounters++;
                    else if (cpuKnown && previouslySeen)
                        invalidCpuCounterDeltas++;
                    if (reuseName) reusedProcessNames++;
                    else nativeProcessNames++;
                    if (foregroundPid != 0 && processId == foregroundPid)
                    {
                        foregroundFromSnapshot = name;
                        foregroundFound = true;
                        foregroundCpuComparable = canReuseMemory;
                    }
                    if (AiKnownGameProcessPolicy.IsKnownGame(name))
                    {
                        gameActive = true;
                    }
                    if (cpuKnown)
                        _processCpuHistory.Remember(processId, cpu, name);
                    if (name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase))
                    {
                        oneDriveInstanceCount++;
                        oneDrivePid = processId;
                        oneDriveCpuComparable = canReuseMemory;
                        oneDriveMemoryTarget = oneDriveInstanceCount == 1 ? process : null;
                        oneDriveMemoryReusable = canReuseMemory;
                    }
                    else if (name.Equals("Dropbox", StringComparison.OrdinalIgnoreCase))
                    {
                        dropboxInstanceCount++;
                        dropboxPid = processId;
                        dropboxCpuComparable = canReuseMemory;
                        dropboxMemoryTarget = dropboxInstanceCount == 1 ? process : null;
                        dropboxMemoryReusable = canReuseMemory;
                    }
                    else if (name.Equals("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase))
                    {
                        epicInstanceCount++;
                        epicPid = processId;
                        epicCpuComparable = canReuseMemory;
                        epicMemoryTarget = epicInstanceCount == 1 ? process : null;
                        epicMemoryReusable = canReuseMemory;
                    }
                    else if (name.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase))
                    {
                        qbtInstanceCount++;
                        qbtPid = processId;
                        qbtCpuComparable = canReuseMemory;
                        qbtMemoryTarget = qbtInstanceCount == 1 ? process : null;
                        qbtMemoryReusable = canReuseMemory;
                    }
                    else if (name.Equals("SearchIndexer", StringComparison.OrdinalIgnoreCase))
                    {
                        searchIndexerInstanceCount++;
                        searchIndexerPid = processId;
                        searchIndexerCpuComparable = canReuseMemory;
                        searchIndexerMemoryTarget = searchIndexerInstanceCount == 1 ? process : null;
                        searchIndexerMemoryReusable = canReuseMemory;
                    }
                    else if (name.Equals("SearchProtocolHost", StringComparison.OrdinalIgnoreCase))
                    {
                        searchProtocolInstanceCount++;
                        searchProtocolPid = processId;
                        searchProtocolCpuComparable = canReuseMemory;
                        searchProtocolMemoryTarget = searchProtocolInstanceCount == 1 ? process : null;
                        searchProtocolMemoryReusable = canReuseMemory;
                    }
                    else if (name.Equals("SearchFilterHost", StringComparison.OrdinalIgnoreCase))
                    {
                        searchFilterInstanceCount++;
                        searchFilterPid = processId;
                        searchFilterCpuComparable = canReuseMemory;
                        searchFilterMemoryTarget = searchFilterInstanceCount == 1 ? process : null;
                        searchFilterMemoryReusable = canReuseMemory;
                    }

                    if (canReuseMemory)
                    {
                        double delta = (cpu - previous.Cpu).TotalSeconds;
                        if (delta > 0)
                        {
                            totalCpuSeconds += delta;
                            // Reuse this very same native CPU-delta snapshot.
                            // The target may cease to be the top CPU process
                            // *because* its scheduling priority was lowered.
                            if (name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase))
                                oneDriveCpuSeconds += delta;
                            else if (name.Equals("Dropbox", StringComparison.OrdinalIgnoreCase))
                                dropboxCpuSeconds += delta;
                            else if (name.Equals("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase))
                                epicCpuSeconds += delta;
                            else if (name.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase))
                                qbtCpuSeconds += delta;
                            else if (name.Equals("SearchIndexer", StringComparison.OrdinalIgnoreCase))
                                searchIndexerCpuSeconds += delta;
                            else if (name.Equals("SearchProtocolHost", StringComparison.OrdinalIgnoreCase))
                                searchProtocolCpuSeconds += delta;
                            else if (name.Equals("SearchFilterHost", StringComparison.OrdinalIgnoreCase))
                                searchFilterCpuSeconds += delta;
                            if (delta > topCpuSeconds)
                            {
                                topCpuSeconds = delta;
                                topProcess = name;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException or ArgumentException)
                {
                    // Processes can exit or deny access during enumeration.
                }
            }
            // No working-set query (not even cache access) for any name with
            // two or more instances: those samples are NEVER AI-eligible.
            duplicateWorkingSetCandidates =
                (oneDriveInstanceCount > 1 ? oneDriveInstanceCount : 0) +
                (dropboxInstanceCount > 1 ? dropboxInstanceCount : 0) +
                (epicInstanceCount > 1 ? epicInstanceCount : 0) +
                (qbtInstanceCount > 1 ? qbtInstanceCount : 0) +
                (searchIndexerInstanceCount > 1 ? searchIndexerInstanceCount : 0) +
                (searchProtocolInstanceCount > 1 ? searchProtocolInstanceCount : 0) +
                (searchFilterInstanceCount > 1 ? searchFilterInstanceCount : 0);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(oneDriveInstanceCount) &&
                oneDriveMemoryTarget is not null)
                oneDriveWorkingSetMiB = ReadCachedWorkingSetMiB(oneDriveMemoryTarget,
                    refreshWorkingSet, oneDriveMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(dropboxInstanceCount) &&
                dropboxMemoryTarget is not null)
                dropboxWorkingSetMiB = ReadCachedWorkingSetMiB(dropboxMemoryTarget,
                    refreshWorkingSet, dropboxMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(epicInstanceCount) &&
                epicMemoryTarget is not null)
                epicWorkingSetMiB = ReadCachedWorkingSetMiB(epicMemoryTarget,
                    refreshWorkingSet, epicMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(qbtInstanceCount) &&
                qbtMemoryTarget is not null)
                qbtWorkingSetMiB = ReadCachedWorkingSetMiB(qbtMemoryTarget,
                    refreshWorkingSet, qbtMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(searchIndexerInstanceCount) &&
                searchIndexerMemoryTarget is not null)
                searchIndexerWorkingSetMiB = ReadCachedWorkingSetMiB(searchIndexerMemoryTarget,
                    refreshWorkingSet, searchIndexerMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(searchProtocolInstanceCount) &&
                searchProtocolMemoryTarget is not null)
                searchProtocolWorkingSetMiB = ReadCachedWorkingSetMiB(searchProtocolMemoryTarget,
                    refreshWorkingSet, searchProtocolMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(searchFilterInstanceCount) &&
                searchFilterMemoryTarget is not null)
                searchFilterWorkingSetMiB = ReadCachedWorkingSetMiB(searchFilterMemoryTarget,
                    refreshWorkingSet, searchFilterMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
        }
        finally
        {
            ProcessSnapshotCleanup.DisposeAll(processes);
        }

        _processCpuHistory.EndCycle();
        if (refreshWorkingSet)
            _lastWorkingSetReadUtc = now;
        _previousProcessSampleTimestamp = sampleStart;

        double totalPercent = Math.Clamp(
            totalCpuSeconds / elapsed * 100d / logical,
            0d,
            100d);

        double topPercent = Math.Clamp(
            topCpuSeconds / elapsed * 100d / logical,
            0d,
            100d);

        string foregroundProcess = foregroundFound
            ? foregroundFromSnapshot
            : ForegroundProcessProbe.GetName();

        _cachedProcessSample = new ProcessLoadSample(
            totalPercent,
            topProcess,
            topPercent,
            foregroundProcess,
            gameActive,
            AiProcessCpuEvidencePolicy.Percent(
                oneDriveInstanceCount, oneDriveCpuComparable, oneDriveCpuSeconds, elapsed, logical),
            AiProcessCpuEvidencePolicy.Percent(
                dropboxInstanceCount, dropboxCpuComparable, dropboxCpuSeconds, elapsed, logical),
            oneDriveInstanceCount,
            dropboxInstanceCount,
            AiProcessCpuEvidencePolicy.Percent(
                searchIndexerInstanceCount, searchIndexerCpuComparable, searchIndexerCpuSeconds, elapsed, logical),
            AiProcessCpuEvidencePolicy.Percent(
                searchProtocolInstanceCount, searchProtocolCpuComparable, searchProtocolCpuSeconds, elapsed, logical),
            AiProcessCpuEvidencePolicy.Percent(
                searchFilterInstanceCount, searchFilterCpuComparable, searchFilterCpuSeconds, elapsed, logical),
            searchIndexerInstanceCount,
            searchProtocolInstanceCount,
            searchFilterInstanceCount,
            foregroundPid,
            oneDriveInstanceCount == 1 ? oneDrivePid : 0,
            dropboxInstanceCount == 1 ? dropboxPid : 0,
            searchIndexerInstanceCount == 1 ? searchIndexerPid : 0,
            searchProtocolInstanceCount == 1 ? searchProtocolPid : 0,
            searchFilterInstanceCount == 1 ? searchFilterPid : 0,
            oneDriveInstanceCount == 1 ? oneDriveWorkingSetMiB : null,
            dropboxInstanceCount == 1 ? dropboxWorkingSetMiB : null,
            searchIndexerInstanceCount == 1 ? searchIndexerWorkingSetMiB : null,
            searchProtocolInstanceCount == 1 ? searchProtocolWorkingSetMiB : null,
            searchFilterInstanceCount == 1 ? searchFilterWorkingSetMiB : null,
            _lastWorkingSetReadUtc,
            AiProcessCpuEvidencePolicy.HasRepresentativeForegroundCoverage(
                comparableCpuCounters, processes.Length, foregroundCpuComparable),
            AiProcessCpuEvidencePolicy.Percent(
                epicInstanceCount, epicCpuComparable, epicCpuSeconds, elapsed, logical),
            epicInstanceCount,
            epicInstanceCount == 1 ? epicPid : 0,
            epicInstanceCount == 1 ? epicWorkingSetMiB : null,
            AiProcessCpuEvidencePolicy.Percent(
                qbtInstanceCount, qbtCpuComparable, qbtCpuSeconds, elapsed, logical),
            qbtInstanceCount,
            qbtInstanceCount == 1 ? qbtPid : 0,
            qbtInstanceCount == 1 ? qbtWorkingSetMiB : null);
        // A baseline-only scan still updates process identities, but must not
        // authorize A/B/A trials, priority mutations, or repeated workload votes.
        _lastProcessSampleUtc = comparableCpuWindow && comparableCpuCounters > 0
            ? now : default;
        // Every successful native snapshot contributes once. Report one
        // bounded, allocation-free aggregate per minute; never add a timer.
        // Unlike a single arbitrary sample, maxMs reveals periodic stalls.
        double thisSampleMs = Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds;
        long thisSampleAllocatedB =
            GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart;
        _sampleMetrics.Add(thisSampleMs, thisSampleAllocatedB, processes.Length,
            nativeProcessNames, reusedProcessNames,
            nativeWorkingSetReads, cachedWorkingSetReads, refreshWorkingSet,
            duplicateWorkingSetCandidates,
            nativeCpuReadAttempts, _cpuDeniedCache.AccessDeniedThisCycle,
            _cpuDeniedCache.SuppressedThisCycle);
        if (metricDue)
        {
            _lastProcessSampleMetricUtc = now;
            AdaptiveProcessSampleMetricWindow.Snapshot minute =
                _sampleMetrics.TakeAndReset();
            int reusedSnapshots = _processSnapshotCacheReuses;
            _processSnapshotCacheReuses = 0;
            ownMetrics!.Refresh();
            long currentCpuTicks = ownMetrics.TotalProcessorTime.Ticks;
            double cpuDuringThisSampleMs = TimeSpan.FromTicks(
                Math.Max(0, currentCpuTicks - cpuBeforeTicks)).TotalMilliseconds;
            double cpuSinceLastMetricMs = _lastMetricProcessCpuTicks < 0
                ? -1
                : TimeSpan.FromTicks(
                    Math.Max(0, currentCpuTicks - _lastMetricProcessCpuTicks))
                    .TotalMilliseconds;
            _lastMetricProcessCpuTicks = currentCpuTicks;
            long processRamMiB = ownMetrics.WorkingSet64 / 1048576;
            int processHandles = ownMetrics.HandleCount;
            int processThreads = ownMetrics.Threads.Count;
            // Same already cached status, no extra service/file writes or
            // WinUI polling. Visible read-only metrics use the LAST minute
            // aggregate, never a fake single "average" sample.
            _lastSamplerSummary =
                $"Sběr procesů {minute.Scans}× (cache {reusedSnapshots}×): průměr {minute.MeanMs:0.##} ms, " +
                $"max {minute.MaxMs:0.##} ms; alokace průměr " +
                $"{minute.MeanAllocatedB / 1024:0.#} KiB, max " +
                $"{minute.MaxAllocatedB / 1024d:0.#} KiB; " +
                $"CPU procesu za interval " +
                $"{(cpuSinceLastMetricMs < 0 ? "—" : cpuSinceLastMetricMs.ToString("0.#") + " ms")}; " +
                $"RAM {processRamMiB} MiB, handly {processHandles}, vlákna {processThreads}; " +
                $"CPU okno {(comparableCpuWindow ? "ověřené" : "nový základ")}, " +
                $"pokrytí {( _cachedProcessSample.HasRepresentativeCpuCoverage ? "dostatečné" : "neúplné")}; " +
                $"CPU čtení {minute.NativeCpuReadAttempts}, " +
                $"odmítnuto {minute.CpuAccessDenied}, " +
                $"ušetřeno {minute.SuppressedDeniedCpuReads}";
            LogService.Info("performance",
                $"ADAPTIVE_PROCESS_SAMPLE scans={minute.Scans} reusedSnapshots={reusedSnapshots} " +
                $"avgMs={minute.MeanMs:0.###} minMs={minute.MinMs:0.###} " +
                $"maxMs={minute.MaxMs:0.###} ms={thisSampleMs:0.###} " +
                $"avgAllocatedB={minute.MeanAllocatedB:0.#} peakAllocatedB={minute.MaxAllocatedB} " +
                $"allocatedB={thisSampleAllocatedB} " +
                $"avgProcesses={minute.MeanProcesses:0.#} peakProcesses={minute.MaxProcesses} " +
                $"nameReuse={minute.ReusedNames} nativeNames={minute.NativeNameReads} " +
                $"workingSetNativeReads={minute.NativeWorkingSetReads} " +
                $"workingSetCacheHits={minute.WorkingSetCacheHits} " +
                $"workingSetRefreshes={minute.WorkingSetRefreshes} " +
                $"duplicateCandidatesSkipped={minute.DuplicateCandidatesSkipped} " +
                $"comparableCpuCounters={comparableCpuCounters} totalProcesses={processes.Length} " +
                $"foregroundCpuComparable={foregroundCpuComparable} " +
                $"representativeNativeCpu={_cachedProcessSample.HasRepresentativeCpuCoverage} " +
                $"invalidCpuDeltas={invalidCpuCounterDeltas} " +
                $"nativeCpuReads={minute.NativeCpuReadAttempts} " +
                $"nativeCpuAccessDenied={minute.CpuAccessDenied} " +
                $"nativeCpuDeniedSkipped={minute.SuppressedDeniedCpuReads} " +
                $"selfCpuDeltaMs={cpuDuringThisSampleMs:0.##} " +
                $"selfCpuIntervalMs={cpuSinceLastMetricMs:0.##} " +
                $"selfWorkingSetMiB={processRamMiB} " +
                $"selfHandles={processHandles} selfThreads={processThreads} " +
                $"foregroundSnapshot={foregroundFound} cpuWindowComparable={comparableCpuWindow} " +
                $"cpuWindowSeconds={elapsed:0.###} cpuPercent={totalPercent:0.0}");
        }
        return _cachedProcessSample;
    }
}
