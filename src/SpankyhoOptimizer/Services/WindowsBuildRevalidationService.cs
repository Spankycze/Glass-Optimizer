using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record BuildRevalidationState(int PreviousBuild, int CurrentBuild, DateTimeOffset ChangedUtc, bool Pending);

public static class WindowsBuildRevalidationService
{
    private static string FileName => Path.Combine(LogService.Root, "build-revalidation.json");
    private static BuildRevalidationState? _state;
    private static int _revision;
    public static int Revision => System.Threading.Volatile.Read(ref _revision);

    public static BuildRevalidationState CheckAndUpdate()
    {
        int current = Environment.OSVersion.Version.Build;
        var previous = Load();
        if (previous == null)
        {
            _state = new(current, current, DateTimeOffset.UtcNow, false);
            System.Threading.Interlocked.Increment(ref _revision);
            Save(_state);
            return _state;
        }
        if (previous.CurrentBuild != current)
        {
            _state = new(previous.CurrentBuild, current, DateTimeOffset.UtcNow, true);
            System.Threading.Interlocked.Increment(ref _revision);
            Save(_state);
            LogService.Info("compat", $"Windows build changed {previous.CurrentBuild} -> {current}; tweak revalidation required.");
            return _state;
        }
        _state = previous;
        return previous;
    }

    public static bool NeedsRevalidation(TweakDefinition tweak)
    {
        var state = _state ?? Load();
        if (state == null || !state.Pending) return false;
        return tweak.LastVerifiedUtc is not DateTime verified || verified < state.ChangedUtc.UtcDateTime;
    }

    public static void MarkValidatedIfComplete(IEnumerable<TweakDefinition> tweaks)
    {
        var state = _state ?? Load();
        if (state == null || !state.Pending) return;
        if (tweaks.Any(NeedsRevalidation)) return;
        _state = state with { Pending = false };
        System.Threading.Interlocked.Increment(ref _revision);
        Save(_state);
    }

    public static BuildRevalidationState? Current => _state ?? Load();
    private static BuildRevalidationState? Load() =>
        SelfHealingJsonService.ReadOrDefault<BuildRevalidationState?>(FileName, () => null, "build-revalidation");

    private static void Save(BuildRevalidationState state)
    {
        try
        {
            Directory.CreateDirectory(LogService.Root);
            AtomicFileService.WriteText(FileName, JsonSerializer.Serialize(state));
        }
        catch (Exception ex) { LogService.Error("compat", "Build revalidation state persist failed", ex); }
    }
}
