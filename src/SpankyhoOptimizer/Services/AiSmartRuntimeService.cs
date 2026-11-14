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

public sealed partial class AiSmartRuntimeService
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
}
