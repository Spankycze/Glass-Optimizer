using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

// Index configured launcher process names once, then inspect a single native
// process snapshot rather than running GetProcessesByName for every executable.
internal static class LauncherProcessMatcher
{
    internal static Dictionary<string, List<string>> BuildIndex(
        IEnumerable<string> selected,
        IReadOnlyDictionary<string, string[]> launchers)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string launcher in selected)
        {
            if (launcher is null || !seen.Add(launcher) || !launchers.TryGetValue(launcher, out string[]? processNames))
                continue;
            foreach (string processName in processNames)
            {
                if (!result.TryGetValue(processName, out List<string>? owners))
                {
                    owners = new List<string>();
                    result.Add(processName, owners);
                }
                owners.Add(launcher);
            }
        }
        return result;
    }

    // Keep the startup matching policy testable without touching real
    // startup entries or changing any running process.
    internal static bool StartupMatches(
        string launcher, IReadOnlyList<string> processNames,
        string entryName, string command)
    {
        // The launcher brand is specific enough in a startup name or command.
        if (ContainsWholeName(entryName, launcher) || ContainsWholeName(command, launcher))
            return true;

        foreach (string processName in processNames)
        {
            // These generic process names belong to numerous unrelated apps.
            // Without the launcher brand, we cannot safely modify startup.
            if (processName.Equals("Agent", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("upc", StringComparison.OrdinalIgnoreCase))
                continue;
            if (ContainsWholeName(command, processName, executableSuffix: true))
                return true;
        }
        return false;
    }

    private static bool ContainsWholeName(
        string text, string name, bool executableSuffix = false)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(name))
            return false;
        static bool IsPart(char value) => char.IsLetterOrDigit(value) ||
            value is '_' or '-' or '.';

        int offset = 0;
        while ((offset = text.IndexOf(name, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = offset + name.Length;
            bool leftBoundary = offset == 0 || !IsPart(text[offset - 1]);
            bool rightBoundary = end == text.Length || !IsPart(text[end]);
            // A full executable name can have the optional .exe suffix.
            if (!rightBoundary && executableSuffix &&
                text.AsSpan(end).StartsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                int afterExe = end + 4;
                rightBoundary = afterExe == text.Length || !IsPart(text[afterExe]);
            }
            // Launcher names in a path (e.g. Steam\\steam.exe) or startup
            // display name are accepted, but not SecurityAgent / Discordant.
            if (leftBoundary && rightBoundary) return true;
            offset++;
        }
        return false;
    }

    internal static string RunStartupMatchingSmoke()
    {
        // All fixtures are synthetic; never enumerate or mutate real startup
        // entries, process priorities, registry values or scheduled tasks.
        var cases = new (string Launcher, string[] Names, string Entry, string Command, bool Match)[]
        {
            ("Battle.net", ["Battle.net", "Agent"], "Battle.net", @"C:\Games\Battle.net\Battle.net.exe", true),
            ("Battle.net", ["Battle.net", "Agent"], "SecurityAgent", @"C:\Tools\SecurityAgent.exe", false),
            ("Battle.net", ["Battle.net", "Agent"], "Backup Agent", @"C:\Tools\Agent.exe", false),
            ("Discord", ["Discord"], "Update", @"C:\User\Discord\Update.exe --processStart Discord.exe", true),
            ("Discord", ["Discord"], "Discordant Helper", @"C:\Tools\Discordant.exe", false),
            ("Steam", ["steamwebhelper"], "Steamroller", @"C:\Tools\steamwebhelper2.exe", false),
            ("Steam", ["steamwebhelper"], "Steam", @"C:\Games\Steam\steam.exe", true),
            ("Epic Games", ["EpicWebHelper"], "Epic Launcher", @"C:\Games\EpicWebHelper.exe", true),
            ("Ubisoft Connect", ["upc", "UbisoftConnect"], "BackupUPC", @"C:\Tools\Backupupc.exe", false)
        };
        int rejected = 0;
        foreach (var item in cases)
        {
            bool actual = StartupMatches(item.Launcher, item.Names, item.Entry, item.Command);
            if (actual != item.Match)
                throw new InvalidOperationException(
                    $"Unsafe launcher startup match: {item.Launcher} entry={item.Entry} actual={actual}");
            if (!actual) rejected++;
        }
        return $"LAUNCHER_STARTUP_MATCH_SMOKE_OK cases={cases.Length} rejected={rejected} startupChanges=0 processChanges=0";
    }
}
