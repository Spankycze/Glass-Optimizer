using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed record SafePresetProfile(
    string[] ExcludedTweakIds,
    DateTime UpdatedUtc)
{
    public bool Allows(string tweakId) =>
        !ExcludedTweakIds.Contains(tweakId, StringComparer.OrdinalIgnoreCase);
}

public static class SafePresetProfileService
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static string FilePath =>
        Path.Combine(LogService.Root, "safe-preset-profile.json");
    private static readonly object Gate = new();
    private static SafePresetProfile? Cache;

    public static SafePresetProfile Load()
    {
        lock (Gate)
        {
            if (Cache != null) return Cache;
            try
            {
                if (!File.Exists(FilePath)) return Cache = new(Array.Empty<string>(), DateTime.UtcNow);
                return Cache = JsonSerializer.Deserialize<SafePresetProfile>(File.ReadAllText(FilePath))
                    ?? new(Array.Empty<string>(), DateTime.UtcNow);
            }
            catch { return Cache = new(Array.Empty<string>(), DateTime.UtcNow); }
        }
    }
    public static void Save(IEnumerable<string> excludedTweakIds)
    {
        var value = new SafePresetProfile(
            excludedTweakIds.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            DateTime.UtcNow);
        Directory.CreateDirectory(LogService.Root);
        AtomicFileService.WriteText(FilePath,
            JsonSerializer.Serialize(value, IndentedJson));
        lock (Gate) Cache = value;
    }

    public static void SetAllowed(string tweakId, bool allowed)
    {
        var profile = Load();
        var values = profile.ExcludedTweakIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowed) values.Remove(tweakId); else values.Add(tweakId);
        Save(values);
    }
}
