using System;
using System.IO;

namespace SpankyhoOptimizer.Services;

public static class AdaptivePowerRuntimePathPolicy
{
    public static string WritablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer",
        "FanAgent",
        "adaptive-power-runtime.json");

    public static string LegacyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer",
        "adaptive-power-runtime.json");

    public static string ResolveLoadPath()
    {
        return ChooseLoadPath(WritablePath, LegacyPath,
            File.Exists(WritablePath), File.Exists(LegacyPath));
    }

    // Explicitly prefer the current per-user source. The legacy ProgramData
    // file can be days old even while FanAgent refreshes the new path.
    internal static string ChooseLoadPath(
        string writable, string legacy, bool writableExists, bool legacyExists) =>
        writableExists || !legacyExists ? writable : legacy;

    internal static string RunFunctionalSmoke()
    {
        const string current = "current-telemetry";
        const string legacy = "legacy-telemetry";
        if (ChooseLoadPath(current, legacy, true, true) != current ||
            ChooseLoadPath(current, legacy, true, false) != current ||
            ChooseLoadPath(current, legacy, false, true) != legacy ||
            ChooseLoadPath(current, legacy, false, false) != current)
            throw new InvalidOperationException("Adaptive runtime telemetry source precedence regressed.");
        return "ADAPTIVE_POWER_SOURCE_SMOKE_OK currentPreferred=true legacyFallback=true";
    }
}
