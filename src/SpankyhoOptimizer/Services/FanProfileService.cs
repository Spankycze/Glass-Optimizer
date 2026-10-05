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
    private const string WatchdogRunValueName = "Spankyho Fan Watchdog";
    private const string AgentTaskName = "SpankyhoOptimizer-FanAgent";
    private const string AgentMutexName = @"Local\SpankyhoOptimizer.FanAgent.SingleInstance";
    private const string WatchdogLoopMutexName = @"Local\SpankyhoOptimizer.FanWatchdogLoop.SingleInstance";
    private static readonly FanAgentRuntimePathSet RuntimePaths = FanAgentRuntimePaths.Create(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private static readonly string HeartbeatPath = RuntimePaths.HeartbeatPath;
    private static readonly string AgentVersionPath = RuntimePaths.VersionPath;
    private static readonly string AgentRestartRequestPath = RuntimePaths.RestartRequestPath;
    private static readonly string AgentHealthPath = RuntimePaths.HealthPath;
    private static readonly string StateDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpankyhoOptimizer");
    private static readonly string StatePath = Path.Combine(StateDirectory, "fan-profile.json");
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
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    public static FanProfile Default { get; } = new();

    public static FanAgentHealth? ReadHealthStatus()
    {
        try
        {
            if (!File.Exists(AgentHealthPath))
                return null;

            var info = new FileInfo(AgentHealthPath);
            lock (HealthCacheGate)
            {
                if (_cachedHealth != null &&
                    _cachedHealthWriteUtc == info.LastWriteTimeUtc &&
                    _cachedHealthLength == info.Length)
                {
                    return _cachedHealth;
                }

                _cachedHealth = JsonSerializer.Deserialize<FanAgentHealth>(File.ReadAllText(AgentHealthPath));
                _cachedHealthWriteUtc = info.LastWriteTimeUtc;
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
            if (!File.Exists(StatePath))
            {
                lock (ProfileCacheGate)
                {
                    _cachedProfile = Default;
                    _cachedProfileWriteUtc = DateTime.MinValue;
                    _cachedProfileLength = -1;
                    return _cachedProfile;
                }
            }

            var info = new FileInfo(StatePath);
            DateTime writeUtc = info.LastWriteTimeUtc;
            long length = info.Length;

            lock (ProfileCacheGate)
            {
                if (_cachedProfile != null &&
                    _cachedProfileWriteUtc == writeUtc &&
                    _cachedProfileLength == length)
                {
                    return _cachedProfile;
                }

                FanProfile? profile = JsonSerializer.Deserialize<FanProfile>(File.ReadAllText(StatePath));
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

    public static void Save(FanProfile profile)
    {
        profile = Normalize(profile);
        Directory.CreateDirectory(StateDirectory);
        string json = JsonSerializer.Serialize(profile, IndentedJsonOptions);
        string temp = StatePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, StatePath, true);

        var info = new FileInfo(StatePath);
        lock (ProfileCacheGate)
        {
            _cachedProfile = profile;
            _cachedProfileWriteUtc = info.LastWriteTimeUtc;
            _cachedProfileLength = info.Length;
        }

        EnsureStartupAgentRegistered();
        if (profile.AutoCurveEnabled || profile.ChannelCurveEnabled.Any(x => x) || profile.CpuTempTargetEnabled)
            StartAgentIfNeeded();
    }

    public static bool OpenFansFromVoice() => OpenSectionFromVoice("lianli");

    public static bool OpenSectionFromVoice(string section)
    {
        string normalized = (section ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0 ||
            normalized.Any(c => !(char.IsLetterOrDigit(c) || c == '-')))
            return false;

        string exe = ResolveMainAppPath();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return false;

        return ProcessRunner.StartDetached(
            exe,
            "--open-section=" + normalized,
            useShellExecute: true);
    }

    public static bool ApplyVoicePreset(string preset)
    {
        FanProfile current = Load();
        string key = (preset ?? string.Empty).Trim().ToLowerInvariant();

        if (key is "auto" or "auto65")
        {
            Save(current with
            {
                TargetRpm = 1620,
                CpuTempTargetEnabled = true,
                CpuTempTargetC = 65,
                MotherboardPwm = false,
                AutoCurveEnabled = false,
                ChannelCurveEnabled = new[] { false, false, false, false }
            });
            LianLiSlInfinityService.SetAllFanRpm(1620);
            StartAgentIfNeeded();
            return true;
        }

        int rpm = key switch
        {
            "night" or "noc" => 700,
            "quiet" or "silent" or "tichy" => 1100,
            "balanced" or "normal" => 1620,
            "performance" or "gaming" or "vykon" => 2100,
            _ => 0
        };
        if (rpm == 0) return false;

        Save(current with
        {
            TargetRpm = rpm,
            CpuTempTargetEnabled = false,
            AutoCurveEnabled = false,
            ChannelCurveEnabled = new[] { false, false, false, false },
            MotherboardPwm = false
        });
        LianLiSlInfinityService.SetAllFanRpm(rpm);
        return true;
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
            : string.Empty;
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

    private static void CleanupLegacyWatchdogTasksBestEffort()
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
                _ = ProcessRunner.RunAsync(
                        "schtasks.exe",
                        $"/Delete /TN \"{taskName}\" /F",
                        TimeSpan.FromSeconds(3),
                        CancellationToken.None,
                        logNonZeroExitAsError: false)
                    .GetAwaiter()
                    .GetResult();
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

    private static bool EnsureFanAgentTaskRegisteredBestEffort(string exe)
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
            ProcessResult result = ProcessRunner.RunAsync(
                    "schtasks.exe",
                    $"/Create /F /TN \"{AgentTaskName}\" /XML \"{xmlPath}\"",
                    TimeSpan.FromSeconds(8),
                    CancellationToken.None,
                    logNonZeroExitAsError: false)
                .GetAwaiter()
                .GetResult();
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
        string exe = ResolveRuntimeHostPath();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;

        CleanupLegacyWatchdogTasksBestEffort();
        string protectedRuntimeHost = Path.Combine(
            AppContext.BaseDirectory,
            "SpankyhoOptimizer.RuntimeHost.exe");
        HardwareTelemetryBridgeService.EnsureRegisteredBestEffort(protectedRuntimeHost);

        bool taskReady = EnsureFanAgentTaskRegisteredBestEffort(exe);
        using RegistryKey? run = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (taskReady)
            run?.DeleteValue(RunValueName, throwOnMissingValue: false);
        else
            run?.SetValue(RunValueName, $"\"{exe}\" --fan-agent", RegistryValueKind.String);
        run?.DeleteValue(WatchdogRunValueName, throwOnMissingValue: false);
    }

    public static void EnsureAgentUpdated()
    {
        try
        {
            string currentVersion = typeof(FanProfileService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            string runningVersion = File.Exists(AgentVersionPath)
                ? File.ReadAllText(AgentVersionPath).Trim()
                : string.Empty;

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

    private static bool IsHeartbeatFresh(TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(HeartbeatPath))
                return false;

            DateTime last = File.GetLastWriteTimeUtc(HeartbeatPath);
            if (DateTime.UtcNow - last > maxAge)
                return false;

            string payload = File.ReadAllText(HeartbeatPath).Trim();
            string pidText = payload.Split('|')[0].Trim();
            if (!int.TryParse(pidText, out int pid) || pid <= 0)
                return false;

            using Process process = Process.GetProcessById(pid);
            if (process.HasExited)
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
        try
        {
            string currentVersion = typeof(FanProfileService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            string runningVersion = File.Exists(AgentVersionPath) ? File.ReadAllText(AgentVersionPath).Trim() : string.Empty;
            bool needsUpdate = FanAgentVersionPolicy.ShouldRequestUpdate(
                currentVersion,
                runningVersion);

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
            if (!force && now - _lastHeartbeatWriteUtc < TimeSpan.FromSeconds(10))
                return;

            string? dir = Path.GetDirectoryName(HeartbeatPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            string temp = HeartbeatPath + ".tmp";
            File.WriteAllText(temp, $"{Environment.ProcessId}|{now:O}");
            File.Move(temp, HeartbeatPath, true);
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
            if (!force && now - _lastHealthWriteUtc < TimeSpan.FromSeconds(5))
                return;

            string? directory = Path.GetDirectoryName(AgentHealthPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string version = typeof(FanProfileService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var health = new FanAgentHealth(
                now,
                Environment.ProcessId,
                version,
                hidOk && telemetry.Celsius.HasValue,
                hidOk,
                lastHidSuccessUtc,
                lastError,
                telemetry.Celsius,
                rpms?.Take(4).ToArray() ?? Array.Empty<int>(),
                profile.CpuTempTargetEnabled,
                powerMode,
                gameActive,
                realIdle,
                Math.Round(loadPercent, 1),
                telemetry.PackagePowerW,
                telemetry.EffectiveClockMHz,
                ai.Enabled,
                ai.Mode,
                ai.AnomalyScore,
                ai.Confidence,
                ai.PredictedTemp10s,
                ai.Prediction,
                ai.LearnedBaseline,
                ai.LastAction,
                ai.GamePhase);

            string temp = AgentHealthPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(health));
            File.Move(temp, AgentHealthPath, true);
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

    private static bool HasUpdateRestartRequest()
    {
        try
        {
            if (!File.Exists(AgentRestartRequestPath))
                return false;

            string requested = File.ReadAllText(AgentRestartRequestPath).Trim();
            return FanAgentRestartPolicy.ShouldRestart(requested);
        }
        catch
        {
            return true;
        }
    }

    public static async Task RunAgentAsync(CancellationToken token = default)
    {
        using var mutex = new Mutex(false, AgentMutexName);
        bool owns;
        try { owns = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns) return;

        FanTrayIconService? tray = null;
        SpankyVoiceListenerService? voiceListener = null;
        bool restartRequested = false;
        try
        {
            LegacyRamGuardCleanupService.CleanupBestEffort();
            MarkAgentVersionRunning();
            WriteHeartbeat(force: true);
            EnsureStartupAgentRegistered();

            try { tray = new FanTrayIconService(); }
            catch (Exception ex) { LogService.Error("lianli", "Fan tray icon startup failed", ex); }

            try
            {
                voiceListener = new SpankyVoiceListenerService();
                await voiceListener.StartAsync(token).ConfigureAwait(false);
                if (VoiceConversationWarmupPolicy.ShouldWarmGemini(
                    ChatGptPlanAuthService.IsConnected,
                    VoiceCloudAiSettingsService.HasApiKey))
                {
                    _ = Task.Run(() => SpankyConversationService.WarmupAsync(token), token);
                }
            }
            catch (Exception ex)
            {
                LogService.Error("voice", "Spanky Voice AI background listener failed", ex);
                if (voiceListener != null)
                {
                    await voiceListener.DisposeAsync().ConfigureAwait(false);
                    voiceListener = null;
                }
            }

            await ApplySavedProfileAsync(token).ConfigureAwait(false);

            var adaptivePower = new AdaptivePowerRuntimeService();
            var aiRuntime = new AiSmartRuntimeService();
            var aiSystemRuntime = new AiSystemResourceManagerService();
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
            DateTime? lastHidSuccessUtc = null;
            string? lastHidError = null;
            string? lastLoggedHidError = null;
            DateTime lastHidErrorLogUtc = DateTime.MinValue;
            bool hidWasFaulted = false;
            double? filteredTemp = null;
            double? previousFilteredTemp = null;
            FanProfile initial = Load();
            int targetModeRpm = Math.Clamp(initial.TargetRpm, 700, LianLiSlInfinityService.MaximumRpm);
            int auto65BaseRpm = Math.Clamp(
                Math.Max(initial.TargetRpm, 1620),
                1620,
                LianLiSlInfinityService.MaximumRpm);

            while (!token.IsCancellationRequested)
            {
                if (HasUpdateRestartRequest())
                {
                    LogService.Info("lianli", "FAN_AGENT_UPDATE_RESTART_ACCEPTED");
                    restartRequested = true;
                    break;
                }

                WriteHeartbeat();
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

                FanProfile profile = Load();
                CpuTemperatureService.CpuTelemetrySample telemetry = HardwareTelemetryBridgeService.ReadBestTelemetry();
                int[]? liveRpms = null;
                FanTrayState trayState = FanTrayState.Ok;

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
                    if (changed || now - lastHidErrorLogUtc >= TimeSpan.FromSeconds(30))
                    {
                        lastLoggedHidError = lastHidError;
                        lastHidErrorLogUtc = now;
                        LogService.Error("lianli", "Fan tray RPM read failed", ex);
                    }
                }

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

                if (telemetry.Celsius is double temp && temp > 5 && temp < 150)
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
                            DateTime.UtcNow - lastApplyUtc[0] >= TimeSpan.FromSeconds(20);

                        if (materiallyChanged || stale)
                        {
                            try
                            {
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
                            int[] activeReadback = liveRpms.Where(x => x > 100).ToArray();
                            if (activeReadback.Length > 0)
                            {
                                double averageReadback = activeReadback.Average();
                                if (averageReadback < targetModeRpm - 220)
                                    rpmUnderTargetVotes++;
                                else
                                    rpmUnderTargetVotes = 0;

                                if (rpmUnderTargetVotes >= 3)
                                {
                                    try
                                    {
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
                        for (int channel = 0; channel < 4; channel++)
                        {
                            bool enabled = profile.ChannelCurveEnabled[channel] || profile.AutoCurveEnabled;
                            if (!enabled) continue;

                            int rpm = profile.ChannelCurveEnabled[channel]
                                ? CalculateChannelCurveRpm(profile, channel, filteredTemp.Value)
                                : CalculateCurveRpm(profile, filteredTemp.Value);

                            bool materiallyChanged = lastAppliedRpm[channel] < 0 || Math.Abs(rpm - lastAppliedRpm[channel]) >= 75;
                            bool stale = DateTime.UtcNow - lastApplyUtc[channel] >= TimeSpan.FromSeconds(20);
                            if (!materiallyChanged && !stale) continue;

                            try
                            {
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

                try
                {
                    await adaptivePower.TickAsync(
                        telemetry,
                        liveRpms,
                        profile.CpuTempTargetEnabled,
                        profile.AiSmartRuntimeEnabled,
                        aiDecision,
                        token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogService.Error("power", "Adaptive runtime power tick failed", ex);
                }

                try
                {
                    await aiSystemRuntime.TickAsync(
                        adaptivePower.RealIdle,
                        adaptivePower.LoadPercent,
                        token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogService.Error("ai-system", "AI System resource runtime tick failed", ex);
                }

                int[]? tooltipRpms = liveRpms == null
                    ? null
                    : smoothedRpms.Select(x => (int)Math.Round(x / 15d) * 15).ToArray();

                tray?.Update(
                    BuildTrayTooltip(
                        telemetry,
                        tooltipRpms,
                        adaptivePower.ModeName,
                        profile.CpuTempTargetEnabled,
                        adaptivePower.GameActive,
                        aiDecision),
                    trayState);

                WriteHealthStatus(
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

                bool dynamicFanControl =
                    profile.CpuTempTargetEnabled ||
                    profile.AutoCurveEnabled ||
                    profile.ChannelCurveEnabled.Any(x => x);
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
            if (voiceListener != null)
            {
                try { await voiceListener.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            tray?.Dispose();
            CpuTemperatureService.Dispose();
            try { mutex.ReleaseMutex(); } catch { }
            try { File.Delete(HeartbeatPath); } catch { }
            try { File.Delete(AgentHealthPath); } catch { }

            if (restartRequested)
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
                    "--open-section=lianli",
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
                    bool anyPerChannelCurve = profile.ChannelCurveEnabled.Any(x => x);

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

        if (rpms == null || rpms.Length == 0)
        {
            fanText = "RPM —";
        }
        else
        {
            int[] active = rpms.Take(4).Where(x => x > 100).ToArray();
            fanText = active.Length == 0
                ? "RPM 0"
                : "Fan " + string.Join("/", active) + " RPM";
        }

        string mode = auto65 ? "AUTO65" : powerMode;
        if (gameActive)
            mode += " • DIABLO PERF";
        if (ai.Enabled)
            mode += ai.AnomalyScore >= 55 ? $" • AI {ai.AnomalyScore:0}" : " • AI AUTO";
        return $"CPU {cpu} • {fanText} • {mode}";
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

    private static int InterpolateCurve(double temperature,
        int t1, int r1, int t2, int r2, int t3, int r3, int t4, int r4, int fallback)
    {
        var p = new[] { (T: t1, R: r1), (T: t2, R: r2), (T: t3, R: r3), (T: t4, R: r4) };
        if (temperature <= p[0].T) return p[0].R;
        if (temperature >= p[3].T) return p[3].R;

        for (int i = 0; i < p.Length - 1; i++)
        {
            if (temperature > p[i + 1].T) continue;
            double span = Math.Max(1, p[i + 1].T - p[i].T);
            double t = (temperature - p[i].T) / span;
            return Math.Clamp(
                (int)Math.Round(p[i].R + (p[i + 1].R - p[i].R) * t),
                LianLiSlInfinityService.MinimumSafeRpm,
                LianLiSlInfinityService.MaximumRpm);
        }

        return fallback;
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
