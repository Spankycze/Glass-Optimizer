using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static partial class BackgroundActivityService
{
    // Pure legacy-oracle parity over ALL fixed UI/AI definition slots.
    // A single scan of a potentially large preference profile must behave
    // exactly like repeated UserFeatureProfile.Keeps() calls. Keep wins over
    // Unused even if Unused occurs first; untrusted input is always Unknown.
    internal static bool RunPreferenceBitmaskParitySmoke()
    {
        if (Definitions.Length * 2 > 64 ||
            PreferenceTokenBits.Count != Definitions.Length * 2)
            return false;

        var profiles = new List<UserFeatureProfile>
        {
            new(Array.Empty<string>(), DateTime.UtcNow),
            new(new[] { "BG:SPOTIFY:UNUSED", "bg:spotify:KEEP",
                "bg:discord:unused", "misc", "bg:steam:keep" }, DateTime.UtcNow),
            new(new[] { "bg:steam:unused", "BG:STEAM:UNUSED",
                "bg:steam:KEEP", "bg:steam:unused" }, DateTime.UtcNow),
            new(Enumerable.Range(0, 1000)
                .Select(i => "unrelated:" + i).ToArray(), DateTime.UtcNow),
            new(new[] { "bg:steam:keep", "bg:widgets:unused" },
                DateTime.UtcNow) { IsUntrusted = true },
            new(null!, DateTime.UtcNow)
        };
        for (int i = 0; i < Definitions.Length; i++)
        {
            Def def = Definitions[i];
            if (def.PreferenceSlot != i ||
                !PreferenceTokenBits.TryGetValue(
                    def.KeepPreferenceKey.ToUpperInvariant(), out ulong keep) ||
                keep != 1UL << i ||
                !PreferenceTokenBits.TryGetValue(
                    def.UnusedPreferenceKey.ToUpperInvariant(), out ulong unused) ||
                unused != 1UL << (i + Definitions.Length))
                return false;
            profiles.Add(new(
                new[] { def.UnusedPreferenceKey, def.KeepPreferenceKey,
                    def.KeepPreferenceKey.ToUpperInvariant(), "misc" },
                DateTime.UtcNow));
            profiles.Add(new(
                new[] { def.UnusedPreferenceKey }, DateTime.UtcNow));
        }

        foreach (UserFeatureProfile profile in profiles)
        {
            ulong flags = ReadPreferenceFlags(profile);
            foreach (Def def in Definitions)
            {
                var expected = GetPreference(profile, def);
                if (GetPreference(flags, def) != expected ||
                    GetPreference(flags, profile, def.Key) != expected ||
                    GetPreference(flags, profile, def.Key.ToUpperInvariant()) != expected)
                    return false;
            }
            foreach (string unknown in new[] {
                "unknown-app", "firefox", "NOT-MANAGED" })
                if (GetPreference(flags, profile, unknown) !=
                    GetPreference(profile, unknown))
                    return false;
        }
        return true;
    }

    // No user profile writes in this fixture. A second click on an
    // identical canonical preference should skip the durable write, but
    // conflict/duplicate/case cleanup must still write and verify.
    internal static bool RunCanonicalPreferenceWriteSkipSmoke()
    {
        DateTime now = new(2026, 10, 23, 3, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            Array.Empty<string>(),
            new[] { "misc", "bg:spotify:keep", "more" },
            new[] { "bg:spotify:unused", "other" },
            new[] { "BG:SPOTIFY:KEEP", "other" },
            new[] { "bg:spotify:keep", "bg:spotify:keep" },
            new[] { "bg:spotify:unused", "bg:spotify:keep" },
            new[] { "misc", "BG:SPOTIFY:UNUSED" },
            new[] { "bg:discord:keep", "misc", "bg:steam:unused" }
        };
        foreach (string[] features in rows)
        foreach (var preference in new[]
        {
            BackgroundAppPreference.Keep,
            BackgroundAppPreference.Unused,
            BackgroundAppPreference.Unknown
        })
        {
            var profile = new UserFeatureProfile(features, now);
            string[] updated = ReplacePreferenceKeys(features, "spotify", preference);
            bool sameCanonical = features.AsSpan().SequenceEqual(updated) &&
                GetPreference(profile, "spotify") == preference;
            if (IsCanonicalPreferenceNoOp(profile, updated,
                    "spotify", preference) != sameCanonical)
                return false;
            if (IsCanonicalPreferenceNoOp(
                    profile with { IsUntrusted = true },
                    updated, "spotify", preference))
                return false;
        }
        var keep = new UserFeatureProfile(
            new[] { "misc", "bg:spotify:keep" }, now);
        if (!IsCanonicalPreferenceNoOp(keep,
                ReplacePreferenceKeys(keep.KeepFeatures, "spotify",
                    BackgroundAppPreference.Keep), "spotify",
                BackgroundAppPreference.Keep))
            return false;
        var conflict = new UserFeatureProfile(
            new[] { "bg:spotify:unused", "bg:spotify:keep" }, now);
        return !IsCanonicalPreferenceNoOp(conflict,
            ReplacePreferenceKeys(conflict.KeepFeatures, "spotify",
                BackgroundAppPreference.Keep), "spotify",
            BackgroundAppPreference.Keep);
    }

    // Pure record-identity + value oracle: a redraw must always return a
    // new array, clone only rows whose effective preference changed, and
    // never modify the original immutable records or the input ordering.
    internal static bool RunPreferenceProjectionIdentitySmoke()
    {
        var rows = new BackgroundActivityState[Definitions.Length + 1];
        for (int i = 0; i < rows.Length; i++)
        {
            Def def = Definitions[i % Definitions.Length];
            rows[i] = new BackgroundActivityState(
                i == rows.Length - 1 ? "not-managed" : def.Key,
                def.Label, def.Processes, i, 2.5 * i, i / 3d,
                i % 3 == 0 ? BackgroundAppPreference.Keep :
                    BackgroundAppPreference.Unknown,
                i % 2, i % 4);
        }
        DateTime at = new(2026, 10, 23, 3, 0, 0, DateTimeKind.Utc);
        var profiles = new[]
        {
            new UserFeatureProfile(Array.Empty<string>(), at),
            new UserFeatureProfile(new[] {
                "BG:SPOTIFY:KEEP", "bg:discord:unused",
                "bg:steam:keep" }, at),
            new UserFeatureProfile(new[] {
                "BG:SPOTIFY:UNUSED", "bg:spotify:keep",
                "bg:not-managed:unused" }, at)
        };
        foreach (UserFeatureProfile profile in profiles)
        {
            var expected = rows.Select(row =>
                row with { Preference = GetPreference(profile, row.Key) }).ToArray();
            var actual = ProjectCurrentPreferences(rows, profile);
            if (ReferenceEquals(rows, actual) ||
                !expected.SequenceEqual(actual))
                return false;
            for (int i = 0; i < rows.Length; i++)
            {
                bool changed = expected[i].Preference != rows[i].Preference;
                if (ReferenceEquals(actual[i], rows[i]) == changed ||
                    actual[i].Key != rows[i].Key ||
                    actual[i].WorkingSetMb != rows[i].WorkingSetMb ||
                    actual[i].ProcessCount != rows[i].ProcessCount)
                    return false;
            }
        }
        return true;
    }

    internal static bool RunAiPreferenceSnapshotSmoke()
    {
        var now = new DateTime(2026, 10, 22, 18, 0, 0, DateTimeKind.Utc);
        var protectedApp = new UserFeatureProfile(
            new[] { "bg:spotify:keep" }, now);
        var unusedApp = new UserFeatureProfile(
            new[] { "bg:spotify:unused" }, now);
        var conflictingApp = new UserFeatureProfile(
            new[] { "BG:SPOTIFY:UNUSED", "bg:spotify:keep" }, now);
        var damagedApp = protectedApp with { IsUntrusted = true };
        return !NeedsStartupScoring(includeDormant: true) &&
            NeedsStartupScoring(includeDormant: false) &&
            HasExactAiInventoryScope() &&
            RunAiObservedIndexSmoke() &&
            RunUiProjectionParitySmoke() &&
            RunUiPreAllocationFilterParitySmoke() &&
            RunStartupSearchSpanParitySmoke() &&
            RunUiStartupSinglePassParitySmoke() &&
            RunFreshStartupBatchAdmissionSmoke() &&
            RunManualBackgroundActionsParitySmoke() &&
            RunPreferenceBitmaskParitySmoke() &&
            RunCanonicalPreferenceWriteSkipSmoke() &&
            RunPreferenceProjectionIdentitySmoke() &&
            AiRecoveryProcesses.TryGetValue("STEAM", out var steamProcesses) &&
            steamProcesses.Contains("steamwebhelper", StringComparer.OrdinalIgnoreCase) &&
            !AiRecoveryProcesses.ContainsKey("widgets") &&
            !AiRecoveryProcesses.ContainsKey("onedrive") &&
            ReadFirefoxPresence(new Dictionary<string, BackgroundProcessStats>()) ==
                0 &&
            ReadFirefoxPresence(new Dictionary<string, BackgroundProcessStats>
            {
                ["firefox"] = new(2, 0, 0)
            }) == 2 &&
            ReferenceEquals(ResolveProfile(protectedApp), protectedApp) &&
            GetPreference(ResolveProfile(protectedApp), "spotify") ==
                BackgroundAppPreference.Keep &&
            GetPreference(ResolveProfile(unusedApp), "spotify") ==
                BackgroundAppPreference.Unused &&
            GetPreference(ResolveProfile(unusedApp), "discord") ==
                BackgroundAppPreference.Unknown &&
            GetPreference(conflictingApp, "SPOTIFY") ==
                BackgroundAppPreference.Keep &&
            GetPreference(damagedApp, "spotify") ==
                BackgroundAppPreference.Unknown &&
            GetPreference(protectedApp, "nonexistent-key") ==
                BackgroundAppPreference.Unknown &&
            DefinitionsByKey.TryGetValue("SPOTIFY", out Def? spotify) &&
            spotify.KeepPreferenceKey == "bg:spotify:keep" &&
            spotify.UnusedPreferenceKey == "bg:spotify:unused" &&
            GetPreference(protectedApp, spotify) ==
                BackgroundAppPreference.Keep;
    }

    // Pure projection from the existing AI process table. An extra native
    // GetProcessesByName would repeat enumeration and open/close handles.
    internal static int ReadFirefoxPresence(
        IReadOnlyDictionary<string, BackgroundProcessStats> metrics) =>
        metrics.TryGetValue("firefox", out BackgroundProcessStats value)
            ? value.Count : 0;

    private static BackgroundActivityState[] ReadCore(
        IReadOnlyList<StartupEntry> startup,
        CancellationToken token, bool includeDormant = false,
        UserFeatureProfile? profileSnapshot = null)
    {
        var profile = ResolveProfile(profileSnapshot);
        ulong preferenceFlags = ReadPreferenceFlags(profile);
        var definitions = includeDormant ? AiDefinitions : Definitions;
        // Both inventories use an exact fixed-size array. The user-facing
        // projection compacts and stable-sorts that same buffer in place,
        // instead of creating a List plus LINQ Where/OrderBy/ThenBy arrays.
        BackgroundActivityState[] result = new BackgroundActivityState[
            definitions.Length + (includeDormant ? 1 : 0)];
        int resultIndex = 0;
        // Automatic decisions only enumerate process names that can actually
        // be managed by AI. UI still captures every configured process.
        var processMetrics = BackgroundProcessSnapshotService.Capture(
            includeDormant ? AiPresenceProcessNames : RelevantProcessNames, token,
            readResourceCounters: BackgroundProcessSnapshotService
                .ShouldReadResourceCounters(includeDormant));
        // The normal UI needs full startup scoring; AI presence snapshots
        // do not. Process each startup row ONCE, retaining only two tiny
        // per-definition counters on the stack. The previous approach kept
        // one joined search string per startup row alive through 17 complete
        // passes over that array. No token, field or source matching rules
        // change, including matches spanning adjacent joined fields.
        Span<int> startupTotalCounts = stackalloc int[definitions.Length];
        Span<int> startupEnabledCounts = stackalloc int[definitions.Length];
        if (NeedsStartupScoring(includeDormant))
            CountStartupMatches(startup, definitions,
                startupTotalCounts, startupEnabledCounts, token);
        else
        {
            // stackalloc is not guaranteed to contain zeroes. AI does
            // not scan startup entries, so explicitly reset its counters.
            startupTotalCounts.Clear();
            startupEnabledCounts.Clear();
        }

        for (int defIndex = 0; defIndex < definitions.Length; defIndex++)
        {
            token.ThrowIfCancellationRequested();
            Def def = definitions[defIndex];
            int count = 0;
            long workingSet = 0;
            double cpuSeconds = 0;

            foreach (string processName in def.Processes)
            {
                if (processMetrics.TryGetValue(processName, out BackgroundProcessStats stats))
                {
                    count += stats.Count;
                    workingSet += stats.WorkingSetBytes;
                    cpuSeconds += stats.CpuSeconds;
                }
            }

            int totalStartupMatches = startupTotalCounts[defIndex];
            int enabledStartupMatches = startupEnabledCounts[defIndex];
            BackgroundAppPreference effectivePreference =
                GetPreference(preferenceFlags, def);
            // UI never displays fully dormant/unknown entries. Exclude
            // them BEFORE allocating a record; AI deliberately keeps all
            // fixed indexed rows for its absence/presence decisions.
            if (!includeDormant && count == 0 &&
                totalStartupMatches == 0 &&
                effectivePreference == BackgroundAppPreference.Unknown)
                continue;
            result[resultIndex++] = new BackgroundActivityState(
                def.Key,
                def.Label,
                def.Processes,
                count,
                workingSet / 1048576d,
                cpuSeconds,
                effectivePreference,
                enabledStartupMatches,
                totalStartupMatches);
        }

        if (includeDormant)
        {
            // Internal AI-only presence row; never added to the UI or the
            // list of apps allowed to be automatically disabled. No List
            // capacity growth or final ToArray copy in this hot path.
            result[resultIndex] = new BackgroundActivityState(
                "firefox", "Firefox", FirefoxPresenceNames,
                ReadFirefoxPresence(processMetrics), 0, 0,
                BackgroundAppPreference.Unknown, 0, 0);
            return result;
        }
        return ProjectUiStatesStable(result, resultIndex);
    }

    // This runs only for the UI, not the AI background scanner. Joined
    // fields are identical to the former cache; they are just short-lived
    // per startup instead of retained in an O(startupCount) string array.
    // Buffers are stack-backed and fixed to the 17 known definitions.
    private static void CountStartupMatches(
        IReadOnlyList<StartupEntry> startup,
        IReadOnlyList<Def> definitions,
        Span<int> totals, Span<int> enabled,
        CancellationToken token)
    {
        // The caller supplies stack memory with unspecified initial bytes.
        // Never interpret stale stack contents as actual startup evidence.
        totals.Clear();
        enabled.Clear();
        // Allocate stack scratch ONCE for the whole inventory, not once
        // per iteration (which could exhaust the stack on large lists).
        Span<char> scratch = stackalloc char[
            BackgroundStartupSearchPolicy.ScratchCharacters];
        for (int i = 0; i < startup.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            StartupEntry item = startup[i];
            ReadOnlySpan<char> combined =
                BackgroundStartupSearchPolicy.FormatInto(
                    item.Name, item.Command, item.FilePath, item.Publisher,
                    scratch);
            // Exact, non-truncated fallback for unusually long commands.
            string? longText = combined.IsEmpty
                ? BackgroundStartupSearchPolicy.BuildSearchText(
                    item.Name, item.Command, item.FilePath, item.Publisher)
                : null;
            for (int j = 0; j < definitions.Count; j++)
            {
                if (!BackgroundStartupSearchPolicy.MatchesText(
                        longText is null ? combined : longText.AsSpan(),
                        definitions[j].MatchTokens))
                    continue;
                totals[j]++;
                if (item.Enabled) enabled[j]++;
            }
        }
    }

    // Only 17 fixed UI definitions can enter this function. Compact the
    // same array in place and stable-insertion-sort it, retaining EXACT
    // LINQ Where/OrderByDescending/ThenBy semantics and original ties.
    // O(17^2) comparisons have a fixed small bound; no transient LINQ
    // iterator/sort buffers, no List, and no extra output array when every
    // application is visible. A shorter result is resized once.
    internal static BackgroundActivityState[] ProjectUiStatesStable(
        BackgroundActivityState[] candidates) =>
        ProjectUiStatesStable(candidates, candidates.Length);

    // The used prefix can be shorter than buffer capacity because fully
    // hidden UI rows were not instantiated in the first place.
    private static BackgroundActivityState[] ProjectUiStatesStable(
        BackgroundActivityState[] candidates, int usedCount)
    {
        int count = 0;
        StringComparer labelComparer = StringComparer.CurrentCultureIgnoreCase;
        for (int sourceIndex = 0; sourceIndex < usedCount; sourceIndex++)
        {
            BackgroundActivityState candidate = candidates[sourceIndex];
            if (candidate.ProcessCount == 0 &&
                candidate.TotalStartupMatches == 0 &&
                candidate.Preference == BackgroundAppPreference.Unknown)
                continue;
            int insert = count;
            while (insert > 0)
            {
                BackgroundActivityState previous = candidates[insert - 1];
                int byMemory = previous.WorkingSetMb.CompareTo(candidate.WorkingSetMb);
                if (byMemory > 0 ||
                    (byMemory == 0 &&
                     labelComparer.Compare(previous.Label, candidate.Label) <= 0))
                    break;
                candidates[insert] = previous;
                insert--;
            }
            candidates[insert] = candidate;
            count++;
        }
        if (count != candidates.Length)
            Array.Resize(ref candidates, count);
        return candidates;
    }

    private static bool MatchesStartup(StartupEntry item, Def def) =>
        BackgroundStartupSearchPolicy.MatchesFields(
            item.Name, item.Command, item.FilePath, item.Publisher,
            def.MatchTokens);

    // Fast path for the fixed process definitions, including the full UI
    // and the AI-only candidate subset. These immutable keys avoid two
    // interpolated string allocations per state in repeated inventories.
    private static BackgroundAppPreference GetPreference(UserFeatureProfile profile, Def def)
    {
        if (profile.Keeps(def.KeepPreferenceKey)) return BackgroundAppPreference.Keep;
        if (profile.Keeps(def.UnusedPreferenceKey)) return BackgroundAppPreference.Unused;
        return BackgroundAppPreference.Unknown;
    }

    private static BackgroundAppPreference GetPreference(UserFeatureProfile profile, string key)
    {
        // User-facing preferences may arrive by an arbitrary key; retain
        // the existing behavior for unknown identifiers.
        if (DefinitionsByKey.TryGetValue(key, out Def? def))
            return GetPreference(profile, def);
        if (profile.Keeps(PreferenceKey(key, true))) return BackgroundAppPreference.Keep;
        if (profile.Keeps(PreferenceKey(key, false))) return BackgroundAppPreference.Unused;
        return BackgroundAppPreference.Unknown;
    }

    private static string PreferenceKey(string key, bool keep) =>
        $"bg:{key}:{(keep ? "keep" : "unused")}";
}
