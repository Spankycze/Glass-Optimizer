using System;

namespace SpankyhoOptimizer.Services;

public static class FanWatchdogLoopPolicy
{
    public static TimeSpan StartupGrace { get; } = TimeSpan.FromSeconds(4);
    public static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(15);

    public static bool ShouldRestartAgent(bool heartbeatFresh) => !heartbeatFresh;

    // FanAgent heartbeat/health refresh and fan-target reassertion must not
    // freeze for hours if the system UTC clock is corrected backwards.
    // First use and backwards jumps both start a new cadence window.
    public static bool IsPeriodicWorkDue(DateTime lastUtc, DateTime nowUtc, TimeSpan interval) =>
        lastUtc == DateTime.MinValue ||
        nowUtc < lastUtc ||
        nowUtc - lastUtc >= interval;

    // A reused PID cannot validate a heartbeat written before that process started.
    public static bool MatchesHeartbeatProcessStart(DateTime startedUtc, DateTime heartbeatWrittenUtc) =>
        startedUtc != default && heartbeatWrittenUtc != default &&
        startedUtc <= heartbeatWrittenUtc.AddSeconds(2);

    // A future-dated heartbeat must never keep a failed fan agent marked alive.
    public static bool IsHeartbeatTimestampFresh(DateTime writtenUtc, DateTime nowUtc, TimeSpan maxAge) =>
        writtenUtc != default && maxAge > TimeSpan.Zero &&
        writtenUtc <= nowUtc.AddSeconds(2) && nowUtc - writtenUtc <= maxAge;
}
