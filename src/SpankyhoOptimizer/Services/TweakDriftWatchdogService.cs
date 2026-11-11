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
public sealed record TweakDriftReport(
    DateTimeOffset CheckedUtc, int WindowsBuild, int Checked,
    TweakDriftItem[] Drifted, int Failed = 0, int Unavailable = 0);

public static class TweakDriftWatchdogService
{
    private static string FileName => Path.Combine(LogService.Root, "tweak-drift-watchdog.json");

    private static bool IsVerifiedClean(TweakDriftReport report) =>
        report.Checked > 0 && report.Failed == 0 && report.Unavailable == 0 &&
        report.Drifted.Length == 0;

    internal static string FormatDriftSummary(TweakDriftReport report)
    {
        string checkedText = $"{report.Checked} ověřeno";
        if (report.Failed > 0 || report.Unavailable > 0)
            return $"{report.Drifted.Length} odchylek • {checkedText} • " +
                $"{report.Failed + report.Unavailable} neověřeno";
        if (report.Checked == 0)
            return "Zatím nic neověřeno";
        return $"{report.Drifted.Length} odchylek • {checkedText}";
    }

    internal static string RunDriftIncompleteSmoke()
    {
        var incomplete = new TweakDriftReport(
            DateTimeOffset.UtcNow, 26100, Checked: 1, Drifted: [], Failed: 2);
        var unverified = new TweakDriftReport(
            DateTimeOffset.UtcNow, 26100, Checked: 0, Drifted: [], Unavailable: 2);
        var clean = new TweakDriftReport(DateTimeOffset.UtcNow, 26100, 3, []);
        var drift = new TweakDriftReport(DateTimeOffset.UtcNow, 26100, 3,
            [new TweakDriftItem("fixture.tweak", "Fixture", true, false)]);
        if (IsVerifiedClean(incomplete) || IsVerifiedClean(unverified) ||
            !IsVerifiedClean(clean) || IsVerifiedClean(drift))
            throw new InvalidOperationException(
                "Tweak drift watchdog reports an incomplete or failed scan as verified clean.");
        string json = JsonSerializer.Serialize(incomplete);
        var roundTrip = JsonSerializer.Deserialize<TweakDriftReport>(json);
        if (roundTrip?.Failed != 2 || roundTrip.Checked != 1)
            throw new InvalidOperationException("Incomplete scan counters lost during persistence.");
        if (!FormatDriftSummary(incomplete).Contains("2 neověřeno", StringComparison.Ordinal) ||
            !FormatDriftSummary(unverified).Contains("2 neověřeno", StringComparison.Ordinal) ||
            !FormatDriftSummary(clean).Contains("0 odchylek", StringComparison.Ordinal) ||
            FormatDriftSummary(drift).StartsWith("0 odchylek", StringComparison.Ordinal))
            throw new InvalidOperationException("Update Center displays an incomplete scan as healthy.");
        const string oldJson = "{\"CheckedUtc\":\"2026-10-21T00:00:00Z\",\"WindowsBuild\":26100,\"Checked\":3,\"Drifted\":[]}";
        var legacy = JsonSerializer.Deserialize<TweakDriftReport>(oldJson);
        if (legacy == null || legacy.Failed != 0 || legacy.Unavailable != 0 ||
            legacy.Checked != 3)
            throw new InvalidOperationException("Old persisted drift report is not compatible.");
        var none = new TweakDriftReport(DateTimeOffset.UtcNow, 26100, 0, []);
        if (IsVerifiedClean(none) || !FormatDriftSummary(none).Contains("nic neověřeno", StringComparison.Ordinal))
            throw new InvalidOperationException("Zero observations were presented as verified clean.");
        return "TWEAK_DRIFT_INCOMPLETE_SMOKE_OK failedScanNotClean=true " +
            "unavailableScanNotClean=true verifiedClean=true driftNotClean=true " +
            "uiIncompleteVisible=true oldJsonReadable=true failureCountersPersist=true systemWrites=0";
    }

    public static async Task<TweakDriftReport> CheckAppliedTweaksAsync(CancellationToken token = default)
    {
        var expected = await TweakBatchHistoryService.ReadLatestExpectedStatesAsync(80, token).ConfigureAwait(false);
        var definitions = SystemStateSnapshotService.BuildAllTweaks()
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var drifted = new List<TweakDriftItem>();
        int checkedCount = 0;
        int failed = 0;
        int unavailable = 0;

        foreach (var pair in expected)
        {
            token.ThrowIfCancellationRequested();
            if (!definitions.TryGetValue(pair.Key, out TweakDefinition? tweak))
            {
                unavailable++;
                continue;
            }
            try
            {
                bool actual = await tweak.DetectAsync(token).ConfigureAwait(false);
                checkedCount++;
                if (actual != pair.Value)
                    drifted.Add(new(pair.Key, tweak.Name, pair.Value, actual));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw; // A cancelled scan must not persist a partial "healthy" report.
            }
            catch (Exception ex)
            {
                failed++;
                LogService.Error("drift", $"Drift check failed id={pair.Key}", ex);
            }
        }

        token.ThrowIfCancellationRequested();
        var report = new TweakDriftReport(DateTimeOffset.UtcNow, Environment.OSVersion.Version.Build,
            checkedCount, drifted.ToArray(), failed, unavailable);
        try
        {
            Directory.CreateDirectory(LogService.Root);
            await AtomicFileService.WriteTextAsync(FileName, JsonSerializer.Serialize(report), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { LogService.Error("drift", "Drift report persist failed", ex); }

        if (report.Failed > 0 || report.Unavailable > 0 || report.Checked == 0)
            LogService.Info("drift",
                $"TWEAK_DRIFT_WATCHDOG_INCOMPLETE checked={report.Checked} failed={report.Failed} unavailable={report.Unavailable} drifted={report.Drifted.Length}");
        else if (IsVerifiedClean(report))
            LogService.Info("drift", $"TWEAK_DRIFT_WATCHDOG_OK checked={report.Checked} drifted=0 build={report.WindowsBuild}");
        else
            LogService.Info("drift", $"TWEAK_DRIFT_WATCHDOG_FOUND checked={report.Checked} drifted={report.Drifted.Length} ids={string.Join(",", report.Drifted.Select(x => x.Id))}");

        return report;
    }

    public static TweakDriftReport? LoadLast() =>
        SelfHealingJsonService.ReadOrDefault<TweakDriftReport?>(FileName, () => null, "tweak-drift-watchdog");
}
