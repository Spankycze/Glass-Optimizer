using System;
using System.IO;

namespace SpankyhoOptimizer.Services;

public static class RuntimeHostPathPolicy
{
    public static string Resolve(
        string stagedPath,
        bool stagedExists,
        string candidatePath,
        bool candidateExists,
        string currentPath)
    {
        if (stagedExists)
            return stagedPath;
        if (candidateExists)
            return candidatePath;

        return !string.IsNullOrWhiteSpace(currentPath) &&
               Path.GetFileNameWithoutExtension(currentPath)
                   .Equals("SpankyhoOptimizer.RuntimeHost", StringComparison.OrdinalIgnoreCase)
            ? currentPath
            : string.Empty;
    }
}
