using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed partial class AiSystemResourceManagerService
{
    public static void RestoreProcessPriorityNow()
    {
        var service = new AiSystemResourceManagerService();
        service._processPriority.Tick(false, null, AiWorkloadClass.Unknown, DateTime.UtcNow);
    }

    public static async Task RestoreAllNowAsync(CancellationToken token = default)
    {
        var service = new AiSystemResourceManagerService();
        if (!service._journalLoadHealthy)
            throw new InvalidOperationException(
                "Obnovu nelze bezpečně spustit: záznam vlastnictví AI změn není čitelný.");

        bool journalCommitted = false;
        Exception? coreFailure = null;
        Exception? appFailure = null;
        try
        {
            // Core Windows resources and optional third-party apps have
            // independent ownership journals. Attempt BOTH groups even if
            // one has a recoverable failure; an error in security/power
            // restore must not strand the Firefox or Steam recovery path.
            try
            {
                service._processPriority.Tick(false, null, AiWorkloadClass.Unknown, DateTime.UtcNow);
                await service.RestoreAllAsync(token).ConfigureAwait(false);
                string? searchRestore = service._searchOptimization.Tick(enabled: false);
                if (!string.IsNullOrEmpty(service._searchOptimization.LastError))
                    throw new IOException("Windows Search restore: " +
                        service._searchOptimization.LastError);
                if (searchRestore != null)
                    AppendAction("service", "WSearch", "AI restore", searchRestore);
                string? pnpRestore = await service._unusedPnpDevices.TickAsync(
                    aiAllowed: false, bluetoothOwned: false, token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(service._unusedPnpDevices.LastError))
                    throw new IOException("Device Manager restore: " +
                        service._unusedPnpDevices.LastError);
                if (pnpRestore != null)
                    AppendAction("device", "Intel USB Bluetooth", "AI restore", pnpRestore);
                string? locationRestore = service._locationOptimization.Tick(
                    serviceAutomationEnabled: false);
                if (!string.IsNullOrEmpty(service._locationOptimization.LastError))
                    throw new IOException("Geolocation restore: " +
                        service._locationOptimization.LastError);
                if (locationRestore != null)
                    AppendAction("service", "lfsvc", "AI restore", locationRestore);
                string? bluetoothRestore = service._bluetoothOptimization.Tick(enabled: false);
                if (!string.IsNullOrEmpty(service._bluetoothOptimization.LastError))
                    throw new IOException("Bluetooth restore: " +
                        service._bluetoothOptimization.LastError);
                if (bluetoothRestore != null)
                    AppendAction("service", "bthserv", "AI restore", bluetoothRestore);
                string? maintenanceRestore = service._searchMaintenanceTask.Tick(
                    enabled: false, token);
                if (!string.IsNullOrEmpty(service._searchMaintenanceTask.LastError))
                    throw new IOException("Windows Search task restore: " +
                        service._searchMaintenanceTask.LastError);
                if (maintenanceRestore != null)
                    AppendAction("task",
                        @"\Microsoft\Windows\Shell\IndexerAutomaticMaintenance",
                        "AI restore", maintenanceRestore);
                // Restore owned optional tasks alongside other AI resource
                // scopes; never leave user-disabled AI experiments behind.
                service._optionalTasks.Tick(enabled: false, TaskAllowlist, token);
                if (!string.IsNullOrEmpty(service._optionalTasks.LastError))
                    throw new IOException("Optional task restore: " +
                        service._optionalTasks.LastError);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                coreFailure = ex;
                LogService.Error("ai-system", "Core Restore all partly failed", ex);
            }

            try
            {
                await service._thirdPartyIdle.RestoreOwnedNowAsync(token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                appFailure = ex;
                LogService.Error("ai-system", "Third-party Restore all partly failed", ex);
            }
        }
        finally
        {
            // A partial restore changes ownership records even when a later
            // service/task fails or cancellation arrives. Always commit what
            // was actually restored; keep unresolved entries in the journal.
            journalCommitted = service.PersistItems(unchangedIsSuccess: true);
            service.WriteHealth(LoadConfig(), 0, false, false);
        }

        if (!journalCommitted)
            throw new IOException(
                "Obnova AI nebyla bezpečně potvrzena uloženým záznamem. Další obnova je nutná.");

        int stillManaged = service._items.Values.Count(x => x.Managed);
        bool pendingRecovery =
            AiPendingTrialSelectionPolicy.HasInterruptedOrPendingJournal(service._items.Values);
        if (stillManaged != 0 || pendingRecovery || coreFailure != null || appFailure != null)
        {
            var problems = new List<string>(3);
            if (stillManaged != 0 || pendingRecovery)
                problems.Add($"Spravovaných položek zbývá: {stillManaged}; obnova čeká: {pendingRecovery}.");
            if (coreFailure != null)
                problems.Add("Windows: " + coreFailure.GetBaseException().Message);
            if (appFailure != null)
                problems.Add("Aplikace: " + appFailure.GetBaseException().Message);
            throw new InvalidOperationException(
                "Obnova AI není dokončená. " + string.Join(" ", problems) +
                " Nedokončené položky zůstávají evidované.");
        }
    }

    public static int ResetLearnedProtection()
    {
        var service = new AiSystemResourceManagerService();
        if (!service._journalLoadHealthy || service._recoverInterruptedTrials)
            throw new InvalidOperationException(
                "Naučené blokace nelze bezpečně změnit: záznam AI není čitelný nebo čeká na obnovu.");
        int changed = 0;
        foreach (var pair in service._items.ToArray())
        {
            AiSystemRuntimeItemState state = pair.Value;
            if (state.Rollbacks < 2 && state.FailedBenefitTrials == 0 &&
                !state.BlockedReason.Contains("learned protection", StringComparison.OrdinalIgnoreCase))
                continue;

            service._items[pair.Key] = state with
            {
                Rollbacks = 0,
                FailedBenefitTrials = 0,
                BlockedReason = string.Empty,
                CooldownUntilUtc = null,
                ObservationSinceUtc = null,
                LastAction = "learned protection reset",
                LastChangedUtc = DateTime.UtcNow
            };
            changed++;
        }

        if (changed > 0)
        {
            // Only report a reset after its checkpoint reached disk. An
            // interrupted write leaves the previous protection authoritative.
            if (!service.PersistItems())
                throw new IOException(
                    "Zrušení naučených blokací nebylo potvrzeno uloženým záznamem; původní ochrana musí být ověřena.");
            AppendAction("system", "learned-protection", "reset", $"{changed} položek");
        }
        return changed;
    }

    // Shared, read-only admission predicate; actual imports still require
    // a healthy ownership journal and durable write with all safety gates.
    internal static bool IsCloudCandidateAllowed(string kind, string id) =>
        kind.Equals("service", StringComparison.OrdinalIgnoreCase)
            ? AutomaticServiceCandidates.Contains(id) && !IsCriticalService(id)
            : kind.Equals("task", StringComparison.OrdinalIgnoreCase)
                ? AutomaticTaskCandidates.Contains(id)
                : kind.Equals("driver", StringComparison.OrdinalIgnoreCase) &&
                  AutomaticDriverCandidates.Contains(id);

    public static int ImportCloudMemory(IEnumerable<AiSystemRuntimeItemState> cloudItems)
    {
        var service = new AiSystemResourceManagerService();
        if (!service._journalLoadHealthy || service._recoverInterruptedTrials ||
            AiPendingTrialSelectionPolicy.HasInterruptedOrPendingJournal(
                service._items.Values) ||
            service._items.Values.Any(x => x.Managed || x.Sleeping))
            return 0;
        int imported = 0;
        DateTime importedAt = DateTime.UtcNow;

        foreach (AiSystemRuntimeItemState row in cloudItems)
        {
            bool allowed = IsCloudCandidateAllowed(row.Kind, row.Id);
            if (!allowed) continue;

            string key = Key(row.Kind, row.Id);
            service._items.TryGetValue(key, out AiSystemRuntimeItemState? local);
            AiSystemRuntimeItemState? merged =
                AiCloudProfilePolicy.MergeLearnedProtection(local, row, importedAt);
            if (merged is null)
                continue;
            service._items[key] = merged;
            imported++;
        }

        // Do not report successful cloud learning unless the protected local
        // journal has actually reached disk. Never overwrite an active trial.
        return imported > 0 && service.PersistItems() ? imported : 0;
    }
}
