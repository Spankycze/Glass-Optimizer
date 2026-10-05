using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record DetectedDesktopApp(string Name, string Category, string? ExePath, string Source);

public static class AppDetectionService
{
    private static readonly object CacheLock = new();
    private static IReadOnlyList<DetectedDesktopApp>? _cache;
    private static DateTime _cacheAt;
    private static Task<IReadOnlyList<DetectedDesktopApp>>? _flight;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly (string Name, string Category, string[] Paths)[] Known =
    {
        ("Firefox","Browser",new[]{@"%ProgramFiles%\Mozilla Firefox\firefox.exe",@"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe"}),
        ("Google Chrome","Browser",new[]{@"%ProgramFiles%\Google\Chrome\Application\chrome.exe",@"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"}),
        ("Microsoft Edge","Browser",new[]{@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"}),
        ("Discord","Communication",new[]{@"%LOCALAPPDATA%\Discord\Update.exe"}),
        ("Steam","Launcher",new[]{@"%ProgramFiles(x86)%\Steam\steam.exe",@"%ProgramFiles%\Steam\steam.exe"}),
        ("Battle.net","Launcher",new[]{@"%ProgramFiles(x86)%\Battle.net\Battle.net Launcher.exe",@"%ProgramFiles%\Battle.net\Battle.net Launcher.exe"}),
        ("Epic Games Launcher","Launcher",new[]{@"%ProgramFiles(x86)%\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"}),
        ("Ubisoft Connect","Launcher",new[]{@"%ProgramFiles(x86)%\Ubisoft\Ubisoft Game Launcher\UbisoftConnect.exe"})
    };

    public static void Invalidate()
    {
        lock (CacheLock)
        {
            _cache = null;
            _cacheAt = default;
        }
    }

    public static async Task<IReadOnlyList<DetectedDesktopApp>> DetectAsync(CancellationToken token = default)
    {
        Task<IReadOnlyList<DetectedDesktopApp>> work;
        lock (CacheLock)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < CacheTtl) return _cache;
            if (_flight != null && !_flight.IsCompleted) work = _flight;
            else
            {
                _flight = work = Task.Run(() => Scan(CancellationToken.None));
                _ = work.ContinueWith(t =>
                {
                    if (t.IsFaulted) _ = t.Exception;
                    lock (CacheLock)
                        if (ReferenceEquals(_flight, work)) _flight = null;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        return await work.WaitAsync(token).ConfigureAwait(false);
    }

    private static IReadOnlyList<DetectedDesktopApp> Scan(CancellationToken token)
    {
        var found = new List<DetectedDesktopApp>();
        foreach (var app in Known)
        {
            token.ThrowIfCancellationRequested();
            string? exe = app.Paths.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists);
            if (exe != null) found.Add(new DetectedDesktopApp(app.Name, app.Category, exe, "Known path"));
        }
        AddFromUninstall(found, RegistryHive.LocalMachine, RegistryView.Registry64, token);
        AddFromUninstall(found, RegistryHive.LocalMachine, RegistryView.Registry32, token);
        AddFromUninstall(found, RegistryHive.CurrentUser, RegistryView.Registry64, token);
        IReadOnlyList<DetectedDesktopApp> result = found.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(x => x.Category).ThenBy(x => x.Name).ToArray();
        lock (CacheLock) { _cache = result; _cacheAt = DateTime.UtcNow; }
        return result;
    }

    private static void AddFromUninstall(List<DetectedDesktopApp> found, RegistryHive hive, RegistryView view, CancellationToken token)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root == null) return;
            foreach (string sub in root.GetSubKeyNames())
            {
                token.ThrowIfCancellationRequested();
                using var key = root.OpenSubKey(sub);
                string name = key?.GetValue("DisplayName")?.ToString() ?? string.Empty;
                if (name.Length == 0 || !Known.Any(k => name.Contains(k.Name.Split(' ')[0], StringComparison.OrdinalIgnoreCase))) continue;
                string? icon = key?.GetValue("DisplayIcon")?.ToString()?.Trim('"').Split(',')[0];
                found.Add(new DetectedDesktopApp(name, "Installed", File.Exists(icon) ? icon : null, "Uninstall registry"));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LogService.Error("apps", $"Uninstall registry scan failed hive={hive} view={view}", ex); }
    }
}
