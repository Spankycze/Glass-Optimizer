namespace SpankyhoOptimizer.Services;

public static class FanAgentRestartPolicy
{
    public static bool ShouldRestart(string? payload) =>
        !string.IsNullOrWhiteSpace(payload);
}
