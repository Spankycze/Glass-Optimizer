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

public static partial class BackgroundActivityService
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
}
