using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed partial class AdaptivePowerRuntimeService
{
    internal static bool CanReuseEppStatusRead(bool readKnown, bool mutationAttempted,
        string? statusGuid, string? preflightGuid) =>
        readKnown && !mutationAttempted &&
        !string.IsNullOrWhiteSpace(statusGuid) &&
        string.Equals(statusGuid, preflightGuid, StringComparison.OrdinalIgnoreCase);

    // Same native preflight/plan/mutation safety contract applies to Boost.
    // The value MUST be read again after any attempted power change.
    internal static bool CanReuseBoostStatusRead(bool readKnown, bool mutationAttempted,
        string? statusGuid, string? preflightGuid) =>
        CanReuseEppStatusRead(readKnown, mutationAttempted, statusGuid, preflightGuid);

    internal static string RunEppStatusReadPolicySmoke()
    {
        const string plan = "a1b2-c3d4";
        if (!CanReuseEppStatusRead(true, false, plan, plan.ToUpperInvariant()) ||
            CanReuseEppStatusRead(false, false, plan, plan) ||
            CanReuseEppStatusRead(true, true, plan, plan) ||
            CanReuseEppStatusRead(true, false, plan, "other-plan") ||
            CanReuseEppStatusRead(true, false, "", plan) ||
            CanReuseEppStatusRead(true, false, plan, null))
            throw new InvalidOperationException("EPP status read-after-mutation/plan switch safety regression.");
        return "ADAPTIVE_EPP_STATUS_REUSE_SMOKE_OK unchangedPlanReuse=true " +
            "failedWriteForcesRefresh=true planChangeForcesRefresh=true unknownForcesRefresh=true";
    }

    public static bool IsStatusFresh(
        AdaptivePowerRuntimeStatus? status,
        DateTime nowUtc,
        TimeSpan? maximumAge = null)
    {
        if (status == null || status.UpdatedUtc == DateTime.MinValue ||
            status.UpdatedUtc.Kind != DateTimeKind.Utc ||
            nowUtc.Kind != DateTimeKind.Utc)
            return false;
        if (status.UpdatedUtc > nowUtc.AddSeconds(5))
            return false;

        TimeSpan age = nowUtc - status.UpdatedUtc;
        return age >= TimeSpan.Zero &&
               age <= (maximumAge ?? TimeSpan.FromSeconds(20));
    }

    public static AdaptivePowerRuntimeStatus? ReadStatus()
    {
        try
        {
            string path = AdaptivePowerRuntimePathPolicy.ResolveLoadPath();
            return StatusReadCache.Read(path);
        }
        catch
        {
            return null;
        }
    }

    // The foreground app needs immediate mode/game/EPP transitions, not a
    // new atomic JSON file every time the busiest process name fluctuates.
    internal static bool ShouldWriteAdaptiveStatus(AdaptivePowerStatusKey? previousCriticalKey,
        AdaptivePowerStatusKey nextCriticalKey, DateTime lastWriteUtc, DateTime nowUtc) =>
        previousCriticalKey != nextCriticalKey || nowUtc < lastWriteUtc ||
        nowUtc - lastWriteUtc >= TimeSpan.FromSeconds(5);

    internal static string RunAdaptiveStatusHotpathSmoke()
    {
        // Test current/legacy source selection without any disk writes.
        _ = AdaptivePowerRuntimePathPolicy.RunFunctionalSmoke();
        // A jittering blocker alone must not trigger 120 atomic file writes.
        // A mode or game-state change must still publish immediately.
        DateTime start = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        var steady = new AdaptivePowerStatusKey("Balanced", false, false, 20, 4, "Stable");
        AdaptivePowerStatusKey? current = null;
        DateTime last = DateTime.MinValue;
        int writes = 0;
        for (int i = 0; i < 120; i++)
        {
            DateTime now = start.AddSeconds(i);
            if (!ShouldWriteAdaptiveStatus(current, steady, last, now)) continue;
            current = steady;
            last = now;
            writes++;
        }
        if (writes != 24 ||
            ShouldWriteAdaptiveStatus(steady, steady, start, start.AddSeconds(4)) ||
            !ShouldWriteAdaptiveStatus(steady, steady, start, start.AddSeconds(5)) ||
            !ShouldWriteAdaptiveStatus(steady,
                new AdaptivePowerStatusKey("Performance", true, false, 12, 4, "Stable"),
                start, start.AddSeconds(1)) ||
            !ShouldWriteAdaptiveStatus(steady, steady, start, start.AddSeconds(-1)))
            throw new InvalidOperationException("Adaptive status I/O pacing or critical-transition regression.");
        // Read-only same-host comparison. No settings/file mutations.
        const int samples = 240;
        var status = ReadStatus();
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long clock = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++) _ = ReadStatus();
        double diskMs = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        long diskBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        clock = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++)
            _ = IsStatusFresh(status, DateTime.UtcNow);
        double memoryMs = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        long memoryBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        return $"ADAPTIVE_STATUS_IO_SMOKE_OK jitterWritesOld=120 jitterWritesNew={writes} " +
            $"criticalImmediate=true samples={samples} fileReadMs={diskMs:F3} " +
            $"sameAgentMs={memoryMs:F3} fileReadAllocB={diskBytes} sameAgentAllocB={memoryBytes} " +
            $"statusPresent={status != null}";
    }

    // Read-only baseline comparison on the same machine: old duplicate
    // scheme/config metadata reads vs one snapshot shared within one tick.
    internal static string RunAdaptiveSharedTickSmoke()
    {
        const int cycles = 180;
        var config = AiSystemResourceManagerService.LoadConfig();
        bool active = TryGetActiveSpankyScheme(out string? guid);
        if (config == null || (active && string.IsNullOrWhiteSpace(guid)))
            throw new InvalidOperationException("Power-plan/config query returned invalid state.");
        for (int i = 0; i < 8; i++) _ = TryGetActiveSpankyScheme(out _);

        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long clock = Stopwatch.GetTimestamp();
        for (int i = 0; i < cycles; i++)
        {
            _ = TryGetActiveSpankyScheme(out _);
            _ = TryGetActiveSpankyScheme(out _);
            _ = TryGetActiveSpankyScheme(out _);
            _ = AiSystemResourceManagerService.LoadConfig();
            _ = AiSystemResourceManagerService.LoadConfig();
        }
        double oldMs = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

        beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        clock = Stopwatch.GetTimestamp();
        for (int i = 0; i < cycles; i++)
        {
            bool once = TryGetActiveSpankyScheme(out string? plan);
            var sameTick = AiSystemResourceManagerService.LoadConfig();
            // Consumers use the same immutable snapshot, not another disk
            // metadata query. An actual power mutation still revalidates.
            bool reused = once && !string.IsNullOrWhiteSpace(plan);
            _ = reused;
            _ = sameTick.PowerPlanEnabled;
        }
        double newMs = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        long newBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

        return $"ADAPTIVE_SHARED_TICK_SMOKE_OK cycles={cycles} " +
            $"originalPlanQueries={cycles * 3} optimizedPlanQueries={cycles} " +
            $"originalConfigReads={cycles * 2} optimizedConfigReads={cycles} " +
            $"originalMs={oldMs:F3} optimizedMs={newMs:F3} " +
            $"originalAllocatedB={oldBytes} optimizedAllocatedB={newBytes} " +
            $"spankyPlanActive={active}";
    }

    // A/B of the exact read-only Windows registry operation eliminated from
    // the normal AI EPP tick. Power mutation still performs its own verified
    // re-read under AiPowerGate, and disabled AI never touches live settings.
    internal static string RunEppSameTickReuseSmoke()
    {
        if (!TryGetActiveSpankyScheme(out string? guid) || string.IsNullOrWhiteSpace(guid))
            return "ADAPTIVE_EPP_REGISTRY_REUSE_SMOKE_OK skipped=no-active-spanky-plan";

        const int cycles = 256;
        bool readable = TryReadAcSetting(guid, EppSetting, out _);
        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < cycles; i++)
        {
            _ = TryReadAcSetting(guid, EppSetting, out _);
            _ = TryReadAcSetting(guid, EppSetting, out _);
        }
        double repeatedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long repeatedBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;

        startBytes = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < cycles; i++)
        {
            bool known = TryReadAcSetting(guid, EppSetting, out int first);
            // Identical native snapshot is reused by the mode-selection
            // preflight; fresh read-back is still required before writes.
            int reused = first;
            _ = known && reused >= 0;
        }
        double reusedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long reusedBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
        return $"ADAPTIVE_EPP_REGISTRY_REUSE_SMOKE_OK cycles={cycles} " +
            $"previousReads={cycles * 2} reusedReads={cycles} " +
            $"readable={readable} beforeMs={repeatedMs:F3} afterMs={reusedMs:F3} " +
            $"beforeAllocatedB={repeatedBytes} afterAllocatedB={reusedBytes} " +
            "mutations=0";
    }

    private void MaybeWriteStatus(AdaptivePowerRuntimeStatus status)
    {
        AdaptivePowerStatusKey criticalKey = AdaptivePowerStatusKey.From(status);
        DateTime now = DateTime.UtcNow;
        if (!ShouldWriteAdaptiveStatus(_lastCriticalStatusKey, criticalKey, _lastStatusWriteUtc, now))
            return;
        if (!IsStatusWriteRetryDue(now, _lastStatusFailureUtc))
            return;

        try
        {
            AtomicFileService.WriteText(
                StatusPath,
                JsonSerializer.Serialize(status));
            _lastStatusWriteUtc = now;
            _lastStatusFailureUtc = DateTime.MinValue;
            _lastCriticalStatusKey = criticalKey;
        }
        catch (Exception ex)
        {
            _lastStatusFailureUtc = now;
            if (IsStatusWriteErrorLogDue(now, _lastStatusErrorLogUtc))
            {
                _lastStatusErrorLogUtc = now;
                LogService.Error("power", "Adaptive runtime status write failed", ex);
            }
        }
    }

    internal static bool IsStatusWriteRetryDue(
        DateTime nowUtc,
        DateTime lastFailureUtc) =>
        lastFailureUtc == DateTime.MinValue || nowUtc < lastFailureUtc ||
        nowUtc - lastFailureUtc >= TimeSpan.FromSeconds(30);

    internal static bool IsStatusWriteErrorLogDue(DateTime nowUtc, DateTime lastLogUtc) =>
        lastLogUtc == DateTime.MinValue || nowUtc < lastLogUtc ||
        nowUtc - lastLogUtc >= TimeSpan.FromMinutes(5);

    private RuntimeMode ChooseMode(
        double load,
        double rawLoad,
        bool realIdle,
        bool userInputIdle,
        bool gameActive,
        bool aiSmartEnabled,
        AiSmartRuntimeDecision aiDecision,
        double? packagePowerW,
        double? clockMHz,
        AiWorkloadClass workload)
    {
        load = Math.Clamp(load, 0, 100);
        rawLoad = Math.Clamp(rawLoad, 0, 100);

        if (gameActive)
        {
            // Gaming bypasses ordinary thermal-vote evaluation. Discard the
            // previous non-gaming streak so it cannot affect the first
            // post-game evaluation without fresh thermal evidence.
            _thermalGuardVotes = 0;
            _performanceVotes = 3;
            _idleVotes = 0;
            _exitPerformanceVotes = 0;
            return RuntimeMode.Performance;
        }

        bool thermalGuard =
            aiSmartEnabled &&
            AiPowerDemandPolicy.ShouldThermalGuard(
                gameActive,
                aiDecision.PredictedTemp10s,
                Math.Max(load, rawLoad),
                aiDecision.Confidence);

        _thermalGuardVotes = thermalGuard
            ? Math.Min(3, _thermalGuardVotes + 1)
            : Math.Max(0, _thermalGuardVotes - 1);

        if (_thermalGuardVotes >= 2)
        {
            _performanceVotes = 0;
            _idleVotes = 0;
            _exitPerformanceVotes = 0;
            return RuntimeMode.Balanced;
        }

        // A single thermal-warning epoch must not build Performance entry
        // votes that can immediately override the safety decision next tick.
        if (thermalGuard)
            _performanceVotes = 0;

        bool aiPerformanceSignal =
            aiSmartEnabled &&
            AiPowerDemandPolicy.HasPerformanceDemand(
                gameActive,
                Math.Max(load, rawLoad),
                packagePowerW,
                clockMHz,
                aiDecision.AnomalyScore,
                workload,
                aiDecision.Confidence) &&
            !thermalGuard;

        bool aiIdleSignal =
            aiSmartEnabled &&
            AiPowerIdleAdmissionPolicy.AllowsSuggestedIdle(
                aiDecision.PreferIdle, workload,
                aiDecision.Confidence, aiDecision.AnomalyScore,
                Math.Max(load, rawLoad), packagePowerW);

        bool conservativeWorkload =
            AiWorkloadContextPolicy.RequiresConservativePerformanceThreshold(workload);

        if (aiPerformanceSignal)
        {
            int voteGain = AiPowerTransitionPolicy.PerformanceVoteGain(
                true,
                workload,
                aiDecision.Confidence,
                Math.Max(load, rawLoad),
                packagePowerW);
            _performanceVotes = Math.Min(3, _performanceVotes + voteGain);
        }
        else if (!conservativeWorkload && !thermalGuard &&
                 (load >= 35 || rawLoad >= 60))
        {
            // A verified short CPU burst can precede the smoothed-load EMA.
            // React without waiting for averaging, but retain two-vote entry,
            // conservative-workload admission and the thermal safety gate.
            double demandLoad = Math.Max(load, rawLoad);
            _performanceVotes = Math.Min(3, _performanceVotes +
                AiPowerTransitionPolicy.FallbackInteractiveVoteGain(workload, demandLoad));
        }
        else
            _performanceVotes = 0;

        // A delayed idle classification must never accumulate votes while
        // the same CPU epoch reports a fresh heavy burst. The fast raw-load
        // Performance admission and Idle admission must use compatible evidence.
        if (AiPowerTransitionHysteresisPolicy.AllowIdleEntry(
                realIdle, aiIdleSignal, userInputIdle,
                _lastIdleExitUtc, DateTime.UtcNow) && rawLoad < 35 &&
            !thermalGuard && !aiPerformanceSignal && _performanceVotes == 0)
            _idleVotes++;
        else
            _idleVotes = 0;

        if (_mode == RuntimeMode.Performance)
        {
            bool powerCoolEnough =
                !packagePowerW.HasValue ||
                packagePowerW.Value < 70;

            // Do not exit Performance on a low EMA while the same validated
            // tick still observes a heavy raw CPU burst. Avoid EPP oscillation
            // without removing the existing sustained-exit hysteresis.
            bool sustainedExitSignal =
                !aiPerformanceSignal && rawLoad < 35 &&
                ((load < 30 && powerCoolEnough) ||
                 (load < 20 && rawLoad < 20));

            if (sustainedExitSignal)
                _exitPerformanceVotes++;
            else
                _exitPerformanceVotes = 0;

            if (_exitPerformanceVotes < 4)
                return RuntimeMode.Performance;
            if (AiPowerTransitionHysteresisPolicy.HoldPerformanceForBriefOrdinaryDip(
                    _lastModeTransitionUtc, DateTime.UtcNow, load, realIdle))
            {
                _deferredOrdinaryTransitions++;
                return RuntimeMode.Performance;
            }

            _exitPerformanceVotes = 0;
        }
        else
        {
            _exitPerformanceVotes = 0;
        }

        if (_performanceVotes >= 2)
        {
            // The same interactive 25..40% load used to flip EPP20->12
            // within 2-3 seconds after Balanced exit. Delay ONLY ordinary
            // re-entries: games, >=60% bursts, thermal safety and strong
            // 3-vote urgent demand remain immediate.
            // Three accumulated ordinary votes are NOT an urgent request.
            // Only a genuine per-tick urgent signal (game, measured burst
            // >=60%, or high CPU-package power) can skip recovery dwell.
            bool urgentOnThisTick = Math.Max(load, rawLoad) >= 60 ||
                (aiPerformanceSignal &&
                 AiPowerTransitionPolicy.PerformanceVoteGain(
                     true, workload, aiDecision.Confidence,
                     Math.Max(load, rawLoad), packagePowerW) >= 3);
            if (_mode == RuntimeMode.Balanced &&
                AiPowerTransitionHysteresisPolicy.HoldBalancedAfterRecentPerformanceExit(
                    _lastModeTransitionUtc, DateTime.UtcNow,
                    load, rawLoad, gameActive, urgentOnThisTick))
            {
                _deferredOrdinaryTransitions++;
                return RuntimeMode.Balanced;
            }
            return RuntimeMode.Performance;
        }

        if (_idleVotes >= 4)
            return RuntimeMode.Idle;

        if (_mode == RuntimeMode.Idle && realIdle)
            return RuntimeMode.Idle;

        return RuntimeMode.Balanced;
    }

    private static (int Epp, int Boost) GetModeValues(RuntimeMode mode)
    {
        return mode switch
        {
            RuntimeMode.Idle => (30, 3),
            RuntimeMode.Performance => (12, 4),
            _ => (20, 4)
        };
    }

    // Shared catalog avoids allocating a new 12-entry tuple array per self-heal pass.
    private static readonly (string Setting, int Value, string Name)[] BasePowerSettings =
        {
            ("893dee8e-2bef-41e0-89c6-b55d0929964c", 5, "cpu-min"),
            ("bc5038f7-23e0-4960-96da-33abaf5935ec", 100, "cpu-max"),
            ("8baa4a8a-14c6-4451-8e8b-14bdbd197537", 1, "cppc"),
            ("5d76a2ca-e8c0-402f-a133-2158492d58ad", 0, "cpu-idle"),
            ("94d3a615-a899-4ac5-ae2b-e4d8f634367f", 1, "cooling"),
            ("0cc5b647-c1df-4637-891a-dec35c318583", 25, "core-min"),
            ("ea062031-0e34-4ff1-9b6d-eb1059334028", 100, "core-max"),
            ("465e1f50-b610-473a-ab58-00d1077dc418", 2, "perf-inc-policy"),
            ("40fbefc7-2e9d-4d25-a185-0cfd8574bac6", 0, "perf-dec-policy"),
            ("06cadf0e-64ed-448a-8927-ce7bf90eb35d", 10, "perf-inc-threshold"),
            ("12a0ab44-fe28-4fa9-b3bd-4b64f44960a6", 8, "perf-dec-threshold"),
            ("4d2b0152-7d5c-498b-88e2-34345392a2c5", 15, "perf-check")
        };

    private static async Task<int> EnsureBaseSettingsAsync(string guid, CancellationToken token)
    {
        // The entire 12-setting self-heal, ownership journal and final plan
        // refresh must be one exclusive transaction. Otherwise the UI or
        // another RuntimeHost could restore EPP/Boost halfway through it.
        return await RunBasePowerRepairTransactionAsync(
            AiPowerPath,
            async ct =>
            {
                int repaired = 0;
                foreach (var item in BasePowerSettings)
                {
                    ct.ThrowIfCancellationRequested();
                    if (TryReadAcSetting(guid, item.Setting, out int current) &&
                        current == item.Value)
                        continue;

                    if (await MutateAiSettingAsync(guid, item.Setting, item.Value, ct)
                            .ConfigureAwait(false))
                    {
                        repaired++;
                        LogService.Info("power",
                            $"ADAPTIVE_SELF_HEAL setting={item.Name} value={item.Value}");
                    }
                }
                return repaired;
            },
            async ct =>
            {
                var refresh = await PrivilegedProcessRunner.RunAsync(
                    "powercfg.exe", $"/setactive {guid}",
                    TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                if (!refresh.Success)
                    throw new InvalidOperationException("AI base profile refresh failed");
            },
            token,
            stillActive: () =>
                TryGetActiveSpankyScheme(out string? currentPlan) &&
                string.Equals(currentPlan, guid, StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
    }

    // Testable coordination boundary. The same cross-process and in-process
    // ownership locks must cover each repair AND the /setactive refresh.
    internal static Task<int> RunBasePowerRepairTransactionAsync(
        string ownershipPath,
        Func<CancellationToken, Task<int>> repairs,
        Func<CancellationToken, Task> refresh,
        CancellationToken token = default,
        Func<bool>? stillActive = null)
    {
        ArgumentNullException.ThrowIfNull(repairs);
        ArgumentNullException.ThrowIfNull(refresh);
        return RunAiPowerTransactionAsync(ownershipPath, async ct =>
        {
            ct.ThrowIfCancellationRequested();
            // The caller checked its plan before waiting for ownership. Check
            // again inside the lease, or stale queued repairs may run later.
            if (stillActive is not null && !stillActive()) return 0;
            int repaired = await repairs(ct).ConfigureAwait(false);
            if (repaired > 0)
            {
                ct.ThrowIfCancellationRequested();
                // A user-selected plan switch must never be undone by this
                // delayed self-heal's /setactive refresh.
                if (stillActive is not null && !stillActive()) return 0;
                await refresh(ct).ConfigureAwait(false);
            }
            return repaired;
        }, token);
    }
}
