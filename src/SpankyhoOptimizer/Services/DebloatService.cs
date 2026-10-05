using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Management.Deployment;

namespace SpankyhoOptimizer.Services;

public sealed record DebloatResult(InstalledAppInfo App, bool Success, string Message, TimeSpan Duration);

public static class DebloatService
{
    public static async Task<DebloatResult> RemoveAsync(InstalledAppInfo app, CancellationToken token = default, bool allUsers = false)
    {
        if (app.Protected || app.Risk == DebloatRisk.Protected)
            return new DebloatResult(app, false, "Balík je chráněný a Debloater ho neodebere.", TimeSpan.Zero);

        string id = ErrorService.NewCorrelationId();
        var started = System.Diagnostics.Stopwatch.StartNew();
        await OperationJournalService.WriteAsync(id, "debloat", "remove", app.PackageFullName, "Started", app.DisplayName, token);
        try
        {
            await WorkerClientService.RunAsync<WorkerAck>("debloat-remove",
                WorkerPayloads.Encode(new DebloatPackageRequest(app.PackageFullName, allUsers)), TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
            InstalledAppService.InvalidateCache();
            bool stillInstalled = await InstalledAppService.IsPackageInstalledAsync(app.PackageFullName, token);
            if (stillInstalled) throw new InvalidOperationException("Read-back potvrdil, že balík je stále nainstalovaný.");
            try { await GTweakDebloatPostCleanupService.ApplyAsync(app, CancellationToken.None); }
            catch (Exception cleanupEx) { LogService.Error("debloat", $"GTweak post-remove cleanup failed: {app.PackageName}", cleanupEx); }
            started.Stop();
            await OperationJournalService.WriteAsync(id, "debloat", "remove", app.PackageFullName, "Verified",
                $"{started.Elapsed.TotalSeconds:0.0}s", token);
            return new DebloatResult(app, true, "Odebráno a ověřeno read-backem.", started.Elapsed);
        }
        catch (Exception ex)
        {
            started.Stop();
            LogService.Error("debloat", $"Remove failed {app.PackageFullName} id={id}", ex);
            try { await OperationJournalService.WriteAsync(id, "debloat", "remove", app.PackageFullName, "Failed", ex.Message, CancellationToken.None); }
            catch (Exception journalEx) { LogService.Error("debloat", "Operation journal write failed after remove error", journalEx); }
            return new DebloatResult(app, false, ex.Message, started.Elapsed);
        }
    }
}
