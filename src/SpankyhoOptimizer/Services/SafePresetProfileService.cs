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
    // Runtime-only safety status: never write it into a user's JSON file.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUntrusted { get; init; }

    public bool Allows(string tweakId) =>
        !IsUntrusted && ExcludedTweakIds != null &&
        !ExcludedTweakIds.Contains(tweakId, StringComparer.OrdinalIgnoreCase);
}

public static class SafePresetProfileService
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static string FilePath =>
        Path.Combine(LogService.Root, "safe-preset-profile.json");
    private static readonly object Gate = new();
    private static SafePresetProfile? Cache;

    // Profile parsing isolated from the real settings location for safe testing.
    private static SafePresetProfile ReadProfileFromPath(string path)
    {
        try
        {
            // Read directly: File.Exists can return false on access-denied
            // paths, which is NOT the same as a missing first-run profile.
            var parsed = JsonSerializer.Deserialize<SafePresetProfile>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Safe preset was JSON null.");
            if (parsed.ExcludedTweakIds == null ||
                parsed.ExcludedTweakIds.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Safe preset contains invalid exclusion IDs.");
            return parsed;
        }
        catch (FileNotFoundException) { return new(Array.Empty<string>(), DateTime.UtcNow); }
        catch (DirectoryNotFoundException) { return new(Array.Empty<string>(), DateTime.UtcNow); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException
                                   or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogService.Error("safe-preset", "Saved exclusions unavailable: Recommended will not apply changes", ex);
            // Do not lose any explicitly excluded tweak if the saved profile
            // cannot be trusted. Keep the original file intact for recovery.
            return new SafePresetProfile(Array.Empty<string>(), DateTime.UtcNow)
            {
                IsUntrusted = true
            };
        }
    }

    internal static string RunFailClosedPresetSmoke()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "spanky-safe-preset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "safe-preset-profile.json");
            var missing = ReadProfileFromPath(file);
            if (missing.IsUntrusted || missing.ExcludedTweakIds.Length != 0)
                throw new InvalidOperationException("First-run missing preset should use safe defaults.");

            var valid = new SafePresetProfile(new[] { "sys.telemetry", "gpu.hags" }, DateTime.UtcNow);
            File.WriteAllText(file, JsonSerializer.Serialize(valid));
            var retained = ReadProfileFromPath(file);
            if (retained.IsUntrusted || retained.Allows("sys.telemetry") ||
                !retained.ExcludedTweakIds.SequenceEqual(valid.ExcludedTweakIds))
                throw new InvalidOperationException("Valid user exclusions were changed.");

            foreach (string invalid in new[]
            {
                "{not-json",
                "null",
                "{\"ExcludedTweakIds\":null,\"UpdatedUtc\":\"2026-10-21T00:00:00Z\"}",
                "{\"ExcludedTweakIds\":[null],\"UpdatedUtc\":\"2026-10-21T00:00:00Z\"}"
            })
            {
                File.WriteAllText(file, invalid);
                var broken = ReadProfileFromPath(file);
                if (!broken.IsUntrusted ||
                    !RecommendedEngine.MustBlockRecommendations(broken) ||
                    broken.Allows("sys.telemetry") ||
                    File.ReadAllText(file) != invalid)
                    throw new InvalidOperationException(
                        "Invalid saved exclusions silently turned into empty, permissive Recommended rules.");
            }

            File.WriteAllText(file, JsonSerializer.Serialize(valid));
            using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var blocked = ReadProfileFromPath(file);
                if (!blocked.IsUntrusted || !RecommendedEngine.MustBlockRecommendations(blocked))
                    throw new InvalidOperationException(
                        "Temporarily locked exclusions were silently treated as empty.");
            }
            if (ReadProfileFromPath(file).IsUntrusted)
                throw new InvalidOperationException("Readable profile remained blocked.");

            var fakeTweaks = new[]
            {
                new SpankyhoOptimizer.Models.TweakDefinition
                {
                    Id = "sys.keep-on", Section = "system", Group = "fixture",
                    Name = "Keep on", Description = "smoke", Current = true, Recommended = true
                },
                new SpankyhoOptimizer.Models.TweakDefinition
                {
                    Id = "sys.keep-off", Section = "system", Group = "fixture",
                    Name = "Keep off", Description = "smoke", Current = false, Recommended = true
                }
            };
            var blockedPlan = RecommendedEngine.BuildBlockedPresetPlan(fakeTweaks);
            if (blockedPlan.EligibleCount != 0 ||
                blockedPlan.Decisions.Count != 2 ||
                blockedPlan.Decisions.Any(d => d.Eligible || d.Tier != "Blocked") ||
                blockedPlan.Decisions[0].Enable != true ||
                blockedPlan.Decisions[1].Enable != false)
                throw new InvalidOperationException(
                    "Corrupt preset failed to protect existing ON/OFF tweak states.");

            return "SAFE_PRESET_FAIL_CLOSED_SMOKE_OK validExclusionsPreserved=true " +
                   "untrustedInputsRejected=5 missingProfileAllowed=true " +
                   "recommendedCandidates=0 existingStatesPreserved=2 networkCalls=0 userFilesTouched=0";
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    public static SafePresetProfile Load()
    {
        lock (Gate)
        {
            // Retry only while untrusted, so manually repairing an invalid
            // file can restore recommendations without restarting the GUI.
            if (Cache != null && !Cache.IsUntrusted) return Cache;
            return Cache = ReadProfileFromPath(FilePath);
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
        if (profile.IsUntrusted)
            throw new InvalidOperationException(
                "Nelze změnit Recommended výjimky: uložený profil není bezpečně čitelný.");
        var values = profile.ExcludedTweakIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowed) values.Remove(tweakId); else values.Add(tweakId);
        Save(values);
    }
}
