using System;
using System.ServiceProcess;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Persisted evidence of continuous scheduler observations for an optional Windows feature.
/// Stopped is not equivalent to unused. This policy never authorizes a service change.
/// </summary>
internal readonly record struct AiOptionalFeatureObservation(
    DateTime FirstUtc, DateTime LastUtc, int Samples, bool WasRunning);

internal static class AiOptionalFeatureObservationPolicy
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan MaximumGap = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan LongObservationWindow = TimeSpan.FromHours(6);

    // A Stopped -> Running transition observed within one fresh monitoring
    // interval is evidence of OS/application demand, not proof of idleness.
    // Older or malformed snapshots cannot manufacture a new activation.
    internal static bool WasRecentlyActivated(
        AiOptionalFeatureObservation? previous, bool running, DateTime nowUtc) =>
        running && nowUtc.Kind == DateTimeKind.Utc &&
        previous is AiOptionalFeatureObservation old &&
        !old.WasRunning && old.Samples >= 1 &&
        old.FirstUtc.Kind == DateTimeKind.Utc &&
        old.LastUtc.Kind == DateTimeKind.Utc &&
        old.FirstUtc <= old.LastUtc && old.LastUtc <= nowUtc &&
        nowUtc - old.LastUtc <= MaximumGap;

    // A Stopped->StartPending transition proves that Windows or an app
    // requested the service, even if our next scan sees it already Running.
    // StopPending, Paused and other state changes do NOT assert fresh demand.
    internal static bool WasRecentlyStarted(
        AiOptionalFeatureObservation? previous,
        ServiceControllerStatus liveStatus, DateTime nowUtc) =>
        (liveStatus is ServiceControllerStatus.Running or
            ServiceControllerStatus.StartPending) &&
        WasRecentlyActivated(previous, true, nowUtc);

    internal static AiOptionalFeatureObservation Next(
        AiOptionalFeatureObservation? previous, DateTime nowUtc, bool running)
    {
        if (nowUtc.Kind != DateTimeKind.Utc ||
            previous is not AiOptionalFeatureObservation last ||
            last.FirstUtc.Kind != DateTimeKind.Utc ||
            last.LastUtc.Kind != DateTimeKind.Utc ||
            last.Samples < 1 || last.FirstUtc > last.LastUtc ||
            last.LastUtc > nowUtc ||
            nowUtc - last.LastUtc > MaximumGap ||
            last.WasRunning != running)
            return new(nowUtc, nowUtc, 1, running);

        if (nowUtc - last.LastUtc < SampleInterval)
            return last;

        return new(last.FirstUtc, nowUtc,
            Math.Min(last.Samples + 1, 1_000_000), running);
    }

    // Six hours of *observed* stopped state is a review hint, not proof of
    // unused hardware or authorization to change its start mode.
    internal static bool HasLongStoppedObservation(
        AiOptionalFeatureObservation observation, DateTime nowUtc) =>
        !observation.WasRunning && observation.Samples >= 6 &&
        observation.FirstUtc.Kind == DateTimeKind.Utc &&
        observation.LastUtc.Kind == DateTimeKind.Utc &&
        observation.FirstUtc <= observation.LastUtc &&
        observation.LastUtc <= nowUtc &&
        nowUtc - observation.LastUtc <= MaximumGap &&
        observation.LastUtc - observation.FirstUtc >= LongObservationWindow;
}
