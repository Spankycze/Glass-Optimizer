using System;
using System.IO;

namespace SpankyhoOptimizer;

public sealed partial class App
{
    // A readiness-only launch is a release probe, not a general file writer.
    // The caller must allocate an empty, GUID-named directory directly inside
    // TEMP. No existing file may be replaced and no reparse-point directory
    // may redirect the output into a user's profile or installed application.
    internal static bool TryValidateIsolatedReadinessMarkerPath(
        string? candidate, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        try
        {
            string file = Path.GetFullPath(candidate);
            string? directory = Path.GetDirectoryName(file);
            if (directory is null || !Directory.Exists(directory) ||
                !string.Equals(Path.GetExtension(file), ".txt", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(file) || Directory.Exists(file))
                return false;

            string? configuredTempRoot = Environment.GetEnvironmentVariable("TEMP",
                EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(configuredTempRoot))
                configuredTempRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Temp");
            string tempRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredTempRoot))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(tempRoot) ||
                (File.GetAttributes(tempRoot) & FileAttributes.ReparsePoint) != 0)
                return false;
            string? parent = Path.GetDirectoryName(directory);
            if (!string.Equals(parent, tempRoot, StringComparison.OrdinalIgnoreCase))
                return false;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                return false;

            string folder = Path.GetFileName(directory);
            const string releasePrefix = "SpankyhoOptimizer-ReleaseGate-";
            const string isolatedPrefix = "SpankyhoOptimizer-ReadyTest-";
            string? guidSuffix = folder.StartsWith(releasePrefix, StringComparison.OrdinalIgnoreCase)
                ? folder.Substring(releasePrefix.Length)
                : folder.StartsWith(isolatedPrefix, StringComparison.OrdinalIgnoreCase)
                    ? folder.Substring(isolatedPrefix.Length)
                    : null;
            if (guidSuffix is null || !Guid.TryParseExact(guidSuffix, "N", out _))
                return false;

            fullPath = file;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or
                                   NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
