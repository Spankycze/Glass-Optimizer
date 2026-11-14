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

internal static partial class AiThirdPartyIdlePolicy
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
}
