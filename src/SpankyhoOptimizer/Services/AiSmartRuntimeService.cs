using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed record AiSmartRuntimeDecision(
    bool Enabled,
    string Mode,
    string GamePhase,
    double AnomalyScore,
    double Confidence,
    double PredictedTemp10s,
    string Prediction,
    string LearnedBaseline,
    string LastAction,
    int FanRpmBias,
    bool PreferPerformance,
    bool PreferIdle);

internal sealed record AiSmartWorkloadBaseline(
    int Samples,
    double AvgTempC,
    double AvgPowerW,
    double AvgLoadPercent,
    double AvgRpm,
    double AvgClockMHz,
    DateTime UpdatedUtc);

internal sealed record AiSmartRuntimeModel(
    int Samples,
    double AvgTempC,
    double AvgPowerW,
    double AvgLoadPercent,
    double AvgRpm,
    double AvgClockMHz,
    DateTime UpdatedUtc,
    Dictionary<string, AiSmartWorkloadBaseline>? Workloads = null);

public sealed class AiSmartRuntimeService
{
    private AiSmartRuntimeModel _model = LoadModel();
    private DateTime _lastPersistUtc = DateTime.MinValue;
    private DateTime _lastModelSampleUtc = DateTime.MinValue;
    private AiWorkloadClass _lastModelSampleWorkload = AiWorkloadClass.Unknown;
    private bool _modelDirty;
    private DateTime _lastActionUtc = DateTime.MinValue;
    private DateTime _lastActionLogUtc = DateTime.MinValue;
    private string _lastAction = "none";
    private string _lastLoggedAction = string.Empty;
    private double? _previousTemp;
    private DateTime _previousTempUtc = DateTime.MinValue;
    private double _smoothedTrend;
    private double _smoothedAnomaly;
    private int _highAnomalyVotes;
    private AiWorkloadClass _lastConfidenceWorkload = AiWorkloadClass.Unknown;
    private int _workloadStableSamples;

    public AiSmartRuntimeDecision Tick(
        bool enabled,
        CpuTemperatureService.CpuTelemetrySample telemetry,
        int[]? fanRpms,
        int currentTargetRpm,
        bool hidOk,
        bool gameActive,
        bool realIdle,
        double loadPercent,
        string blocker,
        AiWorkloadClass workload = AiWorkloadClass.Unknown)
    {
        if (!enabled)
        {
            return new AiSmartRuntimeDecision(
                false, "OFF", gameActive ? "Detected" : "None", 0, 0, telemetry.Celsius ?? 0,
                "AI Smart Runtime je vypnutý.", BaselineText(workload), _lastAction, 0, false, false);
        }

        DateTime nowUtc = DateTime.UtcNow;
        AiSmartWorkloadBaseline baseline = ResolveBaseline(workload);
        double? liveTemp = telemetry.Celsius;
        bool temperatureAvailable =
            liveTemp.HasValue &&
            liveTemp.Value > 5 &&
            liveTemp.Value < 150;
        bool powerAvailable = telemetry.PackagePowerW.HasValue;

        double temp = temperatureAvailable ? liveTemp!.Value : baseline.AvgTempC;
        double power = telemetry.PackagePowerW ?? baseline.AvgPowerW;
        double clock = telemetry.EffectiveClockMHz ?? baseline.AvgClockMHz;
        double load = Math.Clamp(loadPercent, 0, 100);
        double avgRpm = AverageActiveRpm(fanRpms);
        double trend = 0;

        if (temperatureAvailable)
        {
            trend = _previousTemp.HasValue && _previousTempUtc != DateTime.MinValue
                ? NormalizeTemperatureTrend(
                    temp - _previousTemp.Value,
                    Math.Max(0.1, (nowUtc - _previousTempUtc).TotalSeconds))
                : 0;
            _previousTemp = temp;
            _previousTempUtc = nowUtc;
            _smoothedTrend = _smoothedTrend == 0
                ? trend
                : _smoothedTrend * 0.72 + trend * 0.28;
        }
        else
        {
            _smoothedTrend *= 0.70;
        }

        string gamePhase = DetectGamePhase(gameActive, load, power, clock);
        double rawAnomaly = CalculateAnomaly(
            temp, power, load, avgRpm, currentTargetRpm, hidOk, blocker, baseline);
        _smoothedAnomaly = _smoothedAnomaly == 0
            ? rawAnomaly
            : _smoothedAnomaly * 0.70 + rawAnomaly * 0.30;

        if (_smoothedAnomaly >= 65)
            _highAnomalyVotes++;
        else
            _highAnomalyVotes = Math.Max(0, _highAnomalyVotes - 1);

        double anomaly = _highAnomalyVotes >= 2
            ? Math.Max(_smoothedAnomaly, rawAnomaly)
            : Math.Min(_smoothedAnomaly, 64);

        double confidence = CalculateConfidence(
            hidOk,
            temperatureAvailable,
            powerAvailable,
            baseline);

        if (workload == AiWorkloadClass.Unknown)
        {
            _workloadStableSamples = 0;
        }
        else if (workload == _lastConfidenceWorkload)
        {
            _workloadStableSamples = Math.Min(5, _workloadStableSamples + 1);
        }
        else
        {
            _lastConfidenceWorkload = workload;
            _workloadStableSamples = 0;
        }

        confidence = AdjustConfidenceForWorkloadStability(
            confidence,
            workload,
            _workloadStableSamples);

        double predictedTemp10s = PredictTemperature10s(
            temp, power, load, _smoothedTrend, baseline);

        bool learningSampleDue = AiSmartLearningPolicy.IsSampleDue(
            _lastModelSampleUtc,
            _lastModelSampleWorkload,
            workload,
            nowUtc);
        if (learningSampleDue &&
            hidOk &&
            temperatureAvailable &&
            powerAvailable &&
            telemetry.EffectiveClockMHz.HasValue &&
            anomaly < 60)
        {
            UpdateModel(temp, power, load, avgRpm, clock, workload);
            _lastModelSampleUtc = nowUtc;
            _lastModelSampleWorkload = workload;
        }

        int fanBias = AiFanBiasPolicy.Compute(
            temp,
            predictedTemp10s,
            power,
            load,
            _smoothedTrend,
            anomaly,
            gamePhase,
            confidence,
            workload);
        bool preferPerformance = AiPowerDemandPolicy.HasPerformanceDemand(
            gameActive,
            load,
            powerAvailable ? power : null,
            telemetry.EffectiveClockMHz,
            anomaly,
            workload,
            confidence);

        bool preferIdle =
            temperatureAvailable &&
            powerAvailable &&
            !gameActive &&
            realIdle &&
            anomaly < 35 &&
            load <= 8 &&
            power > 0 && power <= Math.Max(45, baseline.AvgPowerW * 0.82);

        string mode = !temperatureAvailable
            ? "SensorFallback"
            : preferPerformance
                ? "Performance"
                : preferIdle
                    ? "Idle"
                    : workload == AiWorkloadClass.Background
                        ? "BackgroundEfficient"
                        : anomaly >= 55
                            ? "Watch"
                            : "Adaptive";

        string prediction = BuildPrediction(
            temp,
            predictedTemp10s,
            power,
            _smoothedTrend,
            fanBias,
            anomaly,
            confidence,
            gamePhase,
            temperatureAvailable);
        MaybeSetAction(
            anomaly,
            fanBias,
            hidOk,
            temperatureAvailable,
            avgRpm,
            currentTargetRpm,
            gamePhase);
        PersistModelIfDue(workload);

        return new AiSmartRuntimeDecision(
            true,
            mode,
            gamePhase,
            Math.Round(anomaly, 1),
            Math.Round(confidence, 1),
            Math.Round(predictedTemp10s, 1),
            prediction,
            BaselineText(workload),
            _lastAction,
            fanBias,
            preferPerformance,
            preferIdle);
    }

    public int AdjustAuto65Target(int baseTargetRpm, AiSmartRuntimeDecision decision)
    {
        if (!decision.Enabled)
            return Math.Clamp(baseTargetRpm, 1620, LianLiSlInfinityService.MaximumRpm);

        int target = baseTargetRpm + decision.FanRpmBias;
        if (decision.AnomalyScore >= 85)
            target = Math.Max(target, 1950);

        return Math.Clamp(target, 1620, LianLiSlInfinityService.MaximumRpm);
    }

    private static string DetectGamePhase(bool gameActive, double load, double power, double clock)
    {
        if (!gameActive) return "None";
        if (load >= 55 || power >= 82) return "Loading/Heavy";
        if (load >= 25 || power >= 55 || clock >= 3300) return "Gameplay";
        if (load <= 18 && power <= 48) return "Menu/AFK";
        return "Light";
    }

    private double CalculateAnomaly(
        double temp,
        double power,
        double load,
        double avgRpm,
        int targetRpm,
        bool hidOk,
        string blocker,
        AiSmartWorkloadBaseline baseline)
    {
        double score = 0;

        if (!hidOk)
            score += 45;

        if (targetRpm >= 1000 && avgRpm > 100 && avgRpm < targetRpm - 220)
            score += Math.Min(35, (targetRpm - avgRpm - 220) / 8d);

        if (baseline.Samples >= 20)
        {
            double expectedTemp = baseline.AvgTempC +
                                  Math.Max(0, power - baseline.AvgPowerW) * 0.18 +
                                  Math.Max(0, load - baseline.AvgLoadPercent) * 0.05;
            if (temp > expectedTemp + 7)
                score += Math.Min(35, (temp - expectedTemp - 7) * 3.5);

            if (load < baseline.AvgLoadPercent + 8 &&
                power > baseline.AvgPowerW + 22)
            {
                score += Math.Min(20, (power - baseline.AvgPowerW - 22) * 0.8);
            }
        }

        if (TryParseBlockerPercent(blocker, out double blockerPercent))
        {
            if (blockerPercent >= 14) score += 20;
            else if (blockerPercent >= 9) score += 10;
        }

        if (temp >= 78) score += 25;
        else if (temp >= 70) score += 12;

        return Math.Clamp(score, 0, 100);
    }

    private double CalculateConfidence(
        bool hidOk,
        bool temperatureAvailable,
        bool powerAvailable,
        AiSmartWorkloadBaseline baseline)
    {
        double sampleConfidence = Math.Clamp(baseline.Samples / 250d * 100d, 0, 100);
        bool modelFresh =
            baseline.UpdatedUtc != DateTime.MinValue &&
            DateTime.UtcNow >= baseline.UpdatedUtc &&
            DateTime.UtcNow - baseline.UpdatedUtc <= TimeSpan.FromMinutes(10);

        return AdjustConfidenceForTelemetry(
            sampleConfidence,
            hidOk,
            temperatureAvailable,
            powerAvailable,
            modelFresh);
    }

    internal static double AdjustConfidenceForTelemetry(
        double sampleConfidence,
        bool hidOk,
        bool temperatureAvailable,
        bool powerAvailable,
        bool modelFresh)
    {
        double confidence = Math.Clamp(sampleConfidence, 0, 100);

        if (!hidOk)
            confidence *= 0.55;
        if (!temperatureAvailable)
            confidence *= 0.25;
        if (!powerAvailable)
            confidence *= 0.85;
        if (!modelFresh)
            confidence *= 0.8;

        return Math.Clamp(confidence, 0, 100);
    }

    internal static double AdjustConfidenceForWorkloadStability(
        double confidence,
        AiWorkloadClass workload,
        int stableSamples)
    {
        confidence = Math.Clamp(confidence, 0, 100);
        if (workload == AiWorkloadClass.Game)
            return confidence;
        if (workload == AiWorkloadClass.Unknown)
            return confidence * 0.75;

        double factor = stableSamples switch
        {
            <= 0 => 0.65,
            1 => 0.78,
            2 => 0.90,
            _ => 1.0
        };
        return Math.Clamp(confidence * factor, 0, 100);
    }

    internal static double NormalizeTemperatureTrend(double deltaCelsius, double elapsedSeconds)
    {
        if (!double.IsFinite(deltaCelsius) || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
            return 0;
        return Math.Clamp(deltaCelsius / elapsedSeconds, -3.0, 3.0);
    }

    private double PredictTemperature10s(
        double temp,
        double power,
        double load,
        double trend,
        AiSmartWorkloadBaseline baseline)
    {
        double thermalPush = 0;
        if (baseline.Samples >= 20)
        {
            thermalPush += Math.Max(0, power - baseline.AvgPowerW) * 0.035;
            thermalPush += Math.Max(0, load - baseline.AvgLoadPercent) * 0.008;
        }

        double trendProjection = Math.Clamp(trend * 4.0, -2.5, 5.0);
        double prediction = temp + trendProjection + thermalPush;
        return Math.Clamp(prediction, temp - 3.0, temp + 8.0);
    }

    private static string BuildPrediction(
        double temp,
        double predictedTemp10s,
        double power,
        double trend,
        int fanBias,
        double anomaly,
        double confidence,
        string gamePhase,
        bool temperatureAvailable)
    {
        string direction = trend switch
        {
            >= 0.7 => "teplota rychle roste",
            >= 0.2 => "teplota roste",
            <= -0.5 => "teplota klesá",
            _ => "teplota je stabilní"
        };

        string fan = fanBias > 0
            ? $"předstih +{fanBias} RPM"
            : fanBias < 0
                ? $"zklidnění {fanBias} RPM"
                : "bez korekce RPM";

        string game = gamePhase == "None" ? string.Empty : $" • {gamePhase}";
        if (!temperatureAvailable)
        {
            return $"CPU teplota nedostupná • modelový odhad {temp:0.0} °C → 10 s {predictedTemp10s:0.0} °C • {power:0} W • {fan} • anomaly {anomaly:0} • confidence {confidence:0}%{game}";
        }

        return $"{direction} • teď {temp:0.0} °C → 10 s {predictedTemp10s:0.0} °C • {power:0} W • {fan} • anomaly {anomaly:0} • confidence {confidence:0}%{game}";
    }

    private void MaybeSetAction(
        double anomaly,
        int fanBias,
        bool hidOk,
        bool temperatureAvailable,
        double avgRpm,
        int targetRpm,
        string gamePhase)
    {
        DateTime now = DateTime.UtcNow;

        if (temperatureAvailable &&
            string.Equals(_lastAction, "CPU telemetry fallback", StringComparison.Ordinal))
        {
            SetAction("CPU telemetry restored", now, anomaly, fanBias);
            return;
        }

        if (now - _lastActionUtc < TimeSpan.FromSeconds(20))
            return;

        string? action = null;
        if (!hidOk)
            action = "HID reconnect/watch";
        else if (!temperatureAvailable)
            action = "CPU telemetry fallback";
        else if (targetRpm > 1000 && avgRpm > 100 && avgRpm < targetRpm - 220)
            action = "fan target self-heal";
        else if (anomaly >= 75)
            action = "thermal anomaly protection";
        else if (fanBias >= 150)
            action = "predictive fan preboost";
        else if (gamePhase == "Loading/Heavy")
            action = "game load preboost";

        if (action != null)
            SetAction(action, now, anomaly, fanBias);
    }

    private void SetAction(
        string action,
        DateTime now,
        double anomaly,
        int fanBias)
    {
        _lastAction = action;
        _lastActionUtc = now;

        bool changed = !string.Equals(action, _lastLoggedAction, StringComparison.Ordinal);
        if (changed || now - _lastActionLogUtc >= TimeSpan.FromMinutes(2))
        {
            _lastLoggedAction = action;
            _lastActionLogUtc = now;
            LogService.Info("hardware", $"AI_SMART action={action} anomaly={anomaly:0.0} bias={fanBias}");
        }
    }

    internal static bool IsWorkloadBaselineEligible(
        int samples,
        DateTime updatedUtc,
        DateTime nowUtc) =>
        samples >= 20 &&
        updatedUtc != DateTime.MinValue &&
        nowUtc >= updatedUtc &&
        nowUtc - updatedUtc <= TimeSpan.FromDays(7);

    private AiSmartWorkloadBaseline ResolveBaseline(AiWorkloadClass workload)
    {
        if (workload != AiWorkloadClass.Unknown &&
            _model.Workloads != null &&
            _model.Workloads.TryGetValue(workload.ToString(), out AiSmartWorkloadBaseline? learned) &&
            IsWorkloadBaselineEligible(learned.Samples, learned.UpdatedUtc, DateTime.UtcNow))
        {
            return learned;
        }

        return new AiSmartWorkloadBaseline(
            _model.Samples,
            _model.AvgTempC,
            _model.AvgPowerW,
            _model.AvgLoadPercent,
            _model.AvgRpm,
            _model.AvgClockMHz,
            _model.UpdatedUtc);
    }

    internal static AiSmartWorkloadBaseline LearnWorkloadBaseline(
        AiSmartWorkloadBaseline? current,
        double temp,
        double power,
        double load,
        double rpm,
        double clock,
        DateTime now)
    {
        const double alphaWarmup = 0.10;
        const double alphaStable = 0.025;

        if (current == null || current.Samples <= 0)
        {
            return new AiSmartWorkloadBaseline(
                1,
                temp,
                power,
                load,
                rpm > 100 ? rpm : 1620,
                clock > 100 ? clock : 2800,
                now);
        }

        double alpha = current.Samples < 50 ? alphaWarmup : alphaStable;
        return current with
        {
            Samples = Math.Min(500000, current.Samples + 1),
            AvgTempC = Ewma(current.AvgTempC, temp, alpha),
            AvgPowerW = Ewma(current.AvgPowerW, power, alpha),
            AvgLoadPercent = Ewma(current.AvgLoadPercent, load, alpha),
            AvgRpm = rpm > 100 ? Ewma(current.AvgRpm, rpm, alpha) : current.AvgRpm,
            AvgClockMHz = clock > 100 ? Ewma(current.AvgClockMHz, clock, alpha) : current.AvgClockMHz,
            UpdatedUtc = now
        };
    }

    private void UpdateModel(
        double temp,
        double power,
        double load,
        double rpm,
        double clock,
        AiWorkloadClass workload)
    {
        const double alphaWarmup = 0.10;
        const double alphaStable = 0.025;
        DateTime now = DateTime.UtcNow;
        double alpha = _model.Samples < 50 ? alphaWarmup : alphaStable;

        if (_model.Samples == 0)
        {
            _model = new AiSmartRuntimeModel(
                1,
                temp,
                power,
                load,
                rpm > 100 ? rpm : 1620,
                clock > 100 ? clock : 2800,
                now,
                _model.Workloads);
        }
        else
        {
            _model = _model with
            {
                Samples = Math.Min(500000, _model.Samples + 1),
                AvgTempC = Ewma(_model.AvgTempC, temp, alpha),
                AvgPowerW = Ewma(_model.AvgPowerW, power, alpha),
                AvgLoadPercent = Ewma(_model.AvgLoadPercent, load, alpha),
                AvgRpm = rpm > 100 ? Ewma(_model.AvgRpm, rpm, alpha) : _model.AvgRpm,
                AvgClockMHz = clock > 100 ? Ewma(_model.AvgClockMHz, clock, alpha) : _model.AvgClockMHz,
                UpdatedUtc = now
            };
        }

        _modelDirty = true;
        if (workload == AiWorkloadClass.Unknown)
            return;

        var workloads = _model.Workloads != null
            ? new Dictionary<string, AiSmartWorkloadBaseline>(
                _model.Workloads,
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, AiSmartWorkloadBaseline>(StringComparer.OrdinalIgnoreCase);

        string key = workload.ToString();
        workloads.TryGetValue(key, out AiSmartWorkloadBaseline? current);
        workloads[key] = LearnWorkloadBaseline(
            current,
            temp,
            power,
            load,
            rpm > 100 ? rpm : _model.AvgRpm,
            clock > 100 ? clock : _model.AvgClockMHz,
            now);

        _model = _model with { Workloads = workloads };
    }

    private void PersistModelIfDue(AiWorkloadClass workload)
    {
        DateTime now = DateTime.UtcNow;
        if (!AiSmartLearningPolicy.IsPersistenceDue(
                _modelDirty,
                _lastPersistUtc,
                workload,
                now))
            return;

        _lastPersistUtc = now;
        try
        {
            string modelPath = AiSmartModelPathPolicy.WritablePath;
            _model = SanitizeModel(_model);
            AtomicFileService.WriteText(
                modelPath,
                JsonSerializer.Serialize(_model));
            _modelDirty = false;
        }
        catch (Exception ex)
        {
            LogService.Error("hardware", "AI Smart model persist failed", ex);
        }
    }

    internal static AiSmartRuntimeModel SanitizeModel(
        AiSmartRuntimeModel model,
        DateTime? nowOverride = null)
    {
        DateTime now = nowOverride ?? DateTime.UtcNow;

        static double Safe(double value, double minimum, double maximum, double fallback) =>
            double.IsFinite(value) && value >= minimum && value <= maximum
                ? value
                : fallback;

        DateTime updated = model.UpdatedUtc;
        if (updated != DateTime.MinValue && updated > now.AddMinutes(5))
            updated = DateTime.MinValue;

        double temp = Safe(model.AvgTempC, 5, 120, 55);
        double power = Safe(model.AvgPowerW, 0, 300, 55);
        double load = Safe(model.AvgLoadPercent, 0, 100, 20);
        double rpm = Safe(model.AvgRpm, 0, 5000, 1620);
        double clock = Safe(model.AvgClockMHz, 100, 8000, 2800);

        Dictionary<string, AiSmartWorkloadBaseline>? workloads = null;
        if (model.Workloads != null)
        {
            workloads = new Dictionary<string, AiSmartWorkloadBaseline>(
                StringComparer.OrdinalIgnoreCase);

            foreach ((string key, AiSmartWorkloadBaseline baseline) in model.Workloads)
            {
                if (!Enum.TryParse(key, true, out AiWorkloadClass workload) ||
                    workload == AiWorkloadClass.Unknown ||
                    baseline.Samples <= 0 ||
                    baseline.UpdatedUtc == DateTime.MinValue ||
                    baseline.UpdatedUtc > now.AddMinutes(5) ||
                    !double.IsFinite(baseline.AvgTempC) ||
                    baseline.AvgTempC is < 5 or > 120 ||
                    !double.IsFinite(baseline.AvgPowerW) ||
                    baseline.AvgPowerW is < 0 or > 300 ||
                    !double.IsFinite(baseline.AvgLoadPercent) ||
                    baseline.AvgLoadPercent is < 0 or > 100 ||
                    !double.IsFinite(baseline.AvgRpm) ||
                    baseline.AvgRpm is < 0 or > 5000 ||
                    !double.IsFinite(baseline.AvgClockMHz) ||
                    baseline.AvgClockMHz is < 100 or > 8000)
                {
                    continue;
                }

                workloads[workload.ToString()] = baseline with
                {
                    Samples = Math.Clamp(baseline.Samples, 1, 500000)
                };
            }

            if (workloads.Count == 0)
                workloads = null;
        }

        return model with
        {
            Samples = Math.Clamp(model.Samples, 0, 500000),
            AvgTempC = temp,
            AvgPowerW = power,
            AvgLoadPercent = load,
            AvgRpm = rpm,
            AvgClockMHz = clock,
            UpdatedUtc = updated,
            Workloads = workloads
        };
    }

    private static AiSmartRuntimeModel LoadModel()
    {
        try
        {
            string loadPath = AiSmartModelPathPolicy.ResolveLoadPath();
            if (!File.Exists(loadPath))
                return EmptyModel();

            var model = JsonSerializer.Deserialize<AiSmartRuntimeModel>(
                File.ReadAllText(loadPath));
            return model == null ? EmptyModel() : SanitizeModel(model);
        }
        catch
        {
            return EmptyModel();
        }
    }

    private static AiSmartRuntimeModel EmptyModel() =>
        new(0, 55, 55, 20, 1620, 2800, DateTime.MinValue);

    private string BaselineText(AiWorkloadClass workload)
    {
        AiSmartWorkloadBaseline baseline = ResolveBaseline(workload);
        bool specialized =
            workload != AiWorkloadClass.Unknown &&
            _model.Workloads != null &&
            _model.Workloads.TryGetValue(workload.ToString(), out AiSmartWorkloadBaseline? learned) &&
            IsWorkloadBaselineEligible(learned.Samples, learned.UpdatedUtc, DateTime.UtcNow);

        string source = specialized ? workload.ToString() : "global";
        return $"{source} n={baseline.Samples} • {baseline.AvgTempC:0.0}°C • {baseline.AvgPowerW:0}W • {baseline.AvgLoadPercent:0}% • {baseline.AvgRpm:0}RPM";
    }

    private static double AverageActiveRpm(int[]? rpms)
    {
        if (rpms == null) return 0;
        int[] active = rpms.Where(x => x > 100).ToArray();
        return active.Length == 0 ? 0 : active.Average();
    }

    private static double Ewma(double current, double next, double alpha) =>
        current + (next - current) * alpha;

    private static bool TryParseBlockerPercent(string blocker, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(blocker))
            return false;

        int pct = blocker.LastIndexOf('%');
        if (pct <= 0) return false;
        int start = pct - 1;
        while (start >= 0 && (char.IsDigit(blocker[start]) || blocker[start] == '.' || blocker[start] == ','))
            start--;
        string text = blocker[(start + 1)..pct].Replace(',', '.');
        return double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out percent);
    }
}
