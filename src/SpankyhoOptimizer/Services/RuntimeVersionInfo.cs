using System.Reflection;

namespace SpankyhoOptimizer.Services;

public static class RuntimeVersionInfo
{
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
