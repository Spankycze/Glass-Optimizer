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
    int Boost);

public sealed class AdaptivePowerRuntimeService
{
    private const string CpuSubgroup = "54533251-82be-4824-96c1-47b60b740d00";
    private const string EppSetting = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    private const string BoostSetting = "be337238-0d82-4146-a960-4f3749d470c7";
    public static string StatusPath => AdaptivePowerRuntimePathPolicy.WritablePath;

    private enum RuntimeMode
    {
        Unknown,
        Idle,
        Balanced,
        Performance
    }

    private sealed record ProcessCpuSnapshot(TimeSpan Cpu, string Name);
    private sealed record ProcessLoadSample(double TotalPercent, string TopProcess, bool GameActive);

    private readonly Dictionary<int, ProcessCpuSnapshot> _previous = new();
    private DateTime _previousAt = DateTime.UtcNow;
    private RuntimeMode _mode = RuntimeMode.Unknown;
    private int _idleVotes;
    private int _performanceVotes;
    private int _exitPerformanceVotes;
    private bool _loadInitialized;
    private double _smoothedLoad;
    private DateTime _lastApplyUtc = DateTime.MinValue;
    private DateTime _lastLogUtc = DateTime.MinValue;
    private DateTime _lastCoolingLimitLogUtc = DateTime.MinValue;
    private DateTime _lastBaseHealUtc = DateTime.MinValue;
    private DateTime _lastStatusWriteUtc = DateTime.MinValue;
    private DateTime _lastStatusFailureUtc = DateTime.MinValue;
    private DateTime _lastStatusErrorLogUtc = DateTime.MinValue;
    private DateTime _lastProcessSampleUtc = DateTime.MinValue;
    private ProcessLoadSample _cachedProcessSample = new(0, "none", false);
    private string _lastStatusKey = string.Empty;
    private string _lastBlocker = string.Empty;

    public string LastBlocker => _lastBlocker;
    public string ModeName => _mode.ToString();
    public bool GameActive { get; private set; }
    public bool RealIdle { get; private set; }
    public double LoadPercent { get; private set; }

    public async Task TickAsync(
        CpuTemperatureService.CpuTelemetrySample telemetry,
        int[]? fanRpms,
        bool auto65Enabled,
        bool aiSmartEnabled,
        AiSmartRuntimeDecision aiDecision,
        CancellationToken token)
    {
        var processSample = SampleProcessLoad();
        double rawLoad = telemetry.LoadPercent ?? processSample.TotalPercent;
        _smoothedLoad = _loadInitialized
            ? _smoothedLoad * 0.68 + rawLoad * 0.32
            : rawLoad;
        _loadInitialized = true;

        double load = Math.Clamp(_smoothedLoad, 0, 100);
        _lastBlocker = processSample.TopProcess;

        bool realIdle = IsRealIdle(
            load,
            telemetry.EffectiveClockMHz,
            telemetry.PackagePowerW);

        GameActive = processSample.GameActive;
        RealIdle = realIdle;
        LoadPercent = load;

        var runtimeConfig = AiSystemResourceManagerService.LoadConfig();
        bool powerManaged = runtimeConfig.Enabled && runtimeConfig.PowerPlanEnabled;
        if (!powerManaged)
        {
            await RestoreAllAiSettingsAsync(token).ConfigureAwait(false);
            _mode = RuntimeMode.Unknown;
            _lastApplyUtc = DateTime.MinValue;
            _lastBaseHealUtc = DateTime.MinValue;
        }

        RuntimeMode desired = ChooseMode(
            load,
            rawLoad,
            realIdle,
            processSample.GameActive,
            aiSmartEnabled,
            aiDecision,
            telemetry.PackagePowerW,
            telemetry.EffectiveClockMHz);
        bool verifyDue = DateTime.UtcNow - _lastApplyUtc > TimeSpan.FromSeconds(30);

        if (powerManaged && (desired != _mode || verifyDue) &&
            TryGetActiveSpankyScheme(out string? activeGuid) &&
            !string.IsNullOrWhiteSpace(activeGuid))
        {
            var values = GetModeValues(desired);
            bool drift = !TryReadAcSetting(activeGuid!, EppSetting, out int currentEpp) ||
                         !TryReadAcSetting(activeGuid!, BoostSetting, out int currentBoost) ||
                         currentEpp != values.Epp ||
                         currentBoost != values.Boost;

            if (drift)
            {
                await ApplyAsync(activeGuid!, values.Epp, values.Boost, token).ConfigureAwait(false);
                LogService.Info(
                    "power",
                    $"ADAPTIVE_POWER mode={desired} load={load:0.0}% raw={rawLoad:0.0}% game={processSample.GameActive} epp={values.Epp} boost={values.Boost} blocker={_lastBlocker}");
            }

            _mode = desired;
            _lastApplyUtc = DateTime.UtcNow;
        }

        if (powerManaged && DateTime.UtcNow - _lastBaseHealUtc > TimeSpan.FromSeconds(30) &&
            TryGetActiveSpankyScheme(out string? healGuid) &&
            !string.IsNullOrWhiteSpace(healGuid))
        {
            await EnsureBaseSettingsAsync(healGuid!, token).ConfigureAwait(false);
            _lastBaseHealUtc = DateTime.UtcNow;
        }

        bool statusPlanActive = TryGetActiveSpankyScheme(out string? statusGuid);
        var currentValues = (Epp: -1, Boost: -1);
        if (statusPlanActive && statusGuid != null)
        {
            if (TryReadAcSetting(statusGuid, EppSetting, out int actualEpp)) currentValues.Epp = actualEpp;
            if (TryReadAcSetting(statusGuid, BoostSetting, out int actualBoost)) currentValues.Boost = actualBoost;
        }
        MaybeWriteStatus(new AdaptivePowerRuntimeStatus(
            DateTime.UtcNow,
            !powerManaged ? "Observation (AI off)" : !statusPlanActive ? "Observation (no Spanky plan)" : (_mode == RuntimeMode.Unknown ? desired : _mode).ToString(),
            Math.Round(load, 1),
            _lastBlocker,
            telemetry.Celsius,
            telemetry.EffectiveClockMHz,
            telemetry.PackagePowerW,
            realIdle,
            processSample.GameActive,
            currentValues.Epp,
            currentValues.Boost));

        if (DateTime.UtcNow - _lastLogUtc > TimeSpan.FromSeconds(30))
        {
            LogService.Info(
                "hardware",
                $"CPU_RUNTIME temp={telemetry.Celsius:0.0}C load={load:0.0}% raw={rawLoad:0.0}% clock={telemetry.EffectiveClockMHz:0}MHz power={telemetry.PackagePowerW:0.0}W realIdle={realIdle} mode={_mode} blocker={_lastBlocker}");
            _lastLogUtc = DateTime.UtcNow;
        }

        if (auto65Enabled &&
            telemetry.Celsius is double temp &&
            temp >= 68 &&
            fanRpms != null &&
            fanRpms.Any(x => x >= 1850) &&
            DateTime.UtcNow - _lastCoolingLimitLogUtc > TimeSpan.FromMinutes(2))
        {
            LogService.Info(
                "lianli",
                $"AUTO65_COOLING_LIMIT temp={temp:0.0}C maxFan={fanRpms.Max()}rpm cpuPerfUncapped=true");
            _lastCoolingLimitLogUtc = DateTime.UtcNow;
        }
    }

    public static AdaptivePowerRuntimeStatus? ReadStatus()
    {
        try
        {
            string path = AdaptivePowerRuntimePathPolicy.ResolveLoadPath();
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<AdaptivePowerRuntimeStatus>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private void MaybeWriteStatus(AdaptivePowerRuntimeStatus status)
    {
        string key = $"{status.Mode}|{status.GameActive}|{status.RealIdle}|{status.Epp}|{status.Boost}|{status.Blocker}";
        DateTime now = DateTime.UtcNow;
        bool changed = !string.Equals(key, _lastStatusKey, StringComparison.Ordinal);
        if (!changed && now - _lastStatusWriteUtc < TimeSpan.FromSeconds(5))
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
            _lastStatusKey = key;
        }
        catch (Exception ex)
        {
            _lastStatusFailureUtc = now;
            if (now - _lastStatusErrorLogUtc >= TimeSpan.FromMinutes(5))
            {
                _lastStatusErrorLogUtc = now;
                LogService.Error("power", "Adaptive runtime status write failed", ex);
            }
        }
    }

    internal static bool IsStatusWriteRetryDue(
        DateTime nowUtc,
        DateTime lastFailureUtc) =>
        lastFailureUtc == DateTime.MinValue ||
        nowUtc - lastFailureUtc >= TimeSpan.FromSeconds(30);

    private static bool IsRealIdle(double load, double? clockMHz, double? packagePowerW)
    {
        bool loadOk = load <= 10;
        bool clockOk = !clockMHz.HasValue || clockMHz.Value <= 2200;
        bool powerOk = !packagePowerW.HasValue || packagePowerW.Value <= 45;
        return loadOk && clockOk && powerOk;
    }

    private RuntimeMode ChooseMode(
        double load,
        double rawLoad,
        bool realIdle,
        bool gameActive,
        bool aiSmartEnabled,
        AiSmartRuntimeDecision aiDecision,
        double? packagePowerW,
        double? clockMHz)
    {
        load = Math.Clamp(load, 0, 100);
        rawLoad = Math.Clamp(rawLoad, 0, 100);

        if (gameActive)
        {
            _performanceVotes = 3;
            _idleVotes = 0;
            _exitPerformanceVotes = 0;
            return RuntimeMode.Performance;
        }

        bool aiPerformanceSignal =
            aiSmartEnabled &&
            (aiDecision.PreferPerformance ||
             (aiDecision.Confidence >= 55 && aiDecision.PredictedTemp10s >= 66) ||
             (packagePowerW is double watts && watts >= 78) ||
             (clockMHz is double mhz && mhz >= 3600 && load >= 24) ||
             (load >= 42 && rawLoad >= 48));

        bool aiIdleSignal =
            aiSmartEnabled &&
            aiDecision.PreferIdle &&
            aiDecision.Confidence >= 60 &&
            aiDecision.AnomalyScore < 35;

        if (rawLoad >= 55 || aiPerformanceSignal)
            _performanceVotes = 3;
        else if (load >= 35)
            _performanceVotes++;
        else
            _performanceVotes = 0;

        if (realIdle || aiIdleSignal)
            _idleVotes++;
        else
            _idleVotes = 0;

        if (_mode == RuntimeMode.Performance)
        {
            bool powerCoolEnough =
                !packagePowerW.HasValue ||
                packagePowerW.Value < 70;

            bool sustainedExitSignal =
                !aiPerformanceSignal &&
                ((load < 30 && powerCoolEnough) || load < 20);

            if (sustainedExitSignal)
                _exitPerformanceVotes++;
            else
                _exitPerformanceVotes = 0;

            if (_exitPerformanceVotes < 4)
                return RuntimeMode.Performance;

            _exitPerformanceVotes = 0;
        }
        else
        {
            _exitPerformanceVotes = 0;
        }

        if (_performanceVotes >= 2)
            return RuntimeMode.Performance;

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

    private static async Task EnsureBaseSettingsAsync(string guid, CancellationToken token)
    {
        var settings = new (string Setting, int Value, string Name)[]
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

        int repaired = 0;
        foreach (var item in settings)
        {
            if (TryReadAcSetting(guid, item.Setting, out int current) && current == item.Value)
                continue;

            if (await MutateAiSettingAsync(guid, item.Setting, item.Value, token).ConfigureAwait(false))
            {
                repaired++;
                LogService.Info("power", $"ADAPTIVE_SELF_HEAL setting={item.Name} value={item.Value}");
            }
        }

        if (repaired > 0)
        {
            await ProcessRunner.RunAsync(
                "powercfg.exe",
                $"/setactive {guid}",
                TimeSpan.FromSeconds(8),
                token).ConfigureAwait(false);
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
        var write = await ProcessRunner.RunAsync("powercfg.exe",
            $"/setacvalueindex {guid} {CpuSubgroup} {setting} {value}", TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
        if (!write.Success || !TryReadAcSetting(guid, setting, out int actual) || actual != value)
            throw new InvalidOperationException("AI power read-back failed: " + setting + " " + write.StdErr);
    }

    private static async Task<bool> MutateAiSettingAsync(string guid, string setting, int value, CancellationToken token)
    {
        await AiPowerGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!TryReadAcSetting(guid, setting, out int current))
                return false; // missing setting has no verifiable original
            var rows = await LoadAiPowerAsync(token).ConfigureAwait(false);
            string key = guid + "|" + setting;
            rows.TryGetValue(key, out var previous);
            if (previous?.Suppressed == true) return false;
            if (previous?.Managed == true && current != previous.Applied)
            {
                rows[key] = previous with { Managed = false, Suppressed = true };
                await SaveAiPowerAsync(rows, token).ConfigureAwait(false);
                LogService.Info("power", "AI_POWER_MANUAL_OVERRIDE preserved " + key);
                return false;
            }
            if (current == value) return false;
            var row = new AiPowerOwnership(guid, setting,
                previous?.Managed == true ? previous.Original : current, value, true, false);
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
        finally { AiPowerGate.Release(); }
    }

    public static async Task RestoreAllAiSettingsAsync(CancellationToken token = default)
    {
        await AiPowerGate.WaitAsync(token).ConfigureAwait(false);
        try
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
                var refresh = await ProcessRunner.RunAsync("powercfg.exe", "/setactive " + active,
                    TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
                if (!refresh.Success) throw new InvalidOperationException("AI power restore refresh failed");
            }
        }
        finally { AiPowerGate.Release(); }
    }

    private static async Task ApplyAsync(string guid, int epp, int boost, CancellationToken token)
    {
        try
        {
            bool changed = await MutateAiSettingAsync(guid, EppSetting, epp, token).ConfigureAwait(false);
            changed |= await MutateAiSettingAsync(guid, BoostSetting, boost, token).ConfigureAwait(false);
            if (!changed) return;
            var active = await ProcessRunner.RunAsync("powercfg.exe", $"/setactive {guid}",
                TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
            if (!active.Success) throw new InvalidOperationException("AI adaptive refresh failed");
        }
        catch
        {
            using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await RestoreAllAiSettingsAsync(rollback.Token).ConfigureAwait(false);
            throw;
        }
    }

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

    private ProcessLoadSample SampleProcessLoad()
    {
        DateTime now = DateTime.UtcNow;
        if (_lastProcessSampleUtc != DateTime.MinValue &&
            now - _lastProcessSampleUtc < TimeSpan.FromSeconds(2))
        {
            return _cachedProcessSample;
        }

        double elapsed = Math.Max(0.25, (now - _previousAt).TotalSeconds);
        int logical = Math.Max(1, Environment.ProcessorCount);
        var next = new Dictionary<int, ProcessCpuSnapshot>();
        double totalCpuSeconds = 0;
        double topCpuSeconds = 0;
        string topProcess = string.Empty;
        bool gameActive = false;

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                TimeSpan cpu = process.TotalProcessorTime;
                string name = process.ProcessName;
                if (name.Equals("DiabloImmortal", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Diablo Immortal", StringComparison.OrdinalIgnoreCase))
                {
                    gameActive = true;
                }
                next[process.Id] = new ProcessCpuSnapshot(cpu, name);

                if (_previous.TryGetValue(process.Id, out ProcessCpuSnapshot? previous))
                {
                    double delta = (cpu - previous.Cpu).TotalSeconds;
                    if (delta > 0)
                    {
                        totalCpuSeconds += delta;
                        if (delta > topCpuSeconds)
                        {
                            topCpuSeconds = delta;
                            topProcess = name;
                        }
                    }
                }
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }

        _previous.Clear();
        foreach (var item in next)
            _previous[item.Key] = item.Value;
        _previousAt = now;

        double totalPercent = Math.Clamp(
            totalCpuSeconds / elapsed * 100d / logical,
            0d,
            100d);

        double topPercent = Math.Clamp(
            topCpuSeconds / elapsed * 100d / logical,
            0d,
            100d);

        string blocker = string.IsNullOrWhiteSpace(topProcess)
            ? "none"
            : $"{topProcess} {topPercent:0.0}%";

        _cachedProcessSample = new ProcessLoadSample(totalPercent, blocker, gameActive);
        _lastProcessSampleUtc = now;
        return _cachedProcessSample;
    }
}
