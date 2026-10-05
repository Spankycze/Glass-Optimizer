using System.IO;

namespace SpankyhoOptimizer.Services;

public static class RuntimeHostStagePath
{
    public static string GetDirectory(string localAppData, string version) =>
        Path.Combine(localAppData, "SpankyhoOptimizer", "RuntimeHost", version);

    public static string GetHostPath(string localAppData, string version) =>
        Path.Combine(GetDirectory(localAppData, version), "SpankyhoOptimizer.RuntimeHost.exe");
}
