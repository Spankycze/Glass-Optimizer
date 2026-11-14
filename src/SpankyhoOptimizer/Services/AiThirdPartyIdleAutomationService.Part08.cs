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
}
