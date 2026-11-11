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
    bool SecurityEnabled = true,
    bool AutomaticGameGpuPriorityEnabled = true,
    bool AutomaticProcessPriorityEnabled = true,
    bool DisableWindowsSearchIndexing = false);

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
    double? CandidateLoadMax = null,
    int FailedBenefitTrials = 0,
    DateTime? OriginalTaskNextRunUtc = null,
    string PendingMutation = "",
    double LastVerifiedBenefitAbsolute = 0,
    DateTime? LastBenefitVerifiedUtc = null,
    double LastVerifiedBenefitPercent = 0,
    double? BaselineAvailableRamMiB = null,
    int VerificationMemorySamples = 0,
    double VerificationMemoryTotalMiB = 0,
    double? VerificationMemoryMinMiB = null,
    double? VerificationMemoryMaxMiB = null,
    DateTime? VerificationLastMemorySnapshotUtc = null,
    int CandidateMemorySamples = 0,
    double? CandidateAvailableRamMiB = null,
    double? CandidateMemoryMinMiB = null,
    double? CandidateMemoryMaxMiB = null,
    double? LastVerifiedAvailableRamBenefitMiB = null,
    DateTime? FeatureFirstObservedUtc = null,
    DateTime? FeatureLastObservedUtc = null,
    int FeatureObservationSamples = 0,
    bool? FeatureObservedRunning = null,
    AiOptionalFeatureUsage FeatureObservedUsage = AiOptionalFeatureUsage.Unknown,
    DateTime? FeatureLastConfirmedUseUtc = null,
    DateTime? FeatureLastUsageProbeUtc = null,
    DateTime? DriverObservationLastUtc = null);

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
    int ActiveVerifiedOptimizations = 0,
    int RecentVerifiedWins = 0,
    double LatestMeasuredCpuBenefitPp = 0,
    double LatestMeasuredBenefitPercent = 0,
    string LatestVerifiedTarget = "",
    DateTime? LatestVerifiedAtUtc = null,
    double? LatestMeasuredAvailableRamBenefitMiB = null);

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
    private static readonly ReusableFileMetadata ConfigMetadata = new(ConfigPath);
    private static readonly ReusableFileMetadata HealthMetadata = new(HealthPath);
    private static readonly ReusableFileMetadata ItemsMetadata = new(ItemStatePath);
    private static readonly ReusableFileMetadata ActionHistoryMetadata = new(ActionHistoryPath);
    private static FileMetadataStamp CachedConfigStamp;
    private static FileMetadataStamp CachedHealthStamp;
    private static FileMetadataStamp CachedItemsStamp;
    private static FileMetadataStamp CachedRejectedItemsStamp;
    private static DateTime CachedRejectedItemsReadUtc;
    private static bool CachedItemsRejected;
    private static FileMetadataStamp CachedActionHistoryStamp;

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
        "WdiServiceHost",
        // Optional legacy monitoring and application support, measured A/B/A only.
        "ADPSvc",
        "InventorySvc",
        "GraphicsPerfSvc",
        "SNMPTrap",
        "SmsRouter",
        "AxInstSV",
        "pla",
        // Additional optional Windows user-facing features. These may be
        // experimented on ONLY during independently proven quiet windows;
        // live stop/dependency/readback, A/B/A and rollback still gate writes.
        "EntAppSvc",           // enterprise app management (if installed)
        // lfsvc has dedicated privacy-consent ownership and restoration.
        // Never A/B/A-disable it through the generic service candidate pool.
        "SensorDataService",   // optional sensor data service
        "SensorService",       // optional sensor access service
        "SensrSvc"             // optional sensor monitoring
    };

    // Precompute immutable journal lookup keys once. The frequent fast pass
    // must not format dozens of service|name strings every few seconds.
    internal static readonly string[] ServiceAllowlistKeys =
        Array.ConvertAll(ServiceAllowlist, static name => "service|" + name);

    private static readonly HashSet<string> AutomaticServiceCandidates =
        new(ServiceAllowlist, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] DriverAllowlist =
    {
        "Beep",
        "flpydisk",
        "sfloppy",
        "parport",
        "parvdm",
        "Changer",
        "sermouse",
        "Serial",
        "fdc",      // floppy disk controller: only demand-start/no Enum device
        "serenum"   // legacy serial enumerator: same strict driver gates
    };

    // Fast active driver checks never need to format durable journal keys.
    internal static readonly string[] DriverAllowlistKeys =
        Array.ConvertAll(DriverAllowlist, static name => "driver|" + name);

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
        @"\Microsoft\Windows\PerformanceTrace\ShowFeedbackToast",
        // Optional legacy telemetry tasks: absent on many Windows 11 builds.
        // They retain the same live-state, A/B/A and scheduled-run safeguards.
        @"\Microsoft\Windows\Application Experience\AitAgent",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Uploader",
        // Legacy scheduled performance assessment; optional/absent on many builds.
        @"\Microsoft\Windows\Maintenance\WinSAT",
        // Optional telemetry/feedback, subject to existing live-state and rollback gates.
        @"\Microsoft\Windows\Customer Experience Improvement Program\HypervisorFlightingTask",
        @"\Microsoft\Windows\Application Experience\MareBackup",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Sqm-Tasks",
        // Optional telemetry, cosmetic sync and scheduled power diagnostics.
        // Still subject to live-state, upcoming-run, A/B/A and restore gates.
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp",
        @"\Microsoft\Windows\Shell\ThemesSyncedImageDownload",
        @"\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem",
        @"\Microsoft\Windows\Flighting\FeatureConfig\UsageDataReporting",
        @"\Microsoft\Windows\Flighting\FeatureConfig\BootstrapUsageDataReporting",
        @"\Microsoft\Windows\Flighting\FeatureConfig\UsageDataFlushing",
        @"\Microsoft\Windows\Flighting\FeatureConfig\UsageDataReceiver",
        // Optional usage-processing job. Do not include configuration
        // reconciliation, security safeguards or Windows Update tasks.
        @"\Microsoft\Windows\Flighting\FeatureConfig\GovernedFeatureUsageProcessing"
    };

    internal static readonly string[] TaskAllowlistKeys =
        Array.ConvertAll(TaskAllowlist, static path => "task|" + path);

    // Read-only summary uses these immutable admission identities; it must
    // never treat a missing journal row as proof of a missing OS component.
    private static readonly HashSet<string> AutomaticTaskCandidates =
        new(TaskAllowlist, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AutomaticDriverCandidates =
        new(DriverAllowlist, StringComparer.OrdinalIgnoreCase);

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
    private long _lastTickStamp; // monotonic maintenance cadence
    private string _lastAction = "none";
    private string _lastError = string.Empty;
    private int _restoredCount;
    private readonly AiSelfOptimizationService _selfOptimizer = new();
    // Learned local hour/day activity; only defers NEW optional trials.
    private readonly AiWorkloadForecastService _forecast = new();
    private readonly AiAutomaticProcessPriorityService _processPriority = new();
    private readonly AiWindowsSearchOptimizationService _searchOptimization = new();
    private readonly AiUnusedBluetoothOptimizationService _bluetoothOptimization = new();
    private readonly AiUnusedLocationOptimizationService _locationOptimization = new();
    private readonly AiUnusedPnpDeviceOptimizationService _unusedPnpDevices = new();
    private readonly AiWindowsSearchMaintenanceTaskService _searchMaintenanceTask = new();
    private readonly AiOptionalTaskOptimizationService _optionalTasks = new();
    private readonly AiSystemInventoryCoverageService _fullInventory = new();
    private readonly AiThirdPartyIdleAutomationService _thirdPartyIdle = new();
    private readonly AiGameGpuPriorityService _gameGpuPriority = new();
    private readonly AiForegroundWindowResponseProbe _foregroundResponse = new();
    private readonly AiTweakRuntimeService _tweakRuntime = new();
    private readonly AiSecurityHardeningService _securityRuntime = new();
    // Security-sensitive service ownership/dependency cache ages by elapsed
    // monotonic time, not by mutable Windows/NTP wall-clock timestamps.
    private readonly Dictionary<string, (long CollectedStamp, bool Value)> _boolProbeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private string _lastPersistJson = string.Empty;
    // Immutable record values from the last successful durable checkpoint.
    // Avoid repeat sorting/JSON allocations during unchanged 5s cycles.
    private AiSystemRuntimeItemState[]? _lastDurableJournalValues;
    private AiSystemHealthFingerprint? _lastHealthFingerprint;
    private DateTime _lastHealthWriteUtc = DateTime.MinValue;
    private long _lastCloudSyncStamp;
    private long _lastServiceDeepScanStamp;
    private DateTime _lastPerUserObservationPruneUtc = DateTime.MinValue;
    private long _lastTaskDeepScanStamp;
    private long _lastDriverDeepScanStamp;
    private double _lastServiceScanMs;
    private double _lastTaskScanMs;
    private double _lastDriverScanMs;
    private readonly Queue<AiBackgroundLoadSample> _quietLoadSamples = new();
    private DateTime? _quietEpochStartUtc;
    private bool _maintenanceMutationCommitted;
    private int _maintenanceKindCursor;
    private int _serviceCandidateCursor;
    private int _taskCandidateCursor;
    private int _driverCandidateCursor;
    private bool _recoverInterruptedTrials;
    private bool _maintenanceWriteFaulted;
    private bool _journalLoadHealthy = true;
    private bool _journalInitiallyAbsent;

    public AiSystemResourceManagerService()
    {
        LoadItems();
        _recoverInterruptedTrials =
            AiPendingTrialSelectionPolicy.HasInterruptedOrPendingJournal(_items.Values);
    }

    public static AiSystemRuntimeConfig LoadConfig()
    {
        lock (SnapshotGate)
        {
            try
            {
                FileMetadataStamp stamp = ConfigMetadata.Read();
                if (!stamp.Exists)
                {
                    // Initial startup may use the documented defaults. If a
                    // previously loaded config VANISHES, do not silently
                    // recreate it with automatic Windows changes enabled.
                    if (CachedConfig is not null)
                        return AiRuntimeScopePolicy.Sanitize(CachedConfig with { Enabled = false });
                    AiSystemRuntimeConfig defaults = AiRuntimeScopePolicy.Sanitize(new AiSystemRuntimeConfig());
                    SaveConfig(defaults);
                    return defaults;
                }

                if (CachedConfig != null && stamp == CachedConfigStamp)
                    return AiRuntimeScopePolicy.Sanitize(CachedConfig);

                // On a changed config snapshot, decode UTF-8 directly while
                // retaining legacy BOM support and existing sanitize/fallback.
                AiSystemRuntimeConfig loaded =
                    ObservedJsonSnapshotCache<AiSystemRuntimeConfig>.ReadSnapshotFromFile(ConfigPath)
                    ?? new AiSystemRuntimeConfig();
                AiSystemRuntimeConfig sanitized = AiRuntimeScopePolicy.Sanitize(loaded);
                if (!Equals(loaded, sanitized))
                {
                    SaveConfig(sanitized);
                    return sanitized;
                }

                CachedConfig = sanitized;
                CachedConfigStamp = stamp;
                return sanitized;
            }
            catch
            {
                // A corrupt/locked config is UNKNOWN, not permission to
                // re-enable automation. Preserve the known scope switches
                // for display, but refuse automatic actions until recovered.
                return AiRuntimeScopePolicy.Sanitize(
                    (CachedConfig ?? new AiSystemRuntimeConfig()) with { Enabled = false });
            }
        }
    }

    // Serialize AI setting writes across updated GUI / RuntimeHost instances.
    // Lock order is always SnapshotGate -> config file lease. LoadConfig's
    // first-run SaveConfig call follows the same order (Monitor is reentrant).
    private static FileStream AcquireConfigWriteLease()
    {
        Directory.CreateDirectory(StateDirectory);
        try
        {
            return new FileStream(ConfigPath + ".lease", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "Nastavení AI právě ukládá jiná instance. Zkus to znovu.", ex);
        }
    }

    private static void WriteConfigUnderLease(AiSystemRuntimeConfig config)
    {
        // Different unique temp files also protect against old app instances
        // which do not yet participate in the new config write lease.
        string temp = ConfigPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp,
                JsonSerializer.SerializeToUtf8Bytes(config, IndentedJsonOptions));
            File.Move(temp, ConfigPath, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp); // Only our own GUID-named staging file.
        }
        AiSystemRuntimeConfig verified =
            ObservedJsonSnapshotCache<AiSystemRuntimeConfig>.ReadSnapshotFromFile(ConfigPath)
            ?? throw new InvalidDataException("Zápis AI nastavení nebylo možné ověřit.");
        if (verified != config)
            throw new InvalidDataException("Uložené nastavení AI neodpovídá požadovaným volbám.");
        CachedConfig = config;
        CachedConfigStamp = ConfigMetadata.Read();
    }

    public static void SaveConfig(AiSystemRuntimeConfig config)
    {
        AiSystemRuntimeConfig sanitized = AiRuntimeScopePolicy.Sanitize(config);
        lock (SnapshotGate)
        {
            using FileStream lease = AcquireConfigWriteLease();
            WriteConfigUnderLease(sanitized);
        }
    }

    // Change one AI scope without overwriting unrelated switches that
    // another updated instance saved milliseconds earlier. Re-read the
    // actual file while holding the write lease, not the metadata cache.
    public static AiSystemRuntimeConfig UpdateConfig(
        Func<AiSystemRuntimeConfig, AiSystemRuntimeConfig> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (SnapshotGate)
        {
            using FileStream lease = AcquireConfigWriteLease();
            AiSystemRuntimeConfig current;
            if (File.Exists(ConfigPath))
            {
                current = ObservedJsonSnapshotCache<AiSystemRuntimeConfig>
                    .ReadSnapshotFromFile(ConfigPath)
                    ?? throw new InvalidDataException("Uložené nastavení AI je neplatné.");
                current = AiRuntimeScopePolicy.Sanitize(current);
            }
            else
            {
                // Only an untouched first-run config may use defaults.
                // A vanished previously cached config is not consent to
                // re-enable automation scopes the user turned off.
                if (CachedConfig != null)
                    throw new InvalidDataException(
                        "Uložené nastavení AI zmizelo; změna byla zablokována.");
                current = AiRuntimeScopePolicy.Sanitize(new AiSystemRuntimeConfig());
            }
            AiSystemRuntimeConfig updated = AiRuntimeScopePolicy.Sanitize(
                change(current) ?? throw new InvalidOperationException(
                    "Změna konfigurace AI nevrátila platný stav."));
            if (updated == current)
            {
                CachedConfig = current;
                CachedConfigStamp = ConfigMetadata.Read();
                return current;
            }
            WriteConfigUnderLease(updated);
            return updated;
        }
    }

    public static AiSystemRuntimeHealth? ReadHealth()
    {
        lock (SnapshotGate)
        {
            try
            {
                FileMetadataStamp stamp = HealthMetadata.Read();
                if (CachedHealth != null && stamp == CachedHealthStamp)
                    return CachedHealth;

                CachedHealth = stamp.Exists
                    ? ObservedJsonSnapshotCache<AiSystemRuntimeHealth>.ReadSnapshotFromFile(HealthPath)
                    : null;
                CachedHealthStamp = stamp;
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
                FileMetadataStamp stamp = ItemsMetadata.Read();
                if (CachedItems != null && stamp == CachedItemsStamp)
                    return CachedItems;
                // A malformed ownership file is still invalid on every GUI
                // refresh. Cache the failure by metadata stamp instead of
                // reparsing the same large broken JSON repeatedly.
                if (CachedItemsRejected &&
                    AiJournalSnapshotReadPolicy.CanReuseRejected(
                        CachedRejectedItemsStamp, stamp,
                        CachedRejectedItemsReadUtc, DateTime.UtcNow))
                    return CachedItems ?? Array.Empty<AiSystemRuntimeItemState>();

                if (!stamp.Exists)
                {
                    CachedItems = Array.Empty<AiSystemRuntimeItemState>();
                    CachedItemsStamp = stamp;
                    CachedItemsRejected = false;
                    return CachedItems;
                }

                // The UI must not publish malformed/duplicate ownership rows
                // after the FanAgent has independently failed closed. Retain
                // its last known good cache, never cache the invalid file.
                // Guard by the already cached metadata before allocating a
                // potentially oversized JSON string for display.
                if (!AiJournalSnapshotReadPolicy.TryReadStable(
                        ItemStatePath, ItemsMetadata, stamp,
                        out AiSystemRuntimeItemState[] rows,
                        out FileMetadataStamp accepted))
                {
                    CachedRejectedItemsStamp = accepted;
                    CachedRejectedItemsReadUtc = DateTime.UtcNow;
                    CachedItemsRejected = true;
                    return CachedItems ?? Array.Empty<AiSystemRuntimeItemState>();
                }
                CachedItems = rows;
                CachedItemsStamp = accepted;
                CachedItemsRejected = false;
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
                FileMetadataStamp stamp = ActionHistoryMetadata.Read();
                if (CachedActionHistory != null && stamp == CachedActionHistoryStamp)
                    return CachedActionHistory;

                CachedActionHistory = stamp.Exists
                    ? ReadActionHistoryFile()
                    : Array.Empty<AiSystemActionHistoryEntry>();
                CachedActionHistoryStamp = stamp;
                return CachedActionHistory;
            }
            catch
            {
                return CachedActionHistory ?? Array.Empty<AiSystemActionHistoryEntry>();
            }
        }
    }

    private static AiSystemActionHistoryEntry[] ReadActionHistoryFile() =>
        DecodeActionHistoryBytes(File.ReadAllBytes(ActionHistoryPath));

    // Use the SAME atomic file snapshot for legacy Unicode as for UTF-8.
    // File.ReadAllText here reopened the journal after File.ReadAllBytes,
    // doubling legacy disk I/O and risking a mixed-generation read.
    internal static AiSystemActionHistoryEntry[] DecodeActionHistoryBytes(
        byte[] bytes)
    {
        // Check four-byte Unicode BOMs before two-byte UTF-16 (FF FE is
        // also the beginning of a UTF-32 LE BOM).
        if (bytes.Length >= 4 &&
            bytes[0] == 0xFF && bytes[1] == 0xFE &&
            bytes[2] == 0x00 && bytes[3] == 0x00)
            return JsonSerializer.Deserialize<AiSystemActionHistoryEntry[]>(
                new UTF32Encoding(bigEndian: false, byteOrderMark: false)
                    .GetString(bytes, 4, bytes.Length - 4)) ??
                Array.Empty<AiSystemActionHistoryEntry>();
        if (bytes.Length >= 4 &&
            bytes[0] == 0x00 && bytes[1] == 0x00 &&
            bytes[2] == 0xFE && bytes[3] == 0xFF)
            return JsonSerializer.Deserialize<AiSystemActionHistoryEntry[]>(
                new UTF32Encoding(bigEndian: true, byteOrderMark: false)
                    .GetString(bytes, 4, bytes.Length - 4)) ??
                Array.Empty<AiSystemActionHistoryEntry>();
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return JsonSerializer.Deserialize<AiSystemActionHistoryEntry[]>(
                Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)) ??
                Array.Empty<AiSystemActionHistoryEntry>();
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return JsonSerializer.Deserialize<AiSystemActionHistoryEntry[]>(
                Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)) ??
                Array.Empty<AiSystemActionHistoryEntry>();
        int start = bytes.Length >= 3 &&
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? 3 : 0;
        return JsonSerializer.Deserialize<AiSystemActionHistoryEntry[]>(
            bytes.AsSpan(start)) ?? Array.Empty<AiSystemActionHistoryEntry>();
    }

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

        // Independent foreground-game GPU scheduling. Reuse the existing
        // FanAgent sample/tick, never add a process scanner or a timer.
        // OFF, telemetry loss and journal failure restore owned priority.
        _gameGpuPriority.Tick(
            // A live GPU scheduling boost would confound EPP/service/task
            // A/B/A evidence. Restore owned boost during other experiments.
            AiGameGpuPriorityPolicy.MayEnableForTick(
                config.Enabled && config.AutomaticGameGpuPriorityEnabled &&
                _journalLoadHealthy && cpuEvidenceAvailable &&
                powerTelemetryComparable && !_recoverInterruptedTrials,
                AiPerformanceExperimentCoordinator.IsActive),
            powerStatus, workload, now);

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
    // without touching services, tasks, drivers, GPU priority or the power plan.
    public static void RestoreProcessPriorityNow()
    {
        var service = new AiSystemResourceManagerService();
        service._processPriority.Tick(false, null, AiWorkloadClass.Unknown, DateTime.UtcNow);
    }

    public static async Task RestoreAllNowAsync(CancellationToken token = default)
    {
        var service = new AiSystemResourceManagerService();
        if (!service._journalLoadHealthy)
            throw new InvalidOperationException(
                "Obnovu nelze bezpečně spustit: záznam vlastnictví AI změn není čitelný.");

        bool journalCommitted = false;
        Exception? coreFailure = null;
        Exception? appFailure = null;
        try
        {
            // Core Windows resources and optional third-party apps have
            // independent ownership journals. Attempt BOTH groups even if
            // one has a recoverable failure; an error in security/power
            // restore must not strand the Firefox or Steam recovery path.
            try
            {
                service._processPriority.Tick(false, null, AiWorkloadClass.Unknown, DateTime.UtcNow);
                service._gameGpuPriority.Tick(false, null, AiWorkloadClass.Unknown, DateTime.UtcNow);
                await service.RestoreAllAsync(token).ConfigureAwait(false);
                string? searchRestore = service._searchOptimization.Tick(enabled: false);
                if (!string.IsNullOrEmpty(service._searchOptimization.LastError))
                    throw new IOException("Windows Search restore: " +
                        service._searchOptimization.LastError);
                if (searchRestore != null)
                    AppendAction("service", "WSearch", "AI restore", searchRestore);
                string? pnpRestore = await service._unusedPnpDevices.TickAsync(
                    aiAllowed: false, bluetoothOwned: false, token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(service._unusedPnpDevices.LastError))
                    throw new IOException("Device Manager restore: " +
                        service._unusedPnpDevices.LastError);
                if (pnpRestore != null)
                    AppendAction("device", "Intel USB Bluetooth", "AI restore", pnpRestore);
                string? locationRestore = service._locationOptimization.Tick(
                    serviceAutomationEnabled: false);
                if (!string.IsNullOrEmpty(service._locationOptimization.LastError))
                    throw new IOException("Geolocation restore: " +
                        service._locationOptimization.LastError);
                if (locationRestore != null)
                    AppendAction("service", "lfsvc", "AI restore", locationRestore);
                string? bluetoothRestore = service._bluetoothOptimization.Tick(enabled: false);
                if (!string.IsNullOrEmpty(service._bluetoothOptimization.LastError))
                    throw new IOException("Bluetooth restore: " +
                        service._bluetoothOptimization.LastError);
                if (bluetoothRestore != null)
                    AppendAction("service", "bthserv", "AI restore", bluetoothRestore);
                string? maintenanceRestore = service._searchMaintenanceTask.Tick(
                    enabled: false, token);
                if (!string.IsNullOrEmpty(service._searchMaintenanceTask.LastError))
                    throw new IOException("Windows Search task restore: " +
                        service._searchMaintenanceTask.LastError);
                if (maintenanceRestore != null)
                    AppendAction("task",
                        @"\Microsoft\Windows\Shell\IndexerAutomaticMaintenance",
                        "AI restore", maintenanceRestore);
                // Restore owned optional tasks alongside other AI resource
                // scopes; never leave user-disabled AI experiments behind.
                service._optionalTasks.Tick(enabled: false, TaskAllowlist, token);
                if (!string.IsNullOrEmpty(service._optionalTasks.LastError))
                    throw new IOException("Optional task restore: " +
                        service._optionalTasks.LastError);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                coreFailure = ex;
                LogService.Error("ai-system", "Core Restore all partly failed", ex);
            }

            try
            {
                await service._thirdPartyIdle.RestoreOwnedNowAsync(token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                appFailure = ex;
                LogService.Error("ai-system", "Third-party Restore all partly failed", ex);
            }
        }
        finally
        {
            // A partial restore changes ownership records even when a later
            // service/task fails or cancellation arrives. Always commit what
            // was actually restored; keep unresolved entries in the journal.
            journalCommitted = service.PersistItems(unchangedIsSuccess: true);
            service.WriteHealth(LoadConfig(), 0, false, false);
        }

        if (!journalCommitted)
            throw new IOException(
                "Obnova AI nebyla bezpečně potvrzena uloženým záznamem. Další obnova je nutná.");

        int stillManaged = service._items.Values.Count(x => x.Managed);
        bool pendingRecovery =
            AiPendingTrialSelectionPolicy.HasInterruptedOrPendingJournal(service._items.Values);
        if (stillManaged != 0 || pendingRecovery || coreFailure != null || appFailure != null)
        {
            var problems = new List<string>(3);
            if (stillManaged != 0 || pendingRecovery)
                problems.Add($"Spravovaných položek zbývá: {stillManaged}; obnova čeká: {pendingRecovery}.");
            if (coreFailure != null)
                problems.Add("Windows: " + coreFailure.GetBaseException().Message);
            if (appFailure != null)
                problems.Add("Aplikace: " + appFailure.GetBaseException().Message);
            throw new InvalidOperationException(
                "Obnova AI není dokončená. " + string.Join(" ", problems) +
                " Nedokončené položky zůstávají evidované.");
        }
    }

    public static int ResetLearnedProtection()
    {
        var service = new AiSystemResourceManagerService();
        if (!service._journalLoadHealthy || service._recoverInterruptedTrials)
            throw new InvalidOperationException(
                "Naučené blokace nelze bezpečně změnit: záznam AI není čitelný nebo čeká na obnovu.");
        int changed = 0;
        foreach (var pair in service._items.ToArray())
        {
            AiSystemRuntimeItemState state = pair.Value;
            if (state.Rollbacks < 2 && state.FailedBenefitTrials == 0 &&
                !state.BlockedReason.Contains("learned protection", StringComparison.OrdinalIgnoreCase))
                continue;

            service._items[pair.Key] = state with
            {
                Rollbacks = 0,
                FailedBenefitTrials = 0,
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
            // Only report a reset after its checkpoint reached disk. An
            // interrupted write leaves the previous protection authoritative.
            if (!service.PersistItems())
                throw new IOException(
                    "Zrušení naučených blokací nebylo potvrzeno uloženým záznamem; původní ochrana musí být ověřena.");
            AppendAction("system", "learned-protection", "reset", $"{changed} položek");
        }
        return changed;
    }

    // Shared, read-only admission predicate; actual imports still require
    // a healthy ownership journal and durable write with all safety gates.
    internal static bool IsCloudCandidateAllowed(string kind, string id) =>
        kind.Equals("service", StringComparison.OrdinalIgnoreCase)
            ? AutomaticServiceCandidates.Contains(id) && !IsCriticalService(id)
            : kind.Equals("task", StringComparison.OrdinalIgnoreCase)
                ? AutomaticTaskCandidates.Contains(id)
                : kind.Equals("driver", StringComparison.OrdinalIgnoreCase) &&
                  AutomaticDriverCandidates.Contains(id);

    public static int ImportCloudMemory(IEnumerable<AiSystemRuntimeItemState> cloudItems)
    {
        var service = new AiSystemResourceManagerService();
        if (!service._journalLoadHealthy || service._recoverInterruptedTrials ||
            AiPendingTrialSelectionPolicy.HasInterruptedOrPendingJournal(
                service._items.Values) ||
            service._items.Values.Any(x => x.Managed || x.Sleeping))
            return 0;
        int imported = 0;
        DateTime importedAt = DateTime.UtcNow;

        foreach (AiSystemRuntimeItemState row in cloudItems)
        {
            bool allowed = IsCloudCandidateAllowed(row.Kind, row.Id);
            if (!allowed) continue;

            string key = Key(row.Kind, row.Id);
            service._items.TryGetValue(key, out AiSystemRuntimeItemState? local);
            AiSystemRuntimeItemState? merged =
                AiCloudProfilePolicy.MergeLearnedProtection(local, row, importedAt);
            if (merged is null)
                continue;
            service._items[key] = merged;
            imported++;
        }

        // Do not report successful cloud learning unless the protected local
        // journal has actually reached disk. Never overwrite an active trial.
        return imported > 0 && service.PersistItems() ? imported : 0;
    }

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
        _items[Key(state.Kind, state.Id)] =
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
            LastChangedUtc = nowUtc,
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
        _items[Key(state.Kind, state.Id)] =
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
            LastChangedUtc = nowUtc,
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
        _items[Key(state.Kind, state.Id)] = released with
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
            $"AI_SYSTEM_FEEDBACK_REJECT kind={state.Kind} id={state.Id} " +
            $"reason={reason} learnedFailures={failedBenefitTrials} retryHours={recoveryCooldown.TotalHours:0}");
        await Task.CompletedTask;
    }

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

                AiSystemRuntimeItemState completed = intent with
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
                    BaselineAvailableRamMiB = baselineAvailableRam,
                    VerificationMemorySamples = 0,
                    VerificationMemoryTotalMiB = 0,
                    VerificationMemoryMinMiB = null,
                    VerificationMemoryMaxMiB = null,
                    VerificationLastMemorySnapshotUtc = null,
                    CandidateMemorySamples = 0,
                    CandidateAvailableRamMiB = null,
                    CandidateMemoryMinMiB = null,
                    CandidateMemoryMaxMiB = null,
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

    private async Task EvaluateTasksAsync(
        AiSystemRuntimeConfig config,
        bool quietEnough,
        bool fullDiscovery,
        AdaptivePowerRuntimeStatus? candidatePowerStatus,
        CancellationToken token)
    {
        DateTime nowUtc = DateTime.UtcNow;
        // Allocate the COM request list only after a qualifying task is
        // found. A fast foreground check with no AI owner needs zero COM.
        List<string>? candidates = null;
        bool fastRecoveryOnly = !quietEnough && !fullDiscovery;
        for (int i = 0; i < TaskAllowlist.Length; i++)
        {
            string path = TaskAllowlist[i];
            AiSystemRuntimeItemState state;
            if (fastRecoveryOnly)
            {
                // No recorded owner/observation/pending mutation: don't create
                // a dummy journal row or a freshly allocated task|path key.
                if (!_items.TryGetValue(TaskAllowlistKeys[i], out state))
                    continue;
            }
            else
                state = GetState("task", path);
            if (AiMaintenanceWriteAheadPolicy.IsUnownedPending(state))
            {
                _recoverInterruptedTrials = true;
                SetBlocked("task", path,
                    "pending journal without confirmed AI ownership; no Windows write");
                continue;
            }
            if (AiMaintenanceCandidatePolicy.ShouldDeferForCooldown(state, nowUtc))
                continue;
            if (!state.Managed && state.Rollbacks >= 2)
                continue;
            if (!AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(
                    quietEnough, fullDiscovery, state))
            {
                var suspended =
                    AiMaintenanceSchedulingPolicy.SuspendUnownedTaskObservation(state);
                if (!ReferenceEquals(suspended, state))
                    _items[TaskAllowlistKeys[i]] = suspended;
                continue;
            }
            (candidates ??= new List<string>(
                fastRecoveryOnly ? 4 : TaskAllowlist.Length)).Add(path);
        }

        if (candidates is null)
            return;

        IReadOnlyDictionary<string, TaskRuntimeState> taskStates;
        var unreadableTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            taskStates = TaskSchedulerComService.GetRuntimeStates(candidates, token, unreadableTasks);
        }
        catch (Exception ex)
        {
            _lastError = "Task Scheduler batch snapshot: " + ex.GetBaseException().Message;
            LogService.Error("ai-system", "AI System Runtime task batch snapshot failed", ex);
            return;
        }

        // Fast foreground recovery must never build/rank the full 28-task
        // opportunity array merely to check one owned Task Scheduler row.
        // Full discovery and quiet-window A/B/A preserve measured ranking.
        int[]? taskOrder = null;
        string[]? installedTaskCandidates = null;
        if (!fastRecoveryOnly)
        {
            // Task Scheduler COM, not a hardcoded 2019/2020 task list,
            // decides what can be considered for a NEW experiment. The
            // original IDs stay available for owned/pending recovery.
            installedTaskCandidates = WindowsComponentCatalogPolicy.PresentInSnapshot(
                TaskAllowlist,
                new HashSet<string>(taskStates.Keys, StringComparer.OrdinalIgnoreCase));
            int taskStart = _taskCandidateCursor;
            _taskCandidateCursor = AiMaintenanceCandidatePolicy.NextCursor(
                taskStart, installedTaskCandidates.Length);
            taskOrder = AiMaintenanceOpportunityPolicy.OrderedIndices(
                "task", installedTaskCandidates, taskStart, _items, DateTime.UtcNow);
        }
        int candidateCount = fastRecoveryOnly ? candidates.Count : installedTaskCandidates!.Length;
        for (int offset = 0; offset < candidateCount; offset++)
        {
            string path = fastRecoveryOnly
                ? candidates[offset] : installedTaskCandidates![taskOrder![offset]];
            token.ThrowIfCancellationRequested();
            if (!path.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase))
            {
                SetBlocked("task", path, "není Windows systémový task");
                continue;
            }

            AiSystemRuntimeItemState state = GetState("task", path);
            if (AiMaintenanceCandidatePolicy.ShouldDeferForCooldown(state, DateTime.UtcNow))
                continue;
            if (!state.Managed && state.Rollbacks >= 2)
            {
                SetBlocked("task", path, "AI learned protection • opakovaně obnoveno systémem");
                continue;
            }

            // Never label unqueried fast-scan candidates as not installed.
            // Only owned/pending entries were sent to Task Scheduler COM.
            if (!AiMaintenanceSchedulingPolicy.NeedsFastTaskRuntimeRead(
                    quietEnough, fullDiscovery, state))
                continue;
            if (!taskStates.TryGetValue(path, out TaskRuntimeState taskState))
            {
                // Absence from Task Scheduler and failure to read a task are
                // distinct outcomes; never claim absent on an access error.
                SetBlocked("task", path, unreadableTasks.Contains(path)
                    ? "task status unreadable" : "not installed");
                continue;
            }
            bool enabled = taskState.Enabled;

            if (state.Managed && state.Sleeping && enabled)
            {
                int rollbacks = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(
                    state.Rollbacks, externallyNeeded: true);
                // An external task re-enable disproves a presently safe
                // disabled hold; old measured savings must not boost its
                // ranking when cooldown expires.
                _items[Key("task", path)] =
                    AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                        AiMaintenanceOwnershipPolicy.Relinquish(
                            state, DateTime.UtcNow,
                            "external task enable; AI yielded",
                            rollbacks >= 2 ? TimeSpan.FromHours(6)
                                : TimeSpan.FromMinutes(30)),
                        externallyNeeded: true) with
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

            // A previously disabled task running on demand is a genuine
            // demand signal even if COM still reports Enabled=false.
            if (AiManagedTaskDemandPolicy.RequiresImmediateRestore(state, taskState))
            {
                RestoreManagedTaskForSchedule(state,
                    "task executed while AI-disabled", DateTime.UtcNow, token,
                    externallyNeeded: true);
                continue;
            }

            if (state.Managed && state.Sleeping && !enabled &&
                AiManagedTaskHoldPolicy.ReleaseReason(state, DateTime.UtcNow) is string releaseReason)
            {
                RestoreManagedTaskForSchedule(state, releaseReason, DateTime.UtcNow, token);
                continue;
            }

            if (!quietEnough || !enabled)
            {
                ResetObservation("task", path);
                continue;
            }

            // Use the same policy at initial eligibility and final A/B/A
            // readback. A task that just became overdue, or has future
            // last-run metadata, must not be disabled on stale assumptions.
            string? unsafeTask = AiMaintenanceRevalidationPolicy.TaskReapplyBlockReason(
                taskState, nowUtc);
            if (unsafeTask != null)
            {
                SetBlocked("task", path, "task is active or scheduled: " + unsafeTask);
                continue;
            }

            DateTime observationNow = DateTime.UtcNow;
            DateTime observed = AiMaintenanceOpportunityPolicy.CandidateObservationStart(
                observationNow, _quietEpochStartUtc ?? observationNow,
                state.ObservationSinceUtc, config.ObservationSeconds, "task", state);
            state = state with { ObservationSinceUtc = observed, BlockedReason = string.Empty };
            _items[Key("task", path)] = state;
            if (observationNow - observed < TimeSpan.FromSeconds(AiMaintenanceOpportunityPolicy.CandidateObservationSeconds(
                    config.ObservationSeconds, "task", state, observationNow)))
                continue;
            if (_maintenanceMutationCommitted)
                continue;

            double baselineLoad = AiBackgroundPerformancePolicy.MedianLoad(_quietLoadSamples);
            if (!double.IsFinite(baselineLoad) || baselineLoad < 0.75)
            {
                SetBlocked("task", path, "baseline výkonu není dostatečně stabilní");
                continue;
            }

            // Scheduled task state can change between batch COM snapshot and
            // the observation deadline. Probe live state at the mutation
            // boundary; never disable a task that started or is now due.
            TaskRuntimeState freshTask = TaskSchedulerComService.GetRuntimeState(path, token);
            string? finalTaskBlock = AiMaintenanceRevalidationPolicy.TaskReapplyBlockReason(
                freshTask, DateTime.UtcNow);
            if (finalTaskBlock != null)
            {
                SetBlocked("task", path, "revalidated before AI trial: " + finalTaskBlock);
                continue;
            }

            // Batch COM + per-task readback may be slow. Do not use old
            // file telemetry to allow a new task mutation.
            DateTime trialNow = DateTime.UtcNow;
            AiSystemSmoothnessHealth? trialSnapshot =
                AiSystemSmoothnessGuardService.ReadAgentSnapshot(trialNow);
            if (!AiMaintenanceBoundaryEvidencePolicy.CanStartNewTrial(
                    candidatePowerStatus, trialNow,
                    AiSystemSmoothnessGuardService.AllowsBackgroundExperiment(
                        trialSnapshot, trialNow), _recoverInterruptedTrials))
            {
                SetBlocked("task", path,
                    "fresh AI CPU/RAM/disk quiet evidence lost before task mutation");
                continue;
            }
            double? baselineAvailableRam = AiPassiveMemoryTrialPolicy.FreshAvailableMiB(
                trialSnapshot, trialNow, null, null);
            // An event/logon/idle-triggered task may have no meaningful
            // NextRunTime. Never disable it based on a quiet clock gap.
            if (AiTaskTriggerSafetyPolicy.BlockReason(
                    TaskSchedulerComService.TryGetTaskXml(path),
                    freshTask.NextRunUtc, DateTime.UtcNow) is string triggerBlock)
            {
                SetBlocked("task", path, "task trigger safety: " + triggerBlock);
                continue;
            }

            AiSystemRuntimeItemState intent = state with
            {
                Managed = true,
                Sleeping = true,
                OriginalTaskEnabled = true,
                OriginalTaskNextRunUtc = freshTask.NextRunUtc,
                VerificationStartedUtc = DateTime.UtcNow,
                VerificationPhase = "CandidateB",
                PerformanceVerified = false,
                PendingMutation = AiMaintenanceWriteAheadPolicy.TaskDisable
            };
            BeginWriteAheadMutation(state, intent);

            TaskSchedulerComService.SetEnabled(path, false, token);
            TaskRuntimeState afterDisable =
                TaskSchedulerComService.GetRuntimeState(path, token);
            if (AiMaintenanceRevalidationPolicy.TaskDisableReadbackReason(
                    freshTask, afterDisable) is string readbackFailure)
                throw new InvalidOperationException(
                    "initial Candidate B task readback: " + readbackFailure);

            AiSystemRuntimeItemState completed = intent with
            {
                Managed = true,
                Sleeping = true,
                OriginalTaskEnabled = true,
                OriginalTaskNextRunUtc = freshTask.NextRunUtc,
                ObservationSinceUtc = null,
                CooldownUntilUtc = DateTime.UtcNow.AddSeconds(config.ActionCooldownSeconds),
                LastAction = "candidate trial: waiting for measured feedback",
                LastChangedUtc = DateTime.UtcNow,
                BaselineLoadPercent = baselineLoad,
                BaselineAvailableRamMiB = baselineAvailableRam,
                VerificationMemorySamples = 0,
                VerificationMemoryTotalMiB = 0,
                VerificationMemoryMinMiB = null,
                VerificationMemoryMaxMiB = null,
                VerificationLastMemorySnapshotUtc = null,
                CandidateMemorySamples = 0,
                CandidateAvailableRamMiB = null,
                CandidateMemoryMinMiB = null,
                CandidateMemoryMaxMiB = null,
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
            CommitWriteAheadMutation(intent, completed);
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
            await _tweakRuntime.RestoreAllAsync(token).ConfigureAwait(false);
        await _securityRuntime.RestoreAllAsync(token).ConfigureAwait(false);
        await AdaptivePowerRuntimeService.RestoreAllAiSettingsAsync(token).ConfigureAwait(false);
        await RestoreKindAsync("service", token).ConfigureAwait(false);
        await RestoreKindAsync("task", token).ConfigureAwait(false);
        await RestoreKindAsync("driver", token).ConfigureAwait(false);
        _lastAction = "restore all";
    }

    private async Task RestoreKindAsync(string kind, CancellationToken token)
    {
        IReadOnlyList<KeyValuePair<string, AiSystemRuntimeItemState>> owned =
            AiPendingTrialSelectionPolicy.SnapshotManagedKind(_items, kind);
        for (int index = 0; index < owned.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var pair = owned[index];
            var state = pair.Value;
            try
            {
                if (kind == "service")
                {
                    int liveStart = ReadServiceStartType(state.Id);
                    if (!AiMaintenanceOwnershipPolicy.ServiceStartStillOwned(
                            state.OriginalServiceStart, liveStart, state.VerificationPhase))
                    {
                        _items[pair.Key] = AiMaintenanceOwnershipPolicy.Relinquish(state,
                            DateTime.UtcNow, "external service start mode preserved during restore",
                            TimeSpan.FromHours(12));
                        AppendAction("service", state.Id, "external override", "restore all yielded");
                        continue;
                    }

                    using var controller = new ServiceController(state.Id);
                    await RestoreServiceAsync(state, controller, "restore all", token,
                        externallyNeeded: false).ConfigureAwait(false);
                }
                else if (kind == "task")
                {
                    if (state.OriginalTaskEnabled != true)
                    {
                        _items[pair.Key] = AiMaintenanceOwnershipPolicy.Relinquish(state,
                            DateTime.UtcNow, "task original enabled state not AI-owned",
                            TimeSpan.FromHours(12));
                        continue;
                    }

                    TaskRuntimeState current = TaskSchedulerComService.GetRuntimeState(state.Id, token);
                    if (current.Enabled)
                    {
                        _items[pair.Key] = AiMaintenanceOwnershipPolicy.Relinquish(state,
                            DateTime.UtcNow, "task already enabled outside AI", TimeSpan.FromHours(12));
                        continue;
                    }

                    TaskSchedulerComService.SetEnabled(state.Id, true, token);
                    if (!TaskSchedulerComService.GetRuntimeState(state.Id, token).Enabled)
                        throw new InvalidOperationException("AI task restore readback did not confirm enabled.");

                    _items[pair.Key] = AiMaintenanceOwnershipPolicy.Relinquish(state,
                        DateTime.UtcNow, "restored by AI", TimeSpan.FromMinutes(10));
                    _restoredCount++;
                    AppendAction("task", state.Id, "restored", "restore all");
                }
                else if (kind == "driver")
                {
                    int liveStart = ReadServiceStartType(state.Id);
                    if (!AiMaintenanceOwnershipPolicy.DriverStartStillOwned(
                            state.OriginalServiceStart, liveStart))
                    {
                        string reason = AiMaintenanceOwnershipPolicy.DriverAlreadyRestored(
                            state.OriginalServiceStart, liveStart)
                            ? "driver already restored outside AI"
                            : "external driver start mode preserved during restore";
                        _items[pair.Key] = AiMaintenanceOwnershipPolicy.Relinquish(state,
                            DateTime.UtcNow, reason, TimeSpan.FromHours(12));
                        AppendAction("driver", state.Id, "external override", reason);
                        continue;
                    }

                    // Restore all runs outside the candidate observation loop;
                    // ownership of Start=4 alone does not prove this is still
                    // the SAME optional Windows driver that AI disabled.
                    DriverRegistrySnapshot restoreDriver = ReadDriverRegistrySnapshot(state.Id);
                    if (!CanRestoreDriverProvenance(
                            restoreDriver.Installed, restoreDriver.WindowsOwned,
                            restoreDriver.Type, restoreDriver.Start, liveStart))
                    {
                        SetBlocked("driver", state.Id,
                            "obnova ovladače blokována: identita se změnila; ruční kontrola nutná, AI záznam zachován");
                        continue;
                    }
                    WriteServiceStartType(state.Id, state.OriginalServiceStart!.Value);
                    int confirmedStart = ReadServiceStartType(state.Id);
                    if (AiMaintenanceWriteAheadPolicy.DriverRestoreReadbackReason(
                            state.OriginalServiceStart, confirmedStart) is string driverFailure)
                        throw new InvalidOperationException(
                            "restore all driver readback: " + driverFailure);
                    _items[pair.Key] = AiMaintenanceOwnershipPolicy.Relinquish(state,
                        DateTime.UtcNow, "driver start restored", TimeSpan.FromMinutes(10));
                    _restoredCount++;
                    AppendAction("driver", state.Id, "restored", "restore all");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // A cancelled restore is incomplete, never an implicit
                // success. The enclosing user action persists partial
                // progress in its finally block before reporting failure.
                throw;
            }
            catch (Exception ex)
            {
                _lastError = ex.GetBaseException().Message;
                LogService.Error("ai-system",
                    $"Restore {kind}/{state.Id} failed; ownership retained", ex);
            }
        }
    }

    // Every path that may re-enable a previously AI-owned service (manual
    // Restore all, crash recovery, A/B/A rollback and Reference A) must check
    // CURRENT image and ServiceDll provenance, not the cached candidate gate.
    // If provenance changed, keep the durable journal for manual recovery.
    private void RequireFreshServiceIdentityForRestore(AiSystemRuntimeItemState state)
    {
        if (IsWindowsOwnedServiceEntry(state.Id, requireFresh: true)) return;
        const string reason = "obnova blokována: změněná/neověřená cesta služby nebo ServiceDll; ruční obnova nutná, AI záznam zachován";
        SetBlocked("service", state.Id, reason);
        throw new InvalidOperationException(reason);
    }

    internal static bool CanRestoreDriverProvenance(
        bool installed, bool windowsOwned, int type, int snapshotStart, int liveStart) =>
        installed && windowsOwned &&
        AiDriverAutomationPolicy.IsEligibleDriverType(type) &&
        snapshotStart == liveStart && liveStart == 4;

    private async Task RestoreServiceAsync(
        AiSystemRuntimeItemState state,
        ServiceController controller,
        string reason,
        CancellationToken token,
        bool externallyNeeded = true)
    {
        // A stop/restart rollback must never launch a now-untrusted image.
        RequireFreshServiceIdentityForRestore(state);
        // Final readback even on recovery paths: a concurrent manual or OS
        // Start-type override must never be reverted by an old AI journal.
        int liveStart = ReadServiceStartType(state.Id);
        string restoreMode = AiVerifiedServiceHoldPolicy.WakeupDecision(
            state.OriginalServiceStart, liveStart);
        if (restoreMode == "external")
            throw new InvalidOperationException(
                "service start mode no longer owned by AI during restore");
        if (AiVerifiedServiceHoldPolicy.RequiresRestoreStartWrite(
                state.OriginalServiceStart, liveStart))
            WriteServiceStartType(state.Id, state.OriginalServiceStart!.Value);

        controller.Refresh();
        if (state.WasRunningBefore && controller.Status == ServiceControllerStatus.Stopped)
        {
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
        }

        // RestoreAll, service wakeup and crash recovery ALL use this method.
        // A successful SCM command is not proof that Windows retained the
        // original Start mode or that a previously running service restarted.
        controller.Refresh();
        int confirmedOriginalStart = ReadServiceStartType(state.Id);
        if (AiMaintenanceReferenceTransitionPolicy.ServiceRestoreReadbackReason(
                state.OriginalServiceStart, confirmedOriginalStart,
                state.WasRunningBefore, controller.Status)
            is string restoreReadbackFailure)
            throw new InvalidOperationException(
                "AI common service restore readback: " + restoreReadbackFailure);

        int rollbacks = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(state.Rollbacks, externallyNeeded);
        TimeSpan nextCooldown = AiMaintenanceCandidatePolicy.RestoreCooldown(rollbacks, externallyNeeded);
        DateTime restoredAt = DateTime.UtcNow;
        AiSystemRuntimeItemState releasedEvidence =
            AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                AiMaintenanceOwnershipPolicy.Relinquish(
                    state, restoredAt, "restored: " + reason, nextCooldown),
                externallyNeeded);
        _items[Key("service", state.Id)] = releasedEvidence with
        {
            Rollbacks = rollbacks,
            BlockedReason = externallyNeeded && rollbacks >= 2 ? "opakovaně obnoveno systémem / pravděpodobně potřebné" : string.Empty,
            CooldownUntilUtc = DateTime.UtcNow.Add(nextCooldown),
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

    internal static bool IsCriticalService(string name)
    {
        if (CriticalServiceDenylist.Contains(name)) return true;
        // Windows service identities are normally ASCII. Compare the
        // denylist fragments without allocating a lowercase copy or LINQ
        // predicate on every candidate admission check. Preserve exact
        // legacy invariant-lowercase semantics for non-ASCII imports.
        if (name.AsSpan().IndexOfAnyExceptInRange('\0', '\x7f') < 0)
        {
            foreach (string fragment in CriticalServiceFragments)
                if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        string lower = name.ToLowerInvariant();
        return CriticalServiceFragments.Any(lower.Contains);
    }

    // A fast foreground pass only needs current managed-service status and
    // the short-cadence Wi-Fi/print demand evidence. The periodic discovery
    // still uses GetServices for the complete SCM + per-user inventory.
    private static readonly string[] FastDemandServiceNames =
    {
        "WlanSvc", "Spooler", "PrintNotify",
        "PrintDeviceConfigurationService", "PrintScanBrokerService"
    };

    internal static IReadOnlyDictionary<string, ServiceControllerStatus>
        GetFastServiceStatusSnapshot(IReadOnlyList<string>? managedServices,
            ISet<string>? unreadableNames = null)
    {
        var states = new Dictionary<string, ServiceControllerStatus>(
            StringComparer.OrdinalIgnoreCase);

        static void ReadStatus(
            Dictionary<string, ServiceControllerStatus> result, string name,
            ISet<string>? unreadableNames)
        {
            // Missing/denied services have no invented Running/Stopped value;
            // failed reads are recorded separately and never authorize writes.
            if (result.ContainsKey(name))
                return;
            try
            {
                using var controller = new ServiceController(name);
                result[name] = controller.Status;
            }
            catch (Exception)
            {
                // Fast SCM status query cannot establish non-installation.
                unreadableNames?.Add(name);
            }
        }

        foreach (string name in FastDemandServiceNames)
            ReadStatus(states, name, unreadableNames);
        if (managedServices != null)
            foreach (string name in managedServices)
                ReadStatus(states, name, unreadableNames);
        return states;
    }

    private static IReadOnlyDictionary<string, ServiceControllerStatus> GetServiceStatusSnapshot(
        ISet<string>? unreadableNames = null)
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
                    // The controller exists in SCM, but reading its status
                    // failed. Keep it separate from absent service names.
                    unreadableNames?.Add(controller.ServiceName);
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
        if (_boolProbeCache.TryGetValue(cacheKey, out var cached) &&
            IsMonotonicProbeFresh(cached.CollectedStamp, Stopwatch.GetTimestamp(),
                Stopwatch.Frequency, TimeSpan.FromSeconds(30)))
            return cached.Value;

        bool running;
        try
        {
            running = AiDependentServiceProbe.HasRunningOrUnknown(
                controller.DependentServices,
                dependent => dependent.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending);
        }
        catch
        {
            running = true;
        }

        StoreBoundedProbe(_boolProbeCache, cacheKey, running, Stopwatch.GetTimestamp());
        return running;
    }

    private static DriverRegistrySnapshot ReadDriverRegistrySnapshot(string name)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name);
            if (key == null)
                return new(false, false, 0, -1, false);

            string imagePath = Convert.ToString(key.GetValue("ImagePath", string.Empty))?.Trim() ?? string.Empty;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            // A missing driver ImagePath has a Windows fallback at load time,
            // but that alone is NOT proof of origin for automatic disabling.
            // Share the conservative binary path policy with service trials.
            bool windowsOwned = !string.IsNullOrWhiteSpace(imagePath) &&
                IsWindowsKernelDriverBinaryPath(Environment.ExpandEnvironmentVariables(imagePath), windows);

            int type = Convert.ToInt32(key.GetValue("Type", 0));
            int start = AiMaintenanceServiceStartPolicy.NormalizeStartValue(
                key.GetValue("Start"));
            using RegistryKey? enumKey = key.OpenSubKey("Enum");
            bool attached = Convert.ToInt32(enumKey?.GetValue("Count", 0) ?? 0) > 0;
            return new(true, windowsOwned, type, start, attached);
        }
        catch
        {
            return new(false, false, 0, -1, true);
        }
    }

    // Path provenance is a conservative prerequisite, NOT proof of a
    // Microsoft signature. Final service mutation also checks SCM state,
    // live dependencies, service start, quiet workload and owned journal.
    // Never treat a Win32 .exe/.com as a kernel-driver image, or a .sys
    // image as a Win32 service binary. Both callers share path normalization,
    // but the last-write gate always requires the correct binary kind.
    internal static bool IsWindowsKernelDriverBinaryPath(
        string? commandLine, string? windowsDirectory) =>
        IsWindowsServiceBinaryPath(commandLine, windowsDirectory, kernelDriver: true);

    internal static bool IsWindowsServiceBinaryPath(
        string? commandLine, string? windowsDirectory, bool kernelDriver = false)
    {
        if (string.IsNullOrWhiteSpace(commandLine) ||
            string.IsNullOrWhiteSpace(windowsDirectory)) return false;
        string text = commandLine.Trim().Replace('/', '\\');
        string executable;
        bool hasArguments;
        if (text.StartsWith('"'))
        {
            int endQuote = text.IndexOf('"', 1);
            if (endQuote < 2) return false;
            executable = text.Substring(1, endQuote - 1);
            if (endQuote + 1 < text.Length && !char.IsWhiteSpace(text[endQuote + 1]))
                return false;
            hasArguments = endQuote + 1 < text.TrimEnd().Length;
        }
        else
        {
            int whitespace = text.IndexOfAny([' ', '\t', '\r', '\n']);
            executable = whitespace >= 0 ? text[..whitespace] : text;
            hasArguments = whitespace >= 0 && text[whitespace..].Length > 0;
        }
        if (executable.Length == 0 || executable.Contains('"') ||
            (kernelDriver && hasArguments) ||
            executable.Split('\\').Any(part => part is "." or ".."))
            return false;

        string windows = windowsDirectory.Trim().Replace('/', '\\').TrimEnd('\\');
        string prefix = windows + @"\System32\";
        string suffix;
        if (executable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            suffix = executable[prefix.Length..];
        else if (executable.StartsWith(@"\SystemRoot\System32\", StringComparison.OrdinalIgnoreCase))
            suffix = executable[@"\SystemRoot\System32\".Length..];
        else if (executable.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            suffix = executable[@"System32\".Length..];
        else return false;

        // Refuse drive streams, empty final filename and unexpected binaries.
        // A service that cannot be attributed safely is left untouched.
        if (suffix.Contains(':')) return false;
        string[] segments = suffix.Split('\\');
        if (segments.Any(static part => string.IsNullOrWhiteSpace(part)))
            return false;
        string filename = segments[^1];
        if (filename.Length < 5 || filename.EndsWith(' ')) return false;
        string extension = Path.GetExtension(filename);
        // Executables must be directly in System32; accepting arbitrary
        // System32 subfolders would classify untrusted Temp payloads as OS.
        if (segments.Length == 1)
            return !kernelDriver &&
                   (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".com", StringComparison.OrdinalIgnoreCase));
        // Optional kernel drivers must reside directly in System32\drivers,
        // and a Win32 service cannot pass with a driver .sys image.
        return kernelDriver && segments.Length == 2 &&
               segments[0].Equals("drivers", StringComparison.OrdinalIgnoreCase) &&
               extension.Equals(".sys", StringComparison.OrdinalIgnoreCase);
    }

    // Only own-process (0x10) and shared-process (0x20) Win32 services are
    // candidates. A kernel driver must use the separate driver-specific gate.
    internal static bool IsEligibleWin32ServiceType(int type) => type is 0x10 or 0x20;

    // svchost.exe is only a host; its Parameters\\ServiceDll is the code it
    // actually loads. Keep this conservative and limited to a direct
    // System32 DLL (no Temp, nested folders, ADS, traversal or arguments).
    internal static bool IsWindowsServiceDllPath(string? rawPath, string? windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawPath) ||
            string.IsNullOrWhiteSpace(windowsDirectory)) return false;
        string path = rawPath.Trim().Replace('/', '\\');
        if (path.StartsWith('"'))
        {
            if (path.Length < 3 || path[^1] != '"') return false;
            path = path[1..^1];
        }
        if (path.IndexOfAny(['"', ' ', '\t', '\r', '\n', ':']) >= 0)
        {
            // A drive colon is valid only in the canonical drive prefix.
            bool drivePrefix = path.Length > 3 && path[1] == ':' &&
                char.IsLetter(path[0]) && path[2] == '\\';
            if (!drivePrefix || path[3..].IndexOfAny(['"', ' ', '\t', '\r', '\n', ':']) >= 0)
                return false;
        }
        if (path.Split('\\').Any(part => part is "" or "." or ".."))
        {
            // NT \\SystemRoot begins with one separator, which is expected.
            if (!path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase) ||
                path[1..].Split('\\').Any(part => part is "" or "." or ".."))
                return false;
        }
        string root = windowsDirectory.Trim().Replace('/', '\\').TrimEnd('\\');
        string prefix = root + @"\System32\";
        string dllName;
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            dllName = path[prefix.Length..];
        else if (path.StartsWith(@"\SystemRoot\System32\", StringComparison.OrdinalIgnoreCase))
            dllName = path[@"\SystemRoot\System32\".Length..];
        else if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            dllName = path[@"System32\".Length..];
        else return false;
        return dllName.Length > 4 && !dllName.Contains('\\') &&
            dllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            !dllName.EndsWith(" .dll", StringComparison.OrdinalIgnoreCase);
    }

    // Parse ONLY the executable token, not the arguments. A service with
    // "svchost.exe" inside an unrelated flag must not be misclassified as
    // the svchost host, nor may an argument masquerade as an unsupported
    // DLL loader. The path admission guard separately validates location.
    internal static string GetServiceExecutableName(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return string.Empty;
        string text = imagePath.Trim().Replace('/', '\\');
        string executable;
        if (text.StartsWith('"'))
        {
            int closing = text.IndexOf('"', 1);
            if (closing <= 1 ||
                (closing + 1 < text.Length && !char.IsWhiteSpace(text[closing + 1])))
                return string.Empty;
            executable = text[1..closing];
        }
        else
        {
            int boundary = text.IndexOfAny([' ', '\t', '\r', '\n']);
            executable = boundary < 0 ? text : text[..boundary];
        }
        return executable.Contains('"') ? string.Empty : Path.GetFileName(executable);
    }

    internal static bool RequiresServiceDllReadback(string imagePath) =>
        string.Equals(GetServiceExecutableName(imagePath), "svchost.exe",
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnsupportedDllLauncher(string imagePath)
    {
        string fileName = GetServiceExecutableName(imagePath);
        return fileName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("dllhost.exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsWindowsServiceAdmissionPath(
        string imagePath, string? serviceDll, string windowsDirectory, int serviceType) =>
        IsEligibleWin32ServiceType(serviceType) &&
        IsWindowsServiceBinaryPath(imagePath, windowsDirectory) &&
        !IsUnsupportedDllLauncher(imagePath) &&
        (!RequiresServiceDllReadback(imagePath) ||
         IsWindowsServiceDllPath(serviceDll, windowsDirectory));

    internal static string RunServiceModuleAdmissionSmoke()
    {
        const string root = @"C:\Windows";
        const string svchost = @"C:\Windows\System32\svchost.exe -k netsvcs";
        const string normal = @"C:\Windows\System32\ExampleService.exe";
        const string goodDll = @"C:\Windows\System32\ExampleService.dll";
        // Named assertions make the first rejected/unsafe case actionable
        // without weakening the regression or touching real service state.
        var cases = new (string Label, bool Expected, bool Actual)[]
        {
            ("valid svchost", true, IsWindowsServiceAdmissionPath(svchost, goodDll, root, 0x20)),
            ("valid NT DLL", true, IsWindowsServiceAdmissionPath(svchost,
                @"\SystemRoot\System32\ExampleService.dll", root, 0x20)),
            ("own process", true, IsWindowsServiceAdmissionPath(normal, null, root, 0x10)),
            ("shared exe", true, IsWindowsServiceAdmissionPath(normal, null, root, 0x20)),
            ("quoted svchost executable", true, IsWindowsServiceAdmissionPath(
                "\"C:\\Windows\\System32\\svchost.exe\" -k netsvcs", goodDll, root, 0x20)),
            ("svchost word only in arguments", true, IsWindowsServiceAdmissionPath(
                normal + " --description=svchost.exe", null, root, 0x10)),
            ("loader word only in arguments", true, IsWindowsServiceAdmissionPath(
                normal + " --description=rundll32.exe", null, root, 0x10)),
            ("quoted regular executable", true, IsWindowsServiceAdmissionPath(
                "\"C:\\Windows\\System32\\ExampleService.exe\" --note=dllhost.exe",
                null, root, 0x10)),
            ("empty DLL", false, IsWindowsServiceAdmissionPath(svchost, string.Empty, root, 0x20)),
            ("driver as regular executable", false, IsWindowsServiceAdmissionPath(normal, null, root, 1)),
            ("missing DLL", false, IsWindowsServiceAdmissionPath(svchost, null, root, 0x20)),
            ("temp payload", false, IsWindowsServiceAdmissionPath(svchost,
                @"C:\Windows\Temp\hijack.dll", root, 0x20)),
            ("nested payload", false, IsWindowsServiceAdmissionPath(svchost,
                @"C:\Windows\System32\Temp\hijack.dll", root, 0x20)),
            ("module traversal", false, IsWindowsServiceAdmissionPath(svchost,
                @"C:\Windows\System32\..\Temp\hijack.dll", root, 0x20)),
            ("alternate stream", false, IsWindowsServiceAdmissionPath(svchost,
                @"C:\Windows\System32\hijack.dll:alternate", root, 0x20)),
            ("wrong extension", false, IsWindowsServiceAdmissionPath(svchost,
                @"C:\Windows\System32\hijack.exe", root, 0x20)),
            ("DLL arguments", false, IsWindowsServiceAdmissionPath(svchost,
                @"C:\Windows\System32\hijack.dll -x", root, 0x20)),
            ("driver type", false, IsWindowsServiceAdmissionPath(svchost, goodDll, root, 1)),
            ("rundll32", false, IsWindowsServiceAdmissionPath(
                @"C:\Windows\System32\rundll32.exe payload.dll,X", null, root, 0x10)),
            ("dllhost", false, IsWindowsServiceAdmissionPath(
                @"C:\Windows\System32\dllhost.exe /Processid:123", null, root, 0x10))
        };
        foreach (var test in cases)
            if (test.Actual != test.Expected)
                throw new InvalidOperationException(
                    $"AI service module {test.Label}: expected={test.Expected}, actual={test.Actual}");
        return "AI_SERVICE_MODULE_ADMISSION_SMOKE_OK hostDllMandatory=true " +
               "injectionBlocked=true serviceTypes=true windowsWrites=0";
    }

    private bool IsWindowsOwnedServiceEntry(string name, bool requireFresh = false)
    {
        string cacheKey = "windows-owned|" + name;
        if (MayReuseProbeCache(requireFresh) &&
            _boolProbeCache.TryGetValue(cacheKey, out var cached) &&
            IsMonotonicProbeFresh(cached.CollectedStamp, Stopwatch.GetTimestamp(),
                Stopwatch.Frequency, TimeSpan.FromMinutes(10)))
            return cached.Value;

        bool owned = false;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + name);
            if (key != null)
            {
                string imagePath = Convert.ToString(key.GetValue("ImagePath", string.Empty))?.Trim() ?? string.Empty;
                // Owning a service is not proven by merely living somewhere
                // under C:\Windows. At the last write boundary, require a
                // plausible System32 binary path and reject path traversal.
                // Blank ImagePath is not sufficient authorization to stop a
                // service, even when its name matches a known driver.
                if (!string.IsNullOrWhiteSpace(imagePath))
                {
                    string expanded = Environment.ExpandEnvironmentVariables(imagePath);
                    string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    int serviceType = Convert.ToInt32(key.GetValue("Type", 0));
                    string library = string.Empty;
                    if (RequiresServiceDllReadback(expanded))
                    {
                        using RegistryKey? parameters = key.OpenSubKey("Parameters");
                        library = Environment.ExpandEnvironmentVariables(
                            Convert.ToString(parameters?.GetValue(
                                "ServiceDll", string.Empty))?.Trim() ?? string.Empty);
                    }
                    owned = IsWindowsServiceAdmissionPath(
                        expanded, library, windows, serviceType);
                }
            }
        }
        catch
        {
            owned = false;
        }

        StoreBoundedProbe(_boolProbeCache, cacheKey, owned, Stopwatch.GetTimestamp());
        return owned;
    }

    private static int ReadServiceStartType(string name)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\" + name);
        return AiMaintenanceServiceStartPolicy.NormalizeStartValue(
            key?.GetValue("Start"));
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

    private void EvaluateSafeDrivers(
        AiSystemRuntimeConfig config, bool quietEnough,
        AdaptivePowerRuntimeStatus? candidatePowerStatus)
    {
        int driverStart = _driverCandidateCursor;
        _driverCandidateCursor = AiMaintenanceCandidatePolicy.NextCursor(
            driverStart, DriverAllowlist.Length);
        for (int offset = 0; offset < DriverAllowlist.Length; offset++)
        {
            int index = AiMaintenanceCandidatePolicy.IndexAt(
                driverStart, offset, DriverAllowlist.Length);
            string name = DriverAllowlist[index];
            // A driver absent from the current Services registry is not a
            // valid NEW experiment. Keep any old owned/pending journal item
            // on its original recovery path instead of deleting history.
            if (!WindowsComponentCatalogPolicy.DriverRegistered(name) &&
                (!_items.TryGetValue(DriverAllowlistKeys[index], out var tracked) ||
                 (!tracked.Managed && !tracked.Sleeping &&
                  string.IsNullOrEmpty(tracked.PendingMutation))))
                continue;
            AiSystemRuntimeItemState state;
            if (!quietEnough)
            {
                // No durable owner/observation: don't fill the journal
                // with dummy driver rows while user activity forbids trials.
                if (!_items.TryGetValue(DriverAllowlistKeys[index], out state))
                    continue;
            }
            else
                state = GetState("driver", name);
            if (AiMaintenanceWriteAheadPolicy.IsUnownedPending(state))
            {
                _recoverInterruptedTrials = true;
                SetBlocked("driver", name,
                    "pending journal without confirmed AI ownership; no Windows write");
                continue;
            }
            if (AiMaintenanceCandidatePolicy.ShouldDeferForCooldown(state, DateTime.UtcNow))
                continue;
            if (!state.Managed && state.Rollbacks >= 2)
            {
                SetBlocked("driver", name, "AI learned protection • opakovaně obnoveno systémem");
                continue;
            }

            // During foreground work the AI can neither start nor continue
            // unowned driver-disabling observations. Avoid a native registry
            // + Enum probe for every unowned candidate. Managed drivers still
            // ALWAYS reach the live readback/restore branches below.
            if (!AiMaintenanceSchedulingPolicy.NeedsDriverRegistryRead(
                    quietEnough, state.Managed, state.Sleeping,
                    !string.IsNullOrEmpty(state.PendingMutation)))
            {
                AiSystemRuntimeItemState suspended =
                    AiMaintenanceSchedulingPolicy.SuspendUnownedDriverObservation(state);
                if (!ReferenceEquals(state, suspended))
                    _items[Key("driver", name)] = suspended;
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
                    // A tightened image-path policy must not erase older
                    // durable AI ownership. Block new writes and flag owned
                    // disabled drivers for manual recovery when untrusted.
                    SetBlocked("driver", name, state.Managed && state.Sleeping
                        ? "cesta spravovaného ovladače se změnila; ruční obnova nutná, AI záznam zachován"
                        : "není potvrzený Windows/System32 driver");
                    continue;
                }

                if (!AiDriverAutomationPolicy.IsEligibleDriverType(driver.Type))
                {
                    SetBlocked("driver", name, "jen volitelné kernel ovladače (Type=1)");
                    continue;
                }

                int start = driver.Start;
                if (state.Managed && state.Sleeping &&
                    AiMaintenanceServiceStartPolicy.MustRetainOwnerOnUnknownStart(start))
                {
                    SetBlocked("driver", name,
                        "driver Start unreadable; retain original AI disable ownership");
                    continue;
                }

                if (state.Managed && state.Sleeping && start == 4 &&
                    !driver.AttachedDevice &&
                    AiDriverAutomationPolicy.ShouldRelease(state, DateTime.UtcNow))
                {
                    if (state.OriginalServiceStart is int original && original == 3)
                    {
                        DriverRegistrySnapshot freshRestore = ReadDriverRegistrySnapshot(name);
                        if (!CanRestoreDriverProvenance(
                                freshRestore.Installed, freshRestore.WindowsOwned,
                                freshRestore.Type, freshRestore.Start, start))
                        {
                            SetBlocked("driver", name,
                                "obnova po expiraci blokována: identita ovladače se změnila; AI záznam zachován");
                            continue;
                        }
                        WriteServiceStartType(name, original);
                        int confirmedDriverStart = ReadServiceStartType(name);
                        if (AiMaintenanceWriteAheadPolicy.DriverRestoreReadbackReason(
                                original, confirmedDriverStart) is string restoreFailure)
                            throw new InvalidOperationException(
                                "AI driver hold-expiry readback: " + restoreFailure);
                        _items[Key("driver", name)] = AiMaintenanceOwnershipPolicy.Relinquish(
                            state, DateTime.UtcNow, "driver hold expired; original demand start restored",
                            TimeSpan.FromHours(12));
                        _restoredCount++;
                        AppendAction("driver", name, "restored", "maximum AI hold expired");
                    }
                    else
                        SetBlocked("driver", name, "missing safe original start value; manual recovery required");
                    continue;
                }

                if (state.Managed && state.Sleeping && start == 4 && driver.AttachedDevice)
                {
                    // The durable original must be known. A corrupt/legacy
                    // journal must not cause AI to invent a start value.
                    if (!AiDriverAutomationPolicy.CanRestoreAttachedDevice(state, start, true))
                    {
                        SetBlocked("driver", name,
                            "device attached but original driver start mode unknown; manual recovery required");
                        continue;
                    }
                    // A newly attached device must not override a driver whose
                    // ImagePath/Type was changed after this tick's first read.
                    DriverRegistrySnapshot freshRestore = ReadDriverRegistrySnapshot(name);
                    if (!CanRestoreDriverProvenance(
                            freshRestore.Installed, freshRestore.WindowsOwned,
                            freshRestore.Type, freshRestore.Start, start))
                    {
                        SetBlocked("driver", name,
                            "obnova připojeného ovladače blokována: identita se změnila; ruční kontrola nutná");
                        continue;
                    }
                    int restoreStart = state.OriginalServiceStart!.Value;
                    WriteServiceStartType(name, restoreStart);
                    int confirmedDriverStart = ReadServiceStartType(name);
                    if (AiMaintenanceWriteAheadPolicy.DriverRestoreReadbackReason(
                            restoreStart, confirmedDriverStart) is string restoreFailure)
                        throw new InvalidOperationException(
                            "AI reattached driver restore readback: " + restoreFailure);
                    DateTime restoredAt = DateTime.UtcNow;
                    int learned = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(
                        state.Rollbacks, externallyNeeded: true);
                    _items[Key("driver", name)] =
                        AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                            AiMaintenanceOwnershipPolicy.Relinquish(
                                state, restoredAt,
                                "device attached again; AI restored driver",
                                TimeSpan.FromHours(24)),
                            externallyNeeded: true) with
                        {
                            Rollbacks = learned,
                            LastAction = "restored because device became attached"
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
                    int rollbacks = AiMaintenanceCandidatePolicy.RollbacksAfterRestore(
                        state.Rollbacks, externallyNeeded: true);
                    DateTime relinquishedAt = DateTime.UtcNow;
                    TimeSpan externalCooldown = rollbacks >= 2
                        ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(30);
                    _items[Key("driver", name)] =
                        AiMaintenanceCandidatePolicy.AfterRestoreEvidence(
                            AiMaintenanceOwnershipPolicy.Relinquish(
                                state, relinquishedAt,
                                rollbacks >= 2
                                    ? "opakovaně obnoveno systémem / pravděpodobně potřebné"
                                    : "systém driver znovu povolil; AI předala řízení",
                                externalCooldown),
                            externallyNeeded: true) with
                        {
                            Rollbacks = rollbacks,
                            LastAction = "externally restored / assumed needed"
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

                DateTime observationNow = DateTime.UtcNow;
                DateTime observed = AiDriverAutomationPolicy.ObservationStart(
                    state.ObservationSinceUtc, state.DriverObservationLastUtc, observationNow);
                state = state with
                {
                    ObservationSinceUtc = observed,
                    DriverObservationLastUtc = observationNow,
                    BlockedReason = string.Empty
                };
                _items[Key("driver", name)] = state;
                if (DateTime.UtcNow - observed < AiDriverAutomationPolicy.RequiredObservation(config.ObservationSeconds))
                    continue;
                if (_maintenanceMutationCommitted)
                    continue;

                // The long observation is only a preliminary candidate. Read
                // driver registry/device occupancy again at the write boundary;
                // never disable after a device arrives or Windows changes Start.
                DriverRegistrySnapshot latest = ReadDriverRegistrySnapshot(name);
                DateTime trialNow = DateTime.UtcNow;
                AiSystemSmoothnessHealth? freshHealth =
                    AiSystemSmoothnessGuardService.ReadAgentSnapshot(trialNow);
                bool freshQuiet = AiMaintenanceBoundaryEvidencePolicy.CanStartNewTrial(
                    candidatePowerStatus, trialNow,
                    AiSystemSmoothnessGuardService.AllowsBackgroundExperiment(
                        freshHealth, trialNow), _recoverInterruptedTrials);
                if (AiDriverAutomationPolicy.MutationBlockReason(
                        latest.Installed, latest.WindowsOwned, latest.Type,
                        latest.Start, latest.AttachedDevice, freshQuiet)
                    is string mutationBlock)
                {
                    SetBlocked("driver", name, "write boundary: " + mutationBlock);
                    continue;
                }

                // Registry Enum Count alone cannot prove a demand-start
                // driver is inactive. Read the live Service Control Manager
                // driver state before journaling any next-boot disable.
                if (AiKernelDriverRuntimeProbe.BlockReason(name)
                    is string liveDriverBlock)
                {
                    SetBlocked("driver", name, "write boundary: " + liveDriverBlock);
                    continue;
                }

                AiSystemRuntimeItemState intent = state with
                {
                    Managed = true,
                    Sleeping = true,
                    OriginalServiceStart = start,
                    PendingMutation = AiMaintenanceWriteAheadPolicy.DriverDisable,
                    LastChangedUtc = DateTime.UtcNow
                };
                BeginWriteAheadMutation(state, intent);

                WriteServiceStartType(name, 4);
                int disabledDriverStart = ReadServiceStartType(name);
                if (AiMaintenanceWriteAheadPolicy.DriverDisableReadbackReason(
                        disabledDriverStart) is string disableFailure)
                    throw new InvalidOperationException(
                        "AI driver disable readback: " + disableFailure);

                AiSystemRuntimeItemState completed = intent with
                {
                    Managed = true,
                    Sleeping = true,
                    OriginalServiceStart = start,
                    ObservationSinceUtc = null,
                    DriverObservationLastUtc = null,
                    CooldownUntilUtc = DateTime.UtcNow.AddSeconds(config.ActionCooldownSeconds),
                    LastAction = "disabled for next boot after long idle observation",
                    LastChangedUtc = DateTime.UtcNow,
                    PerformanceVerified = false
                };
                CommitWriteAheadMutation(intent, completed);
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
                if (AiMaintenanceWriteAheadPolicy.IsPending(GetState("driver", name)))
                    _recoverInterruptedTrials = true;
                SetBlocked("driver", name, ex.GetBaseException().Message);
            }
        }
    }

    // Runtime polling, cloud backoff and read-only discovery cadence use
    // elapsed monotonic time. A Windows/NTP clock correction must not cause
    // a burst of extra scans or premature cloud sync uploads.
    internal static bool IsWithinMonotonicInterval(
        long lastStamp, long nowStamp, long frequency, TimeSpan interval) =>
        lastStamp > 0 && nowStamp >= lastStamp && frequency > 0 &&
        interval > TimeSpan.Zero &&
        (nowStamp - (double)lastStamp) / frequency < interval.TotalSeconds;

    internal static bool ShouldRunMonotonicDeepScan(
        ref long lastStamp, long nowStamp, long frequency, TimeSpan interval)
    {
        // Invalid monotonic evidence must not cause repeated expensive
        // service/task/driver discovery on every RuntimeHost tick.
        if (nowStamp <= 0 || frequency <= 0 || interval <= TimeSpan.Zero)
            return false;
        if (lastStamp < 0 || lastStamp > nowStamp)
        {
            // A corrupt or discontinuous counter is not elapsed time.
            // Rebaseline and defer until a full real interval passes.
            lastStamp = nowStamp;
            return false;
        }
        if (IsWithinMonotonicInterval(lastStamp, nowStamp, frequency, interval))
            return false;
        lastStamp = nowStamp;
        return true;
    }

    internal static string RunMonotonicRuntimeCadenceSmoke()
    {
        const long freq = 10_000;
        const long t0 = 100_000;
        long fiveSeconds = t0 + 5 * freq;
        long fifteenMinutes = t0 + 15 * 60 * freq;
        if (!IsWithinMonotonicInterval(t0, fiveSeconds - 1, freq,
                TimeSpan.FromSeconds(5)) ||
            IsWithinMonotonicInterval(t0, fiveSeconds, freq,
                TimeSpan.FromSeconds(5)) ||
            !IsWithinMonotonicInterval(t0, fifteenMinutes - 1, freq,
                TimeSpan.FromMinutes(15)) ||
            IsWithinMonotonicInterval(t0, fifteenMinutes, freq,
                TimeSpan.FromMinutes(15)) ||
            IsWithinMonotonicInterval(t0, t0 - 1, freq, TimeSpan.FromSeconds(5)) ||
            IsWithinMonotonicInterval(0, t0, freq, TimeSpan.FromSeconds(5)) ||
            IsWithinMonotonicInterval(t0, t0, 0, TimeSpan.FromSeconds(5)) ||
            IsWithinMonotonicInterval(t0, t0, freq, TimeSpan.Zero))
            throw new InvalidOperationException("AI runtime monotonic cadence bounds failed.");
        long scan = 0;
        if (!ShouldRunMonotonicDeepScan(ref scan, t0, freq,
                TimeSpan.FromMinutes(5)) || scan != t0 ||
            ShouldRunMonotonicDeepScan(ref scan, t0 + freq * 5, freq,
                TimeSpan.FromMinutes(5)) ||
            !ShouldRunMonotonicDeepScan(ref scan, t0 + freq * 301, freq,
                TimeSpan.FromMinutes(5)) || scan != t0 + freq * 301)
            throw new InvalidOperationException("AI deep-scan monotonic scheduling failed.");
        long validStamp = scan;
        if (ShouldRunMonotonicDeepScan(ref scan, 0, freq, TimeSpan.FromMinutes(5)) ||
            ShouldRunMonotonicDeepScan(ref scan, -1, freq, TimeSpan.FromMinutes(5)) ||
            ShouldRunMonotonicDeepScan(ref scan, validStamp + freq, 0, TimeSpan.FromMinutes(5)) ||
            ShouldRunMonotonicDeepScan(ref scan, validStamp + freq, freq, TimeSpan.Zero) ||
            ShouldRunMonotonicDeepScan(ref scan, validStamp + freq, freq, TimeSpan.FromSeconds(-1)) ||
            scan != validStamp)
            throw new InvalidOperationException("AI invalid scheduler clock triggered an unexpected scan.");
        long rebaseline = t0 + freq * 301;
        if (ShouldRunMonotonicDeepScan(ref rebaseline, t0, freq, TimeSpan.FromSeconds(5)) ||
            rebaseline != t0 ||
            ShouldRunMonotonicDeepScan(ref rebaseline, t0 + 5 * freq - 1, freq,
                TimeSpan.FromSeconds(5)) ||
            !ShouldRunMonotonicDeepScan(ref rebaseline, t0 + 5 * freq, freq,
                TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("AI backward counter failed to rebaseline/defer scans.");
        long corruptStamp = -200;
        if (ShouldRunMonotonicDeepScan(ref corruptStamp, t0, freq, TimeSpan.FromSeconds(5)) ||
            corruptStamp != t0)
            throw new InvalidOperationException("AI corrupted deep-scan stamp was not quarantined.");
        // A two-hour wall-clock correction does not affect elapsed stamps.
        DateTime wall = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        if (wall.AddHours(2) <= wall ||
            !IsWithinMonotonicInterval(t0, t0 + freq * 4, freq,
                TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("AI runtime scheduler coupled to wall clock.");
        return "AI_RUNTIME_MONOTONIC_CADENCE_SMOKE_OK tick=true deepScans=true " +
               "cloud15m=true clockJumpIgnored=true windowsWrites=0";
    }

    // A wall-clock correction can place the last tick ahead of 'now'.
    // Never defer maintenance, deep scans or cloud sync until that old wall
    // clock catches up. A zero/negative interval is immediately due.
    internal static bool IsWithinRecentInterval(
        DateTime lastUtc, DateTime nowUtc, TimeSpan interval) =>
        lastUtc != DateTime.MinValue &&
        interval > TimeSpan.Zero &&
        nowUtc >= lastUtc &&
        nowUtc - lastUtc < interval;

    // Cached observations speed up inventory screens but must never become
    // an authorization token at the point of a service mutation.
    internal static bool MayReuseProbeCache(bool requireFresh) => !requireFresh;

    // Only two boolean probe families exist today. Keep a fixed memory bound:
    // forcing a fresh read when the oldest entry is evicted is safer than
    // indefinitely retaining stale classification from removed services.
    internal static void StoreBoundedProbe(
        Dictionary<string, (long CollectedStamp, bool Value)> cache,
        string key, bool value, long stamp, int maxEntries = 256)
    {
        if (maxEntries < 1 || stamp <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries),
                "Monotonic probe stamp and capacity must be positive.");
        if (!cache.ContainsKey(key) && cache.Count >= maxEntries)
        {
            string? oldestKey = null;
            long oldestStamp = long.MaxValue;
            foreach (var entry in cache)
            {
                if (entry.Value.CollectedStamp < oldestStamp)
                {
                    oldestStamp = entry.Value.CollectedStamp;
                    oldestKey = entry.Key;
                }
            }
            if (oldestKey != null) cache.Remove(oldestKey);
        }
        cache[key] = (stamp, value);
    }

    internal static bool IsMonotonicProbeFresh(
        long collectedStamp, long nowStamp, long frequency, TimeSpan ttl) =>
        collectedStamp > 0 && nowStamp >= collectedStamp && frequency > 0 &&
        ttl > TimeSpan.Zero &&
        (nowStamp - (double)collectedStamp) / frequency < ttl.TotalSeconds;

    internal static string RunMonotonicProbeCacheSmoke()
    {
        const long frequency = 10_000;
        const long stored = 100_000;
        long thirtySeconds = stored + 30 * frequency;
        long tenMinutes = stored + 10 * 60 * frequency;
        if (!IsMonotonicProbeFresh(stored, stored, frequency, TimeSpan.FromSeconds(30)) ||
            !IsMonotonicProbeFresh(stored, thirtySeconds - 1, frequency,
                TimeSpan.FromSeconds(30)) ||
            IsMonotonicProbeFresh(stored, thirtySeconds, frequency,
                TimeSpan.FromSeconds(30)) ||
            !IsMonotonicProbeFresh(stored, tenMinutes - 1, frequency,
                TimeSpan.FromMinutes(10)) ||
            IsMonotonicProbeFresh(stored, tenMinutes, frequency,
                TimeSpan.FromMinutes(10)) ||
            IsMonotonicProbeFresh(stored, stored - 1, frequency,
                TimeSpan.FromSeconds(30)) ||
            IsMonotonicProbeFresh(0, stored, frequency, TimeSpan.FromSeconds(30)) ||
            IsMonotonicProbeFresh(stored, stored, 0, TimeSpan.FromSeconds(30)) ||
            IsMonotonicProbeFresh(stored, stored, frequency, TimeSpan.Zero))
            throw new InvalidOperationException("AI service probe cache monotonic TTL regressed.");
        // A long-lived RuntimeHost must not accumulate unbounded service
        // identities. Evicting only the oldest invokes a fresh safety probe.
        var bounded = new Dictionary<string, (long CollectedStamp, bool Value)>(
            StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i <= 260; i++)
            StoreBoundedProbe(bounded, "service-" + i, i % 2 == 0, i);
        if (bounded.Count != 256 || bounded.ContainsKey("service-1") ||
            bounded.ContainsKey("service-4") || !bounded.ContainsKey("service-5") ||
            !bounded.TryGetValue("service-260", out _))
            throw new InvalidOperationException("AI service probe cache exceeded capacity or evicted a fresh entry.");
        StoreBoundedProbe(bounded, "SERVICE-260", false, 500);
        if (bounded.Count != 256 || bounded["service-260"].Value)
            throw new InvalidOperationException("AI service probe cache case-insensitive refresh failed.");
        // All three driver restore paths (manual Restore all, hold expiry,
        // device attachment) must read a fresh Type=kernel, Start=Disabled
        // and trusted path before they restore demand startup (3).
        if (!CanRestoreDriverProvenance(true, true, 1, 4, 4) ||
            CanRestoreDriverProvenance(false, true, 1, 4, 4) ||
            CanRestoreDriverProvenance(true, false, 1, 4, 4) ||
            CanRestoreDriverProvenance(true, true, 0x20, 4, 4) ||
            CanRestoreDriverProvenance(true, true, 1, 3, 4) ||
            CanRestoreDriverProvenance(true, true, 1, 4, 3) ||
            CanRestoreDriverProvenance(true, true, 1, -1, 4))
            throw new InvalidOperationException(
                "AI driver restore could modify a changed/unverified driver identity.");
        // A stored Windows-ownership positive is only advisory. Revalidation
        // at the actual mutation boundary MUST bypass even fresh entries.
        if (!MayReuseProbeCache(requireFresh: false) ||
            MayReuseProbeCache(requireFresh: true))
            throw new InvalidOperationException("AI final service identity check reused cached authorization.");
        // Only a recognizable System32 binary path can be considered for an
        // automatic trial. No filesystem/registry accesses in these cases.
        const string root = @"C:\Windows";
        if (!IsWindowsServiceBinaryPath(@"C:\Windows\System32\svchost.exe -k netsvcs", root) ||
            !IsWindowsServiceBinaryPath("\"C:\\Windows\\System32\\svchost.exe\" -k netsvcs", root) ||
            !IsWindowsKernelDriverBinaryPath(@"\SystemRoot\System32\drivers\sample.sys", root) ||
            !IsWindowsKernelDriverBinaryPath(@"System32\drivers\device.sys", root) ||
            !IsWindowsServiceBinaryPath(@"System32\svchost.exe -k local", root) ||
            !IsWindowsServiceBinaryPath(@"System32\command.com", root) ||
            IsWindowsServiceBinaryPath(@"System32\drivers\device.sys", root) ||
            IsWindowsKernelDriverBinaryPath(@"System32\svchost.exe -k local", root) ||
            IsWindowsKernelDriverBinaryPath(@"System32\command.com", root) ||
            IsWindowsKernelDriverBinaryPath(@"System32\drivers\device.sys -load", root) ||
            IsWindowsKernelDriverBinaryPath("\"System32\\drivers\\device.sys\" /flag", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\Temp\malware.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\DriverStore\malware.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\drivers\malware.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\drivers\nested\malware.sys", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\driver.sys", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\\svchost.exe", root) ||
            IsWindowsServiceBinaryPath(string.Empty, root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\Temp\untrusted.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\SysWOW64\untrusted.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\..\Temp\untrusted.exe", root) ||
            IsWindowsServiceBinaryPath(@"\SystemRoot\System32\..\Temp\untrusted.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32.evil\agent.exe", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\helper.exe:evil", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\", root) ||
            IsWindowsServiceBinaryPath("\"C:\\Windows\\System32\\svchost.exe\"junk", root) ||
            IsWindowsServiceBinaryPath(@"C:\Windows\System32\program.bat", root) ||
            IsWindowsServiceBinaryPath(null, root))
            throw new InvalidOperationException("AI service image path provenance is too permissive.");
        // A discontinuous OS clock never enters the live cached-value gate.
        DateTime utc = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        if (utc.AddHours(-2) >= utc ||
            !IsMonotonicProbeFresh(stored, stored + frequency * 3,
                frequency, TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("AI cache TTL depends on Windows clock time.");
        return "AI_PROBE_CACHE_MONOTONIC_SMOKE_OK service30s=true ownership10m=true " +
            "clockIndependent=true invalidCountersFailClosed=true boundedEntries=256 windowsWrites=0";
    }

    // Legacy wall-clock helper is retained for existing diagnostic tests;
    // the live service dependency/ownership cache never calls this path.
    internal static bool IsCachedProbeFresh(
        DateTime expiresUtc, DateTime nowUtc, TimeSpan maxLifetime)
    {
        TimeSpan remaining = expiresUtc - nowUtc;
        return maxLifetime > TimeSpan.Zero &&
            remaining > TimeSpan.Zero && remaining <= maxLifetime;
    }

    internal static bool ShouldRunDeepScan(
        ref DateTime lastScanUtc,
        DateTime nowUtc,
        TimeSpan interval)
    {
        if (IsWithinRecentInterval(lastScanUtc, nowUtc, interval))
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
            ObservationSinceUtc = null,
            DriverObservationLastUtc = null
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
            DriverObservationLastUtc = null,
            BlockedReason = string.Empty
        };
    }

    private void LoadItems()
    {
        try
        {
            // File.Exists silently returns false for some access-denied
            // conditions. Only a genuinely absent file is a first install;
            // unreadable ownership records must fail closed instead.
            try { _ = File.GetAttributes(ItemStatePath); }
            catch (FileNotFoundException)
            {
                _journalInitiallyAbsent = true;
                lock (SnapshotGate) CachedItems = Array.Empty<AiSystemRuntimeItemState>();
                return;
            }
            catch (DirectoryNotFoundException)
            {
                _journalInitiallyAbsent = true;
                lock (SnapshotGate) CachedItems = Array.Empty<AiSystemRuntimeItemState>();
                return;
            }

            // An atomic File.Move may replace the ledger between stat/read.
            // Accept only a stable, bounded file snapshot from this agent.
            if (!AiJournalSnapshotReadPolicy.TryReadStable(
                    ItemStatePath, ItemsMetadata, ItemsMetadata.Read(),
                    out AiSystemRuntimeItemState[] rows, out _))
                throw new InvalidDataException("AI ownership journal could not be read consistently and safely.");
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
                CachedItemsStamp = ItemsMetadata.Read();
                CachedItemsRejected = false;
            }
            _lastPersistJson = JsonSerializer.Serialize(snapshot);
            // Only an admissible normalized checkpoint can be fast-accepted.
            // A legacy file may decode within its read limit yet expand past
            // the durable write limit when the JSON serializer escapes it.
            if (AiWriteAheadSerializationPolicy.FitsDurableJournal(
                    _lastPersistJson.Length, snapshot.Length))
                _lastDurableJournalValues = _items.Values.ToArray();
        }
        catch (Exception ex)
        {
            // An unreadable ownership journal may contain owned Windows
            // changes. Do not silently replace it with an empty journal or
            // admit more autonomous service/task/driver experiments.
            _journalLoadHealthy = false;
            _items.Clear();
            _lastError = "AI ownership journal unreadable; Windows changes paused";
            lock (SnapshotGate) CachedItems = Array.Empty<AiSystemRuntimeItemState>();
            LogService.Error("ai-system", "AI ownership journal load failed closed", ex);
        }
    }

    private static void AppendAction(string kind, string id, string action, string reason)
    {
        lock (ActionHistoryGate)
        {
            try
            {
                Directory.CreateDirectory(StateDirectory);
                var history = AiActionHistoryAppendPolicy.Append(
                    CachedActionHistory ?? (AiSystemActionHistoryEntry[])ReadActionHistory(),
                    new AiSystemActionHistoryEntry(DateTime.UtcNow, kind, id, action, reason));
                // Serialize directly as UTF-8 with byte-identical JSON.
                // Publish the new memory snapshot only after atomic durability:
                // a failed write must not appear committed in later UI reads.
                AtomicFileService.WriteAllBytesAtomic(
                    ActionHistoryPath, JsonSerializer.SerializeToUtf8Bytes(history));
                CachedActionHistory = history;
                CachedActionHistoryStamp = ActionHistoryMetadata.Read();
            }
            catch
            {
            }
        }
    }

    // Pre-write intent MUST be durable before touching any Windows resource.
    // A failed intent persistence is a veto, not a best-effort log.
    private void BeginWriteAheadMutation(
        AiSystemRuntimeItemState prior, AiSystemRuntimeItemState intent)
    {
        if (_maintenanceWriteFaulted || _recoverInterruptedTrials)
            throw new InvalidOperationException(
                "AI new Windows intervention blocked until ownership checkpoint recovery.");
        string key = Key(intent.Kind, intent.Id);
        _items[key] = intent;
        if (!PersistItems(writeAhead: true))
        {
            _items[key] = prior;
            _maintenanceWriteFaulted = true; // veto all other mutations this cycle
            throw new IOException("AI mutation denied: durable ownership intent could not be saved.");
        }
        _recoverInterruptedTrials = true;
    }

    private void CommitWriteAheadMutation(
        AiSystemRuntimeItemState intent, AiSystemRuntimeItemState completed)
    {
        string key = Key(intent.Kind, intent.Id);
        _items[key] = completed with { PendingMutation = string.Empty };
        if (!PersistItems())
        {
            _items[key] = intent;
            _recoverInterruptedTrials = true;
            throw new IOException("Windows changed but AI completion record could not be saved; recovery required.");
        }
        _recoverInterruptedTrials =
            AiPendingTrialSelectionPolicy.HasPendingWriteAheadMutation(_items.Values);
    }

    private bool PersistItems(bool writeAhead = false, bool unchangedIsSuccess = false)
    {
        // A failed load is NOT an empty journal: never overwrite a damaged
        // ownership record with a fresh snapshot, even during an OFF restore.
        if (!_journalLoadHealthy)
            return false;
        try
        {
            // No mutation of the immutable journal records means the last
            // successfully committed JSON remains current. Keep the original
            // new-mutation vs recovery semantics even on this fast path.
            if (_lastPersistJson.Length != 0 &&
                AiWriteAheadSerializationPolicy.MatchesDurableValues(
                    _lastDurableJournalValues, _items.Values))
                return unchangedIsSuccess;

            var snapshot = _items.Values.OrderBy(x => x.Kind).ThenBy(x => x.Id).ToArray();
            string json = JsonSerializer.Serialize(snapshot);
            // Never persist a checkpoint larger than our own startup/GUI
            // decoder admits: that would strand owned Windows changes after
            // the next restart, even if the disk write itself succeeded.
            if (!AiWriteAheadSerializationPolicy.FitsDurableJournal(
                    json.Length, snapshot.Length))
                return false;
            AiWriteAheadSerializationPolicy.CheckpointDecision checkpoint =
                AiWriteAheadSerializationPolicy.DecideCheckpoint(
                    json, _lastPersistJson, unchangedIsSuccess);
            if (checkpoint != AiWriteAheadSerializationPolicy.CheckpointDecision.WriteRequired)
                return checkpoint == AiWriteAheadSerializationPolicy.CheckpointDecision.AlreadyDurable;
            // An unchanged periodic tick does not need any filesystem
            // metadata probe; create directories only for real writes.
            Directory.CreateDirectory(StateDirectory);
            string temp = ItemStatePath + ".tmp";
            if (writeAhead)
            {
                // Flush the intent to disk before issuing Stop/Disable.
                // Atomic same-volume rename makes a torn temp irrelevant.
                // Persist EXACTLY the JSON already compared against the
                // durable checkpoint; no second serializer pass or risk of
                // a mismatched UTF-8 payload under mutable journal rows.
                byte[] bytes = AiWriteAheadSerializationPolicy.EncodeJournalJson(json);
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
            }
            else
                File.WriteAllText(temp, json);
            // First-install must CREATE, never overwrite a journal another
            // agent wrote after our initial absence check. A collision
            // fails closed instead of destroying its ownership evidence.
            AiJournalSnapshotReadPolicy.CommitSnapshot(
                temp, ItemStatePath, _journalInitiallyAbsent);
            _journalInitiallyAbsent = false;
            _lastPersistJson = json;
            _lastDurableJournalValues = _items.Values.ToArray();
            // The atomic move is already durable. A failure refreshing
            // IN-MEMORY cache metadata cannot turn it into a failed write:
            // the caller could otherwise revert the intent in RAM while
            // leaving its durable PendingMutation on disk.
            try
            {
                lock (SnapshotGate)
                {
                    CachedItems = snapshot;
                    CachedItemsStamp = ItemsMetadata.Read();
                    CachedItemsRejected = false;
                }
            }
            catch (Exception ex)
            {
                LogService.Error("ai-system",
                    "AI journal saved but optional read cache refresh failed", ex);
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
            DateTime nowUtc = DateTime.UtcNow;
            double roundedLoad = Math.Round(loadPercent, 1);
            AiMeasuredOptimizationPolicy.HealthEvaluation result =
                AiMeasuredOptimizationPolicy.EvaluateHealth(
                    _items.Values, nowUtc, _lastHealthFingerprint?.LatestTarget);
            AiMeasuredOptimizationPolicy.HealthCounts totals = result.Counts;
            AiMeasuredOptimizationPolicy.Summary measured = result.Measured;
            int managed = totals.Managed;
            int sleeping = totals.Sleeping;
            int blocked = totals.Blocked;
            int managedServices = totals.ServiceManaged;
            int managedTasks = totals.TaskManaged;
            int managedDrivers = totals.DriverManaged;
            // This is an actual SCM-verified opt-out, not a measured CPU win.
            // Count the stopped, AI-owned service but never fabricate A/B/A savings.
            if (_searchOptimization.IsManaged)
            {
                managed++;
                sleeping++;
                managedServices++;
            }
            if (_bluetoothOptimization.IsManaged)
            {
                managed++;
                sleeping++;
                managedServices++;
            }
            if (_locationOptimization.IsManaged)
            {
                managed++;
                sleeping++;
                managedServices++;
            }
            if (_unusedPnpDevices.IsManaged)
            {
                managed++;
                sleeping++;
                managedDrivers++;
            }
            int optionalTaskManaged = _optionalTasks.ManagedCount +
                (_searchMaintenanceTask.IsManaged ? 1 : 0);
            managed += optionalTaskManaged;
            sleeping += optionalTaskManaged;
            managedTasks += optionalTaskManaged;
            var fingerprint = new AiSystemHealthFingerprint(
                config.Enabled, config.ServicesEnabled, config.ScheduledTasksEnabled,
                config.OptionalDevicesEnabled,
                managed, sleeping, blocked, _restoredCount,
                managedServices, managedTasks, managedDrivers,
                measured.ActiveVerified, measured.ConfirmedLast7Days,
                measured.LatestBenefitPp, measured.LatestBenefitPercent,
                measured.LatestTarget, measured.LatestVerifiedUtc,
                measured.LatestObservedAvailableRamBenefitMiB,
                _lastAction, _lastError,
                roundedLoad, gameActive, realIdle);

            if (_lastHealthFingerprint is AiSystemHealthFingerprint previous &&
                previous == fingerprint &&
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
                measured.ActiveVerified,
                measured.ConfirmedLast7Days,
                measured.LatestBenefitPp,
                measured.LatestBenefitPercent,
                measured.LatestTarget,
                measured.LatestVerifiedUtc,
                measured.LatestObservedAvailableRamBenefitMiB);

            Directory.CreateDirectory(StateDirectory);
            // Serialize directly to UTF-8; retain the existing atomic file
            // replacement and publish health only after durable success.
            // Unique sidecars keep two RuntimeHosts from trampling the same
            // fixed .tmp payload for this shared machine health file.
            AtomicFileService.WriteAllBytesAtomic(
                HealthPath, JsonSerializer.SerializeToUtf8Bytes(health));
            _lastHealthFingerprint = fingerprint;
            _lastHealthWriteUtc = nowUtc;
            lock (SnapshotGate)
            {
                CachedHealth = health;
                CachedHealthStamp = HealthMetadata.Read();
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

