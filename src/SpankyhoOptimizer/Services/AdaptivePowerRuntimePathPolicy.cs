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
        if (File.Exists(WritablePath))
            return WritablePath;

        return File.Exists(LegacyPath)
            ? LegacyPath
            : WritablePath;
    }
}
