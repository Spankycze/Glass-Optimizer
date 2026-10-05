using System;
using System.IO;

namespace SpankyhoOptimizer.Services;

public static class VoiceLegacyRuntimeCleanup
{
    public static bool TryDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return true;

        try
        {
            Directory.Delete(path, recursive: true);
            return !Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
