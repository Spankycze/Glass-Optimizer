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
    private sealed record Def(string Key, string Label, string[] Processes, string[] MatchTokens);

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
        new("xbox", "Xbox app", new[] { "XboxPcApp" }, new[] { "xboxpcapp", "xbox app" })
    };

    public static Task<IReadOnlyList<BackgroundActivityState>> ReadAsync(
        IReadOnlyList<StartupEntry> startup,
        CancellationToken token = default) =>
        Task.Run<IReadOnlyList<BackgroundActivityState>>(() => ReadCore(startup, token), token);

    public static void SetPreference(string key, BackgroundAppPreference preference)
    {
        string keepKey = PreferenceKey(key, true);
        string unusedKey = PreferenceKey(key, false);
        var existing = UserFeatureProfileService.Load().KeepFeatures
            .Where(x => !x.Equals(keepKey, StringComparison.OrdinalIgnoreCase) &&
                        !x.Equals(unusedKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (preference == BackgroundAppPreference.Keep) existing.Add(keepKey);
        else if (preference == BackgroundAppPreference.Unused) existing.Add(unusedKey);

        UserFeatureProfileService.Save(existing);
    }

    public static async Task<int> DisableStartupMatchesAsync(
        string key,
        IReadOnlyList<StartupEntry> startup,
        CancellationToken token = default)
    {
        var def = Definitions.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Unknown background app key.", nameof(key));
        var matches = startup
            .Where(x => x.Enabled && x.CanToggle && MatchesStartup(x, def))
            .ToArray();

        int changed = 0;
        foreach (var item in matches)
        {
            token.ThrowIfCancellationRequested();
            await StartupManagerService.SetEnabledAsync(item, false).WaitAsync(token).ConfigureAwait(false);
            changed++;
        }
        StartupManagerService.InvalidateCache(invalidateTaskScheduler: false);
        LogService.Info("startup", $"BACKGROUND_STARTUP_DISABLED key={key} count={changed}");
        return changed;
    }

    public static Task<int> StopProcessesAsync(string key, CancellationToken token = default) =>
        Task.Run(() =>
        {
            var def = Definitions.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Unknown background app key.", nameof(key));
            int stopped = 0;
            foreach (string processName in def.Processes)
            {
                token.ThrowIfCancellationRequested();
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        process.Kill(entireProcessTree: false);
                        stopped++;
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("startup", $"Background process stop failed: {processName}", ex);
                    }
                    finally { process.Dispose(); }
                }
            }
            LogService.Info("startup", $"BACKGROUND_PROCESSES_STOPPED key={key} count={stopped}");
            return stopped;
        }, token);

    private static BackgroundActivityState[] ReadCore(
        IReadOnlyList<StartupEntry> startup,
        CancellationToken token)
    {
        var profile = UserFeatureProfileService.Load();
        var result = new List<BackgroundActivityState>(Definitions.Length);

        foreach (var def in Definitions)
        {
            token.ThrowIfCancellationRequested();
            int count = 0;
            long workingSet = 0;
            double cpuSeconds = 0;

            foreach (string processName in def.Processes)
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        count++;
                        workingSet += Math.Max(0, process.WorkingSet64);
                        cpuSeconds += Math.Max(0, process.TotalProcessorTime.TotalSeconds);
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }

            var startupMatches = startup.Where(x => MatchesStartup(x, def)).ToArray();
            var preference = GetPreference(profile, def.Key);
            result.Add(new BackgroundActivityState(
                def.Key,
                def.Label,
                def.Processes,
                count,
                workingSet / 1048576d,
                cpuSeconds,
                preference,
                startupMatches.Count(x => x.Enabled),
                startupMatches.Length));
        }

        return result
            .Where(x => x.ProcessCount > 0 || x.TotalStartupMatches > 0 || x.Preference != BackgroundAppPreference.Unknown)
            .OrderByDescending(x => x.WorkingSetMb)
            .ThenBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool MatchesStartup(StartupEntry item, Def def)
    {
        string haystack = string.Join(" ",
            item.Name ?? string.Empty,
            item.Command ?? string.Empty,
            item.FilePath ?? string.Empty,
            item.Publisher ?? string.Empty);
        return def.MatchTokens.Any(token =>
            haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static BackgroundAppPreference GetPreference(UserFeatureProfile profile, string key)
    {
        if (profile.Keeps(PreferenceKey(key, true))) return BackgroundAppPreference.Keep;
        if (profile.Keeps(PreferenceKey(key, false))) return BackgroundAppPreference.Unused;
        return BackgroundAppPreference.Unknown;
    }

    private static string PreferenceKey(string key, bool keep) =>
        $"bg:{key}:{(keep ? "keep" : "unused")}";
}
