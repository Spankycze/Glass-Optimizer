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
                // Plugin JSON is untrusted local input: reject pathological files
                // before allocating and parsing their complete contents.
                if (new FileInfo(file).Length > 256 * 1024)
                {
                    LogService.Info("extensions", $"PLUGIN_MANIFEST_SKIPPED size-limit file={Path.GetFileName(file)}");
                    continue;
                }
                if (TryParseManifest(File.ReadAllText(file), out var item) && item != null)
                    list.Add(item);
                else
                    LogService.Info("extensions", $"PLUGIN_MANIFEST_SKIPPED invalid-schema file={Path.GetFileName(file)}");
            }
            catch (Exception ex) { LogService.Error("Plugin manifest load failed: " + file, ex); }
        }
        return list.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private static bool TryParseManifest(string json, out SpankyPluginManifest? item)
    {
        item = JsonSerializer.Deserialize<SpankyPluginManifest>(json);
        if (item == null ||
            string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 128 ||
            string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 128 ||
            item.MinBuild < 0 || item.MaxBuild < item.MinBuild ||
            item.Tweaks is not { Length: > 0 and <= 512 })
            return false;

        // Duplicate/malformed tweak IDs could otherwise be handed to ApplyAsync
        // more than once, or throw during dictionary lookup in Plugin Manager.
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in item.Tweaks)
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 ||
                !string.Equals(id.Trim(), id, StringComparison.Ordinal) ||
                !unique.Add(id))
                return false;
        return true;
    }

    internal static string RunManifestValidationSmoke()
    {
        var valid = new SpankyPluginManifest(
            "valid.plugin", "Valid Plugin", "Built-in tweak collection",
            "Local", "1.0", 19041, 30000,
            new[] { "sys.hyperv", "sys.vmp" });

        static void Check(SpankyPluginManifest source, bool expected, string label)
        {
            bool accepted = TryParseManifest(JsonSerializer.Serialize(source), out _);
            if (accepted != expected)
                throw new InvalidOperationException(
                    $"Plugin manifest {label}: expected accepted={expected}, got {accepted}.");
        }

        Check(valid, true, "valid");
        Check(valid with { Name = string.Empty }, false, "missing-name");
        Check(valid with { Tweaks = new[] { "sys.hyperv", null! } }, false, "null-tweak");
        Check(valid with { Tweaks = new[] { "sys.hyperv", "SYS.HYPERV" } }, false, "duplicate-tweaks");
        Check(valid with { Tweaks = Array.Empty<string>() }, false, "no-tweaks");
        Check(valid with { MinBuild = 30000, MaxBuild = 19041 }, false, "inverted-build");
        Check(valid with { Id = new string('x', 256) }, false, "oversize-id");
        return "PLUGIN_MANIFEST_VALIDATION_SMOKE_OK cases=7 accepted=1 rejected=6";
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
