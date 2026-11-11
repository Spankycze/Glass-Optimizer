using System;

namespace SpankyhoOptimizer.Services;

internal enum AiCpuLoadSource : byte { Unknown, Hardware, NativeProcesses }

/// <summary>
/// Hardware telemetry can temporarily report NaN/Infinity or a bad range.
/// Never poison the EMA or EPP decision with one broken sensor sample.
/// The already-measured native process CPU total is a coverage-gated fallback.
/// </summary>
internal static class AiCpuLoadSelectionPolicy
{
    internal static double Select(double? hardwarePercent, double processPercent)
    {
        if (hardwarePercent is double hardware && IsValid(hardware))
            return hardware;
        return IsValid(processPercent) ? processPercent : 0d;
    }

    // Unknown native process evidence is not measured 0%. When hardware
    // telemetry also fails, callers must defer any new power/scheduler choice.
    internal static bool TrySelect(double? hardwarePercent, double processPercent,
        bool representativeNativeCoverage, out double selectedPercent)
        => TrySelect(hardwarePercent, processPercent,
            representativeNativeCoverage, out selectedPercent, out _);

    // Hardware and process-summed CPU are not directly comparable across a
    // provider transition. Report source changes without per-tick allocations.
    internal static bool TrySelect(double? hardwarePercent, double processPercent,
        bool representativeNativeCoverage, out double selectedPercent,
        out AiCpuLoadSource source)
    {
        if (hardwarePercent is double hardware && IsValid(hardware))
        {
            selectedPercent = hardware;
            source = AiCpuLoadSource.Hardware;
            return true;
        }
        if (representativeNativeCoverage && IsValid(processPercent))
        {
            selectedPercent = processPercent;
            source = AiCpuLoadSource.NativeProcesses;
            return true;
        }
        selectedPercent = 0;
        source = AiCpuLoadSource.Unknown;
        return false;
    }

    internal static double Smooth(double previousLoad, double rawLoad,
        bool previouslyInitialized, AiCpuLoadSource previousSource,
        AiCpuLoadSource currentSource) =>
        previouslyInitialized && previousSource == currentSource &&
        currentSource != AiCpuLoadSource.Unknown && IsValid(previousLoad) &&
        IsValid(rawLoad)
            ? previousLoad * 0.68d + rawLoad * 0.32d
            : rawLoad;

    internal static bool IsValid(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 100;
}
