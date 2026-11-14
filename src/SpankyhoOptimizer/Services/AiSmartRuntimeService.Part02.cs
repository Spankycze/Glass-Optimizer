using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed partial class AiSmartRuntimeService
{
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
}
