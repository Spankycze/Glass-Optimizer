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

public static partial class FanProfileService
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
}
