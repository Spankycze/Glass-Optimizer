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
    private DateTime _lastPersistFailureUtc = DateTime.MinValue;
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
    private DateTime _lastAnomalyEvidenceUtc = DateTime.MinValue;
    private AiWorkloadClass _lastConfidenceWorkload = AiWorkloadClass.Unknown;
    private int _workloadStableSamples;
    private DateTime _lastConfidenceSampleUtc = DateTime.MinValue;
    // Baseline labels are shown every tick, while learned baselines change infrequently.
    private AiSmartWorkloadBaseline? _lastBaselineLabelValue;
    private string? _lastBaselineLabelSource;
    private string? _lastBaselineLabelText;
    private AiSmartRuntimeModel? _globalBaselineSourceModel;
    private AiSmartWorkloadBaseline? _globalBaselineCache;

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
        DateTime nowUtc = DateTime.UtcNow;
        if (!enabled)
        {
            // A disabled interval is a gap in workload observation, not
            // evidence that the previously classified workload stayed stable.
            _lastConfidenceWorkload = AiWorkloadClass.Unknown;
            _workloadStableSamples = 0;
            _lastConfidenceSampleUtc = DateTime.MinValue;
            // A disabled period also breaks thermal-trend continuity. Keep
            // anomaly protection history intact; only discard stale velocity.
            _previousTemp = null;
            _previousTempUtc = DateTime.MinValue;
            _smoothedTrend = 0;
            // Preserve newly learned baselines when AI is switched off.
            // A clean model does no I/O; failed writes still obey retry backoff.
            PersistModelIfDue(workload, nowUtc, forceDue: true);
            return new AiSmartRuntimeDecision(
                false, "OFF", gameActive ? "Detected" : "None", 0, 0,
                telemetry.Celsius is double offTemp && double.IsFinite(offTemp) ? offTemp : 0,
                "AI Smart Runtime je vypnutý.", BaselineText(workload, nowUtc), _lastAction, 0, false, false);
        }

        AiSmartWorkloadBaseline baseline = ResolveBaseline(workload, nowUtc);
        double? liveTemp = telemetry.Celsius;
        bool temperatureAvailable =
            liveTemp.HasValue &&
            liveTemp.Value > 5 &&
            liveTemp.Value < 150;
        // The optional CPU sensor data can contain NaN, infinity or an
        // implausible value even when HasValue is true. Reuse the same
        // conservative evidence thresholds as the AI power engine.
        bool powerAvailable = AiPowerTelemetryEvidencePolicy.HasUsablePower(telemetry.PackagePowerW);
        bool clockAvailable = AiPowerTelemetryEvidencePolicy.HasUsableClock(telemetry.EffectiveClockMHz);
        bool loadAvailable = double.IsFinite(loadPercent) && loadPercent is >= 0 and <= 100;

        double temp = temperatureAvailable ? liveTemp!.Value : baseline.AvgTempC;
        double power = powerAvailable ? telemetry.PackagePowerW!.Value : baseline.AvgPowerW;
        double clock = clockAvailable ? telemetry.EffectiveClockMHz!.Value : baseline.AvgClockMHz;
        double load = loadAvailable ? loadPercent : baseline.AvgLoadPercent;
        double avgRpm = AverageActiveRpm(fanRpms);
        double trend = 0;

        if (temperatureAvailable)
        {
            // Only adjacent, continuously observed samples can establish a
            // thermal velocity. A sensor outage, clock rollback or a long
            // scheduling gap invalidates the previous derivative.
            TimeSpan elapsed = nowUtc - _previousTempUtc;
            bool contiguous = _previousTemp.HasValue &&
                _previousTempUtc != DateTime.MinValue &&
                elapsed >= TimeSpan.Zero &&
                elapsed <= TimeSpan.FromSeconds(30);
            if (!contiguous)
                _smoothedTrend = 0;

            // Two very fast ticks are not independent evidence of thermal
            // velocity. Do not divide their sensor jitter by an invented
            // 0.1-second interval, nor replace the previous valid sample.
            // The absolute current temperature is still used for safety.
            bool tooSoon = contiguous && elapsed < TimeSpan.FromMilliseconds(500);
            if (!tooSoon)
            {
                trend = contiguous
                    ? NormalizeTemperatureTrend(
                        temp - _previousTemp!.Value, elapsed.TotalSeconds)
                    : 0;
                _previousTemp = temp;
                _previousTempUtc = nowUtc;
                _smoothedTrend = _smoothedTrend == 0
                    ? trend
                    : _smoothedTrend * 0.72 + trend * 0.28;
            }
        }
        else
        {
            // Never bridge a missing sensor interval with an old temperature.
            // Reset velocity only; retain protective anomaly history.
            _previousTemp = null;
            _previousTempUtc = DateTime.MinValue;
            _smoothedTrend = 0;
        }

        // A learned baseline is not evidence of current game load. Missing
        // telemetry must not invent a Heavy/Gameplay phase or trigger its
        // associated fan preboost; the game-active power guard stays intact.
        string gamePhase = DetectGamePhase(gameActive,
            loadAvailable ? load : 0,
            powerAvailable ? power : 0,
            clockAvailable ? clock : 0);
        double rawAnomaly = CalculateAnomaly(
            temp, power, load, avgRpm, currentTargetRpm, hidOk, blocker, baseline,
            temperatureAvailable, powerAvailable, loadAvailable);
        // Two calls in the same short sampling window are not two
        // independent anomaly observations. Keep immediate raw temperature
        // safety in AiFanBiasPolicy, but only advance the smoothed anomaly
        // and its confirmation votes on genuinely separated evidence.
        TimeSpan anomalyGap = nowUtc - _lastAnomalyEvidenceUtc;
        // Missing CPU temperature is not an independent thermal observation.
        // Do not promote baseline estimates into confirmed anomaly votes;
        // the existing direct sensor and performance fallbacks stay active.
        bool fanReadbackHealthy = avgRpm > 100 &&
            (currentTargetRpm < 1000 || avgRpm >= currentTargetRpm - 220);
        bool independentAnomalySample = hidOk && fanReadbackHealthy &&
            temperatureAvailable && powerAvailable && loadAvailable &&
            (_lastAnomalyEvidenceUtc == DateTime.MinValue ||
             anomalyGap < TimeSpan.Zero ||
             anomalyGap >= TimeSpan.FromMilliseconds(500));
        // A telemetry outage breaks the chain of independent anomaly votes.
        // Discard the old confirmation rather than carrying it across a gap.
        if (!hidOk || !fanReadbackHealthy || !temperatureAvailable ||
            !powerAvailable || !loadAvailable)
        {
            _smoothedAnomaly = 0;
            _highAnomalyVotes = 0;
            _lastAnomalyEvidenceUtc = DateTime.MinValue;
        }
        if (independentAnomalySample)
        {
            if (_lastAnomalyEvidenceUtc != DateTime.MinValue &&
                (anomalyGap < TimeSpan.Zero || anomalyGap > TimeSpan.FromSeconds(30)))
            {
                _smoothedAnomaly = 0;
                _highAnomalyVotes = 0;
            }
            _smoothedAnomaly = _smoothedAnomaly == 0
                ? rawAnomaly
                : _smoothedAnomaly * 0.70 + rawAnomaly * 0.30;

            if (_smoothedAnomaly >= 65)
                _highAnomalyVotes = Math.Min(2, _highAnomalyVotes + 1);
            else
                _highAnomalyVotes = Math.Max(0, _highAnomalyVotes - 1);
            _lastAnomalyEvidenceUtc = nowUtc;
        }

        double anomaly = _highAnomalyVotes >= 2
            ? Math.Max(_smoothedAnomaly, rawAnomaly)
            : Math.Min(_smoothedAnomaly, 64);
        // Current HID failure or measured fan RPM deficit is direct safety
        // evidence, not a thermal trend awaiting two independent samples.
        if (!hidOk || (currentTargetRpm >= 1000 && avgRpm > 100 &&
                       avgRpm < currentTargetRpm - 220))
            anomaly = Math.Max(anomaly, rawAnomaly);

        double confidence = CalculateConfidence(
            hidOk,
            temperatureAvailable,
            powerAvailable,
            baseline,
            nowUtc);
        // A valid workload label alone cannot justify full confidence
        // without independent CPU clock and load evidence. This also
        // applies to Game, whose stability factor intentionally stays 1.
        if (!clockAvailable) confidence *= 0.70;
        if (!loadAvailable) confidence *= 0.70;
        // A current fan-speed deficit is not reliable cooling telemetry.
        if (currentTargetRpm >= 1000 && avgRpm > 100 &&
            avgRpm < currentTargetRpm - 220) confidence *= 0.70;
        // A responding controller does not prove real fan feedback exists.
        // Missing or stopped RPM readback must reduce AI confidence.
        if (avgRpm <= 100) confidence *= 0.70;

        if (workload == AiWorkloadClass.Unknown || !hidOk || avgRpm <= 100 ||
            (currentTargetRpm >= 1000 && avgRpm < currentTargetRpm - 220) ||
            !temperatureAvailable || !powerAvailable || !clockAvailable || !loadAvailable)
        {
            // A workload label observed without valid CPU evidence does not
            // prove the workload stayed stable. Requalify confidence when
            // telemetry returns, even if the label is unchanged.
            _lastConfidenceWorkload = AiWorkloadClass.Unknown;
            _workloadStableSamples = 0;
            _lastConfidenceSampleUtc = DateTime.MinValue;
        }
        else if (workload == _lastConfidenceWorkload)
        {
            TimeSpan elapsed = nowUtc - _lastConfidenceSampleUtc;
            if (_lastConfidenceSampleUtc == DateTime.MinValue ||
                elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(30))
            {
                // A clock rollback or an unobserved interval cannot confirm
                // that a workload stayed stable during the gap.
                _workloadStableSamples = 0;
                _lastConfidenceSampleUtc = nowUtc;
            }
            else if (elapsed >= TimeSpan.FromMilliseconds(500))
            {
                // Repeated immediate UI/runtime ticks are not independent
                // samples and must not inflate the classifier confidence.
                _workloadStableSamples = Math.Min(5, _workloadStableSamples + 1);
                _lastConfidenceSampleUtc = nowUtc;
            }
        }
        else
        {
            _lastConfidenceWorkload = workload;
            _workloadStableSamples = 0;
            _lastConfidenceSampleUtc = nowUtc;
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
        // An unknown workload cannot provide a classified learning example.
        // Wait for a recognized workload before updating its baseline.
        if (learningSampleDue &&
            workload != AiWorkloadClass.Unknown &&
            hidOk &&
            temperatureAvailable &&
            powerAvailable &&
            clockAvailable &&
            loadAvailable &&
            avgRpm > 100 &&
            (currentTargetRpm < 1000 || avgRpm >= currentTargetRpm - 220) &&
            // Reject fresh anomalies and unconfirmed high thermal observations;
            // only sustained, healthy measurements can update the baseline.
            rawAnomaly < 60 && anomaly < 60 &&
            _highAnomalyVotes == 0 &&
            // Exclude both a rising trend and a predicted hot near-term state.
            _smoothedTrend <= 0.5 &&
            predictedTemp10s < 75 &&
            // A hot current CPU reading or strong thermal prediction error
            // is not a trustworthy steady-state learning baseline.
            temp < 75 && Math.Abs(predictedTemp10s - temp) < 10 &&
            // Require both decision confidence and independently confirmed
            // workload stability before recording a classified baseline.
            confidence >= 0.6 && _workloadStableSamples >= 2)
        {
            UpdateModel(temp, power, load, avgRpm, clock, workload, nowUtc);
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
        // A cooling (negative RPM) advisory requires current trustworthy
        // sensor and fan-controller evidence. Baseline estimates are not
        // proof that it is safe to reduce real fan speed.
        // An observed RPM shortfall is not cooling headroom: do not lower
        // the requested fan target while tachometry is below that target.
        if (!temperatureAvailable || !hidOk || !clockAvailable ||
            !powerAvailable || !loadAvailable || avgRpm <= 100 ||
            (currentTargetRpm >= 1000 && avgRpm < currentTargetRpm - 220))
            fanBias = Math.Max(0, fanBias);
        // A missing CPU temperature must never be presented as evidence
        // supporting a low-power preference. This is a conservative advisory
        // decision; the adaptive power engine retains its own safety gates.
        bool preferPerformance = !temperatureAvailable || !hidOk ||
            !clockAvailable || !powerAvailable || !loadAvailable ||
            avgRpm <= 100 ||
            (currentTargetRpm >= 1000 && avgRpm < currentTargetRpm - 220) ||
            AiPowerDemandPolicy.HasPerformanceDemand(
                gameActive,
                // Keep invalid raw load as an explicit fail-safe signal to
                // the power policy. Fallback values serve calculations only.
                loadAvailable ? load : loadPercent,
                powerAvailable ? power : null,
                clockAvailable ? telemetry.EffectiveClockMHz : null,
                anomaly,
                workload,
                confidence);

        // Idle is advisory only when fan control is healthy and there is
        // actual thermal headroom. Low CPU load alone must not justify Idle
        // while the CPU is hot, a temperature rise is predicted, or a
        // competing performance demand has already been established.
        bool preferIdle =
            hidOk &&
            !preferPerformance &&
            temperatureAvailable &&
            powerAvailable &&
            clockAvailable &&
            loadAvailable &&
            avgRpm > 100 &&
            temp < 68 &&
            predictedTemp10s < 70 &&
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
                    : anomaly >= 55
                        ? "Watch"
                        : workload == AiWorkloadClass.Background
                            ? "BackgroundEfficient"
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
            gamePhase,
            nowUtc);
        PersistModelIfDue(workload, nowUtc);

        return new AiSmartRuntimeDecision(
            true,
            mode,
            gamePhase,
            Math.Round(anomaly, 1),
            Math.Round(confidence, 1),
            Math.Round(predictedTemp10s, 1),
            prediction,
            BaselineText(workload, nowUtc),
            _lastAction,
            fanBias,
            preferPerformance,
            preferIdle);
    }

    public int AdjustAuto65Target(int baseTargetRpm, AiSmartRuntimeDecision decision)
    {
        if (!decision.Enabled)
            return Math.Clamp(baseTargetRpm, 1620, LianLiSlInfinityService.MaximumRpm);

        // Stay in 64-bit arithmetic until the physical RPM bounds are
        // applied. An out-of-range upstream base target must never overflow
        // to a low (potentially minimum) fan setting.
        long target = (long)baseTargetRpm + decision.FanRpmBias;
        if (decision.AnomalyScore >= 85)
            target = Math.Max(target, 1950L);

        return (int)Math.Clamp(target, 1620L, LianLiSlInfinityService.MaximumRpm);
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
        AiSmartWorkloadBaseline baseline,
        bool temperatureAvailable,
        bool powerAvailable,
        bool loadAvailable)
    {
        double score = 0;

        if (!hidOk)
            score += 45;

        if (targetRpm >= 1000 && avgRpm > 100 && avgRpm < targetRpm - 220)
            score += Math.Min(35, (targetRpm - avgRpm - 220) / 8d);

        if (baseline.Samples >= 20 && temperatureAvailable &&
            powerAvailable && loadAvailable)
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

        if (temperatureAvailable && temp >= 78) score += 25;
        else if (temperatureAvailable && temp >= 70) score += 12;

        return Math.Clamp(score, 0, 100);
    }

    private double CalculateConfidence(
        bool hidOk,
        bool temperatureAvailable,
        bool powerAvailable,
        AiSmartWorkloadBaseline baseline,
        DateTime nowUtc)
    {
        double sampleConfidence = Math.Clamp(baseline.Samples / 250d * 100d, 0, 100);
        // Reuse the exact tick timestamp for both freshness and classification.
        bool modelFresh =
            baseline.UpdatedUtc != DateTime.MinValue &&
            nowUtc >= baseline.UpdatedUtc &&
            nowUtc - baseline.UpdatedUtc <= TimeSpan.FromMinutes(10);

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
        string gamePhase,
        DateTime now)
    {

        // Safety events are not normal advisory actions. When their kind
        // changes, record them immediately instead of hiding a new HID,
        // temperature or fan fault behind the ordinary 20-second cooldown.
        // Repeated identical events remain rate-limited by the same cooldown.
        string? safetyAction = null;
        if (!hidOk)
            safetyAction = "HID reconnect/watch";
        else if (!temperatureAvailable)
            safetyAction = "CPU telemetry fallback";
        else if (targetRpm >= 1000 && avgRpm <= 100)
            safetyAction = "fan feedback unavailable";
        else if (targetRpm >= 1000 && avgRpm > 100 && avgRpm < targetRpm - 220)
            safetyAction = "fan target self-heal";
        else if (anomaly >= 75)
            safetyAction = "thermal anomaly protection";

        if (safetyAction != null &&
            !string.Equals(_lastAction, safetyAction, StringComparison.Ordinal))
        {
            SetAction(safetyAction, now, anomaly, fanBias);
            return;
        }

        // A recovery is observable only after all higher-priority fault
        // checks have cleared. Never report restored CPU telemetry while
        // HID or measured fan feedback is still in failure.
        if (safetyAction == null)
        {
            if (string.Equals(_lastAction, "HID reconnect/watch", StringComparison.Ordinal))
            {
                SetAction("HID telemetry restored", now, anomaly, fanBias);
                return;
            }
            if (string.Equals(_lastAction, "CPU telemetry fallback", StringComparison.Ordinal))
            {
                SetAction("CPU telemetry restored", now, anomaly, fanBias);
                return;
            }
            if (avgRpm > 100 &&
                (string.Equals(_lastAction, "fan feedback unavailable", StringComparison.Ordinal) ||
                 string.Equals(_lastAction, "fan target self-heal", StringComparison.Ordinal)))
            {
                SetAction("fan feedback restored", now, anomaly, fanBias);
                return;
            }
        }

        // A backwards wall-clock adjustment invalidates the old cooldown.
        if (now >= _lastActionUtc && now - _lastActionUtc < TimeSpan.FromSeconds(20))
            return;

        string? action = safetyAction;
        if (action == null && fanBias >= 150)
            action = "predictive fan preboost";
        else if (action == null && gamePhase == "Loading/Heavy")
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
        if (changed || now < _lastActionLogUtc ||
            now - _lastActionLogUtc >= TimeSpan.FromMinutes(2))
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

    // Canonical immutable keys avoid enum formatting and repeated allocations
    // in the learning path while preserving the exact persisted JSON keys.
    internal static string WorkloadKey(AiWorkloadClass workload) => workload switch
    {
        AiWorkloadClass.Idle => "Idle",
        AiWorkloadClass.Background => "Background",
        AiWorkloadClass.Interactive => "Interactive",
        AiWorkloadClass.Game => "Game",
        _ => "Unknown"
    };

    private AiSmartWorkloadBaseline ResolveBaseline(AiWorkloadClass workload, DateTime nowUtc) =>
        ResolveBaseline(workload, nowUtc, out _);

    private AiSmartWorkloadBaseline ResolveBaseline(AiWorkloadClass workload, DateTime nowUtc, out bool specialized)
    {
        specialized = false;
        if (workload != AiWorkloadClass.Unknown &&
            _model.Workloads != null &&
            _model.Workloads.TryGetValue(WorkloadKey(workload), out AiSmartWorkloadBaseline? learned) &&
            IsWorkloadBaselineEligible(learned.Samples, learned.UpdatedUtc, nowUtc))
        {
            specialized = true;
            return learned;
        }

        if (ReferenceEquals(_globalBaselineSourceModel, _model) && _globalBaselineCache != null)
            return _globalBaselineCache;

        _globalBaselineSourceModel = _model;
        _globalBaselineCache = new AiSmartWorkloadBaseline(
            _model.Samples,
            _model.AvgTempC,
            _model.AvgPowerW,
            _model.AvgLoadPercent,
            _model.AvgRpm,
            _model.AvgClockMHz,
            _model.UpdatedUtc);
        return _globalBaselineCache;
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
        AiWorkloadClass workload,
        DateTime now)
    {
        const double alphaWarmup = 0.10;
        const double alphaStable = 0.025;
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

        // FanAgent owns this model on its serial tick. The saved JSON is a
        // separate serialized copy, so recreating the whole workload dictionary
        // on every 5–10 second learning sample only adds GC pressure.
        // Preserve its case-insensitive key semantics and retain all baselines.
        var workloads = _model.Workloads;
        if (workloads == null)
        {
            workloads = new Dictionary<string, AiSmartWorkloadBaseline>(
                StringComparer.OrdinalIgnoreCase);
            _model = _model with { Workloads = workloads };
        }

        string key = WorkloadKey(workload);
        workloads.TryGetValue(key, out AiSmartWorkloadBaseline? current);
        workloads[key] = LearnWorkloadBaseline(
            current,
            temp,
            power,
            load,
            rpm > 100 ? rpm : _model.AvgRpm,
            clock > 100 ? clock : _model.AvgClockMHz,
            now);
    }

    // Read-only training microbenchmark: same EWMA, same dictionary/JSON after
    // all samples. Avoid rebuilding the complete map for one changed key.
    internal static string RunWorkloadDictionaryReuseSmoke()
    {
        const int rounds = 8192;
        static Dictionary<string, AiSmartWorkloadBaseline> Seed()
        {
            var now = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
            var result = new Dictionary<string, AiSmartWorkloadBaseline>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "Game", "Interactive", "Background", "Idle" })
                result[key] = new AiSmartWorkloadBaseline(
                    30, 60, 57, 25, 1700, 3600, now);
            return result;
        }

        var beforeMap = Seed();
        var afterMap = Seed();
        var identity = afterMap;
        var startUtc = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        // Warm the same numeric/JIT code without changing either measured map.
        var warm = Seed();
        for (int i = 0; i < 100; i++)
            warm["Game"] = LearnWorkloadBaseline(
                warm["Game"], 65, 72, 48, 1850, 3900, startUtc.AddSeconds(i));

        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
        {
            var next = new Dictionary<string, AiSmartWorkloadBaseline>(
                beforeMap, StringComparer.OrdinalIgnoreCase);
            next["Game"] = LearnWorkloadBaseline(
                next["Game"], 65, 72, 48, 1850, 3900, startUtc.AddSeconds(i));
            beforeMap = next;
        }
        double oldMs = System.Diagnostics.Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;

        startBytes = GC.GetAllocatedBytesForCurrentThread();
        startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
        {
            afterMap["Game"] = LearnWorkloadBaseline(
                afterMap["Game"], 65, 72, 48, 1850, 3900, startUtc.AddSeconds(i));
        }
        double newMs = System.Diagnostics.Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
        long newBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;

        if (!ReferenceEquals(identity, afterMap) ||
            beforeMap.Count != afterMap.Count ||
            !beforeMap["Game"].Equals(afterMap["Game"]) ||
            JsonSerializer.Serialize(beforeMap) != JsonSerializer.Serialize(afterMap) ||
            oldBytes <= newBytes ||
            afterMap.Comparer != StringComparer.OrdinalIgnoreCase)
            throw new InvalidOperationException("AI workload memory or persisted baselines changed.");

        return $"AI_SMART_WORKLOAD_MAP_REUSE_OK samples={rounds} " +
            $"oldAllocB={oldBytes} newAllocB={newBytes} " +
            $"oldMs={oldMs:F3} newMs={newMs:F3} " +
            "learningIdentical=true jsonIdentical=true caseInsensitive=true writes=0";
    }

    private void PersistModelIfDue(AiWorkloadClass workload, DateTime now, bool forceDue = false)
    {
        if (!_modelDirty) return;
        if (_lastPersistFailureUtc != DateTime.MinValue)
        {
            TimeSpan elapsed = now - _lastPersistFailureUtc;
            if (elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(30))
                return;
        }
        else if (!forceDue && !AiSmartLearningPolicy.IsPersistenceDue(
                     _modelDirty,
                     _lastPersistUtc,
                     workload,
                     now))
            return;

        try
        {
            string modelPath = AiSmartModelPathPolicy.WritablePath;
            _model = SanitizeModel(_model, now);
            AtomicFileService.WriteText(
                modelPath,
                JsonSerializer.Serialize(_model));
            _lastPersistUtc = now;
            _lastPersistFailureUtc = DateTime.MinValue;
            _modelDirty = false;
        }
        catch (Exception ex)
        {
            // A failed write did not persist any learning. Keep the model dirty
            // and retry after a bounded 30-second delay on the normal FanAgent
            // tick; the usual 1-5 minute cadence applies only after success.
            // Do not spin, create a timer or repeatedly write on every tick.
            _lastPersistFailureUtc = now;
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
                    // Enum.TryParse also accepts numeric or unknown enum
                    // values: only canonical named workload keys belong in
                    // the persisted learning model.
                    !string.Equals(key, WorkloadKey(workload), StringComparison.OrdinalIgnoreCase) ||
                    // Invalid JSON can contain a null workload value even
                    // though the record type declares baselines non-null.
                    // Discard that one entry, not every other valid profile.
                    baseline is null ||
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

            // The model contains one global baseline and at most four named
            // workloads. Reject an implausibly large/corrupt JSON file before
            // deserializing; do not read, truncate or modify its contents.
            using var modelStream = new FileStream(loadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (modelStream.Length > 1024 * 1024)
                return EmptyModel();
            var model = JsonSerializer.Deserialize<AiSmartRuntimeModel>(modelStream);
            return model == null ? EmptyModel() : SanitizeModel(model);
        }
        catch
        {
            return EmptyModel();
        }
    }

    private static AiSmartRuntimeModel EmptyModel() =>
        new(0, 55, 55, 20, 1620, 2800, DateTime.MinValue);

    private string BaselineText(AiWorkloadClass workload, DateTime nowUtc)
    {
        AiSmartWorkloadBaseline baseline = ResolveBaseline(workload, nowUtc, out bool specialized);

        string source = specialized ? WorkloadKey(workload) : "global";
        if (_lastBaselineLabelText != null &&
            source == _lastBaselineLabelSource &&
            baseline == _lastBaselineLabelValue)
            return _lastBaselineLabelText;

        string label = $"{source} n={baseline.Samples} • {baseline.AvgTempC:0.0}°C • {baseline.AvgPowerW:0}W • {baseline.AvgLoadPercent:0}% • {baseline.AvgRpm:0}RPM";
        _lastBaselineLabelValue = baseline;
        _lastBaselineLabelSource = source;
        _lastBaselineLabelText = label;
        return label;
    }

    // Called each FanAgent RPM cycle: calculating the mean of at most four
    // channels should not materialize a Where iterator and a new int[].
    // Use a wide integral accumulator, matching LINQ Average(int)'s arithmetic.
    private static double AverageActiveRpm(int[]? rpms)
    {
        if (rpms == null) return 0;
        long sum = 0;
        int count = 0;
        foreach (int rpm in rpms)
        {
            if (rpm <= 100) continue;
            sum += rpm;
            count++;
        }
        return count == 0 ? 0 : (double)sum / count;
    }

    internal static string RunAverageRpmNoAllocationSmoke()
    {
        static double Legacy(int[]? rpms)
        {
            if (rpms == null) return 0;
            int[] active = rpms.Where(x => x > 100).ToArray();
            return active.Length == 0 ? 0 : active.Average();
        }
        if (AverageActiveRpm(null) != Legacy(null))
            throw new InvalidOperationException("Null RPM parity.");
        for (int count = 0; count <= 8; count++)
        {
            for (int variation = 0; variation < 64; variation++)
            {
                var values = new int[count];
                for (int i = 0; i < count; i++)
                    values[i] = (variation + i) % 7 == 0 ? 0 :
                                (variation + i) % 7 == 1 ? 100 :
                                520 + (i * 371 + variation * 41) % 5000;
                if (AverageActiveRpm(values) != Legacy(values))
                    throw new InvalidOperationException("Average RPM parity regression.");
            }
        }
        int[] live = { 1620, 0, 1790, 2140 };
        const int rounds = 16384;
        double oldCheck = 0, newCheck = 0;
        long bytesStart = GC.GetAllocatedBytesForCurrentThread();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++) oldCheck += Legacy(live);
        double oldMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - bytesStart;
        bytesStart = GC.GetAllocatedBytesForCurrentThread();
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++) newCheck += AverageActiveRpm(live);
        double newMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long newBytes = GC.GetAllocatedBytesForCurrentThread() - bytesStart;
        if (oldCheck != newCheck || newBytes >= oldBytes)
            throw new InvalidOperationException("Average RPM allocation regression.");
        return $"AI_SMART_AVG_RPM_NO_ALLOC_OK samples={rounds} " +
            $"oldAllocB={oldBytes} newAllocB={newBytes} " +
            $"oldMs={oldMs:F3} newMs={newMs:F3} " +
            "parityCases=577 identical=true writes=0";
    }

    private static double Ewma(double current, double next, double alpha) =>
        current + (next - current) * alpha;

    internal static bool TryParseBlockerPercent(string blocker, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(blocker))
            return false;

        int pct = blocker.LastIndexOf('%');
        if (pct <= 0) return false;
        int start = pct - 1;
        while (start >= 0 && (char.IsDigit(blocker[start]) || blocker[start] == '.' || blocker[start] == ','))
            start--;
        ReadOnlySpan<char> digits = blocker.AsSpan(start + 1, pct - start - 1);
        if (digits.IndexOf(',') < 0)
            return double.TryParse(digits,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out percent);

        // Preserve decimal-comma normalization, but avoid Substring and
        // Replace allocations on the recurring fan-pressure assessment.
        Span<char> normalized = digits.Length <= 64
            ? stackalloc char[digits.Length] : new char[digits.Length];
        digits.CopyTo(normalized);
        normalized.Replace(',', '.');
        return double.TryParse(normalized,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out percent);
    }
}
