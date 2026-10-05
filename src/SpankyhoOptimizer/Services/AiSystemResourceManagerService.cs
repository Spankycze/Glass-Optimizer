using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record AiSystemRuntimeConfig(
    bool Enabled = true,
    bool ServicesEnabled = true,
    bool ScheduledTasksEnabled = true,
    bool OptionalDevicesEnabled = true,
    bool CloudSyncEnabled = true,
    bool SelfOptimizationEnabled = true,
    int ObservationSeconds = 180,
    int ActionCooldownSeconds = 300,
    bool TweaksEnabled = true,
    bool PowerPlanEnabled = true,
    bool NetworkEnabled = true,
    bool SecurityEnabled = true);

public sealed record AiSystemRuntimeItemState(
    string Kind,
    string Id,
    bool Managed,
    bool Sleeping,
    string BlockedReason,
    DateTime? ObservationSinceUtc,
    DateTime? CooldownUntilUtc,
    int? OriginalServiceStart,
    bool? OriginalTaskEnabled,
    bool WasRunningBefore,
    int Rollbacks,
    string LastAction,
    DateTime? LastChangedUtc = null,
    double? BaselineLoadPercent = null,
    DateTime? VerificationStartedUtc = null,
    DateTime? VerificationLastSampleUtc = null,
    int VerificationSamples = 0,
    double VerificationLoadTotal = 0,
    bool PerformanceVerified = false,
    string VerificationPhase = "",
    double? CandidateLoadPercent = null,
    double? VerificationLoadMin = null,
    double? VerificationLoadMax = null,
    double? CandidateLoadMin = null,
    double? CandidateLoadMax = null);

public sealed record AiSystemActionHistoryEntry(
    DateTime TimestampUtc,
    string Kind,
    string Id,
    string Action,
    string Reason);

public sealed record AiSystemRuntimeHealth(
    DateTime UpdatedUtc,
    bool Enabled,
    bool ServicesEnabled,
    bool ScheduledTasksEnabled,
    bool OptionalDevicesEnabled,
    int Managed,
    int Sleeping,
    int Blocked,
    int Restored,
    int ServiceManaged,
    int TaskManaged,
    int DeviceManaged,
    string LastAction,
    string LastError,
    double LastLoadPercent,
    bool LastGameActive,
    bool LastRealIdle,
    double LastCycleMs = 0,
    double LastServiceScanMs = 0,
    double LastTaskScanMs = 0,
    double LastDriverScanMs = 0);

public sealed class AiSystemResourceManagerService
{
    private readonly record struct DriverRegistrySnapshot(
        bool Installed,
        bool WindowsOwned,
        int Type,
        int Start,
        bool AttachedDevice);

    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer");
    private static readonly string ConfigPath = Path.Combine(StateDirectory, "ai-system-runtime.json");
    private static readonly string HealthPath = Path.Combine(StateDirectory, "ai-system-runtime-health.json");
    private static readonly string ItemStatePath = Path.Combine(StateDirectory, "ai-system-runtime-items.json");
    private static readonly string ActionHistoryPath = Path.Combine(StateDirectory, "ai-system-actions.json");
    private static readonly object ActionHistoryGate = new();
    private static readonly object SnapshotGate = new();
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    private static AiSystemRuntimeConfig? CachedConfig;
    private static AiSystemRuntimeHealth? CachedHealth;
    private static AiSystemRuntimeItemState[]? CachedItems;
    private static AiSystemActionHistoryEntry[]? CachedActionHistory;
    private static DateTime CachedConfigWriteUtc = DateTime.MinValue;
    private static DateTime CachedHealthWriteUtc = DateTime.MinValue;
    private static DateTime CachedItemsWriteUtc = DateTime.MinValue;
    private static DateTime CachedActionHistoryWriteUtc = DateTime.MinValue;

    private static readonly string[] ServiceAllowlist =
    {
        "DiagTrack",
        "MapsBroker",
        "Fax",
        "RetailDemo",
        "RemoteRegistry",
        "WMPNetworkSvc",
        "diagnosticshub.standardcollector.service",
        "AJRouter",
        "Wecsvc",
        "WalletService",
        "WerSvc",
        "TrkWks",
        "wisvc",
        "dmwappushservice",
        "PhoneSvc",
        "SEMgrSvc",
        "SharedRealitySvc",
        "DPS",
        "WdiSystemHost",
        "WdiServiceHost"
    };

    private static readonly string[] DriverAllowlist =
    {
        "Beep",
        "flpydisk",
        "sfloppy",
        "parport",
        "parvdm",
        "Changer",
        "sermouse",
        "Serial"
    };

    private static readonly string[] TaskAllowlist =
    {
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
        @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
        @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
        @"\Microsoft\Windows\Feedback\Siuf\DmClient",
        @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
        @"\Microsoft\Windows\Maps\MapsToastTask",
        @"\Microsoft\Windows\Maps\MapsUpdateTask",
        @"\Microsoft\Windows\Application Experience\PcaPatchDbTask",
        @"\Microsoft\Windows\Application Experience\StartupAppTask",
        @"\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask",
        @"\Microsoft\Windows\Customer Experience Improvement Program\BthSQM",
        @"\Microsoft\Windows\Windows Error Reporting\QueueReporting",
        @"\Microsoft\Windows\PerformanceTrace\ShowFeedbackToast"
    };

    private static readonly HashSet<string> CriticalServiceDenylist = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs","DcomLaunch","RpcEptMapper","PlugPlay","Power","EventLog","Schedule",
        "Winmgmt","W32Time","Dhcp","Dnscache","NlaSvc","Netman","LanmanWorkstation",
        "LanmanServer","BFE","MpsSvc","WinDefend","WdNisSvc","SecurityHealthService",
        "CryptSvc","TrustedInstaller","wuauserv","UsoSvc","BITS","DoSvc","msiserver",
        "AudioSrv","AudioEndpointBuilder","MMCSS","StorSvc","stornvme","disk","partmgr",
        "USBHUB3","usbhub","HidServ","PawnIO"
    };

    private static readonly string[] CriticalServiceFragments =
    {
        "rpc","storage","stor","disk","nvme","usb","hid","network","defender",
        "security","firewall","update","installer","crypt","eventlog","audio"
    };

    private readonly Dictionary<string, AiSystemRuntimeItemState> _items =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastTickUtc = DateTime.MinValue;
    private string _lastAction = "none";
    private string _lastError = string.Empty;
    private int _restoredCount;
    private readonly AiSelfOptimizationService _selfOptimizer = new();
    private readonly AiTweakRuntimeService _tweakRuntime = new();
    private readonly AiNetworkRuntimeService _networkRuntime = new();
    private readonly AiSecurityHardeningService _securityRuntime = new();
    private readonly Dictionary<string, (DateTime ExpiresUtc, bool Value)> _boolProbeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private string _lastPersistJson = string.Empty;
    private string _lastHealthFingerprint = string.Empty;
    private DateTime _lastHealthWriteUtc = DateTime.MinValue;
    private DateTime _lastCloudSyncUtc = DateTime.MinValue;
    private DateTime _lastServiceDeepScanUtc = DateTime.MinValue;
    private DateTime _lastTaskDeepScanUtc = DateTime.MinValue;
    private DateTime _lastDriverDeepScanUtc = DateTime.MinValue;
    private double _lastCycleMs;
    private double _lastServiceScanMs;
    private double _lastTaskScanMs;
    private double _lastDriverScanMs;
    private readonly Queue<AiBackgroundLoadSample> _quietLoadSamples = new();
    private bool _maintenanceMutationCommitted;
    private int _maintenanceKindCursor;

    public AiSystemResourceManagerService()
    {
        LoadItems();
    }

    public static AiSystemRuntimeConfig LoadConfig()
    {
        lock (SnapshotGate)
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    AiSystemRuntimeConfig defaults = AiRuntimeScopePolicy.Sanitize(new AiSystemRuntimeConfig());
                    SaveConfig(defaults);
                    return defaults;
                }

                DateTime writeUtc = File.GetLastWriteTimeUtc(ConfigPath);
                if (CachedConfig != null && writeUtc == CachedConfigWriteUtc)
                    return AiRuntimeScopePolicy.Sanitize(CachedConfig);

                AiSystemRuntimeConfig loaded =
                    JsonSerializer.Deserialize<AiSystemRuntimeConfig>(File.ReadAllText(ConfigPath))
                    ?? new AiSystemRuntimeConfig();
                AiSystemRuntimeConfig sanitized = AiRuntimeScopePolicy.Sanitize(loaded);
                if (!Equals(loaded, sanitized))
                {
                    SaveConfig(sanitized);
                    return sanitized;
                }

                CachedConfig = sanitized;
                CachedConfigWriteUtc = writeUtc;
                return sanitized;
            }
            catch
            {
                return AiRuntimeScopePolicy.Sanitize(
                    CachedConfig ?? new AiSystemRuntimeConfig());
            }
        }
    }

    public static void SaveConfig(AiSystemRuntimeConfig config)
    {
        AiSystemRuntimeConfig sanitized = AiRuntimeScopePolicy.Sanitize(config);
        Directory.CreateDirectory(StateDirectory);
        string temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(sanitized, IndentedJsonOptions));
        File.Move(temp, ConfigPath, true);
        lock (SnapshotGate)
        {
            CachedConfig = sanitized;
            CachedConfigWriteUtc = File.GetLastWriteTimeUtc(ConfigPath);
        }
    }

    public static AiSystemRuntimeHealth? ReadHealth()
    {
        lock (SnapshotGate)
        {
            try
            {
                DateTime writeUtc = File.Exists(HealthPath)
                    ? File.GetLastWriteTimeUtc(HealthPath)
                    : DateTime.MinValue;
                if (CachedHealth != null && writeUtc == CachedHealthWriteUtc)
                    return CachedHealth;

                CachedHealth = writeUtc != DateTime.MinValue
                    ? JsonSerializer.Deserialize<AiSystemRuntimeHealth>(File.ReadAllText(HealthPath))
                    : null;
                CachedHealthWriteUtc = writeUtc;
                return CachedHealth;
            }
            catch { return CachedHealth; }
        }
    }

    public static IReadOnlyList<AiSystemRuntimeItemState> ReadItems()
    {
        lock (SnapshotGate)
        {
            try
            {
                DateTime writeUtc = File.Exists(ItemStatePath)
                    ? File.GetLastWriteTimeUtc(ItemStatePath)
                    : DateTime.MinValue;
                if (CachedItems != null && writeUtc == CachedItemsWriteUtc)
                    return CachedItems;

                CachedItems = writeUtc != DateTime.MinValue
                    ? JsonSerializer.Deserialize<AiSystemRuntimeItemState[]>(File.ReadAllText(ItemStatePath))
                      ?? Array.Empty<AiSystemRuntimeItemState>()
                    : Array.Empty<AiSystemRuntimeItemState>();
                CachedItemsWriteUtc = writeUtc;
                return CachedItems;
            }
            catch
            {
                return CachedItems ?? Array.Empty<AiSystemRuntimeItemState>();
            }
        }
    }

    public static IReadOnlyList<AiSystemActionHistoryEntry> ReadActionHistory()
    {
        lock (ActionHistoryGate)
        {
            try
            {
                DateTime writeUtc = File.Exists(ActionHistoryPath)
                    ? File.GetLastWriteTimeUtc(ActionHistoryPath)
                    : DateTime.MinValue;
                if (CachedActionHistory != null && writeUtc == CachedActionHistoryWriteUtc)
                    return CachedActionHistory;

                CachedActionHistory = writeUtc != DateTime.MinValue
                    ? JsonSerializer.Deserialize<AiSystemActionHistoryEntry[]>(File.ReadAllText(ActionHistoryPath))
                      ?? Array.Empty<AiSystemActionHistoryEntry>()
                    : Array.Empty<AiSystemActionHistoryEntry>();
                CachedActionHistoryWriteUtc = writeUtc;
                return CachedActionHistory;
            }
            catch
            {
                return CachedActionHistory ?? Array.Empty<AiSystemActionHistoryEntry>();
            }
        }
    }

    public async Task TickAsync(
        bool realIdle,
        double loadPercent,
        CancellationToken token)
    {
        AiSystemRuntimeConfig config = AiRuntimeScopePolicy.Sanitize(LoadConfig());
        DateTime now = DateTime.UtcNow;
        AdaptivePowerRuntimeStatus? powerStatus = AdaptivePowerRuntimeService.ReadStatus();
        bool powerStatusFresh = AdaptivePowerRuntimeService.IsStatusFresh(powerStatus, now);
        bool gameActive = powerStatusFresh && powerStatus?.GameActive == true;
        AiWorkloadClass workload = AiSharedWorkloadPolicy.Resolve(
            powerStatus,
            realIdle,
            loadPercent,
            now);

        bool maintenanceCompatible =
            AiSharedWorkloadPolicy.AllowsBackgroundAutomation(workload);
        bool comparableQuietSample = maintenanceCompatible &&
            AiBackgroundPerformancePolicy.IsComparableQuietSample(gameActive, realIdle, loadPercent);

        AiBackgroundPerformancePolicy.AddSample(_quietLoadSamples, comparableQuietSample, loadPercent, now);
        _tweakRuntime.QueueTick(config, realIdle, loadPercent, gameActive, token);
        _networkRuntime.QueueTick(config.Enabled && config.NetworkEnabled, realIdle, loadPercent, gameActive, workload, token);
        if (config.Enabled && AiPerformanceExperimentCoordinator.IsActive)
            return;
        int maintenanceIntervalSeconds = _selfOptimizer.Sample(config.SelfOptimizationEnabled, workload);
        if (_lastTickUtc != DateTime.MinValue &&
            now - _lastTickUtc < TimeSpan.FromSeconds(maintenanceIntervalSeconds))
            return;
        bool maintenanceLease = config.Enabled;
        if (maintenanceLease && !AiPerformanceExperimentCoordinator.TryAcquire("maintenance"))
            return;
        _lastTickUtc = now;
        long cycleStarted = Stopwatch.GetTimestamp();
        try
        {
            if (!config.Enabled)
            {
                await RestoreAllAsync(token, includePerformanceTrials: false).ConfigureAwait(false);
                WriteHealth(config, loadPercent, false, realIdle);
                return;
            }

            if (config.SecurityEnabled)
                await _securityRuntime.TickAsync(true, token).ConfigureAwait(false);
            else
                await _securityRuntime.RestoreAllAsync(token).ConfigureAwait(false);

            bool hadPendingFeedback = await EvaluatePendingMaintenanceFeedbackAsync(
                comparableQuietSample,
                loadPercent,
                now,
                token).ConfigureAwait(false);

            bool safeMaintenanceWindow = !hadPendingFeedback &&
                comparableQuietSample &&
                AiBackgroundPerformancePolicy.HasStableMeasuredPressure(_quietLoadSamples);
            _maintenanceMutationCommitted = hadPendingFeedback;

            bool serviceFast = KindNeedsFastCheck("service");
            bool taskFast = KindNeedsFastCheck("task");
            bool driverFast = KindNeedsFastCheck("driver");

            async Task ScanServicesScopeAsync()
            {
                if (!config.ServicesEnabled)
                {
                    await RestoreKindAsync("service", token).ConfigureAwait(false);
                    return;
                }

                TimeSpan interval = AdaptiveScanInterval(TimeSpan.FromMinutes(2), _lastServiceScanMs);
                bool deepScan = safeMaintenanceWindow &&
                    ShouldRunDeepScan(ref _lastServiceDeepScanUtc, now, interval);
                if (!serviceFast && !deepScan) return;

                long started = Stopwatch.GetTimestamp();
                await EvaluateServicesAsync(config, quietEnough: deepScan, token).ConfigureAwait(false);
                _lastServiceScanMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            async Task ScanTasksScopeAsync()
            {
                if (!config.ScheduledTasksEnabled)
                {
                    await RestoreKindAsync("task", token).ConfigureAwait(false);
                    return;
                }

                TimeSpan interval = AdaptiveScanInterval(TimeSpan.FromMinutes(3), _lastTaskScanMs);
                bool deepScan = safeMaintenanceWindow &&
                    ShouldRunDeepScan(ref _lastTaskDeepScanUtc, now, interval);
                if (!taskFast && !deepScan) return;

                long started = Stopwatch.GetTimestamp();
                await EvaluateTasksAsync(config, quietEnough: deepScan, token).ConfigureAwait(false);
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
                bool deepScan = safeMaintenanceWindow &&
                    ShouldRunDeepScan(ref _lastDriverDeepScanUtc, now, interval);
                if (!driverFast && !deepScan) return;

                long started = Stopwatch.GetTimestamp();
                EvaluateSafeDrivers(config, quietEnough: deepScan);
                _lastDriverScanMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            foreach (string kind in AiMaintenanceSchedulingPolicy.OrderForCycle(_maintenanceKindCursor++))
            {
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
                (stateChanged || _lastCloudSyncUtc == DateTime.MinValue ||
                 DateTime.UtcNow - _lastCloudSyncUtc >= TimeSpan.FromMinutes(15)))
            {
                AiCloudProfileService.SyncBestEffort(config, _items.Values);
                _lastCloudSyncUtc = DateTime.UtcNow;
            }
            _lastCycleMs = Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
            WriteHealth(config, loadPercent, false, realIdle);
        }
        catch (Exception ex)
        {
            _lastCycleMs = Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
            _lastError = ex.GetBaseException().Message;
            LogService.Error("ai-system", "AI System Runtime tick failed", ex);
            WriteHealth(config, loadPercent, false, realIdle);
        }
        finally
        {
            if (maintenanceLease) AiPerformanceExperimentCoordinator.Release("maintenance");
        }
    }

    public static async Task RestoreAllNowAsync(CancellationToken token = default)
    {
        var service = new AiSystemResourceManagerService();
        await service.RestoreAllAsync(token).ConfigureAwait(false);
        service.PersistItems();
        service.WriteHealth(LoadConfig(), 0, false, false);
    }

    public static int ResetLearnedProtection()
    {
        var service = new AiSystemResourceManagerService();
        int changed = 0;
        foreach (var pair in service._items.ToArray())
        {
            AiSystemRuntimeItemState state = pair.Value;
            if (state.Rollbacks < 2 && !state.BlockedReason.Contains("learned protection", StringComparison.OrdinalIgnoreCase))
                continue;

            service._items[pair.Key] = state with
            {
                Rollbacks = 0,
                BlockedReason = string.Empty,
                CooldownUntilUtc = null,
                ObservationSinceUtc = null,
                LastAction = "learned protection reset",
                LastChangedUtc = DateTime.UtcNow
            };
            changed++;
        }

        if (changed > 0)
        {
            service.PersistItems();
            AppendAction("system", "learned-protection", "reset", $"{changed} položek");
        }
        return changed;
    }

    public static int ImportCloudMemory(IEnumerable<AiSystemRuntimeItemState> cloudItems)
    {
        var service = new AiSystemResourceManagerService();
        int imported = 0;

        foreach (AiSystemRuntimeItemState row in cloudItems)
        {
            bool allowed =
                row.Kind.Equals("service", StringComparison.OrdinalIgnoreCase)
                    ? ServiceAllowlist.Contains(row.Id, StringComparer.OrdinalIgnoreCase) && !IsCriticalService(row.Id)
                    : row.Kind.Equals("task", StringComparison.OrdinalIgnoreCase)
                        ? TaskAllowlist.Contains(row.Id, StringComparer.OrdinalIgnoreCase)
                        : row.Kind.Equals("driver", StringComparison.OrdinalIgnoreCase) &&
                          DriverAllowlist.Contains(row.Id, StringComparer.OrdinalIgnoreCase);
            if (!allowed) continue;

            string previous = string.IsNullOrWhiteSpace(row.LastAction) ? "none" : row.LastAction;
            service._items[Key(row.Kind, row.Id)] = row with
            {
                Managed = false,
                Sleeping = false,
                BlockedReason = string.Empty,
                ObservationSinceUtc = DateTime.UtcNow,
                CooldownUntilUtc = null,
                LastAction = "cloud memory • previous: " + previous,
                LastChangedUtc = DateTime.UtcNow
            };
            imported++;
        }

        service.PersistItems();
        return imported;
    }

    private async Task<bool> EvaluatePendingMaintenanceFeedbackAsync(
        bool comparableQuietSample,
        double loadPercent,
        DateTime nowUtc,
        CancellationToken token)
    {
        AiSystemRuntimeItemState[] pending = _items.Values
            .Where(x => x.Managed &&
                        x.VerificationStartedUtc != null &&
                        (x.Kind.Equals("service", StringComparison.OrdinalIgnoreCase) ||
                         x.Kind.Equals("task", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.VerificationStartedUtc)
            .ToArray();

        if (pending.Length == 0)
            return false;

        foreach (AiSystemRuntimeItemState state in pending)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (state.VerificationStartedUtc is DateTime started &&
                    nowUtc - started > AiMaintenanceFeedbackPolicy.MaximumVerificationDuration)
                {
                    await RestoreMaintenanceTrialAsync(
                        state,
                        "verification timeout / inconclusive",
                        token).ConfigureAwait(false);
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

                AiSystemRuntimeItemState sampled = state with
                {
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

                    if (!candidateVerdict.Accepted)
                    {
                        await RestoreMaintenanceTrialAsync(sampled, candidateVerdict.Reason, token).ConfigureAwait(false);
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

                if (verdict.Accepted)
                {
                    await ApplyMaintenanceCandidateStateAsync(sampled, token).ConfigureAwait(false);
                    _items[Key(state.Kind, state.Id)] = sampled with
                    {
                        Sleeping = true,
                        BaselineLoadPercent = null,
                        VerificationStartedUtc = null,
                        VerificationLastSampleUtc = null,
                        VerificationSamples = 0,
                        VerificationLoadTotal = 0,
                        PerformanceVerified = true,
                        VerificationPhase = string.Empty,
                        CandidateLoadPercent = null,
                        VerificationLoadMin = null,
                        VerificationLoadMax = null,
                        CandidateLoadMin = null,
                        CandidateLoadMax = null,
                        LastAction = "A/B/A verified: " + verdict.Reason,
                        LastChangedUtc = nowUtc
                    };
                    _lastAction = $"{state.Kind} {state.Id}: A/B/A benefit confirmed";
                    AppendAction(state.Kind, state.Id, "verified A/B/A", verdict.Reason);
                    LogService.Info("ai-system",
                        $"AI_SYSTEM_FEEDBACK_ACCEPT_ABA kind={state.Kind} id={state.Id} improvement={verdict.ImprovementPercent:0.0}%");
                }
                else
                {
                    await RestoreMaintenanceTrialAsync(sampled, verdict.Reason, token).ConfigureAwait(false);
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

    private async Task RestoreMaintenanceReferenceStateAsync(
        AiSystemRuntimeItemState state,
        CancellationToken token)
    {
        if (state.Kind.Equals("service", StringComparison.OrdinalIgnoreCase))
        {
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
            if (state.WasRunningBefore && controller.Status != ServiceControllerStatus.Running)
                throw new InvalidOperationException("Reference A service state did not recover.");
        }
        else if (state.Kind.Equals("task", StringComparison.OrdinalIgnoreCase) &&
                 state.OriginalTaskEnabled == true)
        {
            TaskSchedulerComService.SetEnabled(state.Id, true, token);
        }

        await Task.CompletedTask;
    }

    private async Task ApplyMaintenanceCandidateStateAsync(
        AiSystemRuntimeItemState state,
        CancellationToken token)
    {
        if (state.Kind.Equals("service", StringComparison.OrdinalIgnoreCase))
        {
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
        }
        else if (state.Kind.Equals("task", StringComparison.OrdinalIgnoreCase))
        {
            TaskSchedulerComService.SetEnabled(state.Id, false, token);
        }

        await Task.CompletedTask;
    }

    private async Task RestoreMaintenanceTrialAsync(
        AiSystemRuntimeItemState state,
        string reason,
        CancellationToken token)
    {
        if (state.Kind.Equals("service", StringComparison.OrdinalIgnoreCase))
        {
            using var controller = new ServiceController(state.Id);
            if (state.OriginalServiceStart is int originalStart)
                WriteServiceStartType(state.Id, originalStart);

            controller.Refresh();
            if (state.WasRunningBefore && controller.Status == ServiceControllerStatus.Stopped)
            {
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
            }
        }
        else if (state.Kind.Equals("task", StringComparison.OrdinalIgnoreCase))
        {
            if (state.OriginalTaskEnabled == true)
                TaskSchedulerComService.SetEnabled(state.Id, true, token);
        }

        _items[Key(state.Kind, state.Id)] = state with
        {
            Managed = false,
            Sleeping = false,
            BlockedReason = "no measured performance benefit",
            CooldownUntilUtc = DateTime.UtcNow.AddHours(12),
            LastAction = "trial rolled back: " + reason,
            LastChangedUtc = DateTime.UtcNow,
            BaselineLoadPercent = null,
            VerificationStartedUtc = null,
            VerificationLastSampleUtc = null,
            VerificationSamples = 0,
            VerificationLoadTotal = 0,
            PerformanceVerified = false,
            VerificationPhase = string.Empty,
            CandidateLoadPercent = null,
            VerificationLoadMin = null,
            VerificationLoadMax = null,
            CandidateLoadMin = null,
            CandidateLoadMax = null
        };
        _restoredCount++;
        _lastAction = $"{state.Kind} {state.Id}: no measured benefit -> restored";
        AppendAction(state.Kind, state.Id, "trial rollback", reason);
        LogService.Info("ai-system",
            $"AI_SYSTEM_FEEDBACK_REJECT kind={state.Kind} id={state.Id} reason={reason}");
        await Task.CompletedTask;
    }

    private async Task EvaluateServicesAsync(
        AiSystemRuntimeConfig config,
        bool quietEnough,
        CancellationToken token)
    {
        IReadOnlyDictionary<string, ServiceControllerStatus> serviceStates = GetServiceStatusSnapshot();

        foreach (string name in ServiceAllowlist)
        {
            token.ThrowIfCancellationRequested();
            if (IsCriticalService(name))
            {
                SetBlocked("service", name, "critical denylist");
                continue;
            }

            AiSystemRuntimeItemState state = GetState("service", name);
            if (state.CooldownUntilUtc is DateTime cooldown && cooldown > DateTime.UtcNow)
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
                    SetBlocked("service", name, "not installed");
                    continue;
                }

                if (!state.Managed && status != ServiceControllerStatus.Running)
                {
                    ResetObservation("service", name);
                    continue;
                }

                if (!IsWindowsOwnedServiceEntry(name))
                {
                    SetBlocked("service", name, "není potvrzená Windows/System32 komponenta");
                    continue;
                }

                using var controller = new ServiceController(name);

                if (state.Managed && state.Sleeping && status == ServiceControllerStatus.Running)
                {
                    await RestoreServiceAsync(state, controller, "service needed again", token).ConfigureAwait(false);
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

                DateTime observed = state.ObservationSinceUtc ?? DateTime.UtcNow;
                state = state with { ObservationSinceUtc = observed, BlockedReason = string.Empty };
                _items[Key("service", name)] = state;
                if (DateTime.UtcNow - observed < TimeSpan.FromSeconds(config.ObservationSeconds))
                    continue;
                if (_maintenanceMutationCommitted)
                    continue;

                double baselineLoad = AiBackgroundPerformancePolicy.MedianLoad(_quietLoadSamples);
                if (!double.IsFinite(baselineLoad) || baselineLoad < 0.75)
                {
                    SetBlocked("service", name, "baseline výkonu není dostatečně stabilní");
                    continue;
                }

                int originalStart = ReadServiceStartType(name);
                bool wasRunning = status == ServiceControllerStatus.Running;

                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                if (controller.Status != ServiceControllerStatus.Stopped)
                    throw new InvalidOperationException($"{name} nepotvrdila Stop.");

                if (originalStart == 2)
                    WriteServiceStartType(name, 3);

                _items[Key("service", name)] = state with
                {
                    Managed = true,
                    Sleeping = true,
                    OriginalServiceStart = originalStart,
                    WasRunningBefore = wasRunning,
                    ObservationSinceUtc = null,
                    CooldownUntilUtc = DateTime.UtcNow.AddSeconds(config.ActionCooldownSeconds),
                    LastAction = "candidate trial: waiting for measured feedback",
                    LastChangedUtc = DateTime.UtcNow,
                    BaselineLoadPercent = baselineLoad,
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
                _quietLoadSamples.Clear();
                _maintenanceMutationCommitted = true;
                _lastAction = $"Service {name}: measured background-pressure trial";
                AppendAction("service", name, "stopped", $"measured background pressure; originalStart={originalStart}");
                LogService.Info("ai-system", $"AI_SYSTEM_SERVICE_SLEEP name={name} originalStart={originalStart} measuredPressure=true");
                return;
            }
            catch (Exception ex)
            {
                SetBlocked("service", name, ex.GetBaseException().Message);
            }
        }
    }

    private async Task EvaluateTasksAsync(
        AiSystemRuntimeConfig config,
        bool quietEnough,
        CancellationToken token)
    {
        DateTime nowUtc = DateTime.UtcNow;
        var candidates = new List<string>(TaskAllowlist.Length);
        foreach (string path in TaskAllowlist)
        {
            AiSystemRuntimeItemState state = GetState("task", path);
            if (state.CooldownUntilUtc is DateTime cooldown && cooldown > nowUtc)
                continue;
            if (!state.Managed && state.Rollbacks >= 2)
                continue;
            candidates.Add(path);
        }

        if (candidates.Count == 0)
            return;

        IReadOnlyDictionary<string, TaskRuntimeState> taskStates;
        try
        {
            taskStates = TaskSchedulerComService.GetRuntimeStates(candidates, token);
        }
        catch (Exception ex)
        {
            _lastError = "Task Scheduler batch snapshot: " + ex.GetBaseException().Message;
            LogService.Error("ai-system", "AI System Runtime task batch snapshot failed", ex);
            return;
        }

        foreach (string path in TaskAllowlist)
        {
            token.ThrowIfCancellationRequested();
            if (!path.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase))
            {
                SetBlocked("task", path, "není Windows systémový task");
                continue;
            }

            AiSystemRuntimeItemState state = GetState("task", path);
            if (state.CooldownUntilUtc is DateTime cooldown && cooldown > DateTime.UtcNow)
                continue;
            if (!state.Managed && state.Rollbacks >= 2)
            {
                SetBlocked("task", path, "AI learned protection • opakovaně obnoveno systémem");
                continue;
            }

            if (!taskStates.TryGetValue(path, out TaskRuntimeState taskState))
            {
                SetBlocked("task", path, "not installed/unreadable");
                continue;
            }
            bool enabled = taskState.Enabled;

            if (state.Managed && state.Sleeping && enabled)
            {
                int rollbacks = state.Rollbacks + 1;
                _items[Key("task", path)] = state with
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

            if (!quietEnough || !enabled)
            {
                ResetObservation("task", path);
                continue;
            }

            if (taskState.Running)
            {
                SetBlocked("task", path, "task právě běží");
                continue;
            }

            if (taskState.LastRunUtc is DateTime lastRun &&
                lastRun <= nowUtc &&
                nowUtc - lastRun <= TimeSpan.FromHours(2))
            {
                SetBlocked("task", path, "task byl aktivní v posledních 2 hodinách");
                continue;
            }

            DateTime? nextRunUtc = taskState.NextRunUtc;
            if (nextRunUtc is DateTime nextRun &&
                nextRun > nowUtc &&
                nextRun - nowUtc <= TimeSpan.FromMinutes(30))
            {
                SetBlocked("task", path, "blízký trigger / další spuštění do 30 minut");
                continue;
            }

            DateTime observed = state.ObservationSinceUtc ?? nowUtc;
            state = state with { ObservationSinceUtc = observed, BlockedReason = string.Empty };
            _items[Key("task", path)] = state;
            if (DateTime.UtcNow - observed < TimeSpan.FromSeconds(config.ObservationSeconds))
                continue;
            if (_maintenanceMutationCommitted)
                continue;

            double baselineLoad = AiBackgroundPerformancePolicy.MedianLoad(_quietLoadSamples);
            if (!double.IsFinite(baselineLoad) || baselineLoad < 0.75)
            {
                SetBlocked("task", path, "baseline výkonu není dostatečně stabilní");
                continue;
            }

            TaskSchedulerComService.SetEnabled(path, false, token);

            _items[Key("task", path)] = state with
            {
                Managed = true,
                Sleeping = true,
                OriginalTaskEnabled = true,
                ObservationSinceUtc = null,
                CooldownUntilUtc = DateTime.UtcNow.AddSeconds(config.ActionCooldownSeconds),
                LastAction = "candidate trial: waiting for measured feedback",
                LastChangedUtc = DateTime.UtcNow,
                BaselineLoadPercent = baselineLoad,
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
        {
            await _tweakRuntime.RestoreAllAsync(token).ConfigureAwait(false);
            await _networkRuntime.RestoreAllAsync(token).ConfigureAwait(false);
        }
        await _securityRuntime.RestoreAllAsync(token).ConfigureAwait(false);
        await AdaptivePowerRuntimeService.RestoreAllAiSettingsAsync(token).ConfigureAwait(false);
        await RestoreKindAsync("service", token).ConfigureAwait(false);
        await RestoreKindAsync("task", token).ConfigureAwait(false);
        await RestoreKindAsync("driver", token).ConfigureAwait(false);
        _lastAction = "restore all";
    }

    private async Task RestoreKindAsync(string kind, CancellationToken token)
    {
        foreach (var pair in _items.Where(x =>
                     x.Value.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
                     x.Value.Managed).ToArray())
        {
            token.ThrowIfCancellationRequested();
            var state = pair.Value;
            try
            {
                if (kind == "service")
                {
                    using var controller = new ServiceController(state.Id);
                    await RestoreServiceAsync(state, controller, "restore all", token).ConfigureAwait(false);
                }
                else if (kind == "task")
                {
                    if (state.OriginalTaskEnabled == true)
                        TaskSchedulerComService.SetEnabled(state.Id, true, token);
                    _items[pair.Key] = state with
                    {
                        Managed = false,
                        Sleeping = false,
                        LastAction = "restored",
                        CooldownUntilUtc = DateTime.UtcNow.AddMinutes(10),
                        LastChangedUtc = DateTime.UtcNow
                    };
                    _restoredCount++;
                    AppendAction("task", state.Id, "restored", "restore all");
                }
                else if (kind == "driver")
                {
                    if (state.OriginalServiceStart is int originalStart)
                        WriteServiceStartType(state.Id, originalStart);
                    _items[pair.Key] = state with
                    {
                        Managed = false,
                        Sleeping = false,
                        LastAction = "driver start restored",
                        CooldownUntilUtc = DateTime.UtcNow.AddMinutes(10),
                        LastChangedUtc = DateTime.UtcNow
                    };
                    _restoredCount++;
                    AppendAction("driver", state.Id, "restored", "restore all");
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.GetBaseException().Message;
            }
        }
    }

    private async Task RestoreServiceAsync(
        AiSystemRuntimeItemState state,
        ServiceController controller,
        string reason,
        CancellationToken token)
    {
        if (state.OriginalServiceStart is int originalStart)
            WriteServiceStartType(state.Id, originalStart);

        if (state.WasRunningBefore && controller.Status == ServiceControllerStatus.Stopped)
        {
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
        }

        int rollbacks = state.Rollbacks + 1;
        _items[Key("service", state.Id)] = state with
        {
            Managed = false,
            Sleeping = false,
            Rollbacks = rollbacks,
            BlockedReason = rollbacks >= 2 ? "opakovaně obnoveno systémem / pravděpodobně potřebné" : string.Empty,
            CooldownUntilUtc = DateTime.UtcNow.Add(rollbacks >= 2 ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(30)),
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

    private static bool IsCriticalService(string name)
    {
        if (CriticalServiceDenylist.Contains(name)) return true;
        string lower = name.ToLowerInvariant();
        return CriticalServiceFragments.Any(lower.Contains);
    }

    private static IReadOnlyDictionary<string, ServiceControllerStatus> GetServiceStatusSnapshot()
    {
        var result = new Dictionary<string, ServiceControllerStatus>(StringComparer.OrdinalIgnoreCase);
        ServiceController[] controllers = Array.Empty<ServiceController>();
        try
        {
            controllers = ServiceController.GetServices();
            foreach (ServiceController controller in controllers)
            {
                try
                {
                    result[controller.ServiceName] = controller.Status;
                }
                catch
                {
                }
            }
            return result;
        }
        finally
        {
            foreach (ServiceController controller in controllers)
                controller.Dispose();
        }
    }

    private bool HasRunningDependents(ServiceController controller, string serviceName)
    {
        string cacheKey = "service-dependents|" + serviceName;
        if (_boolProbeCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return cached.Value;

        bool running;
        try
        {
            running = controller.DependentServices.Any(x =>
            {
                try { return x.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending; }
                catch { return true; }
            });
        }
        catch
        {
            running = true;
        }

        _boolProbeCache[cacheKey] = (DateTime.UtcNow.AddSeconds(30), running);
        return running;
    }

    private static DriverRegistrySnapshot ReadDriverRegistrySnapshot(string name)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name);
            if (key == null)
                return new(false, false, 0, 3, false);

            string imagePath = Convert.ToString(key.GetValue("ImagePath", string.Empty))?.Trim() ?? string.Empty;
            string expanded = Environment.ExpandEnvironmentVariables(imagePath)
                .Trim('"', ' ')
                .Replace('/', '\\');
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
            bool windowsOwned = string.IsNullOrWhiteSpace(imagePath) ||
                                expanded.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase) ||
                                expanded.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) ||
                                expanded.StartsWith(windows + @"\", StringComparison.OrdinalIgnoreCase);

            int type = Convert.ToInt32(key.GetValue("Type", 0));
            int start = Convert.ToInt32(key.GetValue("Start", 3));
            using RegistryKey? enumKey = key.OpenSubKey("Enum");
            bool attached = Convert.ToInt32(enumKey?.GetValue("Count", 0) ?? 0) > 0;
            return new(true, windowsOwned, type, start, attached);
        }
        catch
        {
            return new(false, false, 0, 3, true);
        }
    }

    private bool IsWindowsOwnedServiceEntry(string name)
    {
        string cacheKey = "windows-owned|" + name;
        if (_boolProbeCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return cached.Value;

        bool owned = false;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name);
            if (key != null)
            {
                string imagePath = Convert.ToString(key.GetValue("ImagePath", string.Empty))?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(imagePath))
                {
                    owned = DriverAllowlist.Contains(name, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    string expanded = Environment.ExpandEnvironmentVariables(imagePath)
                        .Trim('"', ' ')
                        .Replace('/', '\\');
                    string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                        .TrimEnd('\\');

                    owned = expanded.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase) ||
                            expanded.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) ||
                            expanded.StartsWith(windows + @"\", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            owned = false;
        }

        _boolProbeCache[cacheKey] = (DateTime.UtcNow.AddMinutes(10), owned);
        return owned;
    }

    private static int ReadServiceStartType(string name)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + name);
        return Convert.ToInt32(key?.GetValue("Start", 3) ?? 3);
    }

    private static void WriteServiceStartType(string name, int value)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + name, writable: true)
            ?? throw new InvalidOperationException("Nelze otevĹ™Ă­t konfiguraci sluĹľby " + name);
        key.SetValue("Start", value, RegistryValueKind.DWord);
        int readback = Convert.ToInt32(key.GetValue("Start", -1));
        if (readback != value)
            throw new InvalidOperationException($"{name} nepotvrdil Start={value}.");
    }

    private void EvaluateSafeDrivers(AiSystemRuntimeConfig config, bool quietEnough)
    {
        foreach (string name in DriverAllowlist)
        {
            AiSystemRuntimeItemState state = GetState("driver", name);
            if (state.CooldownUntilUtc is DateTime cooldown && cooldown > DateTime.UtcNow)
                continue;
            if (!state.Managed && state.Rollbacks >= 2)
            {
                SetBlocked("driver", name, "AI learned protection • opakovaně obnoveno systémem");
                continue;
            }

            try
            {
                DriverRegistrySnapshot driver = ReadDriverRegistrySnapshot(name);
                if (!driver.Installed)
                {
                    SetBlocked("driver", name, "not installed");
                    continue;
                }
                if (!driver.WindowsOwned)
                {
                    SetBlocked("driver", name, "není potvrzený Windows/System32 driver");
                    continue;
                }

                if ((driver.Type & 0x3) == 0)
                {
                    SetBlocked("driver", name, "není kernel/filesystem driver");
                    continue;
                }

                int start = driver.Start;

                if (state.Managed && state.Sleeping && start == 4 && driver.AttachedDevice)
                {
                    int restoreStart = state.OriginalServiceStart ?? 3;
                    WriteServiceStartType(name, restoreStart);
                    _items[Key("driver", name)] = state with
                    {
                        Managed = false,
                        Sleeping = false,
                        Rollbacks = state.Rollbacks + 1,
                        BlockedReason = "device attached again; AI restored driver",
                        CooldownUntilUtc = DateTime.UtcNow.AddHours(24),
                        LastAction = "restored because device became attached",
                        LastChangedUtc = DateTime.UtcNow
                    };
                    _restoredCount++;
                    _lastAction = $"Driver {name}: attached device detected -> restored";
                    AppendAction("driver", name, "restored", "device attached after AI disable");
                    LogService.Info("ai-system",
                        $"AI_SYSTEM_DRIVER_RESTORE name={name} reason=device_attached start={restoreStart}");
                    continue;
                }

                if (state.Managed && state.Sleeping && start != 4)
                {
                    int rollbacks = state.Rollbacks + 1;
                    _items[Key("driver", name)] = state with
                    {
                        Managed = false,
                        Sleeping = false,
                        Rollbacks = rollbacks,
                        BlockedReason = rollbacks >= 2 ? "opakovaně obnoveno systémem / pravděpodobně potřebné" : string.Empty,
                        CooldownUntilUtc = DateTime.UtcNow.Add(rollbacks >= 2 ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(30)),
                        LastAction = "externally restored / assumed needed",
                        LastChangedUtc = DateTime.UtcNow
                    };
                    _restoredCount++;
                    _lastAction = $"Driver {name}: needed again";
                    AppendAction("driver", name, "restored externally", rollbacks >= 2
                        ? "opakovaně obnoveno systémem"
                        : "systém driver znovu povolil");
                    continue;
                }

                if (!quietEnough || start == 4)
                {
                    ResetObservation("driver", name);
                    continue;
                }

                if (!AiDriverAutomationPolicy.IsEligibleStartType(start))
                {
                    SetBlocked("driver", name, AiDriverAutomationPolicy.IneligibleReason(start));
                    continue;
                }

                if (driver.AttachedDevice)
                {
                    SetBlocked("driver", name, "připojené zařízení / driver je aktivně používaný");
                    continue;
                }

                DateTime observed = state.ObservationSinceUtc ?? DateTime.UtcNow;
                state = state with { ObservationSinceUtc = observed, BlockedReason = string.Empty };
                _items[Key("driver", name)] = state;
                if (DateTime.UtcNow - observed < AiDriverAutomationPolicy.RequiredObservation(config.ObservationSeconds))
                    continue;
                if (_maintenanceMutationCommitted)
                    continue;

                WriteServiceStartType(name, 4);

                _items[Key("driver", name)] = state with
                {
                    Managed = true,
                    Sleeping = true,
                    OriginalServiceStart = start,
                    ObservationSinceUtc = null,
                    CooldownUntilUtc = DateTime.UtcNow.AddSeconds(config.ActionCooldownSeconds),
                    LastAction = "disabled for next boot after long idle observation",
                    LastChangedUtc = DateTime.UtcNow,
                    PerformanceVerified = false
                };
                _maintenanceMutationCommitted = true;
                _lastAction = $"Driver {name}: optional demand-start driver disabled for next boot";
                AppendAction("driver", name, "disabled",
                    $"long idle observation; no attached device; demand-start only; originalStart={start}; effect next boot");
                LogService.Info("ai-system",
                    $"AI_SYSTEM_DRIVER_DISABLE name={name} originalStart={start} measuredPerformance=false nextBoot=true");
                return;
            }
            catch (Exception ex)
            {
                SetBlocked("driver", name, ex.GetBaseException().Message);
            }
        }
    }

    private bool KindNeedsFastCheck(string kind)
    {
        foreach (AiSystemRuntimeItemState item in _items.Values)
        {
            if (!item.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                continue;
            if (item.Managed || item.Sleeping || item.ObservationSinceUtc != null)
                return true;
        }
        return false;
    }

    private static bool ShouldRunDeepScan(
        ref DateTime lastScanUtc,
        DateTime nowUtc,
        TimeSpan interval)
    {
        if (lastScanUtc != DateTime.MinValue && nowUtc - lastScanUtc < interval)
            return false;
        lastScanUtc = nowUtc;
        return true;
    }

    private static TimeSpan AdaptiveScanInterval(TimeSpan baseInterval, double lastScanMs)
    {
        double multiplier = lastScanMs >= 100 ? 3d : lastScanMs >= 50 ? 2d : 1d;
        return TimeSpan.FromTicks((long)(baseInterval.Ticks * multiplier));
    }

    private AiSystemRuntimeItemState GetState(string kind, string id)
    {
        string key = Key(kind, id);
        if (_items.TryGetValue(key, out var state))
            return state;
        state = new AiSystemRuntimeItemState(
            kind, id, false, false, string.Empty, null, null, null, null, false, 0, "none");
        _items[key] = state;
        return state;
    }

    private void SetBlocked(string kind, string id, string reason)
    {
        var state = GetState(kind, id);
        if (state.ObservationSinceUtc == null &&
            string.Equals(state.BlockedReason, reason, StringComparison.Ordinal))
            return;

        _items[Key(kind, id)] = state with
        {
            BlockedReason = reason,
            ObservationSinceUtc = null
        };
    }

    private void ResetObservation(string kind, string id)
    {
        var state = GetState(kind, id);
        if (state.ObservationSinceUtc == null && string.IsNullOrEmpty(state.BlockedReason))
            return;
        _items[Key(kind, id)] = state with
        {
            ObservationSinceUtc = null,
            BlockedReason = string.Empty
        };
    }

    private void LoadItems()
    {
        try
        {
            if (!File.Exists(ItemStatePath))
            {
                lock (SnapshotGate) CachedItems = Array.Empty<AiSystemRuntimeItemState>();
                return;
            }

            var rows = JsonSerializer.Deserialize<AiSystemRuntimeItemState[]>(File.ReadAllText(ItemStatePath))
                       ?? Array.Empty<AiSystemRuntimeItemState>();
            foreach (var row in rows)
            {
                if (row.Kind.Equals("device", StringComparison.OrdinalIgnoreCase))
                    continue;
                _items[Key(row.Kind, row.Id)] = row;
            }

            var snapshot = _items.Values.OrderBy(x => x.Kind).ThenBy(x => x.Id).ToArray();
            lock (SnapshotGate)
            {
                CachedItems = snapshot;
                CachedItemsWriteUtc = File.GetLastWriteTimeUtc(ItemStatePath);
            }
            _lastPersistJson = JsonSerializer.Serialize(snapshot);
        }
        catch { }
    }

    private static void AppendAction(string kind, string id, string action, string reason)
    {
        lock (ActionHistoryGate)
        {
            try
            {
                Directory.CreateDirectory(StateDirectory);
                var history = (CachedActionHistory ?? ReadActionHistory().ToArray()).ToList();
                history.Add(new AiSystemActionHistoryEntry(DateTime.UtcNow, kind, id, action, reason));
                if (history.Count > 200)
                    history.RemoveRange(0, history.Count - 200);

                CachedActionHistory = history.ToArray();
                string temp = ActionHistoryPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(CachedActionHistory));
                File.Move(temp, ActionHistoryPath, true);
                CachedActionHistoryWriteUtc = File.GetLastWriteTimeUtc(ActionHistoryPath);
            }
            catch
            {
            }
        }
    }

    private bool PersistItems()
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            string json = JsonSerializer.Serialize(
                _items.Values.OrderBy(x => x.Kind).ThenBy(x => x.Id).ToArray());
            if (string.Equals(json, _lastPersistJson, StringComparison.Ordinal))
                return false;

            var snapshot = _items.Values.OrderBy(x => x.Kind).ThenBy(x => x.Id).ToArray();
            string temp = ItemStatePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, ItemStatePath, true);
            _lastPersistJson = json;
            lock (SnapshotGate)
            {
                CachedItems = snapshot;
                CachedItemsWriteUtc = File.GetLastWriteTimeUtc(ItemStatePath);
            }
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("ai-system", "AI System Runtime state persist failed", ex);
            return false;
        }
    }

    private void WriteHealth(
        AiSystemRuntimeConfig config,
        double loadPercent,
        bool gameActive,
        bool realIdle)
    {
        try
        {
            int managed = 0;
            int sleeping = 0;
            int blocked = 0;
            int managedServices = 0;
            int managedTasks = 0;
            int managedDrivers = 0;
            foreach (AiSystemRuntimeItemState item in _items.Values)
            {
                if (item.Managed)
                {
                    managed++;
                    if (item.Kind == "service") managedServices++;
                    else if (item.Kind == "task") managedTasks++;
                    else if (item.Kind == "driver") managedDrivers++;
                }
                if (item.Sleeping) sleeping++;
                if (!string.IsNullOrWhiteSpace(item.BlockedReason)) blocked++;
            }

            double roundedLoad = Math.Round(loadPercent, 1);
            string fingerprint = string.Join('|',
                config.Enabled,
                config.ServicesEnabled,
                config.ScheduledTasksEnabled,
                config.OptionalDevicesEnabled,
                managed,
                sleeping,
                blocked,
                _restoredCount,
                managedServices,
                managedTasks,
                managedDrivers,
                _lastAction,
                _lastError,
                roundedLoad,
                gameActive,
                realIdle,
                Math.Round(_lastCycleMs, 2),
                Math.Round(_lastServiceScanMs, 2),
                Math.Round(_lastTaskScanMs, 2),
                Math.Round(_lastDriverScanMs, 2));

            DateTime nowUtc = DateTime.UtcNow;
            if (string.Equals(fingerprint, _lastHealthFingerprint, StringComparison.Ordinal) &&
                _lastHealthWriteUtc != DateTime.MinValue &&
                nowUtc - _lastHealthWriteUtc < TimeSpan.FromMinutes(2))
                return;

            var health = new AiSystemRuntimeHealth(
                nowUtc,
                config.Enabled,
                config.ServicesEnabled,
                config.ScheduledTasksEnabled,
                config.OptionalDevicesEnabled,
                managed,
                sleeping,
                blocked,
                _restoredCount,
                managedServices,
                managedTasks,
                managedDrivers,
                _lastAction,
                _lastError,
                roundedLoad,
                gameActive,
                realIdle,
                Math.Round(_lastCycleMs, 2),
                Math.Round(_lastServiceScanMs, 2),
                Math.Round(_lastTaskScanMs, 2),
                Math.Round(_lastDriverScanMs, 2));

            Directory.CreateDirectory(StateDirectory);
            string temp = HealthPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(health));
            File.Move(temp, HealthPath, true);
            _lastHealthFingerprint = fingerprint;
            _lastHealthWriteUtc = nowUtc;
            lock (SnapshotGate)
            {
                CachedHealth = health;
                CachedHealthWriteUtc = File.GetLastWriteTimeUtc(HealthPath);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("ai-system", "AI System Runtime health write failed", ex);
        }
    }

    private static string Key(string kind, string id) => kind + "|" + id;
    private static string ShortTask(string path)
    {
        int slash = path.LastIndexOf('\\');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }
}

