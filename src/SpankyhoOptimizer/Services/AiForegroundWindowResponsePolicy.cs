using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Bounded WM_NULL response-time PROXY. It is not input-to-photon latency,
/// frame time, scheduling fairness or proof that Windows became faster.
/// Readings are only comparable for the same foreground PID and HWND.
/// </summary>
internal static class AiForegroundWindowResponsePolicy
{
    internal const int ProbeIntervalSeconds = 30;
    internal const int NativeTimeoutMs = 80;
    internal const int RequiredSamples = 2;
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(65);
    internal static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(100);

    internal sealed record Snapshot(
        DateTime ObservedUtc, int ForegroundPid, long WindowHandle,
        double? RoundTripMs, string Reason);

    internal readonly record struct Window(
        int Count, double TotalMs, double MinimumMs, double MaximumMs,
        DateTime LastUpdatedUtc, long WindowHandle)
    {
        internal double AverageMs => Count > 0 ? TotalMs / Count : double.NaN;
    }

    internal static bool CanProbe(
        bool aiEnabled, AdaptivePowerRuntimeStatus? power,
        AiWorkloadClass workload, DateTime nowUtc) =>
        aiEnabled && workload == AiWorkloadClass.Interactive &&
        AdaptivePowerRuntimeService.IsStatusFresh(power, nowUtc) &&
        power is { GameActive: false, ForegroundProcessId: > 0 } &&
        !string.IsNullOrWhiteSpace(power.ForegroundProcess) &&
        !AiAutomaticProcessPriorityPolicy.IsWindowsSearchIndexer(power.ForegroundProcess) &&
        !AiAutomaticProcessPriorityPolicy.IsInteractiveSearchUi(power.ForegroundProcess);

    internal static bool FreshValid(
        Snapshot? sample, int expectedPid, DateTime phaseSinceUtc, DateTime nowUtc) =>
        sample is not null &&
        expectedPid > 0 &&
        sample.ForegroundPid == expectedPid &&
        sample.WindowHandle != 0 &&
        sample.RoundTripMs is double ms && double.IsFinite(ms) &&
        ms >= 0 && ms <= NativeTimeoutMs &&
        sample.ObservedUtc > phaseSinceUtc &&
        sample.ObservedUtc <= nowUtc &&
        nowUtc - sample.ObservedUtc <= MaximumAge;

    internal static bool NativeProbeFailedDuringTrial(
        Snapshot? sample, int expectedPid, DateTime phaseSinceUtc, DateTime nowUtc) =>
        sample is not null &&
        expectedPid > 0 && sample.ForegroundPid == expectedPid &&
        sample.ObservedUtc > phaseSinceUtc &&
        sample.ObservedUtc <= nowUtc &&
        nowUtc - sample.ObservedUtc <= MaximumAge &&
        sample.RoundTripMs is null &&
        sample.Reason is ("native-timeout-or-unavailable" or
            "probe-unavailable" or "foreground-switched-before-probe" or
            "foreground-switched-after-probe");

    // A changed HWND with the SAME PID still means a different foreground
    // window/task. This must abort the trial even after B/A already gathered
    // an earlier valid sample; AddDistinct alone would ignore the mismatch.
    internal static bool WindowSwitched(
        Snapshot? sample, int expectedPid, long expectedWindowHandle,
        DateTime phaseSinceUtc, DateTime nowUtc) =>
        expectedWindowHandle != 0 &&
        FreshValid(sample, expectedPid, phaseSinceUtc, nowUtc) &&
        sample!.WindowHandle != expectedWindowHandle;

    internal static bool StableWindow(Window readings) =>
        readings.Count >= RequiredSamples && readings.WindowHandle != 0 &&
        double.IsFinite(readings.AverageMs) &&
        readings.MaximumMs >= readings.MinimumMs &&
        readings.MaximumMs - readings.MinimumMs <=
            Math.Max(12, readings.AverageMs * 1.5);

    internal static bool IsCurrentDisplaySample(
        Snapshot? sample, int currentForegroundPid, DateTime nowUtc) =>
        sample is not null && currentForegroundPid > 0 &&
        FreshValid(sample, currentForegroundPid, DateTime.MinValue, nowUtc) &&
        string.Equals(sample.Reason, "WM_NULL-roundtrip", StringComparison.Ordinal);

    internal static Window AddDistinct(
        Window current, Snapshot? sample,
        int expectedPid, DateTime phaseSinceUtc, DateTime nowUtc)
    {
        if (!FreshValid(sample, expectedPid, phaseSinceUtc, nowUtc) ||
            sample!.ObservedUtc <= current.LastUpdatedUtc ||
            (current.Count > 0 &&
             (sample.WindowHandle != current.WindowHandle ||
              sample.ObservedUtc - current.LastUpdatedUtc > MaximumGap)))
            return current;
        double ms = sample.RoundTripMs!.Value;
        return new Window(
            current.Count + 1,
            current.TotalMs + ms,
            current.Count == 0 ? ms : Math.Min(current.MinimumMs, ms),
            current.Count == 0 ? ms : Math.Max(current.MaximumMs, ms),
            sample.ObservedUtc, sample.WindowHandle);
    }

    internal static bool SignificantRegression(Window baseline, Window candidate) =>
        StableWindow(baseline) &&
        StableWindow(candidate) &&
        baseline.WindowHandle == candidate.WindowHandle &&
        baseline.AverageMs >= 0 &&
        candidate.AverageMs > baseline.AverageMs + Math.Max(15, baseline.AverageMs * 2);

    internal static bool ComparableAba(
        Window baseline, Window candidate, Window restored) =>
        StableWindow(baseline) &&
        StableWindow(candidate) &&
        StableWindow(restored) &&
        baseline.WindowHandle == candidate.WindowHandle &&
        baseline.WindowHandle == restored.WindowHandle &&
        // A/B/A is invalid if ambient window response drifted after priority
        // restoration: two individually stable A phases need not be alike.
        // WM_NULL is only a bounded UI-response proxy, not input latency.
        Math.Abs(restored.AverageMs - baseline.AverageMs) <=
            Math.Max(10, baseline.AverageMs * 0.5);

    internal static string Describe(Window baseline, Window candidate, Window restored)
    {
        if (!ComparableAba(baseline, candidate, restored))
            return "WM_NULL odezva okna: nesrovnatelné vzorky, bez výkonového závěru";
        return $"WM_NULL odezva okna (indikátor, ne input lag): " +
            $"A={baseline.AverageMs:0.##} ms / B={candidate.AverageMs:0.##} ms / " +
            $"A={restored.AverageMs:0.##} ms; nezaručuje vyšší FPS ani rychlejší Windows";
    }

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var power = new AdaptivePowerRuntimeStatus(
            now.AddSeconds(-4), "Observation", 9, "", null, null, null, false, false,
            20, 2, "Interactive", "", "SearchIndexer", 4, "chrome",
            ForegroundProcessId: 501);
        var first = new Snapshot(now.AddSeconds(-60), 501, 12345, 3, "WM_NULL");
        var second = first with { ObservedUtc = now.AddSeconds(-30), RoundTripMs = 4 };
        Window baseline = AddDistinct(default, first, 501, now.AddMinutes(-2), now);
        baseline = AddDistinct(baseline, second, 501, now.AddMinutes(-2), now);
        Window duplicated = AddDistinct(baseline, second, 501, now.AddMinutes(-2), now);
        Window trial = AddDistinct(
            AddDistinct(default, first with { RoundTripMs = 19 }, 501,
                now.AddMinutes(-2), now),
            second with { RoundTripMs = 23 }, 501, now.AddMinutes(-2), now);
        Window restored = AddDistinct(
            AddDistinct(default, first, 501, now.AddMinutes(-2), now),
            second, 501, now.AddMinutes(-2), now);
        Window noisyBaseline = baseline with {
            MaximumMs = 65, MinimumMs = 1
        };
        if (!StableWindow(baseline) || StableWindow(noisyBaseline) ||
            SignificantRegression(noisyBaseline, trial) ||
            !WindowSwitched(second with {
                ObservedUtc = now.AddSeconds(-5), WindowHandle = 22222
            }, 501, 12345, now.AddMinutes(-1), now) ||
            WindowSwitched(second with {
                ObservedUtc = now.AddSeconds(-5), WindowHandle = 22222,
                ForegroundPid = 502
            }, 501, 12345, now.AddMinutes(-1), now) ||
            WindowSwitched(second with {
                ObservedUtc = now.AddMinutes(-4), WindowHandle = 22222
            }, 501, 12345, now.AddMinutes(-1), now) ||
            !IsCurrentDisplaySample(second with {
                Reason = "WM_NULL-roundtrip"
            }, 501, now) ||
            IsCurrentDisplaySample(second with {
                Reason = "WM_NULL-roundtrip"
            }, 502, now) ||
            IsCurrentDisplaySample(second with {
                Reason = "native-timeout-or-unavailable"
            }, 501, now) ||
            IsCurrentDisplaySample(second with {
                Reason = "WM_NULL-roundtrip",
                ObservedUtc = now.AddMinutes(-3)
            }, 501, now) ||
            !CanProbe(true, power, AiWorkloadClass.Interactive, now) ||
            CanProbe(false, power, AiWorkloadClass.Interactive, now) ||
            CanProbe(true, power, AiWorkloadClass.Game, now) ||
            CanProbe(true, power with { GameActive = true }, AiWorkloadClass.Interactive, now) ||
            CanProbe(true, power with { ForegroundProcessId = 0 }, AiWorkloadClass.Interactive, now) ||
            CanProbe(true, power with { ForegroundProcess = "SearchHost" }, AiWorkloadClass.Interactive, now) ||
            CanProbe(true, power with { UpdatedUtc = now.AddMinutes(-5) }, AiWorkloadClass.Interactive, now) ||
            baseline.Count != 2 || duplicated.Count != 2 ||
            !SignificantRegression(baseline, trial) ||
            SignificantRegression(baseline, restored) ||
            !NativeProbeFailedDuringTrial(second with {
                ObservedUtc = now.AddSeconds(-5), RoundTripMs = null,
                Reason = "native-timeout-or-unavailable"
            }, 501, now.AddMinutes(-1), now) ||
            NativeProbeFailedDuringTrial(second with {
                ObservedUtc = now.AddMinutes(-5), RoundTripMs = null,
                Reason = "native-timeout-or-unavailable"
            }, 501, now.AddMinutes(-6), now) ||
            NativeProbeFailedDuringTrial(second with {
                ObservedUtc = now.AddSeconds(-5), ForegroundPid = 502,
                RoundTripMs = null, Reason = "native-timeout-or-unavailable"
            }, 501, now.AddMinutes(-1), now) ||
            NativeProbeFailedDuringTrial(second with {
                ObservedUtc = now.AddSeconds(-5), RoundTripMs = null,
                Reason = "not-windows-or-invalid-pid"
            }, 501, now.AddMinutes(-1), now) ||
            AddDistinct(baseline, second with { ForegroundPid = 502 }, 501, now.AddMinutes(-2), now).Count != 2 ||
            AddDistinct(baseline, second with { WindowHandle = 22222, ObservedUtc = now.AddSeconds(-10) },
                501, now.AddMinutes(-2), now).Count != 2 ||
            AddDistinct(default, first with { RoundTripMs = null }, 501, now.AddMinutes(-2), now).Count != 0 ||
            AddDistinct(default, first, 501, now.AddSeconds(-20), now).Count != 0 ||
            AddDistinct(default, first with { ObservedUtc = now.AddMinutes(-5) },
                501, now.AddMinutes(-6), now).Count != 0 ||
            !ComparableAba(baseline, trial, restored) ||
            // A' with another mean is not a matching reference, even when
            // both A and A' separately meet StableWindow thresholds.
            !StableWindow(restored with {
                TotalMs = 46, MinimumMs = 22, MaximumMs = 24
            }) ||
            ComparableAba(baseline, trial, restored with {
                TotalMs = 46, MinimumMs = 22, MaximumMs = 24
            }) ||
            !Describe(baseline, trial, restored with {
                TotalMs = 46, MinimumMs = 22, MaximumMs = 24
            }).Contains("nesrovnatelné", StringComparison.Ordinal) ||
            ComparableAba(baseline, trial, restored with { WindowHandle = 999 }) ||
            ComparableAba(noisyBaseline, trial, restored) ||
            !Describe(baseline, trial, restored).Contains("ne input lag", StringComparison.Ordinal) ||
            !Describe(baseline, default, restored).Contains("nesrovnatelné", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "AI foreground response policy accepted stale/mismatched evidence or invented latency benefit.");
        const int count = 32768;
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long tickStart = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
            if (!FreshValid(second, 501, now.AddMinutes(-2), now))
                throw new InvalidOperationException("Unexpected loss of valid GUI probe.");
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds;
        if (bytes > 128)
            throw new InvalidOperationException("Foreground response policy hot path allocated memory.");
        return $"AI_FOREGROUND_RESPONSE_POLICY_SMOKE_OK gameExcluded=true " +
            $"windowPidBound=true windowSwitchRollback=true stableAba=true " +
            $"restoredResponseMustMatchOriginal=true " +
            $"displayBoundToCurrentPid=true timedOutIgnored=true noFakeFpsGain=true " +
            $"syntheticChecks={count} elapsedMs={ms:0.###} allocBytes={bytes} writes=0";
    }
}
