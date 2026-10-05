using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record OptimizerConfig(string Version, DateTime CreatedUtc, Dictionary<string, bool> Tweaks);

public static class OptimizerConfigService
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly string[] Browsers = { "Firefox", "Chrome", "Edge", "Brave", "Opera", "Opera GX", "Vivaldi" };
    private static readonly string[] ComponentGroups = { "Služby", "Ovladače", "Task Scheduler" };

    public static TweakDefinition[] GetConfigurableTweaks()
    {
        var list = new List<TweakDefinition>();
        foreach (string section in new[] { "system", "gpu", "memory", "storage", "network", "ui", "update", "privacy", "ai", "defender" })
            list.AddRange(CatalogService.GetTweaks(section));
        foreach (string browser in Browsers) list.AddRange(CatalogService.GetTweaks("browsers", browser));
        foreach (string group in ComponentGroups) list.AddRange(CatalogService.GetTweaks("components", group));
        return list.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToArray();
    }
    public static async Task ExportAsync(string path, CancellationToken token = default)
    {
        var tweaks = GetConfigurableTweaks();
        var engine = new TweakEngine();
        await engine.DetectAsync(tweaks, token);
        var values = tweaks.Where(x => x.IsLoaded).ToDictionary(x => x.Id, x => x.Current, StringComparer.OrdinalIgnoreCase);
        var config = new OptimizerConfig("1.0", DateTime.UtcNow, values);
        var json = JsonSerializer.Serialize(config, IndentedJson);
        await File.WriteAllTextAsync(path, json, token);
    }

    public static async Task<IReadOnlyList<string>> ImportAndApplyAsync(string path, CancellationToken token = default)
    {
        var json = await File.ReadAllTextAsync(path, token);
        var config = JsonSerializer.Deserialize<OptimizerConfig>(json) ?? throw new InvalidDataException("Neplatný config soubor.");
        var tweaks = GetConfigurableTweaks();
        var engine = new TweakEngine();
        await engine.DetectAsync(tweaks, token);
        foreach (var tweak in tweaks)
            if (config.Tweaks.TryGetValue(tweak.Id, out bool desired) && tweak.IsLoaded)
                tweak.Pending = desired;
        return await engine.ApplyAsync(tweaks, token);
    }
}
