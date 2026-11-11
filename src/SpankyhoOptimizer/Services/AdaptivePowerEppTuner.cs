using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// Bounded A/B/A learning of BALANCED-mode EPP on the existing Spankyho plan ONLY.
// No new service, timer, shell command, power scheme or reboot-only registry tweak.
internal sealed class AdaptivePowerEppTuner
{
    internal const int ReferenceEpp = 20;
    internal const int CandidateEpp = 12;
    private const string LeaseOwner = "power-epp-aba";
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(24);
    private static readonly TimeSpan InterruptedCooldown = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan EarlyInterruptedCooldown =
        AiEppTrialOpportunityPolicy.EarlyInterruptedCooldown;
    private static readonly TimeSpan FailedCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan TrialLimit = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan SampleGap = TimeSpan.FromSeconds(5);
    private readonly AiSystemPerformanceProbe _probe = new();
    private readonly List<AiSystemPerformanceSample> _baseline = new();
    private readonly List<AiSystemPerformanceSample> _candidate = new();
    private readonly List<AiSystemPerformanceSample> _reference = new();
    private string _phase = "";
    private long _trialStartStamp;
    private long _lastSampleStamp;
    private int _invalidSamples;
    private int _phaseWindowRetries;
    private int _trialWindowRetries;
    private AiSystemPerformanceSample? _preflightFirst;
    private long _preflightFirstStamp;
    private long _preflightNextStamp;
    private string _plan = "";
    private string _currentPlan = "";
    private bool _loaded;
    private string _opportunityStatus = "EPP učení: zatím nevyhodnoceno";
    internal string OpportunityStatus => _opportunityStatus;
    private SavedState _saved = new("", 0, ReferenceEpp, DateTime.MinValue, "");
    private static string StorePath => Path.Combine(LogService.Root, "ai-verified-epp.json");

    // A measured EPP gain is tied to the CPU that produced the evidence.
    // Older five-field JSON loads with an empty identity but is not approved.
    private sealed record SavedState(
        string Plan, int WindowsBuild, int Epp, DateTime CheckedUtc,
        string Evidence, string CpuIdentity = "");

    private static string CurrentCpuIdentity =>
        PerformanceProfilesService.GetCpuName().Trim().ToUpperInvariant() + "|" +
        Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture);

    public async Task<int> TickAsync(
        bool enabled, bool balancedMode, bool gameActive, bool realIdle,
        double cpuPercent, double? temperatureC, string? plan,
        int activeEpp, CancellationToken token, bool stableBalancedMode = true)
    {
        LoadOnce();
        _currentPlan = plan ?? "";
        DateTime nowUtc = DateTime.UtcNow;
        bool planActive = !string.IsNullOrWhiteSpace(plan);
        bool cooldownActive = IsRetryCooldownActive(
            _saved.CheckedUtc, _saved.Evidence, nowUtc);
        _opportunityStatus = AiEppTrialOpportunityPolicy.Describe(
            enabled, balancedMode, gameActive, realIdle, cpuPercent, temperatureC,
            planActive, stableBalancedMode, cooldownActive, _phase.Length != 0);
        if (!enabled || !balancedMode || gameActive || realIdle ||
            !double.IsFinite(cpuPercent) || cpuPercent < 3 || cpuPercent > 35 || temperatureC is not double temperature ||
            !AiPowerTelemetryEvidencePolicy.HasUsableCpuTemperature(temperature) || temperature >= 70 || string.IsNullOrWhiteSpace(plan))
        {
            _preflightFirst = null;
            AbortTrial("workload/mode/temperature changed");
            return ReferenceEpp;
        }

        // A persisted gain is scoped to the same Spankyho scheme and Windows build.
        // Never auto-apply an unverified or user-supplied numeric value.
        bool approved = IsApproved(_saved, plan, Environment.OSVersion.Version.Build);
        if (_phase.Length == 0 && approved)
        {
            _preflightFirst = null;
            _opportunityStatus = "EPP učení: dříve ověřený kandidát EPP 12 je aktivní";
            return CandidateEpp;
        }
        // Interrupted by normal user activity is not a measured rejection.
        // Retry after a bounded 20-minute quiet opportunity rather than
        // being locked out for 24 hours by a single foreground switch.
        // Native failure still has a longer bounded retry; actual completed
        // A/B/A rejections and approvals keep the 24-hour policy.
        if (_phase.Length == 0 && cooldownActive)
        {
            _preflightFirst = null;
            return ReferenceEpp;
        }

        // Short Balanced entries are common while compiling/browsing. Avoid
        // acquiring the exclusive lease and touching EPP until the current
        // Balanced mode has really survived a 15-second observation window.
        if (_phase.Length == 0 && !stableBalancedMode)
        {
            _preflightFirst = null;
            return ReferenceEpp;
        }

        if (_phase.Length == 0)
        {
            if (activeEpp != ReferenceEpp)
                return ReferenceEpp;
            // Read-only pre-admission: a stable power-mode label alone does
            // not establish that the user's foreground work is comparable.
            // These two samples are explicitly excluded from A1/B/A2.
            if (!AiEppTrialOpportunityPolicy.IsPreflightRetryDue(
                    _preflightNextStamp, Stopwatch.GetTimestamp()))
            {
                _opportunityStatus = "EPP učení: čeká na opakované stabilní vzorky práce";
                return ReferenceEpp;
            }
            try
            {
                AiSystemPerformanceSample preflight =
                    await _probe.SampleAsync(token).ConfigureAwait(false);
                if (IsSamplerBusy(preflight))
                {
                    _preflightNextStamp = AiEppTrialOpportunityPolicy.PreflightNextDeadline(
                        Stopwatch.GetTimestamp(), Stopwatch.Frequency, SampleGap);
                    return ReferenceEpp;
                }
                string preflightBlocker = AiSystemPerformancePolicy.SampleBlocker(preflight);
                if (preflightBlocker.Length != 0)
                {
                    LogService.Info("power",
                        "EPP_ABA_PREFLIGHT_DEFER invalid read-only sample: " + preflightBlocker);
                    _preflightFirst = null;
                    TimeSpan retryDelay =
                        AiEppTrialOpportunityPolicy.FailedPreflightRetryDelay(preflight);
                    _preflightNextStamp = AiEppTrialOpportunityPolicy.PreflightNextDeadline(
                        Stopwatch.GetTimestamp(), Stopwatch.Frequency, retryDelay);
                    _opportunityStatus = retryDelay == AiEppTrialOpportunityPolicy.PreflightSampleGap
                        ? "EPP učení: obnovuje nativní CPU čítače před dalším měřením"
                        : "EPP učení: vzorky práce nejsou porovnatelné, odloženo";
                    return ReferenceEpp;
                }
                if (_preflightFirst is null)
                {
                    _preflightFirst = preflight;
                    _preflightFirstStamp = Stopwatch.GetTimestamp();
                    _preflightNextStamp = AiEppTrialOpportunityPolicy.PreflightNextDeadline(
                        _preflightFirstStamp, Stopwatch.Frequency,
                        AiEppTrialOpportunityPolicy.PreflightSampleGap);
                    _opportunityStatus = "EPP učení: ověřuje stabilitu práce před A/B/A";
                    return ReferenceEpp;
                }
                if (!AiEppTrialOpportunityPolicy.IsMonotonicPreflightGapValid(
                        _preflightFirstStamp, Stopwatch.GetTimestamp(), Stopwatch.Frequency) ||
                    !AiEppTrialOpportunityPolicy.ComparablePreflight(
                        _preflightFirst, preflight, monotonicGapVerified: true))
                {
                    LogService.Info("power",
                        "EPP_ABA_PREFLIGHT_DEFER foreground/workload not stable");
                    _preflightFirst = null;
                    _preflightNextStamp = AiEppTrialOpportunityPolicy.PreflightNextDeadline(
                        Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                        AiEppTrialOpportunityPolicy.UnstablePreflightCooldown);
                    _opportunityStatus = "EPP učení: změna práce nebo kolísání, odloženo";
                    return ReferenceEpp;
                }
                _preflightFirst = null;
                LogService.Info("power",
                    "EPP_ABA_PREFLIGHT_PASS 2 read-only comparable Balanced samples");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _preflightFirst = null;
                throw;
            }
            catch (Exception ex)
            {
                _preflightFirst = null;
                _preflightNextStamp = AiEppTrialOpportunityPolicy.PreflightNextDeadline(
                    Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                    AiEppTrialOpportunityPolicy.UnstablePreflightCooldown);
                _opportunityStatus = "EPP učení: pasivní kontrola selhala, odloženo";
                LogService.Error("power", "EPP preflight read-only sampling failed", ex);
                return ReferenceEpp;
            }

            if (!AiPerformanceExperimentCoordinator.TryAcquire(LeaseOwner))
                return ReferenceEpp;
            _phase = "A1";
            _plan = plan;
            _trialStartStamp = Stopwatch.GetTimestamp();
            _lastSampleStamp = 0;
            _invalidSamples = 0;
            _phaseWindowRetries = 0;
            _trialWindowRetries = 0;
        }
        long monotonicNow = Stopwatch.GetTimestamp();
        if (!string.Equals(plan, _plan, StringComparison.OrdinalIgnoreCase) ||
            IsTrialTimeout(_trialStartStamp, monotonicNow, Stopwatch.Frequency) ||
            !AiPerformanceExperimentCoordinator.TryAcquire(LeaseOwner))
        {
            AbortTrial("plan changed, trial timed out or lease lost");
            return ReferenceEpp;
        }

        int targetEpp = _phase == "B" ? CandidateEpp : ReferenceEpp;
        if (activeEpp != targetEpp) return targetEpp; // Apply first, sample next tick.
        if (!IsSampleDue(_lastSampleStamp, monotonicNow, Stopwatch.Frequency))
            return targetEpp;

        try
        {
            var sample = await _probe.SampleAsync(token).ConfigureAwait(false);
            _lastSampleStamp = Stopwatch.GetTimestamp();
            // Contention is not failed telemetry: retry later while preserving
            // the trial's original timeout and all evidence requirements.
            if (IsSamplerBusy(sample)) return targetEpp;
            if (AiSystemPerformancePolicy.SampleBlocker(sample).Length != 0)
            {
                if (++_invalidSamples >= 10)
                {
                    AbortTrial("samples not comparable");
                    return ReferenceEpp;
                }
                return targetEpp;
            }
            List<AiSystemPerformanceSample> window = _phase switch
            {
                "A1" => _baseline,
                "B" => _candidate,
                "A2" => _reference,
                _ => _baseline
            };
            window.Add(sample);
            if (window.Count < AiSystemPerformancePolicy.SamplesPerWindow) return targetEpp;

            // Repeat only predeclared statistical noise and sampling gaps,
            // within the original six-minute experiment lease. A changed
            // workload, missing/invalid CPU, or thermal risk aborts without
            // resampling a "winning" candidate selectively.
            string blocker = AiSystemPerformancePolicy.WindowBlocker(window);
            if (blocker.Length != 0)
            {
                if (AiEppWindowRetryPolicy.CanRetry(
                        blocker, _phaseWindowRetries, _trialWindowRetries))
                {
                    _phaseWindowRetries++;
                    _trialWindowRetries++;
                    window.Clear();
                    _invalidSamples = 0;
                    LogService.Info("power",
                        $"EPP_ABA_RESAMPLE phase={_phase} reason={blocker} " +
                        $"phaseAttempt={_phaseWindowRetries} total={_trialWindowRetries}");
                    return targetEpp;
                }
                AbortTrial("noisy or incomplete window: " + blocker);
                return ReferenceEpp;
            }
            _phaseWindowRetries = 0;
            if (_phase == "A1") { _phase = "B"; return CandidateEpp; }
            if (_phase == "B") { _phase = "A2"; return ReferenceEpp; }

            AiPerformanceVerdict verdict = AiSystemPerformancePolicy.Compare(_baseline, _candidate, _reference);
            if (verdict.Accepted && ThermalGuard(_baseline, _candidate, _reference))
            {
                // Never enable a newly learned EPP based on volatile memory
                // alone. A crash would erase its evidence and invalidate the
                // rollback/restart decision. Commit BEFORE publishing approval.
                var approvedEvidence = new SavedState(
                    plan, Environment.OSVersion.Version.Build, CandidateEpp,
                    DateTime.UtcNow, verdict.Reason, CurrentCpuIdentity);
                bool persisted = Store(approvedEvidence);
                if (!CanActivateCandidate(verdict.Accepted, true, persisted))
                {
                    _saved = new(plan, Environment.OSVersion.Version.Build,
                        ReferenceEpp, DateTime.UtcNow,
                        "Trial interrupted: evidence persistence failure");
                    LogService.Info("power",
                        "EPP_ABA_PERSIST_DEFER candidate rejected until evidence is durable");
                    EndTrial();
                    return ReferenceEpp;
                }
                _saved = approvedEvidence;
                LogService.Info("power", "EPP_ABA_ACCEPT " + verdict.Reason);
                EndTrial();
                return CandidateEpp;
            }
            if (IsInconclusiveVerdict(verdict))
            {
                // A different foreground/memory/reference workload does NOT
                // measure whether EPP 12 was faster. Restore reference EPP
                // and retry after a quiet opportunity, not 24h as a loss.
                _saved = new(plan, Environment.OSVersion.Version.Build,
                    ReferenceEpp, DateTime.UtcNow,
                    "Trial inconclusive: " + verdict.Reason);
                Store(_saved);
                LogService.Info("power", "EPP_ABA_INCONCLUSIVE " + verdict.Reason);
                EndTrial();
                return ReferenceEpp;
            }
            _saved = new(plan, Environment.OSVersion.Version.Build, ReferenceEpp, DateTime.UtcNow,
                verdict.Accepted ? "candidate thermal regression" : verdict.Reason);
            Store(_saved);
            LogService.Info("power", "EPP_ABA_REJECT " + _saved.Evidence);
            EndTrial();
            return ReferenceEpp;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            AbortTrial("canceled");
            return ReferenceEpp;
        }
        catch (Exception ex)
        {
            LogService.Error("power", "EPP trial failed (restoring original EPP)", ex);
            AbortTrial("failure");
            return ReferenceEpp;
        }
    }

    // A/B/A windows require actual elapsed time, never DateTime.UtcNow:
    // NTP and daylight/clock corrections must not expire a six-minute trial
    // prematurely or postpone its next five-second sample indefinitely.
    internal static bool IsTrialTimeout(long started, long now, long frequency) =>
        started <= 0 || frequency <= 0 || now < started ||
        (now - (double)started) / frequency > TrialLimit.TotalSeconds;

    internal static bool IsSampleDue(long previous, long now, long frequency) =>
        frequency > 0 && now >= 0 &&
        (previous <= 0 ||
         (now >= previous &&
          (now - (double)previous) / frequency >= SampleGap.TotalSeconds));

    internal static string RunMonotonicTimingSmoke()
    {
        const long freq = 10_000;
        const long start = 100_000;
        long sixMinutes = start + 6 * 60 * freq;
        long fiveSeconds = start + 5 * freq;
        if (IsTrialTimeout(start, sixMinutes, freq) ||
            !IsTrialTimeout(start, sixMinutes + 1, freq) ||
            !IsTrialTimeout(start, start - 1, freq) ||
            !IsTrialTimeout(0, sixMinutes, freq) ||
            !IsTrialTimeout(start, sixMinutes, 0) ||
            !IsSampleDue(0, start, freq) ||
            IsSampleDue(start, fiveSeconds - 1, freq) ||
            !IsSampleDue(start, fiveSeconds, freq) ||
            IsSampleDue(start, start - 1, freq) ||
            IsSampleDue(start, fiveSeconds, 0))
            throw new InvalidOperationException("EPP monotonic A/B/A timing policy regressed.");
        // A jump of the Windows wall clock has no influence over these ticks.
        DateTime utc = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        if ((utc.AddHours(2) - utc) <= TrialLimit ||
            IsTrialTimeout(start, start + freq * 3, freq))
            throw new InvalidOperationException("EPP wall clock jump affected trial lifetime.");
        return "AI_EPP_MONOTONIC_TIMING_SMOKE_OK duration6min=true sampleGap5s=true " +
               "wallClockJumpIgnored=true writes=0";
    }

    internal static bool IsSamplerBusy(AiSystemPerformanceSample sample) =>
        !sample.Valid && string.Equals(sample.Reason,
            "Native process sampler busy; stale queued read skipped",
            StringComparison.Ordinal);

    private static bool ThermalGuard(IReadOnlyList<AiSystemPerformanceSample> a,
        IReadOnlyList<AiSystemPerformanceSample> b, IReadOnlyList<AiSystemPerformanceSample> r)
    {
        double baseline = Math.Min(
            AiSystemPerformancePolicy.WindowMedian(a,
                AiSystemPerformancePolicy.WindowMetric.Temperature),
            AiSystemPerformancePolicy.WindowMedian(r,
                AiSystemPerformancePolicy.WindowMetric.Temperature));
        double candidate = AiSystemPerformancePolicy.WindowMedian(b,
            AiSystemPerformancePolicy.WindowMetric.Temperature);
        return candidate <= baseline + 3;
    }

    private void AbortTrial(string reason)
    {
        if (_phase.Length == 0) return;
        LogService.Info("power", "EPP_ABA_ABORT " + reason);
        // An abort BEFORE the first comparable reference window touched
        // candidate EPP=12 is a cheap preflight interruption, not evidence
        // of a failed A/B/A comparison. Retry later, but do not lock out for
        // the full 20-minute in-progress-experiment cooldown.
        bool early = _phase == "A1" &&
            _baseline.Count < AiSystemPerformancePolicy.SamplesPerWindow &&
            _candidate.Count == 0 && _reference.Count == 0 &&
            !reason.Contains("failure", StringComparison.OrdinalIgnoreCase);
        _saved = new(_plan, Environment.OSVersion.Version.Build, ReferenceEpp,
            DateTime.UtcNow,
            (early ? "Trial interrupted early: " : "Trial interrupted: ") + reason);
        Store(_saved);
        EndTrial();
    }

    private void EndTrial()
    {
        _phase = "";
        _trialStartStamp = 0;
        _lastSampleStamp = 0;
        _baseline.Clear();
        _candidate.Clear();
        _reference.Clear();
        _invalidSamples = 0;
        _phaseWindowRetries = 0;
        _trialWindowRetries = 0;
        _plan = "";
        AiPerformanceExperimentCoordinator.Release(LeaseOwner);
        // Prevent a restart loop after an inconclusive experiment.
        if (_saved.CheckedUtc == DateTime.MinValue)
        {
            _saved = new("", 0, ReferenceEpp, DateTime.UtcNow, "trial incomplete");
            Store(_saved);
        }
    }

    private void LoadOnce()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(StorePath))
                _saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StorePath)) ?? _saved;
        }
        catch (Exception ex) { LogService.Error("power", "EPP evidence load failed", ex); }
    }

    private static bool Store(SavedState state)
    {
        try
        {
            Directory.CreateDirectory(LogService.Root);
            AtomicFileService.WriteText(StorePath, JsonSerializer.Serialize(state));
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("power", "EPP evidence persistence failed", ex);
            return false;
        }
    }

    // Approval is forbidden if atomic evidence persistence failed, even when
    // measured performance and temperature passed all independent trial gates.
    internal static bool CanActivateCandidate(
        bool statisticallyAccepted, bool thermalGuardPassed, bool evidencePersisted) =>
        statisticallyAccepted && thermalGuardPassed && evidencePersisted;

    // Exact allowlist: only an A/B/A comparison with changed reference
    // conditions is inconclusive. Candidate regressions/no repeatable gain
    // remain real rejections with their existing 24-hour cooldown.
    internal static bool IsInconclusiveVerdict(AiPerformanceVerdict verdict) =>
        !verdict.Accepted && IsInconclusiveReason(verdict.Reason);

    internal static bool IsInconclusiveReason(string? reason) => reason is
            "Reference CPU/workload changed" or
            "Reference memory/workload changed" or
            "Workload changed; reduced activity is not a gain" or
            "Foreground workload changed; lower activity is not proof of productivity" or
            "Original-state reference did not recover baseline; inconclusive" or
            "Temperature changed; inconclusive" or
            "Temperature changed between A/B/A windows; inconclusive" or
            "Reference scheduler-delay changed; inconclusive";

    // A forged/invalid "Trial inconclusive:" prefix must not shorten the
    // rejection cooldown for an actual candidate regression.
    internal static bool HasInconclusiveEvidence(string? evidence)
    {
        if (IsInconclusiveReason(evidence)) return true; // legacy evidence
        const string prefix = "Trial inconclusive: ";
        return evidence is not null &&
            evidence.StartsWith(prefix, StringComparison.Ordinal) &&
            IsInconclusiveReason(evidence[prefix.Length..]);
    }

    internal static TimeSpan RetryCooldownForEvidence(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return Cooldown;
        // Old releases persisted unprefixed inconclusive comparisons as
        // full 24-hour losses. Migrate those exact reasons without altering
        // successful/actual rejected candidates.
        if (HasInconclusiveEvidence(evidence))
            return InterruptedCooldown;
        if (evidence.StartsWith("Trial interrupted early: ", StringComparison.Ordinal))
            return evidence.Contains("failure", StringComparison.OrdinalIgnoreCase)
                ? FailedCooldown : EarlyInterruptedCooldown;
        if (!evidence.StartsWith("Trial interrupted:", StringComparison.Ordinal))
            return Cooldown;
        return evidence.Contains("failure", StringComparison.OrdinalIgnoreCase)
            ? FailedCooldown : InterruptedCooldown;
    }

    public string StatusText => _phase.Length != 0
        ? "EPP A/B/A: " + _phase
        : IsApproved(_saved, _currentPlan, Environment.OSVersion.Version.Build)
            ? "EPP A/B/A: zlepšení ověřeno"
            : (_saved.Evidence.StartsWith("Trial interrupted:", StringComparison.Ordinal) ||
               _saved.Evidence.StartsWith("Trial interrupted early:", StringComparison.Ordinal))
                ? "EPP A/B/A: přerušeno, automatické opakování s prodlevou"
                : HasInconclusiveEvidence(_saved.Evidence)
                    ? "EPP A/B/A: neporovnatelné zatížení, opakování po zklidnění"
                    : "EPP: výchozí režim";

    // Keep persisted evidence from an older/broken build or hand-edited file
    // from approving EPP 12 simply because it contains the words "A/B/A".
    // This validates the actual fixed-work verdict format emitted by Compare.
    private const string VerifiedEvidencePrefix = "A/B/A fixed-work improvement ";
    private const string VerifiedEvidenceSuffix =
        "% with stable workload; memory/scheduler guards passed";

    internal static bool HasVerifiedImprovementEvidence(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence) ||
            !evidence.StartsWith(VerifiedEvidencePrefix, StringComparison.Ordinal) ||
            !evidence.EndsWith(VerifiedEvidenceSuffix, StringComparison.Ordinal))
            return false;
        ReadOnlySpan<char> numeric = evidence.AsSpan(
            VerifiedEvidencePrefix.Length,
            evidence.Length - VerifiedEvidencePrefix.Length - VerifiedEvidenceSuffix.Length);
        if (numeric.IsEmpty || !(
            double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            double.TryParse(numeric, NumberStyles.Float, CultureInfo.GetCultureInfo("cs-CZ"), out value)))
            return false;
        return double.IsFinite(value) && value >= 10 && value < 100;
    }

    // DateTime.MaxValue from corrupt saved JSON must not overflow Add(cooldown)
    // or hold the agent in a permanent future lockout. A new candidate still
    // needs all native workload/thermal A/B/A gates before being enabled.
    internal static bool IsPlausibleEvidenceTime(DateTime checkedUtc, DateTime nowUtc) =>
        checkedUtc != DateTime.MinValue && checkedUtc <= nowUtc.AddMinutes(5);

    internal static bool IsRetryCooldownActive(DateTime checkedUtc, string? evidence, DateTime nowUtc) =>
        IsPlausibleEvidenceTime(checkedUtc, nowUtc) &&
        nowUtc - checkedUtc < RetryCooldownForEvidence(evidence);

    private static bool IsApproved(SavedState state, string plan, int build) =>
        state.Epp == CandidateEpp && state.WindowsBuild == build &&
        IsPlausibleEvidenceTime(state.CheckedUtc, DateTime.UtcNow) &&
        string.Equals(state.Plan, plan, StringComparison.OrdinalIgnoreCase) &&
        // A power plan and Windows build can survive a motherboard/CPU swap.
        // Never reuse previously learned EPP across different CPU hardware.
        !string.IsNullOrWhiteSpace(state.CpuIdentity) &&
        string.Equals(state.CpuIdentity, CurrentCpuIdentity, StringComparison.Ordinal) &&
        HasVerifiedImprovementEvidence(state.Evidence);

    internal static string RunOfflineSmoke()
    {
        var good = new SavedState("spanky", 26100, CandidateEpp,
            DateTime.UtcNow, "A/B/A fixed-work improvement 12.0% with stable workload; memory/scheduler guards passed",
            CurrentCpuIdentity);
        if (!IsApproved(good, "spanky", 26100) ||
            IsApproved(good with { Epp = 0 }, "spanky", 26100) ||
            IsApproved(good, "other", 26100) ||
            IsApproved(good, "spanky", 26200) ||
            IsApproved(good with { CpuIdentity = "" }, "spanky", 26100) ||
            IsApproved(good with { CpuIdentity = "OTHER-CPU|8" }, "spanky", 26100) ||
            IsApproved(good with { Evidence = "manual" }, "spanky", 26100) ||
            IsApproved(good with { Evidence = "Trial interrupted: A/B/A fixed-work improvement 12%" }, "spanky", 26100) ||
            IsApproved(good with { CheckedUtc = DateTime.MaxValue }, "spanky", 26100))
            throw new InvalidOperationException("EPP AB/A persisted-evidence policy failed.");
        return "EPP_ABA_OFFLINE_SMOKE_OK accepted=verified-only different-plan=blocked different-build=blocked cpu-change=blocked legacy-unsigned=blocked";
    }
}
