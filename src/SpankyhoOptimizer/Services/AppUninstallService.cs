using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record AppUninstallResult(bool Success, string Message, IReadOnlyList<string> RemovedPaths);

public static class AppUninstallService
{
    private static readonly Dictionary<string, string[]> CleanupMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Discord"] = new[]
        {
            @"%APPDATA%\discord",
            @"%LOCALAPPDATA%\Discord"
        },
        ["Steam"] = new[]
        {
            @"%LOCALAPPDATA%\Steam",
            @"%APPDATA%\Steam"
        },
        ["Battle.net"] = new[]
        {
            @"%APPDATA%\Battle.net",
            @"%LOCALAPPDATA%\Battle.net",
            @"%PROGRAMDATA%\Battle.net",
            @"%PROGRAMDATA%\Blizzard Entertainment"
        },
        ["Epic Games Launcher"] = new[]
        {
            @"%LOCALAPPDATA%\EpicGamesLauncher",
            @"%PROGRAMDATA%\Epic"
        },
        ["Ubisoft Connect"] = new[]
        {
            @"%LOCALAPPDATA%\Ubisoft Game Launcher",
            @"%PROGRAMDATA%\Ubisoft"
        }
    };

    internal static string RunCleanupIdentitySmoke()
    {
        string[] unrelated = { "SteamVR", "Steam Client Service", "Discord Canary",
            "Discord PTB", "BetterDiscord", "Battle.net Helper",
            "Epic Games Launcher Tools", "Ubisoft Connect Beta" };
        string[] supported = { "Steam", "Discord", "Battle.net",
            "Epic Games Launcher", "Ubisoft Connect" };

        foreach (string name in unrelated)
        {
            var item = new DetectedDesktopApp(name, "Installed", null, "Uninstall registry");
            if (AppDetectionService.IsSupportedAppName(name) ||
                GetCleanupPaths(item).Any())
                throw new InvalidOperationException(
                    "Unrelated installed app was mistaken for a cleanup target: " + name);
        }

        foreach (string name in supported)
        {
            var item = new DetectedDesktopApp(name, "Installed", null, "Uninstall registry");
            if (!AppDetectionService.IsSupportedAppName(name) ||
                !GetCleanupPaths(item).Any())
                throw new InvalidOperationException(
                    "Canonical app lost its known cleanup profile: " + name);
        }

        var discord = new DetectedDesktopApp("Discord", "Communication", null, "Known path");
        if (GetCleanupPaths(discord).Any(path =>
            string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                "SquirrelTemp", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Global SquirrelTemp must not be deleted for Discord.");

        return "APP_UNINSTALL_IDENTITY_SMOKE_OK canonical=5 unrelatedRejected=8 " +
               "sharedSquirrelTempPreserved=true realUninstalls=0 realDeletes=0";
    }

    public static async Task<AppUninstallResult> UninstallWithCleanupAsync(DetectedDesktopApp app, CancellationToken token = default)
    {
        // Fail closed at the mutation boundary, even if callers bypass the
        // curated detection list. Never launch WinGet for a lookalike product.
        if (!AppDetectionService.IsSupportedAppName(app.Name))
            return new AppUninstallResult(false,
                "Aplikace není jednoznačně rozpoznaná pro bezpečnou odinstalaci a úklid.",
                Array.Empty<string>());

        string safeName = app.Name.Trim();
        if (safeName.Length == 0)
            return new AppUninstallResult(false, "Chybí název aplikace.", Array.Empty<string>());

        var result = await UnelevatedProcessRunner.RunAsync(
            "winget.exe",
            $"uninstall --name \"{safeName}\" --exact --silent --accept-source-agreements --disable-interactivity",
            TimeSpan.FromMinutes(20),
            token).ConfigureAwait(false);

        if (!result.Success)
        {
            string detail = (result.StdErr + result.StdOut).Trim();
            if (detail.Length > 500) detail = detail[..500];
            return new AppUninstallResult(false,
                string.IsNullOrWhiteSpace(detail) ? $"Odinstalace {safeName} selhala." : detail,
                Array.Empty<string>());
        }

        var removed = new List<string>();
        foreach (string path in GetCleanupPaths(app))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    removed.Add(path);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    removed.Add(path);
                }
            }
            catch (Exception ex)
            {
                LogService.Error("apps", "Cleanup zbytku selhal: " + path, ex);
            }
        }

        AppDetectionService.Invalidate();
        WingetUpdateService.InvalidateStatus();
        return new AppUninstallResult(true,
            removed.Count == 0
                ? $"{safeName} byl odinstalován. Nebyly nalezeny další bezpečně známé zbytky."
                : $"{safeName} byl odinstalován a vyčištěno {removed.Count} známých zbytkových umístění.",
            removed);
    }

    private static IEnumerable<string> GetCleanupPaths(DetectedDesktopApp app)
    {
        foreach (var pair in CleanupMap)
        {
            if (!string.Equals(app.Name.Trim(), pair.Key, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (string raw in pair.Value)
            {
                string expanded = Environment.ExpandEnvironmentVariables(raw);
                if (IsSafeCleanupPath(expanded))
                    yield return expanded;
            }
            yield break;
        }
    }

    private static bool IsSafeCleanupPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }

        string[] allowedRoots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        return allowedRoots.Any(root =>
            !string.IsNullOrWhiteSpace(root) &&
            full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }
}
