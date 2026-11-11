using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Read-only evidence quality for a *reversible* per-process scheduling trial.
/// A smaller top-process CPU share is NOT proof of faster games, lower latency,
/// permanently freed RAM, or whole-PC CPU savings.
/// </summary>
internal static class AiAutomaticPriorityTrialPolicy
{
    internal const int RequiredCandidateSnapshots = 2;
    internal const int RequiredRestoredSnapshots = 2;
    internal const double MaximumSystemLoadDriftPp = 12;
    internal const double MaximumTopCpuSpreadPp = 3;
    internal static readonly TimeSpan EvidenceDeadline = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MeasurementMaximumAge = TimeSpan.FromSeconds(40);

    internal readonly record struct CpuWindow(
        int Count, double TotalTopCpuPercent, double MinimumTopCpuPercent,
        double MaximumTopCpuPercent, DateTime LastUpdatedUtc)
    {
        internal double Average => Count > 0 ? TotalTopCpuPercent / Count : double.NaN;
    }

    internal readonly record struct Observation(bool Comparable, string Reason);

    // A/B/A is meaningful only while the same class of foreground activity
    // remains active. An Interactive -> Game or Game -> Interactive transition
    // can keep the same foreground PID but completely change scheduler demand.
    // Unknown/Idle never authorizes continuing an experimental priority.
    internal static bool SameWorkload(
        AiWorkloadClass baseline, AiWorkloadClass current, bool gameActive) =>
        (baseline is AiWorkloadClass.Interactive or AiWorkloadClass.Game) &&
        baseline == current &&
        (baseline != AiWorkloadClass.Game || gameActive);

    // If restored A2 lost safe context, cancel it on the next agent tick.
    // No additional native probes or scheduling timers are needed.
    internal static bool AbortRestoredReferenceImmediately(
        bool enabled, bool fresh, AiWorkloadClass baseline,
        AiWorkloadClass current, bool gameActive) =>
        !enabled || !fresh || !SameWorkload(baseline, current, gameActive);

    // Optional disk counters are permitted to be unavailable, but once
    // reported they must be physically meaningful. NaN, negative readings
    // or >100% busy must never turn a "Normal" label into OS mutation consent.
    internal static bool InvalidDiskEvidence(AiSystemSmoothnessHealth smooth) =>
        (smooth.DiskBusyPercent is double busy &&
         (!double.IsFinite(busy) || busy < 0 || busy > 100)) ||
        (smooth.DiskLatencyMs is double latency &&
         (!double.IsFinite(latency) || latency < 0));

    // An optional logical-core sample must be physically meaningful once
    // reported. A genuine three-sample saturated core is still pressure,
    // even if the textual State came from an older/mismatched source.
    internal static bool InvalidLogicalCoreEvidence(AiSystemSmoothnessHealth smooth) =>
        smooth.LogicalCorePressureSamples < 0 ||
        smooth.LogicalCorePressureSamples > AiLogicalCorePressurePolicy.RequiredConsecutiveSamples ||
        (smooth.HottestLogicalProcessorPercent is double peak &&
         (!double.IsFinite(peak) || peak is < 0 or > 100));

    // A background A/B/A experiment cannot start during RAM, CPU, or disk
    // pressure. Use the same guard for OPTIONAL WM_NULL baseline admission:
    // probes should not compete with a game when the experiment is forbidden.
    // In-flight B/A2 restoration still follows the existing owned path.
    internal static bool CanStartProbeBaseline(
        AiSystemSmoothnessHealth? smooth, DateTime nowUtc) =>
        smooth is { State: "Normal", GameActive: false } &&
        !DangerousAfterApplying(smooth, nowUtc);

    internal static string NewTrialPressureReason(
        AiSystemSmoothnessHealth? smooth, DateTime nowUtc)
    {
        if (smooth is null ||
            !AiSystemSmoothnessGuardService.IsFresh(
                smooth.UpdatedUtc, nowUtc, TimeSpan.FromSeconds(90)) ||
            smooth.State is "Unknown")
            return "chybi bezpecne aktualni mereni systemove zateze";
        if (!double.IsFinite(smooth.FreeRamGiB) ||
            !double.IsFinite(smooth.UsedRamPercent) ||
            smooth.FreeRamGiB < 1.5 ||
            (smooth.UsedRamPercent >= 92 && smooth.FreeRamGiB < 4))
            return "nedostatek volne RAM";
        if (!double.IsFinite(smooth.CpuLoadPercent) ||
            smooth.CpuLoadPercent is < 0 or >= 75)
            return "vysoka zatez CPU";
        if (smooth.State == "Single-core pressure")
            return "dlouhodobe vytizene logicke jadro CPU";
        if (InvalidDiskEvidence(smooth) || InvalidLogicalCoreEvidence(smooth))
            return "chybi bezpecne aktualni mereni systemove zateze";
        if (AiLogicalCorePressurePolicy.IsSustained(smooth.LogicalCorePressureSamples))
            return "dlouhodobe vytizene logicke jadro CPU";
        if (smooth.DiskBusyPercent is >= 95 and <= 100 &&
            smooth.DiskLatencyMs is >= 50)
            return "zatez uloziste";
        return "mereni zateze neni srovnatelne";
    }

    // Independent of the ordinary 25-second B sampling cadence.
    // A priority intervention must be rechecked on the next existing
    // FanAgent tick whenever resource pressure or ownership is unsafe.
    internal static bool UrgentOwnedRollback(
        bool ownsPriority, AiSystemSmoothnessHealth? smooth, DateTime nowUtc) =>
        ownsPriority && DangerousAfterApplying(smooth, nowUtc);

    internal static bool DangerousAfterApplying(
        AiSystemSmoothnessHealth? smooth, DateTime nowUtc)
    {
        // A game is an explicitly protected activity; "Gaming" is not by itself
        // degradation, but any unknown/CPU/RAM/disk pressure IS. Missing data
        // is never safe as a basis for retaining an unverified OS intervention.
        return smooth is null ||
            !AiSystemSmoothnessGuardService.IsFresh(
                smooth.UpdatedUtc, nowUtc, TimeSpan.FromSeconds(90)) ||
            smooth.State is not ("Normal" or "Gaming") ||
            // Preserve defense in depth even if an older or external agent
            // still reports "Gaming" while raw RAM/CPU is under pressure.
            !double.IsFinite(smooth.CpuLoadPercent) ||
            smooth.CpuLoadPercent is < 0 or >= 75 ||
            !double.IsFinite(smooth.UsedRamPercent) ||
            smooth.UsedRamPercent is < 0 or > 100 ||
            !double.IsFinite(smooth.FreeRamGiB) ||
            smooth.FreeRamGiB < 1.5 ||
            (smooth.UsedRamPercent >= 92 && smooth.FreeRamGiB < 4) ||
            InvalidDiskEvidence(smooth) ||
            InvalidLogicalCoreEvidence(smooth) ||
            AiLogicalCorePressurePolicy.IsSustained(smooth.LogicalCorePressureSamples) ||
            (smooth.DiskBusyPercent is >= 95 and <= 100 &&
             smooth.DiskLatencyMs is >= 50);
    }

    internal static bool TryGetTargetCpu(
        AdaptivePowerRuntimeStatus power, string target, out double cpuPercent)
    {
        int instances =
            string.Equals(target, "OneDrive", StringComparison.OrdinalIgnoreCase)
                ? power.OneDriveInstanceCount
                : string.Equals(target, "Dropbox", StringComparison.OrdinalIgnoreCase)
                    ? power.DropboxInstanceCount
                    : string.Equals(target, "EpicGamesLauncher", StringComparison.OrdinalIgnoreCase)
                        ? power.EpicGamesLauncherInstanceCount
                    : string.Equals(target, "qbittorrent", StringComparison.OrdinalIgnoreCase)
                        ? power.QBittorrentInstanceCount
                    : string.Equals(target, "SearchIndexer", StringComparison.OrdinalIgnoreCase)
                        ? power.SearchIndexerInstanceCount
                        : string.Equals(target, "SearchProtocolHost", StringComparison.OrdinalIgnoreCase)
                            ? power.SearchProtocolHostInstanceCount
                            : string.Equals(target, "SearchFilterHost", StringComparison.OrdinalIgnoreCase)
                                ? power.SearchFilterHostInstanceCount
                                : 0;
        // 0 / >1 exact-name processes can mix unrelated CPU deltas. For a
        // fresh 1073 sampler we must observe exactly the one owned process.
        // -1 means an old optional JSON field is missing; no same-run native
        // scheduler mutation is allowed from an old status alone.
        if (instances != 1)
        {
            cpuPercent = double.NaN;
            return false;
        }
        double? observed =
            string.Equals(target, "OneDrive", StringComparison.OrdinalIgnoreCase)
                ? power.OneDriveCpuPercent
                : string.Equals(target, "Dropbox", StringComparison.OrdinalIgnoreCase)
                    ? power.DropboxCpuPercent
                    : string.Equals(target, "EpicGamesLauncher", StringComparison.OrdinalIgnoreCase)
                        ? power.EpicGamesLauncherCpuPercent
                    : string.Equals(target, "qbittorrent", StringComparison.OrdinalIgnoreCase)
                        ? power.QBittorrentCpuPercent
                    : string.Equals(target, "SearchIndexer", StringComparison.OrdinalIgnoreCase)
                        ? power.SearchIndexerCpuPercent
                        : string.Equals(target, "SearchProtocolHost", StringComparison.OrdinalIgnoreCase)
                            ? power.SearchProtocolHostCpuPercent
                            : string.Equals(target, "SearchFilterHost", StringComparison.OrdinalIgnoreCase)
                                ? power.SearchFilterHostCpuPercent
                                : null;
        // Backward-compatible fallback for pre-1073 runtime samples. A new
        // sample also tracks the target even when it is no longer #1 by CPU.
        if (!observed.HasValue &&
            string.Equals(power.TopCpuProcess, target, StringComparison.OrdinalIgnoreCase))
            observed = power.TopCpuProcessPercent;
        cpuPercent = observed.GetValueOrDefault(double.NaN);
        return observed.HasValue && double.IsFinite(cpuPercent) &&
            cpuPercent is >= 0 and <= 100;
    }

    // Reject a changed system-load context on the next existing tick, without
    // waiting for the 25s evidence cadence or starting a native probe.
    internal static bool SystemLoadDrifted(
        AdaptivePowerRuntimeStatus? power, double baselineLoadPercent) =>
        power is null || !double.IsFinite(baselineLoadPercent) ||
        !double.IsFinite(power.LoadPercent) ||
        power.LoadPercent is < 0 or > 100 ||
        LoadDriftExceeded(power.LoadPercent, baselineLoadPercent);

    // A/B/A validity is proven by the restored A2 target CPU matching A within
    // MaximumTopCpuSpreadPp; total load only has to stay in the same regime.
    // Busy desktops (browsers) swing more in absolute pp, so scale the window
    // with baseline load. Safety rollback stays in DangerousAfterApplying.
    internal static double LoadDriftTolerancePp(double baselineLoadPercent) =>
        Math.Max(MaximumSystemLoadDriftPp, baselineLoadPercent * 0.5);

    internal static bool LoadDriftExceeded(double currentLoadPercent, double baselineLoadPercent) =>
        Math.Abs(currentLoadPercent - baselineLoadPercent) >
            LoadDriftTolerancePp(baselineLoadPercent);

    internal static Observation Comparable(
        AdaptivePowerRuntimeStatus? power, DateTime nowUtc,
        string target, string foreground,
        double baselineLoadPercent, DateTime phaseStartUtc,
        int expectedTargetPid, int expectedForegroundPid)
    {
        if (!AdaptivePowerRuntimeService.IsStatusFresh(power, nowUtc) ||
            power is null || power.UpdatedUtc <= phaseStartUtc ||
            CpuObservedUtc(power) <= phaseStartUtc ||
            CpuObservedUtc(power) > nowUtc ||
            nowUtc - CpuObservedUtc(power) > TimeSpan.FromSeconds(15))
            return new(false, "missing, stale or pre-phase load sample");
        if (!string.Equals(power.ForegroundProcess, foreground, StringComparison.OrdinalIgnoreCase) ||
            !TryGetTargetCpu(power, target, out _))
            return new(false, "foreground changed or target no longer measured");
        if (!AiPriorityProcessIdentityPolicy.Matches(
            power, target, expectedTargetPid, expectedForegroundPid))
            return new(false, "target PID or active window PID changed");
        if (!double.IsFinite(baselineLoadPercent) ||
            !double.IsFinite(power.LoadPercent) ||
            power.LoadPercent is < 0 or > 100 ||
            LoadDriftExceeded(power.LoadPercent, baselineLoadPercent))
            return new(false, "system load no longer comparable");
        return new(true, "same tasks with comparable system load");
    }

    // The Agent can publish a new STATUS each tick while reusing the same
    // native CPU snapshot. Never count cached values as distinct A/B/A evidence.
    private static DateTime CpuObservedUtc(AdaptivePowerRuntimeStatus power) =>
        power.ProcessCpuObservedUtc == default ? power.UpdatedUtc : power.ProcessCpuObservedUtc;

    // Before any native priority mutation require CPU evidence from a real
    // recent process scan and the exact foreground HWND from that scan.
    // Older agents that don't supply these fields fail closed.
    internal static bool MayStartNativePriorityMutation(
        AdaptivePowerRuntimeStatus? power, DateTime nowUtc,
        int liveForegroundPid, long liveForegroundHwnd) =>
        power != null && power.ProcessCpuObservedUtc != default &&
        power.ForegroundWindowHandle != 0 && liveForegroundHwnd != 0 &&
        power.ForegroundWindowHandle == liveForegroundHwnd &&
        liveForegroundPid > 0 && power.ForegroundProcessId == liveForegroundPid &&
        power.ProcessCpuObservedUtc <= power.UpdatedUtc &&
        AiSystemSmoothnessGuardService.IsFresh(
            power.ProcessCpuObservedUtc, nowUtc, TimeSpan.FromSeconds(10)) &&
        AdaptivePowerRuntimeService.IsStatusFresh(power, nowUtc);

    internal static CpuWindow AddDistinct(
        CpuWindow window, AdaptivePowerRuntimeStatus power, string target)
    {
        DateTime observedUtc = CpuObservedUtc(power);
        if (observedUtc <= window.LastUpdatedUtc || observedUtc > power.UpdatedUtc ||
            !TryGetTargetCpu(power, target, out double value))
            return window;
        return new(
            window.Count + 1,
            window.TotalTopCpuPercent + value,
            window.Count == 0 ? value : Math.Min(window.MinimumTopCpuPercent, value),
            window.Count == 0 ? value : Math.Max(window.MaximumTopCpuPercent, value),
            observedUtc);
    }

    // Finish the reversible B phase as soon as it holds comparable evidence.
    // Never retain an unverified BelowNormal intervention for three minutes
    // solely to wait for the maximum safety hold. Restore first, then gather A.
    // Reuses the current FanAgent CPU/WM_NULL samples; no new native polling.
    internal static readonly TimeSpan MinimumCandidateEvidenceDuration =
        TimeSpan.FromSeconds(75);

    internal static bool ReadyForRestoredReference(
        CpuWindow candidate, AiForegroundWindowResponsePolicy.Window baselineResponse,
        AiForegroundWindowResponsePolicy.Window candidateResponse,
        DateTime appliedUtc, DateTime nowUtc)
    {
        if (appliedUtc == default || nowUtc < appliedUtc ||
            nowUtc - appliedUtc < MinimumCandidateEvidenceDuration ||
            candidate.Count < RequiredCandidateSnapshots ||
            !ValidCpuEvidence(candidate) ||
            candidate.MaximumTopCpuPercent - candidate.MinimumTopCpuPercent >
                MaximumTopCpuSpreadPp ||
            candidate.LastUpdatedUtc <= appliedUtc ||
            candidate.LastUpdatedUtc > nowUtc ||
            nowUtc - candidate.LastUpdatedUtc > MeasurementMaximumAge)
            return false;

        // Games do not receive WM_NULL probes. An interactive window that
        // supplied an A-phase baseline must also supply stable fresh B samples
        // from exactly the same HWND. Missing evidence never permits early exit.
        if (baselineResponse.Count < AiForegroundWindowResponsePolicy.RequiredSamples)
            return true;
        return AiForegroundWindowResponsePolicy.StableWindow(baselineResponse) &&
            AiForegroundWindowResponsePolicy.StableWindow(candidateResponse) &&
            baselineResponse.WindowHandle == candidateResponse.WindowHandle &&
            candidateResponse.LastUpdatedUtc > appliedUtc &&
            candidateResponse.LastUpdatedUtc <= nowUtc &&
            nowUtc - candidateResponse.LastUpdatedUtc <=
                AiForegroundWindowResponsePolicy.MaximumAge &&
            !AiForegroundWindowResponsePolicy.SignificantRegression(
                baselineResponse, candidateResponse);
    }

    // Never allow NaN, infinity, negative, out-of-range or inverted
    // evidence to manufacture an A/B/A CPU-share observation.
    internal static bool ValidCpuEvidence(CpuWindow window) =>
        window.Count > 0 &&
        double.IsFinite(window.TotalTopCpuPercent) &&
        double.IsFinite(window.MinimumTopCpuPercent) &&
        double.IsFinite(window.MaximumTopCpuPercent) &&
        double.IsFinite(window.Average) &&
        window.MinimumTopCpuPercent >= 0 &&
        window.MaximumTopCpuPercent <= 100 &&
        window.MinimumTopCpuPercent <= window.MaximumTopCpuPercent &&
        window.Average >= window.MinimumTopCpuPercent &&
        window.Average <= window.MaximumTopCpuPercent &&
        window.TotalTopCpuPercent >= window.Count * window.MinimumTopCpuPercent - 0.000001 &&
        window.TotalTopCpuPercent <= window.Count * window.MaximumTopCpuPercent + 0.000001;

    internal static string Verdict(
        double baselineTopCpuPercent, CpuWindow candidate, CpuWindow restored)
    {
        if (candidate.Count < RequiredCandidateSnapshots ||
            restored.Count < RequiredRestoredSnapshots ||
            !double.IsFinite(baselineTopCpuPercent) ||
            baselineTopCpuPercent is < 0 or > 100 ||
            baselineTopCpuPercent < AiAutomaticProcessPriorityPolicy.MinimumBackgroundCpuPercent ||
            !ValidCpuEvidence(candidate) || !ValidCpuEvidence(restored) ||
            candidate.MaximumTopCpuPercent - candidate.MinimumTopCpuPercent > MaximumTopCpuSpreadPp ||
            restored.MaximumTopCpuPercent - restored.MinimumTopCpuPercent > MaximumTopCpuSpreadPp ||
            Math.Abs(restored.Average - baselineTopCpuPercent) > MaximumTopCpuSpreadPp)
            return "neprůkazné (kolísání nebo neobnovená reference)";
        // Observation only: priority changes scheduler eligibility, not CPU
        // work demand. Never assert a demonstrated system speedup.
        // Require a real effect that is larger than sampling noise and whose
        // candidate/restored ranges do not overlap. Two flat samples with a
        // tiny mean difference are not evidence of a scheduling effect.
        double minimumEffectPp = Math.Max(0.5, baselineTopCpuPercent * 0.2);
        bool rangesOverlap =
            candidate.MinimumTopCpuPercent <= restored.MaximumTopCpuPercent &&
            restored.MinimumTopCpuPercent <= candidate.MaximumTopCpuPercent;
        double delta = Math.Round(restored.Average - candidate.Average, 2);
        return Math.Abs(delta) >= minimumEffectPp && !rangesOverlap
            ? $"pozorování změny podílu CPU procesu {delta:+0.##;-0.##;0} pb (nikoli prokázané zrychlení PC)"
            : "bez průkazné změny podílu CPU procesu";
    }

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var smooth = new AiSystemSmoothnessHealth(now.AddSeconds(-12), 5, 40, 8,
            "Normal", "", false);
        // A real observed 96.4%-RAM/0.57GiB-free Gaming snapshot is NOT
        // headroom for another experiment. The Game state alone must never
        // hide pressure; B/A2 restoration remains a separate safety path.
        var memoryPressure = smooth with {
            State = "Gaming", GameActive = true,
            UsedRamPercent = 96.4, FreeRamGiB = 0.57
        };
        var cpuPressure = smooth with { CpuLoadPercent = 78 };
        var diskPressure = smooth with {
            DiskBusyPercent = 98, DiskLatencyMs = 65
        };
        if (UrgentOwnedRollback(false, memoryPressure, now) ||
            UrgentOwnedRollback(true, smooth, now) ||
            !UrgentOwnedRollback(true, memoryPressure, now) ||
            !UrgentOwnedRollback(true, cpuPressure, now) ||
            !UrgentOwnedRollback(true, diskPressure, now) ||
            !UrgentOwnedRollback(true, memoryPressure with { State = "Gaming" }, now) ||
            !UrgentOwnedRollback(true, cpuPressure with { State = "Gaming" }, now) ||
            !UrgentOwnedRollback(true, null, now) ||
            !UrgentOwnedRollback(true, smooth with {
                UpdatedUtc = now.AddMinutes(-3)
            }, now) ||
            !CanStartProbeBaseline(smooth, now) ||
            CanStartProbeBaseline(memoryPressure, now) ||
            CanStartProbeBaseline(cpuPressure, now) ||
            CanStartProbeBaseline(diskPressure, now) ||
            CanStartProbeBaseline(null, now) ||
            CanStartProbeBaseline(smooth with { UpdatedUtc = now.AddMinutes(-4) }, now) ||
            NewTrialPressureReason(memoryPressure, now) != "nedostatek volne RAM" ||
            NewTrialPressureReason(memoryPressure with { State = "Memory pressure" }, now) != "nedostatek volne RAM" ||
            NewTrialPressureReason(cpuPressure, now) != "vysoka zatez CPU" ||
            NewTrialPressureReason(cpuPressure with { State = "CPU pressure" }, now) != "vysoka zatez CPU" ||
            NewTrialPressureReason(diskPressure, now) != "zatez uloziste" ||
            NewTrialPressureReason(diskPressure with { State = "Disk latency" }, now) != "zatez uloziste" ||
            NewTrialPressureReason(smooth with { State = "Unknown" }, now) != "chybi bezpecne aktualni mereni systemove zateze" ||
            NewTrialPressureReason(smooth with { UpdatedUtc = now.AddMinutes(-3) }, now) != "chybi bezpecne aktualni mereni systemove zateze")
            throw new InvalidOperationException(
                "Pressure-blocked AI priority trial still admitted baseline WM_NULL.");
        var power = new AdaptivePowerRuntimeStatus(now.AddSeconds(-10), "Observation",
            12, "", null, null, null, false, false, 20, 2, "Interactive", "",
            "OneDrive", 4.5, "chrome", 4.5, null, 1, 0,
            null, null, null, -1, -1, -1,
            501, 432, 0, 0, 0, 0);
        if (SystemLoadDrifted(power, 12) ||
            SystemLoadDrifted(power with { LoadPercent = 24 }, 12) ||
            !SystemLoadDrifted(power with { LoadPercent = 24.01 }, 12) ||
            !SystemLoadDrifted(power with { LoadPercent = double.NaN }, 12) ||
            !SystemLoadDrifted(power with { LoadPercent = -1 }, 12) ||
            !SystemLoadDrifted(power, double.PositiveInfinity) ||
            !SystemLoadDrifted(null, 12) ||
            // Busy baseline: 40% tolerates 20pp, but not more.
            SystemLoadDrifted(power with { LoadPercent = 60 }, 40) ||
            !SystemLoadDrifted(power with { LoadPercent = 60.01 }, 40))
            throw new InvalidOperationException("AI A/B/A load-drift guard accepted invalid CPU evidence.");
        // Same foreground PID can host a different activity; only equal
        // trusted workload classifications may form a single A/B/A comparison.
        if (!SameWorkload(AiWorkloadClass.Interactive,
                AiWorkloadClass.Interactive, false) ||
            !SameWorkload(AiWorkloadClass.Game, AiWorkloadClass.Game, true) ||
            SameWorkload(AiWorkloadClass.Interactive, AiWorkloadClass.Game, true) ||
            SameWorkload(AiWorkloadClass.Game, AiWorkloadClass.Interactive, false) ||
            SameWorkload(AiWorkloadClass.Game, AiWorkloadClass.Game, false) ||
            SameWorkload(AiWorkloadClass.Unknown, AiWorkloadClass.Unknown, false) ||
            SameWorkload(AiWorkloadClass.Idle, AiWorkloadClass.Idle, false) ||
            SameWorkload(AiWorkloadClass.Interactive, AiWorkloadClass.Unknown, false))
            throw new InvalidOperationException(
                "Priority A/B/A accepted changed or untrusted activity as comparable.");

        if (AbortRestoredReferenceImmediately(true, true,
                AiWorkloadClass.Interactive, AiWorkloadClass.Interactive, false) ||
            !AbortRestoredReferenceImmediately(false, true,
                AiWorkloadClass.Interactive, AiWorkloadClass.Interactive, false) ||
            !AbortRestoredReferenceImmediately(true, false,
                AiWorkloadClass.Interactive, AiWorkloadClass.Interactive, false) ||
            !AbortRestoredReferenceImmediately(true, true,
                AiWorkloadClass.Interactive, AiWorkloadClass.Game, true) ||
            !AbortRestoredReferenceImmediately(true, true,
                AiWorkloadClass.Game, AiWorkloadClass.Game, false) ||
            AbortRestoredReferenceImmediately(true, true,
                AiWorkloadClass.Game, AiWorkloadClass.Game, true))
            throw new InvalidOperationException("Restored reference cancellation gate failed.");

        // Pure phase-duration regression: A/B/A must never trade missing,
        // noisy or old B evidence for a faster OS scheduler intervention.
        DateTime applied = now.AddSeconds(-90);
        var stableB = new CpuWindow(2, 4.2, 2.0, 2.2, now.AddSeconds(-10));
        var baselineWindow = new AiForegroundWindowResponsePolicy.Window(
            2, 5, 2, 3, now.AddSeconds(-95), 123);
        var candidateWindow = new AiForegroundWindowResponsePolicy.Window(
            2, 6, 2.5, 3.5, now.AddSeconds(-10), 123);
        if (!ReadyForRestoredReference(stableB, default, default, applied, now) ||
            !ReadyForRestoredReference(stableB, baselineWindow,
                candidateWindow, applied, now) ||
            ReadyForRestoredReference(stableB, default, default,
                now.AddSeconds(-74), now) ||
            ReadyForRestoredReference(stableB with { Count = 1 },
                default, default, applied, now) ||
            ReadyForRestoredReference(stableB with {
                TotalTopCpuPercent = double.NaN
            }, default, default, applied, now) ||
            ReadyForRestoredReference(stableB with {
                TotalTopCpuPercent = -4.0
            }, default, default, applied, now) ||
            ReadyForRestoredReference(stableB with {
                MinimumTopCpuPercent = 3.0, MaximumTopCpuPercent = 2.0
            }, default, default, applied, now) ||
            ReadyForRestoredReference(stableB with {
                MinimumTopCpuPercent = 0, MaximumTopCpuPercent = 10
            }, default, default, applied, now) ||
            ReadyForRestoredReference(stableB with {
                LastUpdatedUtc = now.AddSeconds(-41)
            }, default, default, applied, now) ||
            ReadyForRestoredReference(stableB with {
                LastUpdatedUtc = now.AddSeconds(1)
            }, default, default, applied, now) ||
            ReadyForRestoredReference(stableB, baselineWindow,
                candidateWindow with { WindowHandle = 456 }, applied, now) ||
            ReadyForRestoredReference(stableB, baselineWindow,
                candidateWindow with { Count = 1 }, applied, now) ||
            ReadyForRestoredReference(stableB, baselineWindow,
                candidateWindow with { LastUpdatedUtc = now.AddSeconds(-70) },
                applied, now) ||
            ReadyForRestoredReference(stableB, baselineWindow,
                candidateWindow with { TotalMs = 100, MaximumMs = 50 },
                applied, now) ||
            ReadyForRestoredReference(stableB, baselineWindow,
                candidateWindow, now.AddSeconds(1), now))
            throw new InvalidOperationException(
                "Priority B phase exited before comparable fresh CPU and window evidence.");
        CpuWindow empty = default;
        CpuWindow one = AddDistinct(empty, power, "OneDrive");
        CpuWindow duplicated = AddDistinct(one, power, "OneDrive");
        CpuWindow two = AddDistinct(one, power with {
            UpdatedUtc = now.AddSeconds(-5), TopCpuProcessPercent = 4.2,
            OneDriveCpuPercent = 4.2
        }, "OneDrive");
        CpuWindow restored = AddDistinct(
            AddDistinct(empty, power with {
                TopCpuProcessPercent = 4.1, OneDriveCpuPercent = 4.1
            }, "OneDrive"), power with {
                UpdatedUtc = now.AddSeconds(-5), TopCpuProcessPercent = 4.0,
                OneDriveCpuPercent = 4.0
            }, "OneDrive");
        // Once scheduling alters the ranking, a named target's CPU reading
        // must remain available without an additional process enumeration.
        var displaced = power with {
            TopCpuProcess = "game", TopCpuProcessPercent = 9.2,
            OneDriveCpuPercent = 2.4, OneDriveInstanceCount = 1
        };
        CpuWindow displacedWindow = AddDistinct(empty, displaced, "OneDrive");
        var windowsSearch = displaced with {
            SearchIndexerCpuPercent = 3.2, SearchIndexerInstanceCount = 1,
            SearchProtocolHostCpuPercent = 2.3, SearchProtocolHostInstanceCount = 1,
            SearchFilterHostCpuPercent = 2.1, SearchFilterHostInstanceCount = 1,
            SearchIndexerProcessId = 1001, SearchProtocolHostProcessId = 1002,
            SearchFilterHostProcessId = 1003
        };
        CpuWindow windowsWindow = AddDistinct(empty, windowsSearch, "SearchIndexer");
        if (windowsWindow.Count != 1 || windowsWindow.Average != 3.2 ||
            !TryGetTargetCpu(windowsSearch, "SearchProtocolHost", out double protocol) ||
            protocol != 2.3 ||
            !TryGetTargetCpu(windowsSearch, "SearchFilterHost", out double filter) ||
            filter != 2.1 ||
            TryGetTargetCpu(windowsSearch with { SearchIndexerInstanceCount = 2 },
                "SearchIndexer", out _) ||
            TryGetTargetCpu(windowsSearch with { SearchIndexerInstanceCount = -1 },
                "SearchIndexer", out _) ||
            TryGetTargetCpu(windowsSearch with { SearchProtocolHostInstanceCount = 0 },
                "SearchProtocolHost", out _) ||
            TryGetTargetCpu(windowsSearch with { SearchFilterHostInstanceCount = 3 },
                "SearchFilterHost", out _) ||
            !Comparable(windowsSearch, now, "SearchIndexer", "chrome", 11,
                now.AddSeconds(-30), 1001, 501).Comparable ||
            Comparable(windowsSearch with { SearchIndexerProcessId = 2001 },
                now, "SearchIndexer", "chrome", 11, now.AddSeconds(-30),
                1001, 501).Comparable ||
            Comparable(windowsSearch with { ForegroundProcessId = 502 },
                now, "SearchIndexer", "chrome", 11, now.AddSeconds(-30),
                1001, 501).Comparable ||
            TryGetTargetCpu(displaced with { OneDriveInstanceCount = 2 },
                "OneDrive", out _) ||
            TryGetTargetCpu(displaced with { OneDriveInstanceCount = 0 },
                "OneDrive", out _) ||
            TryGetTargetCpu(displaced with { OneDriveInstanceCount = -1 },
                "OneDrive", out _) ||
            DangerousAfterApplying(smooth, now) ||
            DangerousAfterApplying(smooth with { State = "Gaming" }, now) ||
            !DangerousAfterApplying(smooth with { State = "Gaming", FreeRamGiB = 0.8 }, now) ||
            !DangerousAfterApplying(smooth with { State = "Gaming", CpuLoadPercent = 85 }, now) ||
            !DangerousAfterApplying(smooth with { State = "Gaming",
                DiskBusyPercent = 97, DiskLatencyMs = 90 }, now) ||
            !DangerousAfterApplying(smooth with { State = "Disk latency" }, now) ||
            !DangerousAfterApplying(smooth with { State = "Memory pressure" }, now) ||
            !DangerousAfterApplying(smooth with {
                UpdatedUtc = now.AddMinutes(-2)
            }, now) ||
            !DangerousAfterApplying(null, now) ||
            !Comparable(power, now, "onedrive", "Chrome", 11,
                now.AddSeconds(-30), 432, 501).Comparable ||
            Comparable(power, now, "onedrive", "chrome", 30,
                now.AddSeconds(-30), 432, 501).Comparable ||
            Comparable(power, now, "onedrive", "firefox", 11,
                now.AddSeconds(-30), 432, 501).Comparable ||
            Comparable(power, now, "onedrive", "chrome", 11,
                now.AddSeconds(-3), 432, 501).Comparable ||
            duplicated.Count != 1 || two.Count != 2 ||
            displacedWindow.Count != 1 || displacedWindow.Average != 2.4 ||
            !Comparable(displaced, now, "OneDrive", "chrome", 11,
                now.AddSeconds(-30), 432, 501).Comparable ||
            Comparable(displaced with { OneDriveCpuPercent = null }, now,
                "OneDrive", "chrome", 11, now.AddSeconds(-30), 432, 501).Comparable ||
            !ValidCpuEvidence(two) ||
            ValidCpuEvidence(two with { TotalTopCpuPercent = 5.6 }) ||
            ValidCpuEvidence(two with { TotalTopCpuPercent = double.NaN }) ||
            ValidCpuEvidence(two with { Count = 0 }) ||
            !Verdict(4, two, restored).Contains("bez průkazné změny",
                StringComparison.Ordinal) ||
            !Verdict(4, two with { TotalTopCpuPercent = 5.6 },
                restored).StartsWith("neprůkazné", StringComparison.Ordinal) ||
            !Verdict(4, two, restored with {
                TotalTopCpuPercent = 12
            }).StartsWith("neprůkazné", StringComparison.Ordinal) ||
            ReadyForRestoredReference(stableB with {
                TotalTopCpuPercent = 5.0
            }, default, default, applied, now) ||
            !Verdict(4, two with { TotalTopCpuPercent = double.NaN },
                restored).StartsWith("neprůkazné", StringComparison.Ordinal) ||
            !Verdict(4, two with { MinimumTopCpuPercent = double.PositiveInfinity },
                restored).StartsWith("neprůkazné", StringComparison.Ordinal) ||
            !Verdict(4, two with { TotalTopCpuPercent = -2 },
                restored).StartsWith("neprůkazné", StringComparison.Ordinal) ||
            !Verdict(4, two with { MinimumTopCpuPercent = 6, MaximumTopCpuPercent = 3 },
                restored).StartsWith("neprůkazné", StringComparison.Ordinal) ||
            !Verdict(4, two, restored with { TotalTopCpuPercent = double.NaN })
                .StartsWith("neprůkazné", StringComparison.Ordinal) ||
            !Verdict(double.PositiveInfinity, two, restored)
                .StartsWith("neprůkazné", StringComparison.Ordinal) ||
            !Verdict(101, two, restored)
                .StartsWith("neprůkazné", StringComparison.Ordinal) ||
            !Verdict(4, one, restored).StartsWith("neprůkazné", StringComparison.Ordinal) ||
            !Verdict(4, two, restored with {
                MinimumTopCpuPercent = 1, MaximumTopCpuPercent = 15
            }).StartsWith("neprůkazné", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Process-priority feedback accepted stale, duplicate or non-comparable evidence.");

        // Isolated read-only hot-path benchmark: the candidate's CPU data
        // is already in the FanAgent snapshot; no process enumeration or
        // heap allocations should occur while consuming it.
        const int cycles = 32768;
        long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        double checksum = 0;
        for (int i = 0; i < cycles; i++)
        {
            if (!TryGetTargetCpu(displaced, "OneDrive", out double cpu))
                throw new InvalidOperationException("Named CPU sample was lost.");
            checksum += cpu;
        }
        double readMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
        if (allocated > 128 || Math.Abs(checksum - cycles * 2.4) > 0.0001)
            throw new InvalidOperationException("Named process sampling allocated or corrupted values.");
        return $"AI_PROCESS_PRIORITY_FEEDBACK_SMOKE_OK distinctPhaseSnapshots=true " +
            $"singleInstanceRequired=true windowsSearchNamedCpu=true " +
            $"gamingRawPressureRestores=true cpuRamDiskPressureRestores=true foregroundMustMatch=true " +
            $"systemLoadComparable=true earlyBToA75sStableOnly=true " +
            $"freshCpuAndSameWindowRequired=true noFakeCpuSavings=true " +
            $"hotReads={cycles} hotReadMs={readMs:0.###} hotAllocB={allocated} writes=0";
    }
}
