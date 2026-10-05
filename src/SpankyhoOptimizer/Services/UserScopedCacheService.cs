using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpankyhoOptimizer.Services;

internal static class UserScopedCacheService
{
    private sealed record CacheEnvelope<T>(DateTime SavedUtc, T Value);
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "Cache");

    public static bool TryRead<T>(string key, TimeSpan maxAge, out T? value)
        => TryRead(key, maxAge, out value, out _);

    public static bool TryRead<T>(string key, TimeSpan maxAge, out T? value, out TimeSpan age)
    {
        value = default;
        age = TimeSpan.MaxValue;
        try
        {
            string file = PathFor(key);
            if (!File.Exists(file)) return false;
            var envelope = JsonSerializer.Deserialize<CacheEnvelope<T>>(File.ReadAllText(file), JsonOptions);
            if (envelope == null) return false;
            age = DateTime.UtcNow - envelope.SavedUtc;
            if (age > maxAge) return false;
            value = envelope.Value;
            return value != null;
        }
        catch { return false; }
    }

    public static void Write<T>(string key, T value)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(CacheDir);
                AtomicFileService.WriteText(
                    PathFor(key),
                    JsonSerializer.Serialize(new CacheEnvelope<T>(DateTime.UtcNow, value), JsonOptions));
            }
        }
        catch (Exception ex) { LogService.Error("user-cache", "Persistent cache write failed: " + key, ex); }
    }

    public static void Remove(string key)
    {
        try
        {
            lock (Gate)
            {
                string file = PathFor(key);
                if (File.Exists(file)) File.Delete(file);
            }
        }
        catch (Exception ex) { LogService.Error("user-cache", "Persistent cache remove failed: " + key, ex); }
    }

    internal static void BackdateForSmoke(string key, TimeSpan age)
    {
        lock (Gate)
        {
            string file = PathFor(key);
            if (!File.Exists(file))
                throw new FileNotFoundException("User-scoped cache file not found for smoke backdate.", file);

            JsonNode? root = JsonNode.Parse(File.ReadAllText(file));
            if (root is not JsonObject obj)
                throw new InvalidDataException("User-scoped cache envelope is not a JSON object.");

            obj["savedUtc"] = DateTime.UtcNow.Subtract(age);
            AtomicFileService.WriteText(file, obj.ToJsonString(JsonOptions));
        }
    }

    private static string PathFor(string key)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
        return Path.Combine(CacheDir, key + ".json");
    }
}
