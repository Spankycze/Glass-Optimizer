using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public sealed record TweakDriftItem(string Id, string Name, bool Expected, bool Actual);
public sealed record TweakDriftReport(DateTimeOffset CheckedUtc, int WindowsBuild, int Checked, TweakDriftItem[] Drifted);

public static class TweakDriftWatchdogService
{
    private static string FileName => Path.Combine(LogService.Root, "tweak-drift-watchdog.json");

    public static async Task<TweakDriftReport> CheckAppliedTweaksAsync(CancellationToken token = default)
    {
        var expected = await TweakBatchHistoryService.ReadLatestExpectedStatesAsync(80, token).ConfigureAwait(false);
        var definitions = SystemStateSnapshotService.BuildAllTweaks()
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var drifted = new List<TweakDriftItem>();
        int checkedCount = 0;

        foreach (var pair in expected)
        {
            token.ThrowIfCancellationRequested();
            if (!definitions.TryGetValue(pair.Key, out TweakDefinition? tweak)) continue;
            try
            {
                bool actual = await tweak.DetectAsync(token).ConfigureAwait(false);
                checkedCount++;
                if (actual != pair.Value)
                    drifted.Add(new(pair.Key, tweak.Name, pair.Value, actual));
            }
            catch (Exception ex)
            {
                LogService.Error("drift", $"Drift check failed id={pair.Key}", ex);
            }
        }

        var report = new TweakDriftReport(DateTimeOffset.UtcNow, Environment.OSVersion.Version.Build,
            checkedCount, drifted.ToArray());
        try
        {
            Directory.CreateDirectory(LogService.Root);
            await AtomicFileService.WriteTextAsync(FileName, JsonSerializer.Serialize(report), token).ConfigureAwait(false);
        }
        catch (Exception ex) { LogService.Error("drift", "Drift report persist failed", ex); }

        if (report.Drifted.Length == 0)
            LogService.Info("drift", $"TWEAK_DRIFT_WATCHDOG_OK checked={report.Checked} drifted=0 build={report.WindowsBuild}");
        else
            LogService.Info("drift", $"TWEAK_DRIFT_WATCHDOG_FOUND checked={report.Checked} drifted={report.Drifted.Length} ids={string.Join(",", report.Drifted.Select(x => x.Id))}");

        return report;
    }

    public static TweakDriftReport? LoadLast() =>
        SelfHealingJsonService.ReadOrDefault<TweakDriftReport?>(FileName, () => null, "tweak-drift-watchdog");
}
