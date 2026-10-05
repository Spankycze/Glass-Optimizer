using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace SpankyhoOptimizer.Services;

public sealed record UserFeatureProfile(string[] KeepFeatures, DateTime UpdatedUtc)
{
    public bool Keeps(string key) => KeepFeatures.Contains(key, StringComparer.OrdinalIgnoreCase);
}
public static class UserFeatureProfileService
{
    private static string StateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "State");
    private static string FilePath => Path.Combine(StateRoot, "user-feature-profile.json");
    private static string LegacyFilePath => Path.Combine(LogService.Root, "user-feature-profile.json");
    private static readonly string[] Defaults = { "updates", "notifications", "store" };
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static UserFeatureProfile? Cache;

    public static UserFeatureProfile Load()
    {
        lock (Gate)
        {
            if (Cache != null) return Cache;
            EnsureMigrated();
            try
            {
                if (File.Exists(FilePath))
                    return Cache = JsonSerializer.Deserialize<UserFeatureProfile>(File.ReadAllText(FilePath))
                        ?? new(Defaults, DateTime.UtcNow);
            }
            catch (Exception ex) { LogService.Error("profile", "Feature profile load failed", ex); }
            return Cache = new(Defaults, DateTime.UtcNow);
        }
    }
    public static void Save(IEnumerable<string> features)
    {
        var value = new UserFeatureProfile(features.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(), DateTime.UtcNow);
        Directory.CreateDirectory(StateRoot);
        AtomicFileService.WriteText(FilePath, JsonSerializer.Serialize(value, IndentedJson));
        lock (Gate) Cache = value;
    }

    private static void EnsureMigrated()
    {
        try
        {
            if (File.Exists(FilePath) || !File.Exists(LegacyFilePath)) return;
            Directory.CreateDirectory(StateRoot);
            File.Copy(LegacyFilePath, FilePath, overwrite: false);
            File.Delete(LegacyFilePath);
            LogService.Info("profile", "Migrated user feature profile to LocalAppData.");
        }
        catch (Exception ex) { LogService.Error("profile", "Feature profile migration failed", ex); }
    }
}
