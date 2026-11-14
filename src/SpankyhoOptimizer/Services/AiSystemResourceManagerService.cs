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

public sealed partial class AiSystemResourceManagerService
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
}
