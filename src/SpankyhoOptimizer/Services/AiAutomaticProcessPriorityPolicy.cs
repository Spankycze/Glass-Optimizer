using System;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// A small, per-process decision policy, not a collection of user-selected
/// gaming/work/streaming profiles. Only trusted background sync and noncritical
/// Windows Search indexing helpers explicitly allowlisted here may temporarily
/// move from Normal to BelowNormal. Core Windows, update, security, audio,
/// display, games, compilers and browsers are never modified.
/// </summary>
internal static class AiAutomaticProcessPriorityPolicy
{
    // Low-risk candidates with sustained 1%+ CPU can be worth reducing
    // while another app owns the foreground. Actual scheduling changes
    // still require two independent samples, trusted identity, foreground
    // response evidence, WAL ownership and A/B/A rollback.
    internal const double MinimumBackgroundCpuPercent = 1.0;
    internal static readonly TimeSpan MinimumObservation = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MaximumObservationGap = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaximumHold = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan IdleProbeCadence = TimeSpan.FromSeconds(25);
    internal static readonly TimeSpan CandidateProbeCadence = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan OwnedProbeCadence = TimeSpan.FromSeconds(10);

    internal static TimeSpan ProbeCadence(bool candidateObserved, bool ownedOrReference) =>
        ownedOrReference ? OwnedProbeCadence :
        candidateObserved ? CandidateProbeCadence : IdleProbeCadence;

    internal static bool MayLower(
        bool enabled, AiWorkloadClass workload, string? topProcess,
        double topCpuPercent, string? foregroundProcess, bool gameActive)
    {
        if (!enabled ||
            (workload != AiWorkloadClass.Interactive &&
             workload != AiWorkloadClass.Game) ||
            (workload == AiWorkloadClass.Game && !gameActive) ||
            !double.IsFinite(topCpuPercent) ||
            topCpuPercent < MinimumBackgroundCpuPercent || topCpuPercent > 100 ||
            string.IsNullOrWhiteSpace(topProcess) ||
            string.IsNullOrWhiteSpace(foregroundProcess) ||
            string.Equals(topProcess, foregroundProcess, StringComparison.OrdinalIgnoreCase) ||
            (IsWindowsSearchIndexer(topProcess) &&
             IsInteractiveSearchUi(foregroundProcess)))
            return false;
        // Without a trusted game/interactive classification, there is no
        // justification to lower a user app just because it is busy.
        // Epic's main launcher is eligible only as a *background* CPU-heavy
        // process during ordinary interactive desktop work. Never change it
        // while gaming, launching a game or viewing the launcher itself.
        if (string.Equals(topProcess, "EpicGamesLauncher",
                StringComparison.OrdinalIgnoreCase) &&
            (gameActive || workload != AiWorkloadClass.Interactive))
            return false;
        return IsAllowlisted(topProcess);
    }

    // Game recognition can precede the stabilized workload classification.
    // An already lowered Epic launcher must be restored on that FIRST tick,
    // even if the workload still says Interactive for a short interval.
    internal static bool EpicGameProtectionRequired(string? name, bool gameActive) =>
        gameActive && string.Equals(name, "EpicGamesLauncher", StringComparison.OrdinalIgnoreCase);

    internal static bool IsInteractiveSearchUi(string? name) =>
        string.Equals(name, "SearchHost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "SearchApp", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "SearchUI", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase);

    internal static bool IsWindowsSearchIndexer(string? name) =>
        string.Equals(name, "SearchIndexer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "SearchProtocolHost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "SearchFilterHost", StringComparison.OrdinalIgnoreCase);

    internal static bool IsAllowlisted(string? name) =>
        string.Equals(name, "OneDrive", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Dropbox", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "EpicGamesLauncher", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "qbittorrent", StringComparison.OrdinalIgnoreCase) ||
        IsWindowsSearchIndexer(name);

    internal static bool TrustedExecutable(string? name, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !System.IO.Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        // Match trusted installation locations exactly, not just a basename
        // or a suffix beneath an arbitrary writable directory.
        string normalized = System.IO.Path.GetFullPath(path);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        bool Same(string root, params string[] parts) =>
            !string.IsNullOrWhiteSpace(root) &&
            string.Equals(normalized, System.IO.Path.Combine(root, System.IO.Path.Combine(parts)),
                StringComparison.OrdinalIgnoreCase);
        // Only the genuine native Windows Search executables in the Windows
        // system directory. Never svchost, DWM, Defender, Windows Update etc.
        // If a protected Search process cannot be inspected, skip it safely.
        if (IsWindowsSearchIndexer(name))
            return Same(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", name + ".exe");
        if (string.Equals(name, "EpicGamesLauncher", StringComparison.OrdinalIgnoreCase))
            return Same(programFiles, "Epic Games", "Launcher", "Portal",
                       "Binaries", "Win64", "EpicGamesLauncher.exe");
        if (string.Equals(name, "qbittorrent", StringComparison.OrdinalIgnoreCase))
            return Same(programFiles, "qBittorrent", "qbittorrent.exe") ||
                   Same(programFilesX86, "qBittorrent", "qbittorrent.exe");
        return string.Equals(name, "OneDrive", StringComparison.OrdinalIgnoreCase)
            ? Same(programFiles, "Microsoft OneDrive", "OneDrive.exe") ||
              Same(programFilesX86, "Microsoft OneDrive", "OneDrive.exe") ||
              Same(localAppData, "Microsoft", "OneDrive", "OneDrive.exe")
            : string.Equals(name, "Dropbox", StringComparison.OrdinalIgnoreCase) &&
              (Same(programFiles, "Dropbox", "Client", "Dropbox.exe") ||
               Same(programFilesX86, "Dropbox", "Client", "Dropbox.exe") ||
               Same(localAppData, "Dropbox", "Client", "Dropbox.exe"));
    }

    internal static bool ShouldRestore(
        bool enabled, AiWorkloadClass workload, string? topProcess,
        string? foregroundProcess, DateTime appliedUtc, DateTime nowUtc) =>
        !enabled || (workload != AiWorkloadClass.Game &&
                     workload != AiWorkloadClass.Interactive) ||
        string.Equals(topProcess, foregroundProcess, StringComparison.OrdinalIgnoreCase) ||
        (IsWindowsSearchIndexer(topProcess) && IsInteractiveSearchUi(foregroundProcess)) ||
        (string.Equals(topProcess, "EpicGamesLauncher", StringComparison.OrdinalIgnoreCase) &&
         workload == AiWorkloadClass.Game) ||
        !IsAllowlisted(topProcess) ||
        nowUtc < appliedUtc || nowUtc - appliedUtc >= MaximumHold;

    // Use the five already sampled trusted targets, not only TopCpuProcess.
    // The busiest foreground app is often the top CPU consumer; requiring it
    // to be an allowlisted helper would prevent useful safe trials entirely.
    // This is read-only: no extra Process enumeration or scheduler changes.
    private static readonly string[] SampledTargets = {
        "OneDrive", "Dropbox", "SearchIndexer", "SearchProtocolHost",
        "SearchFilterHost", "EpicGamesLauncher", "qbittorrent"
    };

    internal static string SelectObservedCandidate(
        bool enabled, AiWorkloadClass workload, AdaptivePowerRuntimeStatus? power,
        AiPriorityTrialMemoryPolicy.History? history, DateTime nowUtc,
        string? preferred, out int targetPid, out double targetCpuPercent)
    {
        targetPid = 0;
        targetCpuPercent = double.NaN;
        if (!enabled || !AdaptivePowerRuntimeService.IsStatusFresh(power, nowUtc) ||
            power is null || power.ForegroundProcessId <= 0)
            return string.Empty;

        string best = string.Empty;
        double bestCpu = double.NegativeInfinity;
        foreach (string name in SampledTargets)
        {
            if (!AiAutomaticPriorityTrialPolicy.TryGetTargetCpu(power, name, out double cpu) ||
                !AiPriorityProcessIdentityPolicy.TryGetTargetPid(power, name, out int pid) ||
                AiPriorityTrialMemoryPolicy.ShouldDefer(history, name, nowUtc) ||
                !MayLower(enabled, workload, name, cpu,
                    power.ForegroundProcess, power.GameActive))
                continue;

            // A continuing eligible observation must not be restarted by a
            // transient ranking swap between two allowed background helpers.
            if (string.Equals(preferred, name, StringComparison.OrdinalIgnoreCase))
            {
                targetPid = pid;
                targetCpuPercent = cpu;
                return name;
            }
            if (cpu <= bestCpu) continue;
            best = name;
            bestCpu = cpu;
            targetPid = pid;
            targetCpuPercent = cpu;
        }
        return best;
    }

    // WM_NULL costs one bounded native cross-process roundtrip and a worker.
    // There is no reason to probe a foreground window unless an allowed
    // background target is actually being observed, or an existing A/B/A
    // intervention must finish with live B/A2 responsiveness evidence.
    // No additional process enumeration and no new sampling timer.
    internal static bool NeedsForegroundResponse(
        bool enabled, AiWorkloadClass workload, AdaptivePowerRuntimeStatus? power,
        AiPriorityTrialMemoryPolicy.History? history, DateTime nowUtc,
        string? preferred, bool ownedTrial, bool restoredReference)
    {
        if (!AiForegroundWindowResponsePolicy.CanProbe(
                enabled, power, workload, nowUtc))
            return false;
        if (ownedTrial || restoredReference)
            return true;
        return SelectObservedCandidate(
            enabled, workload, power, history, nowUtc,
            preferred, out _, out _) != string.Empty;
    }

    // Called only when selection returned no candidate. Explain a real
    // read-only gate, rather than presenting all idle/missing/cooldown cases
    // as "AI is waiting". Uses the same five existing named CPU/PID samples.
    internal static string ExplainNoCandidate(
        bool enabled, AiWorkloadClass workload, AdaptivePowerRuntimeStatus? power,
        AiPriorityTrialMemoryPolicy.History? history, DateTime nowUtc)
    {
        if (!enabled) return "automatika vypnuta";
        if (!AdaptivePowerRuntimeService.IsStatusFresh(power, nowUtc) || power is null)
            return "chybi aktualni telemetrie";
        if (power.ForegroundProcessId <= 0 ||
            string.IsNullOrWhiteSpace(power.ForegroundProcess))
            return "chybi overena identita aktivniho okna";
        if (workload is not (AiWorkloadClass.Interactive or AiWorkloadClass.Game) ||
            (workload == AiWorkloadClass.Game && !power.GameActive))
            return "neznamy nebo neaktivni pracovni stav";

        int observed = 0, tooLow = 0, cooldown = 0, protectedCount = 0;
        foreach (string name in SampledTargets)
        {
            if (!AiAutomaticPriorityTrialPolicy.TryGetTargetCpu(power, name, out double cpu) ||
                !AiPriorityProcessIdentityPolicy.TryGetTargetPid(power, name, out _))
                continue;
            observed++;
            if (cpu < MinimumBackgroundCpuPercent)
                tooLow++;
            else if (!MayLower(enabled, workload, name, cpu,
                         power.ForegroundProcess, power.GameActive))
                protectedCount++;
            else if (AiPriorityTrialMemoryPolicy.ShouldDefer(history, name, nowUtc))
                cooldown++;
        }
        if (observed == 0) return "zadny povoleny proces s overenym PID a CPU";
        if (tooLow == observed) return "povolene procesy nemaji dostatecnou zatez";
        if (cooldown > 0 && cooldown + tooLow == observed)
            return "ochranna prodleva po predchozim mereni";
        if (protectedCount > 0) return "ochrana popredi nebo vyhledavani Windows";
        return "zadny bezpecny kandidat za aktualnich podminek";
    }

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        // The busy foreground browser is top CPU. A viable background
        // Dropbox/OneDrive must still be discoverable from named native samples.
        var sampled = new AdaptivePowerRuntimeStatus(
            now.AddSeconds(-5), "Observation", 16, "", null, null, null,
            false, false, 20, 2, "Interactive", "", "chrome", 12, "chrome",
            OneDriveCpuPercent: 3.2, DropboxCpuPercent: 5.0,
            OneDriveInstanceCount: 1, DropboxInstanceCount: 1,
            OneDriveProcessId: 101, DropboxProcessId: 102,
            ForegroundProcessId: 501);
        var history = AiPriorityTrialMemoryPolicy.Empty;
        // A freshly recognized game must invalidate an owned Epic trial even
        // while the workload stabilizer still reports Interactive.
        if (!EpicGameProtectionRequired("EpicGamesLauncher", true) ||
            !EpicGameProtectionRequired("epicgameslauncher", true) ||
            EpicGameProtectionRequired("EpicGamesLauncher", false) ||
            EpicGameProtectionRequired("OneDrive", true) ||
            EpicGameProtectionRequired(null, true))
            throw new InvalidOperationException(
                "Epic priority rollback did not honor the first game-active signal.");
        string best = SelectObservedCandidate(true, AiWorkloadClass.Interactive,
            sampled, history, now, null, out int observedPid, out double observedCpu);
        string preserved = SelectObservedCandidate(true, AiWorkloadClass.Interactive,
            sampled, history, now, "OneDrive", out int preservedPid, out _);
        var cooled = AiPriorityTrialMemoryPolicy.Remember(
            history, "Dropbox", "neprůkazné", now.AddMinutes(-10));
        string afterCooldown = SelectObservedCandidate(true,
            AiWorkloadClass.Interactive, sampled, cooled, now, null,
            out int otherPid, out _);
        var allCooled = AiPriorityTrialMemoryPolicy.Remember(
            cooled, "OneDrive", "neprůkazné", now.AddMinutes(-9));
        if (best != "Dropbox" || observedPid != 102 || observedCpu != 5 ||
            preserved != "OneDrive" || preservedPid != 101 ||
            afterCooldown != "OneDrive" || otherPid != 101 ||
            SelectObservedCandidate(true, AiWorkloadClass.Interactive,
                sampled, allCooled, now, null, out _, out _) != "" ||
            SelectObservedCandidate(true, AiWorkloadClass.Interactive,
                sampled with { ForegroundProcessId = 0 }, history,
                now, null, out _, out _) != "" ||
            SelectObservedCandidate(true, AiWorkloadClass.Interactive,
                sampled with { OneDriveProcessId = 0, DropboxProcessId = 0 },
                history, now, null, out _, out _) != "" ||
            SelectObservedCandidate(true, AiWorkloadClass.Interactive,
                sampled with { DropboxInstanceCount = 2 }, history,
                now, null, out _, out _) != "OneDrive" ||
            SelectObservedCandidate(true, AiWorkloadClass.Game,
                sampled with { GameActive = false }, history,
                now, null, out _, out _) != "" ||
            SelectObservedCandidate(true, AiWorkloadClass.Unknown,
                sampled, history, now, null, out _, out _) != "" ||
            SelectObservedCandidate(false, AiWorkloadClass.Interactive,
                sampled, history, now, null, out _, out _) != "" ||
            SelectObservedCandidate(true, AiWorkloadClass.Interactive,
                sampled with { UpdatedUtc = now.AddMinutes(-10) },
                history, now, null, out _, out _) != "")
            throw new InvalidOperationException(
                "Priority candidate ranking missed named samples, PID or workload protections.");

        // Probe only with a trusted candidate or while completing A/B/A.
        // A candidate on cooldown should stop otherwise useless WM_NULL
        // messages; B and restored A2 MUST keep their window evidence.
        if (!NeedsForegroundResponse(true, AiWorkloadClass.Interactive,
                sampled, history, now, null, false, false) ||
            NeedsForegroundResponse(true, AiWorkloadClass.Interactive,
                sampled, allCooled, now, null, false, false) ||
            !NeedsForegroundResponse(true, AiWorkloadClass.Interactive,
                sampled, allCooled, now, null, true, false) ||
            !NeedsForegroundResponse(true, AiWorkloadClass.Interactive,
                sampled, allCooled, now, null, false, true) ||
            NeedsForegroundResponse(false, AiWorkloadClass.Interactive,
                sampled, history, now, null, true, true) ||
            NeedsForegroundResponse(true, AiWorkloadClass.Game,
                sampled with { GameActive = true }, history,
                now, null, true, true) ||
            NeedsForegroundResponse(true, AiWorkloadClass.Interactive,
                sampled with { ForegroundProcessId = 0 }, history,
                now, null, true, true) ||
            NeedsForegroundResponse(true, AiWorkloadClass.Interactive,
                sampled with { UpdatedUtc = now.AddMinutes(-5) }, history,
                now, null, true, true))
            throw new InvalidOperationException(
                "WM_NULL probe admission lost trial evidence or probed without a safe target.");

        // Read-only reasons explain why no native change was eligible.
        // They must never expand the allowlist, infer missing PID/CPU, or
        // turn a cooldown into a bypass. No process scans or writes.
        if (ExplainNoCandidate(false, AiWorkloadClass.Interactive,
                sampled, history, now) != "automatika vypnuta" ||
            ExplainNoCandidate(true, AiWorkloadClass.Interactive,
                sampled with { UpdatedUtc = now.AddMinutes(-5) },
                history, now) != "chybi aktualni telemetrie" ||
            ExplainNoCandidate(true, AiWorkloadClass.Interactive,
                sampled with { ForegroundProcessId = 0 },
                history, now) != "chybi overena identita aktivniho okna" ||
            ExplainNoCandidate(true, AiWorkloadClass.Unknown,
                sampled, history, now) != "neznamy nebo neaktivni pracovni stav" ||
            ExplainNoCandidate(true, AiWorkloadClass.Game,
                sampled with { GameActive = false },
                history, now) != "neznamy nebo neaktivni pracovni stav" ||
            ExplainNoCandidate(true, AiWorkloadClass.Interactive,
                sampled with { OneDriveProcessId = 0, DropboxProcessId = 0 },
                history, now) != "zadny povoleny proces s overenym PID a CPU" ||
            ExplainNoCandidate(true, AiWorkloadClass.Interactive,
                sampled with { OneDriveCpuPercent = 0.2, DropboxCpuPercent = 0.3 },
                history, now) != "povolene procesy nemaji dostatecnou zatez" ||
            ExplainNoCandidate(true, AiWorkloadClass.Interactive,
                sampled, allCooled, now) != "ochranna prodleva po predchozim mereni" ||
            ExplainNoCandidate(true, AiWorkloadClass.Interactive,
                sampled with { ForegroundProcess = "SearchHost",
                    OneDriveCpuPercent = null, OneDriveInstanceCount = 0,
                    DropboxCpuPercent = null, DropboxInstanceCount = 0,
                    SearchIndexerCpuPercent = 4.0, SearchIndexerInstanceCount = 1,
                    SearchIndexerProcessId = 103 },
                history, now) != "ochrana popredi nebo vyhledavani Windows" ||
            ExplainNoCandidate(true, AiWorkloadClass.Interactive,
                sampled with { ForegroundProcess = "SearchHost",
                    OneDriveCpuPercent = null, OneDriveInstanceCount = 0,
                    SearchIndexerCpuPercent = 4.0, SearchIndexerInstanceCount = 1,
                    SearchIndexerProcessId = 103 },
                cooled, now) != "ochrana popredi nebo vyhledavani Windows")
            throw new InvalidOperationException(
                "Priority eligibility diagnostics discarded PID, cooldown, or foreground protections.");

        // A/B/A must continue to track the named process after a busy
        // foreground task stays above it in TopCpuProcess throughout the trial.
        var a1 = sampled with { UpdatedUtc = now.AddSeconds(-55),
            OneDriveCpuPercent = 3.0 };
        var a2 = sampled with { UpdatedUtc = now.AddSeconds(-45),
            OneDriveCpuPercent = 3.1 };
        var b1 = sampled with { UpdatedUtc = now.AddSeconds(-35),
            OneDriveCpuPercent = 2.0 };
        var b2 = sampled with { UpdatedUtc = now.AddSeconds(-25),
            OneDriveCpuPercent = 2.1 };
        var a3 = sampled with { UpdatedUtc = now.AddSeconds(-15),
            OneDriveCpuPercent = 3.0 };
        var a4 = sampled with { UpdatedUtc = now.AddSeconds(-5),
            OneDriveCpuPercent = 3.1 };
        var baselineA = AiAutomaticPriorityTrialPolicy.AddDistinct(
            AiAutomaticPriorityTrialPolicy.AddDistinct(default, a1, "OneDrive"),
            a2, "OneDrive");
        var measuredB = AiAutomaticPriorityTrialPolicy.AddDistinct(
            AiAutomaticPriorityTrialPolicy.AddDistinct(default, b1, "OneDrive"),
            b2, "OneDrive");
        var restoredA = AiAutomaticPriorityTrialPolicy.AddDistinct(
            AiAutomaticPriorityTrialPolicy.AddDistinct(default, a3, "OneDrive"),
            a4, "OneDrive");
        string observedAba = AiAutomaticPriorityTrialPolicy.Verdict(
            baselineA.Average, measuredB, restoredA);
        if (baselineA.Count != 2 || measuredB.Count != 2 ||
            restoredA.Count != 2 ||
            !observedAba.Contains("pozorování změny", StringComparison.Ordinal) ||
            !observedAba.Contains("nikoli prokázané", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Named-process A/B/A evidence was lost when the foreground stayed top.");

        const int selectionChecks = 16384;
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long beganTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < selectionChecks; i++)
            if (SelectObservedCandidate(true, AiWorkloadClass.Interactive,
                    sampled, history, now, null, out _, out _) != "Dropbox")
                throw new InvalidOperationException("Observed target ranking unstable.");
        long allocatedB = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        double selectionMs = System.Diagnostics.Stopwatch.GetElapsedTime(
            beganTicks).TotalMilliseconds;
        if (allocatedB > 512)
            throw new InvalidOperationException("AI target ranking allocated per tick.");

        // Allocation-free admission on the existing agent tick is far
        // cheaper than a needless 80ms-bounded native foreground probe.
        const int admissionChecks = 8192;
        long admissionBefore = GC.GetAllocatedBytesForCurrentThread();
        long admissionStart = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < admissionChecks; i++)
            if (NeedsForegroundResponse(true, AiWorkloadClass.Interactive,
                    sampled, allCooled, now, null, false, false))
                throw new InvalidOperationException("No-candidate WM_NULL admitted.");
        long admissionAlloc = GC.GetAllocatedBytesForCurrentThread() - admissionBefore;
        double admissionMs = System.Diagnostics.Stopwatch.GetElapsedTime(
            admissionStart).TotalMilliseconds;
        if (admissionAlloc > 512)
            throw new InvalidOperationException("WM_NULL admission hot path allocated memory.");

        const int diagnosticChecks = 8192;
        var lowSampled = sampled with {
            OneDriveCpuPercent = 0.2, DropboxCpuPercent = 0.3
        };
        long diagBefore = GC.GetAllocatedBytesForCurrentThread();
        long diagStart = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < diagnosticChecks; i++)
            if (ExplainNoCandidate(true, AiWorkloadClass.Interactive,
                    lowSampled, history, now) != "povolene procesy nemaji dostatecnou zatez")
                throw new InvalidOperationException("Inconsistent no-candidate diagnostic.");
        long diagAlloc = GC.GetAllocatedBytesForCurrentThread() - diagBefore;
        double diagnosticMs = System.Diagnostics.Stopwatch.GetElapsedTime(
            diagStart).TotalMilliseconds;
        if (diagAlloc > 512)
            throw new InvalidOperationException("Priority eligibility diagnostic allocated per tick.");

        if (!MayLower(true, AiWorkloadClass.Game, "OneDrive", 4.0, "game", true) ||
            !MayLower(true, AiWorkloadClass.Interactive, "Dropbox", 2.5, "firefox", false) ||
            !MayLower(true, AiWorkloadClass.Interactive, "SearchIndexer", 3.4, "explorer", false) ||
            !MayLower(true, AiWorkloadClass.Game, "SearchProtocolHost", 2.5, "game", true) ||
            !MayLower(true, AiWorkloadClass.Game, "SearchFilterHost", 3.0, "game", true) ||
            MayLower(true, AiWorkloadClass.Interactive, "SearchIndexer", 5, "SearchIndexer", false) ||
            MayLower(true, AiWorkloadClass.Interactive, "SearchIndexer", 5, "SearchHost", false) ||
            MayLower(true, AiWorkloadClass.Interactive, "SearchProtocolHost", 5, "StartMenuExperienceHost", false) ||
            MayLower(true, AiWorkloadClass.Interactive, "SearchFilterHost", 5, "", false) ||
            MayLower(true, AiWorkloadClass.Interactive, "SearchIndexer", 0.9, "explorer", false) ||
            MayLower(false, AiWorkloadClass.Game, "OneDrive", 5, "game", true) ||
            MayLower(true, AiWorkloadClass.Idle, "OneDrive", 5, "explorer", false) ||
            MayLower(true, AiWorkloadClass.Game, "OneDrive", 5, null, true) ||
            MayLower(true, AiWorkloadClass.Unknown, "OneDrive", 5, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "OneDrive", double.NaN, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "OneDrive", 0.9, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "OneDrive", 5, "OneDrive", true) ||
            MayLower(true, AiWorkloadClass.Game, "chrome", 8, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "msedgewebview2", 8, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "MsMpEng", 8, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "svchost", 8, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "dwm", 8, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "explorer", 8, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "TiWorker", 8, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "MoUsoCoreWorker", 8, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "TrustedInstaller", 8, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "audiodg", 8, "game", true) ||
            MayLower(true, AiWorkloadClass.Game, "dotnet", 8, "game", true) ||
            !ShouldRestore(false, AiWorkloadClass.Game, "OneDrive", "game", now, now) ||
            !ShouldRestore(true, AiWorkloadClass.Idle, "OneDrive", "explorer", now, now) ||
            !ShouldRestore(true, AiWorkloadClass.Game, "OneDrive", "game", now.AddMinutes(-4), now) ||
            ShouldRestore(true, AiWorkloadClass.Game, "SearchIndexer", "game", now.AddSeconds(-30), now) ||
            !ShouldRestore(true, AiWorkloadClass.Interactive, "SearchIndexer", "SearchHost", now.AddSeconds(-30), now) ||
            !ShouldRestore(true, AiWorkloadClass.Idle, "SearchIndexer", "explorer", now, now) ||
            ShouldRestore(true, AiWorkloadClass.Game, "OneDrive", "game", now.AddSeconds(-30), now) ||
            !TrustedExecutable("OneDrive", System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "OneDrive", "OneDrive.exe")) ||
            !TrustedExecutable("Dropbox", System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Dropbox", "Client", "Dropbox.exe")) ||
            !TrustedExecutable("SearchIndexer", System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "SearchIndexer.exe")) ||
            !TrustedExecutable("SearchProtocolHost", System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "SearchProtocolHost.exe")) ||
            !TrustedExecutable("SearchFilterHost", System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "SearchFilterHost.exe")) ||
            TrustedExecutable("SearchIndexer", System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Temp", "SearchIndexer.exe")) ||
            TrustedExecutable("SearchIndexer", System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "SysWOW64", "SearchIndexer.exe")) ||
            TrustedExecutable("MsMpEng", System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "MsMpEng.exe")) ||
            TrustedExecutable("OneDrive", @"C:\Windows\System32\OneDrive.exe") ||
            TrustedExecutable("OneDrive", @"C:\Temp\OneDrive.exe") ||
            TrustedExecutable("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"))
            throw new InvalidOperationException(
                "Automatic process priority could affect protected apps or ignore restore gates.");
        return "AI_AUTOMATIC_PROCESS_POLICY_SMOKE_OK allowlist=OneDrive,Dropbox,WindowsSearch " +
               "criticalWindowsUpdateDefenderExcluded=true exactSystem32Image=true " +
               "gameInteractiveProtected=true browsersCompilersDefenderExcluded=true " +
               $"namedCpuRanking=true cooldownFallback=true candidateSticky=true " +
               $"selectionChecks={selectionChecks} selectionMs={selectionMs:0.###} " +
               $"selectionAllocB={allocatedB} wmNullCandidateOnly=true " +
               $"admissionChecks={admissionChecks} admissionMs={admissionMs:0.###} " +
               $"admissionAllocB={admissionAlloc} diagnosticReasons=true " +
               $"diagnosticChecks={diagnosticChecks} diagnosticMs={diagnosticMs:0.###} " +
               $"diagnosticAllocB={diagAlloc} restoreBounded=true " +
               "profileSelection=false writes=0";
    }
}
