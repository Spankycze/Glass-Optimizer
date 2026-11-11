using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class IdlePrewarmService
{
    private static readonly SemaphoreSlim ApplyWarmGate = new(1, 1);
    private static bool _applyInfrastructureWarm;

    public static async Task<long> WarmApplyInfrastructureAsync(CancellationToken token = default)
    {
        if (Volatile.Read(ref _applyInfrastructureWarm)) return 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ApplyWarmGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_applyInfrastructureWarm) return 0;

            Task preflightWarm = Task.Run(TweakPreflightService.Warm, token);
            Task backupWarm = TweakFactory.Backups.WarmAsync(token);
            Task historyWarm = TweakBatchHistoryService.WarmAsync(token);
            Task journalWarm = OperationJournalService.WarmAsync(token);
            await Task.WhenAll(preflightWarm, backupWarm, historyWarm, journalWarm).ConfigureAwait(false);

            Volatile.Write(ref _applyInfrastructureWarm, true);
            LogService.Info("performance", $"APPLY_INFRA_PREWARM workMs={sw.ElapsedMilliseconds}");
            return sw.ElapsedMilliseconds;
        }
        finally
        {
            ApplyWarmGate.Release();
        }
    }

    public static async Task RunAsync(CancellationToken token = default)
    {
        try
        {
            // Hydrate the persisted lightweight app summary early, off the UI thread.
            // This is disk/JSON cache work only; it never triggers a fresh PackageManager scan.
            // Doing it before the heavier generic idle prewarm removes Debloater first-open I/O.
            await Task.Delay(1000, token).ConfigureAwait(false);
            bool startupHydrated = await StartupManagerService.TryWarmDisplaySnapshotAsync(token).ConfigureAwait(false);
            var startupSnapshot = StartupManagerService.TryGetDisplaySnapshot();
            if (startupSnapshot is { Count: > 0 })
                _ = await SystemInsightService.GetStartupInsightsAsync(startupSnapshot, token).ConfigureAwait(false);

            await Task.Delay(200, token).ConfigureAwait(false);
            bool powerHydrated = await PowerPlanStateService.TryPrewarmRegistrySnapshotAsync(token).ConfigureAwait(false);

            await Task.Delay(250, token).ConfigureAwait(false);
            // This continuation already runs off the UI thread
            // (ConfigureAwait(false) above). Avoid allocating/scheduling
            // another ThreadPool task just to hydrate the local disk cache.
            token.ThrowIfCancellationRequested();
            bool appsHydrated = InstalledAppService.TryGetDisplaySnapshot() is not null;

            // Keep heavy inventory providers on-demand, but warm cheap apply infrastructure
            // after the first interactive seconds so the first Apply/Recommended click avoids JIT/file-cache cost.
            await Task.Delay(3500, token).ConfigureAwait(false);
            long applyInfraMs = await WarmApplyInfrastructureAsync(token).ConfigureAwait(false);

            // Task Scheduler COM inventory stays strictly on-demand; prewarming it caused
            // ~200 ms of COM/CPU work shortly after startup with no user interaction.

            foreach (var key in new[] { "system", "privacy", "ui", "network", "storage", "update" })
            {
                token.ThrowIfCancellationRequested();
                _ = CatalogService.GetTweaks(key);
                await Task.Yield();
            }

            LogService.Info("startup", $"IDLE_PREWARM_OK appsHydrated={appsHydrated} startupHydrated={startupHydrated} powerHydrated={powerHydrated} catalogs=true applyInfraMs={applyInfraMs} heavyScans=false");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Error("startup", "Idle prewarm failed", ex); }
    }
}
