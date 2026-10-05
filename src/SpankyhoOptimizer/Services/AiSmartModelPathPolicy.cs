using System;
using System.IO;

namespace SpankyhoOptimizer.Services;

public static class AiSmartModelPathPolicy
{
    public static string WritablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer",
        "FanAgent",
        "ai-smart-model.json");

    public static string LegacyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SpankyhoOptimizer",
        "ai-smart-model.json");

    public static string ResolveLoadPath()
    {
        if (File.Exists(WritablePath))
            return WritablePath;

        return File.Exists(LegacyPath)
            ? LegacyPath
            : WritablePath;
    }
}
