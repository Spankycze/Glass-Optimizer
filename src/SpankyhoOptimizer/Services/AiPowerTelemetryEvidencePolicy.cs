using System;

namespace SpankyhoOptimizer.Services;

// Production normalization for the existing AI Power Engine. Invalid optional
// hardware observations are treated as unavailable, NEVER as load demand.
internal static class AiPowerTelemetryEvidencePolicy
{
    internal static bool HasUsablePower(double? watts) =>
        watts is double value && double.IsFinite(value) && value >= 0 && value <= 1000;

    internal static bool HasUsableClock(double? mhz) =>
        mhz is double value && double.IsFinite(value) && value > 0 && value <= 10000;

    internal static bool HasUsableConfidence(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 100;

    internal static bool HasUsableAnomaly(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 100;

    internal static bool HasUsablePredictedTemperature(double value) =>
        double.IsFinite(value) && value >= -10 && value <= 125;

    internal static bool HasUsableCpuTemperature(double? value) =>
        value is double c && double.IsFinite(c) && c >= -10 && c <= 125;
}
