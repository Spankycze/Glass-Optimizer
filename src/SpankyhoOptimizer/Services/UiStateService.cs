using System;
using System.IO;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

public sealed record UiState(string Section = "overview", string? Sub = null, string ViewMode = "Manual", double ScrollOffset = 0, string? Language = null);

public static class UiStateService
{
    private static string StateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "State");
    private static string FileName => Path.Combine(StateRoot, "ui-state.json");
    private static string LegacyFileName => Path.Combine(LogService.Root, "ui-state.json");

    public static UiState Load()
    {
        EnsureMigrated();
        return SelfHealingJsonService.ReadOrDefault(FileName, () => new UiState(), "ui-state");
    }

    public static void Save(UiState state)
    {
        try
        {
            Directory.CreateDirectory(StateRoot);
            AtomicFileService.WriteText(FileName, JsonSerializer.Serialize(state));
        }
        catch (System.Exception ex)
        {
            LogService.Error("ui-state", "UI state save failed", ex);
        }
    }

    private static void EnsureMigrated()
    {
        try
        {
            if (File.Exists(FileName) || !File.Exists(LegacyFileName)) return;
            Directory.CreateDirectory(StateRoot);
            File.Copy(LegacyFileName, FileName, overwrite: false);
            File.Delete(LegacyFileName);
            LogService.Info("ui-state", "Migrated UI state to user-scoped LocalAppData.");
        }
        catch (Exception ex) { LogService.Error("ui-state", "UI state migration failed", ex); }
    }
}
