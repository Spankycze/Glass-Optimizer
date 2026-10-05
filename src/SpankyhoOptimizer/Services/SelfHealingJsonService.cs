using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SpankyhoOptimizer.Services;

public static class SelfHealingJsonService
{
    public static T ReadOrDefault<T>(string path, Func<T> fallback, string area = "state")
    {
        if (!File.Exists(path)) return fallback();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? fallback();
        }
        catch (Exception ex)
        {
            Quarantine(path, area, ex);
            return fallback();
        }
    }

    public static T ReadOrDefault<T>(string path, Func<T> fallback, JsonTypeInfo<T> typeInfo, string area = "state")
    {
        if (!File.Exists(path)) return fallback();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo) ?? fallback();
        }
        catch (Exception ex)
        {
            Quarantine(path, area, ex);
            return fallback();
        }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? LogService.Root);
        AtomicFileService.WriteText(path, JsonSerializer.Serialize(value));
    }
    private static void Quarantine(string path, string area, Exception ex)
    {
        try
        {
            string dir = Path.GetDirectoryName(path) ?? LogService.Root;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string target = Path.Combine(dir, $"{name}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}{ext}");
            File.Move(path, target, true);
            foreach (string old in Directory.GetFiles(dir, name + ".corrupt-*" + ext)
                         .OrderByDescending(File.GetLastWriteTimeUtc).Skip(3))
                try { File.Delete(old); } catch { }
            LogService.Error(area, $"Corrupt state quarantined: {Path.GetFileName(target)}", ex);
        }
        catch (Exception quarantineEx)
        {
            LogService.Error(area, "Corrupt state quarantine failed", quarantineEx);
        }
    }
}
