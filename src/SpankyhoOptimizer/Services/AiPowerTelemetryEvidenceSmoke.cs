using System;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// Pure production-policy checks. No powercfg, service, registry or fan writes.
internal static class AiPowerTelemetryEvidenceSmoke
{
    internal static async Task RunAsync()
    {
        static void Check(bool condition, string scenario)
        {
            if (!condition)
                throw new InvalidOperationException("AI_POWER_EVIDENCE_SMOKE_FAIL: " + scenario);
        }

        // 1-8: Distinguish real measured CPU power from missing/faulty data.
        Check(AiPowerTelemetryEvidencePolicy.HasUsablePower(0), "power zero valid");
        Check(AiPowerTelemetryEvidencePolicy.HasUsablePower(88), "power 88 valid");
        Check(AiPowerTelemetryEvidencePolicy.HasUsablePower(350), "workstation power valid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsablePower(double.NaN), "power NaN invalid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsablePower(double.PositiveInfinity), "power infinity invalid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsablePower(-1), "negative power invalid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsablePower(1001), "wildly high power invalid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsablePower(null), "missing power invalid");

        // 9-13: Clock metadata can be absent without inventing turbo demand.
        Check(AiPowerTelemetryEvidencePolicy.HasUsableClock(3800), "normal turbo clock");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsableClock(0), "clock zero invalid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsableClock(double.PositiveInfinity), "clock infinity invalid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsableClock(15000), "clock physically implausible");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsableClock(null), "clock missing");

        // 14-18: Confidence and anomaly scores are normalized evidence only.
        Check(AiPowerTelemetryEvidencePolicy.HasUsableConfidence(100), "confidence high valid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsableConfidence(double.PositiveInfinity), "confidence infinity invalid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsableConfidence(-1), "confidence negative invalid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsableAnomaly(double.NaN), "anomaly NaN invalid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsableAnomaly(101), "anomaly out of range");

        // 19-22: Implausible temperatures are not a real thermal emergency.
        Check(AiPowerTelemetryEvidencePolicy.HasUsablePredictedTemperature(85), "thermal 85 valid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsablePredictedTemperature(double.PositiveInfinity), "thermal infinity invalid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsablePredictedTemperature(140), "thermal 140 invalid");
        Check(!AiPowerTelemetryEvidencePolicy.HasUsableCpuTemperature(-273), "temperature impossible");

        // 23-26: Measured wattage and clock can justify performance; invalid
        // sensors alone must never create artificial performance demand.
        Check(AiPowerDemandPolicy.HasPerformanceDemand(
            false, 42, 100, null, 0, AiWorkloadClass.Background), "valid power demand");
        Check(!AiPowerDemandPolicy.HasPerformanceDemand(
            false, 42, double.PositiveInfinity, double.PositiveInfinity, 0, AiWorkloadClass.Background),
            "invalid power and clock do not create demand");
        Check(!AiPowerDemandPolicy.HasPerformanceDemand(
            false, 51, null, null, double.PositiveInfinity, AiWorkloadClass.Background),
            "infinite anomaly ignored");
        Check(AiPowerDemandPolicy.HasPerformanceDemand(
            true, 4, null, null, 0, AiWorkloadClass.Game), "game still immediate performance");

        // 27-30: Existing CPU-load and conservative fallback remain functional.
        Check(AiPowerDemandPolicy.HasPerformanceDemand(
            false, double.NaN, null, null, 0), "unknown CPU retains performance");
        Check(AiPowerDemandPolicy.HasPerformanceDemand(
            false, 85, null, null, 0), "high CPU performance");
        Check(!AiPowerDemandPolicy.HasPerformanceDemand(
            false, 5, null, null, 0), "idle CPU no demand");
        Check(AiPowerDemandPolicy.HasPerformanceDemand(
            false, 61, null, null, 0, AiWorkloadClass.Interactive), "interactive demand");

        // 31-34: Trial vote gain must not convert a broken sensor into
        // urgent performance entry, while preserving genuine urgent demand.
        Check(AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Background, 90, 40, double.PositiveInfinity) == 1,
            "infinite power no urgent vote");
        Check(AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Background, 90, 40, 105) == 3,
            "real high power urgent vote");
        Check(AiPowerTransitionPolicy.PerformanceVoteGain(
            false, AiWorkloadClass.Game, 100, 100, 100) == 0,
            "no signal no vote");
        Check(AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Interactive, 75, 61, null) == 2,
            "interactive burst quick entry");

        // Power policy boundaries: preserve hysteresis, thermal safety and
        // immediate gaming admission only when performance demand is present.
        Check(AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Game, 100, 12, null) == 3, "game fast admission");
        Check(AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Idle, 100, 10, null) == 1, "idle cannot fast admit");
        Check(AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Background, 90, 75, null) == 3, "measured high load fast admit");
        Check(AiPowerTransitionPolicy.FallbackInteractiveVoteGain(
            AiWorkloadClass.Interactive, 60) == 2, "interactive fallback boundary");
        Check(AiPowerTransitionPolicy.FallbackInteractiveVoteGain(
            AiWorkloadClass.Interactive, double.NaN) == 1, "invalid fallback load");

        Check(AiPowerTransitionPolicy.PerformanceVoteGain(
            false, AiWorkloadClass.Game, 100, double.NaN, null) == 0,
            "disabled demand cannot fast admit");
        Check(AiPowerTransitionPolicy.PerformanceVoteGain(
            true, AiWorkloadClass.Interactive, 80, 59.9, null) == 2,
            "confident interactive near threshold");
        Check(AiPowerTransitionPolicy.FallbackInteractiveVoteGain(
            AiWorkloadClass.Background, 95) == 1,
            "background does not enter interactive fallback");

        // Known-game admission must recognize executable names without treating
        // generic launchers, browsers or anti-cheat processes as games.
        Check(AiKnownGameProcessPolicy.IsKnownGame("cs2"), "game name admitted");
        Check(AiKnownGameProcessPolicy.IsKnownGame("CS2.EXE"), "game exe case insensitive");
        Check(AiKnownGameProcessPolicy.IsKnownGame("VALORANT-Win64-Shipping.exe"), "game shipping binary");
        Check(!AiKnownGameProcessPolicy.IsKnownGame("steam.exe"), "launcher rejected");
        Check(!AiKnownGameProcessPolicy.IsKnownGame("chrome.exe"), "browser rejected");
        Check(!AiKnownGameProcessPolicy.IsKnownGame("EasyAntiCheat.exe"), "anticheat rejected");
        Check(!AiKnownGameProcessPolicy.IsKnownGame(null), "null process rejected");
        Check(!AiKnownGameProcessPolicy.IsKnownGame(@"C:\\Games\\cs2.exe"), "path rejected");
        Check(!AiKnownGameProcessPolicy.IsKnownGame("cs2.exe --launch"), "command line rejected");
        Check(!AiKnownGameProcessPolicy.IsKnownGame("cs2.exe:stream"), "stream alias rejected");

        // 35-38: Actual thermal guard requires plausible sensor evidence.
        Check(AiPowerDemandPolicy.ShouldThermalGuard(false, 80, 35, 80),
            "real thermal protection retained");
        Check(!AiPowerDemandPolicy.ShouldThermalGuard(false, double.PositiveInfinity, 35, 80),
            "thermal infinity cannot force fake guard");
        Check(!AiPowerDemandPolicy.ShouldThermalGuard(false, 80, 35, double.PositiveInfinity),
            "infinite confidence not admissible");
        Check(!AiPowerDemandPolicy.ShouldThermalGuard(true, 80, 35, 80),
            "gaming thermal routing unchanged");

        // A/B/A comparability regression: candidate temperature and recovered
        // scheduler latency must stay comparable even if fixed work is faster.
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        AiSystemPerformanceSample[] Window(int offsetSeconds, double fixedMs,
            double tempC, double schedulerMs)
        {
            var samples = new AiSystemPerformanceSample[6];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = new AiSystemPerformanceSample(
                    start.AddSeconds(offsetSeconds + i * 6), 20, 4096,
                    schedulerMs, fixedMs, "stable-test-workload", tempC,
                    true, "", "", 10);
            return samples;
        }
        Check(AiSystemPerformancePolicy.SampleBlocker(
            new AiSystemPerformanceSample(start, 20, 4096, 2, 10,
                "stable-test-workload", -90, true, "", "", 10)).Length != 0,
            "implausible negative CPU temperature rejects measured trial");
        Check(AiSystemPerformancePolicy.SampleBlocker(
            new AiSystemPerformanceSample(start, 20, 4096, 2, 10,
                "stable-test-workload", 150, true, "", "", 10)).Length != 0,
            "implausible high CPU temperature rejects measured trial");
        var gapWindow = Window(0, 10, 50, 2);
        // Skipped/busy samples must never conceal an excessive time gap.
        gapWindow[3] = gapWindow[3] with { Utc = gapWindow[3].Utc.AddSeconds(46) };
        for (int i = 4; i < gapWindow.Length; i++)
            gapWindow[i] = gapWindow[i] with { Utc = gapWindow[i].Utc.AddSeconds(46) };
        Check(AiSystemPerformancePolicy.WindowBlocker(gapWindow) ==
            "Duplicate/out-of-order/gapped measurements",
            "skipped native reads cannot create a comparable A/B/A window across a long gap");
        var a = Window(0, 10, 50, 2);
        var b = Window(60, 8, 50, 2);
        var a2 = Window(120, 10, 50, 2);
        Check(AiSystemPerformancePolicy.Compare(a, b, a2).Accepted,
            "stable A/B/A measurable gain accepted");
        var noisyCandidate = Window(60, 8.25, 50, 2);
        for (int i = 3; i < noisyCandidate.Length; i++)
            noisyCandidate[i] = noisyCandidate[i] with { FixedWorkMs = 9.35 };
        Check(!AiSystemPerformancePolicy.Compare(a, noisyCandidate, a2).Accepted,
            "candidate dispersion prevents false measured gain");
        Check(!AiSystemPerformancePolicy.Compare(a, Window(60, 8, 56, 2), a2).Accepted,
            "candidate thermal drift rejected");
        Check(!AiSystemPerformancePolicy.Compare(a, b, Window(120, 10, 50, 4)).Accepted,
            "recovered scheduler drift rejected");
        Check(AiSystemPerformancePolicy.Compare(a, Window(60, 8, 55, 2), a2).Accepted,
            "candidate thermal boundary plus five accepted");
        Check(!AiSystemPerformancePolicy.Compare(a, Window(60, 8, 44, 2), a2).Accepted,
            "candidate thermal drop of six rejected");
        Check(AiSystemPerformancePolicy.Compare(a, b, Window(120, 10, 53, 2)).Accepted,
            "reference temperature boundary plus three accepted");
        Check(!AiSystemPerformancePolicy.Compare(a, b, Window(120, 10, 54, 2)).Accepted,
            "reference temperature plus four rejected");
        Check(AiSystemPerformancePolicy.Compare(a, b, Window(120, 10, 50, 3)).Accepted,
            "reference scheduler boundary plus one accepted");
        Check(!AiSystemPerformancePolicy.Compare(a, b, Window(120, 10, 50, 0.5)).Accepted,
            "reference scheduler decrease beyond one rejected");

        Check(AdaptivePowerEppTuner.IsInconclusiveReason(
            "Temperature changed between A/B/A windows; inconclusive"),
            "thermal comparison is inconclusive, not candidate rejection");
        Check(AdaptivePowerEppTuner.IsInconclusiveReason(
            "Reference scheduler-delay changed; inconclusive"),
            "reference scheduler shift is inconclusive, not candidate rejection");
        Check(AdaptivePowerEppTuner.HasInconclusiveEvidence(
            "Trial inconclusive: Reference scheduler-delay changed; inconclusive"),
            "persisted reference scheduler shift uses inconclusive cooldown");
        Check(!AdaptivePowerEppTuner.IsInconclusiveReason("Scheduler-delay regression"),
            "candidate scheduler regression remains rejection");
        Check(!AdaptivePowerEppTuner.IsInconclusiveReason(
            "No significant repeatable fixed-work improvement"),
            "inadequate measured gain remains rejection");
        DateTime verdictAt = start.AddHours(1);
        const string thermalEvidence = "Trial inconclusive: Temperature changed between A/B/A windows; inconclusive";
        const string schedulerEvidence = "Trial inconclusive: Reference scheduler-delay changed; inconclusive";
        Check(AdaptivePowerEppTuner.RetryCooldownForEvidence(thermalEvidence) == TimeSpan.FromMinutes(20),
            "temperature drift uses twenty-minute retry");
        Check(AdaptivePowerEppTuner.RetryCooldownForEvidence(schedulerEvidence) == TimeSpan.FromMinutes(20),
            "scheduler drift uses twenty-minute retry");
        Check(AdaptivePowerEppTuner.IsRetryCooldownActive(verdictAt, thermalEvidence, verdictAt.AddMinutes(19)),
            "temperature retry blocked before cooldown expires");
        Check(!AdaptivePowerEppTuner.IsRetryCooldownActive(verdictAt, thermalEvidence, verdictAt.AddMinutes(20)),
            "temperature retry allowed at cooldown boundary");
        Check(AdaptivePowerEppTuner.RetryCooldownForEvidence("Scheduler-delay regression") == TimeSpan.FromHours(24),
            "real scheduler regression keeps twenty-four-hour cooldown");
        Check(!AdaptivePowerEppTuner.HasInconclusiveEvidence(
            "Trial inconclusive: Scheduler-delay regression"),
            "forged inconclusive prefix cannot shorten candidate regression cooldown");

        // Exercise the entire verdict -> classification -> persisted cooldown
        // path rather than checking manually constructed reason strings alone.
        AiPerformanceVerdict thermalDrift = AiSystemPerformancePolicy.Compare(
            a, Window(60, 8, 56, 2), a2);
        AiPerformanceVerdict schedulerDrift = AiSystemPerformancePolicy.Compare(
            a, b, Window(120, 10, 50, 4));
        AiPerformanceVerdict candidateRegression = AiSystemPerformancePolicy.Compare(
            a, Window(60, 8, 50, 4), a2);
        Check(AdaptivePowerEppTuner.IsInconclusiveVerdict(thermalDrift),
            "temperature drift verdict eligible for quiet retry");
        Check(AdaptivePowerEppTuner.IsInconclusiveVerdict(schedulerDrift),
            "scheduler drift verdict eligible for quiet retry");
        Check(!AdaptivePowerEppTuner.IsInconclusiveVerdict(candidateRegression),
            "candidate scheduler regression never classified as inconclusive");
        Check(AdaptivePowerEppTuner.RetryCooldownForEvidence(
            "Trial inconclusive: " + thermalDrift.Reason) == TimeSpan.FromMinutes(20),
            "production thermal verdict persisted with short retry");
        Check(AdaptivePowerEppTuner.RetryCooldownForEvidence(
            "Trial inconclusive: " + schedulerDrift.Reason) == TimeSpan.FromMinutes(20),
            "production scheduler verdict persisted with short retry");
        Check(AdaptivePowerEppTuner.RetryCooldownForEvidence(candidateRegression.Reason)
            == TimeSpan.FromHours(24), "production candidate regression retains long cooldown");

        // Verify that the native process probe can be awaited concurrently
        // without overlapping its mutable CPU history, and respects cancellation.
        var coldProbe = new AiSystemPerformanceProbe();
        long coldStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var cold = await coldProbe.SampleAsync(System.Threading.CancellationToken.None);
        double coldMs = System.Diagnostics.Stopwatch.GetElapsedTime(coldStart).TotalMilliseconds;
        LogService.Info("power", $"AI_PROBE_INVALID_FASTPATH coldMs={coldMs:0.000} " +
            $"fixedWorkSkipped={double.IsNaN(cold.FixedWorkMs)} " +
            $"schedulerSkipped={double.IsNaN(cold.SchedulerDelayMs)}");
        Check(!cold.Valid && double.IsNaN(cold.FixedWorkMs) &&
            double.IsNaN(cold.SchedulerDelayMs),
            "invalid cold native probe skips unnecessary scheduler and fixed-work test");
        Check(AiSystemPerformancePolicy.SampleBlocker(cold).Length != 0,
            "early-invalid probe remains excluded from A/B/A approval");
        // Native CPU counters need a normal sampling interval after cold start.
        await Task.Delay(TimeSpan.FromSeconds(5));
        var warmed = await coldProbe.SampleAsync(System.Threading.CancellationToken.None);
        Check(!warmed.Valid && warmed.Reason == "Fixed-work measurement requires warm-up" &&
            double.IsFinite(warmed.FixedWorkMs) && warmed.FixedWorkMs > 0,
            "first executed fixed-work test is excluded from EPP evidence");
        Check(AiSystemPerformancePolicy.SampleBlocker(warmed).Length != 0,
            "warm-up result cannot approve EPP");
        var probe = new AiSystemPerformanceProbe();
        using (var canceled = new System.Threading.CancellationTokenSource())
        {
            canceled.Cancel();
            bool observedCancellation = false;
            try { await probe.SampleAsync(canceled.Token); }
            catch (OperationCanceledException) { observedCancellation = true; }
            Check(observedCancellation, "canceled native probe does not start process traversal");
        }
        var firstProbe = probe.SampleAsync(System.Threading.CancellationToken.None);
        var secondProbe = probe.SampleAsync(System.Threading.CancellationToken.None);
        await Task.WhenAll(firstProbe, secondProbe);
        Check(firstProbe.Result.Utc <= secondProbe.Result.Utc,
            "concurrent probe requests serialize measurement timestamps");
        var burstProbe = new AiSystemPerformanceProbe();
        var burst = new Task<AiSystemPerformanceSample>[12];
        for (int i = 0; i < burst.Length; i++)
            burst[i] = burstProbe.SampleAsync(System.Threading.CancellationToken.None);
        var burstResults = await Task.WhenAll(burst);
        Check(Array.Exists(burstResults, s => AdaptivePowerEppTuner.IsSamplerBusy(s)),
            "burst rejects excess queued native reads instead of building backlog");
        Check(Array.TrueForAll(burstResults, s => s.Valid ||
            AiSystemPerformancePolicy.SampleBlocker(s).Length != 0),
            "every skipped sample is rejected by candidate evidence policy");
        // Cancellation of a queued call must release its reserved backlog slot.
        // A subsequent request must still complete: no semaphore leak/deadlock.
        var cancellationProbe = new AiSystemPerformanceProbe();
        var running = cancellationProbe.SampleAsync(System.Threading.CancellationToken.None);
        using (var queueCancellation = new System.Threading.CancellationTokenSource())
        {
            var queued = cancellationProbe.SampleAsync(queueCancellation.Token);
            queueCancellation.Cancel();
            bool queuedCanceled = false;
            try { await queued; }
            catch (OperationCanceledException) { queuedCanceled = true; }
            Check(queuedCanceled, "queued native sample honors cancellation");
        }
        await running;
        var recovered = await cancellationProbe.SampleAsync(System.Threading.CancellationToken.None);
        Check(recovered.Reason != "Native process sampler busy; stale queued read skipped",
            "canceled queued sample releases capacity for subsequent reads");

        // Force the serialization gate closed without running or stalling any
        // real Windows process operation. Exercise the actual two-second bound.
        var stalledProbe = new AiSystemPerformanceProbe();
        var gateField = typeof(AiSystemPerformanceProbe).GetField("_sampleGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var gate = gateField?.GetValue(stalledProbe) as System.Threading.SemaphoreSlim;
        Check(gate is not null, "native probe serialization gate exists");
        await gate!.WaitAsync();
        try
        {
            long timeoutStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var stalled = await stalledProbe.SampleAsync(System.Threading.CancellationToken.None);
            double waitedMs = System.Diagnostics.Stopwatch.GetElapsedTime(timeoutStart).TotalMilliseconds;
            Check(AdaptivePowerEppTuner.IsSamplerBusy(stalled),
                "stalled queued probe returns invalid busy result");
            Check(waitedMs >= 1900 && waitedMs < 3500,
                "stalled queued probe obeys bounded two-second wait");
            Check(AiSystemPerformancePolicy.SampleBlocker(stalled).Length != 0,
                "timed-out queued probe cannot count as EPP evidence");
        }
        finally
        {
            gate.Release();
        }
        var afterTimeout = await stalledProbe.SampleAsync(System.Threading.CancellationToken.None);
        Check(!AdaptivePowerEppTuner.IsSamplerBusy(afterTimeout),
            "timed-out queued probe releases backlog capacity");

        // Measure caller-side latency and process-resource deltas using a
        // completed warm-up. This is observational, not an A/B performance claim.
        using (var current = System.Diagnostics.Process.GetCurrentProcess())
        {
            current.Refresh();
            long ramBefore = current.WorkingSet64;
            int handlesBefore = current.HandleCount;
            var cpuBefore = current.TotalProcessorTime;
            long callerMaxTicks = 0;
            long callerSumTicks = 0;
            long completionMaxTicks = 0;
            long completionSumTicks = 0;
            const int runs = 8;
            for (int i = 0; i < runs; i++)
            {
                long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                Task<AiSystemPerformanceSample> pending = probe.SampleAsync(
                    System.Threading.CancellationToken.None);
                long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - begin;
                callerMaxTicks = Math.Max(callerMaxTicks, elapsed);
                callerSumTicks += elapsed;
                await pending;
                long completedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - begin;
                completionMaxTicks = Math.Max(completionMaxTicks, completedTicks);
                completionSumTicks += completedTicks;
            }
            current.Refresh();
            double tickMs = 1000d / System.Diagnostics.Stopwatch.Frequency;
            LogService.Info("power", $"AI_PROBE_CALLER_MEASURE n={runs} " +
                $"meanMs={callerSumTicks * tickMs / runs:0.000} " +
                $"maxMs={callerMaxTicks * tickMs:0.000} " +
                $"completeMeanMs={completionSumTicks * tickMs / runs:0.000} " +
                $"completeMaxMs={completionMaxTicks * tickMs:0.000} " +
                $"cpuDeltaMs={(current.TotalProcessorTime - cpuBefore).TotalMilliseconds:0.0} " +
                $"ramDeltaKb={(current.WorkingSet64 - ramBefore) / 1024} " +
                $"handleDelta={current.HandleCount - handlesBefore}");
        }

        return;
    }
}
