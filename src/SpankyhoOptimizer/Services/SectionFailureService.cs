using System;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed record SectionFailureState(string Section, DateTime StartedUtc, DateTime? CompletedUtc, string? ErrorId);

public static class SectionFailureService
{
    private static string FilePath => Path.Combine(LogService.Root, "section-state.json");

    public static void Begin(string section)
    {
        try
        {
            Directory.CreateDirectory(LogService.Root);
            AtomicFileService.WriteText(FilePath, JsonSerializer.Serialize(new SectionFailureState(section, DateTime.UtcNow, null, null), OptimizerJsonContext.Default.SectionFailureState));
        }
        catch (Exception ex) { LogService.Error("section-state", $"Begin persist failed section={section}", ex); }
    }

    public static void Complete(string section)
    {
        try
        {
            var current = Read();
            if (current?.Section != section) return;
            AtomicFileService.WriteText(FilePath, JsonSerializer.Serialize(current with { CompletedUtc = DateTime.UtcNow, ErrorId = null }, OptimizerJsonContext.Default.SectionFailureState));
        }
        catch (Exception ex) { LogService.Error("section-state", $"Complete persist failed section={section}", ex); }
    }

    public static void Fail(string section, string errorId)
    {
        try
        {
            AtomicFileService.WriteText(FilePath, JsonSerializer.Serialize(new SectionFailureState(section, DateTime.UtcNow, null, errorId), OptimizerJsonContext.Default.SectionFailureState));
        }
        catch (Exception ex) { LogService.Error("section-state", $"Fail persist failed section={section} errorId={errorId}", ex); }
    }

    public static SectionFailureState? ReadPending(TimeSpan? maxAge = null)
    {
        var state = Read();
        if (state == null || state.CompletedUtc.HasValue) return null;
        if (DateTime.UtcNow - state.StartedUtc > (maxAge ?? TimeSpan.FromDays(2))) return null;
        return state;
    }

    private static SectionFailureState? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), OptimizerJsonContext.Default.SectionFailureState);
        }
        catch (Exception ex) { LogService.Error("section-state", "Read failed", ex); return null; }
    }
}
