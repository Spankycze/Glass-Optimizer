using System;
using System.Reflection;

namespace SpankyhoOptimizer.Services;

public static class RuntimeVersionInfo
{
    // Display only major.minor. Preserve full assembly version for updates,
    // rollback and cross-process protocol compatibility.
    public static string PublicVersion(Assembly assembly)
    {
        Version? v = assembly.GetName().Version;
        return v == null ? "0.0" : $"{v.Major}.{v.Minor}";
    }

    public static string PublicVersion(string? technicalVersion)
    {
        if (Version.TryParse(technicalVersion, out Version? v))
            return $"{v.Major}.{v.Minor}";
        return technicalVersion ?? string.Empty;
    }

    public static string FromAssembly(Assembly assembly)
    {
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
