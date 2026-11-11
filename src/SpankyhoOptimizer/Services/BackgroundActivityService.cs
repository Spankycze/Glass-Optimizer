using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public enum BackgroundAppPreference
{
    Unknown,
    Keep,
    Unused
}

public sealed record BackgroundActivityState(
    string Key,
    string Label,
    string[] ProcessNames,
    int ProcessCount,
    double WorkingSetMb,
    double CpuSeconds,
    BackgroundAppPreference Preference,
    int EnabledStartupMatches,
    int TotalStartupMatches);

public static class BackgroundActivityService
{
    private sealed record Def(string Key, string Label, string[] Processes, string[] MatchTokens)
    {
        // Cache the preference tokens once per process definition instead
        // of constructing two strings for each app on every AI/UI scan.
        internal string KeepPreferenceKey { get; } = $"bg:{Key}:keep";
        internal string UnusedPreferenceKey { get; } = $"bg:{Key}:unused";
        // Fixed index assigned only once in the type initializer, reused by
        // both UI and the AI subset (whose Def objects are shared).
        internal int PreferenceSlot { get; set; }
    }

    private static readonly Def[] Definitions =
    {
        new("onedrive", "OneDrive", new[] { "OneDrive" }, new[] { "onedrive" }),
        new("phone-link", "Phone Link", new[] { "PhoneExperienceHost", "YourPhone" }, new[] { "phoneexperience", "yourphone", "phone link" }),
        new("widgets", "Windows Widgets", new[] { "Widgets", "WidgetService" }, new[] { "widgets", "widgetservice" }),
        new("copilot", "Copilot", new[] { "Copilot" }, new[] { "copilot" }),
        new("teams", "Microsoft Teams", new[] { "ms-teams", "Teams" }, new[] { "ms-teams", "teams" }),
        new("steam", "Steam", new[] { "steam", "steamwebhelper" }, new[] { "steam.exe", "steam client" }),
        new("battlenet", "Battle.net", new[] { "Battle.net" }, new[] { "battle.net", "battlenet" }),
        new("epic", "Epic Games Launcher", new[] { "EpicGamesLauncher" }, new[] { "epicgameslauncher", "epic games" }),
        new("riot", "Riot Client", new[] { "RiotClientServices", "RiotClientUx" }, new[] { "riotclient", "riot client" }),
        new("discord", "Discord", new[] { "Discord" }, new[] { "discord" }),
        new("xbox", "Xbox app", new[] { "XboxPcApp" }, new[] { "xboxpcapp", "xbox app" }),
        new("ea", "EA app", new[] { "EADesktop", "EABackgroundService", "EALauncher" }, new[] { "eadesktop", "ea app", "ea launcher" }),
        new("ubisoft", "Ubisoft Connect", new[] { "upc", "UbisoftConnect", "UplayWebCore" }, new[] { "ubisoftconnect", "ubisoft connect", "uplay" }),
        new("gog", "GOG Galaxy", new[] { "GalaxyClient", "GalaxyCommunication" }, new[] { "galaxyclient", "gog galaxy" }),
        new("spotify", "Spotify", new[] { "Spotify" }, new[] { "spotify" }),
        new("slack", "Slack", new[] { "slack" }, new[] { "slack.exe", "slack" }),
        new("zoom", "Zoom", new[] { "Zoom", "ZoomWorkplace" }, new[] { "zoom.exe", "zoom workplace" })
    };

    static BackgroundActivityService()
    {
        for (int i = 0; i < Definitions.Length; i++)
            Definitions[i].PreferenceSlot = i;
    }

    // Immutable feature-token-to-bit index shared by UI and AI snapshots.
    // Keep always takes precedence over Unused, regardless of source order.
    // All 17 definitions fit in two disjoint halves of one ulong.
    private static readonly Dictionary<string, ulong> PreferenceTokenBits =
        BuildPreferenceTokenBits();

    private static Dictionary<string, ulong> BuildPreferenceTokenBits()
    {
        var result = new Dictionary<string, ulong>(
            Definitions.Length * 2, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Definitions.Length; i++)
        {
            result.Add(Definitions[i].KeepPreferenceKey, 1UL << i);
            result.Add(Definitions[i].UnusedPreferenceKey,
                1UL << (i + Definitions.Length));
        }
        return result;
    }

    // Trust and individual preferences are computed for this scan only.
    // Never cache flags across profile changes or turn untrusted settings
    // into a trusted empty policy.
    private static ulong ReadPreferenceFlags(UserFeatureProfile profile)
    {
        if (profile.IsUntrusted || profile.KeepFeatures is null)
            return 0;
        ulong flags = 0;
        foreach (string? key in profile.KeepFeatures)
            if (key is not null &&
                PreferenceTokenBits.TryGetValue(key, out ulong bit))
                flags |= bit;
        return flags;
    }

    private static BackgroundAppPreference GetPreference(
        ulong flags, Def definition)
    {
        int slot = definition.PreferenceSlot;
        if ((flags & (1UL << slot)) != 0)
            return BackgroundAppPreference.Keep;
        return (flags & (1UL << (slot + Definitions.Length))) != 0
            ? BackgroundAppPreference.Unused
            : BackgroundAppPreference.Unknown;
    }

    private static BackgroundAppPreference GetPreference(
        ulong flags, UserFeatureProfile profile, string key) =>
        DefinitionsByKey.TryGetValue(key, out Def? definition)
            ? GetPreference(flags, definition)
            : GetPreference(profile, key);

    // Fixed process names across refreshes: construct membership index once.
    private static readonly HashSet<string> RelevantProcessNames = new(
        Definitions.SelectMany(x => x.Processes), StringComparer.OrdinalIgnoreCase);
    // The AI owns only these third-party candidates. Do not inspect Windows
    // Widgets/Copilot/OneDrive/Phone Link/Xbox or other UI-only definitions
    // while making an automatic decision. UI continues using Definitions.
    private static readonly Def[] AiDefinitions = Definitions.Where(x =>
        AiThirdPartyIdlePolicy.AppKeys.Contains(x.Key,
            StringComparer.OrdinalIgnoreCase)).ToArray();
    private static readonly HashSet<string> AiProcessNames = new(
        AiDefinitions.SelectMany(x => x.Processes), StringComparer.OrdinalIgnoreCase);
    // Firefox is NOT a new AI-managed candidate. Its optional scheduled
    // task needs a read-only presence flag, which can be collected from the
    // exact same native process table snapshot as the managed apps.
    private static readonly string[] FirefoxPresenceNames = { "firefox" };
    private static readonly HashSet<string> AiPresenceProcessNames = new(
        AiProcessNames, StringComparer.OrdinalIgnoreCase) { "firefox" };
    // Urgent owned-resource restoration checks happen every 10 seconds.
    // Resolve its trusted app key directly without scanning all definitions
    // with LINQ on every check. This table is immutable after startup.
    private static readonly Dictionary<string, string[]> AiRecoveryProcesses =
        AiDefinitions.ToDictionary(x => x.Key, x => x.Processes,
            StringComparer.OrdinalIgnoreCase);
    // UI preference read-back and startup identity checks also reuse the
    // same fixed definition index, rather than scanning every Def for a key.
    private static readonly Dictionary<string, Def> DefinitionsByKey =
        Definitions.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
    // The AI snapshot is always a fixed-order array. Build its key-to-slot
    // lookup only once, instead of allocating and hashing a second transient
    // dictionary after every native process inventory.
    private static readonly Dictionary<string, int> AiStateIndices =
        AiDefinitions.Select((definition, index) => (definition.Key, index))
            .ToDictionary(x => x.Key, x => x.index,
                StringComparer.OrdinalIgnoreCase);
    private static readonly int FirefoxAiStateIndex = AiDefinitions.Length;

    // Reuse the already-built fixed AI inventory index for the urgent
    // 10-second owner recovery bitset; no second dictionary or linear
    // 12-key scan on every owner probe. Firefox only represents its
    // optional browser-choice task and remains outside the AI app list.
    internal static int GetAiRecoveryProbeIndex(string key) =>
        key.Equals("firefox", StringComparison.OrdinalIgnoreCase)
            ? FirefoxAiStateIndex :
        AiStateIndices.TryGetValue(key, out int index) ? index : -1;

    // A startup executable admission must compare the *canonical* managed
    // app identity. Reuse the existing case-insensitive static index rather
    // than allocating app.ToLowerInvariant() on each native HKCU Run row.
    // Firefox is a PRESENCE-only slot and must never become an admissible
    // startup target. Return the existing static definition key, not a copy.
    internal static string? GetAiCanonicalManagedKey(string key)
    {
        int index = GetAiRecoveryProbeIndex(key);
        return index >= 0 && index < AiDefinitions.Length
            ? AiDefinitions[index].Key : null;
    }

    private static readonly object PreferenceMutationGate = new();

    internal readonly struct AiObservedSnapshot(IReadOnlyList<BackgroundActivityState> states)
    {
        public bool TryGetValue(string key, out BackgroundActivityState state)
        {
            int index;
            if (key.Equals("firefox", StringComparison.OrdinalIgnoreCase))
                index = FirefoxAiStateIndex;
            else if (!AiStateIndices.TryGetValue(key, out index))
            {
                state = null!;
                return false;
            }
            if (index < states.Count &&
                states[index] is { } candidate &&
                candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                state = candidate;
                return true;
            }
            // An absent, short, or unexpectedly reordered array must never
            // be mistaken for evidence that some other app is idle.
            state = null!;
            return false;
        }
    }

    internal static bool HasExactAiInventoryScope() =>
        AiRecoveryProcesses.Count == AiThirdPartyIdlePolicy.AppKeys.Length &&
        AiStateIndices.Count == AiDefinitions.Length &&
        FirefoxAiStateIndex == AiDefinitions.Length &&
        AiThirdPartyIdlePolicy.AppKeys.All(x =>
            GetAiRecoveryProbeIndex(x) is >= 0 and < 63) &&
        GetAiRecoveryProbeIndex("FIREFOX") == FirefoxAiStateIndex &&
        GetAiRecoveryProbeIndex("windows-security") == -1 &&
        DefinitionsByKey.Count == Definitions.Length &&
        AiThirdPartyIdlePolicy.AppKeys.All(x => AiRecoveryProcesses.ContainsKey(x)) &&
        AiDefinitions.Length == AiThirdPartyIdlePolicy.AppKeys.Length &&
        AiDefinitions.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == AiThirdPartyIdlePolicy.AppKeys.Length &&
        AiDefinitions.All(x => AiThirdPartyIdlePolicy.AppKeys.Contains(
            x.Key, StringComparer.OrdinalIgnoreCase)) &&
        AiProcessNames.IsSubsetOf(RelevantProcessNames) &&
        !AiProcessNames.Contains("firefox") &&
        AiPresenceProcessNames.Count == AiProcessNames.Count + 1 &&
        AiPresenceProcessNames.Contains("firefox");

    public static Task<IReadOnlyList<BackgroundActivityState>> ReadAsync(
        IReadOnlyList<StartupEntry> startup,
        CancellationToken token = default) =>
        Task.Run<IReadOnlyList<BackgroundActivityState>>(() => ReadCore(startup, token), token);

    // AI needs evidence of *absence* even for apps without a startup entry.
    // Reuses the existing single native process snapshot, not N scans.
    internal static Task<IReadOnlyList<BackgroundActivityState>> ReadForAiAsync(
        IReadOnlyList<StartupEntry> startup,
        UserFeatureProfile profile, CancellationToken token) =>
        Task.Run<IReadOnlyList<BackgroundActivityState>>(
            () => ReadCore(startup, token, includeDormant: true,
                profileSnapshot: profile), token);

    // Cheap, narrow recovery probe: check only process names of an app
    // already affected by OUR prior AI mutation. No CPU/RAM counters and
    // no full process-table scan on every FanAgent tick.
    internal static bool IsRunningForAi(string key)
    {
        if (key.Equals("firefox", StringComparison.OrdinalIgnoreCase))
            return AiThirdPartyIdlePolicy.HasAnyRunningProcess("firefox");
        return AiRecoveryProcesses.TryGetValue(key, out string[]? names) &&
            AiThirdPartyIdlePolicy.HasAnyRunningProcess(names);
    }

    internal static bool MatchesStartupForAi(string key, StartupEntry entry) =>
        DefinitionsByKey.TryGetValue(key, out Def? def) &&
        MatchesStartup(entry, def);

    public static Task SetPreferenceAsync(string key, BackgroundAppPreference preference,
        CancellationToken token = default) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            SetPreference(key, preference);
        }, token);

    public static void SetPreference(string key, BackgroundAppPreference preference)
    {
        lock (PreferenceMutationGate)
        {
            // Resolve even a potential no-op against the CURRENT on-disk
            // profile while holding the cross-process lease. A pre-lock
            // early return could miss another window's recently changed
            // choice, or accept a stale/corrupted profile as trusted.
            // Update itself skips the atomic write for canonical no-ops.
            UserFeatureProfile saved = UserFeatureProfileService.Update(
                current => ReplacePreferenceKeys(current.KeepFeatures, key, preference));
            BackgroundAppPreference actual = GetPreference(saved, key);
            if (actual != preference)
                throw new InvalidOperationException(
                    $"Background preference read-back nesouhlasí pro {key}. Požadováno={preference}, skutečnost={actual}.");
        }
    }

    // Retained for existing source-level compatibility checks. Production
    // changes now decide no-ops inside UserFeatureProfileService.Update,
    // AFTER the cross-process lease has been acquired.
    private static bool IsCanonicalPreferenceNoOp(
        UserFeatureProfile profile, string[] updated,
        string key, BackgroundAppPreference requested) =>
        !profile.IsUntrusted &&
        profile.KeepFeatures is { } existing &&
        existing.AsSpan().SequenceEqual(updated) &&
        GetPreference(profile, key) == requested;

    // Pure transform: preserve all unrelated settings and avoid duplicate keys.
    internal static string[] ReplacePreferenceKeys(IEnumerable<string> features,
        string key, BackgroundAppPreference preference)
    {
        string keepKey = PreferenceKey(key, true);
        string unusedKey = PreferenceKey(key, false);
        // Persisted user profile features are already a private string[].
        // Count retained entries and allocate the final array only ONCE
        // rather than a List backing array followed by ToArray. Keep the
        // streaming List fallback for unknown/lazy IEnumerable producers:
        // never enumerate them twice or assume their values are stable.
        if (features is string[] keys)
        {
            int keepCount = 0;
            foreach (string existing in keys)
                if (!existing.Equals(keepKey, StringComparison.OrdinalIgnoreCase) &&
                    !existing.Equals(unusedKey, StringComparison.OrdinalIgnoreCase))
                    keepCount++;
            bool append = preference is BackgroundAppPreference.Keep or
                BackgroundAppPreference.Unused;
            var result = new string[keepCount + (append ? 1 : 0)];
            int index = 0;
            foreach (string existing in keys)
                if (!existing.Equals(keepKey, StringComparison.OrdinalIgnoreCase) &&
                    !existing.Equals(unusedKey, StringComparison.OrdinalIgnoreCase))
                    result[index++] = existing;
            if (preference == BackgroundAppPreference.Keep) result[index] = keepKey;
            else if (preference == BackgroundAppPreference.Unused)
                result[index] = unusedKey;
            return result;
        }
        var updated = new List<string>();
        foreach (string existing in features)
            if (!existing.Equals(keepKey, StringComparison.OrdinalIgnoreCase) &&
                !existing.Equals(unusedKey, StringComparison.OrdinalIgnoreCase))
                updated.Add(existing);
        if (preference == BackgroundAppPreference.Keep) updated.Add(keepKey);
        else if (preference == BackgroundAppPreference.Unused) updated.Add(unusedKey);
        return updated.ToArray();
    }

    internal static IReadOnlyList<BackgroundActivityState> ApplyCurrentPreferences(
        IReadOnlyList<BackgroundActivityState> states) =>
        ProjectCurrentPreferences(states, UserFeatureProfileService.Load());

    // Preserve a fresh array snapshot for the WinUI redraw, but records
    // with an unchanged preference are immutable and need not be cloned.
    // Only the card whose effective preference changed gets a new record.
    // This also keeps untrusted-profile fallback exactly as before.
    private static BackgroundActivityState[] ProjectCurrentPreferences(
        IReadOnlyList<BackgroundActivityState> states, UserFeatureProfile profile)
    {
        var updated = new BackgroundActivityState[states.Count];
        ulong flags = ReadPreferenceFlags(profile);
        for (int i = 0; i < states.Count; i++)
        {
            BackgroundActivityState previous = states[i];
            BackgroundAppPreference preference =
                GetPreference(flags, profile, previous.Key);
            updated[i] = preference == previous.Preference
                ? previous : previous with { Preference = preference };
        }
        return updated;
    }

    // Both manual user actions reuse the existing 17-definition static
    // dictionary. Preserve the old ArgumentException for unknown/null keys.
    private static Def ResolveDefinition(string key)
    {
        if (key is null || !DefinitionsByKey.TryGetValue(key, out Def? def))
            throw new ArgumentException("Unknown background app key.", nameof(key));
        return def;
    }

    // Retain one stable snapshot of selected rows until a manual async
    // startup batch finishes. No native read, mutation or extra ToArray.
    private static List<StartupEntry> SelectToggleableStartupMatches(
        Def def, IReadOnlyList<StartupEntry> startup)
    {
        var matches = new List<StartupEntry>();
        foreach (StartupEntry row in startup)
            if (row.Enabled && row.CanToggle && MatchesStartup(row, def))
                matches.Add(row);
        return matches;
    }

    // A displayed StartupEntry can be minutes old by the time the user
    // confirms a manual batch. Compare against a fresh NATIVE inventory
    // BEFORE its first write; a replaced command, a missing item or a
    // duplicate identity must fail closed (even for scheduled tasks/folders).
    private static bool SameStartupLocator(StartupEntry expected, StartupEntry current) =>
        string.Equals(expected.Id, current.Id, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.Name, current.Name, StringComparison.OrdinalIgnoreCase) &&
        expected.Hive == current.Hive &&
        expected.View == current.View &&
        string.Equals(expected.RunPath, current.RunPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.SourceKind, current.SourceKind, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.SourceId, current.SourceId, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnchangedToggleableStartup(
        Def def, StartupEntry expected, StartupEntry current) =>
        SameStartupLocator(expected, current) &&
        expected.Enabled && current.Enabled &&
        expected.CanToggle && current.CanToggle &&
        string.Equals(expected.Command, current.Command, StringComparison.Ordinal) &&
        string.Equals(expected.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase) &&
        MatchesStartup(current, def);

    private static bool HasExactFreshStartupBatch(
        Def def, IReadOnlyList<StartupEntry> selected,
        IReadOnlyList<StartupEntry> fresh)
    {
        for (int i = 0; i < selected.Count; i++)
        {
            StartupEntry expected = selected[i];
            // Never schedule the same target twice, even if the displayed
            // inventory was corrupted or contained accidental duplicates.
            for (int previous = 0; previous < i; previous++)
                if (SameStartupLocator(expected, selected[previous]))
                    return false;
            int exactLocators = 0;
            foreach (StartupEntry actual in fresh)
            {
                if (!SameStartupLocator(expected, actual)) continue;
                if (++exactLocators > 1 ||
                    !IsUnchangedToggleableStartup(def, expected, actual))
                    return false;
            }
            if (exactLocators != 1) return false;
        }
        return true;
    }

    public static async Task<int> DisableStartupMatchesAsync(
        string key,
        IReadOnlyList<StartupEntry> startup,
        CancellationToken token = default)
    {
        Def def = ResolveDefinition(key);
        token.ThrowIfCancellationRequested();
        // Freeze matching identities BEFORE the first async native write,
        // just as the previous Where(...).ToArray did, but avoid its LINQ
        // iterator and an extra list-to-array allocation.
        List<StartupEntry> matches = SelectToggleableStartupMatches(def, startup);
        if (matches.Count == 0) return 0;

        // This is a manual confirmed operation, NOT an AI periodic tick.
        // Force-refresh Task Scheduler as well as registry/folder rows so a
        // cached startup page cannot disable an externally changed target.
        IReadOnlyList<StartupEntry> live = await StartupManagerService
            .GetAsync(forceRefresh: true, forceTaskSchedulerRefresh: true)
            .WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!HasExactFreshStartupBatch(def, matches, live))
            throw new InvalidOperationException(
                "Startup položky se mezitím změnily. Obnov seznam a akci znovu potvrď.");

        int changed = 0;
        try
        {
            foreach (var item in matches)
            {
                token.ThrowIfCancellationRequested();
                await StartupManagerService.SetEnabledAsync(item, false, token).ConfigureAwait(false);
                changed++;
            }
        }
        finally
        {
            // A cancelled or failed batch can still have disabled earlier entries.
            if (changed > 0)
                StartupManagerService.InvalidateCache(invalidateTaskScheduler: false);
        }
        LogService.Info("startup", $"BACKGROUND_STARTUP_DISABLED key={key} count={changed}");
        return changed;
    }

    public static Task<int> StopProcessesAsync(string key, CancellationToken token = default) =>
        Task.Run(() =>
        {
            Def def = ResolveDefinition(key);
            token.ThrowIfCancellationRequested();
            int stopped = 0;
            foreach (string processName in def.Processes)
            {
                token.ThrowIfCancellationRequested();
                stopped += ProcessSnapshotCleanup.Inspect(
                    () => Process.GetProcessesByName(processName),
                    processes =>
                    {
                        int confirmed = 0;
                        foreach (Process process in processes)
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                process.Kill(entireProcessTree: false);
                                bool exited = process.HasExited;
                                long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2.5);
                                while (!exited && Stopwatch.GetTimestamp() < deadline)
                                {
                                    token.ThrowIfCancellationRequested();
                                    exited = process.WaitForExit(100);
                                }
                                if (exited && process.HasExited)
                                {
                                    confirmed++;
                                }
                                else
                                {
                                    LogService.Info("startup",
                                        $"BACKGROUND_PROCESS_EXIT_UNCONFIRMED name={processName} pid={process.Id}");
                                }
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                            {
                                LogService.Error("startup", $"Background process stop failed: {processName}", ex);
                            }
                        }
                        return confirmed;
                    });
            }
            LogService.Info("startup", $"BACKGROUND_PROCESSES_STOPPED key={key} count={stopped}");
            return stopped;
        }, token);

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
