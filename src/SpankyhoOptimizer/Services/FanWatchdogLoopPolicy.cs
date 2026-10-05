using System;

namespace SpankyhoOptimizer.Services;

public static class FanWatchdogLoopPolicy
{
    public static TimeSpan StartupGrace { get; } = TimeSpan.FromSeconds(4);
    public static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(15);

    public static bool ShouldRestartAgent(bool heartbeatFresh) => !heartbeatFresh;
}
