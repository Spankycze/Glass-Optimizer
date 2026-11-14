using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static partial class FanProfileService
{
    private static async Task RunAgentCoreAsync(bool aiOnly, CancellationToken token)
    {
        if (!aiOnly && !LianLiSlInfinityService.Detect().Present)
        {
            await RunAiAgentAsync(token).ConfigureAwait(false);
            return;
        }

        using var mutex = new Mutex(false, aiOnly ? AiAgentMutexName : AgentMutexName);
        bool owns;
        try { owns = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns) return;

        FanTrayIconService? tray = null;
        DateTime nextTrayRetryUtc = DateTime.MinValue;
        bool restartRequested = false;
        try
        {
            // RAM Guard is a separate application. Never enumerate, kill, or clean
            // its processes/startup entries as a side effect of starting fan control.
            // Legacy cleanup is not part of the fan agent lifecycle.
            if (!aiOnly)
            {
                MarkAgentVersionRunning();
                WriteHeartbeat(force: true);
                EnsureStartupAgentRegistered();
            }

            if (!aiOnly)
            {
                try { tray = FanTrayIconService.StartOnMessageThread(); }
                catch (Exception ex)
                {
                    nextTrayRetryUtc = DateTime.UtcNow.AddSeconds(5);
                    LogService.Error("lianli", "Fan tray icon startup failed", ex);
                }

                // Apply the saved fan profile before starting the automatic control loop.
                await ApplySavedProfileAsync(token).ConfigureAwait(false);
            }

            var adaptivePower = new AdaptivePowerRuntimeService();
            var aiRuntime = new AiSmartRuntimeService();
            var aiSystemRuntime = new AiSystemResourceManagerService();
            // FAN/HID and hot temperature polling must not wait for optional
            // service/task/driver AI observation or A/B/A mutation work.
            // The lane is single-flight, coalesces obsolete epochs, and is
            // drained on exit BEFORE the FanAgent singleton mutex is released.
            await using var aiMaintenanceLane =
                new LatestOnlyAsyncWorkLane<FanAiTickObservation>(
                    async (sample, ct) =>
                    {
                        bool evidenceFresh = sample.CanUseEvidence(
                            DateTime.UtcNow, ForegroundProcessProbe.GetWindowIdentity());
                        // A stuck WMI/SCM call must not freeze the single AI lane
                        // forever. The timeout uses the same cancellation contract
                        // as shutdown; write-ahead/journal recovery stays intact.
                        using var tickCts = CancellationTokenSource
                            .CreateLinkedTokenSource(ct);
                        tickCts.CancelAfter(TimeSpan.FromSeconds(60));
                        await aiSystemRuntime.TickAsync(
                            sample.RealIdle,
                            sample.CpuPercent,
                            tickCts.Token,
                            evidenceFresh ? sample.Status : null,
                            evidenceFresh ? sample.Config : null,
                            evidenceFresh).ConfigureAwait(false);
                    },
                    ex => LogService.Error("ai-system",
                        "Async AI maintenance lane failed", ex),
                    token);
            AiSmartRuntimeDecision aiDecision = new(
                false, "OFF", "None", 0, 0, 0,
                "AI Smart Runtime čeká na první vzorek.",
                "n=0", "none", 0, false, false);
            int[] lastAppliedRpm = { -1, -1, -1, -1 };
            DateTime[] lastApplyUtc = { DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue };
            bool[] seenActiveChannel = new bool[4];
            int[] zeroRpmVotes = new int[4];
            int rpmUnderTargetVotes = 0;
            double[] smoothedRpms = new double[4];
            int[] tooltipRpmBuffer = new int[4];
            // Phase counters and 60s Process handle snapshots are diagnostics,
            // not fan safety inputs. No periodic diagnostic work in normal mode.
            FanAgentStageMetrics? fanTickMetrics = LogService.PerformanceDiagnosticsEnabled
                ? new FanAgentStageMetrics() : null;
            DateTime lastSlowAdaptiveLogUtc = DateTime.MinValue;
            DateTime? lastHidSuccessUtc = null;
            string? lastHidError = null;
            string? lastLoggedHidError = null;
            DateTime lastHidErrorLogUtc = DateTime.MinValue;
            bool hidWasFaulted = false;
            double? filteredTemp = null;
            double? previousFilteredTemp = null;
            FanProfile initial = Load();
            int targetModeRpm = Math.Clamp(initial.TargetRpm, 700,
                LianLiSlInfinityService.MaximumRpm);
            int auto65BaseRpm = Math.Clamp(
                Math.Max(initial.TargetRpm, 1620),
                1620,
                LianLiSlInfinityService.MaximumRpm);

            // Fan safety and RPM readbacks run unchanged; passive resource
            // diagnostics are not sampled inside this safety-critical loop.
            while (!token.IsCancellationRequested)
            {
                if (!aiOnly && HasUpdateRestartRequest())
                {
                    LogService.Info("lianli", "FAN_AGENT_UPDATE_RESTART_ACCEPTED");
                    restartRequested = true;
                    break;
                }

                if (!aiOnly) WriteHeartbeat();
                if (!aiOnly && tray == null && DateTime.UtcNow >= nextTrayRetryUtc)
                {
                    try
                    {
                        tray = FanTrayIconService.StartOnMessageThread();
                        LogService.Info("lianli", "FAN_TRAY_RECOVERED");
                    }
                    catch (Exception ex)
                    {
                        nextTrayRetryUtc = DateTime.UtcNow.AddSeconds(10);
                        LogService.Error("lianli", "Fan tray icon retry failed", ex);
                    }
                }
                tray?.PumpMessages();

                while (tray != null && tray.TryDequeueAction(out FanTrayAction action))
                {
                    if (action == FanTrayAction.RestartAgent)
                    {
                        restartRequested = true;
                        break;
                    }

                    await HandleTrayActionAsync(action, token).ConfigureAwait(false);
                }
                if (restartRequested) break;

                long phaseStart = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                FanProfile profile = Load();
                long afterProfile = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                CpuTemperatureService.CpuTelemetrySample telemetry = HardwareTelemetryBridgeService.ReadBestTelemetry();
                long afterTelemetry = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                int[]? liveRpms = null;
                FanTrayState trayState = FanTrayState.Ok;

                if (!aiOnly)
                {
                    try
                    {
                        liveRpms = LianLiSlInfinityService.ReadFanRpms();
                        lastHidSuccessUtc = DateTime.UtcNow;
                        lastHidError = null;
                        if (hidWasFaulted)
                        {
                            hidWasFaulted = false;
                            lastLoggedHidError = null;
                            LogService.Info("lianli", "FAN_HID_RECOVERED");
                        }

                        for (int i = 0; i < Math.Min(4, liveRpms.Length); i++)
                        {
                            int rpm = liveRpms[i];
                            smoothedRpms[i] = smoothedRpms[i] <= 0
                                ? rpm
                                : smoothedRpms[i] * 0.65 + rpm * 0.35;

                            if (rpm > 100)
                            {
                                seenActiveChannel[i] = true;
                                zeroRpmVotes[i] = 0;
                            }
                            else if (seenActiveChannel[i])
                            {
                                zeroRpmVotes[i]++;
                                if (zeroRpmVotes[i] >= 3)
                                    trayState = FanTrayState.Error;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        trayState = FanTrayState.Warning;
                        lastHidError = ex.GetBaseException().Message;
                        hidWasFaulted = true;

                        DateTime now = DateTime.UtcNow;
                        bool changed = !string.Equals(lastLoggedHidError, lastHidError, StringComparison.Ordinal);
                        if (changed || FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                                lastHidErrorLogUtc, now, TimeSpan.FromSeconds(30)))
                        {
                            lastLoggedHidError = lastHidError;
                            lastHidErrorLogUtc = now;
                            LogService.Error("lianli", "Fan tray RPM read failed", ex);
                        }
                    }
                }

                long afterHid = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                aiDecision = aiRuntime.Tick(
                    profile.AiSmartRuntimeEnabled,
                    telemetry,
                    liveRpms,
                    targetModeRpm,
                    liveRpms != null,
                    adaptivePower.GameActive,
                    adaptivePower.RealIdle,
                    adaptivePower.LoadPercent,
                    adaptivePower.LastBlocker,
                    adaptivePower.WorkloadClass);
                long afterAi = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                int fanWriteChannels = 0;
                int fanWriteAttempts = 0;

                if (!aiOnly && telemetry.Celsius is double temp && temp > 5 && temp < 150)
                {
                    filteredTemp = filteredTemp.HasValue ? filteredTemp.Value * 0.72 + temp * 0.28 : temp;
                    double trend = previousFilteredTemp.HasValue ? filteredTemp.Value - previousFilteredTemp.Value : 0;
                    previousFilteredTemp = filteredTemp;

                    if (profile.CpuTempTargetEnabled)
                    {
                        auto65BaseRpm = CalculateTargetTemperatureRpm(
                            auto65BaseRpm,
                            filteredTemp.Value,
                            profile.CpuTempTargetC,
                            trend);
                        int requestedTargetRpm = profile.AiSmartRuntimeEnabled
                            ? aiRuntime.AdjustAuto65Target(auto65BaseRpm, aiDecision)
                            : auto65BaseRpm;
                        targetModeRpm = profile.AiSmartRuntimeEnabled
                            ? AiFanTargetStabilityPolicy.Stabilize(
                                lastAppliedRpm[0],
                                requestedTargetRpm,
                                telemetry.Celsius,
                                aiDecision.PredictedTemp10s,
                                aiDecision.AnomalyScore,
                                aiDecision.Confidence,
                                adaptivePower.WorkloadClass)
                            : requestedTargetRpm;

                        bool materiallyChanged =
                            lastAppliedRpm[0] < 0 ||
                            Math.Abs(targetModeRpm - lastAppliedRpm[0]) >= 75;
                        bool stale =
                            FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                                lastApplyUtc[0], DateTime.UtcNow, TimeSpan.FromSeconds(20));

                        if (materiallyChanged || stale)
                        {
                            try
                            {
                                fanWriteAttempts++;
                                fanWriteChannels += 4;
                                LianLiSlInfinityService.SetAllFanRpm(targetModeRpm);
                                for (int channel = 0; channel < 4; channel++)
                                {
                                    lastAppliedRpm[channel] = targetModeRpm;
                                    lastApplyUtc[channel] = DateTime.UtcNow;
                                }
                            }
                            catch (Exception ex)
                            {
                                trayState = FanTrayState.Error;
                                LogService.Error("lianli", "CPU target temperature fan control failed", ex);
                            }
                        }

                        if (liveRpms != null)
                        {
                            if (FanRpmReadbackPolicy.TryAverageActive(liveRpms, out double averageReadback))
                            {
                                if (averageReadback < targetModeRpm - 220)
                                    rpmUnderTargetVotes++;
                                else
                                    rpmUnderTargetVotes = 0;

                                if (rpmUnderTargetVotes >= 3)
                                {
                                    try
                                    {
                                        fanWriteAttempts++;
                                        fanWriteChannels += 4;
                                        LianLiSlInfinityService.SetAllFanRpm(targetModeRpm);
                                        rpmUnderTargetVotes = 0;
                                        lastApplyUtc[0] = DateTime.UtcNow;
                                        LogService.Info(
                                            "lianli",
                                            $"AUTO65_RPM_SELF_HEAL target={targetModeRpm} avg={averageReadback:0}");
                                    }
                                    catch (Exception ex)
                                    {
                                        trayState = FanTrayState.Error;
                                        LogService.Error("lianli", "AUTO65 RPM self-heal failed", ex);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // A single clock snapshot keeps the four stale checks consistent.
                        DateTime curveCheckUtc = DateTime.UtcNow;
                        for (int channel = 0; channel < 4; channel++)
                        {
                            bool enabled = profile.ChannelCurveEnabled[channel] || profile.AutoCurveEnabled;
                            if (!enabled) continue;

                            int rpm = profile.ChannelCurveEnabled[channel]
                                ? CalculateChannelCurveRpm(profile, channel, filteredTemp.Value)
                                : CalculateCurveRpm(profile, filteredTemp.Value);

                            bool materiallyChanged = lastAppliedRpm[channel] < 0 || Math.Abs(rpm - lastAppliedRpm[channel]) >= 75;
                            bool stale = FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                                lastApplyUtc[channel], curveCheckUtc, TimeSpan.FromSeconds(20));
                            if (!materiallyChanged && !stale) continue;

                            try
                            {
                                fanWriteAttempts++;
                                fanWriteChannels++;
                                LianLiSlInfinityService.SetFanRpm(channel, rpm, profile.ChannelMaxRpms[channel]);
                                lastAppliedRpm[channel] = rpm;
                                lastApplyUtc[channel] = DateTime.UtcNow;
                            }
                            catch (Exception ex)
                            {
                                trayState = FanTrayState.Error;
                                LogService.Error("lianli", $"Fan curve apply failed channel={channel + 1}", ex);
                            }
                        }
                    }
                }
                else
                {
                    trayState = trayState == FanTrayState.Error ? trayState : FanTrayState.Warning;
                }

                long beforeAdaptivePower = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                try
                {
                    await adaptivePower.TickAsync(
                        telemetry,
                        aiOnly ? null : liveRpms,
                        profile.CpuTempTargetEnabled,
                        profile.AiSmartRuntimeEnabled,
                        aiDecision,
                        token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogService.Error("power", "Adaptive runtime power tick failed", ex);
                }

                long afterPower = fanTickMetrics is null ? 0 : Stopwatch.GetTimestamp();
                if (!aiOnly && fanTickMetrics is not null)
                {
                    double adaptivePowerMs = Stopwatch.GetElapsedTime(beforeAdaptivePower, afterPower).TotalMilliseconds;
                    double fanControlMs = Stopwatch.GetElapsedTime(afterAi, beforeAdaptivePower).TotalMilliseconds;
                    if (adaptivePowerMs + fanControlMs >= 200 &&
                        FanWatchdogLoopPolicy.IsPeriodicWorkDue(
                            lastSlowAdaptiveLogUtc, DateTime.UtcNow, TimeSpan.FromMinutes(1)))
                    {
                        lastSlowAdaptiveLogUtc = DateTime.UtcNow;
                        LogService.Info("lianli",
                            $"FAN_POWER_SLOW adaptiveMs={adaptivePowerMs:F1} " +
                            $"fanControlMs={fanControlMs:F1} " +
                            $"fanWriteAttempts={fanWriteAttempts} fanWriteChannels={fanWriteChannels} " +
                            $"dominant={(adaptivePowerMs >= fanControlMs ? "adaptive" : "fanControl")}");
                    }
                }
                // Use already-running FanAgent; no third tray process, scheduler or polling thread.
                AiSystemSmoothnessGuardService.SampleIfDue(
                    adaptivePower.CpuLoadKnown ? adaptivePower.LoadPercent : double.NaN,
                    adaptivePower.GameActive);
                // Automatic, bounded off-thread CPU Sets/wake-latency evidence;
                // never changes affinity or timer resolution.
                // Always deliver the current admission state, including AI-OFF,
                // game activation or lost CPU evidence: a ThreadPool-queued
                // sample must not run later under an obsolete workload.
                AiSchedulerAutoEvidenceService.QueueIfDue(
                    adaptivePower.CurrentAiConfig is { Enabled: true, SelfOptimizationEnabled: true },
                    adaptivePower.CpuLoadKnown ? adaptivePower.LoadPercent : double.NaN,
                    adaptivePower.GameActive);

                // AI maintenance can perform slow service/driver probes or
                // write-ahead journal I/O. The fan safety loop publishes its
                // latest immutable epoch without blocking on that work.
                // The dedicated lane preserves exactly one AI writer.
                aiMaintenanceLane.Submit(new FanAiTickObservation(
                    DateTime.UtcNow,
                    adaptivePower.RealIdle,
                    adaptivePower.LoadPercent,
                    adaptivePower.CpuLoadKnown,
                    adaptivePower.CurrentStatus,
                    adaptivePower.CurrentAiConfig));

                // Reuse the fixed-size four-channel buffer instead of allocating
                // an array and a LINQ iterator every 1-5-second agent tick.
                int[]? tooltipRpms = liveRpms == null ? null : tooltipRpmBuffer;
                if (tooltipRpms != null)
                {
                    for (int channel = 0; channel < tooltipRpms.Length; channel++)
                        tooltipRpms[channel] = (int)Math.Round(smoothedRpms[channel] / 15d) * 15;
                }

                // Avoid assembling CPU/RPM/mode strings every 1–1.5s when
                // Explorer won't be notified. Status changes bypass the
                // regular 5-second pacing and are visible immediately.
                if (!aiOnly && tray != null)
                {
                    if (tray.ShouldComposeTooltip(trayState))
                    {
                        tray.Update(
                            BuildTrayTooltip(
                                telemetry,
                                tooltipRpms,
                                adaptivePower.ModeName,
                                profile.CpuTempTargetEnabled,
                                adaptivePower.GameActive,
                                aiDecision),
                            trayState);
                    }
                    else
                    {
                        // No skipped tray click/menu handling on suppressed updates.
                        tray.PumpMessages();
                    }
                }

                if (!aiOnly) WriteHealthStatus(
                    telemetry,
                    tooltipRpms,
                    profile,
                    adaptivePower.ModeName,
                    adaptivePower.GameActive,
                    adaptivePower.RealIdle,
                    adaptivePower.LoadPercent,
                    aiDecision,
                    liveRpms != null,
                    lastHidSuccessUtc,
                    lastHidError);
                long afterHealth = !aiOnly && fanTickMetrics is not null ? Stopwatch.GetTimestamp() : 0;
                if (!aiOnly && fanTickMetrics is not null)
                {
                    fanTickMetrics.Record(phaseStart, afterProfile, afterTelemetry,
                        afterHid, afterAi, afterPower, afterHealth);
                    fanTickMetrics.ReportIfDue();
                }

                bool dynamicFanControl = !aiOnly && (
                    profile.CpuTempTargetEnabled ||
                    profile.AutoCurveEnabled ||
                    Array.IndexOf(profile.ChannelCurveEnabled, true) >= 0);
                int loopDelayMs =
                    telemetry.Celsius is double currentTemp && currentTemp >= 70
                        ? 1000
                        : dynamicFanControl
                            ? 1500
                            : adaptivePower.GameActive
                                ? 2000
                                : adaptivePower.ModeName.Equals("Idle", StringComparison.OrdinalIgnoreCase)
                                    ? 5000
                                    : 3000;

                await Task.Delay(loopDelayMs, token).ConfigureAwait(false);
            }
        }
        finally
        {
            tray?.Dispose();
            CpuTemperatureService.Dispose();
            try { mutex.ReleaseMutex(); } catch { }
            if (!aiOnly)
            {
                try { File.Delete(HeartbeatPath); } catch { }
                try { File.Delete(AgentHealthPath); } catch { }
            }

            if (restartRequested && !aiOnly)
            {
                await Task.Delay(350, CancellationToken.None).ConfigureAwait(false);
                StartAgentIfNeeded();
            }
        }
    }
}
