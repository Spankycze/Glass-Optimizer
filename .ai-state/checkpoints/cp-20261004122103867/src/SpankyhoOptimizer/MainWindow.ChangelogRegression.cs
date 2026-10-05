using System;
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
            refs.Os, refs.Board, refs.Power, refs.Network, refs.Driver, refs.History, refs.Windows, CancellationToken.None);
        await FlushOverviewUiForSmokeAsync();
        if (!versions.SequenceEqual(_overviewHistoryTimes.Select(x => x.Text)) ||
            !details.SequenceEqual(_overviewHistoryRows.Select(x => x.Text)))
            throw new InvalidOperationException("CHANGELOG_REFRESH_REGRESSION: hardware/tweak refresh overwrote release history.");
        LogService.Info("visual-qa", "CHANGELOG_REFRESH_SMOKE_OK snapshotRefresh=true releaseRowsStable=true");
    }
}
