namespace SpankyhoOptimizer.Services;

public static class RuntimeHostStartupPolicy
{
    public static bool ShouldLogVoiceRuntime(bool watchdog) =>
        ShouldLogVoiceRuntime(watchdog, watchdogLoop: false, hardwareBridge: false);

    public static bool ShouldLogVoiceRuntime(bool watchdog, bool watchdogLoop) =>
        ShouldLogVoiceRuntime(watchdog, watchdogLoop, hardwareBridge: false);

    public static bool ShouldLogVoiceRuntime(
        bool watchdog,
        bool watchdogLoop,
        bool hardwareBridge) =>
        !watchdog && !watchdogLoop && !hardwareBridge;

    public static bool ShouldRunInSession(
        int sessionId,
        bool watchdog,
        bool watchdogLoop,
        bool hardwareBridge)
    {
        // Fan agent / fan watchdog are user-session runtimes. Historical elevated
        // scheduled tasks could start another copy in Session 0 and Local\ mutexes
        // do not span sessions, so both copies could stay alive at once.
        //
        // The hardware telemetry bridge is intentionally exempt: it may run in a
        // protected/elevated context and is designed to publish telemetry to the
        // user-session agent.
        if (hardwareBridge)
            return true;

        return sessionId != 0;
    }
}
