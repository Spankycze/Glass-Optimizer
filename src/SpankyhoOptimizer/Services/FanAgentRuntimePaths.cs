using System.IO;

namespace SpankyhoOptimizer.Services;

public readonly record struct FanAgentRuntimePathSet(
    string RootDirectory,
    string HeartbeatPath,
    string VersionPath,
    string RestartRequestPath,
    string HealthPath);

public static class FanAgentRuntimePaths
{
    public static FanAgentRuntimePathSet Create(string localAppData)
    {
        string root = Path.Combine(localAppData, "SpankyhoOptimizer", "FanAgent");
        return new(
            root,
            Path.Combine(root, "fan-agent-heartbeat.txt"),
            Path.Combine(root, "fan-agent-version.txt"),
            Path.Combine(root, "fan-agent-restart.request"),
            Path.Combine(root, "fan-agent-health.json"));
    }
}
