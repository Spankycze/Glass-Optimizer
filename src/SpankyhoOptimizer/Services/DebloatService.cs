using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Management.Deployment;

namespace SpankyhoOptimizer.Services;

public sealed record DebloatResult(InstalledAppInfo App, bool Success, string Message, TimeSpan Duration,
    bool Unverified = false)
{
    // Successful package read-back must remain distinguishable from a failed
    // final audit write without changing existing positional call sites.
    public bool AuditWarning { get; init; }
}

public static class DebloatService
{
    public static async Task<DebloatResult> RemoveAsync(InstalledAppInfo app, CancellationToken token = default, bool allUsers = false)
    {
        // An already cancelled batch must not even create a Started journal
        // entry or contact a privileged worker for the next package.
        token.ThrowIfCancellationRequested();
        if (app.Protected || app.Risk == DebloatRisk.Protected ||
            InstalledAppService.IsProtectedPackageName(app.PackageName))
            return new DebloatResult(app, false, "Balík je chráněný a Debloater ho neodebere.", TimeSpan.Zero);
        if (string.IsNullOrWhiteSpace(app.PackageName) ||
            string.IsNullOrWhiteSpace(app.PackageFullName) ||
            !app.PackageFullName.StartsWith(app.PackageName + "_", StringComparison.OrdinalIgnoreCase))
            return new DebloatResult(app, false, "Identita balíčku není platná; odebrání zablokováno.", TimeSpan.Zero);

        string id = ErrorService.NewCorrelationId();
        var started = System.Diagnostics.Stopwatch.StartNew();
        string removalScope = allUsers ? "všichni uživatelé" : "aktuální uživatel";
        bool readbackPending = false;
        bool workerPending = false;
        await OperationJournalService.WriteAsync(id, "debloat", "remove", app.PackageFullName, "Started",
            $"{app.DisplayName}; rozsah: {removalScope}", token);
        try
        {
            string workerPayload = WorkerPayloads.Encode(new DebloatPackageRequest(app.PackageFullName, allUsers));
            workerPending = true;
            WorkerAck acknowledgement = await WorkerClientService.RunAsync<WorkerAck>("debloat-remove",
                workerPayload, TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
            if (!acknowledgement.Ok)
                throw new InvalidOperationException("Privilegovaný worker nepotvrdil dokončení odebrání.");
            workerPending = false;
            // The acknowledged mutation is uncertain until both cache
            // invalidation and installed-package read-back have completed.
            readbackPending = true;
            InstalledAppService.InvalidateCache();
            // The worker already acknowledged completion. Finish a bounded
            // read-back even if the user cancels the remaining batch now.
            // Otherwise a late Cancel would erase a verifiable result.
            // Bound both the UI wait and cooperative enumeration work. This
            // cancellation is internal to read-back, never the user's late
            // cancellation of remaining batch operations.
            using var readbackDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            bool stillInstalled = await InstalledAppService.IsPackageInstalledAsync(
                app.PackageFullName, readbackDeadline.Token, allUsers)
                .WaitAsync(TimeSpan.FromSeconds(15));
            readbackPending = false;
            if (stillInstalled)
                throw new InvalidOperationException(allUsers
                    ? "Read-back stále vidí balíček alespoň u jednoho uživatele."
                    : "Read-back stále vidí balíček u aktuálního uživatele.");
            // Removing an explicitly selected Appx package does not authorize
            // unrelated HKLM policy, update-task or file-association changes.
            // Keep optional legacy cleanup code out of this removal path.
            started.Stop();
            try
            {
                // Once removal and read-back succeeded, an audit I/O failure
                // must not report a successfully removed app as "not removed".
                await OperationJournalService.WriteAsync(id, "debloat", "remove", app.PackageFullName, "Verified",
                    $"{started.Elapsed.TotalSeconds:0.0}s; rozsah: {removalScope}", CancellationToken.None);
            }
            catch (Exception auditEx)
            {
                LogService.Error("debloat", $"Verified removal audit failed {app.PackageFullName} id={id}", auditEx);
                return new DebloatResult(app, true,
                    "Balíček odebrán a ověřen; POZOR: nepodařilo se dokončit auditní záznam.", started.Elapsed)
                { AuditWarning = true };
            }
            return new DebloatResult(app, true, "Balíček odebrán a ověřen. Další nastavení Windows beze změny.", started.Elapsed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !readbackPending)
        {
            started.Stop();
            // Only cancellation before the acknowledged read-back phase is
            // an interrupted operation. An independent 15-second read-back
            // deadline must be journaled as Unverified, even when the user
            // also cancelled the remaining batch after worker ACK.
            try
            {
                await OperationJournalService.WriteAsync(id, "debloat", "remove", app.PackageFullName, "Interrupted",
                    $"Rozsah: {removalScope}; výsledek rozpracované operace neznámý.", CancellationToken.None);
            }
            catch (Exception auditEx) { LogService.Error("debloat", "Interrupted removal audit failed", auditEx); }
            throw;
        }
        catch (Exception unknownError) when (workerPending || readbackPending)
        {
            started.Stop();
            // A missing/invalid worker acknowledgement (including IPC
            // timeout) is not proof that a privileged mutation never ran.
            // Likewise, failed read-back cannot prove the resulting state.
            string phase = workerPending ? "odpověď workeru" : "kontrola po odebrání";
            LogService.Error("debloat", $"Unverified removal {app.PackageFullName} id={id}; phase={phase}", unknownError);
            try
            {
                await OperationJournalService.WriteAsync(id, "debloat", "remove", app.PackageFullName,
                    "Unverified", $"Rozsah: {removalScope}; {phase}: {unknownError.GetType().Name}.",
                    CancellationToken.None);
            }
            catch (Exception auditEx) { LogService.Error("debloat", "Unverified removal audit failed", auditEx); }
            return new DebloatResult(app, false,
                $"Výsledek odebrání není ověřen ({phase} selhala). Obnov seznam aplikací; neopakuj odebrání naslepo.",
                started.Elapsed, Unverified: true);
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
