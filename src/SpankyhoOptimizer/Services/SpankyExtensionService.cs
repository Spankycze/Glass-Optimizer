using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record SpankyPluginManifest(
    string Id,
    string Name,
    string Description,
    string Author,
    string Version,
    int MinBuild,
    int MaxBuild,
    string[] Tweaks);

public static class SpankyExtensionService
{
    public static string PluginRoot => Path.Combine(LogService.Root, "Plugins");
    public static IReadOnlyList<SpankyPluginManifest> LoadPlugins()
    {
        Directory.CreateDirectory(PluginRoot);
        var list = new List<SpankyPluginManifest>();
        foreach (string file in Directory.EnumerateFiles(PluginRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var item = JsonSerializer.Deserialize<SpankyPluginManifest>(File.ReadAllText(file));
                if (item != null && !string.IsNullOrWhiteSpace(item.Id) && item.Tweaks != null) list.Add(item);
            }
            catch (Exception ex) { LogService.Error("Plugin manifest load failed: " + file, ex); }
        }
        return list.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public static async Task<ProcessResult> WingetInstallAsync(string id, CancellationToken token = default)
        => await UnelevatedProcessRunner.RunAsync("winget.exe", $"install --id \"{id.Replace("\"", string.Empty)}\" --exact --accept-source-agreements --accept-package-agreements --disable-interactivity", TimeSpan.FromMinutes(20), token);

    public static async Task<string> WingetUpgradeListAsync(CancellationToken token = default)
    {
        var r = await ProcessRunner.RunAsync("winget.exe", "upgrade --accept-source-agreements", TimeSpan.FromMinutes(2), token);
        return string.IsNullOrWhiteSpace(r.StdOut) ? r.StdErr : r.StdOut;
    }

    public static async Task<ProcessResult> WingetUpgradeAllAsync(CancellationToken token = default)
        => await UnelevatedProcessRunner.RunAsync("winget.exe", "upgrade --all --accept-source-agreements --accept-package-agreements --silent --disable-interactivity", TimeSpan.FromMinutes(40), token);
}
