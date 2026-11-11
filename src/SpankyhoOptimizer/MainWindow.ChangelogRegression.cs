using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task FlushOverviewUiForSmokeAsync()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() => ready.TrySetResult(true)))
            throw new InvalidOperationException("UI queue unavailable.");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private string RunChangelogPreviewCachePolicySmoke()
    {
        // Production hot path: four-row preview must not repeatedly parse
        // megabytes of history for every Overview navigation.
        var first = LoadChangelogPreview(4);
        int missesBefore = _changelogPreviewCacheMisses;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 120; i++)
        {
            if (!ReferenceEquals(first, LoadChangelogPreview(4)))
                throw new InvalidOperationException("Changelog preview cache lost its snapshot.");
        }
        watch.Stop();
        long warmBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (_changelogPreviewCacheMisses != missesBefore)
            throw new InvalidOperationException("Changelog steady-state reparsed its full source.");
        // Simulate metadata invalidation in memory without touching the file.
        lock (_changelogPreviewLock) _changelogPreviewStampUtc = DateTime.MinValue;
        var refreshed = LoadChangelogPreview(4);
        if (_changelogPreviewCacheMisses != missesBefore + 1 ||
            ReferenceEquals(first, refreshed) ||
            !first.SequenceEqual(refreshed))
            throw new InvalidOperationException("Changelog preview invalidation failed.");
        return $"CHANGELOG_PREVIEW_CACHE_SMOKE_OK hits=120 misses=0 invalidation=pass" +
            $" warmWallMs={watch.Elapsed.TotalMilliseconds:F3} warmAllocatedBytes={warmBytes}" +
            $" sourceBytes={_changelogPreviewBytes} retainedItems={refreshed.Count}";
    }

    private string RunChangelogPopupIndexCachePolicySmoke()
    {
        var first = BundledChangelogIndex.OpenCached(ChangelogPath);
        for (int i = 0; i < 16; i++)
            if (!ReferenceEquals(first, BundledChangelogIndex.OpenCached(ChangelogPath)))
                throw new InvalidOperationException("Repeated changelog popup reindexed unchanged history.");

        string fixture = Path.Combine(Path.GetTempPath(),
            "spanky-changelog-cache-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            File.WriteAllText(fixture, "## 16.114.95 - Before\n- Old\n");
            var oldIndex = BundledChangelogIndex.OpenCached(fixture);
            if (oldIndex.Sections.Count != 1 ||
                !ReferenceEquals(oldIndex, BundledChangelogIndex.OpenCached(fixture)))
                throw new InvalidOperationException("Synthetic changelog index cache did not retain first page.");
            File.AppendAllText(fixture, "## 16.114.96 - After\n- New\n");
            var changed = BundledChangelogIndex.OpenCached(fixture);
            if (ReferenceEquals(oldIndex, changed) || changed.Sections.Count != 2)
                throw new InvalidOperationException("Changelog index cache did not invalidate on file content change.");
            if (!ReferenceEquals(changed, BundledChangelogIndex.OpenCached(fixture)))
                throw new InvalidOperationException("Refreshed changelog index was not retained.");
        }
        finally
        {
            try { File.Delete(fixture); } catch (IOException) { }
        }
        return "CHANGELOG_POPUP_INDEX_CACHE_SMOKE_OK warmHits=16 fileChangeInvalidates=true historyPreserved=true";
    }

    private async Task ValidateChangelogRefreshSmokeAsync()
    {
        for (int i=0; i<100 && _overviewHistoryRows == null; i++) await Task.Delay(25);
        if (_overviewHistoryRows == null || _overviewHistoryTimes == null || _overviewTextRefs == null)
            throw new InvalidOperationException("Changelog visuals missing.");
        await RefreshOverviewChangelogRowsAsync(CancellationToken.None);
        await FlushOverviewUiForSmokeAsync();
        string[] versions = _overviewHistoryTimes.Select(x => x.Text).ToArray();
        string[] details = _overviewHistoryRows.Select(x => x.Text).ToArray();
        if (!versions.Any(x => x.Contains("15.4.")))
            throw new InvalidOperationException("Changelog preview was not populated.");
        var refs = _overviewTextRefs;
        var snapshot = HardwareSnapshotService.GetOverviewAsync(false, CancellationToken.None);
        await CompleteOverviewDashboardAsync(snapshot, refs.Cpu, refs.Gpu, refs.Ram, refs.Disk,
            refs.Os, refs.Board, refs.Power, refs.Network, refs.Driver, refs.History, refs.Windows, CancellationToken.None, _navigationGeneration);
        await FlushOverviewUiForSmokeAsync();
        if (!versions.SequenceEqual(_overviewHistoryTimes.Select(x => x.Text)) ||
            !details.SequenceEqual(_overviewHistoryRows.Select(x => x.Text)))
            throw new InvalidOperationException("CHANGELOG_REFRESH_REGRESSION: hardware/tweak refresh overwrote release history.");
        LogService.Info("performance", RunChangelogPreviewCachePolicySmoke());
        LogService.Info("visual-qa", "CHANGELOG_REFRESH_SMOKE_OK snapshotRefresh=true releaseRowsStable=true");
    }
}
