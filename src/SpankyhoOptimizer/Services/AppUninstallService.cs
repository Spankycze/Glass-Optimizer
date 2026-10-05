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
        ["Firefox"] = new[]
        {
            @"%APPDATA%\Mozilla\Firefox",
            @"%LOCALAPPDATA%\Mozilla\Firefox",
            @"%PROGRAMDATA%\Mozilla"
        },
        ["Google Chrome"] = new[]
        {
            @"%LOCALAPPDATA%\Google\Chrome",
            @"%PROGRAMDATA%\Google\Chrome"
        },
        ["Discord"] = new[]
        {
            @"%APPDATA%\discord",
            @"%LOCALAPPDATA%\Discord",
            @"%LOCALAPPDATA%\SquirrelTemp"
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

    public static async Task<AppUninstallResult> UninstallWithCleanupAsync(DetectedDesktopApp app, CancellationToken token = default)
    {
        string safeName = (app.Name ?? string.Empty).Replace(((char)34).ToString(), string.Empty).Trim();
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
            if (!app.Name.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
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
