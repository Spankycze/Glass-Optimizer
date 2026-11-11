using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

// AI manages *idle applications* rather than indiscriminately disabling
// Windows services. One observation pass per minute, one change per pass.
// Never kills a process, edits driver/service Start types or disables an updater.
// Ownership is durable; actual program use, AI-off or failed recovery brings
// the original state back. No restart/install needed.
internal sealed record AiThirdPartyFeedbackView(
    string Kind, string Id, string AppKey, DateTime UpdatedUtc,
    string Verdict, string Detail);

internal static class AiThirdPartyIdlePolicy
{
    internal static readonly string[] AppKeys =
    {
        "steam", "discord", "epic", "battlenet", "riot", "teams",
        "ea", "ubisoft", "gog", "spotify", "slack", "zoom"
    };

    // Only an independent, demand-start helper; no core Steam process/service.
    internal static readonly (string App, string Name)[] Services =
    {
        ("steam", "Steam Client Service")
    };

    // Only voluntary browser-choice telemetry, NEVER browser/security updates.
    internal static readonly (string App, string Prefix)[] Tasks =
    {
        ("firefox", @"\Mozilla\Firefox Default Browser Agent")
    };

    private static readonly string[] UnsafeStartupLabels =
    {
        "updater", "update service", "anti-cheat", "anticheat",
        "security", "defender", "gearlink", "armoury", "firmware"
    };

    // The Background Activity UI deliberately uses broad textual search.
    // Automatic Windows writes need stronger executable identity instead:
    // never disable RunOnce, helpers, updaters, security or peripheral agents
    // merely because their publisher/path contains "Steam", "Teams", etc.
    internal static bool CanManageStartup(string app, StartupEntry entry)
    {
        if (!entry.Enabled || !entry.CanToggle ||
            !string.Equals(entry.SourceKind, "Registry", StringComparison.OrdinalIgnoreCase) ||
            entry.Hive != Microsoft.Win32.RegistryHive.CurrentUser ||
            !string.Equals(entry.RunPath,
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(entry.Name) ||
            string.IsNullOrWhiteSpace(entry.FilePath))
            return false;

        // The Windows path span and canonical static app key avoid two
        // strings per candidate (GetFileName + ToLowerInvariant). Never
        // compare the folder or a fuzzy publisher label to identify the exe.
        ReadOnlySpan<char> executable = Path.GetFileName(entry.FilePath.AsSpan());
        if (executable.IsWhiteSpace() ||
            !Path.IsPathFullyQualified(entry.FilePath))
            return false;

        bool recognized = BackgroundActivityService.GetAiCanonicalManagedKey(app) switch
        {
            "steam" => executable.Equals("steam.exe".AsSpan(), StringComparison.OrdinalIgnoreCase),
            "discord" => executable.Equals("discord.exe".AsSpan(), StringComparison.OrdinalIgnoreCase),
            "epic" => executable.Equals("EpicGamesLauncher.exe".AsSpan(), StringComparison.OrdinalIgnoreCase),
            "battlenet" => executable.Equals("Battle.net.exe".AsSpan(), StringComparison.OrdinalIgnoreCase),
            "riot" => executable.Equals("RiotClientServices.exe".AsSpan(), StringComparison.OrdinalIgnoreCase),
            "teams" => executable.Equals("ms-teams.exe".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                       executable.Equals("Teams.exe".AsSpan(), StringComparison.OrdinalIgnoreCase),
            "ea" => executable.Equals("EADesktop.exe".AsSpan(), StringComparison.OrdinalIgnoreCase),
            "ubisoft" => executable.Equals("UbisoftConnect.exe".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                         executable.Equals("upc.exe".AsSpan(), StringComparison.OrdinalIgnoreCase),
            "gog" => executable.Equals("GalaxyClient.exe".AsSpan(), StringComparison.OrdinalIgnoreCase),
            "spotify" => executable.Equals("Spotify.exe".AsSpan(), StringComparison.OrdinalIgnoreCase),
            "slack" => executable.Equals("slack.exe".AsSpan(), StringComparison.OrdinalIgnoreCase),
            "zoom" => executable.Equals("Zoom.exe".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                      executable.Equals("ZoomWorkplace.exe".AsSpan(), StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!recognized) return false;
        // Block explicit updater/maintenance/security identities, including
        // a launcher renamed to an otherwise eligible .exe name.
        foreach (string unsafeName in UnsafeStartupLabels)
        {
            if (entry.Name.Contains(unsafeName, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    // Legacy oracle is intentionally confined to this in-memory smoke.
    // It compares the old string-lowering/path-filename implementation
    // against the new zero-copy span + shared canonical-name index for
    // every approved executable and denied updater/security variant.
    internal static bool RunStartupExecutableAllocationParitySmoke()
    {
        static bool Old(string app, StartupEntry entry)
        {
            if (!entry.Enabled || !entry.CanToggle ||
                !entry.SourceKind.Equals("Registry", StringComparison.OrdinalIgnoreCase) ||
                entry.Hive != Microsoft.Win32.RegistryHive.CurrentUser ||
                !entry.RunPath.Equals(
                    @"Software\Microsoft\Windows\CurrentVersion\Run",
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(entry.FilePath))
                return false;
            string executable = Path.GetFileName(entry.FilePath);
            if (string.IsNullOrWhiteSpace(executable) ||
                !Path.IsPathFullyQualified(entry.FilePath))
                return false;
            bool recognized = app.ToLowerInvariant() switch
            {
                "steam" => executable.Equals("steam.exe", StringComparison.OrdinalIgnoreCase),
                "discord" => executable.Equals("discord.exe", StringComparison.OrdinalIgnoreCase),
                "epic" => executable.Equals("EpicGamesLauncher.exe", StringComparison.OrdinalIgnoreCase),
                "battlenet" => executable.Equals("Battle.net.exe", StringComparison.OrdinalIgnoreCase),
                "riot" => executable.Equals("RiotClientServices.exe", StringComparison.OrdinalIgnoreCase),
                "teams" => executable.Equals("ms-teams.exe", StringComparison.OrdinalIgnoreCase) ||
                           executable.Equals("Teams.exe", StringComparison.OrdinalIgnoreCase),
                "ea" => executable.Equals("EADesktop.exe", StringComparison.OrdinalIgnoreCase),
                "ubisoft" => executable.Equals("UbisoftConnect.exe", StringComparison.OrdinalIgnoreCase) ||
                             executable.Equals("upc.exe", StringComparison.OrdinalIgnoreCase),
                "gog" => executable.Equals("GalaxyClient.exe", StringComparison.OrdinalIgnoreCase),
                "spotify" => executable.Equals("Spotify.exe", StringComparison.OrdinalIgnoreCase),
                "slack" => executable.Equals("slack.exe", StringComparison.OrdinalIgnoreCase),
                "zoom" => executable.Equals("Zoom.exe", StringComparison.OrdinalIgnoreCase) ||
                          executable.Equals("ZoomWorkplace.exe", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (!recognized) return false;
            foreach (string unsafeName in UnsafeStartupLabels)
                if (entry.Name.Contains(unsafeName, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }
        var files = new[]
        {
            "steam.exe", "discord.exe", "EpicGamesLauncher.exe",
            "Battle.net.exe", "RiotClientServices.exe", "ms-teams.exe",
            "Teams.exe", "EADesktop.exe", "UbisoftConnect.exe", "upc.exe",
            "GalaxyClient.exe", "Spotify.exe", "slack.exe",
            "Zoom.exe", "ZoomWorkplace.exe", "Update.exe", "Defender.exe"
        };
        var appNames = new List<string>(AppKeys.Length * 2 + 5);
        foreach (string app in AppKeys)
        {
            appNames.Add(app);
            appNames.Add(app.ToUpperInvariant());
            if (!string.Equals(BackgroundActivityService.GetAiCanonicalManagedKey(app),
                    app, StringComparison.Ordinal) ||
                !string.Equals(BackgroundActivityService.GetAiCanonicalManagedKey(
                    app.ToUpperInvariant()), app, StringComparison.Ordinal))
                return false;
        }
        appNames.AddRange(new[] { "firefox", "FIREFOX", "windows",
            "unknown", " steam" });
        if (BackgroundActivityService.GetAiCanonicalManagedKey("firefox") != null ||
            BackgroundActivityService.GetAiCanonicalManagedKey("windows") != null)
            return false;
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        foreach (string app in appNames)
        foreach (string file in files)
        {
            var entry = new StartupEntry("fixture", "Fixture",
                @"C:\Apps\" + file,
                Microsoft.Win32.RegistryHive.CurrentUser, run, true,
                "Fixture", @"C:\Apps\" + file);
            foreach (StartupEntry row in new[]
            {
                entry, entry with { Name = "Fixture GearLink" },
                entry with { Name = "Fixture updater" },
                entry with { Name = "Fixture security" },
                entry with { FilePath = "relative\\" + file },
                entry with { RunPath = run + "Once" },
                entry with { Hive = Microsoft.Win32.RegistryHive.LocalMachine },
                entry with { SourceKind = "TaskScheduler" },
                entry with { Enabled = false }, entry with { CanToggle = false },
                entry with { FilePath = @"C:\Apps\UPDATER\Update.exe" },
                entry with { FilePath = @"C:\Apps\" + file.ToUpperInvariant() }
            })
                if (CanManageStartup(app, row) != Old(app, row))
                    return false;
        }
        return true;
    }

    internal static bool MatchesOptionalFirefoxTask(string task)
    {
        const string prefix = @"\Mozilla\Firefox Default Browser Agent ";
        if (!task.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        ReadOnlySpan<char> suffix = task.AsSpan(prefix.Length);
        if (suffix.Length != 16) return false;
        foreach (char c in suffix)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    // A user's explicit Keep choice must prevail even if changed between
    // discovery and the final native operation. Corrupt/untrusted preferences
    // must never authorize a new automatic resource change.
    internal static bool MayTouchApp(UserFeatureProfile profile, string app) =>
        !profile.IsUntrusted && profile.KeepFeatures != null &&
        !string.IsNullOrWhiteSpace(app) &&
        !profile.Keeps("bg:" + app + ":keep");

    // Background service/task changes remain bounded trials. An optional
    // HKCU startup opt-out instead protects the NEXT login; undoing it after
    // six hours of continued absence would silently cancel the optimization
    // before a reboot. Keep an owned startup entry until actual app use,
    // user KEEP, AI-off, external override or failed journal validation.
    // Still reject invalid/future timestamps and retain native read-back.
    internal static bool MayKeepOwnedHold(
        string kind, bool enabled, bool servicesEnabled, bool tasksEnabled,
        DateTime appliedUtc, DateTime nowUtc) =>
        enabled && (kind != "service" || servicesEnabled) &&
        (kind != "task" || tasksEnabled) &&
        appliedUtc.Kind == DateTimeKind.Utc &&
        appliedUtc.Ticks > 0 && // Corrupt epoch-zero UTC cannot hold forever.
        appliedUtc <= nowUtc &&
        (string.Equals(kind, "startup", StringComparison.Ordinal) ||
         nowUtc - appliedUtc < TimeSpan.FromHours(6));

    // The normal idle inventory cadence is three minutes. Do not count a
    // longer-than-five-minute blind spot (sleep, host stall or scheduler gap)
    // as continuous evidence that a program was never launched. Leave a
    // two-minute scheduling margin so normal scans retain their credit.
    internal static bool IsContinuousObservation(
        DateTime previousUtc, DateTime nowUtc) =>
        previousUtc.Kind == DateTimeKind.Utc &&
        previousUtc <= nowUtc &&
        nowUtc - previousUtc <= TimeSpan.FromMinutes(5);

    // Native process probes are cheap only when narrowly targeted and
    // throttled. No extra timer or process is created; the existing host
    // tick triggers recovery earlier than the full one-minute inventory.
    internal static bool IsOwnedRecoveryProbeDue(
        int ownedCount, DateTime lastProbeUtc, DateTime nowUtc) =>
        ownedCount > 0 && (lastProbeUtc == default ||
            lastProbeUtc.Kind != DateTimeKind.Utc ||
            lastProbeUtc > nowUtc ||
            nowUtc - lastProbeUtc >= TimeSpan.FromSeconds(10));

    // A corrupt, unreadable or untrusted preference snapshot must never
    // provide time credit toward an automatic disable after profile repair.
    // Existing owned resources are still independently restored as needed.
    internal static bool CanAccumulateIdleEvidence(
        bool aiEnabled, bool trustedPreferences) =>
        aiEnabled && trustedPreferences;

    internal static bool MayObserveReference(
        bool aiEnabled, bool servicesEnabled, bool trustedPreferences) =>
        aiEnabled && servicesEnabled && trustedPreferences;

    // When AI is already off and its journal has no outstanding resources,
    // avoid repeatedly locking the journal and enumerating Windows startup
    // processes on every FanAgent tick. Cross-host journal state is still
    // re-read every 30 seconds, and config changes bypass this throttle.
    internal static bool CanThrottleDisabledIdleTick(
        bool enabled, int ownedCount, int referenceCount,
        DateTime lastSuccessfulScanUtc, DateTime nowUtc) =>
        !enabled && ownedCount == 0 && referenceCount == 0 &&
        lastSuccessfulScanUtc.Kind == DateTimeKind.Utc &&
        nowUtc >= lastSuccessfulScanUtc &&
        nowUtc - lastSuccessfulScanUtc < TimeSpan.FromSeconds(30);

    // Native HKCU Run inventory is necessary for restoration of ANY owned
    // startup entry; otherwise read it only when AI is actually allowed to
    // introduce a new change. AI enabled alone is not sufficient when the
    // game is active, the system is busy or the ownership cap is reached.
    internal static bool NeedsStartupInventory(
        bool canIntroduceNewChange, bool ownsStartup,
        bool trustedPreferences = true) =>
        ownsStartup || (canIntroduceNewChange && trustedPreferences);

    // With neither an owned resource nor a passive A2 reference, a cycle
    // cannot perform useful work when current conditions or user preferences
    // prohibit ALL new changes. Read the shared journal first, then skip
    // native inventories and discard old idle evidence. Never bypass owner
    // recovery or the reference measurement.
    internal static bool CanSkipUnownedInventory(
        bool enabled, bool canIntroduceNewChange, bool trustedPreferences,
        int ownedCount, int referenceCount) =>
        enabled && ownedCount == 0 && referenceCount == 0 &&
        (!canIntroduceNewChange || !trustedPreferences);

    // Firefox's separate process enumeration is useful only for restoring
    // an already-owned browser task or evaluating a new optional task.
    // Keep the prewrite process recheck before each actual task mutation.
    internal static bool NeedsFirefoxPresence(
        bool enabled, bool ownsFirefoxTask, bool canIntroduceNewChange,
        bool tasksEnabled, bool trustedPreferences) =>
        enabled && (ownsFirefoxTask ||
            (canIntroduceNewChange && tasksEnabled && trustedPreferences));

    internal static bool MayRestoreServiceStart(
        bool originallyRunning, ServiceControllerStatus liveStatus,
        int liveMode, int? recordedMode) =>
        originallyRunning && liveStatus == ServiceControllerStatus.Stopped &&
        liveMode is 2 or 3 && (recordedMode is null || recordedMode == liveMode);

    internal static bool MayRestoreFirefoxTask(
        string path, bool originallyEnabled, bool liveRunning) =>
        originallyEnabled && !liveRunning && MatchesOptionalFirefoxTask(path);

    // On AI-off recovery, an owned entry may be missing/locked or have
    // changed outside the app. Failed retries must not enumerate services
    // and HKCU startup on every FanAgent 5-second tick indefinitely.
    // New config decisions or a changed shared journal bypass this cooldown.
    internal static bool IsFailedOffRestoreCoolingDown(
        bool enabled, int ownedCount, DateTime lastFailedUtc, DateTime nowUtc) =>
        !enabled && ownedCount > 0 &&
        lastFailedUtc.Kind == DateTimeKind.Utc &&
        lastFailedUtc <= nowUtc &&
        nowUtc - lastFailedUtc < TimeSpan.FromSeconds(30);

    // Do not overlap a measured service experiment with other new writes.
    // Deferred startup/task actions cannot produce an immediate CPU delta.
    internal static bool MayIntroduceChange(
        bool enabled, bool quietWindow, bool gameActive,
        int managedCount, bool measuringService, bool pendingReference) =>
        enabled && quietWindow && !gameActive &&
        managedCount < 3 && !measuringService && !pendingReference;

    internal static bool HasAnyRunningProcess(params string[] names)
    {
        foreach (string name in names)
        {
            if (ProcessSnapshotCleanup.Inspect(
                () => Process.GetProcessesByName(name),
                processes => processes.Length != 0))
                return true;
        }
        return false;
    }

    // A partial process inventory is NOT proof that an AI-managed app is idle.
    // Prefer restoring a previously disabled optional resource over extending
    // its hold when process visibility is unknown. Only trusted, present rows
    // with zero processes can justify keeping the temporary hold.
    internal static bool OwnedPresenceRequiresRestore(bool known, bool running) =>
        !known || running;

    // Persisted journal may be unreadable for one cycle (sharing violation
    // during security scanning). Fail closed for that cycle, retry under
    // the lease next time, and never silently treat an unknown journal as
    // empty ownership.
    internal static bool MayRetryJournalRead(DateTime lastFailureUtc, DateTime nowUtc) =>
        lastFailureUtc == default ||
        lastFailureUtc.Kind != DateTimeKind.Utc ||
        nowUtc < lastFailureUtc ||
        nowUtc - lastFailureUtc >= TimeSpan.FromMinutes(1);

    // A paused or unreadable dependent service is NOT safe to treat as
    // stopped. Decline any Steam helper mutation when dependency state is
    // unknown; fully dispose every native controller after the query.
    internal static bool HasOnlyStoppedDependents(
        IEnumerable<ServiceControllerStatus?> statuses) =>
        statuses.All(x => x == ServiceControllerStatus.Stopped);

    // Never award CPU/RAM feedback from a helper which was externally
    // restarted after the write. This would credit a non-existent B phase.
    internal static bool MaySampleStoppedHelper(ServiceControllerStatus? status) =>
        status == ServiceControllerStatus.Stopped;

    internal static bool CanReduce(
        TimeSpan observedAbsent, BackgroundAppPreference preference,
        bool appIsRunning, bool gameActive, bool quietWindow) =>
        !appIsRunning && !gameActive && quietWindow &&
        preference != BackgroundAppPreference.Keep &&
        observedAbsent >= (preference == BackgroundAppPreference.Unused
            ? TimeSpan.FromMinutes(20) : TimeSpan.FromHours(1));

    // Require three *pre-stop* quiet samples as well. A single elevated CPU
    // baseline can otherwise fabricate an apparent savings even if the
    // post-stop window is internally stable.
    internal static bool IsComparablePreStopWindow(
        int samples, double minCpu, double maxCpu,
        double minAvailableRamMiB, double maxAvailableRamMiB) =>
        IsComparableObservedWindow(samples, minCpu, maxCpu,
            minAvailableRamMiB, maxAvailableRamMiB);

    // These samples are a system-wide correlation signal, NOT a causal
    // A/B/A proof; never present it as confirmed per-app FPS or RAM savings.
    internal static bool MayAcceptFeedbackSample(
        DateTime appliedUtc, DateTime lastSampleUtc,
        DateTime telemetryUtc, DateTime nowUtc) =>
        AiSystemSmoothnessGuardService.IsFresh(telemetryUtc, nowUtc,
            TimeSpan.FromSeconds(65)) &&
        nowUtc - appliedUtc >= TimeSpan.FromMinutes(2) &&
        (lastSampleUtc == default || telemetryUtc - lastSampleUtc >= TimeSpan.FromSeconds(50));

    // A quiet three-sample window must ALSO be internally stable. A single
    // background spike can fabricate a large mean CPU/RAM improvement when
    // compared with one pre-stop reading; classify that as inconclusive.
    internal static bool IsComparableObservedWindow(
        int samples, double? minCpu, double? maxCpu,
        double? minAvailableRamMiB, double? maxAvailableRamMiB) =>
        samples == 3 &&
        minCpu is double cpuMin && maxCpu is double cpuMax &&
        minAvailableRamMiB is double ramMin && maxAvailableRamMiB is double ramMax &&
        double.IsFinite(cpuMin) && double.IsFinite(cpuMax) &&
        double.IsFinite(ramMin) && double.IsFinite(ramMax) &&
        cpuMin >= 0 && cpuMax <= 100 && cpuMax >= cpuMin &&
        ramMin > 0 && ramMax >= ramMin &&
        cpuMax - cpuMin <= 1.5 && ramMax - ramMin <= 96;

    // Passive A/B/A confirmation: baseline A1 -> quiet candidate B ->
    // naturally restored reference A2. This compares actual phases but
    // remains system-wide evidence, not an isolated per-service causal proof.
    internal static bool HasMatchedAbaTrend(
        double baselineCpu, double baselineRamMiB,
        double candidateCpu, double candidateRamMiB,
        double referenceCpu, double referenceRamMiB,
        int referenceSamples, double? refMinCpu, double? refMaxCpu,
        double? refMinRam, double? refMaxRam)
    {
        if (!IsComparableObservedWindow(referenceSamples,
                refMinCpu, refMaxCpu, refMinRam, refMaxRam) ||
            !double.IsFinite(baselineCpu) || !double.IsFinite(baselineRamMiB) ||
            !double.IsFinite(referenceCpu) || !double.IsFinite(referenceRamMiB) ||
            Math.Abs(referenceCpu - baselineCpu) > 0.6 ||
            Math.Abs(referenceRamMiB - baselineRamMiB) > 128)
            return false;
        return HasObservedPositiveTrend(referenceCpu, referenceRamMiB,
            candidateCpu, candidateRamMiB, 3);
    }

    internal static bool DeferServiceRetry(string verdict, DateTime updatedUtc, DateTime nowUtc) =>
        updatedUtc.Kind != DateTimeKind.Utc || updatedUtc > nowUtc ||
        nowUtc - updatedUtc < (verdict switch
        {
            "aba-matched-system-trend" => TimeSpan.FromDays(7),
            "positive-system-trend" => TimeSpan.FromHours(24),
            "no-observed-gain" or "aba-unconfirmed" => TimeSpan.FromHours(24),
            "inconclusive" or "aba-inconclusive" => TimeSpan.FromHours(4),
            _ => TimeSpan.Zero
        });

    internal static bool HasObservedPositiveTrend(
        double baselineCpuPercent, double baselineAvailableRamMiB,
        double candidateCpuPercent, double candidateAvailableRamMiB, int samples)
    {
        if (samples < 3 ||
            !double.IsFinite(baselineCpuPercent) || !double.IsFinite(baselineAvailableRamMiB) ||
            !double.IsFinite(candidateCpuPercent) || !double.IsFinite(candidateAvailableRamMiB) ||
            baselineCpuPercent is < 0 or > 100 || candidateCpuPercent is < 0 or > 100 ||
            baselineAvailableRamMiB < 0 || candidateAvailableRamMiB < 0)
            return false;
        double cpuDrop = baselineCpuPercent - candidateCpuPercent;
        double ramIncrease = candidateAvailableRamMiB - baselineAvailableRamMiB;
        return (cpuDrop >= 0.8 && ramIncrease >= -64) ||
               (ramIncrease >= 96 && cpuDrop >= -0.5);
    }

    // UI explanation stays explicit that CPU/RAM trends are observational.
    internal static string DescribeFeedback(
        string verdict, int samples, double? cpuDrop, double? ramGain) => verdict switch
    {
        "positive-system-trend" when cpuDrop is double cpu && ramGain is double ram =>
            $"Pozorovaný trend CPU {cpu:+0.00;-0.00;0.00} p. b., " +
            $"dostupná RAM {ram:+0;-0;0} MiB ({samples} měření; příčina nepotvrzena)",
        "aba-matched-system-trend" when cpuDrop is double cpu && ramGain is double ram =>
            $"A/B/A: CPU {cpu:+0.00;-0.00;0.00} p. b., RAM {ram:+0;-0;0} MiB " +
            "(opakovaný systémový trend, nikoli důkaz příčiny)",
        "aba-unconfirmed" =>
            "A/B/A nepotvrdilo rozdíl po obnovení služby • další pokus nejdříve za 24 h",
        "aba-inconclusive" =>
            $"A/B/A neúplné ({samples}/3 měření) • příčina nepotvrzena • odklad 4 h",
        "no-observed-gain" =>
            $"Bez měřitelného přínosu ({samples} měření) • obnova • další pokus nejdříve za 24 h",
        "inconclusive" =>
            $"Neprůkazná telemetrie ({samples}/3 měření) • obnova • odklad 4 h",
        "deferred-until-next-startup" =>
            "Omezeno automatické spouštění • úspora při tomto běhu se neměří",
        "deferred-until-task-trigger" =>
            "Odložena volitelná úloha • úspora CPU/RAM zatím nezjištěna",
        _ => "Výsledek AI není ověřen."
    };

    internal static string RunFunctionalSmoke()
    {
        DateTime now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var genuineSpotify = new StartupEntry(
            $"{Microsoft.Win32.RegistryHive.CurrentUser}|{Microsoft.Win32.RegistryView.Registry64}|{run}|Spotify",
            "Spotify", @"""C:\Apps\Spotify\Spotify.exe"" --minimized",
            Microsoft.Win32.RegistryHive.CurrentUser, run, true,
            "Spotify", @"C:\Apps\Spotify\Spotify.exe");
        var fakeUpdater = genuineSpotify with
        {
            Name = "Spotify updater",
            FilePath = @"C:\Apps\Spotify\Update.exe"
        };
        if (!IsFailedOffRestoreCoolingDown(false, 1,
                now.AddSeconds(-29), now) ||
            IsFailedOffRestoreCoolingDown(false, 1,
                now.AddSeconds(-30), now) ||
            IsFailedOffRestoreCoolingDown(true, 1,
                now.AddSeconds(-1), now) ||
            IsFailedOffRestoreCoolingDown(false, 0,
                now.AddSeconds(-1), now) ||
            IsFailedOffRestoreCoolingDown(false, 2,
                now.AddSeconds(1), now) ||
            IsFailedOffRestoreCoolingDown(false, 2,
                DateTime.SpecifyKind(now, DateTimeKind.Local), now) ||
            !CanThrottleDisabledIdleTick(false, 0, 0,
                now.AddSeconds(-29), now) ||
            CanThrottleDisabledIdleTick(false, 0, 0,
                now.AddSeconds(-30), now) ||
            CanThrottleDisabledIdleTick(true, 0, 0,
                now.AddSeconds(-1), now) ||
            CanThrottleDisabledIdleTick(false, 1, 0,
                now.AddSeconds(-1), now) ||
            CanThrottleDisabledIdleTick(false, 0, 1,
                now.AddSeconds(-1), now) ||
            CanThrottleDisabledIdleTick(false, 0, 0,
                now.AddSeconds(1), now) ||
            CanThrottleDisabledIdleTick(false, 0, 0, default, now) ||
            NeedsStartupInventory(false, false) ||
            !NeedsStartupInventory(false, true) ||
            !NeedsStartupInventory(true, false) ||
            NeedsStartupInventory(true, false, false) ||
            !NeedsStartupInventory(false, true, false) ||
            !NeedsStartupInventory(true, false, true) ||
            NeedsStartupInventory(
                MayIntroduceChange(true, false, true, 0, false, false),
                false) ||
            NeedsStartupInventory(
                MayIntroduceChange(true, true, false, 3, false, false),
                false) ||
            NeedsStartupInventory(
                MayIntroduceChange(true, true, false, 0, true, false),
                false) ||
            NeedsStartupInventory(
                MayIntroduceChange(true, true, false, 0, false, true),
                false) ||
            !NeedsStartupInventory(
                MayIntroduceChange(true, false, true, 3, true, true),
                true) ||
            !NeedsStartupInventory(
                MayIntroduceChange(true, true, false, 0, false, false),
                false) ||
            !CanSkipUnownedInventory(true, false, true, 0, 0) ||
            !CanSkipUnownedInventory(true, true, false, 0, 0) ||
            CanSkipUnownedInventory(true, true, true, 0, 0) ||
            CanSkipUnownedInventory(false, false, false, 0, 0) ||
            CanSkipUnownedInventory(true, false, true, 1, 0) ||
            CanSkipUnownedInventory(true, false, true, 0, 1) ||
            CanSkipUnownedInventory(true, true, false, 1, 0) ||
            CanSkipUnownedInventory(true, true, false, 0, 1) ||
            NeedsFirefoxPresence(false, true, true, true, true) ||
            !NeedsFirefoxPresence(true, true, false, false, false) ||
            NeedsFirefoxPresence(true, false, true, false, true) ||
            NeedsFirefoxPresence(true, false, true, true, false) ||
            NeedsFirefoxPresence(true, false, false, true, true) ||
            !NeedsFirefoxPresence(true, false, true, true, true) ||
            IsOwnedRecoveryProbeDue(0, default, now) ||
            !IsOwnedRecoveryProbeDue(1, default, now) ||
            IsOwnedRecoveryProbeDue(1, now.AddSeconds(-9), now) ||
            !IsOwnedRecoveryProbeDue(1, now.AddSeconds(-10), now) ||
            !IsOwnedRecoveryProbeDue(3, now.AddSeconds(1), now) ||
            !MayObserveReference(true, true, true) ||
            MayObserveReference(true, false, true) ||
            MayObserveReference(false, true, true) ||
            MayObserveReference(true, true, false) ||
            !CanAccumulateIdleEvidence(true, true) ||
            CanAccumulateIdleEvidence(true, false) ||
            CanAccumulateIdleEvidence(false, true) ||
            CanAccumulateIdleEvidence(false, false) ||
            !MayKeepOwnedHold("service", true, true, true, now.AddHours(-5), now) ||
            MayKeepOwnedHold("service", true, false, true, now, now) ||
            MayKeepOwnedHold("task", true, true, false, now, now) ||
            MayKeepOwnedHold("startup", false, true, true, now, now) ||
            MayKeepOwnedHold("service", true, true, true, now.AddHours(-6), now) ||
            !MayKeepOwnedHold("startup", true, true, true, now.AddHours(-7), now) ||
            !MayKeepOwnedHold("startup", true, true, true, now.AddDays(-2), now) ||
            MayKeepOwnedHold("startup", true, true, true,
                DateTime.SpecifyKind(default, DateTimeKind.Utc), now) ||
            MayKeepOwnedHold("task", true, true, true, now.AddHours(-7), now) ||
            MayKeepOwnedHold("startup", true, true, true, now.AddMinutes(1), now) ||
            MayKeepOwnedHold("startup", true, true, true,
                DateTime.SpecifyKind(now, DateTimeKind.Local), now) ||
            !MayKeepOwnedHold("startup", true, false, false, now, now) ||
            !IsContinuousObservation(now.AddMinutes(-3), now) ||
            !IsContinuousObservation(now.AddMinutes(-5), now) ||
            IsContinuousObservation(now.AddMinutes(-6), now) ||
            IsContinuousObservation(now.AddMinutes(-11), now) ||
            IsContinuousObservation(now.AddMinutes(1), now) ||
            IsContinuousObservation(default, now) ||
            !MayRetryJournalRead(default, now) ||
            MayRetryJournalRead(now.AddSeconds(-30), now) ||
            !MayRetryJournalRead(now.AddMinutes(-1), now) ||
            !MayRetryJournalRead(now.AddMinutes(1), now) ||
            !MayRestoreServiceStart(true, ServiceControllerStatus.Stopped, 3, 3) ||
            !MayRestoreServiceStart(true, ServiceControllerStatus.Stopped, 2, null) ||
            MayRestoreServiceStart(true, ServiceControllerStatus.Stopped, 4, 3) ||
            MayRestoreServiceStart(true, ServiceControllerStatus.Stopped, 2, 3) ||
            MayRestoreServiceStart(true, ServiceControllerStatus.Running, 3, 3) ||
            MayRestoreServiceStart(false, ServiceControllerStatus.Stopped, 3, 3) ||
            !MayRestoreFirefoxTask(
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB",
                true, false) ||
            MayRestoreFirefoxTask(
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB",
                true, true) ||
            MayRestoreFirefoxTask(
                @"\Mozilla\Firefox Update Agent 308046B0AF4A39CB",
                true, false) ||
            !HasOnlyStoppedDependents(Array.Empty<ServiceControllerStatus?>()) ||
            !HasOnlyStoppedDependents(new ServiceControllerStatus?[]
                { ServiceControllerStatus.Stopped }) ||
            HasOnlyStoppedDependents(new ServiceControllerStatus?[]
                { ServiceControllerStatus.Paused }) ||
            HasOnlyStoppedDependents(new ServiceControllerStatus?[]
                { ServiceControllerStatus.Running }) ||
            HasOnlyStoppedDependents(new ServiceControllerStatus?[] { null }) ||
            !BackgroundActivityService.HasExactAiInventoryScope() ||
            !BackgroundActivityService.RunAiPreferenceSnapshotSmoke() ||
            StartupManagerService.NeedsPublisherMetadata(aiInventory: true) ||
            !StartupManagerService.NeedsPublisherMetadata(aiInventory: false) ||
            BackgroundProcessSnapshotService.ShouldReadResourceCounters(true) ||
            !BackgroundProcessSnapshotService.ShouldReadResourceCounters(false) ||
            MaySampleStoppedHelper(ServiceControllerStatus.Running) ||
            MaySampleStoppedHelper(ServiceControllerStatus.Paused) ||
            MaySampleStoppedHelper(null) ||
            !MaySampleStoppedHelper(ServiceControllerStatus.Stopped) ||
            !MayTouchApp(new UserFeatureProfile(Array.Empty<string>(), now), "spotify") ||
            MayTouchApp(new UserFeatureProfile(new[] { "bg:spotify:keep" }, now), "spotify") ||
            MayTouchApp(new UserFeatureProfile(Array.Empty<string>(), now)
                { IsUntrusted = true }, "spotify") ||
            MayTouchApp(new UserFeatureProfile(Array.Empty<string>(), now), "") ||
            !CanManageStartup("spotify", genuineSpotify) ||
            !CanManageStartup("steam", genuineSpotify with
            {
                FilePath = @"C:\Apps\Steam\steam.exe", Name = "Steam",
                Command = @"""C:\Apps\Steam\steam.exe"" -silent"
            }) ||
            CanManageStartup("spotify", fakeUpdater) ||
            CanManageStartup("spotify", genuineSpotify with { Name = "Spotify updater" }) ||
            CanManageStartup("spotify", genuineSpotify with
                { RunPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce" }) ||
            CanManageStartup("spotify", genuineSpotify with
                { Hive = Microsoft.Win32.RegistryHive.LocalMachine }) ||
            CanManageStartup("spotify", genuineSpotify with { Enabled = false }) ||
            !AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(genuineSpotify) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Command = null! }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Command = "  " }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { View = Microsoft.Win32.RegistryView.Default }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Id = null! }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Id = "" }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Id = "startup|foreign" }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Name = "Foreign" }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { View = Microsoft.Win32.RegistryView.Registry32 }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(
                genuineSpotify with { Enabled = false }) ||
            AiThirdPartyIdleAutomationService.CanCommitOwnedStartup(null) ||
            CanManageStartup("spotify", genuineSpotify with
                { FilePath = "Spotify.exe" }) ||
            CanManageStartup("discord", genuineSpotify) ||
            !MatchesOptionalFirefoxTask(
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB") ||
            MatchesOptionalFirefoxTask(
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CZ") ||
            MatchesOptionalFirefoxTask(
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB extra") ||
            MatchesOptionalFirefoxTask(@"\Mozilla\Firefox Update Agent 308046B0AF4A39CB"))
            throw new InvalidOperationException("AI third-party startup/task identity regressed.");
        if (!MayIntroduceChange(true, true, false, 0, false, false) ||
            !MayIntroduceChange(true, true, false, 2, false, false) ||
            MayIntroduceChange(true, true, false, 3, false, false) ||
            MayIntroduceChange(true, true, true, 0, false, false) ||
            MayIntroduceChange(true, false, false, 0, false, false) ||
            MayIntroduceChange(false, true, false, 0, false, false) ||
            MayIntroduceChange(true, true, false, 0, true, false) ||
            MayIntroduceChange(true, true, false, 0, false, true) ||
            !OwnedPresenceRequiresRestore(false, false) ||
            !OwnedPresenceRequiresRestore(false, true) ||
            !OwnedPresenceRequiresRestore(true, true) ||
            OwnedPresenceRequiresRestore(true, false) ||
            // Late CPU/game/disk quiet-evidence loss must deny the LAST
            // prewrite gate for each of the three optional native lanes.
            !AiThirdPartyIdleAutomationService.MayMakeNewIdleNativeWrite(
                new AiSystemRuntimeConfig(), "startup", true) ||
            !AiThirdPartyIdleAutomationService.MayMakeNewIdleNativeWrite(
                new AiSystemRuntimeConfig(), "service", true) ||
            !AiThirdPartyIdleAutomationService.MayMakeNewIdleNativeWrite(
                new AiSystemRuntimeConfig(), "task", true) ||
            AiThirdPartyIdleAutomationService.MayMakeNewIdleNativeWrite(
                new AiSystemRuntimeConfig(), "startup", false) ||
            AiThirdPartyIdleAutomationService.MayMakeNewIdleNativeWrite(
                new AiSystemRuntimeConfig(ServicesEnabled: false), "service", true) ||
            AiThirdPartyIdleAutomationService.MayMakeNewIdleNativeWrite(
                new AiSystemRuntimeConfig(ScheduledTasksEnabled: false), "task", true) ||
            CanReduce(TimeSpan.FromHours(5), BackgroundAppPreference.Keep, false, false, true) ||
            CanReduce(TimeSpan.FromHours(5), BackgroundAppPreference.Unknown, true, false, true) ||
            CanReduce(TimeSpan.FromHours(5), BackgroundAppPreference.Unknown, false, true, true) ||
            CanReduce(TimeSpan.FromHours(5), BackgroundAppPreference.Unknown, false, false, false) ||
            CanReduce(TimeSpan.FromMinutes(59), BackgroundAppPreference.Unknown, false, false, true) ||
            !CanReduce(TimeSpan.FromMinutes(60), BackgroundAppPreference.Unknown, false, false, true) ||
            !CanReduce(TimeSpan.FromMinutes(20), BackgroundAppPreference.Unused, false, false, true) ||
            AppKeys.Length < 12 ||
            !AppKeys.Contains("steam", StringComparer.OrdinalIgnoreCase) ||
            !AppKeys.Contains("spotify", StringComparer.OrdinalIgnoreCase) ||
            Services.Length != 1 || Tasks.Length != 1 ||
            AppKeys.Contains("chrome", StringComparer.OrdinalIgnoreCase) ||
            Services.Any(x => x.Name.Contains("update", StringComparison.OrdinalIgnoreCase)) ||
            Tasks.Any(x => x.Prefix.Contains("Update", StringComparison.OrdinalIgnoreCase)) ||
            HasObservedPositiveTrend(8, 8000, 5, 8200, 2) ||
            !HasObservedPositiveTrend(8, 8000, 6.5, 7990, 3) ||
            !HasObservedPositiveTrend(8, 8000, 8.2, 8130, 3) ||
            HasObservedPositiveTrend(8, 8000, 8, 8000, 3) ||
            HasObservedPositiveTrend(double.NaN, 8000, 2, 9000, 3) ||
            HasObservedPositiveTrend(8, 8000, 6, 7000, 3) ||
            !IsComparableObservedWindow(3, 4, 5.2, 8190, 8220) ||
            !IsComparablePreStopWindow(3, 4, 5.2, 8190, 8220) ||
            IsComparablePreStopWindow(2, 4, 5.2, 8190, 8220) ||
            IsComparablePreStopWindow(3, 4, 6, 8190, 8220) ||
            IsComparablePreStopWindow(3, 4, 5.2, 8100, 8300) ||
            !RunStartupExecutableAllocationParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunPreStopEvidenceSmoke() ||
            !AiThirdPartyIdleAutomationService.RunOwnedProbeBitsetSmoke() ||
            !AiThirdPartyIdleAutomationService.RunSteamRetryLookupParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunRestorePrioritySmoke() ||
            !AiThirdPartyIdleAutomationService.RunOwnedReferenceEnumerationSmoke() ||
            !AiThirdPartyIdleAutomationService.RunOwnedPresenceParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupDirectLookupParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupDualStateRestore164Smoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupOccupiedDestination165Smoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupRestoreSlotCollision166Smoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupExternalResolution168Smoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupRegistryRace180Smoke() ||
            !AiThirdPartyIdleAutomationService.RunStartupRegistryAdmission181Smoke() ||
            !TaskSchedulerService.RunTaskMutationSafety187Smoke() ||
            !TaskSchedulerComService.RunTaskRollbackOwnership188Smoke() ||
            !TaskSchedulerService.RunTaskBatchConflict189Smoke() ||
            !TaskSchedulerComService.RunTaskMissingVsUnreadable190Smoke() ||
            !PowerPlanGuardService.RunOwnedGuardTask191Smoke() ||
            !PerformanceProfilesService.RunDuplicatePowerPlanPreflight203Smoke() ||
            !PerformanceProfilesService.RunPerformanceApplyPreflight204Smoke() ||
            !PerformanceProfilesService.RunPerformanceGuardCommit205Smoke() ||
            !AiThirdPartyIdleAutomationService.RunJournalIdentitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunJournalAppIndexParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunOwnedJournalValidationParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunUtf8JournalWriteParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunBoundedJournalByteSliceSmoke() ||
            !AiThirdPartyIdleAutomationService.RunLearningRepairSmoke() ||
            !AiThirdPartyIdleAutomationService.RunLearningNormalizationParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunFeedbackProjectionSmoke() ||
            !AiThirdPartyIdleAutomationService.RunFeedbackTopThreeParitySmoke() ||
            !AiThirdPartyIdleAutomationService.RunFeedbackRetentionSmoke() ||
            !AiThirdPartyIdleAutomationService.RunFailedJournalWriteSmoke() ||
            !AiThirdPartyIdleAutomationService.RunPhaseTimingSmoke() ||
            !AiThirdPartyIdleAutomationService.RunResourceSampleRecoverySmoke() ||
            !AiThirdPartyIdleAutomationService.RunTelemetryCultureSmoke() ||
            !HasMatchedAbaTrend(8, 8000, 6.4, 8120, 8.1, 8020, 3,
                7.9, 8.2, 8000, 8030) ||
            HasMatchedAbaTrend(8, 8000, 6.4, 8120, 5, 8020, 3,
                4.9, 5.2, 8000, 8030) ||
            HasMatchedAbaTrend(8, 8000, 6.4, 8120, 8.1, 8300, 3,
                7.9, 8.2, 8280, 8320) ||
            HasMatchedAbaTrend(8, 8000, 6.4, 8120, 8.1, 8020, 2,
                7.9, 8.2, 8000, 8030) ||
            !DeferServiceRetry("aba-matched-system-trend", now.AddDays(-5), now) ||
            DeferServiceRetry("aba-matched-system-trend", now.AddDays(-8), now) ||
            !DeferServiceRetry("aba-unconfirmed", now.AddHours(-6), now) ||
            DeferServiceRetry("aba-unconfirmed", now.AddHours(-25), now) ||
            !DeferServiceRetry("aba-inconclusive", now.AddHours(-3), now) ||
            DeferServiceRetry("aba-inconclusive", now.AddHours(-5), now) ||
            !DescribeFeedback("aba-inconclusive", 1, null, null).Contains("neúplné") ||
            !DescribeFeedback("aba-matched-system-trend", 3, 1.3, 120).Contains("A/B/A") ||
            IsComparableObservedWindow(2, 4, 5.2, 8190, 8220) ||
            IsComparableObservedWindow(3, 4, 6.1, 8190, 8220) ||
            IsComparableObservedWindow(3, 4, 5.2, 8000, 8200) ||
            IsComparableObservedWindow(3, 4, double.NaN, 8190, 8220) ||
            !DescribeFeedback("positive-system-trend", 3, 1.2, 140)
                .Contains("příčina nepotvrzena") ||
            !DescribeFeedback("no-observed-gain", 3, -0.2, 0).Contains("24 h") ||
            !DescribeFeedback("inconclusive", 2, null, null).Contains("odklad 4 h") ||
            DescribeFeedback("deferred-until-next-startup", 0, null, null)
                .Contains("pozorovaný trend") ||
            MayAcceptFeedbackSample(now.AddMinutes(-1), default, now, now) ||
            MayAcceptFeedbackSample(now.AddMinutes(-5), now.AddSeconds(-30), now, now) ||
            !MayAcceptFeedbackSample(now.AddMinutes(-5), now.AddMinutes(-2), now, now) ||
            MayAcceptFeedbackSample(now.AddMinutes(-5), default, now.AddMinutes(-2), now))
            throw new InvalidOperationException("AI app inactivity decision regressed.");
        return "AI_THIRD_PARTY_IDLE_SMOKE_OK unused20m unknown60m keepNever=true " +
            "runningRestored=true gameDeferred=true updatersUntouched=true " +
            "cpuRamThreeSampleTrend=true comparableWindow=true feedbackFreshness=true " +
            "feedbackDescriptions=true passiveAbaMatched=true retryLearning=true " +
            "feedbackTop3StableBounded=true feedbackPruneNoSnapshot=true " +
            "learningOrderedSinglePassFastPath=true a2ReferencesDirectValidation=true " +
            "isolatedMutations=true measuredStopRequiresBaseline=true " +
            "strictStartupIdentity=true optionalTaskHexIdentity=true " +
            "startupExecutableSpanNoCopy=true canonicalStaticAppNoLowerAllocation=true " +
            "targetedProcessRecheck=true taskReadback=true " +
            "lateKeepPreferenceGuard=true abortedPrewriteOwnershipClean=true " +
            "journalReadRetry=true nativeStoppedServiceFeedback=true " +
            "subFeatureRestore=true invalidHoldTimeRollback=true " +
            "configChangeBypassesThrottle=true absenceGapResets=true " +
            "stableThreeSamplePreStopBaseline=true baselineSpikeRejected=true " +
            "urgentOwnedRecoveryProbe10s=true recoveryFailureNoStarvation=true " +
            "allocationFreeDistinctRecoveryApps=true " +
            "ownerProbeBitsetNoDuplicateNativeChecks=true steamCooldownDirectLookup=true " +
            "stableThreePhaseRecoveryNoSortAllocation=true " +
            "ownedAdmissionNoLinq=true a2ReferenceNoArraySnapshot=true " +
            "leasedOwnerPresenceSinglePass=true ownerAppBitset=true " +
            "firefoxPresenceReusesAiSnapshot=true prewriteRecheckPreserved=true " +
            "referenceCancelledOnServicesOff=true aiOff30sNoScan=true " +
            "failedLeaseFastRetry=true selectiveAiOffInventory=true " +
            "crossHostJournalStamp10s=true strictPersistedOwnerIdentity=true " +
            "restoreStartupExecutableRecheck=true perf15mBounded=true " +
            "monotonicTickDurationMs=true diagnosticExceptionIsolated=true " +
            "startupRestoreNativeReadback=true aiPresenceOnlyNoCpuRamCounters=true " +
            "startupRecoveryNoLinqPredicates=true syntheticStartupParity=true " +
            "startupDisableReadbackNoLinq=true " +
            "startupAdmissionAndPrewriteNoLinq=true " +
            "scmDependentsDisposed=true pausedDependentsBlockStop=true " +
            "narrowHkcuRunInventory=true aiInventoryOnlyManagedApps=true " +
            "aiStartupPublisherMetadataSkipped=true fullUiCountersPreserved=true " +
            "onePreferenceSnapshotPerScan=true failedInventoryRetriesNextTick=true " +
            "untrustedPrefsSkipHkcuInventory=true ownedStartupStillRestored=true " +
            "skippedNativeInventoriesReported15m=true " +
            "unownedBusyInventoryShortCircuit=true " +
            "untrustedPrefsAppInventorySkipped=true " +
            "ownedAndReferenceRecoveryNotSkipped=true " +
            "skippedIdleCreditReset=true " +
            "offRecovery30sBackoff=true exactStartupCommandRecheck=true " +
            "serviceStartModeExternalOverridePreserved=true " +
            "firefoxTaskRunningRestoreBlocked=true " +
            "actualJournalBytesBounded256KiB=true feedbackValidated=true " +
            "referenceValidated=true repairedLearningPreservesOwner=true " +
            "writeBounded256KiB=true staleJournalOverwriteBlocked=true " +
            "feedbackResourceIdentity=true referenceSampleFinite=true " +
            "uiProjectionValidatesFeedback=true missingJournalFailClosed=true " +
            "failedCommitForcesLeasedReload=true tempCleanupNonMasking=true " +
            "sha256SameStampTamperBlocked=true noExtraProbeHashes=true " +
            "singleUtf8JournalBuffer=true boundedHashOnlyConflict=true " +
            "uiJournalDecodeWithoutUnusedDigest=true singlePassOwnerValidation=true " +
            "journalReadZeroBufferCopy=true journalExactSliceSha256=true " +
            "journalSeekableOneAllocation=true ownerProbeSharedKeyIndex=true " +
            "journalTrustedAppSharedIndex=true steamJournalKeyReused=true " +
            "strictUtf8DecoderReused=true " +
            "duplicateJsonOwnerKeysRejectedBeforeDeserialize=true " +
            "phaseAvgMaxMsNoExtraInventory=true " +
            "cpuIntervalAllCoresNoExtraSample=true " +
            "trustedResourceTelemetrySharedAcrossA_B_A2=true " +
            "completedThreeSampleOwnerNeverOversampled=true " +
            "verdictResourceSampleConsistency=true " +
            "perfLogInvariantDecimals=true phaseNoScanReportedNA=true " +
            "monotonicCpuWindow=true noGainDeferred=true writes=0";
    }
}

internal sealed class AiThirdPartyIdleAutomationService
{
    // This exception is reserved exclusively for admission checks that run
    // BEFORE any Windows write. Only these failures may safely discard the
    // write-ahead owner; unknown/partial mutations must keep recovery.
    private sealed class PrewriteAdmissionException(string message)
        : InvalidOperationException(message);

    private sealed record Owner(
        string Kind, string Id, string AppKey, DateTime AppliedUtc,
        bool OriginalEnabled, bool OriginallyRunning,
        string StartupName = "", string StartupCommand = "",
        string StartupRunPath = "", int StartupView = 0, bool Pending = true,
        double? BaselineCpuPercent = null, double? BaselineRamMiB = null,
        int FeedbackSamples = 0, double SumCpuPercent = 0, double SumRamMiB = 0,
        DateTime LastFeedbackSampleUtc = default,
        double? MinCpuPercent = null, double? MaxCpuPercent = null,
        double? MinRamMiB = null, double? MaxRamMiB = null,
        int? OriginalServiceStartMode = null);
    private sealed record FeedbackOutcome(
        DateTime UpdatedUtc, string Verdict, int Samples,
        double? CpuDropPercentPoints = null, double? AvailableRamGainMiB = null,
        string AppKey = "");
    private sealed record ReferenceTrial(
        string Id, string AppKey, DateTime RestoredUtc,
        double BaselineCpu, double BaselineRamMiB,
        double CandidateCpu, double CandidateRamMiB,
        int Samples = 0, double CpuSum = 0, double RamSum = 0,
        DateTime LastSampleUtc = default,
        double? MinCpu = null, double? MaxCpu = null,
        double? MinRamMiB = null, double? MaxRamMiB = null);
    private sealed record Journal(
        Dictionary<string, Owner> Owned,
        Dictionary<string, FeedbackOutcome>? Feedback = null,
        Dictionary<string, ReferenceTrial>? References = null);
    private static readonly string JournalPath =
        Path.Combine(LogService.Root, "ai-third-party-idle-ownership.json");
    // Strict UTF-8 decoder is stateless for these one-shot GetString calls;
    // do not allocate a new Encoding on every journal load or UI visit.
    private static readonly UTF8Encoding StrictJournalUtf8 = new(false, true);
    private readonly Dictionary<string, Owner> _owned =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FeedbackOutcome> _feedback =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReferenceTrial> _references =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _absentSince =
        new(StringComparer.OrdinalIgnoreCase);
    // Only a few reusable samples from existing cached FanAgent telemetry.
    // This is local provisional evidence, never a new native polling timer.
    private readonly Queue<(DateTime AtUtc, double Cpu, double RamMiB)>
        _steamPreStopSamples = new();
    private readonly Dictionary<string, DateTime> _externallyOverriddenUntil =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _journalHealthy = true;
    private bool _learningRepairPending;
    private DateTime _lastJournalErrorUtc;
    private DateTime _lastScanUtc = DateTime.MinValue;
    // Last validated journal identity. Poll only metadata between ordinary
    // scans to discover a second RuntimeHost's newly owned resources.
    private bool _journalStampKnown;
    private bool _journalStampExists;
    private long _journalStampLength;
    private DateTime _journalStampWriteUtc;
    // Digest is only evaluated during a validated load or a real journal
    // commit, never during the periodic 10-second metadata-only probe.
    private byte[]? _journalDigest;
    private DateTime _lastJournalMetadataProbeUtc;
    private DateTime _lastUrgentProbeUtc;
    private DateTime _lastObservationUtc;
    // GUI and RuntimeHost are separate processes. Keep only the last
    // validated preference object so a changed KEEP choice can break an
    // idle window without waiting for the full Windows inventory.
    private UserFeatureProfile? _lastObservedFeatureProfile;
    private DateTime _lastFailedOffRestoreUtc;
    private bool? _lastAiEnabled;
    private bool? _lastServicesEnabled;
    private bool? _lastTasksEnabled;

    // Low-overhead operational measurements, one aggregate report every
    // 15 minutes. Never create a sampler thread or log on individual ticks.
    private readonly object _perfGate = new();
    private DateTime _perfReportAtUtc = DateTime.UtcNow;
    private long _perfTicks, _perfWallTicks, _perfMaxTicks;
    private long _perfLeaseAcquired, _perfStartupScans, _perfAppScans;
    // No extra probing: count policy decisions made by existing cycles so
    // real users can verify avoided native HKCU/Firefox enumerations.
    private long _perfStartupSkips, _perfFirefoxScans, _perfFirefoxSkips;
    private long _perfAppInventorySkips;
    // Timings are Stopwatch deltas from EXISTING native reads, not extra
    // scans or CPU sampling. Interlocked counters need no per-tick objects.
    private long _perfJournalLoadTicks, _perfJournalLoadMaxTicks;
    private long _perfStartupLoadTicks, _perfStartupLoadMaxTicks;
    private long _perfAppLoadTicks, _perfAppLoadMaxTicks;
    private double _perfLastCpuTotalSec = double.NaN;
    // Monotonic CPU interval denominator: wall-clock adjustments (time sync
    // or daylight saving) cannot fabricate CPU spikes or negative usage.
    private long _perfLastCpuClockTicks;

    // Read only the tiny persistent outcome summary on navigation to the AI
    // page; no new background poll and no Windows service/task inspection.
    internal static AiThirdPartyFeedbackView[] ReadFeedbackForUi()
    {
        try
        {
            var file = new FileInfo(JournalPath);
            if (!file.Exists || file.Length is <= 0 or > 262144)
                return Array.Empty<AiThirdPartyFeedbackView>();
            using var stream = File.OpenRead(JournalPath);
            Journal? journal = DeserializeJournalStrict(
                ReadBoundedJournalText(stream));
            return SelectTrustedFeedbackForUi(journal, DateTime.UtcNow);
        }
        catch (Exception)
        {
            // An in-progress journal swap must not break AI navigation.
            return Array.Empty<AiThirdPartyFeedbackView>();
        }
    }

    // The UI is read-only, but untrusted auxiliary feedback should not
    // appear as a valid optimization result. Reuse precisely the same
    // identity validation as durable learning recovery, without disk writes.
    private static AiThirdPartyFeedbackView[] SelectTrustedFeedbackForUi(
        Journal? journal, DateTime now)
    {
        if (!HasValidOwnedJournal(journal?.Owned) ||
            journal.Feedback is null || journal.Feedback.Count is 0 or > 256)
            return Array.Empty<AiThirdPartyFeedbackView>();

        // UI shows at most three records: a stable bounded top-3 scan
        // avoids LINQ filtering, sorting up to 256 entries, DistinctBy's
        // transient HashSet and several iterators on AI navigation.
        // Keep the old ordering: newest first, equal-time ties in original
        // journal iteration order, distinct resource identity ignoring case.
        var newest = new KeyValuePair<string, FeedbackOutcome>[3];
        int count = 0;
        foreach (var candidate in journal.Feedback)
        {
            if (!IsValidPersistedFeedback(candidate.Key, candidate.Value) ||
                candidate.Value.UpdatedUtc > now)
                continue;
            // The optional startup can remain AI-owned across future logins.
            // Do not hide its honest "measure next startup" feedback after
            // fourteen days while the exact durable owner is still active.
            // Ordinary historical outcomes and stale completed trials still
            // expire, without any extra process/registry probe on navigation.
            if (now - candidate.Value.UpdatedUtc > TimeSpan.FromDays(14) &&
                !(candidate.Value.Verdict == "deferred-until-next-startup" &&
                  journal.Owned!.TryGetValue(candidate.Key, out Owner? activeOwner) &&
                  activeOwner.Kind == "startup" && !activeOwner.Pending &&
                  string.Equals(activeOwner.AppKey, candidate.Value.AppKey,
                      StringComparison.OrdinalIgnoreCase)))
                continue;
            int duplicate = -1;
            for (int i = 0; i < count; i++)
            {
                if (candidate.Key.Equals(newest[i].Key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    duplicate = i;
                    break;
                }
            }
            if (duplicate >= 0)
            {
                // Earlier entries win an equal-time tie, like LINQ's
                // stable OrderByDescending followed by DistinctBy.
                if (candidate.Value.UpdatedUtc <= newest[duplicate].Value.UpdatedUtc)
                    continue;
                for (int i = duplicate; i + 1 < count; i++)
                    newest[i] = newest[i + 1];
                count--;
            }
            int insert = 0;
            while (insert < count &&
                   candidate.Value.UpdatedUtc <= newest[insert].Value.UpdatedUtc)
                insert++;
            if (insert >= newest.Length) continue;
            if (count < newest.Length) count++;
            for (int i = count - 1; i > insert; i--)
                newest[i] = newest[i - 1];
            newest[insert] = candidate;
        }
        if (count == 0) return Array.Empty<AiThirdPartyFeedbackView>();
        var result = new AiThirdPartyFeedbackView[count];
        for (int i = 0; i < count; i++)
        {
            var entry = newest[i];
            int split = entry.Key.IndexOf('|');
            string kind = entry.Key[..split];
            string id = entry.Key[(split + 1)..];
            var value = entry.Value;
            result[i] = new AiThirdPartyFeedbackView(
                kind, id, value.AppKey, value.UpdatedUtc, value.Verdict,
                AiThirdPartyIdlePolicy.DescribeFeedback(value.Verdict,
                    value.Samples, value.CpuDropPercentPoints,
                    value.AvailableRamGainMiB));
        }
        return result;
    }

    // Stopwatch uses monotonic ticks; no wall-clock skew or fake
    // whole-system FPS/CPU savings. The per-tick path only measures time.
    internal static double ElapsedMilliseconds(long stopwatchTicks) =>
        stopwatchTicks >= 0
            ? stopwatchTicks * 1000.0 / Stopwatch.Frequency
            : 0;

    // Monotonic timing aggregation for one phase of an already-required
    // inventory read. Safe with concurrent RuntimeHost callbacks.
    private static void RecordPhaseTicks(
        ref long totalTicks, ref long maxTicks, long elapsedTicks)
    {
        if (elapsedTicks < 0) return;
        Interlocked.Add(ref totalTicks, elapsedTicks);
        long prior = Volatile.Read(ref maxTicks);
        while (elapsedTicks > prior)
        {
            long replaced = Interlocked.CompareExchange(
                ref maxTicks, elapsedTicks, prior);
            if (replaced == prior) break;
            prior = replaced;
        }
    }

    private static (double DeltaSec, double AllCoresPct) CpuIntervalUsage(
        double previousCpuSec, double currentCpuSec, double wallSeconds,
        int logicalProcessors)
    {
        if (!double.IsFinite(previousCpuSec) ||
            !double.IsFinite(currentCpuSec) || previousCpuSec < 0 ||
            currentCpuSec < previousCpuSec ||
            !double.IsFinite(wallSeconds) || wallSeconds <= 0 ||
            logicalProcessors <= 0)
            return (double.NaN, double.NaN);
        double delta = currentCpuSec - previousCpuSec;
        return (delta, Math.Clamp(
            100.0 * delta / (wallSeconds * logicalProcessors), 0, 100));
    }

    internal static bool RunPhaseTimingSmoke()
    {
        long total = 0, maximum = 0;
        RecordPhaseTicks(ref total, ref maximum, -1);
        RecordPhaseTicks(ref total, ref maximum, 100);
        RecordPhaseTicks(ref total, ref maximum, 50);
        RecordPhaseTicks(ref total, ref maximum, 250);
        RecordPhaseTicks(ref total, ref maximum, 10);
        var usage = CpuIntervalUsage(12, 14, 100, 8);
        return total == 410 && maximum == 250 &&
            Math.Abs(ElapsedMilliseconds(Stopwatch.Frequency) - 1000) < 0.000001 &&
            Math.Abs(usage.DeltaSec - 2) < 0.000001 &&
            Math.Abs(usage.AllCoresPct - 0.25) < 0.000001 &&
            double.IsNaN(CpuIntervalUsage(double.NaN, 14, 100, 8).DeltaSec) &&
            double.IsNaN(CpuIntervalUsage(14, 12, 100, 8).AllCoresPct) &&
            double.IsNaN(CpuIntervalUsage(12, 14, 0, 8).DeltaSec) &&
            double.IsNaN(CpuIntervalUsage(12, 14, 100, 0).DeltaSec);
    }

    // Performance logs use invariant numeric tokens even when the machine
    // runs with a Czech decimal comma. One log per 15min, no hot-path cost.
    private static string PerfNumber(double value, string format = "0.000") =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string PerfNumberOrNa(double value) =>
        double.IsFinite(value) ? PerfNumber(value) : "na";

    // A phase that did NOT run in this reporting interval is unavailable,
    // not a fabricated zero-millisecond measurement.
    private static string PhaseAverageOrNa(long totalTicks, long count) =>
        count > 0 ? PerfNumber(ElapsedMilliseconds(totalTicks) / count) : "na";

    private static string PhaseMaximumOrNa(long maxTicks, long count) =>
        count > 0 ? PerfNumber(ElapsedMilliseconds(maxTicks)) : "na";

    private static double MonotonicElapsedSeconds(long previous, long current) =>
        previous > 0 && current > previous
            ? (double)(current - previous) / Stopwatch.Frequency
            : double.NaN;

    internal static bool RunTelemetryCultureSmoke() =>
        PerfNumber(1.25) == "1.250" &&
        PerfNumber(1.26, "0.0") == "1.3" &&
        PerfNumberOrNa(double.NaN) == "na" &&
        PerfNumberOrNa(double.PositiveInfinity) == "na" &&
        PhaseAverageOrNa(0, 0) == "na" &&
        PhaseMaximumOrNa(0, 0) == "na" &&
        PhaseAverageOrNa(Stopwatch.Frequency, 2) == "500.000" &&
        PhaseMaximumOrNa(Stopwatch.Frequency, 1) == "1000.000" &&
        double.IsNaN(MonotonicElapsedSeconds(0, Stopwatch.Frequency)) &&
        double.IsNaN(MonotonicElapsedSeconds(100, 99)) &&
        Math.Abs(MonotonicElapsedSeconds(
            Stopwatch.Frequency, Stopwatch.Frequency * 3) - 2) < 1e-7 &&
        double.IsFinite(ElapsedMilliseconds(Stopwatch.Frequency));

    private void RecordTickPerformance(long elapsedTicks)
    {
        // This is OBSERVABILITY, never part of a recovery transaction.
        // Even logger or process metric failures must not mask the original
        // exception/cancellation from TickCoreAsync's finally block.
        try { RecordTickPerformanceCore(elapsedTicks); }
        catch { /* Preserve the original AI outcome without noisy retries. */ }
    }

    private void RecordTickPerformanceCore(long elapsedTicks)
    {
        if (elapsedTicks < 0) return;
        long count, totalTicks, maxTicks, leases, startupScans, appScans;
        long startupSkips, firefoxScans, firefoxSkips, appInventorySkips;
        long journalTicks, journalMax, startupTicks, startupMax, appTicks, appMax;
        DateTime now = DateTime.UtcNow;
        lock (_perfGate)
        {
            _perfTicks++;
            _perfWallTicks += elapsedTicks;
            _perfMaxTicks = Math.Max(_perfMaxTicks, elapsedTicks);
            if (now - _perfReportAtUtc < TimeSpan.FromMinutes(15))
                return;
            count = _perfTicks;
            totalTicks = _perfWallTicks;
            maxTicks = _perfMaxTicks;
            _perfTicks = _perfWallTicks = _perfMaxTicks = 0;
            _perfReportAtUtc = now;
            leases = Interlocked.Exchange(ref _perfLeaseAcquired, 0);
            startupScans = Interlocked.Exchange(ref _perfStartupScans, 0);
            appScans = Interlocked.Exchange(ref _perfAppScans, 0);
            startupSkips = Interlocked.Exchange(ref _perfStartupSkips, 0);
            firefoxScans = Interlocked.Exchange(ref _perfFirefoxScans, 0);
            firefoxSkips = Interlocked.Exchange(ref _perfFirefoxSkips, 0);
            appInventorySkips = Interlocked.Exchange(ref _perfAppInventorySkips, 0);
            journalTicks = Interlocked.Exchange(ref _perfJournalLoadTicks, 0);
            journalMax = Interlocked.Exchange(ref _perfJournalLoadMaxTicks, 0);
            startupTicks = Interlocked.Exchange(ref _perfStartupLoadTicks, 0);
            startupMax = Interlocked.Exchange(ref _perfStartupLoadMaxTicks, 0);
            appTicks = Interlocked.Exchange(ref _perfAppLoadTicks, 0);
            appMax = Interlocked.Exchange(ref _perfAppLoadMaxTicks, 0);
        }

        // One native process snapshot per reporting interval, not per tick.
        // CPU is process-cumulative CPU time, NOT causal optimisation gain.
        try
        {
            using var process = Process.GetCurrentProcess();
            double cpuTotalSec = process.TotalProcessorTime.TotalSeconds;
            double cpuDeltaSec, cpuAllCoresPct;
            long cpuClock = Stopwatch.GetTimestamp();
            lock (_perfGate)
            {
                double elapsedSec = MonotonicElapsedSeconds(
                    _perfLastCpuClockTicks, cpuClock);
                var interval = CpuIntervalUsage(
                    _perfLastCpuTotalSec, cpuTotalSec, elapsedSec,
                    Environment.ProcessorCount);
                cpuDeltaSec = interval.DeltaSec;
                cpuAllCoresPct = interval.AllCoresPct;
                _perfLastCpuTotalSec = cpuTotalSec;
                _perfLastCpuClockTicks = cpuClock;
            }
            // ai-system's high-frequency module INFO is intentionally
            // disabled in normal operation. This single 15-minute summary
            // must instead use the ordinary always-on, bounded async log.
            LogService.Info(
                $"AI_APP_IDLE_RUNTIME_PERF pid={process.Id} ticks={count} " +
                $"avgMs={PerfNumber(ElapsedMilliseconds(totalTicks) / Math.Max(1, count))} " +
                $"maxMs={PerfNumber(ElapsedMilliseconds(maxTicks))} " +
                $"leaseAcquired={leases} startupScans={startupScans} " +
                $"appScans={appScans} appSkipped={appInventorySkips} startupSkipped={startupSkips} " +
                $"firefoxChecks={firefoxScans} firefoxSkipped={firefoxSkips} " +
                $"journalAvgMs={PhaseAverageOrNa(journalTicks, leases)} " +
                $"journalMaxMs={PhaseMaximumOrNa(journalMax, leases)} " +
                $"startupAvgMs={PhaseAverageOrNa(startupTicks, startupScans)} " +
                $"startupMaxMs={PhaseMaximumOrNa(startupMax, startupScans)} " +
                $"appInventoryAvgMs={PhaseAverageOrNa(appTicks, appScans)} " +
                $"appInventoryMaxMs={PhaseMaximumOrNa(appMax, appScans)} " +
                $"cpuTotalSec={PerfNumber(cpuTotalSec, "0.0")} " +
                $"cpuIntervalSec={PerfNumberOrNa(cpuDeltaSec)} " +
                $"cpuAvgAllCoresPct={PerfNumberOrNa(cpuAllCoresPct)} " +
                $"logicalProcessors={Environment.ProcessorCount} " +
                $"workingSetMiB={PerfNumber(process.WorkingSet64 / 1048576.0, "0.0")} " +
                $"privateMiB={PerfNumber(process.PrivateMemorySize64 / 1048576.0, "0.0")} " +
                $"handles={process.HandleCount} threads={process.Threads.Count} " +
                "causalBenefitUnverified=true");
        }
        catch (Exception)
        {
            try
            {
                LogService.Info(
                    $"AI_APP_IDLE_RUNTIME_PERF ticks={count} " +
                    $"avgMs={PerfNumber(ElapsedMilliseconds(totalTicks) / Math.Max(1, count))} " +
                    $"maxMs={PerfNumber(ElapsedMilliseconds(maxTicks))} " +
                    $"leaseAcquired={leases} startupScans={startupScans} " +
                    $"appScans={appScans} appSkipped={appInventorySkips} startupSkipped={startupSkips} " +
                    $"firefoxChecks={firefoxScans} firefoxSkipped={firefoxSkips} " +
                    $"journalAvgMs={PhaseAverageOrNa(journalTicks, leases)} " +
                    $"journalMaxMs={PhaseMaximumOrNa(journalMax, leases)} " +
                    $"startupAvgMs={PhaseAverageOrNa(startupTicks, startupScans)} " +
                    $"startupMaxMs={PhaseMaximumOrNa(startupMax, startupScans)} " +
                    $"appInventoryAvgMs={PhaseAverageOrNa(appTicks, appScans)} " +
                    $"appInventoryMaxMs={PhaseMaximumOrNa(appMax, appScans)} " +
                    "nativeProcessMetricsUnavailable=true");
            }
            catch { /* Measurement must never block normal AI recovery. */ }
        }
    }

    internal async Task TickAsync(
        AiSystemRuntimeConfig config, bool canOptimizeNow,
        bool gameActive, CancellationToken token,
        AiSystemSmoothnessHealth? telemetry = null,
        bool allowInactiveAppStartup = false,
        bool allowInactiveAppObservation = false)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            await TickCoreAsync(config, canOptimizeNow, gameActive,
                token, telemetry, allowInactiveAppStartup,
                allowInactiveAppObservation).ConfigureAwait(false);
        }
        finally
        {
            RecordTickPerformance(Stopwatch.GetTimestamp() - started);
        }
    }

    // Explicit "Restore all" differs from the ordinary background tick:
    // the normal AI limits recovery to ONE owner per scan. Inspect its
    // durable journal between bounded restore attempts so the user is never
    // told that all third-party entries were restored when some remain.
    internal async Task RestoreOwnedNowAsync(CancellationToken token)
    {
        const int maxRestorePasses = 16;
        int priorOwners = int.MaxValue;
        for (int pass = 0; pass < maxRestorePasses; pass++)
        {
            token.ThrowIfCancellationRequested();
            // A previously unsuccessful OFF tick may be cooling down;
            // explicit restoration is a fresh user-requested attempt.
            _lastScanUtc = DateTime.MinValue;
            _lastFailedOffRestoreUtc = default;
            await TickAsync(new AiSystemRuntimeConfig(Enabled: false),
                canOptimizeNow: false, gameActive: false, token)
                .ConfigureAwait(false);

            // TickCore intentionally logs recoverable errors. Verify durable
            // ownership under the same cross-process journal lease before
            // reporting ANY success, including when another host was busy.
            int remaining;
            bool learningRepairPending;
            string leasePath = JournalPath + ".lease";
            try
            {
                using var lease = new FileStream(leasePath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                ReloadJournalUnderLease();
                if (!_journalHealthy)
                    throw new InvalidDataException(
                        "Záznam obnovení aplikací nelze bezpečně přečíst.");
                remaining = _owned.Count;
                learningRepairPending = _learningRepairPending;
            }
            catch (IOException ex)
            {
                throw new IOException(
                    "Záznam obnovy aplikací právě používá jiná instance nebo není dostupný.", ex);
            }
            if (remaining == 0) return;
            if (remaining >= priorOwners && !learningRepairPending)
                throw new InvalidOperationException(
                    $"Obnova aplikací není úplná: zbývá {remaining} spravovaných položek. " +
                    "Záznam byl zachován pro další pokus.");
            priorOwners = remaining;
        }
        throw new InvalidOperationException(
            $"Obnova aplikací nedokončena v bezpečném limitu. " +
            $"Zbývá {_owned.Count} spravovaných položek.");
    }

    // In the absence of owned state, an ineligible cycle is deliberately
    // journal-only; it must not carry stale idle or Steam A/B/A evidence.
    private void ObserveFeatureProfile(UserFeatureProfile current)
    {
        UserFeatureProfile? previous = _lastObservedFeatureProfile;
        if (previous is not null && !ReferenceEquals(previous, current) &&
            (previous.IsUntrusted != current.IsUntrusted ||
             previous.UpdatedUtc != current.UpdatedUtc ||
             !previous.KeepFeatures.SequenceEqual(
                 current.KeepFeatures, StringComparer.OrdinalIgnoreCase)))
        {
            // The user's KEEP protections changed while our 1-3 minute
            // native-inventory cooldown was active. Never spend idle credit
            // accumulated under the old preference snapshot.
            _absentSince.Clear();
            _steamPreStopSamples.Clear();
            _lastObservationUtc = default;
        }
        _lastObservedFeatureProfile = current;
    }

    private void FinishUnownedNoActionScan(
        DateTime now, bool enabled, bool servicesEnabled, bool tasksEnabled)
    {
        _absentSince.Clear();
        _steamPreStopSamples.Clear();
        _lastObservationUtc = default;
        _lastScanUtc = now;
        _lastAiEnabled = enabled;
        _lastServicesEnabled = servicesEnabled;
        _lastTasksEnabled = tasksEnabled;
        Interlocked.Increment(ref _perfStartupSkips);
        Interlocked.Increment(ref _perfFirefoxSkips);
        Interlocked.Increment(ref _perfAppInventorySkips);
    }

    // Every owned app is already indexed by the one immutable AI
    // presence inventory. Reuse those slots for the urgent recovery bitset
    // instead of comparing every owner with all 12 allowed names each tick.
    // Unknown identities still use the original conservative prior-key scan.
    private static int OwnerProbeIndex(string key) =>
        BackgroundActivityService.GetAiRecoveryProbeIndex(key);

    private static bool AnyOwnedAppReopened(
        Dictionary<string, Owner> owners, Func<string, bool> isRunning)
    {
        ulong checkedNames = 0;
        int index = 0;
        foreach (Owner owner in owners.Values)
        {
            // Must advance even for a known duplicate that is skipped, so
            // any later unknown-key fallback sees ALL earlier owners.
            int priorLimit = index++;
            int slot = OwnerProbeIndex(owner.AppKey);
            if (slot >= 0 && slot < 64)
            {
                ulong flag = 1UL << slot;
                if ((checkedNames & flag) != 0) continue;
                checkedNames |= flag;
            }
            else
            {
                // Defensive fallback for malformed or synthetic owner keys.
                bool seen = false;
                int priorIndex = 0;
                foreach (Owner earlier in owners.Values)
                {
                    if (priorIndex++ >= priorLimit) break;
                    if (owner.AppKey.Equals(earlier.AppKey,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        seen = true;
                        break;
                    }
                }
                if (seen) continue;
            }
            if (isRunning(owner.AppKey)) return true;
        }
        return false;
    }

    // These fixtures are in memory only. Probe every allowed identity,
    // Firefox, mixed-case duplicates and foreign names without rechecking
    // any process name. Confirm earliest-match short circuit semantics.
    internal static bool RunOwnedProbeBitsetSmoke()
    {
        DateTime at = new(2026, 10, 23, 2, 0, 0, DateTimeKind.Utc);
        if (AiThirdPartyIdlePolicy.AppKeys.Length >= 63 ||
            OwnerProbeIndex("FIREFOX") != AiThirdPartyIdlePolicy.AppKeys.Length ||
            OwnerProbeIndex("windows") != -1)
            return false;
        var distinctSlots = new HashSet<int>();
        foreach (string key in AiThirdPartyIdlePolicy.AppKeys)
        {
            int slot = OwnerProbeIndex(key);
            if (slot < 0 || slot >= 63 || !distinctSlots.Add(slot) ||
                slot != OwnerProbeIndex(key.ToUpperInvariant()))
                return false;
        }
        if (distinctSlots.Contains(OwnerProbeIndex("firefox")))
            return false;
        var owners = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
        int ordinal = 0;
        void Add(string name)
        {
            string id = (++ordinal).ToString(CultureInfo.InvariantCulture);
            owners["startup|" + id] =
                new Owner("startup", id, name, at, true, false);
        }
        Add("steam");
        Add("STEAM"); // A known duplicate must still count as an earlier row.
        Add("custom");
        Add("CUSTOM"); // Untrusted fallback preserves distinct probes.
        Add("firefox");
        Add("FIREFOX");
        foreach (string name in AiThirdPartyIdlePolicy.AppKeys)
        {
            Add(name);
            Add(name.ToUpperInvariant());
        }
        int count = 0;
        bool any = AnyOwnedAppReopened(owners, _ => { count++; return false; });
        int expected = AiThirdPartyIdlePolicy.AppKeys.Length + 2;
        if (any || count != expected) return false;
        count = 0;
        if (!AnyOwnedAppReopened(owners, x =>
            {
                count++;
                return x.Equals("custom", StringComparison.OrdinalIgnoreCase);
            }) || count != 2)
            return false;
        count = 0;
        if (!AnyOwnedAppReopened(owners, x =>
            {
                count++;
                return x.Equals("firefox", StringComparison.OrdinalIgnoreCase);
            }) || count != 3)
            return false;
        return !AnyOwnedAppReopened(new Dictionary<string, Owner>(), _ => true);
    }

    // A direct exact journal-key lookup must preserve the old case-insensitive
    // LINQ cooldown logic and may not adopt cooldown from unrelated feedback.
    internal static bool RunSteamRetryLookupParitySmoke()
    {
        DateTime now = new(2026, 10, 23, 2, 0, 0, DateTimeKind.Utc);
        var feedback = new Dictionary<string, FeedbackOutcome>(
            StringComparer.OrdinalIgnoreCase);
        static bool Old(IReadOnlyDictionary<string, FeedbackOutcome> rows, DateTime time) =>
            rows.Any(x => x.Key.Equals(SteamServiceOwnerKey,
                StringComparison.OrdinalIgnoreCase) &&
                AiThirdPartyIdlePolicy.DeferServiceRetry(
                    x.Value.Verdict, x.Value.UpdatedUtc, time));
        bool Equal() => IsSteamServiceRetryDeferred(feedback, now) ==
                        Old(feedback, now);
        if (!Equal()) return false;
        feedback["service|Other"] =
            new FeedbackOutcome(now, "positive-system-trend", 3, AppKey: "steam");
        if (!Equal() || IsSteamServiceRetryDeferred(feedback, now))
            return false;
        feedback["SERVICE|steam client service"] =
            new FeedbackOutcome(now.AddHours(-2), "no-observed-gain", 3,
                AppKey: "steam");
        if (!Equal() || !IsSteamServiceRetryDeferred(feedback, now))
            return false;
        feedback[SteamServiceOwnerKey] =
            new FeedbackOutcome(now.AddDays(-8), "aba-matched-system-trend", 3,
                AppKey: "steam");
        if (!Equal() || IsSteamServiceRetryDeferred(feedback, now))
            return false;
        feedback[SteamServiceOwnerKey] =
            new FeedbackOutcome(now.AddMinutes(1), "inconclusive", 2,
                AppKey: "steam");
        if (!Equal() || !IsSteamServiceRetryDeferred(feedback, now))
            return false;
        feedback.Remove(SteamServiceOwnerKey);
        return Equal() && !IsSteamServiceRetryDeferred(feedback, now);
    }

    // Bound to the durable, validated owner journal (at most 64 entries).
    // A short direct dictionary walk avoids per-app captured LINQ predicates
    // during 1-3 minute scans, without changing kind/identity admission.
    // Case-insensitive app identity is intentionally conservative: it must
    // not permit a second mutation for an already-owned differently-cased app.
    private const string SteamServiceOwnerKey = "service|Steam Client Service";

    // Restrict loaded owner/feedback identities to the same immutable
    // allowlist as native AI observations. The shared index includes one
    // extra Firefox PRESENCE slot, which is intentionally NOT an automatic
    // manageable-app identity. Null or forged strings always fail closed.
    private static bool IsManagedJournalApp(string? appKey)
    {
        if (string.IsNullOrEmpty(appKey)) return false;
        int slot = BackgroundActivityService.GetAiRecoveryProbeIndex(appKey);
        return slot >= 0 && slot < AiThirdPartyIdlePolicy.AppKeys.Length;
    }

    // Journal feedback map is always OrdinalIgnoreCase once validated and
    // loaded under the lease. A direct key lookup avoids enumerating every
    // historical outcome and rebuilding the same "service|..." string.
    private static bool IsSteamServiceRetryDeferred(
        IReadOnlyDictionary<string, FeedbackOutcome> feedback, DateTime now) =>
        feedback.TryGetValue(SteamServiceOwnerKey, out var outcome) &&
        AiThirdPartyIdlePolicy.DeferServiceRetry(
            outcome.Verdict, outcome.UpdatedUtc, now);

    private static bool HasOwnedResource(
        Dictionary<string, Owner> owned, string? kind = null,
        string? appKey = null)
    {
        foreach (Owner owner in owned.Values)
        {
            if ((kind is null || owner.Kind == kind) &&
                (appKey is null || owner.AppKey.Equals(
                    appKey, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    // A validated owner map is immutable in its resource identities during
    // this leased AI pass. Restoring/removing an owner returns immediately;
    // feedback samples may replace a value but never alter its kind/app.
    // Capture kind flags and known-app ownership bits ONCE, rather than
    // re-enumerating up to 64 owners for every policy gate and app candidate.
    private readonly struct OwnedPresence(
        Dictionary<string, Owner> owners, ulong appBits,
        bool service, bool startup, bool firefoxTask, bool steamService)
    {
        internal bool HasService => service;
        internal bool HasStartup => startup;
        internal bool HasFirefoxTask => firefoxTask;
        internal bool HasSteamService => steamService;

        internal bool HasApp(string appKey)
        {
            int slot = OwnerProbeIndex(appKey);
            if (slot is >= 0 and < 64)
                return (appBits & (1UL << slot)) != 0;
            // Exact legacy fallback for synthetic/unknown case-variant
            // identities. No broadened resource admission is possible.
            return HasOwnedResource(owners, appKey: appKey);
        }
    }

    private static OwnedPresence CaptureOwnedPresence(
        Dictionary<string, Owner> owners)
    {
        ulong appBits = 0;
        bool service = false, startup = false;
        bool firefoxTask = false, steamService = false;
        foreach (Owner owner in owners.Values)
        {
            int slot = OwnerProbeIndex(owner.AppKey);
            if (slot is >= 0 and < 64)
                appBits |= 1UL << slot;
            if (owner.Kind == "service")
            {
                service = true;
                if (owner.AppKey.Equals("steam",
                    StringComparison.OrdinalIgnoreCase))
                    steamService = true;
            }
            else if (owner.Kind == "startup")
                startup = true;
            else if (owner.Kind == "task" &&
                owner.AppKey.Equals("firefox", StringComparison.OrdinalIgnoreCase))
                firefoxTask = true;
        }
        return new OwnedPresence(owners, appBits, service, startup,
            firefoxTask, steamService);
    }

    // Preserve original stable OrderBy precedence without allocating a
    // sorter/enumerator snapshot on every 3-minute AI scan (including the
    // common empty-owner case). The validated journal permits only these
    // three resource kinds. Explicit priority phases keep relative insertion
    // order intact while allowing a failed recovery to try later owners.
    private static int RecoveryPriority(string kind) =>
        kind == "service" ? 0 : kind == "task" ? 1 : 2;

    private async Task TickCoreAsync(
        AiSystemRuntimeConfig config, bool canOptimizeNow,
        bool gameActive, CancellationToken token,
        AiSystemSmoothnessHealth? telemetry, bool allowInactiveAppStartup,
        bool allowInactiveAppObservation)
    {
        DateTime now = DateTime.UtcNow;
        bool enabled = config.Enabled && config.SelfOptimizationEnabled;
        // The user playing, the computer becoming busy or AI being switched
        // off breaks a continuous idle window IMMEDIATELY. Do not let the
        // ordinary 1-3 minute inventory throttle retain stale evidence.
        if (!enabled || (!canOptimizeNow && !allowInactiveAppObservation) || gameActive)
        {
            _absentSince.Clear();
            _steamPreStopSamples.Clear();
            _lastObservationUtc = default;
        }
        else if (_absentSince.Count != 0 || _steamPreStopSamples.Count != 0 ||
                 _owned.Count != 0)
        {
            // Only metadata is read on unchanged profiles. Skip the extra
            // check when nothing is being observed or recovered.
            ObserveFeatureProfile(UserFeatureProfileService.Load());
        }
        if (!_journalHealthy &&
            !AiThirdPartyIdlePolicy.MayRetryJournalRead(_lastJournalErrorUtc, now))
            return;
        // The startup lane may observe Steam even with service management
        // turned OFF. Its hour of absent-process evidence must NOT be reused
        // to stop Steam Client Service immediately after services are enabled.
        // Start a fresh service-eligible window at each service-switch change;
        // do not discard other applications' independent startup evidence.
        if (_lastServicesEnabled != config.ServicesEnabled)
        {
            _absentSince.Remove("steam");
            _steamPreStopSamples.Clear();
        }
        // The optional Firefox task has its own idle clock. Switching task
        // automation off/on invalidates earlier absence evidence, including
        // when the disabled tick skipped native inventories entirely.
        if (_lastTasksEnabled != config.ScheduledTasksEnabled)
            _absentSince.Remove("firefox");
        // Changes to global AI/service/task switches must not be delayed
        // by the normal 3-minute idle inventory throttle.
        bool configurationChanged =
            _lastAiEnabled != enabled ||
            _lastServicesEnabled != config.ServicesEnabled ||
            _lastTasksEnabled != config.ScheduledTasksEnabled;
        // AI disabled with no owned changes: lightweight bounded periodic
        // shared-journal refresh, not expensive Windows inventory every 5s.
        if (!configurationChanged && _journalHealthy &&
            AiThirdPartyIdlePolicy.IsFailedOffRestoreCoolingDown(
                enabled, _owned.Count, _lastFailedOffRestoreUtc, now) &&
            !ExternalJournalChangeDue(now))
            return;
        if (!configurationChanged && _journalHealthy &&
            AiThirdPartyIdlePolicy.CanThrottleDisabledIdleTick(
                enabled, _owned.Count, _references.Count, _lastScanUtc, now) &&
            !ExternalJournalChangeDue(now))
            return;
        // Continue owned recovery promptly, but native inventory only once
        // per minute during otherwise unchanged FanAgent 5-second cycles.
        if (!configurationChanged && _journalHealthy && enabled &&
            now - _lastScanUtc < (_owned.Count > 0
                ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(3)) &&
            !ExternalJournalChangeDue(now))
        {
            if (!AiThirdPartyIdlePolicy.IsOwnedRecoveryProbeDue(
                    _owned.Count, _lastUrgentProbeUtc, now))
                return;
            _lastUrgentProbeUtc = now;
            bool recoveryRequested = AnyOwnedAppReopened(
                _owned, BackgroundActivityService.IsRunningForAi);
            if (!recoveryRequested)
            {
                // Saving a "Ponechat" preference must restore already
                // AI-owned startup/task/service resources promptly, even
                // when the app has not been launched yet. Load() reuses its
                // synchronized in-memory snapshot after a verified Save().
                UserFeatureProfile currentPreferences = UserFeatureProfileService.Load();
                if (currentPreferences.IsUntrusted)
                {
                    // Lost/corrupt consent is not permission to keep a
                    // previously disabled service/task until minute-long
                    // inventory cooldown expires. Restore owned state now.
                    recoveryRequested = true;
                }
                else
                {
                    recoveryRequested = AnyOwnedAppReopened(_owned,
                        app => currentPreferences.Keeps("bg:" + app + ":keep"));
                }
            }
            if (!recoveryRequested)
                return;
            // User activity OR a new KEEP choice: the normal serialized
            // journal/inventory transaction below performs the restoration.
        }
        // Never grant scan-cooldown credit before acquiring the lease:
        // another RuntimeHost may hold it, and our next 5s tick must retry.
        // Two RuntimeHosts may be alive simultaneously. A disk-based lease
        // serializes load, decision, write-ahead journal and restore. Re-read
        // the journal under that lease: another host might have acted first.
        FileStream? lease = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            lease = new FileStream(JournalPath + ".lease", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        using (lease)
        try
        {
            Interlocked.Increment(ref _perfLeaseAcquired);
            long journalStarted = Stopwatch.GetTimestamp();
            try { ReloadJournalUnderLease(); }
            finally
            {
                RecordPhaseTicks(ref _perfJournalLoadTicks,
                    ref _perfJournalLoadMaxTicks,
                    Stopwatch.GetTimestamp() - journalStarted);
            }
            if (!_journalHealthy) return;
            if (_learningRepairPending)
            {
                // Only auxiliary measurement data was damaged. Preserve ALL
                // validated owners; atomically repair learning while leased,
                // and DO NOT attempt any Windows mutation in this cycle.
                try
                {
                    await PersistAsync(CancellationToken.None).ConfigureAwait(false);
                    _learningRepairPending = false;
                    // Resume valid owner recovery on the NEXT normal host
                    // tick, not after a stale 1-3min or failed-off cooldown.
                    _lastScanUtc = DateTime.MinValue;
                    _lastFailedOffRestoreUtc = default;
                }
                catch (Exception ex)
                {
                    _journalHealthy = false;
                    _lastJournalErrorUtc = DateTime.UtcNow;
                    LogService.Error("ai-system", "AI journal learning repair failed", ex);
                }
                return;
            }
            if (!enabled && _owned.Count == 0 && _references.Count == 0)
            {
                // This is a completed journal-only inventory; no native
                // startup or process work was required.
                _lastScanUtc = now;
                _lastAiEnabled = enabled;
                _lastServicesEnabled = config.ServicesEnabled;
                _lastTasksEnabled = config.ScheduledTasksEnabled;
                return;
            }
            // AI-off still restores owned entries, but needs native startup
            // inventory only if one of our actual owners is a startup row.
            // Other shutdown recovery does not need a full process snapshot.
            OwnedPresence ownedPresence = CaptureOwnedPresence(_owned);
            bool mayIntroduceAtScan = AiThirdPartyIdlePolicy.MayIntroduceChange(
                enabled, canOptimizeNow, gameActive, _owned.Count,
                ownedPresence.HasService,
                _references.Count != 0);
            // Startup-only observation is permitted during normal
            // interactive desktop use when the target app itself is absent.
            // It NEVER allows service stops, Firefox task changes or a game
            // overlay interruption. Those still require whole-PC real idle.
            bool mayObserveUnusedStartup = AiThirdPartyIdlePolicy.MayIntroduceChange(
                enabled, allowInactiveAppObservation, gameActive, _owned.Count,
                ownedPresence.HasService, _references.Count != 0);
            bool mayObserveAny = mayIntroduceAtScan || mayObserveUnusedStartup;
            if (!mayObserveAny)
            {
                _absentSince.Clear();
                _steamPreStopSamples.Clear();
                _lastObservationUtc = default;
            }
            else if (!mayIntroduceAtScan)
            {
                // An app-specific startup absence hour must never be
                // recycled as Steam SERVICE idle evidence.
                _absentSince.Remove("steam");
                _steamPreStopSamples.Clear();
            }
            // An unowned, reference-free AI has nothing to recover while
            // a game is active or the machine is not in a quiet window.
            // The journal was freshly validated above; no profile, registry
            // or process inventory is required in this path.
            if (AiThirdPartyIdlePolicy.CanSkipUnownedInventory(
                    enabled, mayObserveAny, true,
                    _owned.Count, _references.Count))
            {
                FinishUnownedNoActionScan(
                    now, enabled, config.ServicesEnabled, config.ScheduledTasksEnabled);
                return;
            }
            // The preference snapshot is shared by all remaining decisions.
            UserFeatureProfile? profile = enabled
                ? UserFeatureProfileService.Load() : null;
            if (profile is not null)
                ObserveFeatureProfile(profile);
            bool trustedPreferences = profile is { IsUntrusted: false };
            if (AiThirdPartyIdlePolicy.CanSkipUnownedInventory(
                    enabled, mayObserveAny, trustedPreferences,
                    _owned.Count, _references.Count))
            {
                FinishUnownedNoActionScan(
                    now, enabled, config.ServicesEnabled, config.ScheduledTasksEnabled);
                return;
            }
            bool needStartup = AiThirdPartyIdlePolicy.NeedsStartupInventory(
                mayObserveAny,
                ownedPresence.HasStartup,
                trustedPreferences);
            IReadOnlyList<StartupEntry> startup = Array.Empty<StartupEntry>();
            if (!needStartup)
                Interlocked.Increment(ref _perfStartupSkips);
            if (needStartup)
            {
                Interlocked.Increment(ref _perfStartupScans);
                long scanStarted = Stopwatch.GetTimestamp();
                try
                {
                    startup = await StartupManagerService.GetAiUserRunAsync(token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    RecordPhaseTicks(ref _perfStartupLoadTicks,
                        ref _perfStartupLoadMaxTicks,
                        Stopwatch.GetTimestamp() - scanStarted);
                }
            }
            IReadOnlyList<BackgroundActivityState> apps =
                Array.Empty<BackgroundActivityState>();
            if (enabled)
            {
                Interlocked.Increment(ref _perfAppScans);
                long scanStarted = Stopwatch.GetTimestamp();
                try
                {
                    apps = await BackgroundActivityService.ReadForAiAsync(
                        startup, profile!, token).ConfigureAwait(false);
                }
                finally
                {
                    RecordPhaseTicks(ref _perfAppLoadTicks,
                        ref _perfAppLoadMaxTicks,
                        Stopwatch.GetTimestamp() - scanStarted);
                }
            }
            // Do not grant 1/3-minute scan credit or swallow a config
            // transition until ALL native reads have succeeded. Transient
            // inventory errors must retry next FanAgent tick.
            _lastScanUtc = now;
            _lastAiEnabled = enabled;
            _lastServicesEnabled = config.ServicesEnabled;
            _lastTasksEnabled = config.ScheduledTasksEnabled;
            if (AiThirdPartyIdlePolicy.CanAccumulateIdleEvidence(
                    enabled, trustedPreferences))
            {
                if (!AiThirdPartyIdlePolicy.IsContinuousObservation(
                        _lastObservationUtc, now))
                    _absentSince.Clear();
                _lastObservationUtc = now;
            }
            else
            {
                // No trusted preferences means no admissible idle evidence.
                // Discard historical credits so a future repaired profile
                // must observe a fresh quiet period before any new change.
                _absentSince.Clear();
                _steamPreStopSamples.Clear();
                _lastObservationUtc = default;
            }
            // Fixed AI inventory order has a prebuilt static key index.
            // This value-type view avoids the second per-scan Dictionary
            // allocation while retaining fast, identity-checked lookup.
            var observed = new BackgroundActivityService.AiObservedSnapshot(apps);
            await AdvancePassiveReferenceAsync(now,
                AiThirdPartyIdlePolicy.MayObserveReference(
                    enabled, config.ServicesEnabled, trustedPreferences),
                canOptimizeNow && !gameActive,
                observed.TryGetValue("steam", out var observedSteam) &&
                observedSteam.ProcessCount == 0, telemetry, token).ConfigureAwait(false);
            // Firefox browser-agent task is a narrowly scoped optional task.
            // It has no startup preference entry in the background app list.
            bool shouldReadFirefoxPresence =
                AiThirdPartyIdlePolicy.NeedsFirefoxPresence(
                    enabled,
                    ownedPresence.HasFirefoxTask,
                    AiThirdPartyIdlePolicy.MayIntroduceChange(
                        enabled, canOptimizeNow, gameActive, _owned.Count,
                        ownedPresence.HasService,
                        _references.Count != 0),
                    config.ScheduledTasksEnabled, trustedPreferences);
            if (shouldReadFirefoxPresence)
                Interlocked.Increment(ref _perfFirefoxScans);
            else
                Interlocked.Increment(ref _perfFirefoxSkips);
            // Read the Firefox presence bit from the ALREADY captured
            // single AI process-table snapshot. Keep the independent
            // immediate prewrite recheck below before any task mutation.
            // Missing process telemetry is UNKNOWN, not proof of absence.
            // The existing pre-write recheck remains mandatory, but cannot
            // make a stale hour-long idle clock trustworthy retroactively.
            BackgroundActivityState firefoxPresence = null!;
            bool firefoxPresenceKnown = shouldReadFirefoxPresence &&
                observed.TryGetValue("firefox", out firefoxPresence);
            bool firefoxRunning = firefoxPresenceKnown &&
                firefoxPresence.ProcessCount > 0;

            bool Activity(string app) =>
                app.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                    ? AiThirdPartyIdlePolicy.OwnedPresenceRequiresRestore(
                        firefoxPresenceKnown, firefoxRunning)
                    : !observed.TryGetValue(app, out var state) ||
                      AiThirdPartyIdlePolicy.OwnedPresenceRequiresRestore(
                          true, state.ProcessCount > 0);

            // Always restore before discovering anything new. An owned entry
            // remains in the journal if the original state cannot be restored.
            bool restoreAttempted = false;
            for (int recoveryPhase = 0; recoveryPhase < 3; recoveryPhase++)
            foreach (Owner owner in _owned.Values)
            {
                if (RecoveryPriority(owner.Kind) != recoveryPhase) continue;
                token.ThrowIfCancellationRequested();
                bool externallyRestored = await WasExternallyRestoredAsync(owner, token)
                    .ConfigureAwait(false);
                bool withinHold = AiThirdPartyIdlePolicy.MayKeepOwnedHold(
                    owner.Kind, enabled, config.ServicesEnabled,
                    config.ScheduledTasksEnabled, owner.AppliedUtc, now);
                // The shared inventory can be older than a just-relaunched
                // app. While a managed change is still being held, make a
                // narrow native presence check before deciding to keep it.
                // The urgent 10-second probe may have detected the reopen
                // after the shared inventory was sampled.
                bool appActive = Activity(owner.AppKey) ||
                    (withinHold && BackgroundActivityService.IsRunningForAi(owner.AppKey));
                // Firefox's optional task is intentionally presence-only:
                // its snapshot preference is always Unknown, so consult
                // the trusted feature profile for ALL owned kinds.
                bool userKeepsApp = trustedPreferences &&
                    profile!.Keeps("bg:" + owner.AppKey + ":keep");
                bool noGainRollback = enabled && owner.Kind == "service" &&
                    config.ServicesEnabled && !userKeepsApp &&
                    !externallyRestored && !appActive &&
                    await UpdateServiceFeedbackAsync(owner, telemetry, now,
                        canOptimizeNow && !gameActive, token)
                        .ConfigureAwait(false);
                if (withinHold && trustedPreferences &&
                    !noGainRollback && !owner.Pending && !appActive &&
                    !userKeepsApp && !externallyRestored)
                    continue;
                // Only a natural hold expiry, not user activity/AI-off/no
                // benefit, permits collecting restored reference A2. No
                // extra Windows service toggle is made for this experiment.
                bool passiveReference = enabled && config.ServicesEnabled &&
                    trustedPreferences && !userKeepsApp && !noGainRollback &&
                    !appActive &&
                    canOptimizeNow && !gameActive && owner.Kind == "service" &&
                    !owner.Pending && owner.AppliedUtc.Kind == DateTimeKind.Utc &&
                    owner.AppliedUtc <= now &&
                    now - owner.AppliedUtc >= TimeSpan.FromHours(6) &&
                    !externallyRestored &&
                    _feedback.TryGetValue(Key(owner.Kind, owner.Id), out var prior) &&
                    prior.Verdict == "positive-system-trend" &&
                    owner.BaselineCpuPercent.HasValue && owner.BaselineRamMiB.HasValue &&
                    owner.FeedbackSamples == 3;
                restoreAttempted = true;
                if (await RestoreAsync(owner, token).ConfigureAwait(false))
                {
                    if (passiveReference)
                    {
                        string id = Key(owner.Kind, owner.Id);
                        _references[id] = new ReferenceTrial(owner.Id, owner.AppKey, now,
                            owner.BaselineCpuPercent!.Value, owner.BaselineRamMiB!.Value,
                            owner.SumCpuPercent / 3, owner.SumRamMiB / 3);
                        LogService.Info("ai-system",
                            $"AI_APP_IDLE_ABA_REFERENCE_START app={owner.AppKey} " +
                            "afterNaturalRestore=true causalProof=false");
                    }
                    _owned.Remove(Key(owner.Kind, owner.Id));
                    _absentSince.Remove(owner.AppKey);
                    _externallyOverriddenUntil[owner.AppKey] = now.AddMinutes(30);
                    await PersistAsync(token).ConfigureAwait(false);
                    LogService.Info("ai-system",
                        $"AI_APP_IDLE_RESTORE kind={owner.Kind} app={owner.AppKey} reason=" +
                        (userKeepsApp ? "user-keeps-app" :
                         noGainRollback ? "no-measurable-resource-trend" :
                         appActive ? "app-in-use" :
                         externallyRestored ? "external-native-override" :
                         "AI-off-or-hold-expired"));
                    _lastFailedOffRestoreUtc = default;
                    // Restore only one completed resource per tick.
                    return;
                }
                // A missing or externally modified entry cannot starve
                // later independently owned resources.
            }
            if (restoreAttempted)
            {
                if (!enabled)
                    _lastFailedOffRestoreUtc = now;
                return; // Never apply while recovery failed.
            }

            // A single newly applied mutation per pass; while the service's
            // CPU/RAM response or restored A2 is being observed, changing
            // any other app would contaminate the experiment.
            if (!mayObserveAny || !trustedPreferences) return;
            foreach (string app in AiThirdPartyIdlePolicy.AppKeys)
            {
                token.ThrowIfCancellationRequested();
                if (!observed.TryGetValue(app, out var state))
                {
                    // An incomplete process inventory breaks continuous
                    // absence evidence. Never spend an earlier idle credit
                    // when this resource becomes visible again later.
                    _absentSince.Remove(app);
                    continue;
                }
                if (ownedPresence.HasApp(app)) continue;
                if (state.ProcessCount > 0)
                {
                    _absentSince.Remove(app);
                    continue;
                }
                // The user's explicit KEEP or a recent external override is
                // a break in eligible idle observation. Never accumulate the
                // hour/20-minute credit while this resource is protected:
                // removing KEEP later must start a fresh quiet period.
                if (state.Preference == BackgroundAppPreference.Keep ||
                    (_externallyOverriddenUntil.TryGetValue(app, out DateTime until) &&
                     until > now))
                {
                    _absentSince.Remove(app);
                    continue;
                }
                if (!_absentSince.TryGetValue(app, out DateTime absent))
                    _absentSince[app] = absent = now;
                if (!AiThirdPartyIdlePolicy.CanReduce(
                    now - absent, state.Preference, false, gameActive,
                    canOptimizeNow || (allowInactiveAppStartup && app != "steam")))
                    continue;

                // First do not start/stop ANY currently running app.
                // Disable one optional HKCU user-start item only, never
                // auto-disable Windows/Google/Edge/Firefox security updates.
                // Select the first ACTUALLY movable startup, not merely the
                // first loosely recognized inventory row. A malformed first
                // row must not mask a later safe independent registry view.
                StartupEntry? entry = FindAdmissibleStartup(
                    app, startup, requireNativeAdmission: true);
                if (entry != null)
                {
                    Owner owner = new("startup", entry.Id, app, now, true, false,
                        entry.Name, entry.Command, entry.RunPath, (int)entry.View);
                    if (await ApplyAsync(owner, async () =>
                    {
                        // A single targeted process recheck is substantially
                        // cheaper than enumerating all app CPU/RAM metrics
                        // and startup matches a second time before each write.
                        if (AiThirdPartyIdlePolicy.HasAnyRunningProcess(state.ProcessNames))
                            throw new PrewriteAdmissionException(
                                "Application relaunched before startup change");
                        var beforeWrite = await StartupManagerService
                            .GetAiUserRunAsync(token).ConfigureAwait(false);
                        if (!HasCurrentAdmissibleStartup(app, owner, beforeWrite,
                                requireNativeAdmission: true))
                            throw new PrewriteAdmissionException(
                                "Startup changed externally before AI write");
                        if (!AiThirdPartyIdlePolicy.MayTouchApp(
                                UserFeatureProfileService.Load(), app) ||
                            AiThirdPartyIdlePolicy.HasAnyRunningProcess(state.ProcessNames))
                            throw new PrewriteAdmissionException(
                                "App became active or protected before AI startup write");
                        RequireLiveAiWriteScope("startup");
                        await StartupManagerService.SetEnabledAsync(entry, false, token)
                            .ConfigureAwait(false);
                        var fresh = await StartupManagerService
                            .GetAiUserRunAsync(token).ConfigureAwait(false);
                        if (!IsStartupDisableConfirmed(owner, entry, fresh,
                                requireNativeAdmission: true))
                            throw new InvalidOperationException(
                                "AI startup disable readback left an enabled copy or lost the disabled owner");
                    }, token).ConfigureAwait(false))
                        return;
                }
            }

            // Steam helper is not Steam itself. It is demand-started and only
            // considered after a full hour with no Steam process activity.
            bool steamServiceCandidate = enabled && canOptimizeNow &&
                config.ServicesEnabled && _owned.Count < 3 &&
                !_references.ContainsKey(SteamServiceOwnerKey) &&
                !IsSteamServiceRetryDeferred(_feedback, now) &&
                !ownedPresence.HasSteamService &&
                observed.TryGetValue("steam", out var steam) &&
                steam.ProcessCount == 0 &&
                _absentSince.TryGetValue("steam", out DateTime lastSteam) &&
                AiThirdPartyIdlePolicy.CanReduce(
                    now - lastSteam, steam.Preference, false, gameActive, canOptimizeNow);
            if (!steamServiceCandidate)
                _steamPreStopSamples.Clear();
            if (steamServiceCandidate &&
                TryGetStableSteamBaseline(now, true, telemetry,
                    out double baselineCpu, out double baselineRamMiB))
            {
                using var service = new ServiceController("Steam Client Service");
                try
                {
                    bool freshTelemetry = AiSystemSmoothnessGuardService.IsFresh(
                        telemetry?.UpdatedUtc ?? default, now, TimeSpan.FromSeconds(65)) &&
                        telemetry is { GameActive: false, State: "Normal" } &&
                        double.IsFinite(telemetry.CpuLoadPercent) &&
                        telemetry.CpuLoadPercent is >= 0 and <= 100 &&
                        double.IsFinite(telemetry.FreeRamGiB) &&
                        telemetry.FreeRamGiB > 0;
                    // Do not stop a live service unless its baseline can
                    // actually be measured and any benefit/rollback assessed.
                    if (freshTelemetry &&
                        service.Status == ServiceControllerStatus.Running &&
                        service.CanStop &&
                        (int)service.StartType is 2 or 3 &&
                        HasNoActiveDependents(service))
                    {
                        Owner owner = new("service", "Steam Client Service",
                            "steam", now, true, true,
                            BaselineCpuPercent: baselineCpu,
                            BaselineRamMiB: baselineRamMiB,
                            OriginalServiceStartMode: (int)service.StartType);
                        if (await ApplyAsync(owner, () =>
                        {
                            if (AiThirdPartyIdlePolicy.HasAnyRunningProcess(
                                    "steam", "steamwebhelper") ||
                                !AiThirdPartyIdlePolicy.MayTouchApp(
                                    UserFeatureProfileService.Load(), "steam"))
                                throw new PrewriteAdmissionException(
                                    "Steam became active or protected before helper stop");
                            service.Refresh();
                            // The earlier dependency scan may be stale after
                            // write-ahead journal admission. Revalidate SCM
                            // dependents immediately before the native stop.
                            // Any newly running or paused dependency blocks
                            // the write, preserving the user's live service.
                            if (service.Status != ServiceControllerStatus.Running ||
                                !service.CanStop ||
                                (int)service.StartType != owner.OriginalServiceStartMode ||
                                !HasNoActiveDependents(service))
                                throw new PrewriteAdmissionException(
                                    "Steam helper or dependent service changed before stop");
                            RequireLiveAiWriteScope("service");
                            service.Stop();
                            service.WaitForStatus(ServiceControllerStatus.Stopped,
                                TimeSpan.FromSeconds(8));
                            return Task.CompletedTask;
                        }, token).ConfigureAwait(false))
                            return; // Exactly one new mutation in this tick.
                    }
                }
                catch (InvalidOperationException) { /* Service not registered. */ }
            }

            // Optional third-party usage agent: never alter updates/security
            // schedulers or the user's ASUS peripheral/GearLink tasks.
            // Unlike the other 12 managed apps, Firefox uses a separate
            // optional task instead of the background-app inventory. Its
            // idle timer must also honor KEEP, a disabled Tasks switch and
            // the post-restore external-override grace period. Otherwise
            // re-enabling tasks could immediately reuse old idle credit.
            bool firefoxProtected = !firefoxPresenceKnown ||
                !config.ScheduledTasksEnabled ||
                profile!.Keeps("bg:firefox:keep") ||
                (_externallyOverriddenUntil.TryGetValue("firefox", out DateTime firefoxOverride) &&
                 firefoxOverride > now);
            if (firefoxRunning || firefoxProtected)
                _absentSince.Remove("firefox");
            if (enabled && canOptimizeNow && !firefoxProtected && !firefoxRunning &&
                !ownedPresence.HasApp("firefox"))
            {
                if (!_absentSince.TryGetValue("firefox", out var idle))
                    _absentSince["firefox"] = idle = now;
                if (now - idle >= TimeSpan.FromHours(1))
                {
                    foreach (var (_, prefix) in AiThirdPartyIdlePolicy.Tasks)
                    {
                        var task = await Task.Run(() => GetMatchingTask(prefix, token), token)
                            .ConfigureAwait(false);
                        if (task is null) continue;
                        // A process can launch between the periodic snapshot
                        // and actual COM write; recheck just before mutation.
                        bool liveFirefox = ProcessSnapshotCleanup.Inspect(
                            () => Process.GetProcessesByName("firefox"),
                            processes => processes.Length != 0);
                        if (liveFirefox) { _absentSince.Remove("firefox"); break; }
                        var current = TaskSchedulerComService.GetRuntimeState(task, token);
                        if (!current.Enabled || current.Running ||
                            AiMaintenanceRevalidationPolicy.TaskReapplyBlockReason(current, now) != null)
                            continue;
                        Owner owner = new("task", task, "firefox", now, true, false);
                        await ApplyAsync(owner, () =>
                        {
                            if (AiThirdPartyIdlePolicy.HasAnyRunningProcess("firefox") ||
                                !AiThirdPartyIdlePolicy.MayTouchApp(
                                    UserFeatureProfileService.Load(), "firefox"))
                                throw new PrewriteAdmissionException(
                                    "Firefox became active or protected before optional task");
                            var freshTask = TaskSchedulerComService.GetRuntimeState(task, token);
                            if (!freshTask.Enabled || freshTask.Running ||
                                AiMaintenanceRevalidationPolicy
                                    .TaskReapplyBlockReason(freshTask, DateTime.UtcNow) != null)
                                throw new PrewriteAdmissionException(
                                    "Optional Firefox task changed before AI write");
                            RequireLiveAiWriteScope("task");
                            TaskSchedulerComService.SetEnabled(task, false, token);
                            if (TaskSchedulerComService.GetRuntimeState(task, token).Enabled)
                                throw new InvalidOperationException(
                                    "Optional Firefox task disable readback failed");
                            return Task.CompletedTask;
                        }, token).ConfigureAwait(false);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogService.Error("ai-system", "AI third-party idle cycle failed", ex);
        }
    }

    // Synthetic identity and .NET 10 dictionary-enumerator test. The
    // production A2 loop updates/removes EXISTING references while retaining
    // its journal lease, so no extra copied array is required. Never inspect
    // actual services, tasks, profiles, or the user's ownership journal here.
    internal static bool RunOwnedReferenceEnumerationSmoke()
    {
        DateTime at = new(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);
        var worker = new AiThirdPartyIdleAutomationService();
        worker._owned["service|steam"] = new Owner(
            "service", "steam", "StEaM", at, true, true);
        worker._owned["startup|spotify"] = new Owner(
            "startup", "spotify", "spotify", at, true, false);
        worker._owned["task|firefox"] = new Owner(
            "task", "firefox", "FiReFoX", at, true, false);
        if (!HasOwnedResource(worker._owned) ||
            !HasOwnedResource(worker._owned, kind: "service") ||
            !HasOwnedResource(worker._owned, kind: "startup", appKey: "SPOTIFY") ||
            !HasOwnedResource(worker._owned, kind: "task", appKey: "firefox") ||
            !HasOwnedResource(worker._owned, appKey: "STEAM") ||
            HasOwnedResource(worker._owned, kind: "driver") ||
            HasOwnedResource(worker._owned, kind: "service", appKey: "discord") ||
            HasOwnedResource(worker._owned, kind: "SERVICE") ||
            HasOwnedResource(new Dictionary<string, Owner>(), appKey: "steam"))
            return false;
        worker._references["service|first"] = new ReferenceTrial(
            "first", "steam", at, 8, 8192, 7, 8300);
        worker._references["service|second"] = new ReferenceTrial(
            "second", "steam", at, 8, 8192, 7, 8300);
        worker._references["service|third"] = new ReferenceTrial(
            "third", "steam", at, 8, 8192, 7, 8300);
        int visited = 0;
        // The same operations are used inside the awaited A2 branch:
        // replacement on a fresh sample, removal after a completed trial.
        foreach (var (key, reference) in worker._references)
        {
            visited++;
            if (key == "service|first")
                worker._references[key] = reference with { Samples = 1 };
            else if (key == "service|second")
                worker._references.Remove(key);
        }
        return visited == 3 &&
            worker._references.Count == 2 &&
            worker._references["service|first"].Samples == 1 &&
            !worker._references.ContainsKey("service|second") &&
            worker._references["service|third"].Samples == 0 &&
            worker._owned.Count == 3;
    }

    // Pure parity against the original repeated scans. A leased tick
    // snapshots immutable owner identities before doing native inventory,
    // and any successful recovery returns before another owner decision.
    internal static bool RunOwnedPresenceParitySmoke()
    {
        DateTime at = new(2026, 10, 23, 3, 0, 0, DateTimeKind.Utc);
        var owners = new Dictionary<string, Owner>(
            StringComparer.OrdinalIgnoreCase);
        bool Check()
        {
            OwnedPresence captured = CaptureOwnedPresence(owners);
            if (captured.HasService != HasOwnedResource(owners, kind: "service") ||
                captured.HasStartup != HasOwnedResource(owners, kind: "startup") ||
                captured.HasFirefoxTask != HasOwnedResource(
                    owners, kind: "task", appKey: "firefox") ||
                captured.HasSteamService != HasOwnedResource(
                    owners, kind: "service", appKey: "steam"))
                return false;
            foreach (string app in AiThirdPartyIdlePolicy.AppKeys)
                if (captured.HasApp(app) != HasOwnedResource(
                    owners, appKey: app) ||
                    captured.HasApp(app.ToUpperInvariant()) !=
                        HasOwnedResource(owners, appKey: app.ToUpperInvariant()))
                    return false;
            foreach (string app in new[]
                { "firefox", "FIREFOX", "unknown", "UNKNOWN", "windows", "chrome" })
                if (captured.HasApp(app) != HasOwnedResource(
                    owners, appKey: app))
                    return false;
            return true;
        }
        if (!Check()) return false;
        int id = 0;
        void Add(string kind, string app)
        {
            string identity = (++id).ToString(CultureInfo.InvariantCulture);
            owners[kind + "|" + identity] = new Owner(
                kind, identity, app, at, true, false);
        }
        foreach (string app in AiThirdPartyIdlePolicy.AppKeys)
        {
            Add("startup", app);
            Add("startup", app.ToUpperInvariant());
            if (!Check()) return false;
        }
        Add("task", "FiReFoX");
        Add("task", "firefox");
        Add("service", "StEaM");
        Add("service", "spotify");
        Add("other", "unknown");
        Add("other", "UNKNOWN");
        if (!Check()) return false;

        // All recognized names fit inside a ulong without using additional
        // dynamic collections, including the read-only Firefox presence bit.
        if (AiThirdPartyIdlePolicy.AppKeys.Length >= 63)
            return false;
        owners.Clear();
        return Check();
    }

    // Pure verification of stable recovery priority, no registry/SCM/tasks.
    // Reverse insertion order deliberately stresses the old OrderBy contract.
    internal static bool RunRestorePrioritySmoke()
    {
        var at = new DateTime(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);
        var owners = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase)
        {
            ["startup|one"] = new("startup", "one", "spotify", at, true, false),
            ["task|one"] = new("task", "one", "firefox", at, true, false),
            ["service|one"] = new("service", "one", "steam", at, true, true),
            ["startup|two"] = new("startup", "two", "discord", at, true, false),
            ["service|two"] = new("service", "two", "steam", at, true, true)
        };
        var order = new List<string>(owners.Count);
        for (int phase = 0; phase < 3; phase++)
            foreach (Owner owner in owners.Values)
                if (RecoveryPriority(owner.Kind) == phase)
                    order.Add(owner.Kind + ":" + owner.Id);
        int emptyChecks = 0;
        var empty = new Dictionary<string, Owner>();
        for (int phase = 0; phase < 3; phase++)
            foreach (Owner _ in empty.Values)
                emptyChecks++;
        // The live feedback path replaces an existing owner record in
        // place while the loop is open; this must not invalidate the
        // dictionary enumerator under .NET 10 semantics.
        int visitedAfterReplacement = 0;
        foreach (Owner owner in owners.Values)
        {
            if (owner.Kind == "service" && owner.Id == "one")
                owners["service|one"] = owner with { Pending = false };
            visitedAfterReplacement++;
        }
        return visitedAfterReplacement == owners.Count &&
            !owners["service|one"].Pending &&
            order.SequenceEqual(new[]
            {
                "service:one", "service:two", "task:one",
                "startup:one", "startup:two"
            }) &&
            emptyChecks == 0 &&
            RecoveryPriority("service") == 0 &&
            RecoveryPriority("task") == 1 &&
            RecoveryPriority("startup") == 2;
    }

    // Pure in-memory fixture; never queries SCM, startup inventory or files.
    internal static bool RunPreStopEvidenceSmoke()
    {
        var worker = new AiThirdPartyIdleAutomationService();
        DateTime start = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
        static AiSystemSmoothnessHealth Sample(DateTime at, double cpu,
            double availableMiB) =>
            new(at, cpu, 42, availableMiB / 1024, "Normal",
                "pre-stop-test", false);
        if (worker.TryGetStableSteamBaseline(start, true,
                Sample(start, 8, 8000), out _, out _) ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(3), true,
                Sample(start.AddMinutes(3), 8.2, 8020), out _, out _) ||
            !worker.TryGetStableSteamBaseline(start.AddMinutes(6), true,
                Sample(start.AddMinutes(6), 8.1, 8010),
                out double cpu, out double ramMiB) ||
            Math.Abs(cpu - 8.1) > 0.01 ||
            Math.Abs(ramMiB - 8010) > 0.01 ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(9), false,
                Sample(start.AddMinutes(9), 8.1, 8010), out _, out _) ||
            worker._steamPreStopSamples.Count != 0 ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(12), true,
                Sample(start.AddMinutes(10), 7.9, 8000), out _, out _) ||
            worker._steamPreStopSamples.Count != 0 ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(15), true,
                Sample(start.AddMinutes(15), 7, 8000), out _, out _) ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(18), true,
                Sample(start.AddMinutes(18), 13, 8005), out _, out _) ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(21), true,
                Sample(start.AddMinutes(21), 7, 8000), out _, out _) ||
            worker.TryGetStableSteamBaseline(start.AddMinutes(24), true,
                Sample(start.AddMinutes(24), 7, 8000), out _, out _) ||
            !worker.TryGetStableSteamBaseline(start.AddMinutes(27), true,
                Sample(start.AddMinutes(27), 7, 8000), out _, out _) ||
            worker._steamPreStopSamples.Count != 3)
            return false;
        // The real skip path discards both historical absence and an
        // incomplete Steam experiment. It must not count an unobserved
        // game/busy interval as confirmed app inactivity after wake-up.
        worker._absentSince["steam"] = start.AddMinutes(-30);
        worker._lastObservationUtc = start.AddMinutes(27);
        DateTime skippedAt = start.AddMinutes(30);
        worker.FinishUnownedNoActionScan(skippedAt, true, true, true);
        // Recovery probe must check one app per distinct case-insensitive
        // identity, short circuit on reopen, and never probe an empty journal.
        var owners = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase)
        {
            ["startup|a"] = new("startup", "a", "steam", start, true, false),
            ["startup|b"] = new("startup", "b", "STEAM", start, true, false),
            ["startup|c"] = new("startup", "c", "discord", start, true, false)
        };
        int checkedApps = 0;
        bool reopened = AnyOwnedAppReopened(owners, key =>
        {
            checkedApps++;
            return key.Equals("discord", StringComparison.OrdinalIgnoreCase);
        });
        if (!reopened || checkedApps != 2)
            return false;
        checkedApps = 0;
        if (AnyOwnedAppReopened(owners, _ => { checkedApps++; return false; }) ||
            checkedApps != 2 ||
            AnyOwnedAppReopened(new Dictionary<string, Owner>(), _ => true))
            return false;
        return worker._absentSince.Count == 0 &&
            worker._steamPreStopSamples.Count == 0 &&
            worker._lastObservationUtc == default &&
            worker._lastScanUtc == skippedAt &&
            worker._lastAiEnabled == true &&
            worker._lastServicesEnabled == true &&
            worker._lastTasksEnabled == true &&
            Interlocked.Read(ref worker._perfAppInventorySkips) == 1 &&
            Interlocked.Read(ref worker._perfStartupSkips) == 1 &&
            Interlocked.Read(ref worker._perfFirefoxSkips) == 1;
    }

    private bool TryGetStableSteamBaseline(
        DateTime now, bool eligible, AiSystemSmoothnessHealth? telemetry,
        out double cpu, out double ramMiB)
    {
        cpu = ramMiB = 0;
        bool comparableTelemetry = eligible &&
            telemetry is { GameActive: false, State: "Normal" } &&
            AiSystemSmoothnessGuardService.IsFresh(
                telemetry.UpdatedUtc, now, TimeSpan.FromSeconds(65)) &&
            IsTrustedResourceSample(
                telemetry.CpuLoadPercent, telemetry.FreeRamGiB);

        if (!comparableTelemetry)
        {
            _steamPreStopSamples.Clear();
            return false;
        }
        if (_steamPreStopSamples.Count > 0)
        {
            DateTime lastUtc = _steamPreStopSamples.Last().AtUtc;
            if (!AiThirdPartyIdlePolicy.IsContinuousObservation(lastUtc, now))
                _steamPreStopSamples.Clear();
            else if (telemetry!.UpdatedUtc <= lastUtc ||
                     telemetry.UpdatedUtc - lastUtc < TimeSpan.FromSeconds(50))
                return false;
        }
        _steamPreStopSamples.Enqueue((
            telemetry!.UpdatedUtc, telemetry.CpuLoadPercent,
            telemetry.FreeRamGiB * 1024));
        while (_steamPreStopSamples.Count > 3)
            _steamPreStopSamples.Dequeue();
        if (_steamPreStopSamples.Count != 3)
            return false;

        // Exactly three tiny records. One pass over the existing Queue
        // avoids ToArray, LINQ Min/Max/Average enumerators and their repeated
        // walks on the AI snapshot path; keep the same acceptance criteria.
        double minCpu = double.PositiveInfinity, maxCpu = double.NegativeInfinity;
        double minRam = double.PositiveInfinity, maxRam = double.NegativeInfinity;
        double cpuSum = 0, ramSum = 0;
        foreach (var sample in _steamPreStopSamples)
        {
            minCpu = Math.Min(minCpu, sample.Cpu);
            maxCpu = Math.Max(maxCpu, sample.Cpu);
            minRam = Math.Min(minRam, sample.RamMiB);
            maxRam = Math.Max(maxRam, sample.RamMiB);
            cpuSum += sample.Cpu;
            ramSum += sample.RamMiB;
        }
        if (!AiThirdPartyIdlePolicy.IsComparablePreStopWindow(
                _steamPreStopSamples.Count, minCpu, maxCpu, minRam, maxRam))
            return false;
        cpu = cpuSum / 3;
        ramMiB = ramSum / 3;
        return true;
    }

    // One validation contract for A/pre-stop, B/stopped helper and
    // A2/restored service. Even finite-but-absurd RAM values must never
    // overflow the persisted MiB sums or poison durable ownership.
    private static bool IsTrustedResourceSample(double cpuPercent, double freeRamGiB) =>
        double.IsFinite(cpuPercent) && cpuPercent is >= 0 and <= 100 &&
        double.IsFinite(freeRamGiB) && freeRamGiB is > 0 and <= 1_048_576 &&
        double.IsFinite(freeRamGiB * 1024);

    // The third persisted B sample already exhausts the bounded evidence
    // budget. If its companion auxiliary verdict was repaired away, finish
    // as inconclusive rather than generating a fourth invalid owner sample.
    private static bool MissingCompletedVerdict(int samples, bool hasRecentVerdict) =>
        samples >= 3 && !hasRecentVerdict;

    internal static bool RunResourceSampleRecoverySmoke() =>
        IsTrustedResourceSample(0, 8) &&
        IsTrustedResourceSample(100, 1_048_576) &&
        !IsTrustedResourceSample(-1, 8) &&
        !IsTrustedResourceSample(100.01, 8) &&
        !IsTrustedResourceSample(double.NaN, 8) &&
        !IsTrustedResourceSample(8, 0) &&
        !IsTrustedResourceSample(8, double.PositiveInfinity) &&
        !IsTrustedResourceSample(8, 1e100) &&
        !MissingCompletedVerdict(2, false) &&
        MissingCompletedVerdict(3, false) &&
        !MissingCompletedVerdict(3, true);

    private static bool HasNoActiveDependents(ServiceController service)
    {
        ServiceController[]? dependents = null;
        try
        {
            dependents = service.DependentServices;
            return AiThirdPartyIdlePolicy.HasOnlyStoppedDependents(
                dependents.Select(x => (ServiceControllerStatus?)x.Status));
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   System.ComponentModel.Win32Exception)
        {
            return false; // Fail closed if SCM state cannot be confirmed.
        }
        finally
        {
            if (dependents != null)
                foreach (ServiceController dependency in dependents)
                    dependency.Dispose();
        }
    }

    private static bool IsServiceActuallyStopped(string id)
    {
        try
        {
            using var helper = new ServiceController(id);
            return AiThirdPartyIdlePolicy.MaySampleStoppedHelper(helper.Status);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                  System.ComponentModel.Win32Exception)
        {
            return false; // Unknown native state is never valid B evidence.
        }
    }

    private async Task<bool> UpdateServiceFeedbackAsync(
        Owner owner, AiSystemSmoothnessHealth? telemetry,
        DateTime now, bool comparableIdle, CancellationToken token)
    {
        if (owner.Kind != "service" || owner.Pending) return false;
        string key = Key(owner.Kind, owner.Id);
        bool hasRecentVerdict = _feedback.TryGetValue(key, out var previous) &&
            previous!.UpdatedUtc >= owner.AppliedUtc;
        if (hasRecentVerdict)
            return previous!.Verdict is "no-observed-gain" or "inconclusive";
        if (MissingCompletedVerdict(owner.FeedbackSamples, hasRecentVerdict))
        {
            // A journal repair can intentionally quarantine an invalid
            // auxiliary verdict while preserving the already collected
            // three durable B samples. Do NOT add a fourth sample and then
            // fail the entire owner journal on the next leased read.
            _feedback[key] = new FeedbackOutcome(now, "inconclusive",
                owner.FeedbackSamples, AppKey: owner.AppKey);
            await PersistAsync(token).ConfigureAwait(false);
            return true; // Recover the previously stopped helper safely.
        }

        // A startup Run entry only saves next-logon work. By contrast the
        // Steam helper actually stops now, so ONLY the service receives a
        // live CPU/available-RAM trend estimate, using the existing cached
        // FanAgent telemetry (no extra hardware polling/process spawning).
        if (owner.BaselineCpuPercent is double baselineCpu &&
            owner.BaselineRamMiB is double baselineRam &&
            comparableIdle && telemetry is { GameActive: false, State: "Normal" } &&
            IsTrustedResourceSample(
                telemetry.CpuLoadPercent, telemetry.FreeRamGiB) &&
            IsServiceActuallyStopped(owner.Id) &&
            AiThirdPartyIdlePolicy.MayAcceptFeedbackSample(
                owner.AppliedUtc, owner.LastFeedbackSampleUtc,
                telemetry.UpdatedUtc, now))
        {
            var sampled = owner with {
                FeedbackSamples = owner.FeedbackSamples + 1,
                SumCpuPercent = owner.SumCpuPercent + telemetry.CpuLoadPercent,
                SumRamMiB = owner.SumRamMiB + telemetry.FreeRamGiB * 1024,
                LastFeedbackSampleUtc = telemetry.UpdatedUtc,
                MinCpuPercent = Math.Min(owner.MinCpuPercent ?? telemetry.CpuLoadPercent,
                    telemetry.CpuLoadPercent),
                MaxCpuPercent = Math.Max(owner.MaxCpuPercent ?? telemetry.CpuLoadPercent,
                    telemetry.CpuLoadPercent),
                MinRamMiB = Math.Min(owner.MinRamMiB ?? telemetry.FreeRamGiB * 1024,
                    telemetry.FreeRamGiB * 1024),
                MaxRamMiB = Math.Max(owner.MaxRamMiB ?? telemetry.FreeRamGiB * 1024,
                    telemetry.FreeRamGiB * 1024)
            };
            _owned[key] = sampled;
            // Keep completed samples across both hosts and restarts so the
            // other host never awards artificial extra evidence.
            if (sampled.FeedbackSamples >= 3)
            {
                double avgCpu = sampled.SumCpuPercent / sampled.FeedbackSamples;
                double avgRam = sampled.SumRamMiB / sampled.FeedbackSamples;
                bool comparable = AiThirdPartyIdlePolicy.IsComparableObservedWindow(
                    sampled.FeedbackSamples, sampled.MinCpuPercent, sampled.MaxCpuPercent,
                    sampled.MinRamMiB, sampled.MaxRamMiB);
                bool positive = comparable && AiThirdPartyIdlePolicy.HasObservedPositiveTrend(
                    baselineCpu, baselineRam, avgCpu, avgRam, sampled.FeedbackSamples);
                string verdict = !comparable ? "inconclusive" :
                    positive ? "positive-system-trend" : "no-observed-gain";
                _feedback[key] = new FeedbackOutcome(now, verdict,
                    sampled.FeedbackSamples,
                    comparable ? Math.Round(baselineCpu - avgCpu, 2) : null,
                    comparable ? Math.Round(avgRam - baselineRam, 1) : null,
                    owner.AppKey);
                LogService.Info("ai-system",
                    $"AI_APP_IDLE_FEEDBACK app={owner.AppKey} verdict={_feedback[key].Verdict} " +
                    $"cpuDeltaPp={_feedback[key].CpuDropPercentPoints:0.00} " +
                    $"availableRamDeltaMiB={_feedback[key].AvailableRamGainMiB:0.0} " +
                    "samples=3 causalProof=false");
            }
            await PersistAsync(token).ConfigureAwait(false);
            return sampled.FeedbackSamples >= 3 &&
                   _feedback[key].Verdict is "no-observed-gain" or "inconclusive";
        }

        // If the telemetry cannot produce three distinct fresh readings,
        // restore without inventing any performance benefit; re-try only
        // after a cooling period, never immediately oscillate this service.
        if (now - owner.AppliedUtc >= TimeSpan.FromMinutes(12))
        {
            _feedback[key] = new FeedbackOutcome(now, "inconclusive",
                owner.FeedbackSamples, AppKey: owner.AppKey);
            await PersistAsync(token).ConfigureAwait(false);
            LogService.Info("ai-system",
                $"AI_APP_IDLE_FEEDBACK app={owner.AppKey} verdict=inconclusive " +
                $"samples={owner.FeedbackSamples} causalProof=false");
            return true;
        }
        return false;
    }

    // A2 is observed ONLY after the normal six-hour auto-restore. Never
    // restart the service merely to manufacture a counterfactual reading.
    // Reuse FanAgent's cached health; at most three persisted samples.
    private async Task AdvancePassiveReferenceAsync(
        DateTime now, bool enabled, bool comparableIdle, bool steamAbsent,
        AiSystemSmoothnessHealth? telemetry, CancellationToken token)
    {
        if (_references.Count == 0) return;
        // .NET 10 permits replacing/removing existing dictionary entries
        // during enumeration. No reference is added in this leased A2 pass;
        // avoid allocating a copied KeyValuePair[] on every measured tick.
        foreach (var (key, reference) in _references)
        {
            token.ThrowIfCancellationRequested();
            // If AI is switched off, Steam is used, or the persisted clock
            // is invalid, abandon comparison without rewriting old evidence.
            if (!enabled || !steamAbsent ||
                reference.RestoredUtc.Kind != DateTimeKind.Utc ||
                reference.RestoredUtc > now)
            {
                _references.Remove(key);
                await PersistAsync(token).ConfigureAwait(false);
                continue;
            }

            if (reference.Samples >= 3)
            {
                // Recovery of a legacy/partially completed A2 trial must
                // not add a fourth sample (Samples is durably capped at 3).
                _feedback[key] = new FeedbackOutcome(now, "aba-inconclusive",
                    reference.Samples, AppKey: reference.AppKey);
                _references.Remove(key);
                await PersistAsync(token).ConfigureAwait(false);
                continue;
            }

            bool expired = now - reference.RestoredUtc >= TimeSpan.FromMinutes(12);
            bool sampleReady = !expired && comparableIdle &&
                telemetry is { GameActive: false, State: "Normal" } &&
                IsTrustedResourceSample(
                    telemetry.CpuLoadPercent, telemetry.FreeRamGiB) &&
                AiThirdPartyIdlePolicy.MayAcceptFeedbackSample(
                    reference.RestoredUtc, reference.LastSampleUtc,
                    telemetry.UpdatedUtc, now);
            if (sampleReady)
            {
                // A stopped helper is not a valid running-service reference.
                bool actuallyRestored = false;
                try
                {
                    using var helper = new ServiceController(reference.Id);
                    actuallyRestored = helper.Status == ServiceControllerStatus.Running;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Unknown reference state is inconclusive, never positive.
                }
                if (!actuallyRestored)
                {
                    _feedback[key] = new FeedbackOutcome(now, "aba-inconclusive",
                        reference.Samples, AppKey: reference.AppKey);
                    _references.Remove(key);
                    await PersistAsync(token).ConfigureAwait(false);
                    continue;
                }

                double cpu = telemetry!.CpuLoadPercent;
                double ram = telemetry.FreeRamGiB * 1024;
                var sampled = reference with
                {
                    Samples = reference.Samples + 1,
                    CpuSum = reference.CpuSum + cpu,
                    RamSum = reference.RamSum + ram,
                    LastSampleUtc = telemetry.UpdatedUtc,
                    MinCpu = Math.Min(reference.MinCpu ?? cpu, cpu),
                    MaxCpu = Math.Max(reference.MaxCpu ?? cpu, cpu),
                    MinRamMiB = Math.Min(reference.MinRamMiB ?? ram, ram),
                    MaxRamMiB = Math.Max(reference.MaxRamMiB ?? ram, ram)
                };
                _references[key] = sampled;
                if (sampled.Samples == 3)
                {
                    double refCpu = sampled.CpuSum / sampled.Samples;
                    double refRam = sampled.RamSum / sampled.Samples;
                    bool matched = AiThirdPartyIdlePolicy.HasMatchedAbaTrend(
                        sampled.BaselineCpu, sampled.BaselineRamMiB,
                        sampled.CandidateCpu, sampled.CandidateRamMiB,
                        refCpu, refRam, sampled.Samples,
                        sampled.MinCpu, sampled.MaxCpu,
                        sampled.MinRamMiB, sampled.MaxRamMiB);
                    string verdict = matched
                        ? "aba-matched-system-trend" : "aba-unconfirmed";
                    _feedback[key] = new FeedbackOutcome(now, verdict, sampled.Samples,
                        matched ? Math.Round(refCpu - sampled.CandidateCpu, 2) : null,
                        matched ? Math.Round(sampled.CandidateRamMiB - refRam, 1) : null,
                        sampled.AppKey);
                    _references.Remove(key);
                    LogService.Info("ai-system",
                        $"AI_APP_IDLE_ABA_REFERENCE_COMPLETE app={sampled.AppKey} " +
                        $"verdict={verdict} samples={sampled.Samples} causalProof=false");
                }
                await PersistAsync(token).ConfigureAwait(false);
            }
            else if (expired)
            {
                _feedback[key] = new FeedbackOutcome(now, "aba-inconclusive",
                    reference.Samples, AppKey: reference.AppKey);
                _references.Remove(key);
                await PersistAsync(token).ConfigureAwait(false);
                LogService.Info("ai-system",
                    $"AI_APP_IDLE_ABA_REFERENCE_TIMEOUT app={reference.AppKey} " +
                    $"samples={reference.Samples} causalProof=false");
            }
        }
    }

    private static string? GetMatchingTask(string prefix, CancellationToken token)
    {
        // A narrow, read-only registry task inventory. No arbitrary task
        // enumeration with mutation rights and no create/register call.
        token.ThrowIfCancellationRequested();
        using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\Mozilla");
        if (root == null) return null;
        foreach (string name in root.GetSubKeyNames())
        {
            string path = @"\Mozilla\" + name;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                AiThirdPartyIdlePolicy.MatchesOptionalFirefoxTask(path))
                return path;
        }
        return null;
    }

    // Decisions begin with the tick's quiet CPU/game/disk evidence, but
    // asynchronous native inventories and write-ahead persistence can delay
    // the actual mutation. Revalidate BOTH the selected automation scope and
    // the latest in-process quiet evidence at the final native write boundary.
    // Recovery/restoration never calls this gate; it must still work on AI-off.
    internal static bool MayMakeNewIdleNativeWrite(
        AiSystemRuntimeConfig current, string kind, bool quietEvidence)
    {
        bool scopeEnabled = kind switch
        {
            "startup" => true,
            "service" => current.ServicesEnabled,
            "task" => current.ScheduledTasksEnabled,
            _ => false
        };
        return current.Enabled && current.SelfOptimizationEnabled &&
            scopeEnabled && quietEvidence;
    }

    private static void RequireLiveAiWriteScope(string kind)
    {
        AiSystemRuntimeConfig current = AiSystemResourceManagerService.LoadConfig();
        // The cached FanAgent measurement and recovery cooldown are checked
        // afresh, not the tick's old canOptimizeNow boolean. Missing or stale
        // measurements fail closed without extra process/hardware polling.
        bool quietEvidence = AiSystemSmoothnessGuardService.AllowsBackgroundExperiment();
        if (!MayMakeNewIdleNativeWrite(current, kind, quietEvidence))
            throw new PrewriteAdmissionException(
                "AI scope or fresh CPU/game/disk quiet evidence lost before native write");
    }

    private async Task<bool> ApplyAsync(Owner owner, Func<Task> apply, CancellationToken token)
    {
        string key = Key(owner.Kind, owner.Id);
        _owned[key] = owner; // Durable ownership BEFORE the first Windows write.
        await PersistAsync(token).ConfigureAwait(false);
        try
        {
            await apply().ConfigureAwait(false);
            _owned[key] = owner with { Pending = false };
            if (owner.Kind == "startup")
                _feedback[key] = new FeedbackOutcome(DateTime.UtcNow,
                    "deferred-until-next-startup", 0, AppKey: owner.AppKey);
            else if (owner.Kind == "task")
                _feedback[key] = new FeedbackOutcome(DateTime.UtcNow,
                    "deferred-until-task-trigger", 0, AppKey: owner.AppKey);
            await PersistAsync(token).ConfigureAwait(false);
            LogService.Info("ai-system",
                $"AI_APP_IDLE_APPLIED kind={owner.Kind} app={owner.AppKey} id={owner.Id}");
            return true;
        }
        catch (PrewriteAdmissionException ex)
        {
            // Admission failed BEFORE the first native mutation. Keeping the
            // write-ahead owner here would let a later restore override an
            // entry the user disabled independently in the meantime.
            _owned.Remove(key);
            try
            {
                // Recovery cleanup is important even if a runtime tick was
                // cancelled after the original journal was committed.
                await PersistAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                _owned[key] = owner; // Journal still contains pending owner.
                throw;
            }
            LogService.Info("ai-system",
                $"AI_APP_IDLE_PREWRITE_ABORT kind={owner.Kind} app={owner.AppKey} " +
                $"reason={ex.Message} pendingOwnerCleared=true windowsWrites=0");
            return false;
        }
        catch
        {
            // A Windows write may have partially succeeded. Keep durable
            // ownership for safe recovery rather than assuming no mutation.
            throw;
        }
    }

    // The tick's startup inventory is captured BEFORE awaited process and
    // telemetry reads. A user may re-enable an AI-owned HKCU Run entry in the
    // meantime; consulting only that old snapshot would keep it 'owned'
    // indefinitely. Query fresh native state before deciding whether
    // to keep the hold, not just later inside RestoreAsync. An unreadable
    // startup snapshot must abort this tick and retry instead of being
    // silently misclassified as an idle resource.
    private static async Task<bool> HasFreshEnabledOwnedStartupAsync(
        Owner owner,
        Func<CancellationToken, Task<IReadOnlyList<StartupEntry>>> read,
        CancellationToken token)
    {
        IReadOnlyList<StartupEntry> current = await read(token).ConfigureAwait(false);
        // A user can re-enable the SAME registry value and simultaneously
        // edit its command. That still ends our right to hold the startup
        // disabled: detect an occupied Run SLOT, not only byte-identical
        // owner commands. RestoreAsync will separately reject any ambiguous
        // or repointed target without modifying external registry values.
        return HasOwnedStartupSlot(owner, current, enabled: true);
    }

    private static async Task<bool> WasExternallyRestoredAsync(
        Owner owner, CancellationToken token)
    {
        if (owner.Kind == "startup")
            return await HasFreshEnabledOwnedStartupAsync(
                owner, StartupManagerService.GetAiUserRunAsync, token)
                .ConfigureAwait(false);
        try
        {
            if (owner.Kind == "service")
            {
                using var service = new ServiceController(owner.Id);
                return service.Status == ServiceControllerStatus.Running;
            }
            if (owner.Kind == "task")
                return TaskSchedulerComService.GetRuntimeState(owner.Id, token).Enabled;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { }
        return false;
    }

    private async Task<bool> RestoreAsync(Owner owner, CancellationToken token)
    {
        try
        {
            if (owner.Kind == "startup")
            {
                // The shared tick inventory can already be minutes old. Even
                // a DISABLED row may have been enabled, removed or repointed
                // while other asynchronous recovery work was running. Always
                // use fresh native HKCU Run evidence before confirming success
                // OR issuing a restorative write to this exact owned entry.
                IReadOnlyList<StartupEntry> latest = await StartupManagerService
                    .GetAiUserRunAsync(token).ConfigureAwait(false);
                if (IsStartupRestoreConfirmed(owner, latest,
                        requireNativeAdmission: true))
                    return true; // Verified existing user restoration.
                if (IsExternallyResolvedStartupWithoutDisabledSlot(owner, latest,
                        requireNativeAdmission: true))
                {
                    // The user replaced the Run command and the AI-owned
                    // disabled slot no longer exists. Nothing remains for AI
                    // to restore; relinquish ownership without ANY Windows
                    // write. A surviving disabled twin always blocks release.
                    LogService.Info("ai-system",
                        $"AI_APP_IDLE_EXTERNAL_STARTUP_RESOLVED app={owner.AppKey} " +
                        "disabledSlotAbsent=true windowsWrites=0");
                    return true;
                }
                StartupEntry? current = FindAdmissibleOwnedRestore(
                    owner, latest, requireNativeAdmission: true);
                if (current is null) return false;
                await StartupManagerService.SetEnabledAsync(current, true, token)
                    .ConfigureAwait(false);
                // Do not discard durable ownership until the actual HKCU
                // Run entry is observed enabled again. SetEnabledAsync may
                // complete despite a later native readback contradiction.
                var confirmed = await StartupManagerService
                    .GetAiUserRunAsync(token).ConfigureAwait(false);
                return IsStartupRestoreConfirmed(owner, confirmed,
                    requireNativeAdmission: true);
            }
            if (owner.Kind == "service")
            {
                using var service = new ServiceController(owner.Id);
                if (service.Status == ServiceControllerStatus.Running) return true;
                if (!AiThirdPartyIdlePolicy.MayRestoreServiceStart(
                        owner.OriginallyRunning, service.Status,
                        (int)service.StartType, owner.OriginalServiceStartMode))
                    return false;
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running,
                    TimeSpan.FromSeconds(8));
                return service.Status == ServiceControllerStatus.Running;
            }
            if (owner.Kind == "task")
            {
                var current = TaskSchedulerComService.GetRuntimeState(owner.Id, token);
                if (current.Enabled) return true;
                // Another host can change task state between inventory and
                // recovery. Reject unrecognized or actively running tasks.
                if (!AiThirdPartyIdlePolicy.MayRestoreFirefoxTask(
                        owner.Id, owner.OriginalEnabled, current.Running))
                    return false;
                TaskSchedulerComService.SetEnabled(owner.Id, true, token);
                return TaskSchedulerComService.GetRuntimeState(owner.Id, token).Enabled;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Manual cancellation cannot be treated as an ordinary blocked
            // candidate. The owned journal remains durable and the caller
            // must report that restoration was interrupted.
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("ai-system",
                $"AI app idle restore blocked kind={owner.Kind} id={owner.Id}", ex);
        }
        return false;
    }

    // Registry value names are case-insensitive slots. A different command
    // in the SAME name/hive/view/Run key still occupies that slot. Refuse an
    // AI disable if its reversible SpankyhoDisabled destination is already
    // occupied, rather than persisting a spurious owner and failing only at
    // the native collision gate. Different registry views remain independent.
    private static bool HasRegistrySlot(
        StartupEntry entry, IReadOnlyList<StartupEntry> rows, bool enabled)
    {
        foreach (StartupEntry row in rows)
            if (row.Enabled == enabled &&
                string.Equals(row.SourceKind, "Registry", StringComparison.OrdinalIgnoreCase) &&
                row.Hive == entry.Hive && row.View == entry.View &&
                string.Equals(row.RunPath, entry.RunPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Only one optional HKCU user Run entry can be admitted per AI cycle.
    // Select the first policy-allowed candidate whose reversible destination
    // is EMPTY; never award a disabled value already owned by another writer.
    // Actual auto-writes additionally demand canonical native admission;
    // keep legacy pure policy parity for read-only synthetic fixtures.
    private static StartupEntry? FindAdmissibleStartup(
        string app, IReadOnlyList<StartupEntry> entries,
        bool requireNativeAdmission = false)
    {
        foreach (StartupEntry entry in entries)
            if (AiThirdPartyIdlePolicy.CanManageStartup(app, entry) &&
                (!requireNativeAdmission || CanCommitOwnedStartup(entry)) &&
                !HasRegistrySlot(entry, entries, enabled: false))
                return entry;
        return null;
    }

    // The pre-WAL admission must prove the startup inventory identity is
    // bound to this EXACT native HKCU Run slot. ReadKey builds Id from
    // hive/view/path/name; a stale or malformed mismatched Id must never
    // become a durable owner even if the displayed executable looks valid.
    // Native mutation and the fresh prewrite readback retain their own gates.
    internal static bool CanCommitOwnedStartup(StartupEntry? entry) =>
        entry is { Enabled: true, Hive: Microsoft.Win32.RegistryHive.CurrentUser } &&
        string.Equals(entry.RunPath,
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            StringComparison.OrdinalIgnoreCase) &&
        StartupManagerService.IsAdmissibleRegistryStartupMove(entry) &&
        string.Equals(entry.Id,
            $"{entry.Hive}|{entry.View}|{entry.RunPath}|{entry.Name}",
            StringComparison.OrdinalIgnoreCase);

    // The second lookup happens against a FRESH native snapshot immediately
    // before a persisted startup mutation. Matching the journal identity is
    // mandatory; merely finding some other allowed launcher is insufficient.
    private static bool HasCurrentAdmissibleStartup(
        string app, Owner owner, IReadOnlyList<StartupEntry> entries,
        bool requireNativeAdmission = false)
    {
        foreach (StartupEntry entry in entries)
            if (IsSameStartup(entry, owner) &&
                AiThirdPartyIdlePolicy.CanManageStartup(app, entry) &&
                (!requireNativeAdmission || CanCommitOwnedStartup(entry)) &&
                !HasRegistrySlot(entry, entries, enabled: false))
                return true;
        return false;
    }

    // A postwrite disabled row is not sufficient if Run was concurrently
    // repopulated under the same name (even with a changed command).
    // Preserve the durable owner when readback is not unambiguous.
    private static bool IsStartupDisableConfirmed(
        Owner owner, StartupEntry original, IReadOnlyList<StartupEntry> rows,
        bool requireNativeAdmission = false) =>
        rows.Any(row => !row.Enabled && IsSameStartup(row, owner) &&
            (!requireNativeAdmission || CanRestoreOwnedStartup(owner, row))) &&
        !HasRegistrySlot(original, rows, enabled: true);

    // These direct loops avoid allocating a captured LINQ predicate on
    // every owned-startup recovery. Ordering is identical to FirstOrDefault
    // and the acceptance criteria are deliberately unchanged.
    private static StartupEntry? FindMatchingStartup(
        Owner owner, IReadOnlyList<StartupEntry> current)
    {
        foreach (StartupEntry row in current)
            if (IsSameStartup(row, owner)) return row;
        return null;
    }

    // A fresh restore candidate must be the exact persisted owner identity,
    // still disabled, and an explicitly allowed end-user launcher. Never
    // operate on a stale StartupEntry captured before awaited native work.
    private static StartupEntry? FindAdmissibleOwnedRestore(
        Owner owner, IReadOnlyList<StartupEntry> current,
        bool requireNativeAdmission = false)
    {
        // Registry value slots are identified by name, hive, view and path,
        // NOT command. A user-modified Run value occupies the destination
        // even if its command no longer equals our durable owner identity.
        // Do not attempt a native move into that occupied destination.
        if (HasOwnedStartupSlot(owner, current, enabled: true))
            return null;
        StartupEntry? entry = FindMatchingStartup(owner, current);
        return entry is { Enabled: false } &&
            (!requireNativeAdmission || CanRestoreOwnedStartup(owner, entry)) &&
            AiThirdPartyIdlePolicy.CanManageStartup(
                owner.AppKey, entry with { Enabled = true }) ? entry : null;
    }

    // Disabled registry inventory IDs refer to the PHYSICAL backup path,
    // unlike the active Run ID persisted in our owner. Never use the
    // enabled-entry CanCommitOwnedStartup gate for a legitimate restore.
    // ReadKey emits CurrentUser|View|RunPath\SpankyhoDisabled|Name.
    private static bool CanRestoreOwnedStartup(Owner owner, StartupEntry? entry) =>
        owner.Kind == "startup" && owner.OriginalEnabled &&
        entry is { Enabled: false, Hive: Microsoft.Win32.RegistryHive.CurrentUser } &&
        string.Equals(owner.StartupRunPath,
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.RunPath, owner.StartupRunPath,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.Name, owner.StartupName,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.Command, owner.StartupCommand,
            StringComparison.Ordinal) &&
        (int)entry.View == owner.StartupView &&
        string.Equals(owner.Id,
            $"CurrentUser|{entry.View}|{entry.RunPath}|{entry.Name}",
            StringComparison.OrdinalIgnoreCase) &&
        StartupManagerService.IsAdmissibleRegistryStartupMove(entry) &&
        string.Equals(entry.Id,
            $"CurrentUser|{entry.View}|{entry.RunPath}\\SpankyhoDisabled|{entry.Name}",
            StringComparison.OrdinalIgnoreCase);

    private static bool HasMatchingStartupState(
        Owner owner, IReadOnlyList<StartupEntry> current, bool enabled)
    {
        foreach (StartupEntry row in current)
            if (row.Enabled == enabled && IsSameStartup(row, owner)) return true;
        return false;
    }

    private static bool HasEnabledMatchingStartup(
        Owner owner, IReadOnlyList<StartupEntry> current) =>
        HasMatchingStartupState(owner, current, enabled: true);

    private static bool HasOwnedStartupSlot(
        Owner owner, IReadOnlyList<StartupEntry> current, bool enabled)
    {
        foreach (StartupEntry row in current)
            if (row.Enabled == enabled && IsSameStartupSlot(row, owner))
                return true;
        return false;
    }

    // Release an obsolete startup journal ONLY when a user-controlled Run
    // value occupies the original physical slot, its command has changed,
    // and the ENTIRE corresponding disabled slot is absent. Otherwise the
    // AI may still own a disabled value and must retain recovery ownership.
    // This is a pure read-only decision; it does not authorize a registry write.
    private static bool IsExternallyResolvedStartupWithoutDisabledSlot(
        Owner owner, IReadOnlyList<StartupEntry> current,
        bool requireNativeAdmission = false) =>
        owner.Kind == "startup" && owner.OriginalEnabled &&
        // A present-but-empty Run string is not evidence of a legitimate
        // user replacement. Never discard durable AI restore ownership
        // based on a blank/unusable enabled command.
        current.Any(row => row.Enabled && IsSameStartupSlot(row, owner) &&
            (!requireNativeAdmission || CanCommitOwnedStartup(row)) &&
            !string.IsNullOrWhiteSpace(row.Command)) &&
        !HasEnabledMatchingStartup(owner, current) &&
        !HasOwnedStartupSlot(owner, current, enabled: false);

    private static bool IsStartupRestoreConfirmed(
        Owner owner, IReadOnlyList<StartupEntry> current,
        bool requireNativeAdmission = false)
    {
        // A restored Run value is not enough if ANY hidden value still
        // occupies the same disabled slot, even after a user edited its
        // command. Keep the durable journal on ambiguous native evidence.
        bool enabled = false;
        foreach (StartupEntry row in current)
        {
            if (!IsSameStartupSlot(row, owner)) continue;
            if (!row.Enabled) return false;
            if (IsSameStartup(row, owner) &&
                (!requireNativeAdmission || CanCommitOwnedStartup(row)) &&
                AiThirdPartyIdlePolicy.CanManageStartup(owner.AppKey, row))
                enabled = true;
        }
        return enabled;
    }

    private static bool IsSameStartupSlot(StartupEntry row, Owner owner) =>
        string.Equals(row.SourceKind, "Registry", StringComparison.OrdinalIgnoreCase) &&
        row.Hive == Microsoft.Win32.RegistryHive.CurrentUser &&
        string.Equals(row.Name, owner.StartupName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(row.RunPath, owner.StartupRunPath, StringComparison.OrdinalIgnoreCase) &&
        (int)row.View == owner.StartupView;

    private static bool IsSameStartup(StartupEntry row, Owner owner) =>
        IsSameStartupSlot(row, owner) &&
        // Registry command values are bytes/characters, not an executable
        // name search. Even casing drift may indicate an external user edit.
        string.Equals(row.Command, owner.StartupCommand, StringComparison.Ordinal);

    private static string Key(string kind, string id) => kind + "|" + id;

    private const int MaxJournalBytes = 262144;

    // A FileInfo.Length check followed by File.ReadAllText has a TOCTOU
    // window if an external writer doesn't honor our cross-host lease.
    // Bound the ACTUAL bytes consumed, and reject invalid UTF-8 rather than
    // silently replacing malformed journal identity characters.
    private readonly record struct BoundedJournalPayload(
        string Text, byte[] Digest);

    // JsonSerializer's Dictionary behavior can silently overwrite duplicate
    // JSON property names BEFORE HasValidOwnedJournal sees them. Reject all
    // ambiguous owner identity at the JSON-token boundary, including exact,
    // case-only and escaped-equivalent duplicates. Parsing is performed only
    // on bounded journal loads/UI navigation, never on hot metadata probes.
    private static Journal? DeserializeJournalStrict(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 32,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("AI owner journal root is not an object");

        var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool ownedPresent = false;
        foreach (JsonProperty prop in document.RootElement.EnumerateObject())
        {
            if (!rootNames.Add(prop.Name))
                throw new InvalidDataException("Duplicate AI journal root property");
            if (!prop.NameEquals("Owned")) continue;
            ownedPresent = true;
            if (prop.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("AI owner journal entries must be an object");
            var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty entry in prop.Value.EnumerateObject())
            {
                if (owners.Count >= 64 || !owners.Add(entry.Name))
                    throw new InvalidDataException(
                        "Duplicate or excessive AI owner identity in JSON");
                if (entry.Value.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("AI owner value must be an object");
                // Duplicate/case-variant fields within a single owner could
                // also change e.g. Id/Kind/Pending during deserialization.
                var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty field in entry.Value.EnumerateObject())
                    if (!fields.Add(field.Name))
                        throw new InvalidDataException(
                            "Duplicate field in AI owner identity JSON");
            }
        }
        if (!ownedPresent)
            throw new InvalidDataException("AI owner journal has no Owned section");
        return document.RootElement.Deserialize<Journal>();
    }

    // Read-only AI page does not consume a durable SHA-256 journal stamp.
    // Keep the SAME byte bound and strict UTF-8 decoding as the leased
    // recovery path but skip the unused digest during UI navigation.
    private static string ReadBoundedJournalText(Stream source) =>
        DecodeBoundedJournalText(ReadBoundedJournalBytes(source));

    private static string DecodeBoundedJournalText(ArraySegment<byte> bytes) =>
        StrictJournalUtf8.GetString(bytes.AsSpan()).TrimStart('\uFEFF');

    // One bounded byte read serves both strict JSON decode on normal load
    // and hash-only conflict checking before an atomic journal swap.
    // A hash-only check must not decode another UTF-8 string per commit.
    private static ArraySegment<byte> ReadBoundedJournalBytes(Stream source)
    {
        // Journal files are seekable: allocate their exact remaining length
        // once instead of doubling MemoryStream's byte[] several times.
        // Keep the bounded streaming loop below: the file may grow after
        // checking its length or an input stream may not be seekable.
        int initialCapacity = 0;
        if (source.CanSeek)
        {
            long remaining = source.Length - source.Position;
            if (remaining > MaxJournalBytes)
                throw new InvalidDataException(
                    "AI app owner journal exceeded safe byte limit");
            if (remaining > 0)
                initialCapacity = checked((int)remaining);
        }
        using var output = new MemoryStream(initialCapacity);
        Span<byte> buffer = stackalloc byte[4096];
        while (true)
        {
            int n = source.Read(buffer);
            if (n == 0) break;
            if (output.Length + n > MaxJournalBytes)
                throw new InvalidDataException("AI app owner journal exceeded safe byte limit");
            output.Write(buffer[..n]);
        }
        if (output.Length == 0)
            throw new InvalidDataException("Empty AI app owner journal");
        // MemoryStream's exposable backing buffer survives Dispose.
        // Retain only the actual byte count, never its larger capacity:
        // otherwise a SHA digest could silently include uninitialized
        // capacity bytes and misidentify a valid ownership journal.
        return new ArraySegment<byte>(
            output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static BoundedJournalPayload ReadBoundedJournalPayload(Stream source)
    {
        ArraySegment<byte> bytes = ReadBoundedJournalBytes(source);
        return new BoundedJournalPayload(
            DecodeBoundedJournalText(bytes),
            SHA256.HashData(bytes.AsSpan()));
    }

    // Pure memory-only boundary verification. Never uses the actual owner
    // journal. A MemoryStream may have more CAPACITY than stored bytes,
    // especially at 4097 bytes; neither hashes nor UTF-8 decode may see it.
    internal static bool RunBoundedJournalByteSliceSmoke()
    {
        foreach (int length in new[]
            { 1, 4095, 4096, 4097, 8191, 8192, MaxJournalBytes })
        {
            byte[] original = new byte[length];
            Array.Fill(original, (byte)'x');
            original[0] = (byte)'{';
            original[^1] = (byte)'}';
            using var source = new MemoryStream(original, writable: false);
            ArraySegment<byte> slice = ReadBoundedJournalBytes(source);
            if (slice.Count != original.Length ||
                !slice.AsSpan().SequenceEqual(original) ||
                !SHA256.HashData(slice.AsSpan())
                    .AsSpan().SequenceEqual(SHA256.HashData(original)) ||
                DecodeBoundedJournalText(slice) != Encoding.UTF8.GetString(original))
                return false;
        }
        // A seekable source may be positioned partway through a larger
        // stream; initial capacity and integrity must reflect REMAINING
        // bytes, not the total length or stale prefix contents.
        byte[] prefix = new byte[17 + 4097];
        Array.Fill(prefix, (byte)'p', 0, 17);
        Array.Fill(prefix, (byte)'z', 17, 4097);
        using (var positioned = new MemoryStream(prefix, writable: false))
        {
            positioned.Position = 17;
            ArraySegment<byte> slice = ReadBoundedJournalBytes(positioned);
            if (slice.Count != 4097 ||
                !slice.AsSpan().SequenceEqual(prefix.AsSpan(17)) ||
                !SHA256.HashData(slice.AsSpan()).AsSpan()
                    .SequenceEqual(SHA256.HashData(prefix.AsSpan(17))))
                return false;
        }
        using var invalid = new MemoryStream(
            new byte[] { (byte)'{', 0xFF, (byte)'}' }, writable: false);
        try
        {
            DecodeBoundedJournalText(ReadBoundedJournalBytes(invalid));
            return false;
        }
        catch (DecoderFallbackException) { }
        return true;
    }

    // The ownership journal is persisted as UTF-8 without BOM. The exact
    // bytes checked for size are also written and hashed, so multibyte
    // identity characters never get an inconsistent byte-length check.
    private static byte[] SerializeBoundedJournalBytes(Journal journal)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        if (bytes.Length > MaxJournalBytes)
            throw new InvalidDataException(
                "Refusing to persist AI journal larger than safe 256KiB limit");
        return bytes;
    }

    // Pure serialization regression: the new single UTF-8 payload must be
    // byte-for-byte compatible with the previous Serialize+WriteAllText
    // format. No real journal, registry, service, or disk writes occur.
    internal static bool RunUtf8JournalWriteParitySmoke()
    {
        DateTime at = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        string id = $"CurrentUser|Registry64|{run}|Česká hudba 😀";
        string ownerKey = Key("startup", id);
        var owner = new Owner("startup", id, "spotify", at, true,
            false, "Česká hudba 😀", "\"C:\\Apps\\Music.exe\"",
            run, 256, false);
        var journal = new Journal(
            new Dictionary<string, Owner> { [ownerKey] = owner },
            new Dictionary<string, FeedbackOutcome>
            {
                [ownerKey] = new FeedbackOutcome(at, "deferred-until-next-startup",
                    0, AppKey: "spotify")
            });
        string legacyText = JsonSerializer.Serialize(journal);
        byte[] newBytes = SerializeBoundedJournalBytes(journal);
        if (!newBytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(legacyText)) ||
            newBytes.Length != Encoding.UTF8.GetByteCount(legacyText) ||
            newBytes.Length == 0)
            return false;
        using var uiView = new MemoryStream(newBytes, writable: false);
        if (ReadBoundedJournalText(uiView) != legacyText) return false;
        using var valid = new MemoryStream(newBytes, writable: false);
        var loaded = ReadBoundedJournalPayload(valid);
        if (loaded.Text != legacyText ||
            !MatchesJournalDigest(loaded.Digest, SHA256.HashData(newBytes)) ||
            DeserializeJournalStrict(loaded.Text) is not { Owned.Count: 1 } ||
            !HasValidOwnedJournal(DeserializeJournalStrict(loaded.Text)?.Owned))
            return false;

        // BOM-bearing external legacy journals remain readable while new
        // writes are deterministic UTF-8 without a BOM.
        byte[] withBom = new byte[newBytes.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        newBytes.CopyTo(withBom, 3);
        using var bom = new MemoryStream(withBom, writable: false);
        if (ReadBoundedJournalPayload(bom).Text != legacyText) return false;
        using var bomUi = new MemoryStream(withBom, writable: false);
        if (ReadBoundedJournalText(bomUi) != legacyText) return false;

        var tooLarge = journal with
        {
            Owned = new Dictionary<string, Owner>
            {
                [ownerKey] = owner with
                {
                    StartupCommand = new string('x', MaxJournalBytes)
                }
            }
        };
        try
        {
            _ = SerializeBoundedJournalBytes(tooLarge);
            return false;
        }
        catch (InvalidDataException) { }

        using var oversizedHashOnly =
            new MemoryStream(new byte[MaxJournalBytes + 1]);
        try
        {
            _ = ReadBoundedJournalBytes(oversizedHashOnly);
            return false;
        }
        catch (InvalidDataException) { }
        using var emptyHashOnly = new MemoryStream();
        try
        {
            _ = ReadBoundedJournalBytes(emptyHashOnly);
            return false;
        }
        catch (InvalidDataException) { }
        return true;
    }

    // These are exclusively synthetic JSON inputs; never use or damage
    // the user's actual ProgramData owner journal for fault injection.
    private static bool RunDuplicateJournalPropertySmoke()
    {
        static bool Rejects(string json)
        {
            try { _ = DeserializeJournalStrict(json); return false; }
            catch (InvalidDataException) { return true; }
        }
        try
        {
            return DeserializeJournalStrict(@"{""Owned"":{}}")?.Owned.Count == 0 &&
                Rejects(@"{""Owned"":{},""Owned"":{}}") &&
                Rejects(@"{""Owned"":{},""owned"":{}}") &&
                Rejects(@"{""Owned"":{""service|Steam Client Service"":{},""service|Steam Client Service"":{}}}") &&
                Rejects(@"{""Owned"":{""service|Steam Client Service"":{},""SERVICE|STEAM CLIENT SERVICE"":{}}}") &&
                Rejects(@"{""Owned"":{""s\u0065rvice|Steam Client Service"":{},""service|Steam Client Service"":{}}}") &&
                Rejects(@"{""Owned"":{""service|Steam Client Service"":{""Kind"":""service"",""Kind"":""task""}}}") &&
                Rejects(@"{""Owned"":{""service|Steam Client Service"":{""Kind"":""service"",""kind"":""task""}}}") &&
                Rejects(@"{""Feedback"":{}}") &&
                Rejects(@"[]");
        }
        catch { return false; }
    }

    private static bool RunBoundedJournalSmoke()
    {
        try
        {
            using var valid = new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"Owned\":{},\"Feedback\":{},\"References\":{}}"));
            BoundedJournalPayload validPayload = ReadBoundedJournalPayload(valid);
            if (!validPayload.Text.Contains("\"Owned\"", StringComparison.Ordinal) ||
                !MatchesJournalDigest(validPayload.Digest, validPayload.Digest))
                return false;
            // Same byte count and identical simulated file timestamps would
            // pass metadata-only stamp checks, but the SHA-256 must reject
            // replaced content even if a noncompliant external writer
            // preserves those attributes.
            using var tampered = new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"Owned\":{},\"Feedback\":{},\"References\":[]}"
            ));
            BoundedJournalPayload other = ReadBoundedJournalPayload(tampered);
            if (validPayload.Text.Length != other.Text.Length ||
                MatchesJournalDigest(validPayload.Digest, other.Digest) ||
                MatchesJournalDigest(null, validPayload.Digest) ||
                MatchesJournalDigest(validPayload.Digest, null))
                return false;
            using var overflow = new MemoryStream(new byte[MaxJournalBytes + 1]);
            try
            {
                _ = ReadBoundedJournalText(overflow);
                return false;
            }
            catch (InvalidDataException) { }
            using var invalidUtf8 = new MemoryStream(new byte[] { 0xFF, 0xFE, 0xFA });
            try
            {
                _ = ReadBoundedJournalText(invalidUtf8);
                return false;
            }
            catch (DecoderFallbackException) { }
            return RunDuplicateJournalPropertySmoke();
        }
        catch { return false; }
    }

    // No live registry/SCM/filesystem calls: both retention modes must
    // evict expired/old outcomes without losing an active owned rollback
    // record or a still-measured passive A2 reference.
    internal static bool RunFeedbackRetentionSmoke()
    {
        DateTime now = new(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);
        static FeedbackOutcome Feedback(DateTime at) =>
            new(at, "deferred-until-next-startup", 0, AppKey: "spotify");
        var worker = new AiThirdPartyIdleAutomationService();
        const string ownedKey = "startup|protected-owner";
        const string referenceKey = "service|Steam Client Service";
        worker._owned[ownedKey] = new Owner(
            "startup", "protected-owner", "spotify", now, true, false);
        worker._references[referenceKey] = new ReferenceTrial(
            "Steam Client Service", "steam", now, 8, 8192, 7, 8300);
        worker._feedback[ownedKey] = Feedback(now.AddDays(-31));
        worker._feedback[referenceKey] = Feedback(now.AddDays(-31));
        worker._feedback["startup|boundary"] = Feedback(now.AddDays(-30));
        for (int i = 0; i < 130; i++)
            worker._feedback["startup|history-" + i] =
                Feedback(now.AddDays(i < 80 ? -32 : -1));
        worker.PruneFeedback(now);
        if (worker._feedback.Count != 53 ||
            !worker._feedback.ContainsKey(ownedKey) ||
            !worker._feedback.ContainsKey(referenceKey) ||
            !worker._feedback.ContainsKey("startup|boundary") ||
            worker._feedback.ContainsKey("startup|history-0") ||
            !worker._feedback.ContainsKey("startup|history-129"))
            return false;

        var capped = new AiThirdPartyIdleAutomationService();
        capped._owned[ownedKey] = worker._owned[ownedKey];
        capped._references[referenceKey] = worker._references[referenceKey];
        capped._feedback[ownedKey] = Feedback(now.AddDays(-29));
        capped._feedback[referenceKey] = Feedback(now.AddDays(-29));
        for (int i = 0; i < 150; i++)
            capped._feedback["startup|history-" + i] =
                Feedback(now.AddDays(-25).AddMinutes(i));
        capped.PruneFeedback(now);
        return capped._feedback.Count == 96 &&
            capped._feedback.ContainsKey(ownedKey) &&
            capped._feedback.ContainsKey(referenceKey) &&
            !capped._feedback.ContainsKey("startup|history-0") &&
            !capped._feedback.ContainsKey("startup|history-55") &&
            capped._feedback.ContainsKey("startup|history-56") &&
            capped._feedback.ContainsKey("startup|history-149");
    }

    // Oracle parity: the previous stable LINQ pipeline is retained only
    // inside this deterministic in-memory test, never on the UI hot path.
    internal static bool RunFeedbackTopThreeParitySmoke()
    {
        DateTime now = new(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);
        static FeedbackOutcome Feedback(DateTime at) =>
            new(at, "deferred-until-next-startup", 0, AppKey: "spotify");
        var outcomes = new Dictionary<string, FeedbackOutcome>
        {
            ["startup|a"] = Feedback(now.AddMinutes(-10)),
            ["startup|b"] = Feedback(now.AddMinutes(-2)),
            ["startup|c"] = Feedback(now.AddMinutes(-1)),
            ["startup|d"] = Feedback(now.AddMinutes(-1)),
            ["startup|e"] = Feedback(now.AddMinutes(-3)),
            ["STARTUP|B"] = Feedback(now),
            ["startup|old"] = Feedback(now.AddDays(-15)),
            ["startup|future"] = Feedback(now.AddMinutes(1)),
            ["service|WinDefend"] = Feedback(now)
        };
        var journal = new Journal(new Dictionary<string, Owner>(), outcomes);
        var expected = outcomes
            .Where(x => IsValidPersistedFeedback(x.Key, x.Value) &&
                x.Value.UpdatedUtc <= now &&
                now - x.Value.UpdatedUtc <= TimeSpan.FromDays(14))
            .OrderByDescending(x => x.Value.UpdatedUtc)
            .DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3).ToArray();
        var actual = SelectTrustedFeedbackForUi(journal, now);
        if (actual.Length != 3 || expected.Length != actual.Length)
            return false;
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i].Kind + "|" + actual[i].Id != expected[i].Key ||
                actual[i].AppKey != expected[i].Value.AppKey ||
                actual[i].UpdatedUtc != expected[i].Value.UpdatedUtc ||
                actual[i].Verdict != expected[i].Value.Verdict)
                return false;
        }
        if (SelectTrustedFeedbackForUi(journal, now.AddDays(15)).Length != 0 ||
            SelectTrustedFeedbackForUi(new Journal(
                new Dictionary<string, Owner>(),
                new Dictionary<string, FeedbackOutcome>()), now).Length != 0)
            return false;
        var excessive = new Dictionary<string, FeedbackOutcome>();
        for (int i = 0; i < 257; i++)
            excessive["startup|" + i.ToString(CultureInfo.InvariantCulture)] =
                Feedback(now.AddHours(-1));
        return SelectTrustedFeedbackForUi(
            new Journal(new Dictionary<string, Owner>(), excessive), now).Length == 0;
    }

    // UI projection is pure and NEVER reads real Windows resources.
    internal static bool RunFeedbackProjectionSmoke()
    {
        DateTime now = DateTime.UtcNow;
        var owner = new Owner("service", "Steam Client Service", "steam",
            now.AddMinutes(-2), true, true, OriginalServiceStartMode: 3);
        string ownerKey = Key(owner.Kind, owner.Id);
        var feedback = new Dictionary<string, FeedbackOutcome>
        {
            [ownerKey] = new FeedbackOutcome(now.AddMinutes(-1),
                "inconclusive", 2, AppKey: "steam"),
            ["task|\\Mozilla\\Firefox Update Agent 308046B0AF4A39CB"] =
                new FeedbackOutcome(now, "inconclusive", 1, AppKey: "firefox"),
            ["task|\\Mozilla\\Firefox Default Browser Agent 308046B0AF4A39CB"] =
                new FeedbackOutcome(now, "inconclusive", 1, AppKey: "steam"),
            ["service|WinDefend"] =
                new FeedbackOutcome(now, "positive-system-trend", 3,
                    AppKey: "steam")
        };
        var journal = new Journal(
            new Dictionary<string, Owner> { [ownerKey] = owner }, feedback);
        var trusted = SelectTrustedFeedbackForUi(journal, now);
        var invalidOwners = journal with
        {
            Owned = new Dictionary<string, Owner>
            {
                [ownerKey] = owner with { AppKey = "defender" }
            }
        };
        // A verified HKCU Run opt-out can outlive historical 14-day UI
        // retention. Keep showing the pending-next-login verdict ONLY while
        // a matching, completed durable startup owner remains, not after
        // restoration, for pending WAL entries or for ordinary service data.
        var epicOwner = new Owner("startup",
            @"CurrentUser|Registry64|Software\Microsoft\Windows\CurrentVersion\Run|EpicGamesLauncher",
            "epic", now.AddDays(-30), true, false,
            "EpicGamesLauncher", @"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
            @"Software\Microsoft\Windows\CurrentVersion\Run", 256, Pending: false);
        string epicKey = Key(epicOwner.Kind, epicOwner.Id);
        var oldFeedback = new Dictionary<string, FeedbackOutcome>
        {
            [epicKey] = new FeedbackOutcome(now.AddDays(-20),
                "deferred-until-next-startup", 0, AppKey: "epic")
        };
        var longOwnedJournal = new Journal(new Dictionary<string, Owner>
        {
            [epicKey] = epicOwner
        }, oldFeedback);
        var longOwned = SelectTrustedFeedbackForUi(longOwnedJournal, now);
        if (longOwned.Length != 1 || longOwned[0].AppKey != "epic" ||
            longOwned[0].Verdict != "deferred-until-next-startup" ||
            SelectTrustedFeedbackForUi(longOwnedJournal with
                { Owned = new Dictionary<string, Owner>() }, now).Length != 0 ||
            SelectTrustedFeedbackForUi(longOwnedJournal with
                {
                    Owned = new Dictionary<string, Owner>
                    {
                        [epicKey] = epicOwner with { Pending = true }
                    }
                }, now).Length != 0 ||
            SelectTrustedFeedbackForUi(longOwnedJournal with
                {
                    Owned = new Dictionary<string, Owner>
                    {
                        [epicKey] = epicOwner with { AppKey = "discord" }
                    }
                }, now).Length != 0)
            return false;
        return trusted.Length == 1 &&
            trusted[0].Kind == "service" &&
            trusted[0].Id == "Steam Client Service" &&
            trusted[0].AppKey == "steam" &&
            SelectTrustedFeedbackForUi(invalidOwners, now).Length == 0 &&
            SelectTrustedFeedbackForUi(journal, now.AddDays(15)).Length == 0 &&
            !MissingJournalRequiresFailClosed(false, false, 0, 0) &&
            MissingJournalRequiresFailClosed(true, true, 0, 0) &&
            MissingJournalRequiresFailClosed(false, false, 1, 0) &&
            MissingJournalRequiresFailClosed(false, false, 0, 1);
    }

    // Pure oracle parity for BOTH paths through durable learning repair:
    // sorted <=128 clean inputs use the single-pass fast path; unsorted,
    // colliding, oversized, and invalid data must exactly match the old
    // stable LINQ sort/dedup/filter contract, including dictionary order.
    internal static bool RunLearningNormalizationParitySmoke()
    {
        DateTime now = DateTime.UtcNow.AddMinutes(-10);
        static FeedbackOutcome Feedback(DateTime at) =>
            new(at, "deferred-until-next-startup", 0, AppKey: "spotify");
        var owner = new Owner("service", "Steam Client Service", "steam",
            now, true, true);
        var owned = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase)
        {
            [Key(owner.Kind, owner.Id)] = owner
        };
        var trial = new ReferenceTrial("Steam Client Service", "steam",
            now, 8, 8192, 7, 8300);

        static bool MatchesLegacy(Journal input)
        {
            var legacyFeedback = (input.Feedback ??
                    new Dictionary<string, FeedbackOutcome>())
                .Where(x => IsValidPersistedFeedback(x.Key, x.Value))
                .OrderByDescending(x => x.Value.UpdatedUtc)
                .DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Take(128)
                .ToDictionary(x => x.Key, x => x.Value,
                    StringComparer.OrdinalIgnoreCase);
            var legacyReferences = (input.References ??
                    new Dictionary<string, ReferenceTrial>())
                .Where(x => IsValidPersistedReference(x.Key, x.Value))
                .DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Value,
                    StringComparer.OrdinalIgnoreCase);
            Journal actual = RecoverValidLearning(input, out bool degraded);
            return ReferenceEquals(actual.Owned, input.Owned) &&
                actual.Feedback is not null && actual.References is not null &&
                actual.Feedback.SequenceEqual(legacyFeedback) &&
                actual.References.SequenceEqual(legacyReferences) &&
                degraded ==
                    (legacyFeedback.Count != (input.Feedback?.Count ?? 0) ||
                     legacyReferences.Count != (input.References?.Count ?? 0));
        }

        if (!MatchesLegacy(new Journal(owned)) ||
            !MatchesLegacy(new Journal(owned,
                new Dictionary<string, FeedbackOutcome>(),
                new Dictionary<string, ReferenceTrial>())))
            return false;

        var sorted = new Dictionary<string, FeedbackOutcome>(
            StringComparer.OrdinalIgnoreCase);
        var reverse = new Dictionary<string, FeedbackOutcome>(
            StringComparer.OrdinalIgnoreCase);
        // Count boundary 128 and 129; the first is a canonical fast path
        // and the latter MUST use full stable top-128 fallback.
        for (int i = 0; i < 129; i++)
        {
            string key = "startup|fixture-" + i.ToString(CultureInfo.InvariantCulture);
            var outcome = Feedback(now.AddMinutes(-i / 2));
            if (i < 128) sorted[key] = outcome;
        }
        for (int i = 128; i >= 0; i--)
            reverse["startup|fixture-" + i.ToString(CultureInfo.InvariantCulture)] =
                Feedback(now.AddMinutes(-i / 2));
        if (!MatchesLegacy(new Journal(owned, sorted)) ||
            !MatchesLegacy(new Journal(owned, reverse)))
            return false;

        var collisions = new Dictionary<string, FeedbackOutcome>(
            StringComparer.Ordinal)
        {
            ["startup|discord"] = Feedback(now.AddMinutes(-20)),
            ["STARTUP|DISCORD"] = Feedback(now.AddMinutes(-1)),
            ["startup|other"] = Feedback(now.AddMinutes(-1)),
            ["STARTUP|OTHER"] = Feedback(now.AddMinutes(-1)),
            ["service|WinDefend"] = Feedback(now),
            ["startup|bad"] = Feedback(now) with { AppKey = "defender" }
        };
        var collidingReferences = new Dictionary<string, ReferenceTrial>(
            StringComparer.Ordinal)
        {
            ["service|WinDefend"] = trial with { Id = "WinDefend" },
            ["SERVICE|STEAM CLIENT SERVICE"] = trial,
            ["service|Steam Client Service"] = trial with { Samples = 1 }
        };
        if (!MatchesLegacy(new Journal(owned, collisions, collidingReferences)))
            return false;

        var invalidFirst = new Dictionary<string, ReferenceTrial>(
            StringComparer.Ordinal)
        {
            ["service|Steam Client Service"] = trial with { Id = "WinDefend" },
            ["SERVICE|STEAM CLIENT SERVICE"] = trial
        };
        var sortedWithInvalid = new Dictionary<string, FeedbackOutcome>(
            StringComparer.Ordinal)
        {
            ["service|WinDefend"] = Feedback(now),
            ["startup|accepted"] = Feedback(now.AddMinutes(-1)),
            ["startup|forged"] = Feedback(now.AddMinutes(-2)) with
            {
                Verdict = "positive-system-trend", Samples = 3
            }
        };
        return MatchesLegacy(new Journal(owned, sortedWithInvalid, invalidFirst));
    }

    // In-memory cross-host recovery fixture; NO real journal or Windows I/O.
    internal static bool RunLearningRepairSmoke()
    {
        DateTime now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
        var owner = new Owner("service", "Steam Client Service", "steam",
            now, true, true, OriginalServiceStartMode: 3);
        string key = Key(owner.Kind, owner.Id);
        var valid = new FeedbackOutcome(now, "inconclusive", 2, AppKey: "steam");
        var reference = new ReferenceTrial(owner.Id, "steam", now,
            8, 8192, 7, 8300);
        var pristine = new Journal(
            new Dictionary<string, Owner> { [key] = owner },
            new Dictionary<string, FeedbackOutcome> { [key] = valid },
            new Dictionary<string, ReferenceTrial> { [key] = reference });
        Journal clean = RecoverValidLearning(pristine, out bool wasDamaged);
        if (wasDamaged || clean.Owned.Count != 1 ||
            clean.Feedback?.Count != 1 || clean.References?.Count != 1)
            return false;

        var malformed = pristine with
        {
            Feedback = new Dictionary<string, FeedbackOutcome>
            {
                [key] = valid,
                ["startup|foreign"] = valid with { AppKey = "defender" },
                // A corrupted optional record must be quarantined without
                // crashing recovery of the valid Steam service owner.
                ["SERVICE|STEAM CLIENT SERVICE"] = valid with
                    { UpdatedUtc = now.AddDays(-1), AppKey = null! },
                [@"task|\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB"] =
                    valid with { Verdict = "deferred-until-task-trigger",
                        Samples = 0, AppKey = null! }
            },
            References = new Dictionary<string, ReferenceTrial>
            {
                [key] = reference,
                ["service|WinDefend"] = reference with { Id = "WinDefend" }
            }
        };
        Journal repaired = RecoverValidLearning(malformed, out bool degraded);
        return degraded &&
            ReferenceEquals(repaired.Owned, malformed.Owned) &&
            repaired.Owned.TryGetValue(key, out var original) &&
            ReferenceEquals(original, owner) &&
            repaired.Feedback?.Count == 1 &&
            repaired.Feedback[key].Verdict == "inconclusive" &&
            repaired.References?.Count == 1 &&
            repaired.References.ContainsKey(key) &&
            !IsValidPersistedOwner(key, owner with { AppKey = "defender" });
    }

    // Reusing the existing AI lookup must never expand the set of journal
    // entries that can justify a native startup/service/task modification.
    // Compare the previous linear allowlist check with the static index,
    // and exercise both trusted startup OWNER and feedback validation.
    internal static bool RunJournalAppIndexParitySmoke()
    {
        DateTime at = new(2026, 10, 23, 3, 0, 0, DateTimeKind.Utc);
        var names = new List<string?> { null, "", "steam", "STEAM",
            "spotify", "SPOTIFY", "firefox", "FIREFOX", "windows-security",
            "onedrive", "chrome", "discord ", "steam.exe", "defender",
            "nonexistent", "český-účet", "STEAM\\0" };
        foreach (string key in AiThirdPartyIdlePolicy.AppKeys)
        {
            names.Add(key);
            names.Add(key.ToUpperInvariant());
        }
        foreach (string? key in names)
        {
            bool wasAllowed = AiThirdPartyIdlePolicy.AppKeys.Contains(
                key, StringComparer.OrdinalIgnoreCase);
            if (IsManagedJournalApp(key) != wasAllowed)
                return false;
            if (key is null) continue;

            const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
            string id = $"CurrentUser|Registry64|{run}|Fixture";
            string ownerKey = Key("startup", id);
            var owner = new Owner("startup", id, key, at,
                true, false, "Fixture", @"C:\Apps\Fixture.exe",
                run, 256, false);
            var feedback = new FeedbackOutcome(at,
                "deferred-until-next-startup", 0, AppKey: key);
            if (IsValidPersistedOwner(ownerKey, owner) != wasAllowed ||
                IsValidPersistedFeedback(ownerKey, feedback) != wasAllowed ||
                MatchesFeedbackResource(ownerKey, key) != wasAllowed)
                return false;
        }
        var steamFeedback = new FeedbackOutcome(at, "inconclusive", 2,
            AppKey: "STEAM");
        string steamKey = "SERVICE|STEAM CLIENT SERVICE";
        return IsValidPersistedFeedback(steamKey, steamFeedback) &&
            IsValidPersistedReference(steamKey,
                new ReferenceTrial("Steam Client Service", "steam",
                    at, 8, 8192, 7, 8300)) &&
            !IsValidPersistedFeedback("service|WinDefend", steamFeedback) &&
            // Firefox is a task-only owner, not a general startup app. Its
            // correctly identified optional task feedback must be retained.
            IsValidPersistedFeedback(
                @"task|\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB",
                new FeedbackOutcome(at, "deferred-until-task-trigger", 0,
                    AppKey: "firefox"));
    }

    // Synthetic asynchronous race: the tick saw DISABLED, but the user's
    // CURRENT HKCU Run snapshot says ENABLED. Only the current snapshot may
    // decide whether to release a held owner. No real registry operations.
    internal static async Task<bool> RunFreshOwnedStartupOverrideSmoke()
    {
        DateTime at = new(2026, 10, 24, 21, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string command = "\"C:\\Apps\\Spotify\\Spotify.exe\"";
        var owner = new Owner("startup", "fixture", "spotify", at,
            true, false, "Spotify", command, run, 0);
        var disabled = new StartupEntry("fixture", "Spotify", command,
            Microsoft.Win32.RegistryHive.CurrentUser, run, false, "Spotify",
            @"C:\Apps\Spotify\Spotify.exe",
            View: Microsoft.Win32.RegistryView.Default);
        if (HasEnabledMatchingStartup(owner, new[] { disabled }))
            return false; // The original shared inventory was stale.
        int reads = 0;
        Task<IReadOnlyList<StartupEntry>> Read(StartupEntry row,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads++;
            return Task.FromResult<IReadOnlyList<StartupEntry>>(new[] { row });
        }
        if (!await HasFreshEnabledOwnedStartupAsync(owner,
                t => Read(disabled with { Enabled = true }, t),
                CancellationToken.None).ConfigureAwait(false) ||
            await HasFreshEnabledOwnedStartupAsync(owner,
                t => Read(disabled, t), CancellationToken.None)
                .ConfigureAwait(false) ||
            !await HasFreshEnabledOwnedStartupAsync(owner,
                t => Read(disabled with
                {
                    Enabled = true, Command = command + " --changed"
                }, t), CancellationToken.None).ConfigureAwait(false) ||
            !await HasFreshEnabledOwnedStartupAsync(owner,
                t => Read(disabled with
                {
                    Enabled = true, Name = "sPoTiFy"
                }, t), CancellationToken.None).ConfigureAwait(false) ||
            await HasFreshEnabledOwnedStartupAsync(owner,
                t => Read(disabled with
                {
                    Enabled = true, View = Microsoft.Win32.RegistryView.Registry64
                }, t), CancellationToken.None).ConfigureAwait(false) ||
            await HasFreshEnabledOwnedStartupAsync(owner,
                t => Read(disabled with
                {
                    Enabled = true, Name = "Other"
                }, t), CancellationToken.None).ConfigureAwait(false) ||
            reads != 6)
            return false;
        // Inaccessible fresh data must abort the cycle instead of awarding
        // six more hours to a resource the user might have re-enabled.
        try
        {
            await HasFreshEnabledOwnedStartupAsync(owner,
                _ => Task.FromException<IReadOnlyList<StartupEntry>>(
                    new IOException("synthetic read error")),
                CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        catch (IOException) { }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await HasFreshEnabledOwnedStartupAsync(owner,
                t => Read(disabled with { Enabled = true }, t),
                cancelled.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) { }
        return reads == 6;
    }

    // A reversible AI disable must not adopt a Run value whose target name
    // is already present in the disabled registry partner. Value-name
    // collisions apply even when the command differs. All synthetic data.
    internal static bool RunStartupOccupiedDestination165Smoke()
    {
        DateTime at = new(2026, 10, 24, 23, 30, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string command = "\"" + @"C:\Apps\Spotify\Spotify.exe" + "\"";
        var owner = new Owner("startup", "fixture", "spotify", at,
            true, false, "Spotify", command, run, 0);
        var active = new StartupEntry("fixture", "Spotify", command,
            Microsoft.Win32.RegistryHive.CurrentUser, run, true,
            "Spotify", @"C:\Apps\Spotify\Spotify.exe",
            View: Microsoft.Win32.RegistryView.Default);
        var disabled = active with { Enabled = false };
        var foreignCommand = disabled with { Command = command + " --user-owned" };
        var mixedCase = disabled with { Name = "sPoTiFy" };
        var differentView = disabled with
            { View = Microsoft.Win32.RegistryView.Registry64 };
        var differentName = disabled with { Name = "Other" };
        var differentHive = disabled with
            { Hive = Microsoft.Win32.RegistryHive.LocalMachine };
        var differentPath = disabled with { RunPath = run + @"\Other" };

        bool Allowed(IReadOnlyList<StartupEntry> rows) =>
            ReferenceEquals(active, FindAdmissibleStartup("spotify", rows)) &&
            HasCurrentAdmissibleStartup("spotify", owner, rows);
        bool Blocked(IReadOnlyList<StartupEntry> rows) =>
            FindAdmissibleStartup("spotify", rows) is null &&
            !HasCurrentAdmissibleStartup("spotify", owner, rows);

        return Allowed(new[] { active }) &&
            Blocked(new[] { active, disabled }) &&
            Blocked(new[] { disabled, active }) &&
            Blocked(new[] { active, foreignCommand }) &&
            Blocked(new[] { mixedCase, active }) &&
            Allowed(new[] { active, differentView }) &&
            Allowed(new[] { active, disabled with { SourceKind = null! } }) &&
            Allowed(new[] { active, disabled with { Name = null! } }) &&
            Allowed(new[] { active, disabled with { RunPath = null! } }) &&
            Allowed(new[] { differentName, active }) &&
            Allowed(new[] { active, differentHive }) &&
            Allowed(new[] { differentPath, active }) &&
            // After a native write, only a disabled exact owner with no
            // surviving enabled same-name registry slot confirms success.
            IsStartupDisableConfirmed(owner, active, new[] { disabled }) &&
            !IsStartupDisableConfirmed(owner, active, new[] { disabled, active }) &&
            !IsStartupDisableConfirmed(owner, active, new[]
                { disabled, active with { Command = command + " --changed" } }) &&
            !IsStartupDisableConfirmed(owner, active, new[] { foreignCommand }) &&
            IsStartupDisableConfirmed(owner, active, new[]
                { disabled, differentView });
    }

    // Synthetic HKCU Run/SpankyhoDisabled collision test. A failed or
    // interrupted third-party move can leave both sides populated with the
    // same command. Neither native restore nor journal release is then safe.
    internal static bool RunStartupDualStateRestore164Smoke()
    {
        DateTime at = new(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string command = "\"" + @"C:\Apps\Spotify\Spotify.exe" + "\"";
        var owner = new Owner("startup", "fixture", "spotify", at,
            true, false, "Spotify", command, run, 0);
        var disabled = new StartupEntry("fixture", "Spotify", command,
            Microsoft.Win32.RegistryHive.CurrentUser, run, false,
            "Spotify", @"C:\Apps\Spotify\Spotify.exe",
            View: Microsoft.Win32.RegistryView.Default);
        var enabled = disabled with { Enabled = true };
        var otherCommand = disabled with { Command = command + " --user-edit" };
        var otherView = disabled with { View = Microsoft.Win32.RegistryView.Registry64 };
        return IsStartupRestoreConfirmed(owner, new[] { enabled }) &&
            !IsStartupRestoreConfirmed(owner, new[] { disabled }) &&
            !IsStartupRestoreConfirmed(owner, new[] { enabled, disabled }) &&
            !IsStartupRestoreConfirmed(owner, new[] { disabled, enabled }) &&
            !IsStartupRestoreConfirmed(owner, new[]
                { enabled with { SourceKind = null! } }) &&
            !IsStartupRestoreConfirmed(owner, new[]
                { enabled with { Name = null! } }) &&
            !IsStartupRestoreConfirmed(owner, new[]
                { enabled with { RunPath = null! } }) &&
            FindAdmissibleOwnedRestore(owner, new[]
                { disabled with { RunPath = null! } }) is null &&
            HasEnabledMatchingStartup(owner, new[] { disabled, enabled }) &&
            FindAdmissibleOwnedRestore(owner, new[] { disabled, enabled }) is null &&
            FindAdmissibleOwnedRestore(owner, new[] { enabled, disabled }) is null &&
            ReferenceEquals(disabled,
                FindAdmissibleOwnedRestore(owner, new[] { disabled })) &&
            !IsStartupRestoreConfirmed(owner, new[] { enabled, otherCommand }) &&
            !IsStartupRestoreConfirmed(owner, new[] { otherCommand, enabled }) &&
            !IsStartupRestoreConfirmed(owner, new[] { enabled with
                { Command = command + " --user-edit" } }) &&
            FindAdmissibleOwnedRestore(owner, new[] { disabled, enabled with
                { Command = command + " --user-edit" } }) is null &&
            FindAdmissibleOwnedRestore(owner, new[] { enabled with
                { Command = command + " --user-edit" }, disabled }) is null &&
            IsStartupRestoreConfirmed(owner, new[] { otherView, enabled }) &&
            FindAdmissibleOwnedRestore(owner, new[] { otherView, disabled }) is not null;
    }

    // v16.181: Do not treat the unnamed Run key (Default) value as an
    // autorun entry. Malformed/nonstring/blank named values may be displayed,
    // but native toggles and AI auto-mutation are not permitted.
    // Everything here is an in-memory synthetic fixture.
    internal static bool RunStartupRegistryAdmission181Smoke()
    {
        static bool Classified(string? name, object? raw, bool expectedPresent,
            bool expectedToggle, string expectedCommand)
        {
            bool present = StartupManagerService.TryClassifyRegistryStartupSlot(
                name, raw, out string command, out bool canToggle);
            return present == expectedPresent && canToggle == expectedToggle &&
                command == expectedCommand;
        }
        if (!Classified("Spotify", @"C:\Apps\Spotify\Spotify.exe",
                true, true, @"C:\Apps\Spotify\Spotify.exe") ||
            !Classified("Spotify", "%APPDATA%\\Spotify.exe",
                true, true, "%APPDATA%\\Spotify.exe") ||
            !Classified("", @"C:\Apps\Spotify\Spotify.exe",
                false, false, @"C:\Apps\Spotify\Spotify.exe") ||
            !Classified("   ", @"C:\Apps\Spotify\Spotify.exe",
                false, false, @"C:\Apps\Spotify\Spotify.exe") ||
            !Classified("Spotify", null, false, false, "") ||
            !Classified("Spotify", "", true, false, "") ||
            !Classified("Spotify", "  ", true, false, "  ") ||
            !Classified("Spotify", 17, true, false, "17") ||
            !Classified("Spotify", new byte[] { 1 }, true, false,
                "System.Byte[]"))
            return false;

        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var good = new StartupEntry($"CurrentUser|Default|{run}|Spotify", "Spotify",
            @"C:\Apps\Spotify\Spotify.exe",
            Microsoft.Win32.RegistryHive.CurrentUser, run, true,
            "Spotify", @"C:\Apps\Spotify\Spotify.exe",
            View: Microsoft.Win32.RegistryView.Default);
        var nativeGood = good with
        {
            View = Microsoft.Win32.RegistryView.Registry64,
            Id = $"CurrentUser|Registry64|{run}|Spotify"
        };
        // If the first recognized row is unsupported, choose the second
        // admissible HKCU registry view rather than ending the opportunity.
        var nativeOwner = new Owner("startup", nativeGood.Id, "spotify",
            new DateTime(2026, 10, 26, 17, 0, 0, DateTimeKind.Utc),
            true, false, nativeGood.Name, nativeGood.Command,
            nativeGood.RunPath, (int)nativeGood.View);
        var nativeDisabled = nativeGood with
        {
            Enabled = false,
            Id = $"CurrentUser|Registry64|{run}\\SpankyhoDisabled|Spotify"
        };
        var userChangedNative = nativeGood with
            { Command = nativeGood.Command + " --manual" };
        if (CanCommitOwnedStartup(nativeGood with
                { Hive = Microsoft.Win32.RegistryHive.LocalMachine,
                    Id = $"LocalMachine|Registry64|{run}|Spotify" }) ||
            CanCommitOwnedStartup(nativeGood with
                { RunPath = run + "Once",
                    Id = $"CurrentUser|Registry64|{run}Once|Spotify" }) ||
            CanRestoreOwnedStartup(nativeOwner with
                { StartupCommand = nativeOwner.StartupCommand + " --external" },
                nativeDisabled) ||
            CanRestoreOwnedStartup(nativeOwner with { Id = "foreign|owner" },
                nativeDisabled) ||
            CanRestoreOwnedStartup(nativeOwner,
                nativeDisabled with { Command = nativeDisabled.Command + " --external" }) ||
            CanRestoreOwnedStartup(nativeOwner,
                nativeDisabled with { Name = "Other",
                    Id = $"CurrentUser|Registry64|{run}\\SpankyhoDisabled|Other" }) ||
            !CanCommitOwnedStartup(nativeGood) ||
            !IsStartupDisableConfirmed(nativeOwner, nativeGood,
                new[] { nativeDisabled }, requireNativeAdmission: true) ||
            IsStartupDisableConfirmed(nativeOwner, nativeGood,
                new[] { nativeDisabled with { Id = nativeGood.Id } },
                requireNativeAdmission: true) ||
            IsStartupDisableConfirmed(nativeOwner, nativeGood,
                new[] { nativeDisabled, nativeGood }, requireNativeAdmission: true) ||
            !IsStartupRestoreConfirmed(nativeOwner,
                new[] { nativeGood }, requireNativeAdmission: true) ||
            IsStartupRestoreConfirmed(nativeOwner,
                new[] { nativeGood with { Id = "foreign|id" } },
                requireNativeAdmission: true) ||
            !IsExternallyResolvedStartupWithoutDisabledSlot(nativeOwner,
                new[] { userChangedNative }, requireNativeAdmission: true) ||
            IsExternallyResolvedStartupWithoutDisabledSlot(nativeOwner,
                new[] { userChangedNative with { Id = "foreign|id" } },
                requireNativeAdmission: true) ||
            IsExternallyResolvedStartupWithoutDisabledSlot(nativeOwner,
                new[] { userChangedNative, nativeDisabled },
                requireNativeAdmission: true) ||
            !CanRestoreOwnedStartup(nativeOwner, nativeDisabled) ||
            !ReferenceEquals(nativeDisabled, FindAdmissibleOwnedRestore(
                nativeOwner, new[] { nativeDisabled }, requireNativeAdmission: true)) ||
            CanRestoreOwnedStartup(nativeOwner, nativeDisabled with { Id = nativeGood.Id }) ||
            CanRestoreOwnedStartup(nativeOwner, nativeDisabled with { Id = null! }) ||
            CanRestoreOwnedStartup(nativeOwner, nativeDisabled with { View = Microsoft.Win32.RegistryView.Default }) ||
            CanRestoreOwnedStartup(nativeOwner, nativeDisabled with { Command = " " }) ||
            CanRestoreOwnedStartup(nativeOwner, nativeDisabled with { Enabled = true }) ||
            FindAdmissibleOwnedRestore(nativeOwner,
                new[] { nativeDisabled with { Id = nativeGood.Id } },
                requireNativeAdmission: true) is not null ||
            !HasCurrentAdmissibleStartup("spotify", nativeOwner,
                new[] { nativeGood }, requireNativeAdmission: true) ||
            HasCurrentAdmissibleStartup("spotify", nativeOwner,
                new[] { nativeGood with { Id = "foreign|id" } },
                requireNativeAdmission: true) ||
            HasCurrentAdmissibleStartup("spotify", nativeOwner,
                new[] { nativeGood with { Id = "" } },
                requireNativeAdmission: true) ||
            HasCurrentAdmissibleStartup("spotify", nativeOwner,
                new[] { nativeGood with { Command = " " } },
                requireNativeAdmission: true) ||
            !ReferenceEquals(nativeGood,
                FindAdmissibleStartup("spotify", new[] { good, nativeGood },
                    requireNativeAdmission: true)) ||
            FindAdmissibleStartup("spotify", new[] { good },
                requireNativeAdmission: true) != null ||
            FindAdmissibleStartup("spotify", new[] { nativeGood with
                { Command = " " }, nativeGood },
                requireNativeAdmission: true) != nativeGood)
            return false;
        return AiThirdPartyIdlePolicy.CanManageStartup("spotify", good) &&
            // Native inventories can contain partially unreadable entries.
            // They must never authorize auto-disable or throw during recovery.
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { SourceKind = null! }) &&
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { RunPath = null! }) &&
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { Name = null! }) &&
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { FilePath = null! }) &&
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { CanToggle = false }) &&
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { Name = "", CanToggle = false }) &&
            !AiThirdPartyIdlePolicy.CanManageStartup("spotify",
                good with { Command = "", CanToggle = false });
    }

    // v16.180: a registry value can disappear between enumeration and read.
    // ReadKey must never manufacture a phantom enabled slot in that race;
    // even a legitimately present empty REG_SZ cannot prove external
    // restoration and must not discard an AI-owned durable journal.
    // Read-only synthetic snapshots only; no live registry or services.
    internal static bool RunStartupRegistryRace180Smoke()
    {
        bool missing = StartupManagerService.TryReadPresentRegistryCommand(
            null, out string missingCommand);
        bool empty = StartupManagerService.TryReadPresentRegistryCommand(
            "", out string emptyCommand);
        bool changed = StartupManagerService.TryReadPresentRegistryCommand(
            "\"C:\\Apps\\Spotify\\Spotify.exe\" --manual",
            out string replacementCommand);
        bool whitespace = StartupManagerService.TryReadPresentRegistryCommand(
            "  ", out string whitespaceCommand);
        if (missing || missingCommand != "" || !empty || emptyCommand != "" ||
            !changed || replacementCommand != "\"C:\\Apps\\Spotify\\Spotify.exe\" --manual" ||
            !whitespace || whitespaceCommand != "  ")
            return false;

        DateTime at = new(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string originalCommand = "\"C:\\Apps\\Spotify\\Spotify.exe\"";
        var owner = new Owner("startup", "fixture", "spotify", at,
            true, false, "Spotify", originalCommand, run, 0);
        var enabled = new StartupEntry("fixture", "Spotify", originalCommand,
            Microsoft.Win32.RegistryHive.CurrentUser, run, true, "Spotify",
            @"C:\Apps\Spotify\Spotify.exe",
            View: Microsoft.Win32.RegistryView.Default);
        var blank = enabled with { Command = emptyCommand };
        var spaces = enabled with { Command = whitespaceCommand };
        var replacement = enabled with { Command = replacementCommand };
        var disabled = enabled with { Enabled = false };
        return !IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   Array.Empty<StartupEntry>()) &&
               !IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   new[] { blank }) &&
               !IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   new[] { spaces }) &&
               !IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   new[] { enabled }) &&
               !IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   new[] { replacement, disabled }) &&
               IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   new[] { replacement }) &&
               IsExternallyResolvedStartupWithoutDisabledSlot(owner,
                   new[] { blank, replacement });
    }

    // A user-owned Run replacement may legitimately outlive its AI-owned
    // disabled twin (which the user has independently removed). That journal
    // has nothing left to restore and should not block other AI experiments
    // forever. Preserve ownership on ANY ambiguous or still-disabled slot.
    internal static bool RunStartupExternalResolution168Smoke()
    {
        DateTime at = new(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string command = "\"" + @"C:\Apps\Spotify\Spotify.exe" + "\"";
        var owner = new Owner("startup", "fixture", "spotify", at,
            true, false, "Spotify", command, run, 0);
        var disabled = new StartupEntry("fixture", "Spotify", command,
            Microsoft.Win32.RegistryHive.CurrentUser, run, false, "Spotify",
            @"C:\Apps\Spotify\Spotify.exe",
            View: Microsoft.Win32.RegistryView.Default);
        var enabled = disabled with { Enabled = true };
        var changedRun = enabled with { Command = command + " --manual" };
        var changedDisabled = disabled with { Command = command + " --manual" };
        var differentView = changedDisabled with
            { View = Microsoft.Win32.RegistryView.Registry64 };
        var differentHive = changedDisabled with
            { Hive = Microsoft.Win32.RegistryHive.LocalMachine };
        var differentName = changedRun with { Name = "Other" };
        var differentPath = changedRun with { RunPath = run + @"\Other" };
        var differentSource = changedRun with { SourceKind = "TaskScheduler" };
        bool Resolved(params StartupEntry[] rows) =>
            IsExternallyResolvedStartupWithoutDisabledSlot(owner, rows);

        return Resolved(changedRun) &&
            Resolved(changedRun with { Name = "sPoTiFy" }) &&
            Resolved(differentView, changedRun) &&
            Resolved(changedRun, differentHive) &&
            !Resolved(enabled) &&
            !Resolved(disabled) &&
            !Resolved() &&
            !Resolved(differentName) &&
            !Resolved(differentPath) &&
            !Resolved(differentSource) &&
            !Resolved(changedRun, disabled) &&
            !Resolved(disabled, changedRun) &&
            !Resolved(changedDisabled, changedRun) &&
            !Resolved(changedRun, changedDisabled) &&
            !IsExternallyResolvedStartupWithoutDisabledSlot(
                owner with { OriginalEnabled = false }, new[] { changedRun }) &&
            !IsExternallyResolvedStartupWithoutDisabledSlot(
                owner with { Kind = "task" }, new[] { changedRun }) &&
            // Same evidence was previously classified as not restored, and
            // MUST still be blocked by the restorative write candidate gate.
            !IsStartupRestoreConfirmed(owner, new[] { changedRun }) &&
            FindAdmissibleOwnedRestore(owner, new[] { changedRun }) is null &&
            IsStartupRestoreConfirmed(owner, new[] { enabled }) &&
            FindAdmissibleOwnedRestore(owner, new[] { disabled }) is not null;
    }

    // Read-only synthetic verification of the physical registry slot rule:
    // user-edited values occupy the same Run/SpankyhoDisabled destination,
    // while separate registry views and hives must not be conflated.
    internal static bool RunStartupRestoreSlotCollision166Smoke()
    {
        DateTime at = new(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string command = "\"" + @"C:\Apps\Spotify\Spotify.exe" + "\"";
        var owner = new Owner("startup", "fixture", "spotify", at, true, false,
            "Spotify", command, run, 0);
        var disabled = new StartupEntry("fixture", "Spotify", command,
            Microsoft.Win32.RegistryHive.CurrentUser, run, false, "Spotify",
            @"C:\Apps\Spotify\Spotify.exe",
            View: Microsoft.Win32.RegistryView.Default);
        var enabled = disabled with { Enabled = true };
        var edited = disabled with { Command = command + " --new-user-command" };
        var editedCaps = edited with { Name = "sPoTiFy" };
        var otherView = edited with { View = Microsoft.Win32.RegistryView.Registry64 };
        var otherHive = edited with { Hive = Microsoft.Win32.RegistryHive.LocalMachine };
        var otherPath = edited with { RunPath = run + @"\Other" };

        return IsStartupRestoreConfirmed(owner, new[] { enabled }) &&
            FindAdmissibleOwnedRestore(owner, new[] { disabled }) is not null &&
            !IsStartupRestoreConfirmed(owner, new[] { enabled, edited }) &&
            !IsStartupRestoreConfirmed(owner, new[] { edited, enabled }) &&
            !IsStartupRestoreConfirmed(owner, new[] { enabled, editedCaps }) &&
            FindAdmissibleOwnedRestore(owner,
                new[] { disabled, edited with { Enabled = true } }) is null &&
            FindAdmissibleOwnedRestore(owner,
                new[] { editedCaps with { Enabled = true }, disabled }) is null &&
            !IsStartupRestoreConfirmed(owner,
                new[] { edited with { Enabled = true } }) &&
            IsStartupRestoreConfirmed(owner, new[] { enabled, otherView }) &&
            IsStartupRestoreConfirmed(owner, new[] { enabled, otherHive }) &&
            IsStartupRestoreConfirmed(owner, new[] { enabled, otherPath }) &&
            ReferenceEquals(disabled, FindAdmissibleOwnedRestore(
                owner, new[] { disabled, otherView with { Enabled = true } })) &&
            ReferenceEquals(disabled, FindAdmissibleOwnedRestore(
                owner, new[] { otherHive with { Enabled = true }, disabled }));
    }

    // Compare the original FirstOrDefault/Any lookup semantics against the
    // allocation-free recovery loops. A mixed ENABLED/DISABLED exact identity
    // remains detectable as both states, but neither restoration confirmation
    // nor a new restoration write is permitted for that ambiguous twin pair.
    // All entries are synthetic; this smoke never reads or writes HKCU.
    internal static bool RunStartupDirectLookupParitySmoke()
    {
        DateTime at = new(2026, 10, 23, 3, 0, 0, DateTimeKind.Utc);
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string command = @"""C:\Apps\Spotify\Spotify.exe""";
        var owner = new Owner("startup", "fixture", "spotify", at,
            true, false, "Spotify", command, run, 0);
        var matching = new StartupEntry("fixture", "Spotify", command,
            Microsoft.Win32.RegistryHive.CurrentUser, run, false,
            "Spotify", @"C:\Apps\Spotify\Spotify.exe",
            View: Microsoft.Win32.RegistryView.Default);
        var enabled = matching with { Enabled = true };
        var unrelated = matching with { Name = "Other" };
        var modified = matching with { Command = command.ToUpperInvariant() };
        var wrongHive = matching with
        {
            Hive = Microsoft.Win32.RegistryHive.LocalMachine
        };
        var wrongPath = matching with { RunPath = run + "\\Other" };
        var wrongView = matching with
        {
            View = Microsoft.Win32.RegistryView.Registry64
        };
        var wrongSource = matching with { SourceKind = "TaskScheduler" };
        var unsafeUpdater = enabled with { Name = "Spotify Updater" };
        var unsafeSecurity = enabled with { Name = "Spotify security" };
        var unsafePeripheral = enabled with { Name = "Spotify GearLink" };
        var deniedRunOnce = enabled with { RunPath = run + "Once" };
        var deniedToggle = enabled with { CanToggle = false };
        var deniedExecutable = enabled with
            { FilePath = @"C:\Apps\Discord\Discord.exe" };
        var samples = new IReadOnlyList<StartupEntry>[]
        {
            Array.Empty<StartupEntry>(),
            new[] { unrelated, modified, wrongHive, wrongPath,
                wrongView, wrongSource },
            new[] { matching },
            new[] { enabled },
            new[] { unrelated, matching, enabled },
            new[] { unrelated, enabled, matching },
            new[] { matching, unrelated, matching },
            new[] { modified, enabled, matching },
            new[] { wrongSource, wrongView, matching, enabled },
            new[] { unsafeUpdater, unsafeSecurity, unsafePeripheral,
                deniedRunOnce, deniedToggle, deniedExecutable },
            new[] { wrongHive, unsafeUpdater, deniedRunOnce, matching, enabled },
            new[] { deniedToggle, enabled, unsafeUpdater, enabled },
            // Stale disabled snapshots must never authorize restoring the
            // now-enabled, repointed, updater or untoggleable live entry.
            new[] { matching with { CanToggle = false } },
            new[] { matching with { FilePath = @"C:\Apps\Updater\Update.exe" } },
            new[] { matching with { FilePath = @"C:\Apps\Discord\Discord.exe" } },
            new[] { matching with { SourceKind = "TaskScheduler" } },
            new[] { matching with { Command = command + " --changed" } },
            new[] { enabled, matching },
            new[] { matching, enabled },
            new[] { enabled, matching with { Command = command + " --foreign" } },
            new[] { matching, enabled with { Command = command + " --foreign" } },
            new[] { enabled, matching with { Name = "sPoTiFy" } },
            new[] { enabled, matching with { View = Microsoft.Win32.RegistryView.Registry64 } }
        };
        foreach (IReadOnlyList<StartupEntry> rows in samples)
        {
            StartupEntry? first = rows.FirstOrDefault(x =>
                IsSameStartup(x, owner));
            bool active = rows.Any(x =>
                IsSameStartup(x, owner) && x.Enabled);
            bool inactive = rows.Any(x =>
                IsSameStartup(x, owner) && !x.Enabled);
            bool confirmed = rows.Any(x =>
                x.Enabled && IsSameStartup(x, owner) &&
                AiThirdPartyIdlePolicy.CanManageStartup(owner.AppKey, x));
            // A mixed physical slot is ambiguous even after a command edit.
            // Neither restore confirmation nor a new restore write is allowed.
            bool occupiedDisabled = rows.Any(x =>
                !x.Enabled && x.SourceKind.Equals(
                    "Registry", StringComparison.OrdinalIgnoreCase) &&
                x.Hive == Microsoft.Win32.RegistryHive.CurrentUser &&
                x.Name.Equals(owner.StartupName, StringComparison.OrdinalIgnoreCase) &&
                x.RunPath.Equals(owner.StartupRunPath, StringComparison.OrdinalIgnoreCase) &&
                (int)x.View == owner.StartupView);
            bool occupiedEnabled = rows.Any(x =>
                x.Enabled && x.SourceKind.Equals(
                    "Registry", StringComparison.OrdinalIgnoreCase) &&
                x.Hive == Microsoft.Win32.RegistryHive.CurrentUser &&
                x.Name.Equals(owner.StartupName, StringComparison.OrdinalIgnoreCase) &&
                x.RunPath.Equals(owner.StartupRunPath, StringComparison.OrdinalIgnoreCase) &&
                (int)x.View == owner.StartupView);
            confirmed &= !occupiedDisabled;
            StartupEntry? expectedRestorable = !occupiedEnabled &&
                first is { Enabled: false } &&
                AiThirdPartyIdlePolicy.CanManageStartup(
                    owner.AppKey, first with { Enabled = true }) ? first : null;
            if (!ReferenceEquals(first, FindMatchingStartup(owner, rows)) ||
                !ReferenceEquals(expectedRestorable,
                    FindAdmissibleOwnedRestore(owner, rows)) ||
                active != HasEnabledMatchingStartup(owner, rows) ||
                inactive != HasMatchingStartupState(owner, rows, enabled: false) ||
                occupiedEnabled != HasOwnedStartupSlot(owner, rows, enabled: true) ||
                occupiedDisabled != HasOwnedStartupSlot(owner, rows, enabled: false) ||
                confirmed != IsStartupRestoreConfirmed(owner, rows))
                return false;

            // The source selection and just-before-write readback have
            // different semantics: one chooses FIRST eligible startup,
            // the other requires the SAME persisted owner identity.
            // Independent LINQ oracle: native value-name occupancy ignores
            // command changes and casing, but not registry view/hive/path.
            bool Occupied(StartupEntry x) => rows.Any(v =>
                !v.Enabled &&
                v.SourceKind.Equals("Registry", StringComparison.OrdinalIgnoreCase) &&
                v.Hive == x.Hive && v.View == x.View &&
                v.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase) &&
                v.RunPath.Equals(x.RunPath, StringComparison.OrdinalIgnoreCase));
            foreach (string app in AiThirdPartyIdlePolicy.AppKeys)
            {
                StartupEntry? expected = rows.FirstOrDefault(x =>
                    AiThirdPartyIdlePolicy.CanManageStartup(app, x) &&
                    !Occupied(x));
                bool beforeWrite = rows.Any(x =>
                    IsSameStartup(x, owner) &&
                    AiThirdPartyIdlePolicy.CanManageStartup(app, x) &&
                    !Occupied(x));
                if (!ReferenceEquals(
                        expected, FindAdmissibleStartup(app, rows)) ||
                    beforeWrite != HasCurrentAdmissibleStartup(
                        app, owner, rows))
                    return false;
            }
            if (FindAdmissibleStartup("windows", rows) != null ||
                FindAdmissibleStartup("firefox", rows) != null ||
                HasCurrentAdmissibleStartup("windows", owner, rows))
                return false;
        }
        return !HasEnabledMatchingStartup(owner,
                   Array.Empty<StartupEntry>()) &&
            FindMatchingStartup(owner, Array.Empty<StartupEntry>()) is null;
    }

    // Pure regression fixtures. They do not read or write the live journal.
    internal static bool RunJournalIdentitySmoke()
    {
        DateTime at = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
        var steam = new Owner("service", "Steam Client Service", "steam",
            at, true, true);
        var firefox = new Owner("task",
            @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB",
            "firefox", at, true, false);
        const string spotifyRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
        string spotifyId = $"CurrentUser|Registry64|{spotifyRun}|Spotify";
        var spotify = new Owner("startup", spotifyId, "spotify", at,
            true, false, "Spotify", @"""C:\Apps\Spotify\Spotify.exe""",
            spotifyRun, 256);
        var spotifyActive = new StartupEntry(spotifyId, "Spotify",
            spotify.StartupCommand, Microsoft.Win32.RegistryHive.CurrentUser,
            spotify.StartupRunPath, true, "Spotify",
            @"C:\Apps\Spotify\Spotify.exe", View: Microsoft.Win32.RegistryView.Registry64);
        var validFeedback = new FeedbackOutcome(at, "inconclusive", 2,
            AppKey: "steam");
        var validReference = new ReferenceTrial("Steam Client Service",
            "steam", at, 8, 8192, 7, 8300);
        string steamKey = Key("service", steam.Id);
        var duplicateOwners = new Dictionary<string, Owner>(
            StringComparer.Ordinal) {
            [steamKey] = steam,
            [steamKey.ToUpperInvariant()] = steam
        };
        return RunBoundedJournalSmoke() &&
            HasValidOwnedJournal(new Dictionary<string, Owner> {
                [steamKey] = steam }) &&
            !HasValidOwnedJournal(duplicateOwners) &&
            !IsValidPersistedOwner(steamKey,
                steam with { FeedbackSamples = 4 }) &&
            !IsValidPersistedOwner(steamKey,
                steam with { SumCpuPercent = double.NaN }) &&
            !IsValidPersistedOwner(steamKey,
                steam with { SumRamMiB = double.PositiveInfinity }) &&
            !IsValidPersistedOwner(steamKey,
                steam with { BaselineCpuPercent = double.NaN }) &&
            !IsValidPersistedOwner(steamKey,
                steam with { FeedbackSamples = 1, Pending = true }) &&
            !IsValidPersistedOwner(steamKey,
                steam with { LastFeedbackSampleUtc = at.ToLocalTime() }) &&
            IsValidPersistedFeedback(Key("service", steam.Id), validFeedback) &&
            !IsValidPersistedFeedback(Key("service", steam.Id),
                validFeedback with { AppKey = "defender" }) &&
            !IsValidPersistedFeedback(Key("service", steam.Id),
                validFeedback with { AppKey = null! }) &&
            !IsValidPersistedFeedback(Key("service", steam.Id),
                validFeedback with { Samples = 4 }) &&
            !IsValidPersistedFeedback(Key("service", steam.Id),
                validFeedback with { Verdict = "approved-unknown" }) &&
            !IsValidPersistedFeedback(Key("service", steam.Id),
                validFeedback with { Verdict = "no-observed-gain", Samples = 2 }) &&
            !IsValidPersistedFeedback(Key("service", steam.Id),
                validFeedback with { Verdict = "aba-matched-system-trend", Samples = 2 }) &&
            IsValidPersistedFeedback(Key("service", steam.Id),
                validFeedback with { Verdict = "no-observed-gain", Samples = 3 }) &&
            !IsValidPersistedFeedback(Key("service", steam.Id),
                validFeedback with { Verdict = "deferred-until-next-startup", Samples = 0 }) &&
            !IsValidPersistedFeedback(Key("service", steam.Id),
                validFeedback with { Verdict = "inconclusive",
                    CpuDropPercentPoints = 1 }) &&
            IsValidPersistedFeedback(Key("task",
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB"),
                validFeedback with { Verdict = "deferred-until-task-trigger",
                    Samples = 0, AppKey = "firefox" }) &&
            !IsValidPersistedFeedback(Key("task",
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB"),
                validFeedback with { Verdict = "deferred-until-task-trigger",
                    Samples = 0, AppKey = null! }) &&
            !IsValidPersistedFeedback(Key("task",
                @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB"),
                validFeedback with { Verdict = "no-observed-gain",
                    Samples = 3, AppKey = "firefox" }) &&
            !IsValidPersistedFeedback(Key("service", steam.Id),
                validFeedback with { AppKey = "spotify" }) &&
            !IsValidPersistedFeedback(
                Key("task", @"\Mozilla\Firefox Update Agent 308046B0AF4A39CB"),
                validFeedback with { AppKey = "firefox" }) &&
            !IsValidPersistedFeedback(
                Key("task", @"\Mozilla\Firefox Default Browser Agent 308046B0AF4A39CB"),
                validFeedback with { AppKey = "steam" }) &&
            !MatchesFeedbackResource("startup|", "spotify") &&
            !CanReplaceJournalStamp(false, false, 0, default,
                false, 0, default) &&
            CanReplaceJournalStamp(true, false, 0, default,
                false, 0, default) &&
            !CanReplaceJournalStamp(true, false, 0, default,
                true, 32, at) &&
            !CanReplaceJournalStamp(true, true, 32, at,
                true, 33, at) &&
            !CanReplaceJournalStamp(true, true, 32, at,
                true, 32, at.AddTicks(1)) &&
            IsValidPersistedReference(Key("service", steam.Id), validReference) &&
            !IsValidPersistedReference(Key("service", steam.Id),
                validReference with { Id = "WinDefend" }) &&
            !IsValidPersistedReference(Key("service", steam.Id),
                validReference with { CandidateRamMiB = double.NaN }) &&
            !IsValidPersistedReference(Key("service", steam.Id),
                validReference with { CpuSum = double.NaN }) &&
            !IsValidPersistedReference(Key("service", steam.Id),
                validReference with { LastSampleUtc = at.ToLocalTime() }) &&
            IsStartupRestoreConfirmed(spotify, new[] { spotifyActive }) &&
            !IsStartupRestoreConfirmed(spotify, new[] { spotifyActive with { Enabled = false } }) &&
            !IsStartupRestoreConfirmed(spotify, new[] { spotifyActive with {
                Command = "something else" } }) &&
            !IsStartupRestoreConfirmed(spotify, new[] { spotifyActive with {
                Command = spotify.StartupCommand.ToUpperInvariant() } }) &&
            !IsStartupRestoreConfirmed(spotify, new[] { spotifyActive with {
                FilePath = @"C:\Apps\Updater\Update.exe" } }) &&
            !IsStartupRestoreConfirmed(spotify, Array.Empty<StartupEntry>()) &&
            Math.Abs(ElapsedMilliseconds(Stopwatch.Frequency) - 1000) < 0.000001 &&
            ElapsedMilliseconds(-1) == 0 &&
            ElapsedMilliseconds(0) == 0 &&
            IsValidPersistedOwner(Key(steam.Kind, steam.Id), steam) &&
            IsValidPersistedOwner(Key(steam.Kind, steam.Id),
                steam with { OriginalServiceStartMode = 3 }) &&
            !IsValidPersistedOwner(Key(steam.Kind, steam.Id),
                steam with { OriginalServiceStartMode = 4 }) &&
            IsValidPersistedOwner(Key(firefox.Kind, firefox.Id), firefox) &&
            IsValidPersistedOwner(Key(spotify.Kind, spotify.Id), spotify) &&
            !IsValidPersistedOwner(Key(spotify.Kind, spotify.Id),
                spotify with { Id = "fixture", StartupView = 256 }) &&
            !IsValidPersistedOwner(Key(spotify.Kind, spotify.Id),
                spotify with { StartupView = 0 }) &&
            !IsValidPersistedOwner(Key(spotify.Kind, spotify.Id),
                spotify with { StartupView = 512 }) &&
            !IsValidPersistedOwner(Key(spotify.Kind, spotify.Id),
                spotify with { StartupName = "Changed" }) &&
            !IsValidPersistedOwner(Key(spotify.Kind, spotify.Id),
                spotify with { StartupRunPath = null! }) &&
            !IsValidPersistedOwner(Key(spotify.Kind, spotify.Id),
                spotify with { StartupRunPath = "" }) &&
            !IsValidPersistedOwner(Key(spotify.Kind, spotify.Id),
                spotify with { AppliedUtc = DateTime.SpecifyKind(default, DateTimeKind.Utc) }) &&
            !IsValidPersistedOwner("service|random", steam) &&
            !IsValidPersistedOwner(Key(steam.Kind, steam.Id),
                steam with { Id = "WinDefend" }) &&
            !IsValidPersistedOwner(Key(steam.Kind, steam.Id),
                steam with { AppKey = "windows" }) &&
            !IsValidPersistedOwner(Key(steam.Kind, steam.Id),
                steam with { OriginalEnabled = false }) &&
            !IsValidPersistedOwner(Key(firefox.Kind, firefox.Id),
                firefox with { Id = @"\Mozilla\Firefox Update Agent 308046B0AF4A39CB" }) &&
            !IsValidPersistedOwner(Key(spotify.Kind, spotify.Id),
                spotify with { StartupRunPath =
                    @"Software\Microsoft\Windows\CurrentVersion\RunOnce" }) &&
            !IsValidPersistedOwner(Key(spotify.Kind, spotify.Id),
                spotify with { AppKey = "defender" }) &&
            !IsValidPersistedOwner(Key(spotify.Kind, spotify.Id),
                spotify with { AppliedUtc = at.ToLocalTime() }) &&
            !HasJournalStampChanged(false, 0, default, false, 0, default) &&
            HasJournalStampChanged(false, 0, default, true, 5, at) &&
            HasJournalStampChanged(true, 5, at, false, 0, default) &&
            HasJournalStampChanged(true, 5, at, true, 6, at) &&
            HasJournalStampChanged(true, 5, at, true, 5, at.AddSeconds(1)) &&
            !HasJournalStampChanged(true, 5, at, true, 5, at);
    }

    // Feedback cannot grant Windows write rights, but forged/malformed
    // outcomes can incorrectly extend service deferrals or start A/B/A.
    // Keep recovered learning restricted to recognized apps and verdicts.
    // Feedback keys are NOT interchangeable between service, task and
    // startup. A forged valid app name on a foreign resource cannot impose
    // a false cooldown on another independently-owned resource.
    private static bool MatchesFeedbackResource(string key, string? appKey) =>
        (key.StartsWith("startup|", StringComparison.OrdinalIgnoreCase) &&
         key.Length > "startup|".Length &&
         IsManagedJournalApp(appKey)) ||
        (key.Equals(SteamServiceOwnerKey,
             StringComparison.OrdinalIgnoreCase) &&
         string.Equals(appKey, "steam", StringComparison.OrdinalIgnoreCase)) ||
        (key.StartsWith("task|", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(appKey, "firefox", StringComparison.OrdinalIgnoreCase) &&
         AiThirdPartyIdlePolicy.MatchesOptionalFirefoxTask(
             key["task|".Length..]));

    // A verdict is evidence for a PARTICULAR kind of resource at a
    // PARTICULAR sample count, not a standalone status string. An auxiliary
    // record with an impossible combination must be quarantined, while
    // validated owner entries remain intact for reliable restoration.
    private static bool MatchesFeedbackEvidence(
        string key, FeedbackOutcome value)
    {
        bool startup = key.StartsWith("startup|", StringComparison.OrdinalIgnoreCase);
        bool task = key.StartsWith("task|", StringComparison.OrdinalIgnoreCase);
        bool service = key.Equals(SteamServiceOwnerKey,
            StringComparison.OrdinalIgnoreCase);
        return value.Verdict switch
        {
            "deferred-until-next-startup" => startup && value.Samples == 0 &&
                !value.CpuDropPercentPoints.HasValue &&
                !value.AvailableRamGainMiB.HasValue,
            "deferred-until-task-trigger" => task && value.Samples == 0 &&
                !value.CpuDropPercentPoints.HasValue &&
                !value.AvailableRamGainMiB.HasValue,
            "inconclusive" or "aba-inconclusive" =>
                service && !value.CpuDropPercentPoints.HasValue &&
                !value.AvailableRamGainMiB.HasValue,
            "positive-system-trend" or "no-observed-gain" =>
                service && value.Samples == 3,
            "aba-matched-system-trend" or "aba-unconfirmed" =>
                service && value.Samples == 3,
            _ => false
        };
    }

    private static bool IsValidPersistedFeedback(
        string key, FeedbackOutcome? value) =>
        value is not null && value.UpdatedUtc.Kind == DateTimeKind.Utc &&
        value.UpdatedUtc <= DateTime.UtcNow.AddMinutes(5) &&
        value.Samples is >= 0 and <= 3 &&
        key.Length <= 1024 &&
        (!value.CpuDropPercentPoints.HasValue ||
            double.IsFinite(value.CpuDropPercentPoints.Value)) &&
        (!value.AvailableRamGainMiB.HasValue ||
            double.IsFinite(value.AvailableRamGainMiB.Value)) &&
        // Resource-specific admission already checks the startup app allowlist,
        // the exact Steam service, or the exact optional Firefox task. A
        // second generic app filter incorrectly rejects valid Firefox feedback.
        MatchesFeedbackResource(key, value.AppKey) &&
        MatchesFeedbackEvidence(key, value);

    private static bool IsValidPersistedReference(
        string key, ReferenceTrial? value) =>
        value is not null &&
        !string.IsNullOrWhiteSpace(value.Id) &&
        !string.IsNullOrWhiteSpace(value.AppKey) &&
        key.Equals(SteamServiceOwnerKey,
            StringComparison.OrdinalIgnoreCase) &&
        value.Id.Equals("Steam Client Service", StringComparison.OrdinalIgnoreCase) &&
        value.AppKey.Equals("steam", StringComparison.OrdinalIgnoreCase) &&
        value.RestoredUtc.Kind == DateTimeKind.Utc &&
        value.RestoredUtc <= DateTime.UtcNow.AddMinutes(5) &&
        value.Samples is >= 0 and <= 3 &&
        (value.LastSampleUtc == default ||
            (value.LastSampleUtc.Kind == DateTimeKind.Utc &&
             value.LastSampleUtc >= value.RestoredUtc &&
             value.LastSampleUtc <= DateTime.UtcNow.AddMinutes(5))) &&
        double.IsFinite(value.CpuSum) && value.CpuSum >= 0 &&
        double.IsFinite(value.RamSum) && value.RamSum >= 0 &&
        (!value.MinCpu.HasValue || (double.IsFinite(value.MinCpu.Value) &&
            value.MinCpu.Value is >= 0 and <= 100)) &&
        (!value.MaxCpu.HasValue || (double.IsFinite(value.MaxCpu.Value) &&
            value.MaxCpu.Value is >= 0 and <= 100)) &&
        (!value.MinRamMiB.HasValue ||
            (double.IsFinite(value.MinRamMiB.Value) && value.MinRamMiB.Value > 0)) &&
        (!value.MaxRamMiB.HasValue ||
            (double.IsFinite(value.MaxRamMiB.Value) && value.MaxRamMiB.Value > 0)) &&
        double.IsFinite(value.BaselineCpu) &&
        double.IsFinite(value.CandidateCpu) &&
        double.IsFinite(value.BaselineRamMiB) &&
        double.IsFinite(value.CandidateRamMiB) &&
        value.BaselineCpu is >= 0 and <= 100 &&
        value.CandidateCpu is >= 0 and <= 100 &&
        value.BaselineRamMiB > 0 && value.CandidateRamMiB > 0;

    // Lossy recovery applies ONLY to auxiliary outcomes. Active ownership
    // must be validated separately and carried across unchanged. Keep at
    // most the newest 128 valid feedback rows; an optional bad A2 sample
    // is dropped rather than keeping a real native restoration hostage.
    private static Journal RecoverValidLearning(Journal rows, out bool degraded)
    {
        // Most journals are already normalized from our last recovery. If
        // all surviving rows are newest-first with unique resource identity
        // and <=128 entries, avoid sorting and building LINQ iterators on
        // every leased AI scan. On ANY disorder/case collision/overflow,
        // use the unchanged stable legacy pipeline: latest duplicate wins,
        // equal timestamps preserve original order, oldest entries drop.
        Dictionary<string, FeedbackOutcome>? feedback = null;
        if (rows.Feedback is null)
            feedback = new(StringComparer.OrdinalIgnoreCase);
        else if (rows.Feedback.Count <= 128)
        {
            var candidate = new Dictionary<string, FeedbackOutcome>(
                rows.Feedback.Count, StringComparer.OrdinalIgnoreCase);
            DateTime previousUtc = DateTime.MaxValue;
            bool alreadyOrdered = true;
            foreach (var pair in rows.Feedback)
            {
                if (!IsValidPersistedFeedback(pair.Key, pair.Value)) continue;
                if (pair.Value.UpdatedUtc > previousUtc ||
                    !candidate.TryAdd(pair.Key, pair.Value))
                {
                    alreadyOrdered = false;
                    break;
                }
                previousUtc = pair.Value.UpdatedUtc;
            }
            if (alreadyOrdered) feedback = candidate;
        }
        feedback ??= (rows.Feedback ?? new Dictionary<string, FeedbackOutcome>())
            .Where(x => IsValidPersistedFeedback(x.Key, x.Value))
            .OrderByDescending(x => x.Value.UpdatedUtc)
            .DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(128)
            .ToDictionary(x => x.Key, x => x.Value,
                StringComparer.OrdinalIgnoreCase);

        // A2 references have no ordering or top-N rule: the former
        // Where/DistinctBy/ToDictionary pipeline simply accepted the first
        // valid case-insensitive identity. TryAdd does exactly that in one
        // bounded pass, including invalid-before-valid duplicate cases.
        var references = new Dictionary<string, ReferenceTrial>(
            rows.References?.Count ?? 0, StringComparer.OrdinalIgnoreCase);
        if (rows.References is not null)
            foreach (var pair in rows.References)
                if (IsValidPersistedReference(pair.Key, pair.Value))
                    references.TryAdd(pair.Key, pair.Value);

        degraded = feedback.Count != (rows.Feedback?.Count ?? 0) ||
                   references.Count != (rows.References?.Count ?? 0);
        return rows with { Feedback = feedback, References = references };
    }

    // JSON dictionaries deserialize with ordinal case-sensitive keys,
    // whereas Windows resource identity and our runtime owner maps use
    // OrdinalIgnoreCase. Refuse rather than silently overwrite colliding
    // owners when transferring JSON into the authoritative runtime map.
    // Loaded JSON dictionaries can contain case-only duplicate identities,
    // whereas the live owner map already uses OrdinalIgnoreCase. Validate
    // every owner once and only allocate a duplicate-key set for an untrusted
    // dictionary comparer. Keep the original <=64 owner hard bound.
    private static bool HasValidOwnedJournal(Dictionary<string, Owner>? owned)
    {
        if (owned is null || owned.Count > 64) return false;
        HashSet<string>? identities =
            ReferenceEquals(owned.Comparer, StringComparer.OrdinalIgnoreCase)
                ? null : new HashSet<string>(owned.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in owned)
        {
            if ((identities != null && !identities.Add(pair.Key)) ||
                !IsValidPersistedOwner(pair.Key, pair.Value))
                return false;
        }
        return true;
    }

    // Pure oracle parity against the old LINQ validation: case-only
    // duplicates in untrusted JSON, original count bound and actual owner
    // allowlist must all behave identically. No real journal/SCM/registry.
    internal static bool RunOwnedJournalValidationParitySmoke()
    {
        DateTime at = new(2026, 10, 22, 13, 0, 0, DateTimeKind.Utc);
        static bool Legacy(Dictionary<string, Owner>? owners) =>
            owners is { Count: <= 64 } &&
            owners.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                owners.Count &&
            owners.All(x => IsValidPersistedOwner(x.Key, x.Value));
        var ordinal = new Dictionary<string, Owner>(StringComparer.Ordinal);
        var insensitive = new Dictionary<string, Owner>(
            StringComparer.OrdinalIgnoreCase);
        if (!HasValidOwnedJournal(ordinal) || !HasValidOwnedJournal(insensitive) ||
            HasValidOwnedJournal(null))
            return false;
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static string IdFor(int i) =>
            $"CurrentUser|Registry64|Software\\Microsoft\\Windows\\CurrentVersion\\Run|Spotify{i}";
        for (int i = 0; i < 64; i++)
        {
            string id = IdFor(i);
            var owner = new Owner("startup", id, "spotify", at,
                true, false, "Spotify" + i.ToString(CultureInfo.InvariantCulture),
                @"C:\Apps\Spotify.exe", run, 256, false);
            string key = Key(owner.Kind, owner.Id);
            ordinal[key] = owner;
            insensitive[key] = owner;
            if (HasValidOwnedJournal(ordinal) != Legacy(ordinal) ||
                HasValidOwnedJournal(insensitive) != Legacy(insensitive))
                return false;
        }
        if (!HasValidOwnedJournal(ordinal) || !HasValidOwnedJournal(insensitive))
            return false;
        var extended = new Dictionary<string, Owner>(ordinal,
            StringComparer.Ordinal);
        extended[Key("startup", IdFor(64))] = ordinal[Key("startup", IdFor(0))] with
        {
            Id = IdFor(64), StartupName = "Spotify64"
        };
        if (HasValidOwnedJournal(extended) || Legacy(extended)) return false;

        var colliding = new Dictionary<string, Owner>(StringComparer.Ordinal)
        {
            [Key("startup", IdFor(0))] = ordinal[Key("startup", IdFor(0))],
            [Key("STARTUP", IdFor(0)).ToUpperInvariant()] =
                ordinal[Key("startup", IdFor(0))]
        };
        if (HasValidOwnedJournal(colliding) || Legacy(colliding)) return false;

        var malformed = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase)
        {
            [Key("startup", IdFor(0))] = ordinal[Key("startup", IdFor(0))] with
            {
                AppKey = "windows"
            }
        };
        return !HasValidOwnedJournal(malformed) &&
            !Legacy(malformed) &&
            !HasValidOwnedJournal(new Dictionary<string, Owner>
            {
                [Key("startup", IdFor(0))] = ordinal[Key("startup", IdFor(0))] with
                {
                    AppliedUtc = at.ToLocalTime()
                }
            });
    }

    // Owner telemetry is part of the durable recovery record, not trusted
    // optional feedback: NaN/Infinity/out-of-range samples must fail the
    // owner journal closed rather than influence native service recovery.
    private static bool HasValidOwnerTelemetry(Owner o) =>
        o.FeedbackSamples is >= 0 and <= 3 &&
        double.IsFinite(o.SumCpuPercent) && o.SumCpuPercent >= 0 &&
        double.IsFinite(o.SumRamMiB) && o.SumRamMiB >= 0 &&
        (!o.BaselineCpuPercent.HasValue ||
            (double.IsFinite(o.BaselineCpuPercent.Value) &&
             o.BaselineCpuPercent.Value is >= 0 and <= 100)) &&
        (!o.BaselineRamMiB.HasValue ||
            (double.IsFinite(o.BaselineRamMiB.Value) &&
             o.BaselineRamMiB.Value > 0)) &&
        (!o.MinCpuPercent.HasValue ||
            (double.IsFinite(o.MinCpuPercent.Value) &&
             o.MinCpuPercent.Value is >= 0 and <= 100)) &&
        (!o.MaxCpuPercent.HasValue ||
            (double.IsFinite(o.MaxCpuPercent.Value) &&
             o.MaxCpuPercent.Value is >= 0 and <= 100)) &&
        (!o.MinRamMiB.HasValue ||
            (double.IsFinite(o.MinRamMiB.Value) && o.MinRamMiB.Value > 0)) &&
        (!o.MaxRamMiB.HasValue ||
            (double.IsFinite(o.MaxRamMiB.Value) && o.MaxRamMiB.Value > 0)) &&
        (o.LastFeedbackSampleUtc == default ||
            (o.LastFeedbackSampleUtc.Kind == DateTimeKind.Utc &&
             o.LastFeedbackSampleUtc >= o.AppliedUtc &&
             o.LastFeedbackSampleUtc <= DateTime.UtcNow.AddMinutes(5))) &&
        (o.FeedbackSamples == 0 || !o.Pending);

    // Fail closed on malformed persisted owners: a corrupted/misplaced
    // journal must never authorize arbitrary service start or task enable.
    // Only IDs that this implementation can itself create are acceptable.
    private static bool IsValidPersistedOwner(string key, Owner? owner)
    {
        if (owner is null || string.IsNullOrWhiteSpace(owner.Id) ||
            string.IsNullOrWhiteSpace(owner.AppKey) || !owner.OriginalEnabled ||
            owner.AppliedUtc.Kind != DateTimeKind.Utc ||
            owner.AppliedUtc.Ticks == 0 ||
            owner.AppliedUtc > DateTime.UtcNow.AddMinutes(5) ||
            !key.Equals(Key(owner.Kind, owner.Id), StringComparison.OrdinalIgnoreCase) ||
            !HasValidOwnerTelemetry(owner))
            return false;

        if (owner.Kind == "service")
            return owner.Id.Equals("Steam Client Service",
                       StringComparison.OrdinalIgnoreCase) &&
                   owner.AppKey.Equals("steam", StringComparison.OrdinalIgnoreCase) &&
                   owner.OriginallyRunning &&
                   (owner.OriginalServiceStartMode is null or 2 or 3);
        if (owner.Kind == "task")
            return owner.AppKey.Equals("firefox", StringComparison.OrdinalIgnoreCase) &&
                   AiThirdPartyIdlePolicy.MatchesOptionalFirefoxTask(owner.Id);
        if (owner.Kind == "startup")
            return IsManagedJournalApp(owner.AppKey) &&
                   !string.IsNullOrWhiteSpace(owner.StartupName) &&
                   !string.IsNullOrWhiteSpace(owner.StartupCommand) &&
                   // JSON may explicitly contain null despite non-null C#
                   // annotations. Reject it, never crash journal recovery.
                   string.Equals(owner.StartupRunPath,
                       @"Software\Microsoft\Windows\CurrentVersion\Run",
                       StringComparison.OrdinalIgnoreCase) &&
                   owner.StartupView is 256 or 512 &&
                   string.Equals(owner.Id,
                       $"CurrentUser|{(Microsoft.Win32.RegistryView)owner.StartupView}|{owner.StartupRunPath}|{owner.StartupName}",
                       StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private void CaptureJournalStamp(byte[]? validatedDigest = null)
    {
        var file = new FileInfo(JournalPath);
        file.Refresh();
        _journalStampExists = file.Exists;
        _journalStampLength = file.Exists ? file.Length : 0;
        _journalStampWriteUtc = file.Exists ? file.LastWriteTimeUtc : default;
        _journalDigest = file.Exists ? validatedDigest : null;
        _journalStampKnown = true;
    }

    // Read-only FileInfo metadata every 10s, never JSON parsing, service
    // enumeration or new background task. A stamp change still goes through
    // the same exclusive lease and full journal validation.
    internal static bool HasJournalStampChanged(
        bool knownExists, long knownLength, DateTime knownWriteUtc,
        bool currentExists, long currentLength, DateTime currentWriteUtc) =>
        knownExists != currentExists ||
        (currentExists && (knownLength != currentLength ||
                           knownWriteUtc != currentWriteUtc));

    private bool ExternalJournalChangeDue(DateTime now)
    {
        if (_lastJournalMetadataProbeUtc.Kind == DateTimeKind.Utc &&
            now >= _lastJournalMetadataProbeUtc &&
            now - _lastJournalMetadataProbeUtc < TimeSpan.FromSeconds(10))
            return false;
        _lastJournalMetadataProbeUtc = now;
        if (!_journalStampKnown) return true;
        try
        {
            var file = new FileInfo(JournalPath);
            file.Refresh();
            return HasJournalStampChanged(
                _journalStampExists, _journalStampLength, _journalStampWriteUtc,
                file.Exists, file.Exists ? file.Length : 0,
                file.Exists ? file.LastWriteTimeUtc : default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // Indeterminate metadata must not hide recovery.
        }
    }

    // Best-effort optimistic guard against EXTERNAL writers which ignore
    // our cross-Host .lease: reject an overwrite if the source changed
    // after our last complete, validated journal read or atomic write.
    // This is intentionally not claimed as a filesystem CAS guarantee.
    internal static bool CanReplaceJournalStamp(
        bool stampKnown, bool oldExists, long oldLength, DateTime oldUtc,
        bool diskExists, long diskLength, DateTime diskUtc) =>
        stampKnown && !HasJournalStampChanged(
            oldExists, oldLength, oldUtc, diskExists, diskLength, diskUtc);

    private static bool MatchesJournalDigest(byte[]? expected, byte[]? actual) =>
        expected is { Length: 32 } && actual is { Length: 32 } &&
        CryptographicOperations.FixedTimeEquals(expected, actual);

    private bool JournalUnchangedSinceLeaseRead()
    {
        if (!_journalStampKnown) return false;
        var file = new FileInfo(JournalPath);
        file.Refresh();
        if (!CanReplaceJournalStamp(_journalStampKnown,
            _journalStampExists, _journalStampLength, _journalStampWriteUtc,
            file.Exists, file.Exists ? file.Length : 0,
            file.Exists ? file.LastWriteTimeUtc : default))
            return false;
        if (!file.Exists) return _journalDigest is null;
        if (_journalDigest is null) return false;
        // Avoid a second JSON deserialization. Under the exclusive lease
        // hash the bounded on-disk bytes only for real durable commits.
        // External writers changing data AFTER this read are not atomic-CAS
        // protected; this is intentionally a best-effort conflict guard.
        using var stream = File.OpenRead(JournalPath);
        return MatchesJournalDigest(_journalDigest,
            SHA256.HashData(ReadBoundedJournalBytes(stream).AsSpan()));
    }

    // A previously observed durable journal disappearing is NOT evidence
    // that its owned Windows resources were restored. Keep in-memory owners
    // and fail closed instead of silently forgetting service/task changes.
    private static bool MissingJournalRequiresFailClosed(
        bool stampKnown, bool previousFileExists, int activeOwners,
        int activeReferences) =>
        (stampKnown && previousFileExists) ||
        activeOwners > 0 || activeReferences > 0;

    private void ReloadJournalUnderLease()
    {
        try
        {
            if (!File.Exists(JournalPath))
            {
                if (MissingJournalRequiresFailClosed(
                    _journalStampKnown, _journalStampExists,
                    _owned.Count, _references.Count))
                    throw new InvalidDataException(
                        "Previously trusted AI owner journal disappeared; ownership cannot be discarded");
                _owned.Clear();
                _feedback.Clear();
                _references.Clear();
                _learningRepairPending = false;
                _journalHealthy = true;
                _lastJournalErrorUtc = default;
                CaptureJournalStamp();
                return;
            }
            var info = new FileInfo(JournalPath);
            if (info.Length is <= 0 or > 262144)
                throw new InvalidDataException("AI app owner journal size outside safe bounds");
            using var stream = File.OpenRead(JournalPath);
            var payload = ReadBoundedJournalPayload(stream);
            var rows = DeserializeJournalStrict(payload.Text);
            if (!HasValidOwnedJournal(rows?.Owned))
                throw new InvalidDataException("Invalid AI app OWNER journal");
            // Learning can be quarantined WITHOUT losing ownership of an
            // already stopped service or disabled startup/task. Invalid
            // OWNERS remain fail-closed and must never be guessed/repaired.
            Journal safe = RecoverValidLearning(rows, out bool degraded);
            _owned.Clear();
            foreach (var pair in safe.Owned) _owned[pair.Key] = pair.Value;
            _feedback.Clear();
            if (safe.Feedback != null)
                foreach (var pair in safe.Feedback) _feedback[pair.Key] = pair.Value;
            _references.Clear();
            if (safe.References != null)
                foreach (var pair in safe.References) _references[pair.Key] = pair.Value;
            _learningRepairPending = degraded;
            _journalHealthy = true;
            _lastJournalErrorUtc = default;
            CaptureJournalStamp(payload.Digest);
        }
        catch (Exception ex)
        {
            _journalHealthy = false;
            _lastJournalErrorUtc = DateTime.UtcNow;
            LogService.Error("ai-system", "Third party AI owner journal unreadable", ex);
        }
    }

    // Keep persisted AI learning small even after years of app/task cycles.
    // Never discard an active transaction or an A2 reference, and never
    // discard a feedback row that could still affect the 7-day cooldown.
    private void PruneFeedback(DateTime now)
    {
        if (_feedback.Count <= 96) return;
        // .NET 10 Dictionary.Remove does not invalidate its enumerator.
        // Walk the existing validated map in place instead of allocating a
        // full KeyValuePair snapshot whenever durable learning is pruned.
        // Active owners/A2 references are still protected from eviction.
        foreach (var pair in _feedback)
        {
            if (now - pair.Value.UpdatedUtc <= TimeSpan.FromDays(30) ||
                _owned.ContainsKey(pair.Key) || _references.ContainsKey(pair.Key))
                continue;
            _feedback.Remove(pair.Key);
        }
        if (_feedback.Count <= 128) return;
        foreach (var key in _feedback
            .Where(x => now - x.Value.UpdatedUtc > TimeSpan.FromDays(7) &&
                !_owned.ContainsKey(x.Key) && !_references.ContainsKey(x.Key))
            .OrderBy(x => x.Value.UpdatedUtc)
            .Take(_feedback.Count - 96)
            .Select(x => x.Key).ToArray())
            _feedback.Remove(key);
    }

    // After ANY journal write failure, the host's pending owner/feedback
    // dictionaries are no longer proof of what's durable on disk. Never
    // start another native action from them: force a leased re-read next
    // retry window, preserving the last known owners until then.
    private void InvalidateJournalAfterFailedWrite(DateTime atUtc)
    {
        _journalHealthy = false;
        _lastJournalErrorUtc = atUtc;
        _lastScanUtc = DateTime.MinValue;
        _lastFailedOffRestoreUtc = default;
    }

    internal static bool RunFailedJournalWriteSmoke()
    {
        var worker = new AiThirdPartyIdleAutomationService();
        DateTime at = new(2026, 10, 22, 11, 0, 0, DateTimeKind.Utc);
        var owner = new Owner("service", "Steam Client Service", "steam",
            at.AddMinutes(-5), true, true, OriginalServiceStartMode: 3);
        worker._owned[Key(owner.Kind, owner.Id)] = owner;
        worker._lastScanUtc = at.AddMinutes(-1);
        worker._lastFailedOffRestoreUtc = at.AddSeconds(-10);
        worker.InvalidateJournalAfterFailedWrite(at);
        return !worker._journalHealthy &&
            worker._lastJournalErrorUtc == at &&
            worker._lastScanUtc == DateTime.MinValue &&
            worker._lastFailedOffRestoreUtc == default &&
            worker._owned.Count == 1 &&
            ReferenceEquals(worker._owned[Key(owner.Kind, owner.Id)], owner) &&
            !AiThirdPartyIdlePolicy.MayRetryJournalRead(at, at.AddSeconds(59)) &&
            AiThirdPartyIdlePolicy.MayRetryJournalRead(at, at.AddSeconds(60));
    }

    private async Task PersistAsync(CancellationToken token)
    {
        string temp = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PruneFeedback(DateTime.UtcNow);
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            byte[] journalBytes = SerializeBoundedJournalBytes(
                new Journal(_owned, _feedback, _references));
            await File.WriteAllBytesAsync(temp, journalBytes, token)
                .ConfigureAwait(false);
            // Cross-Host lease is authoritative for compliant RuntimeHosts.
            // Detect even noncompliant external changes before replacement;
            // never silently clobber a newer ownership/restore journal.
            if (!JournalUnchangedSinceLeaseRead())
                throw new IOException(
                    "AI journal changed since validated load; refusing stale overwrite");
            File.Move(temp, JournalPath, overwrite: true);
            CaptureJournalStamp(SHA256.HashData(journalBytes));
            // Our own atomic swap is not an external change.
        }
        catch
        {
            // A failed prewrite, commit, or external stamp conflict must
            // NEVER leave a healthy journal flag pointing at speculative
            // in-memory state. Existing one-minute retry revalidates disk
            // under the same cross-host exclusive lease.
            InvalidateJournalAfterFailedWrite(DateTime.UtcNow);
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
                // Cleanup must not conceal the original durable write result.
            }
            catch (UnauthorizedAccessException)
            {
                // A leftover uniquely-named temp is never a valid journal.
            }
        }
    }
}
