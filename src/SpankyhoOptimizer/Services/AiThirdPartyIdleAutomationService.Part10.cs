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

internal sealed partial class AiThirdPartyIdleAutomationService
{
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
}
