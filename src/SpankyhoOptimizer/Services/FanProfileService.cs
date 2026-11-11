using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record FanProfile
{
    public int TargetRpm { get; init; } = 1100;
    public bool MotherboardPwm { get; init; }
    public bool ArgbSync { get; init; }
    public string EffectName { get; init; } = "Static";
    public byte EffectMode { get; init; } = 0x01;
    public byte EffectSpeed { get; init; } = 0x01;
    public byte EffectDirection { get; init; }
    public string HexColor { get; init; } = "#5A00FF";
    public int BrightnessPercent { get; init; } = 100;

    public bool AutoCurveEnabled { get; init; }
    public int CurveTemp1 { get; init; } = 40;
    public int CurveRpm1 { get; init; } = 700;
    public int CurveTemp2 { get; init; } = 55;
    public int CurveRpm2 { get; init; } = 1000;
    public int CurveTemp3 { get; init; } = 70;
    public int CurveRpm3 { get; init; } = 1500;
    public int CurveTemp4 { get; init; } = 82;
    public int CurveRpm4 { get; init; } = 2100;

    public int[] ChannelTargetRpms { get; init; } = { 1100, 1100, 1100, 1100 };
    public bool[] ChannelCurveEnabled { get; init; } = { false, false, false, false };
    public int[] ChannelCurveRpm1 { get; init; } = { 700, 700, 700, 700 };
    public int[] ChannelCurveRpm2 { get; init; } = { 1000, 1000, 1000, 1000 };
    public int[] ChannelCurveRpm3 { get; init; } = { 1500, 1500, 1500, 1500 };
    public int[] ChannelCurveRpm4 { get; init; } = { 2100, 2100, 2100, 2100 };
    public string[] ChannelRgbHex { get; init; } = { "#5A00FF", "#5A00FF", "#5A00FF", "#5A00FF" };
    public int[] ChannelMaxRpms { get; init; } = { 2100, 2100, 2100, 2100 };
    public int[] ChannelFanCounts { get; init; } = { 0, 0, 0, 0 };

    public bool CpuTempTargetEnabled { get; init; }
    public int CpuTempTargetC { get; init; } = 65;
    public bool AiSmartRuntimeEnabled { get; init; } = true;
}

public sealed record FanAgentHealth(
    DateTime UpdatedUtc,
    int ProcessId,
    string Version,
    bool Healthy,
    bool HidOk,
    DateTime? LastHidSuccessUtc,
    string? LastError,
    double? CpuTempC,
    int[] Rpms,
    bool Auto65,
    string PowerMode,
    bool GameActive,
    bool RealIdle,
    double LoadPercent,
    double? PackagePowerW,
    double? EffectiveClockMHz,
    bool AiEnabled,
    string AiMode,
    double AiAnomalyScore,
    double AiConfidence,
    double AiPredictedTemp10s,
    string AiPrediction,
    string AiLearnedBaseline,
    string AiLastAction,
    string AiGamePhase);

public static class FanProfileService
{
    private const string RunValueName = "Spankyho Fan Agent";
    private const string AiRunValueName = "Glass Optimizer AI Agent";
    private const string WatchdogRunValueName = "Spankyho Fan Watchdog";
    private const string AgentTaskName = "SpankyhoOptimizer-FanAgent";
    private const string AgentMutexName = @"Local\SpankyhoOptimizer.FanAgent.SingleInstance";
    private const string AiAgentMutexName = @"Local\SpankyhoOptimizer.AiAgent.SingleInstance";
    private const string ProfileWriteMutexName = @"Local\SpankyhoOptimizer.FanProfile.AtomicSave";
    private static long _nextStartupRegistrationCheckUptimeMs;
    private static int _startupRegistrationCheckRunning;
    internal static bool StartupRegistrationDue(long nowUptimeMs, long nextCheckUptimeMs) =>
        nextCheckUptimeMs <= 0 || nowUptimeMs >= nextCheckUptimeMs;
    private static readonly string AgentAssemblyVersion = typeof(FanProfileService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private const string WatchdogLoopMutexName = @"Local\SpankyhoOptimizer.FanWatchdogLoop.SingleInstance";
    private static readonly FanAgentRuntimePathSet RuntimePaths = FanAgentRuntimePaths.Create(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private static readonly string HeartbeatPath = RuntimePaths.HeartbeatPath;
    private static readonly string AgentVersionPath = RuntimePaths.VersionPath;
    private static readonly string AgentRestartRequestPath = RuntimePaths.RestartRequestPath;
    private static readonly string AgentHealthPath = RuntimePaths.HealthPath;
    // Heartbeat and health now use the shared atomic writer, which builds its
    // own unique temp name per call; no fixed ".tmp" path is needed here.
    private static readonly string? HeartbeatDirectory = Path.GetDirectoryName(HeartbeatPath);
    private static readonly string? HealthDirectory = Path.GetDirectoryName(AgentHealthPath);
    private static readonly string StateDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpankyhoOptimizer");
    private static readonly string StatePath = Path.Combine(StateDirectory, "fan-profile.json");
    private static readonly ReusableFileMetadata ProfileMetadata = new(StatePath);
    private static readonly ReusableFileMetadata HealthMetadata = new(AgentHealthPath);
    private static readonly object ProfileCacheGate = new();
    private static readonly object HealthCacheGate = new();
    private static FanProfile? _cachedProfile;
    private static DateTime _cachedProfileWriteUtc = DateTime.MinValue;
    private static long _cachedProfileLength = -1;
    private static FanAgentHealth? _cachedHealth;
    private static DateTime _cachedHealthWriteUtc = DateTime.MinValue;
    private static long _cachedHealthLength = -1;
    private static DateTime _lastHeartbeatWriteUtc = DateTime.MinValue;
    private static DateTime _lastHealthWriteUtc = DateTime.MinValue;
    private static DateTime _lastRestartRequestReadErrorUtc = DateTime.MinValue;

    public static FanProfile Default { get; } = new();

    public static FanAgentHealth? ReadHealthStatus()
    {
        try
        {
            lock (HealthCacheGate)
            {
                FileMetadataStamp info = HealthMetadata.Read();
                if (!info.Exists)
                {
                    _cachedHealth = null;
                    _cachedHealthWriteUtc = DateTime.MinValue;
                    _cachedHealthLength = -1;
                    return null;
                }
                if (_cachedHealth != null &&
                    _cachedHealthWriteUtc == info.LastWriteUtc &&
                    _cachedHealthLength == info.Length)
                {
                    return _cachedHealth;
                }

                using var healthStream = new FileStream(AgentHealthPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _cachedHealth = JsonSerializer.Deserialize(healthStream,
                    FanAgentCompactJsonContext.Default.FanAgentHealth);
                _cachedHealthWriteUtc = info.LastWriteUtc;
                _cachedHealthLength = info.Length;
                return _cachedHealth;
            }
        }
        catch
        {
            lock (HealthCacheGate)
                return _cachedHealth;
        }
    }

    public static FanProfile Load()
    {
        try
        {
            lock (ProfileCacheGate)
            {
                FileMetadataStamp info = ProfileMetadata.Read();
                if (!info.Exists)
                {
                    _cachedProfile = Default;
                    _cachedProfileWriteUtc = DateTime.MinValue;
                    _cachedProfileLength = -1;
                    return _cachedProfile;
                }
                DateTime writeUtc = info.LastWriteUtc;
                long length = info.Length;

                if (_cachedProfile != null &&
                    _cachedProfileWriteUtc == writeUtc &&
                    _cachedProfileLength == length)
                {
                    return _cachedProfile;
                }

                using var profileStream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                FanProfile? profile = JsonSerializer.Deserialize(profileStream,
                    FanAgentCompactJsonContext.Default.FanProfile);
                _cachedProfile = profile == null ? Default : Normalize(profile);
                _cachedProfileWriteUtc = writeUtc;
                _cachedProfileLength = length;
                return _cachedProfile;
            }
        }
        catch
        {
            lock (ProfileCacheGate)
                return _cachedProfile ?? Default;
        }
    }

    // The UI and tray can both persist profiles. Unique atomic temporary
    // files prevent their writes from colliding on one fixed ".tmp" path.
    // Normalize every profile, preserve the previous file on write failure,
    // and commit the cache only AFTER its durable replacement succeeded.
    internal static FanProfile WriteProfileSnapshot(string path, FanProfile profile)
    {
        FanProfile normalized = Normalize(profile);
        string json = JsonSerializer.Serialize(normalized,
            FanAgentIndentedJsonContext.Default.FanProfile);
        // UI and RuntimeHost are separate OS processes; a C# lock is not
        // enough when both replace the same file. Coordinate the replace
        // (and its backup path) for bounded time across the user session.
        using var writeMutex = new Mutex(false, ProfileWriteMutexName);
        bool acquired;
        try { acquired = writeMutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
            throw new IOException("Fan profile save is busy; existing settings preserved.");
        try
        {
            AtomicFileService.WriteText(path, json);
            return normalized;
        }
        finally { writeMutex.ReleaseMutex(); }
    }

    public static void Save(FanProfile profile)
    {
        lock (ProfileCacheGate)
        {
            FanProfile normalized = WriteProfileSnapshot(StatePath, profile);
            profile = normalized;
            // Another process may replace the profile between releasing
            // the OS mutex and inspecting its last-write stamp. Never pair
            // our previous value with another writer's metadata: the next
            // Load must read the actual durable latest file once.
            _cachedProfile = null;
            _cachedProfileWriteUtc = DateTime.MinValue;
            _cachedProfileLength = -1;
        }

        if (!LianLiSlInfinityService.Detect().Present)
        {
            StartAiAgentIfNeeded();
            return;
        }

        EnsureStartupAgentRegistered();
        if (profile.AutoCurveEnabled || Array.IndexOf(profile.ChannelCurveEnabled, true) >= 0 || profile.CpuTempTargetEnabled)
            StartAgentIfNeeded();
    }

    private static string ResolveRuntimeHostPath()
    {
        string version = typeof(FanProfileService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string staged = RuntimeHostStagePath.GetHostPath(localAppData, version);
        string candidate = Path.Combine(AppContext.BaseDirectory, "SpankyhoOptimizer.RuntimeHost.exe");
        string current = Environment.ProcessPath ?? string.Empty;

        return RuntimeHostPathPolicy.Resolve(
            staged,
            File.Exists(staged),
            candidate,
            File.Exists(candidate),
            current);
    }

    private static string ResolveMainAppPath()
    {
        string candidate = Path.Combine(AppContext.BaseDirectory, "SpankyhoOptimizer.exe");
        if (File.Exists(candidate))
            return candidate;

        string current = Environment.ProcessPath ?? string.Empty;
        return !string.IsNullOrWhiteSpace(current) &&
               Path.GetFileNameWithoutExtension(current).Equals("SpankyhoOptimizer", StringComparison.OrdinalIgnoreCase)
            ? current
            : TryResolveInstalledMainAppPath();
    }

    private static string TryResolveInstalledMainAppPath()
    {
        const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SpankyhoOptimizer";

        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? uninstall = root.OpenSubKey(uninstallPath);
                string location = Environment.ExpandEnvironmentVariables(
                    uninstall?.GetValue("InstallLocation")?.ToString() ?? string.Empty).Trim().Trim('"');
                string candidate = Path.Combine(location, "SpankyhoOptimizer.exe");
                if (!string.IsNullOrWhiteSpace(location) && File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Registry access is only a fallback for opening the existing app.
            }
        }

        return string.Empty;
    }

    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static async Task CleanupLegacyWatchdogTasksBestEffortAsync()
    {
        if (!LegacyWatchdogCleanupPolicy.ShouldAttempt(IsCurrentProcessElevated()))
            return;

        string[] taskNames =
        {
            "SpankyhoOptimizer-FanAgent-Watchdog",
            "SpankyhoOptimizer-RuntimeHost-Watchdog",
            "SpankyhoOptimizer-RuntimeHost-Watchdog-User"
        };

        foreach (string taskName in taskNames)
        {
            try
            {
                _ = await ProcessRunner.RunAsync(
                        "schtasks.exe",
                        $"/Delete /TN \"{taskName}\" /F",
                        TimeSpan.FromSeconds(3),
                        CancellationToken.None,
                        logNonZeroExitAsError: false).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    internal static string BuildFanAgentTaskXml(string exe, string userSid)
    {
        string command = SecurityElement.Escape(Path.GetFullPath(exe)) ?? exe;
        string sid = SecurityElement.Escape(userSid) ?? userSid;
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Spankyho Optimizer fan/AI tray agent</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId><Delay>PT3S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <RestartOnFailure><Count>3</Count><Interval>PT1M</Interval></RestartOnFailure>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author"><Exec><Command>{command}</Command><Arguments>--fan-agent</Arguments></Exec></Actions>
</Task>
""";
    }

    private static async Task<bool> EnsureFanAgentTaskRegisteredBestEffortAsync(string exe)
    {
        try
        {
            string sid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
            if (sid.Length == 0) return false;
            string taskPath = @"\" + AgentTaskName;
            string? existing = TaskSchedulerComService.TryGetTaskXml(taskPath);
            if (!string.IsNullOrWhiteSpace(existing) &&
                existing.Contains(Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase) &&
                existing.Contains("--fan-agent", StringComparison.OrdinalIgnoreCase) &&
                existing.Contains("RestartOnFailure", StringComparison.OrdinalIgnoreCase) &&
                existing.Contains("LogonTrigger", StringComparison.OrdinalIgnoreCase))
                return true;

            Directory.CreateDirectory(StateDirectory);
            string xmlPath = Path.Combine(StateDirectory, "fan-agent-task.xml");
            File.WriteAllText(xmlPath, BuildFanAgentTaskXml(exe, sid), System.Text.Encoding.Unicode);
            ProcessResult result = await ProcessRunner.RunAsync(
                    "schtasks.exe",
                    $"/Create /F /TN \"{AgentTaskName}\" /XML \"{xmlPath}\"",
                    TimeSpan.FromSeconds(8),
                    CancellationToken.None,
                    logNonZeroExitAsError: false).ConfigureAwait(false);
            if (!result.Success)
            {
                LogService.Error("lianli", "Fan agent scheduled task registration failed: " +
                    (result.StdErr.Length > 0 ? result.StdErr : result.StdOut));
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan agent scheduled task registration failed", ex);
            return false;
        }
    }

    public static void EnsureStartupAgentRegistered()
    {
        if (!LianLiSlInfinityService.Detect().Present)
        {
            EnsureAiStartupAgentRegistered();
            return;
        }

        // Save() can run on the UI thread: only the cheap due/flight checks
        // belong here. Task Scheduler COM, schtasks and HKCU maintenance must
        // execute off-thread and never block slider changes or navigation.
        long uptime = Environment.TickCount64;
        if (!StartupRegistrationDue(uptime,
                Volatile.Read(ref _nextStartupRegistrationCheckUptimeMs)))
            return;
        if (Interlocked.CompareExchange(ref _startupRegistrationCheckRunning, 1, 0) != 0)
            return;

        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsureStartupAgentRegisteredCoreAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogService.Error("lianli", "Fan startup registration worker failed", ex);
                }
                finally
                {
                    Volatile.Write(ref _startupRegistrationCheckRunning, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _startupRegistrationCheckRunning, 0);
            LogService.Error("lianli", "Fan startup registration dispatch failed", ex);
        }
    }

    public static void EnsureAiStartupAgentRegistered()
    {
        try
        {
            string exe = ResolveRuntimeHostPath();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
            using RegistryKey? run = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.SetValue(AiRunValueName, $"\"{exe}\" --ai-agent", RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            LogService.Error("ai-system", "AI startup registration failed", ex);
        }
    }

    private static async Task EnsureStartupAgentRegisteredCoreAsync()
    {
        long uptime = Environment.TickCount64;
        if (!StartupRegistrationDue(uptime,
                Volatile.Read(ref _nextStartupRegistrationCheckUptimeMs)))
            return;

        string exe = ResolveRuntimeHostPath();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;

        await CleanupLegacyWatchdogTasksBestEffortAsync().ConfigureAwait(false);
        string protectedRuntimeHost = Path.Combine(
            AppContext.BaseDirectory,
            "SpankyhoOptimizer.RuntimeHost.exe");
        HardwareTelemetryBridgeService.EnsureRegisteredBestEffort(protectedRuntimeHost);

        bool taskReady = await EnsureFanAgentTaskRegisteredBestEffortAsync(exe).ConfigureAwait(false);
        using RegistryKey? run = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (taskReady)
            run?.DeleteValue(RunValueName, throwOnMissingValue: false);
        else
            run?.SetValue(RunValueName, $"\"{exe}\" --fan-agent", RegistryValueKind.String);
        run?.DeleteValue(WatchdogRunValueName, throwOnMissingValue: false);

        // Successful registration is stable; the fallback Run entry retries
        // sooner so a transient COM failure does not silently disable startup.
        long delayMs = taskReady ? 10 * 60_000L : 60_000L;
        Volatile.Write(ref _nextStartupRegistrationCheckUptimeMs,
            Environment.TickCount64 + delayMs);
    }

    // Both GUI update checks and the live watchdog read this externally
    // writable version marker. Never allocate from an unbounded file or
    // interpret malformed version content as a reason to restart fans.
    private const int MaxAgentVersionBytes = 64;

    private static bool TryReadAgentVersion(string path, out string version)
    {
        version = string.Empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length <= 0 || stream.Length > MaxAgentVersionBytes)
                return false;
            Span<byte> data = stackalloc byte[MaxAgentVersionBytes + 1];
            int count = 0;
            while (count < data.Length)
            {
                int read = stream.Read(data[count..]);
                if (read == 0) break;
                count += read;
            }
            if (count == 0 || count > MaxAgentVersionBytes)
                return false;
            for (int i = 0; i < count; i++)
                if (data[i] != '.' && (data[i] < '0' || data[i] > '9'))
                    return false;
            string text = System.Text.Encoding.ASCII.GetString(data[..count]);
            if (!Version.TryParse(text, out Version? parsed) ||
                parsed.ToString() != text)
                return false;
            version = text;
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static void EnsureAgentUpdated()
    {
        try
        {
            string currentVersion = typeof(FanProfileService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            if (!TryReadAgentVersion(AgentVersionPath, out string runningVersion))
                return;

            if (!FanAgentVersionPolicy.ShouldRequestUpdate(
                    currentVersion,
                    runningVersion))
            {
                return;
            }

            string? directory = Path.GetDirectoryName(AgentRestartRequestPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                AgentRestartRequestPath,
                $"{currentVersion}|{DateTime.UtcNow:O}");

            LogService.Info(
                "lianli",
                $"FAN_AGENT_UPDATE_RESTART_REQUESTED old={runningVersion} new={currentVersion}");
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan agent update restart request failed", ex);
        }
    }

    private static void EnsureWatchdogLoopRunning(string exe)
    {
        try
        {
            _ = ProcessRunner.StartDetached(
                exe,
                "--fan-watchdog-loop",
                useShellExecute: false);
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan watchdog loop start failed", ex);
        }
    }

    public static void StartAgentIfNeeded()
    {
        try
        {
            if (!LianLiSlInfinityService.Detect().Present)
            {
                StartAiAgentIfNeeded();
                return;
            }
            if (IsHeartbeatFresh(TimeSpan.FromSeconds(20)))
                return;

            string exe = ResolveRuntimeHostPath();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;

            _ = ProcessRunner.StartDetached(
                exe,
                "--fan-agent",
                useShellExecute: false);
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan agent start failed", ex);
        }
    }

    public static void StartAiAgentIfNeeded()
    {
        try
        {
            string exe = ResolveRuntimeHostPath();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
            _ = ProcessRunner.StartDetached(exe, "--ai-agent", useShellExecute: false);
        }
        catch (Exception ex)
        {
            LogService.Error("ai-system", "AI agent start failed", ex);
        }
    }

    // Watchdog checks this small protocol repeatedly while preserving fan
    // control liveness. Never allocate from an untrusted/unbounded heartbeat.
    // The agent emits PID|UTC-O (about 32 bytes) every ten seconds.
    private const int MaxHeartbeatBytes = 96;

    private static bool TryParseHeartbeatPayload(string payload, out int pid,
        out DateTime reportedUtc)
    {
        pid = 0;
        reportedUtc = default;
        int separator = payload.IndexOf('|');
        if (separator <= 0 || separator >= payload.Length - 1 ||
            payload.IndexOf('|', separator + 1) >= 0 ||
            !int.TryParse(payload.AsSpan(0, separator), NumberStyles.None,
                CultureInfo.InvariantCulture, out pid) || pid <= 0)
            return false;
        return DateTime.TryParseExact(payload.AsSpan(separator + 1), "O",
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out reportedUtc) &&
            reportedUtc.Kind == DateTimeKind.Utc && reportedUtc.Ticks > 0;
    }

    private static bool TryReadHeartbeatPid(string path, out int pid,
        out DateTime reportedUtc)
    {
        pid = 0;
        reportedUtc = default;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        if (stream.Length <= 0 || stream.Length > MaxHeartbeatBytes)
            return false;
        // Read at most limit+1 regardless of changes after Length.
        Span<byte> bytes = stackalloc byte[MaxHeartbeatBytes + 1];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = stream.Read(bytes[count..]);
            if (read == 0) break;
            count += read;
        }
        if (count == 0 || count > MaxHeartbeatBytes) return false;
        for (int i = 0; i < count; i++)
            if (bytes[i] < 0x20 || bytes[i] > 0x7e) return false;
        return TryParseHeartbeatPayload(
            System.Text.Encoding.ASCII.GetString(bytes[..count]),
            out pid, out reportedUtc);
    }

    private static bool IsHeartbeatEvidenceFresh(DateTime fileWriteUtc,
        DateTime reportedUtc, DateTime nowUtc, TimeSpan maxAge) =>
        FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(fileWriteUtc, nowUtc, maxAge) &&
        FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(reportedUtc, nowUtc, maxAge) &&
        // A touched file must not refresh an old payload. Allow minor clock
        // and filesystem timestamp granularity differences.
        reportedUtc <= fileWriteUtc.AddSeconds(2);

    // Synthetic file input only; no live heartbeat, HID access, fan changes,
    // watchdog activation or user process termination.
    internal static string RunFanHeartbeatInputSmoke()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "spanky-heartbeat-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "heartbeat.txt");
            string valid = $"12345|{new DateTime(2026, 10, 26, 21, 0, 0, DateTimeKind.Utc):O}";
            File.WriteAllText(file, valid);
            DateTime fixtureUtc = new(2026, 10, 26, 21, 0, 0, DateTimeKind.Utc);
            if (!TryReadHeartbeatPid(file, out int pid, out DateTime reportedUtc) ||
                pid != 12345 || reportedUtc != fixtureUtc ||
                !IsHeartbeatEvidenceFresh(fixtureUtc.AddSeconds(1), reportedUtc,
                    fixtureUtc.AddSeconds(10), TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Valid fan heartbeat rejected.");
            // An unrelated metadata touch must not make a stale heartbeat
            // appear live, and an embedded future timestamp is not trustworthy.
            if (IsHeartbeatEvidenceFresh(fixtureUtc.AddSeconds(1),
                    fixtureUtc.AddMinutes(-5), fixtureUtc.AddSeconds(10),
                    TimeSpan.FromSeconds(30)) ||
                IsHeartbeatEvidenceFresh(fixtureUtc.AddSeconds(1),
                    fixtureUtc.AddMinutes(2), fixtureUtc.AddSeconds(10),
                    TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Stale or future fan heartbeat was trusted.");
            int rejected = 0;
            foreach (string invalid in new[]
            {
                "12345",
                "0|2026-10-26T21:00:00.0000000Z",
                "-1|2026-10-26T21:00:00.0000000Z",
                "not-a-pid|2026-10-26T21:00:00.0000000Z",
                "12345|not-a-date",
                valid + "|injected",
                valid + "\n",
                new string('7', MaxHeartbeatBytes + 1)
            })
            {
                File.WriteAllText(file, invalid);
                if (TryReadHeartbeatPid(file, out _, out _))
                    throw new InvalidOperationException(
                        "Malformed/unbounded fan heartbeat was trusted.");
                rejected++;
            }
            File.WriteAllBytes(file, new byte[] { 0xFF, 0xFE, 0x00 });
            if (TryReadHeartbeatPid(file, out _, out _))
                throw new InvalidOperationException("Non-ASCII fan heartbeat was trusted.");
            rejected++;
            // The same atomic writer used by the live agent must remain
            // readable without transient in-place partial payloads.
            AtomicFileService.WriteText(file, valid);
            if (!TryReadHeartbeatPid(file, out pid, out reportedUtc) || pid != 12345 ||
                reportedUtc != fixtureUtc)
                throw new InvalidOperationException("Atomic fan heartbeat was rejected.");
            // Reproduce the failure case with an actual isolated file: a
            // recently touched filesystem entry carrying an old agent time.
            File.WriteAllText(file, $"12345|{fixtureUtc.AddMinutes(-5):O}");
            File.SetLastWriteTimeUtc(file, fixtureUtc.AddSeconds(10));
            if (!TryReadHeartbeatPid(file, out pid, out reportedUtc) ||
                pid != 12345 ||
                IsHeartbeatEvidenceFresh(File.GetLastWriteTimeUtc(file), reportedUtc,
                    fixtureUtc.AddSeconds(11), TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Touched stale fan heartbeat was trusted.");

            // Exercise BOTH fan-agent version readers' shared bounded
            // admission using isolated files, never the running agent.
            string versionFile = Path.Combine(root, "fan-agent-version.txt");
            if (!TryReadAgentVersion(versionFile, out string absent) ||
                absent.Length != 0)
                throw new InvalidOperationException("Missing agent version was not first-run safe.");
            File.WriteAllText(versionFile, "16.286.0");
            if (!TryReadAgentVersion(versionFile, out string accepted) ||
                accepted != "16.286.0" ||
                !FanAgentVersionPolicy.ShouldRequestUpdate("16.287.0", accepted) ||
                FanAgentVersionPolicy.ShouldRequestUpdate(accepted, accepted))
                throw new InvalidOperationException("Valid fan-agent version policy regressed.");
            int rejectedVersions = 0;
            foreach (string invalidVersion in new[]
            {
                "", "not-a-version", "16.286.0|restart",
                "16.286.0\n", "016.286.0", "16.286.-1",
                new string('9', MaxAgentVersionBytes + 1)
            })
            {
                File.WriteAllText(versionFile, invalidVersion);
                if (TryReadAgentVersion(versionFile, out _))
                    throw new InvalidOperationException("Malformed fan-agent version was trusted.");
                rejectedVersions++;
            }
            File.WriteAllBytes(versionFile, new byte[] { 0xFF, 0xFE, 0x00 });
            if (TryReadAgentVersion(versionFile, out _))
                throw new InvalidOperationException("Invalid UTF-8 fan-agent version was trusted.");
            rejectedVersions++;
            AtomicFileService.WriteText(versionFile, "16.286.0");
            if (!TryReadAgentVersion(versionFile, out accepted) ||
                accepted != "16.286.0")
                throw new InvalidOperationException("Atomic fan-agent version was rejected.");
            return $"FAN_HEARTBEAT_BOUNDED_INPUT_SMOKE_OK rejected={rejected} maxBytes={MaxHeartbeatBytes} " +
                   $"agentVersionRejected={rejectedVersions} agentVersionMaxBytes={MaxAgentVersionBytes} " +
                   "validUtc=true atomic=true fanWrites=0 realHeartbeatUntouched=true";
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static bool IsHeartbeatFresh(TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(HeartbeatPath))
                return false;

            DateTime last = File.GetLastWriteTimeUtc(HeartbeatPath);
            DateTime nowUtc = DateTime.UtcNow;
            if (!FanWatchdogLoopPolicy.IsHeartbeatTimestampFresh(last, nowUtc, maxAge))
                return false;

            // A malformed, oversized or stale embedded timestamp is not
            // evidence of a live agent, even when the file metadata is fresh.
            if (!TryReadHeartbeatPid(HeartbeatPath, out int pid, out DateTime reportedUtc) ||
                !IsHeartbeatEvidenceFresh(last, reportedUtc, nowUtc, maxAge))
                return false;

            using Process process = Process.GetProcessById(pid);
            if (process.HasExited ||
                !FanWatchdogLoopPolicy.MatchesHeartbeatProcessStart(
                    process.StartTime.ToUniversalTime(), reportedUtc))
                return false;

            return process.ProcessName.Equals("SpankyhoOptimizer.RuntimeHost", StringComparison.OrdinalIgnoreCase) ||
                   process.ProcessName.Equals("SpankyhoOptimizer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static async Task RunWatchdogAsync(CancellationToken token = default)
    {
        if (!LianLiSlInfinityService.Detect().Present)
        {
            StartAiAgentIfNeeded();
            return;
        }

        try
        {
            string currentVersion = typeof(FanProfileService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            // Invalid version metadata is not a trusted update signal.
            // The ordinary heartbeat fallback still protects fan liveness.
            bool versionTrusted = TryReadAgentVersion(AgentVersionPath,
                out string runningVersion);
            bool needsUpdate = versionTrusted &&
                FanAgentVersionPolicy.ShouldRequestUpdate(currentVersion, runningVersion);

            if (needsUpdate)
                EnsureAgentUpdated();
            else if (IsHeartbeatFresh(TimeSpan.FromSeconds(30)))
                return;

            LogService.Info("lianli", needsUpdate
                ? $"FAN_WATCHDOG update required old={runningVersion} new={currentVersion}"
                : "FAN_WATCHDOG stale heartbeat -> starting agent");
            StartAgentIfNeeded();
            await Task.Delay(1200, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan watchdog failed", ex);
        }
    }

    public static async Task RunWatchdogLoopAsync(CancellationToken token = default)
    {
        if (!LianLiSlInfinityService.Detect().Present)
        {
            StartAiAgentIfNeeded();
            return;
        }

        using var mutex = new Mutex(false, WatchdogLoopMutexName);
        bool owns;
        try { owns = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns) return;

        try
        {
            LogService.Info("lianli", "FAN_WATCHDOG_LOOP_READY");
            await Task.Delay(FanWatchdogLoopPolicy.StartupGrace, token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                bool heartbeatFresh = IsHeartbeatFresh(TimeSpan.FromSeconds(30));
                if (FanWatchdogLoopPolicy.ShouldRestartAgent(heartbeatFresh))
                {
                    LogService.Info("lianli", "FAN_WATCHDOG_LOOP stale heartbeat -> starting agent");
                    StartAgentIfNeeded();
                }

                await Task.Delay(FanWatchdogLoopPolicy.PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan watchdog loop failed", ex);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    private static void WriteHeartbeat(bool force = false)
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            if (!force && !FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                    _lastHeartbeatWriteUtc, now, TimeSpan.FromSeconds(10)))
                return;

            if (!string.IsNullOrWhiteSpace(HeartbeatDirectory))
                Directory.CreateDirectory(HeartbeatDirectory);

            // Same cross-process reader as the health file: the GUI polls this
            // heartbeat, so use the shared atomic writer with its bounded retry.
            AtomicFileService.WriteText(HeartbeatPath, $"{Environment.ProcessId}|{now:O}");
            _lastHeartbeatWriteUtc = now;
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan heartbeat write failed", ex);
        }
    }

    private static void WriteHealthStatus(
        CpuTemperatureService.CpuTelemetrySample telemetry,
        int[]? rpms,
        FanProfile profile,
        string powerMode,
        bool gameActive,
        bool realIdle,
        double loadPercent,
        AiSmartRuntimeDecision ai,
        bool hidOk,
        DateTime? lastHidSuccessUtc,
        string? lastError,
        bool force = false)
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            if (!force && !FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                    _lastHealthWriteUtc, now, TimeSpan.FromSeconds(5)))
                return;

            if (!string.IsNullOrWhiteSpace(HealthDirectory))
                Directory.CreateDirectory(HealthDirectory);

            double? validTemp = telemetry.Celsius is double measured &&
                double.IsFinite(measured) && measured > 5 && measured < 150
                    ? measured : null;
            string version = AgentAssemblyVersion;
            var health = new FanAgentHealth(
                now,
                Environment.ProcessId,
                version,
                hidOk && validTemp.HasValue,
                hidOk,
                lastHidSuccessUtc,
                lastError,
                validTemp,
                // Serialized synchronously below; the four-channel buffer is not retained.
                // Avoid cloning four RPM values on every health snapshot.
                rpms ?? Array.Empty<int>(),
                profile.CpuTempTargetEnabled,
                powerMode,
                gameActive,
                realIdle,
                double.IsFinite(loadPercent) && loadPercent is >= 0 and <= 100
                    ? Math.Round(loadPercent, 1) : -1,
                AiPowerTelemetryEvidencePolicy.HasUsablePower(telemetry.PackagePowerW)
                    ? telemetry.PackagePowerW : null,
                AiPowerTelemetryEvidencePolicy.HasUsableClock(telemetry.EffectiveClockMHz)
                    ? telemetry.EffectiveClockMHz : null,
                ai.Enabled,
                ai.Mode,
                ai.AnomalyScore,
                ai.Confidence,
                ai.PredictedTemp10s,
                ai.Prediction,
                ai.LearnedBaseline,
                ai.LastAction,
                ai.GamePhase);

            // The GUI reads this health JSON from a separate process. Use the
            // shared atomic writer so a transient handle on the destination
            // retries instead of failing the whole periodic write.
            AtomicFileService.WriteText(AgentHealthPath,
                JsonSerializer.Serialize(health,
                    FanAgentCompactJsonContext.Default.FanAgentHealth));
            _lastHealthWriteUtc = now;
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan health status write failed", ex);
        }
    }

    private static void MarkAgentVersionRunning()
    {
        try
        {
            string? directory = Path.GetDirectoryName(AgentVersionPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string version = typeof(FanProfileService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            File.WriteAllText(AgentVersionPath, version);
            try { File.Delete(AgentRestartRequestPath); } catch { }
            LogService.Info("lianli", $"FAN_AGENT_VERSION_RUNNING version={version}");
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan agent version marker failed", ex);
        }
    }

    public static void RequestAgentRestart()
    {
        try
        {
            string? directory = Path.GetDirectoryName(AgentRestartRequestPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string currentVersion = typeof(FanProfileService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            File.WriteAllText(AgentRestartRequestPath, $"{currentVersion}|{DateTime.UtcNow:O}");
            LogService.Info("lianli", $"FAN_AGENT_MANUAL_RESTART_REQUESTED version={currentVersion}");
        }
        catch (Exception ex)
        {
            LogService.Error("lianli", "Fan agent restart request failed", ex);
        }
    }

    private static bool HasUpdateRestartRequest() =>
        FanAgentRestartPolicy.TryConsumeRestartRequest(
            AgentRestartRequestPath, ReportRestartRequestReadError);

    private static void ReportRestartRequestReadError(Exception ex)
    {
        DateTime now = DateTime.UtcNow;
        if (!FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                _lastRestartRequestReadErrorUtc, now, TimeSpan.FromMinutes(1))) return;
        _lastRestartRequestReadErrorUtc = now;
        LogService.Error("lianli", "Fan agent restart request cannot be consumed; keeping current agent running", ex);
    }

    public static async Task RunAgentAsync(CancellationToken token = default)
        => await RunAgentCoreAsync(aiOnly: false, token).ConfigureAwait(false);

    public static async Task RunAiAgentAsync(CancellationToken token = default)
        => await RunAgentCoreAsync(aiOnly: true, token).ConfigureAwait(false);

    private static async Task RunAgentCoreAsync(bool aiOnly, CancellationToken token)
    {
        if (!aiOnly && !LianLiSlInfinityService.Detect().Present)
        {
            await RunAiAgentAsync(token).ConfigureAwait(false);
            return;
        }

        using var mutex = new Mutex(false, aiOnly ? AiAgentMutexName : AgentMutexName);
        bool owns;
        try { owns = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns) return;

        FanTrayIconService? tray = null;
        DateTime nextTrayRetryUtc = DateTime.MinValue;
        bool restartRequested = false;
        try
        {
            // RAM Guard is a separate application. Never enumerate, kill, or clean
            // its processes/startup entries as a side effect of starting fan control.
            // Legacy cleanup is not part of the fan agent lifecycle.
            if (!aiOnly)
            {
                MarkAgentVersionRunning();
                WriteHeartbeat(force: true);
                EnsureStartupAgentRegistered();
            }

            if (!aiOnly)
            {
                try { tray = FanTrayIconService.StartOnMessageThread(); }
                catch (Exception ex)
                {
                    nextTrayRetryUtc = DateTime.UtcNow.AddSeconds(5);
                    LogService.Error("lianli", "Fan tray icon startup failed", ex);
                }

                // Apply the saved fan profile before starting the automatic control loop.
                await ApplySavedProfileAsync(token).ConfigureAwait(false);
            }

            var adaptivePower = new AdaptivePowerRuntimeService();
            var aiRuntime = new AiSmartRuntimeService();
            var aiSystemRuntime = new AiSystemResourceManagerService();
            // FAN/HID and hot temperature polling must not wait for optional
            // service/task/driver AI observation or A/B/A mutation work.
            // The lane is single-flight, coalesces obsolete epochs, and is
            // drained on exit BEFORE the FanAgent singleton mutex is released.
            await using var aiMaintenanceLane =
                new LatestOnlyAsyncWorkLane<FanAiTickObservation>(
                    async (sample, ct) =>
                    {
                        bool evidenceFresh = sample.CanUseEvidence(
                            DateTime.UtcNow, ForegroundProcessProbe.GetWindowIdentity());
                        // A stuck WMI/SCM call must not freeze the single AI lane
                        // forever. The timeout uses the same cancellation contract
                        // as shutdown; write-ahead/journal recovery stays intact.
                        using var tickCts = CancellationTokenSource
                            .CreateLinkedTokenSource(ct);
                        tickCts.CancelAfter(TimeSpan.FromSeconds(60));
                        await aiSystemRuntime.TickAsync(
                            sample.RealIdle,
                            sample.CpuPercent,
                            tickCts.Token,
                            evidenceFresh ? sample.Status : null,
                            evidenceFresh ? sample.Config : null,
                            evidenceFresh).ConfigureAwait(false);
                    },
                    ex => LogService.Error("ai-system",
                        "Async AI maintenance lane failed", ex),
                    token);
            AiSmartRuntimeDecision aiDecision = new(
                false, "OFF", "None", 0, 0, 0,
                "AI Smart Runtime čeká na první vzorek.",
                "n=0", "none", 0, false, false);
            int[] lastAppliedRpm = { -1, -1, -1, -1 };
            DateTime[] lastApplyUtc = { DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue };
            bool[] seenActiveChannel = new bool[4];
            int[] zeroRpmVotes = new int[4];
            int rpmUnderTargetVotes = 0;
            double[] smoothedRpms = new double[4];
            int[] tooltipRpmBuffer = new int[4];
            // Phase counters and 60s Process handle snapshots are diagnostics,
            // not fan safety inputs. No periodic diagnostic work in normal mode.
            FanAgentStageMetrics? fanTickMetrics = LogService.PerformanceDiagnosticsEnabled
                ? new FanAgentStageMetrics() : null;
            DateTime lastSlowAdaptiveLogUtc = DateTime.MinValue;
            DateTime? lastHidSuccessUtc = null;
            string? lastHidError = null;
            string? lastLoggedHidError = null;
            DateTime lastHidErrorLogUtc = DateTime.MinValue;
            bool hidWasFaulted = false;
            double? filteredTemp = null;
            double? previousFilteredTemp = null;
            FanProfile initial = Load();
            int targetModeRpm = Math.Clamp(initial.TargetRpm, 700,
                LianLiSlInfinityService.MaximumRpm);
            int auto65BaseRpm = Math.Clamp(
                Math.Max(initial.TargetRpm, 1620),
                1620,
                LianLiSlInfinityService.MaximumRpm);

            // Fan safety and RPM readbacks run unchanged; passive resource
            // diagnostics are not sampled inside this safety-critical loop.
            while (!token.IsCancellationRequested)
            {
                if (!aiOnly && HasUpdateRestartRequest())
                {
                    LogService.Info("lianli", "FAN_AGENT_UPDATE_RESTART_ACCEPTED");
                    restartRequested = true;
                    break;
                }

                if (!aiOnly) WriteHeartbeat();
                if (!aiOnly && tray == null && DateTime.UtcNow >= nextTrayRetryUtc)
                {
                    try
                    {
                        tray = FanTrayIconService.StartOnMessageThread();
                        LogService.Info("lianli", "FAN_TRAY_RECOVERED");
                    }
                    catch (Exception ex)
                    {
                        nextTrayRetryUtc = DateTime.UtcNow.AddSeconds(10);
                        LogService.Error("lianli", "Fan tray icon retry failed", ex);
                    }
                }
                tray?.PumpMessages();

                while (tray != null && tray.TryDequeueAction(out FanTrayAction action))
                {
                    if (action == FanTrayAction.RestartAgent)
                    {
                        restartRequested = true;
                        break;
                    }

                    await HandleTrayActionAsync(action, token).ConfigureAwait(false);
                }
                if (restartRequested) break;

                long phaseStart = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                FanProfile profile = Load();
                long afterProfile = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                CpuTemperatureService.CpuTelemetrySample telemetry = HardwareTelemetryBridgeService.ReadBestTelemetry();
                long afterTelemetry = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                int[]? liveRpms = null;
                FanTrayState trayState = FanTrayState.Ok;

                if (!aiOnly)
                {
                    try
                    {
                        liveRpms = LianLiSlInfinityService.ReadFanRpms();
                        lastHidSuccessUtc = DateTime.UtcNow;
                        lastHidError = null;
                        if (hidWasFaulted)
                        {
                            hidWasFaulted = false;
                            lastLoggedHidError = null;
                            LogService.Info("lianli", "FAN_HID_RECOVERED");
                        }

                        for (int i = 0; i < Math.Min(4, liveRpms.Length); i++)
                        {
                            int rpm = liveRpms[i];
                            smoothedRpms[i] = smoothedRpms[i] <= 0
                                ? rpm
                                : smoothedRpms[i] * 0.65 + rpm * 0.35;

                            if (rpm > 100)
                            {
                                seenActiveChannel[i] = true;
                                zeroRpmVotes[i] = 0;
                            }
                            else if (seenActiveChannel[i])
                            {
                                zeroRpmVotes[i]++;
                                if (zeroRpmVotes[i] >= 3)
                                    trayState = FanTrayState.Error;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        trayState = FanTrayState.Warning;
                        lastHidError = ex.GetBaseException().Message;
                        hidWasFaulted = true;

                        DateTime now = DateTime.UtcNow;
                        bool changed = !string.Equals(lastLoggedHidError, lastHidError, StringComparison.Ordinal);
                        if (changed || FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                                lastHidErrorLogUtc, now, TimeSpan.FromSeconds(30)))
                        {
                            lastLoggedHidError = lastHidError;
                            lastHidErrorLogUtc = now;
                            LogService.Error("lianli", "Fan tray RPM read failed", ex);
                        }
                    }
                }

                long afterHid = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                aiDecision = aiRuntime.Tick(
                    profile.AiSmartRuntimeEnabled,
                    telemetry,
                    liveRpms,
                    targetModeRpm,
                    liveRpms != null,
                    adaptivePower.GameActive,
                    adaptivePower.RealIdle,
                    adaptivePower.LoadPercent,
                    adaptivePower.LastBlocker,
                    adaptivePower.WorkloadClass);
                long afterAi = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                int fanWriteChannels = 0;
                int fanWriteAttempts = 0;

                if (!aiOnly && telemetry.Celsius is double temp && temp > 5 && temp < 150)
                {
                    filteredTemp = filteredTemp.HasValue ? filteredTemp.Value * 0.72 + temp * 0.28 : temp;
                    double trend = previousFilteredTemp.HasValue ? filteredTemp.Value - previousFilteredTemp.Value : 0;
                    previousFilteredTemp = filteredTemp;

                    if (profile.CpuTempTargetEnabled)
                    {
                        auto65BaseRpm = CalculateTargetTemperatureRpm(
                            auto65BaseRpm,
                            filteredTemp.Value,
                            profile.CpuTempTargetC,
                            trend);
                        int requestedTargetRpm = profile.AiSmartRuntimeEnabled
                            ? aiRuntime.AdjustAuto65Target(auto65BaseRpm, aiDecision)
                            : auto65BaseRpm;
                        targetModeRpm = profile.AiSmartRuntimeEnabled
                            ? AiFanTargetStabilityPolicy.Stabilize(
                                lastAppliedRpm[0],
                                requestedTargetRpm,
                                telemetry.Celsius,
                                aiDecision.PredictedTemp10s,
                                aiDecision.AnomalyScore,
                                aiDecision.Confidence,
                                adaptivePower.WorkloadClass)
                            : requestedTargetRpm;

                        bool materiallyChanged =
                            lastAppliedRpm[0] < 0 ||
                            Math.Abs(targetModeRpm - lastAppliedRpm[0]) >= 75;
                        bool stale =
                            FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                                lastApplyUtc[0], DateTime.UtcNow, TimeSpan.FromSeconds(20));

                        if (materiallyChanged || stale)
                        {
                            try
                            {
                                fanWriteAttempts++;
                                fanWriteChannels += 4;
                                LianLiSlInfinityService.SetAllFanRpm(targetModeRpm);
                                for (int channel = 0; channel < 4; channel++)
                                {
                                    lastAppliedRpm[channel] = targetModeRpm;
                                    lastApplyUtc[channel] = DateTime.UtcNow;
                                }
                            }
                            catch (Exception ex)
                            {
                                trayState = FanTrayState.Error;
                                LogService.Error("lianli", "CPU target temperature fan control failed", ex);
                            }
                        }

                        if (liveRpms != null)
                        {
                            if (FanRpmReadbackPolicy.TryAverageActive(liveRpms, out double averageReadback))
                            {
                                if (averageReadback < targetModeRpm - 220)
                                    rpmUnderTargetVotes++;
                                else
                                    rpmUnderTargetVotes = 0;

                                if (rpmUnderTargetVotes >= 3)
                                {
                                    try
                                    {
                                        fanWriteAttempts++;
                                        fanWriteChannels += 4;
                                        LianLiSlInfinityService.SetAllFanRpm(targetModeRpm);
                                        rpmUnderTargetVotes = 0;
                                        lastApplyUtc[0] = DateTime.UtcNow;
                                        LogService.Info(
                                            "lianli",
                                            $"AUTO65_RPM_SELF_HEAL target={targetModeRpm} avg={averageReadback:0}");
                                    }
                                    catch (Exception ex)
                                    {
                                        trayState = FanTrayState.Error;
                                        LogService.Error("lianli", "AUTO65 RPM self-heal failed", ex);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // A single clock snapshot keeps the four stale checks consistent.
                        DateTime curveCheckUtc = DateTime.UtcNow;
                        for (int channel = 0; channel < 4; channel++)
                        {
                            bool enabled = profile.ChannelCurveEnabled[channel] || profile.AutoCurveEnabled;
                            if (!enabled) continue;

                            int rpm = profile.ChannelCurveEnabled[channel]
                                ? CalculateChannelCurveRpm(profile, channel, filteredTemp.Value)
                                : CalculateCurveRpm(profile, filteredTemp.Value);

                            bool materiallyChanged = lastAppliedRpm[channel] < 0 || Math.Abs(rpm - lastAppliedRpm[channel]) >= 75;
                            bool stale = FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                                lastApplyUtc[channel], curveCheckUtc, TimeSpan.FromSeconds(20));
                            if (!materiallyChanged && !stale) continue;

                            try
                            {
                                fanWriteAttempts++;
                                fanWriteChannels++;
                                LianLiSlInfinityService.SetFanRpm(channel, rpm, profile.ChannelMaxRpms[channel]);
                                lastAppliedRpm[channel] = rpm;
                                lastApplyUtc[channel] = DateTime.UtcNow;
                            }
                            catch (Exception ex)
                            {
                                trayState = FanTrayState.Error;
                                LogService.Error("lianli", $"Fan curve apply failed channel={channel + 1}", ex);
                            }
                        }
                    }
                }
                else
                {
                    trayState = trayState == FanTrayState.Error ? trayState : FanTrayState.Warning;
                }

                long beforeAdaptivePower = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                try
                {
                    await adaptivePower.TickAsync(
                        telemetry,
                        aiOnly ? null : liveRpms,
                        profile.CpuTempTargetEnabled,
                        profile.AiSmartRuntimeEnabled,
                        aiDecision,
                        token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogService.Error("power", "Adaptive runtime power tick failed", ex);
                }

                long afterPower = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                if (!aiOnly && fanTickMetrics is not null)
                {
                    double adaptivePowerMs = Stopwatch.GetElapsedTime(beforeAdaptivePower, afterPower).TotalMilliseconds;
                    double fanControlMs = Stopwatch.GetElapsedTime(afterAi, beforeAdaptivePower).TotalMilliseconds;
                    if (adaptivePowerMs + fanControlMs >= 200 &&
                        FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                            lastSlowAdaptiveLogUtc, DateTime.UtcNow, TimeSpan.FromMinutes(1)))
                    {
                        lastSlowAdaptiveLogUtc = DateTime.UtcNow;
                        LogService.Info("lianli",
                            $"FAN_POWER_SLOW adaptiveMs={adaptivePowerMs:F1} " +
                            $"fanControlMs={fanControlMs:F1} " +
                            $"fanWriteAttempts={fanWriteAttempts} fanWriteChannels={fanWriteChannels} " +
                            $"dominant={(adaptivePowerMs >= fanControlMs ? "adaptive" : "fanControl")}");
                    }
                }
                // Use already-running FanAgent; no third tray process, scheduler or polling thread.
                AiSystemSmoothnessGuardService.SampleIfDue(
                    adaptivePower.CpuLoadKnown ? adaptivePower.LoadPercent : double.NaN,
                    adaptivePower.GameActive);
                // Automatic, bounded off-thread CPU Sets/wake-latency evidence;
                // never changes affinity or timer resolution.
                // Always deliver the current admission state, including AI-OFF,
                // game activation or lost CPU evidence: a ThreadPool-queued
                // sample must not run later under an obsolete workload.
                AiSchedulerAutoEvidenceService.QueueIfDue(
                    adaptivePower.CurrentAiConfig is { Enabled: true, SelfOptimizationEnabled: true },
                    adaptivePower.CpuLoadKnown ? adaptivePower.LoadPercent : double.NaN,
                    adaptivePower.GameActive);

                // AI maintenance can perform slow service/driver probes or
                // write-ahead journal I/O. The fan safety loop publishes its
                // latest immutable epoch without blocking on that work.
                // The dedicated lane preserves exactly one AI writer.
                aiMaintenanceLane.Submit(new FanAiTickObservation(
                    DateTime.UtcNow,
                    adaptivePower.RealIdle,
                    adaptivePower.LoadPercent,
                    adaptivePower.CpuLoadKnown,
                    adaptivePower.CurrentStatus,
                    adaptivePower.CurrentAiConfig));

                // Reuse the fixed-size four-channel buffer instead of allocating
                // an array and a LINQ iterator every 1-5-second agent tick.
                int[]? tooltipRpms = liveRpms == null ? null : tooltipRpmBuffer;
                if (tooltipRpms != null)
                {
                    for (int channel = 0; channel < tooltipRpms.Length; channel++)
                        tooltipRpms[channel] = (int)Math.Round(smoothedRpms[channel] / 15d) * 15;
                }

                // Avoid assembling CPU/RPM/mode strings every 1–1.5s when
                // Explorer won't be notified. Status changes bypass the
                // regular 5-second pacing and are visible immediately.
                if (!aiOnly && tray != null)
                {
                    if (tray.ShouldComposeTooltip(trayState))
                    {
                        tray.Update(
                            BuildTrayTooltip(
                                telemetry,
                                tooltipRpms,
                                adaptivePower.ModeName,
                                profile.CpuTempTargetEnabled,
                                adaptivePower.GameActive,
                                aiDecision),
                            trayState);
                    }
                    else
                    {
                        // No skipped tray click/menu handling on suppressed updates.
                        tray.PumpMessages();
                    }
                }

                if (!aiOnly) WriteHealthStatus(
                    telemetry,
                    tooltipRpms,
                    profile,
                    adaptivePower.ModeName,
                    adaptivePower.GameActive,
                    adaptivePower.RealIdle,
                    adaptivePower.LoadPercent,
                    aiDecision,
                    liveRpms != null,
                    lastHidSuccessUtc,
                    lastHidError);
                long afterHealth = !aiOnly && fanTickMetrics is not null ? Stopwatch.GetTimestamp() : 0;
                if (!aiOnly && fanTickMetrics is not null)
                {
                    fanTickMetrics.Record(phaseStart, afterProfile, afterTelemetry,
                        afterHid, afterAi, afterPower, afterHealth);
                    fanTickMetrics.ReportIfDue();
                }

                bool dynamicFanControl = !aiOnly && (
                    profile.CpuTempTargetEnabled ||
                    profile.AutoCurveEnabled ||
                    Array.IndexOf(profile.ChannelCurveEnabled, true) >= 0);
                int loopDelayMs =
                    telemetry.Celsius is double currentTemp && currentTemp >= 70
                        ? 1000
                        : dynamicFanControl
                            ? 1500
                            : adaptivePower.GameActive
                                ? 2000
                                : adaptivePower.ModeName.Equals("Idle", StringComparison.OrdinalIgnoreCase)
                                    ? 5000
                                    : 3000;

                await Task.Delay(loopDelayMs, token).ConfigureAwait(false);
            }
        }
        finally
        {
            tray?.Dispose();
            CpuTemperatureService.Dispose();
            try { mutex.ReleaseMutex(); } catch { }
            if (!aiOnly)
            {
                try { File.Delete(HeartbeatPath); } catch { }
                try { File.Delete(AgentHealthPath); } catch { }
            }

            if (restartRequested && !aiOnly)
            {
                await Task.Delay(350, CancellationToken.None).ConfigureAwait(false);
                StartAgentIfNeeded();
            }
        }
    }

    private static async Task HandleTrayActionAsync(FanTrayAction action, CancellationToken token)
    {
        FanProfile current = Load();

        if (action == FanTrayAction.ToggleAuto65)
        {
            bool enabled = !current.CpuTempTargetEnabled;
            Save(current with
            {
                CpuTempTargetEnabled = enabled,
                CpuTempTargetC = 65,
                MotherboardPwm = false,
                AutoCurveEnabled = false,
                ChannelCurveEnabled = new[] { false, false, false, false }
            });
            if (enabled)
                LianLiSlInfinityService.SetAllFanRpm(1620);
            return;
        }

        int rpm = action switch
        {
            FanTrayAction.Set700 => 700,
            FanTrayAction.Set1100 => 1100,
            FanTrayAction.Set1620 => 1620,
            FanTrayAction.Set2100 => 2100,
            _ => 0
        };

        if (rpm > 0)
        {
            Save(current with
            {
                TargetRpm = rpm,
                CpuTempTargetEnabled = false,
                AutoCurveEnabled = false,
                ChannelCurveEnabled = new[] { false, false, false, false },
                MotherboardPwm = false
            });
            LianLiSlInfinityService.SetAllFanRpm(rpm);
            return;
        }

        if (action == FanTrayAction.OpenFans)
        {
            string exe = ResolveMainAppPath();
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                _ = ProcessRunner.StartDetached(
                    exe,
                    useShellExecute: true);
            }
        }

        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
    }

    public static async Task ApplySavedProfileAsync(CancellationToken token = default)
    {
        FanProfile profile = Load();

        for (int attempt = 0; attempt < 12; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var controller = LianLiSlInfinityService.Detect();
                if (!controller.Present)
                {
                    await Task.Delay(2500, token).ConfigureAwait(false);
                    continue;
                }

                if (profile.MotherboardPwm)
                {
                    LianLiSlInfinityService.SetAllMotherboardPwm();
                }
                else if (profile.CpuTempTargetEnabled)
                {
                    var temp = HardwareTelemetryBridgeService.ReadBestTemperature();
                    int rpm = temp.Celsius is double c
                        ? CalculateTargetTemperatureRpm(Math.Clamp(profile.TargetRpm, 700, LianLiSlInfinityService.MaximumRpm), c, profile.CpuTempTargetC)
                        : Math.Clamp(profile.TargetRpm, 700, LianLiSlInfinityService.MaximumRpm);
                    LianLiSlInfinityService.SetAllFanRpm(rpm);
                }
                else
                {
                    var temp = HardwareTelemetryBridgeService.ReadBestTemperature();
                    bool anyPerChannelCurve = Array.IndexOf(profile.ChannelCurveEnabled, true) >= 0;

                    if (!profile.AutoCurveEnabled && !anyPerChannelCurve)
                    {
                        LianLiSlInfinityService.SetAllFanRpm(profile.TargetRpm);
                    }
                    else
                    {
                        for (int channel = 0; channel < 4; channel++)
                        {
                            int rpm = profile.ChannelCurveEnabled[channel] && temp.Celsius is double c1
                                ? CalculateChannelCurveRpm(profile, channel, c1)
                                : profile.AutoCurveEnabled && temp.Celsius is double c2
                                    ? CalculateCurveRpm(profile, c2)
                                    : profile.TargetRpm;

                            LianLiSlInfinityService.SetFanRpm(channel, rpm, profile.ChannelMaxRpms[channel]);
                        }
                    }
                }

                if (profile.ArgbSync)
                {
                    LianLiSlInfinityService.SetArgbSync(true);
                }
                else if (TryParseHex(profile.HexColor, out byte r, out byte g, out byte b))
                {
                    LianLiSlInfinityService.SetAllEffect(
                        profile.EffectMode,
                        profile.EffectSpeed,
                        profile.EffectDirection,
                        r, g, b,
                        profile.BrightnessPercent);
                }
                return;
            }
            catch (Exception ex)
            {
                LogService.Error("lianli", $"Fan agent apply attempt {attempt + 1} failed", ex);
                if (attempt >= 11) return;
                await Task.Delay(2500, token).ConfigureAwait(false);
            }
        }
    }

    private static string BuildTrayTooltip(
        CpuTemperatureService.CpuTelemetrySample telemetry,
        int[]? rpms,
        string powerMode,
        bool auto65,
        bool gameActive,
        AiSmartRuntimeDecision ai)
    {
        string cpu = telemetry.Celsius is double c ? $"{c:0}°C" : "—°C";
        string fanText;

        fanText = FanRpmTooltipPolicy.FormatFirstFour(rpms);

        string mode = auto65 ? "AUTO65" : powerMode;
        if (gameActive)
            mode += " • DIABLO PERF";
        if (ai.Enabled)
            mode += ai.AnomalyScore >= 55 ? $" • AI {ai.AnomalyScore:0}" : " • AI AUTO";
        return $"CPU {cpu} • {fanText} • {mode}";
    }

    // Same-host measured allocation cost of building tooltip strings on each
    // one-second agent tick vs only on due/safety-transition ticks.
    internal static string RunTrayCompositionBenchmark()
    {
        var telemetry = new CpuTemperatureService.CpuTelemetrySample(
            56, "fixture", "CPU", 15, 3800, 65);
        var ai = new AiSmartRuntimeDecision(
            false, "OFF", "None", 0, 0, 0,
            "smoke", "n=0", "none", 0, false, false);
        int[] rpms = { 1620, 1620, 1620, 1620 };
        const int iterations = 120;
        string latest = string.Empty;
        for (int i = 0; i < 20; i++)
            latest = BuildTrayTooltip(telemetry, rpms, "Balanced", true, false, ai);
        long baselineStart = GC.GetAllocatedBytesForCurrentThread();
        long clock = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            rpms[0] = 1600 + i;
            latest = BuildTrayTooltip(telemetry, rpms, "Balanced", true, false, ai);
        }
        double baselineMs = System.Diagnostics.Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        long baselineAllocated = GC.GetAllocatedBytesForCurrentThread() - baselineStart;
        var start = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        DateTime lastUpdated = DateTime.MinValue;
        bool hasPublished = false;
        int rendered = 0;
        long throttledStart = GC.GetAllocatedBytesForCurrentThread();
        clock = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            DateTime now = start.AddSeconds(i);
            if (!FanTrayIconService.NeedsTooltipCompose(
                    hasPublished, FanTrayState.Ok, FanTrayState.Ok, now - lastUpdated))
                continue;
            rpms[0] = 1600 + i;
            latest = BuildTrayTooltip(telemetry, rpms, "Balanced", true, false, ai);
            lastUpdated = now;
            hasPublished = true;
            rendered++;
        }
        double throttledMs = System.Diagnostics.Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        long throttledAllocated = GC.GetAllocatedBytesForCurrentThread() - throttledStart;
        if (rendered != 24 || baselineAllocated <= throttledAllocated ||
            string.IsNullOrWhiteSpace(latest))
            throw new InvalidOperationException("Fan tooltip composition allocation budget failed.");
        return $"FAN_TRAY_FORMAT_BENCH_OK ticks={iterations} originalFormats={iterations} " +
            $"throttledFormats={rendered} originalMs={baselineMs:F3} throttledMs={throttledMs:F3} " +
            $"originalAllocatedB={baselineAllocated} throttledAllocatedB={throttledAllocated}";
    }

    public static int CalculateTargetTemperatureRpm(int currentRpm, double temperature, int targetC) =>
        CalculateTargetTemperatureRpm(currentRpm, temperature, targetC, 0);

    public static int CalculateTargetTemperatureRpm(int currentRpm, double temperature, int targetC, double trendPerTick)
    {
        const int Auto65MinimumRpm = 1620;
        targetC = Math.Clamp(targetC, 50, 85);
        currentRpm = Math.Clamp(currentRpm, Auto65MinimumRpm, LianLiSlInfinityService.MaximumRpm);

        double delta = temperature - targetC;
        int step = delta switch
        {
            >= 6 => 350,
            >= 3 => 250,
            >= 1 => 150,
            >= 0 => 100,
            <= -10 => -120,
            <= -7 => -100,
            <= -4 => -75,
            _ => 0
        };

        if (trendPerTick >= 0.8)
            step += 150;
        else if (trendPerTick >= 0.4)
            step += 75;
        else if (trendPerTick <= -0.8 && step < 0)
            step = (int)Math.Ceiling(step * 0.5);

        return Math.Clamp(currentRpm + step, Auto65MinimumRpm, LianLiSlInfinityService.MaximumRpm);
    }

    public static int CalculateCurveRpm(FanProfile profile, double temperature)
    {
        return InterpolateCurve(
            temperature,
            profile.CurveTemp1, profile.CurveRpm1,
            profile.CurveTemp2, profile.CurveRpm2,
            profile.CurveTemp3, profile.CurveRpm3,
            profile.CurveTemp4, profile.CurveRpm4,
            profile.TargetRpm);
    }

    public static int CalculateChannelCurveRpm(FanProfile profile, int channel, double temperature)
    {
        channel = Math.Clamp(channel, 0, 3);
        return InterpolateCurve(
            temperature,
            profile.CurveTemp1, profile.ChannelCurveRpm1[channel],
            profile.CurveTemp2, profile.ChannelCurveRpm2[channel],
            profile.CurveTemp3, profile.ChannelCurveRpm3[channel],
            profile.CurveTemp4, profile.ChannelCurveRpm4[channel],
            profile.ChannelTargetRpms[channel]);
    }

    // Fan safety hot path: preserve the exact old four-point interpolation
    // without allocating a new (temperature,RPM)[] on each channel/tick.
    private static int InterpolateCurve(double temperature,
        int t1, int r1, int t2, int r2, int t3, int r3, int t4, int r4, int fallback)
    {
        if (temperature <= t1) return r1;
        if (temperature >= t4) return r4;
        // Negated comparisons also preserve the legacy NaN fallback to the
        // first segment (rather than silently using the profile's target RPM).
        if (!(temperature > t2)) return InterpolateCurveSegment(temperature, t1, r1, t2, r2);
        if (!(temperature > t3)) return InterpolateCurveSegment(temperature, t2, r2, t3, r3);
        if (!(temperature > t4)) return InterpolateCurveSegment(temperature, t3, r3, t4, r4);
        return fallback;
    }

    private static int InterpolateCurveSegment(double temperature, int fromT, int fromR, int toT, int toR)
    {
        double span = Math.Max(1, toT - fromT);
        double t = (temperature - fromT) / span;
        return Math.Clamp(
            (int)Math.Round(fromR + (toR - fromR) * t),
            LianLiSlInfinityService.MinimumSafeRpm,
            LianLiSlInfinityService.MaximumRpm);
    }

    // Regression of the allocation-free fan controller against the former
    // exact algorithm, including boundaries and non-finite sensor values.
    // No HID writes: these are pure arithmetic checks.
    internal static string RunFanCurveAllocationSmoke()
    {
        static int LegacyCurve(double temp, int t1, int r1, int t2, int r2,
            int t3, int r3, int t4, int r4, int fallback)
        {
            var points = new[] { (T: t1, R: r1), (T: t2, R: r2),
                                 (T: t3, R: r3), (T: t4, R: r4) };
            if (temp <= points[0].T) return points[0].R;
            if (temp >= points[3].T) return points[3].R;
            for (int i = 0; i < points.Length - 1; i++)
            {
                if (temp > points[i + 1].T) continue;
                double span = Math.Max(1, points[i + 1].T - points[i].T);
                double ratio = (temp - points[i].T) / span;
                return Math.Clamp((int)Math.Round(
                    points[i].R + (points[i + 1].R - points[i].R) * ratio),
                    LianLiSlInfinityService.MinimumSafeRpm,
                    LianLiSlInfinityService.MaximumRpm);
            }
            return fallback;
        }

        const int cases = 4096;
        for (int i = 0; i < cases; i++)
        {
            double temp = i % 127 - 16 + (i % 5) / 5d;
            if (i == 0) temp = double.NaN;
            if (i == 1) temp = double.PositiveInfinity;
            if (i == 2) temp = double.NegativeInfinity;
            int r2 = 1050 + i % 320;
            int oldRpm = LegacyCurve(temp, 30, 700, 47, r2, 67, 1790, 89, 2220, 1450);
            int newRpm = InterpolateCurve(temp, 30, 700, 47, r2, 67, 1790, 89, 2220, 1450);
            if (oldRpm != newRpm)
                throw new InvalidOperationException(
                    $"Fan RPM curve mismatch case={i} temp={temp} old={oldRpm} new={newRpm}");
        }

        const int rounds = 16384;
        int baselineChecksum = 0, optimizedChecksum = 0;
        long startAllocated = GC.GetAllocatedBytesForCurrentThread();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
            baselineChecksum += LegacyCurve(32 + i % 64, 30, 700, 47, 1100, 67, 1790, 89, 2220, 1450);
        double beforeMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long beforeB = GC.GetAllocatedBytesForCurrentThread() - startAllocated;

        startAllocated = GC.GetAllocatedBytesForCurrentThread();
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
            optimizedChecksum += InterpolateCurve(32 + i % 64, 30, 700, 47, 1100, 67, 1790, 89, 2220, 1450);
        double afterMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long afterB = GC.GetAllocatedBytesForCurrentThread() - startAllocated;
        if (baselineChecksum != optimizedChecksum || beforeB <= afterB)
            throw new InvalidOperationException("Fan curve arithmetic/allocation regression.");
        return $"FAN_CURVE_NO_ALLOC_SMOKE_OK cases={cases} rounds={rounds} " +
               $"baselineB={beforeB} optimizedB={afterB} " +
               $"baselineMs={beforeMs:F3} optimizedMs={afterMs:F3} " +
               "rpmIdentical=true noHidWrites=true";
    }

    private static FanProfile Normalize(FanProfile p)
    {
        int t1 = Math.Clamp(p.CurveTemp1, 25, 70);
        int t2 = Math.Clamp(Math.Max(p.CurveTemp2, t1 + 5), 30, 80);
        int t3 = Math.Clamp(Math.Max(p.CurveTemp3, t2 + 5), 40, 90);
        int t4 = Math.Clamp(Math.Max(p.CurveTemp4, t3 + 5), 50, 100);

        return p with
        {
            TargetRpm = Math.Clamp(p.TargetRpm, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm),
            CurveTemp1 = t1,
            CurveTemp2 = t2,
            CurveTemp3 = t3,
            CurveTemp4 = t4,
            CurveRpm1 = Math.Clamp(p.CurveRpm1, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm),
            CurveRpm2 = Math.Clamp(p.CurveRpm2, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm),
            CurveRpm3 = Math.Clamp(p.CurveRpm3, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm),
            CurveRpm4 = Math.Clamp(p.CurveRpm4, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm),
            BrightnessPercent = Math.Clamp(p.BrightnessPercent, 25, 100),
            ChannelTargetRpms = NormalizeRpmArray(p.ChannelTargetRpms, p.TargetRpm),
            ChannelCurveEnabled = NormalizeBoolArray(p.ChannelCurveEnabled),
            ChannelCurveRpm1 = NormalizeRpmArray(p.ChannelCurveRpm1, p.CurveRpm1),
            ChannelCurveRpm2 = NormalizeRpmArray(p.ChannelCurveRpm2, p.CurveRpm2),
            ChannelCurveRpm3 = NormalizeRpmArray(p.ChannelCurveRpm3, p.CurveRpm3),
            ChannelCurveRpm4 = NormalizeRpmArray(p.ChannelCurveRpm4, p.CurveRpm4),
            ChannelRgbHex = NormalizeColorArray(p.ChannelRgbHex, p.HexColor),
            ChannelMaxRpms = NormalizeMaxRpmArray(p.ChannelMaxRpms),
            ChannelFanCounts = NormalizeFanCountArray(p.ChannelFanCounts),
            CpuTempTargetC = Math.Clamp(p.CpuTempTargetC, 50, 85)
        };
    }

    private static int[] NormalizeRpmArray(int[]? source, int fallback)
    {
        var result = new int[4];
        for (int i = 0; i < result.Length; i++)
        {
            int value = source != null && i < source.Length ? source[i] : fallback;
            result[i] = Math.Clamp(value, LianLiSlInfinityService.MinimumSafeRpm, LianLiSlInfinityService.MaximumRpm);
        }
        return result;
    }

    private static bool[] NormalizeBoolArray(bool[]? source)
    {
        var result = new bool[4];
        for (int i = 0; i < result.Length; i++)
            result[i] = source != null && i < source.Length && source[i];
        return result;
    }

    private static int[] NormalizeMaxRpmArray(int[]? source)
    {
        var result = new int[4];
        for (int i = 0; i < result.Length; i++)
        {
            int value = source != null && i < source.Length ? source[i] : LianLiSlInfinityService.MaximumRpm;
            result[i] = Math.Clamp(value, 1200, 2600);
        }
        return result;
    }

    private static int[] NormalizeFanCountArray(int[]? source)
    {
        var result = new int[4];
        for (int i = 0; i < result.Length; i++)
        {
            int value = source != null && i < source.Length ? source[i] : 0;
            result[i] = Math.Clamp(value, 0, 4);
        }
        return result;
    }

    private static string[] NormalizeColorArray(string[]? source, string fallback)
    {
        var result = new string[4];
        for (int i = 0; i < result.Length; i++)
        {
            string value = source != null && i < source.Length ? source[i] : fallback;
            result[i] = TryParseHex(value, out _, out _, out _) ? value.ToUpperInvariant() : fallback;
        }
        return result;
    }

    private static bool TryParseHex(string? value, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        string text = (value ?? string.Empty).Trim().TrimStart('#');
        return text.Length == 6
            && byte.TryParse(text.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(text.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(text.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }
}
