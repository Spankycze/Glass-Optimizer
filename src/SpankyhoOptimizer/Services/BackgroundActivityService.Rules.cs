using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static partial class BackgroundActivityService
{
    // AI only requires process presence and the user's Keep/Unused intent.
    // Startup matching and working-set sorting belong to the normal UI;
    // computing them during every AI pass produces unused allocations.
    internal static bool NeedsStartupScoring(bool includeDormant) => !includeDormant;

    // Use the same exact user preference snapshot for presence decisions
    // and the subsequent AI safety gate; no second disk/cache hydration.
    internal static UserFeatureProfile ResolveProfile(
        UserFeatureProfile? supplied) =>
        supplied ?? UserFeatureProfileService.Load();

    // Pure synthetic snapshot proof: indexed reads are case-insensitive,
    // are exact to the captured app identity, and reject partial/reordered
    // inventories instead of treating someone else's process as absent.
    internal static bool RunAiObservedIndexSmoke()
    {
        var states = new BackgroundActivityState[AiDefinitions.Length + 1];
        for (int i = 0; i < AiDefinitions.Length; i++)
        {
            Def definition = AiDefinitions[i];
            states[i] = new BackgroundActivityState(
                definition.Key, definition.Label, definition.Processes,
                i + 1, 0, 0, BackgroundAppPreference.Unknown, 0, 0);
        }
        states[FirefoxAiStateIndex] = new BackgroundActivityState(
            "firefox", "Firefox", FirefoxPresenceNames, 7, 0, 0,
            BackgroundAppPreference.Unknown, 0, 0);
        var snapshot = new AiObservedSnapshot(states);
        foreach (Def definition in AiDefinitions)
        {
            if (!snapshot.TryGetValue(definition.Key.ToUpperInvariant(),
                    out BackgroundActivityState found) ||
                !found.Key.Equals(definition.Key, StringComparison.Ordinal))
                return false;
        }
        if (!snapshot.TryGetValue("FIREFOX", out var firefox) ||
            firefox.ProcessCount != 7 ||
            snapshot.TryGetValue("windows-security", out _) ||
            snapshot.TryGetValue("onedrive", out _) ||
            new AiObservedSnapshot(Array.Empty<BackgroundActivityState>())
                .TryGetValue("steam", out _) ||
            new AiObservedSnapshot(states.AsSpan(0, states.Length - 1).ToArray())
                .TryGetValue("firefox", out _))
            return false;

        // Even if an inventory is unexpectedly reordered, fail closed
        // instead of returning the process count of another application.
        int steamIndex = AiStateIndices["steam"];
        var changed = (BackgroundActivityState[])states.Clone();
        changed[steamIndex] = new BackgroundActivityState(
            "discord", "Wrong identity", Array.Empty<string>(), 99, 0, 0,
            BackgroundAppPreference.Unknown, 0, 0);
        return !new AiObservedSnapshot(changed).TryGetValue("steam", out _);
    }

    // In-memory oracle verifies the original stable LINQ UI ordering.
    // The UI can discard dormant Unknown rows before allocating their
    // BackgroundActivityState objects. Compare that pre-filtered prefix
    // against the PREVIOUS projection (which allocated every row first),
    // including stable equal-memory/equal-label ordering and zero rows.
    internal static bool RunUiPreAllocationFilterParitySmoke()
    {
        for (int count = 0; count <= Definitions.Length; count++)
        for (int variant = 0; variant < 9; variant++)
        {
            var previousRows = new BackgroundActivityState[count];
            for (int i = 0; i < count; i++)
            {
                bool running = (i + variant) % 4 == 0;
                int startup = (i + variant) % 5 == 0 ? 2 : 0;
                BackgroundAppPreference preference = variant switch
                {
                    0 => BackgroundAppPreference.Unknown,
                    1 => BackgroundAppPreference.Keep,
                    2 => BackgroundAppPreference.Unused,
                    _ => i % 3 == 0 ? BackgroundAppPreference.Keep :
                        BackgroundAppPreference.Unknown
                };
                previousRows[i] = new BackgroundActivityState(
                    "sample-" + i,
                    i % 2 == 0 ? "Écho" : "écho",
                    Array.Empty<string>(), running ? 2 : 0,
                    (i + variant) % 3 == 0 ? 12.25 : 0d, 0,
                    preference, startup, startup);
            }
            BackgroundActivityState[] expected = previousRows
                .Where(x => x.ProcessCount > 0 ||
                    x.TotalStartupMatches > 0 ||
                    x.Preference != BackgroundAppPreference.Unknown)
                .OrderByDescending(x => x.WorkingSetMb)
                .ThenBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var working = new BackgroundActivityState[count];
            int used = 0;
            foreach (BackgroundActivityState row in previousRows)
                if (row.ProcessCount != 0 || row.TotalStartupMatches != 0 ||
                    row.Preference != BackgroundAppPreference.Unknown)
                    working[used++] = row;
            BackgroundActivityState[] actual =
                ProjectUiStatesStable(working, used);
            if (!actual.SequenceEqual(expected) ||
                (used == count && !ReferenceEquals(actual, working)) ||
                used != expected.Length)
                return false;
            // The sorter must reuse exact original immutable row identities.
            foreach (BackgroundActivityState state in actual)
                if (!previousRows.Any(x => ReferenceEquals(x, state)))
                    return false;
        }
        return true;
    }

    internal static bool RunUiProjectionParitySmoke()
    {
        for (int length = 0; length <= Definitions.Length; length++)
        for (int variant = 0; variant < 7; variant++)
        {
            var source = new BackgroundActivityState[length];
            for (int i = 0; i < length; i++)
            {
                source[i] = new BackgroundActivityState(
                    "fixture" + i, i % 2 == 0 ? "Stejný" : "stejný",
                    Array.Empty<string>(), (i + variant) % 3 == 0 ? 1 : 0,
                    (i + variant) % 5 == 0 ? 12.5 : 0,
                    0, variant % 3 == 0 ? BackgroundAppPreference.Keep :
                        BackgroundAppPreference.Unknown,
                    0, (i + variant) % 4 == 0 ? 1 : 0);
            }
            var expected = source.Where(x => x.ProcessCount > 0 ||
                    x.TotalStartupMatches > 0 ||
                    x.Preference != BackgroundAppPreference.Unknown)
                .OrderByDescending(x => x.WorkingSetMb)
                .ThenBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var working = (BackgroundActivityState[])source.Clone();
            var actual = ProjectUiStatesStable(working);
            if (!actual.SequenceEqual(expected) ||
                (expected.Length == source.Length &&
                 !ReferenceEquals(actual, working)))
                return false;
        }
        return true;
    }

    // Pure semantic oracle for a bounded four-field UTF-16 span formatter.
    // The fallback must preserve arbitrarily long startup commands, and a
    // match can span the *spaces inserted between fields* (not just fields
    // independently). No real startup inventory or Windows state is read.
    internal static bool RunStartupSearchSpanParitySmoke()
    {
        var cases = new (string? Name, string? Command,
            string? FilePath, string? Publisher)[]
        {
            (null, null, null, null),
            ("", "", "", ""),
            ("steam", "exe", "", ""),
            ("STEAM", "EXE", @"C:\steam.exe", "Valve"),
            ("a b", " c", "D E", " F "),
            ("é É e", "Türk İ i", "Σ σ ς", "😀 Rocket"),
            ("Dropbox", "Windows updater", @"C:\Spaces\app launcher.exe",
                "A S U S"),
            (new string('x', BackgroundStartupSearchPolicy.ScratchCharacters - 3),
                "", "", ""),
            (new string('x', BackgroundStartupSearchPolicy.ScratchCharacters - 2),
                "", "", ""),
            ("short", new string('q', 8192), "last", "vendor")
        };
        string[] tokens = { "", " ", "  ", "m e", "E C", "x", "xx",
            "steam", "EXE", "valve", "no-match", "app launcher",
            "Türk", "İ", "i", "é", "Σ", "σ", "ς", "😀",
            "Windows updater", "q", "last vendor", "  F " };
        Span<char> scratch = stackalloc char[
            BackgroundStartupSearchPolicy.ScratchCharacters];
        foreach (var sample in cases)
        {
            string legacy = BackgroundStartupSearchPolicy.BuildSearchText(
                sample.Name, sample.Command, sample.FilePath, sample.Publisher);
            ReadOnlySpan<char> formatted = BackgroundStartupSearchPolicy.FormatInto(
                sample.Name, sample.Command, sample.FilePath, sample.Publisher,
                scratch);
            if (legacy.Length <= scratch.Length)
            {
                if (!formatted.SequenceEqual(legacy.AsSpan())) return false;
            }
            else if (!formatted.IsEmpty) return false;
            foreach (string token in tokens)
            {
                bool legacyMatch = legacy.Contains(token,
                    StringComparison.OrdinalIgnoreCase);
                bool current = BackgroundStartupSearchPolicy.MatchesFields(
                    sample.Name, sample.Command, sample.FilePath,
                    sample.Publisher, new[] { token });
                if (legacyMatch != current) return false;
            }
            foreach (string[] combined in new[]
            {
                new[] { "not-found", "m e" },
                new[] { "not-found", "no-match" },
                new[] { "not-found", " ", "steam" },
                Array.Empty<string>()
            })
            {
                bool legacyMatch = combined.Any(t => legacy.Contains(t,
                    StringComparison.OrdinalIgnoreCase));
                bool current = BackgroundStartupSearchPolicy.MatchesFields(
                    sample.Name, sample.Command, sample.FilePath,
                    sample.Publisher, combined);
                if (legacyMatch != current) return false;
            }
        }
        return true;
    }

    // Compare the new single startup pass to the original UI algorithm:
    // first retain joined four-field text for EVERY row, then traverse all
    // rows once per definition. Includes empty lists, non-matching rows,
    // repeated labels, disabled rows, null fields and split-word tokens
    // that happen to match ACROSS a joined field separator.
    internal static bool RunUiStartupSinglePassParitySmoke()
    {
        Span<int> totals = stackalloc int[Definitions.Length];
        Span<int> enabled = stackalloc int[Definitions.Length];
        foreach (int size in new[] { 0, 1, 2, 5, 17, 64, 127 })
        foreach (int variant in new[] { 0, 1, 2, 3 })
        {
            var rows = new List<StartupEntry>(size);
            for (int i = 0; i < size; i++)
            {
                string name = ((i + variant) % 9) switch
                {
                    0 => "steam client", 1 => "Discord", 2 => "phone",
                    3 => "Spotify", 4 => "Xbox app", 5 => "widgetservice",
                    6 => "copilot", 7 => "Battle.net", _ => "not managed"
                };
                string command = ((i + variant) % 7) switch
                {
                    0 => "link", 1 => "riotclient", 2 => "none",
                    3 => "zoom workplace", 4 => "slack.exe",
                    5 => "upc.exe", _ => "EpicGamesLauncher.exe"
                };
                if (variant == 3 && i % 31 == 0)
                    command += new string('x',
                        BackgroundStartupSearchPolicy.ScratchCharacters);
                rows.Add(new StartupEntry(
                    "fixture" + i, name, command,
                    Microsoft.Win32.RegistryHive.CurrentUser,
                    @"Software\Microsoft\Windows\CurrentVersion\Run",
                    (i + variant) % 3 != 0,
                    i % 4 == 0 ? "EA app" : "unknown",
                    i % 3 == 0 ? @"C:\Apps\GalaxyClient.exe" : null));
            }
            var sourceStrings = rows.Select(x =>
                BackgroundStartupSearchPolicy.BuildSearchText(
                    x.Name, x.Command, x.FilePath, x.Publisher)).ToArray();
            // Deliberately dirty stack spans before EVERY sample. The
            // production counting helper must reset them itself, even
            // when no startup items are present.
            totals.Fill(12345);
            enabled.Fill(54321);
            CountStartupMatches(rows, Definitions, totals, enabled,
                CancellationToken.None);
            for (int defIndex = 0; defIndex < Definitions.Length; defIndex++)
            {
                int total = 0, on = 0;
                Def def = Definitions[defIndex];
                for (int i = 0; i < sourceStrings.Length; i++)
                {
                    if (!BackgroundStartupSearchPolicy.MatchesText(
                        sourceStrings[i], def.MatchTokens))
                        continue;
                    total++;
                    if (rows[i].Enabled) on++;
                }
                if (total != totals[defIndex] || on != enabled[defIndex])
                    return false;
            }
        }
        // An already cancelled UI refresh must not consume startup rows.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellationRows = new[] { new StartupEntry(
            "cancel", "Steam", "steam.exe",
            Microsoft.Win32.RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            true, "Steam", @"C:\Apps\Steam.exe") };
        Span<int> scratchTotal = stackalloc int[Definitions.Length];
        Span<int> scratchEnabled = stackalloc int[Definitions.Length];
        try
        {
            CountStartupMatches(cancellationRows, Definitions,
                scratchTotal, scratchEnabled, cancellation.Token);
            return false;
        }
        catch (OperationCanceledException) { }
        return true;
    }

    // No real startup inventory or user state is touched. Reject any
    // externally changed selection before writing its FIRST row, including
    // changes to commands/tasks/folders and duplicate locator collisions.
    internal static bool RunFreshStartupBatchAdmissionSmoke()
    {
        foreach (Def def in Definitions)
        {
            var expected = new StartupEntry(
                "fixture|" + def.Key, def.MatchTokens[0],
                @"C:\Apps\sample.exe", Microsoft.Win32.RegistryHive.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                true, "Publisher", @"C:\Apps\sample.exe");
            var other = expected with
            {
                Id = "other", Name = "unrelated",
                Command = @"C:\Apps\different.exe",
                FilePath = @"C:\Apps\different.exe"
            };
            if (!MatchesStartup(expected, def) ||
                !HasExactFreshStartupBatch(def,
                    new[] { expected }, new[] { other, expected }) ||
                !HasExactFreshStartupBatch(def,
                    new[] { expected }, new[] {
                        expected with { Id = expected.Id.ToUpperInvariant(),
                            Name = expected.Name.ToUpperInvariant() } }))
                return false;

            var altered = new[]
            {
                expected with { Id = "replacement" },
                expected with { Name = "replacement" },
                expected with { Command = @"C:\Apps\changed.exe" },
                expected with { FilePath = @"C:\Apps\changed.exe" },
                expected with { Hive = Microsoft.Win32.RegistryHive.LocalMachine },
                expected with { View = Microsoft.Win32.RegistryView.Registry32 },
                expected with { RunPath = expected.RunPath + "Once" },
                expected with { SourceKind = "ScheduledTask" },
                expected with { SourceId = "other-task" },
                expected with { Enabled = false },
                expected with { CanToggle = false }
            };
            foreach (StartupEntry invalid in altered)
                if (HasExactFreshStartupBatch(def,
                    new[] { expected }, new[] { invalid }))
                    return false;

            if (HasExactFreshStartupBatch(def,
                    new[] { expected }, Array.Empty<StartupEntry>()) ||
                HasExactFreshStartupBatch(def,
                    new[] { expected }, new[] { expected, expected }) ||
                HasExactFreshStartupBatch(def,
                    new[] { expected, expected }, new[] { expected }))
                return false;

            // Publisher may be obtained from a refreshed metadata source.
            // It must not alter a verified executable/registry identity.
            if (!HasExactFreshStartupBatch(def,
                    new[] { expected },
                    new[] { expected with { Publisher = "New Publisher" } }))
                return false;
        }

        // Tasks and startup-folder sources use the same exact locator and
        // command checks. The UI must not disable a replaced task definition
        // after it displays the old command/name.
        Def steam = ResolveDefinition("steam");
        var task = new StartupEntry("task|\\Steam", "steam client",
            "steam.exe", Microsoft.Win32.RegistryHive.CurrentUser,
            "", true, "Task Scheduler", @"C:\Apps\steam.exe",
            "ScheduledTask", "\\Steam");
        var folder = task with
        {
            Id = "folder|steam", SourceKind = "StartupFolder",
            RunPath = @"C:\Startup", SourceId = @"C:\Startup\steam.exe"
        };
        return HasExactFreshStartupBatch(steam, new[] { task },
                   new[] { task }) &&
            HasExactFreshStartupBatch(steam, new[] { folder },
                   new[] { folder }) &&
            !HasExactFreshStartupBatch(steam, new[] { task },
                new[] { task with { Command = "other.exe" } }) &&
            !HasExactFreshStartupBatch(steam, new[] { folder },
                new[] { folder with { SourceId = @"C:\Other\steam.exe" } });
    }

    // Pure oracle for manual UI actions. No process kill, startup writes,
    // registry reads or user preference saves can happen in this smoke.
    internal static bool RunManualBackgroundActionsParitySmoke()
    {
        var rows = new List<StartupEntry>();
        int id = 0;
        foreach (Def def in Definitions)
        {
            string name = def.Label;
            string executable = def.Processes[0] + ".exe";
            var row = new StartupEntry(
                (++id).ToString(System.Globalization.CultureInfo.InvariantCulture),
                name, @"C:\Apps\" + executable,
                Microsoft.Win32.RegistryHive.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                true, name, @"C:\Apps\" + executable);
            rows.Add(row);
            rows.Add(row with { Enabled = false });
            rows.Add(row with { CanToggle = false });
            rows.Add(row with { Command = row.Command.ToUpperInvariant() });
        }
        foreach (Def def in Definitions)
        {
            Def original = Definitions.First(x => x.Key.Equals(
                def.Key.ToUpperInvariant(), StringComparison.OrdinalIgnoreCase));
            if (!ReferenceEquals(original, ResolveDefinition(def.Key)) ||
                !ReferenceEquals(original,
                    ResolveDefinition(def.Key.ToUpperInvariant())))
                return false;
            StartupEntry[] expected = rows.Where(x =>
                x.Enabled && x.CanToggle && MatchesStartup(x, original)).ToArray();
            List<StartupEntry> actual = SelectToggleableStartupMatches(
                ResolveDefinition(def.Key), rows);
            if (!actual.SequenceEqual(expected)) return false;
        }
        try { _ = ResolveDefinition("not-an-app"); return false; }
        catch (ArgumentException) { }
        try { _ = ResolveDefinition(null!); return false; }
        catch (ArgumentException) { }

        string[][] profiles =
        {
            Array.Empty<string>(),
            new[] { "bg:spotify:keep", "BG:SPOTIFY:UNUSED", "misc" },
            new[] { "misc", "MISC", "bg:spotify:keep",
                    "bg:discord:unused", "bg:spotify:keep" },
            new[] { "bg:spotify:unused", "theme:dark",
                    "bg:steam:keep", "bg:spotify:keep" }
        };
        foreach (string[] profile in profiles)
        foreach (string key in new[] { "spotify", "SPOTIFY", "discord", "unknown" })
        foreach (BackgroundAppPreference preference in Enum.GetValues<BackgroundAppPreference>())
        {
            string keep = PreferenceKey(key, true);
            string unused = PreferenceKey(key, false);
            var expected = profile.Where(x =>
                !x.Equals(keep, StringComparison.OrdinalIgnoreCase) &&
                !x.Equals(unused, StringComparison.OrdinalIgnoreCase)).ToList();
            if (preference == BackgroundAppPreference.Keep) expected.Add(keep);
            else if (preference == BackgroundAppPreference.Unused) expected.Add(unused);
            if (!ReplacePreferenceKeys(profile, key, preference)
                    .SequenceEqual(expected))
                return false;
            // The fallback cannot consume an arbitrary one-shot enumerable
            // twice. It must agree with the array optimized path.
            static IEnumerable<string> Stream(string[] source)
            {
                foreach (string item in source) yield return item;
            }
            if (!ReplacePreferenceKeys(Stream(profile), key, preference)
                    .SequenceEqual(expected))
                return false;
        }
        return true;
    }
}
