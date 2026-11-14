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
    internal static bool IsCachedProbeFresh(
        DateTime expiresUtc, DateTime nowUtc, TimeSpan maxLifetime)
    {
        TimeSpan remaining = expiresUtc - nowUtc;
        return maxLifetime > TimeSpan.Zero &&
            remaining > TimeSpan.Zero && remaining <= maxLifetime;
    }

    internal static bool ShouldRunDeepScan(
        ref DateTime lastScanUtc,
        DateTime nowUtc,
        TimeSpan interval)
    {
        if (IsWithinRecentInterval(lastScanUtc, nowUtc, interval))
            return false;
        lastScanUtc = nowUtc;
        return true;
    }

    private static TimeSpan AdaptiveScanInterval(TimeSpan baseInterval, double lastScanMs)
    {
        double multiplier = lastScanMs >= 100 ? 3d : lastScanMs >= 50 ? 2d : 1d;
        return TimeSpan.FromTicks((long)(baseInterval.Ticks * multiplier));
    }

    private AiSystemRuntimeItemState GetState(string kind, string id)
    {
        string key = Key(kind, id);
        if (_items.TryGetValue(key, out var state))
            return state;
        state = new AiSystemRuntimeItemState(
            kind, id, false, false, string.Empty, null, null, null, null, false, 0, "none");
        _items[key] = state;
        return state;
    }

    private void SetBlocked(string kind, string id, string reason)
    {
        var state = GetState(kind, id);
        if (state.ObservationSinceUtc == null &&
            string.Equals(state.BlockedReason, reason, StringComparison.Ordinal))
            return;

        _items[Key(kind, id)] = state with
        {
            BlockedReason = reason,
            ObservationSinceUtc = null,
            DriverObservationLastUtc = null
        };
    }

    private void ResetObservation(string kind, string id)
    {
        var state = GetState(kind, id);
        if (state.ObservationSinceUtc == null && string.IsNullOrEmpty(state.BlockedReason))
            return;
        _items[Key(kind, id)] = state with
        {
            ObservationSinceUtc = null,
            DriverObservationLastUtc = null,
            BlockedReason = string.Empty
        };
    }

    private void LoadItems()
    {
        try
        {
            // File.Exists silently returns false for some access-denied
            // conditions. Only a genuinely absent file is a first install;
            // unreadable ownership records must fail closed instead.
            try { _ = File.GetAttributes(ItemStatePath); }
            catch (FileNotFoundException)
            {
                _journalInitiallyAbsent = true;
                lock (SnapshotGate) CachedItems = Array.Empty<AiSystemRuntimeItemState>();
                return;
            }
            catch (DirectoryNotFoundException)
            {
                _journalInitiallyAbsent = true;
                lock (SnapshotGate) CachedItems = Array.Empty<AiSystemRuntimeItemState>();
                return;
            }

            // An atomic File.Move may replace the ledger between stat/read.
            // Accept only a stable, bounded file snapshot from this agent.
            if (!AiJournalSnapshotReadPolicy.TryReadStable(
                    ItemStatePath, ItemsMetadata, ItemsMetadata.Read(),
                    out AiSystemRuntimeItemState[] rows, out _))
                throw new InvalidDataException("AI ownership journal could not be read consistently and safely.");
            foreach (var row in rows)
            {
                if (row.Kind.Equals("device", StringComparison.OrdinalIgnoreCase))
                    continue;
                _items[Key(row.Kind, row.Id)] = row;
            }

            var snapshot = _items.Values.OrderBy(x => x.Kind).ThenBy(x => x.Id).ToArray();
            lock (SnapshotGate)
            {
                CachedItems = snapshot;
                CachedItemsStamp = ItemsMetadata.Read();
                CachedItemsRejected = false;
            }
            _lastPersistJson = JsonSerializer.Serialize(snapshot);
            // Only an admissible normalized checkpoint can be fast-accepted.
            // A legacy file may decode within its read limit yet expand past
            // the durable write limit when the JSON serializer escapes it.
            if (AiWriteAheadSerializationPolicy.FitsDurableJournal(
                    _lastPersistJson.Length, snapshot.Length))
                _lastDurableJournalValues = _items.Values.ToArray();
        }
        catch (Exception ex)
        {
            // An unreadable ownership journal may contain owned Windows
            // changes. Do not silently replace it with an empty journal or
            // admit more autonomous service/task/driver experiments.
            _journalLoadHealthy = false;
            _items.Clear();
            _lastError = "AI ownership journal unreadable; Windows changes paused";
            lock (SnapshotGate) CachedItems = Array.Empty<AiSystemRuntimeItemState>();
            LogService.Error("ai-system", "AI ownership journal load failed closed", ex);
        }
    }

    private static void AppendAction(string kind, string id, string action, string reason)
    {
        lock (ActionHistoryGate)
        {
            try
            {
                Directory.CreateDirectory(StateDirectory);
                var history = AiActionHistoryAppendPolicy.Append(
                    CachedActionHistory ?? (AiSystemActionHistoryEntry[])ReadActionHistory(),
                    new AiSystemActionHistoryEntry(DateTime.UtcNow, kind, id, action, reason));
                // Serialize directly as UTF-8 with byte-identical JSON.
                // Publish the new memory snapshot only after atomic durability:
                // a failed write must not appear committed in later UI reads.
                AtomicFileService.WriteAllBytesAtomic(
                    ActionHistoryPath, JsonSerializer.SerializeToUtf8Bytes(history));
                CachedActionHistory = history;
                CachedActionHistoryStamp = ActionHistoryMetadata.Read();
            }
            catch
            {
            }
        }
    }

    // Pre-write intent MUST be durable before touching any Windows resource.
    // A failed intent persistence is a veto, not a best-effort log.
    private void BeginWriteAheadMutation(
        AiSystemRuntimeItemState prior, AiSystemRuntimeItemState intent)
    {
        if (_maintenanceWriteFaulted || _recoverInterruptedTrials)
            throw new InvalidOperationException(
                "AI new Windows intervention blocked until ownership checkpoint recovery.");
        string key = Key(intent.Kind, intent.Id);
        _items[key] = intent;
        if (!PersistItems(writeAhead: true))
        {
            _items[key] = prior;
            _maintenanceWriteFaulted = true; // veto all other mutations this cycle
            throw new IOException("AI mutation denied: durable ownership intent could not be saved.");
        }
        _recoverInterruptedTrials = true;
    }

    private void CommitWriteAheadMutation(
        AiSystemRuntimeItemState intent, AiSystemRuntimeItemState completed)
    {
        string key = Key(intent.Kind, intent.Id);
        _items[key] = completed with { PendingMutation = string.Empty };
        if (!PersistItems())
        {
            _items[key] = intent;
            _recoverInterruptedTrials = true;
            throw new IOException("Windows changed but AI completion record could not be saved; recovery required.");
        }
        _recoverInterruptedTrials =
            AiPendingTrialSelectionPolicy.HasPendingWriteAheadMutation(_items.Values);
    }

    private bool PersistItems(bool writeAhead = false, bool unchangedIsSuccess = false)
    {
        // A failed load is NOT an empty journal: never overwrite a damaged
        // ownership record with a fresh snapshot, even during an OFF restore.
        if (!_journalLoadHealthy)
            return false;
        try
        {
            // No mutation of the immutable journal records means the last
            // successfully committed JSON remains current. Keep the original
            // new-mutation vs recovery semantics even on this fast path.
            if (_lastPersistJson.Length != 0 &&
                AiWriteAheadSerializationPolicy.MatchesDurableValues(
                    _lastDurableJournalValues, _items.Values))
                return unchangedIsSuccess;

            var snapshot = _items.Values.OrderBy(x => x.Kind).ThenBy(x => x.Id).ToArray();
            string json = JsonSerializer.Serialize(snapshot);
            // Never persist a checkpoint larger than our own startup/GUI
            // decoder admits: that would strand owned Windows changes after
            // the next restart, even if the disk write itself succeeded.
            if (!AiWriteAheadSerializationPolicy.FitsDurableJournal(
                    json.Length, snapshot.Length))
                return false;
            AiWriteAheadSerializationPolicy.CheckpointDecision checkpoint =
                AiWriteAheadSerializationPolicy.DecideCheckpoint(
                    json, _lastPersistJson, unchangedIsSuccess);
            if (checkpoint != AiWriteAheadSerializationPolicy.CheckpointDecision.WriteRequired)
                return checkpoint == AiWriteAheadSerializationPolicy.CheckpointDecision.AlreadyDurable;
            // An unchanged periodic tick does not need any filesystem
            // metadata probe; create directories only for real writes.
            Directory.CreateDirectory(StateDirectory);
            string temp = ItemStatePath + ".tmp";
            if (writeAhead)
            {
                // Flush the intent to disk before issuing Stop/Disable.
                // Atomic same-volume rename makes a torn temp irrelevant.
                // Persist EXACTLY the JSON already compared against the
                // durable checkpoint; no second serializer pass or risk of
                // a mismatched UTF-8 payload under mutable journal rows.
                byte[] bytes = AiWriteAheadSerializationPolicy.EncodeJournalJson(json);
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
            }
            else
                File.WriteAllText(temp, json);
            // First-install must CREATE, never overwrite a journal another
            // agent wrote after our initial absence check. A collision
            // fails closed instead of destroying its ownership evidence.
            AiJournalSnapshotReadPolicy.CommitSnapshot(
                temp, ItemStatePath, _journalInitiallyAbsent);
            _journalInitiallyAbsent = false;
            _lastPersistJson = json;
            _lastDurableJournalValues = _items.Values.ToArray();
            // The atomic move is already durable. A failure refreshing
            // IN-MEMORY cache metadata cannot turn it into a failed write:
            // the caller could otherwise revert the intent in RAM while
            // leaving its durable PendingMutation on disk.
            try
            {
                lock (SnapshotGate)
                {
                    CachedItems = snapshot;
                    CachedItemsStamp = ItemsMetadata.Read();
                    CachedItemsRejected = false;
                }
            }
            catch (Exception ex)
            {
                LogService.Error("ai-system",
                    "AI journal saved but optional read cache refresh failed", ex);
            }
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("ai-system", "AI System Runtime state persist failed", ex);
            return false;
        }
    }

    private void WriteHealth(
        AiSystemRuntimeConfig config,
        double loadPercent,
        bool gameActive,
        bool realIdle)
    {
        try
        {
            DateTime nowUtc = DateTime.UtcNow;
            double roundedLoad = Math.Round(loadPercent, 1);
            AiMeasuredOptimizationPolicy.HealthEvaluation result =
                AiMeasuredOptimizationPolicy.EvaluateHealth(
                    _items.Values, nowUtc, _lastHealthFingerprint?.LatestTarget);
            AiMeasuredOptimizationPolicy.HealthCounts totals = result.Counts;
            AiMeasuredOptimizationPolicy.Summary measured = result.Measured;
            int managed = totals.Managed;
            int sleeping = totals.Sleeping;
            int blocked = totals.Blocked;
            int managedServices = totals.ServiceManaged;
            int managedTasks = totals.TaskManaged;
            int managedDrivers = totals.DriverManaged;
            // This is an actual SCM-verified opt-out, not a measured CPU win.
            // Count the stopped, AI-owned service but never fabricate A/B/A savings.
            if (_searchOptimization.IsManaged)
            {
                managed++;
                sleeping++;
                managedServices++;
            }
            if (_bluetoothOptimization.IsManaged)
            {
                managed++;
                sleeping++;
                managedServices++;
            }
            if (_locationOptimization.IsManaged)
            {
                managed++;
                sleeping++;
                managedServices++;
            }
            if (_unusedPnpDevices.IsManaged)
            {
                managed++;
                sleeping++;
                managedDrivers++;
            }
            int optionalTaskManaged = _optionalTasks.ManagedCount +
                (_searchMaintenanceTask.IsManaged ? 1 : 0);
            managed += optionalTaskManaged;
            sleeping += optionalTaskManaged;
            managedTasks += optionalTaskManaged;
            var fingerprint = new AiSystemHealthFingerprint(
                config.Enabled, config.ServicesEnabled, config.ScheduledTasksEnabled,
                config.OptionalDevicesEnabled,
                managed, sleeping, blocked, _restoredCount,
                managedServices, managedTasks, managedDrivers,
                measured.ActiveVerified, measured.ConfirmedLast7Days,
                measured.LatestBenefitPp, measured.LatestBenefitPercent,
                measured.LatestTarget, measured.LatestVerifiedUtc,
                measured.LatestObservedAvailableRamBenefitMiB,
                _lastAction, _lastError,
                roundedLoad, gameActive, realIdle);

            if (_lastHealthFingerprint is AiSystemHealthFingerprint previous &&
                previous == fingerprint &&
                _lastHealthWriteUtc != DateTime.MinValue &&
                nowUtc - _lastHealthWriteUtc < TimeSpan.FromMinutes(2))
                return;

            var health = new AiSystemRuntimeHealth(
                nowUtc,
                config.Enabled,
                config.ServicesEnabled,
                config.ScheduledTasksEnabled,
                config.OptionalDevicesEnabled,
                managed,
                sleeping,
                blocked,
                _restoredCount,
                managedServices,
                managedTasks,
                managedDrivers,
                _lastAction,
                _lastError,
                roundedLoad,
                gameActive,
                realIdle,
                measured.ActiveVerified,
                measured.ConfirmedLast7Days,
                measured.LatestBenefitPp,
                measured.LatestBenefitPercent,
                measured.LatestTarget,
                measured.LatestVerifiedUtc,
                measured.LatestObservedAvailableRamBenefitMiB);

            Directory.CreateDirectory(StateDirectory);
            // Serialize directly to UTF-8; retain the existing atomic file
            // replacement and publish health only after durable success.
            // Unique sidecars keep two RuntimeHosts from trampling the same
            // fixed .tmp payload for this shared machine health file.
            AtomicFileService.WriteAllBytesAtomic(
                HealthPath, JsonSerializer.SerializeToUtf8Bytes(health));
            _lastHealthFingerprint = fingerprint;
            _lastHealthWriteUtc = nowUtc;
            lock (SnapshotGate)
            {
                CachedHealth = health;
                CachedHealthStamp = HealthMetadata.Read();
            }
        }
        catch (Exception ex)
        {
            LogService.Error("ai-system", "AI System Runtime health write failed", ex);
        }
    }

    private static string Key(string kind, string id) => kind + "|" + id;
    private static string ShortTask(string path)
    {
        int slash = path.LastIndexOf('\\');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }
}
