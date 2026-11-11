using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record AiStorageDriveStatus(
    string Name, string Bus, string Health, double? TemperatureC, double CapacityGiB);

public sealed record AiStorageVolumeStatus(
    string Drive, double FreeGiB, double FreePercent);

public sealed record AiStorageGuardianStatus(
    DateTime UpdatedUtc, int NvmeCount, int SataSsdCount,
    bool? NtfsTrimEnabled, bool? ScheduledOptimizationEnabled,
    AiStorageDriveStatus[] Drives, AiStorageVolumeStatus[] Volumes,
    string[] Warnings, string LastAction);

// All checks run only via the EXISTING FanAgent's quiet AI cycle.
// No permanent process, drive writes, defrag, synthetic benchmark or disk polling loop.
public static class AiStorageGuardianService
{
    // Recovery flags are optional for older four-field journals. When the
    // storage rollback itself fails, preserve the write-ahead ownership and
    // require explicit human verification before any later automatic repair.
    internal sealed record AutoRepairOwnership(
        bool TrimApplied = false, bool TaskApplied = false,
        bool TrimSuppressed = false, bool TaskSuppressed = false,
        bool TrimRecoveryPending = false, bool TaskRecoveryPending = false);
    private static readonly object ScheduleGate = new();
    private static bool _inFlight;
    // Twelve-hour cooldown uses elapsed monotonic time: Windows clock/NTP
    // changes must not repeatedly start or indefinitely defer SSD scans.
    private static long _lastScheduledStamp;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private static readonly string StatusPath = Path.Combine(LogService.Root, "ai-storage-guardian.json");
    // Storage checks run at most every 12 hours. Reuse the parsed status on
    // repeated GUI visits unless the atomic on-disk snapshot really changed.
    private static readonly ObservedJsonSnapshotCache<AiStorageGuardianStatus> StatusCache = new(StatusPath);
    private static readonly string OwnershipPath = Path.Combine(LogService.Root, "ai-storage-ownership.json");

    public static void ScheduleIfDue(CancellationToken token)
    {
        if (token.IsCancellationRequested ||
            !AiSystemSmoothnessGuardService.AllowsBackgroundExperiment() ||
            AiPerformanceExperimentCoordinator.IsActive)
            return;

        lock (ScheduleGate)
        {
            long stamp = Stopwatch.GetTimestamp();
            if (_inFlight || !IsScheduledScanDue(_lastScheduledStamp, stamp,
                    Stopwatch.Frequency)) return;
            _inFlight = true;
            // Also protects against repeatedly failing WMI/storage providers.
            _lastScheduledStamp = stamp;
        }
        SafeTaskService.Forget(ScanScheduledAsync(token), "storage", "SSD/NVMe autonomous guard");
    }

    private static async Task ScanScheduledAsync(CancellationToken token)
    {
        try { await ScanAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Error("storage", "SSD AI background check failed", ex); }
        finally { lock (ScheduleGate) _inFlight = false; }
    }

    public static AiStorageGuardianStatus? ReadLatest()
    {
        try
        {
            return StatusCache.Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Error("storage", "SSD AI status read failed", ex);
            return null;
        }
    }

    private static AutoRepairOwnership ReadOwnership()
    {
        try
        {
            // File.Exists silently returns false on access errors. Read directly to fail closed.
            return ParseOwnership(File.ReadAllText(OwnershipPath));
        }
        catch (FileNotFoundException) { return new AutoRepairOwnership(); }
        catch (DirectoryNotFoundException) { return new AutoRepairOwnership(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LogService.Error("storage", "SSD AI ownership read failed; automatic repair blocked", ex);
            throw new InvalidDataException("SSD ownership journal nelze ověřit; automatické zásahy jsou zablokovány.", ex);
        }
    }

    internal static bool IsOwnershipJournalValid(string json)
    {
        try { _ = ParseOwnership(json); return true; }
        catch (JsonException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    private static AutoRepairOwnership ParseOwnership(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !HasBoolean("TrimApplied") || !HasBoolean("TaskApplied") ||
            !HasBoolean("TrimSuppressed") || !HasBoolean("TaskSuppressed") ||
            !OptionalBoolean("TrimRecoveryPending") ||
            !OptionalBoolean("TaskRecoveryPending"))
            throw new InvalidDataException("SSD ownership journal je neúplný; automatická oprava je zakázána.");
        return JsonSerializer.Deserialize<AutoRepairOwnership>(json)
            ?? throw new InvalidDataException("SSD ownership journal neobsahuje platná data.");

        bool HasBoolean(string key) => root.TryGetProperty(key, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        bool OptionalBoolean(string key) =>
            !root.TryGetProperty(key, out var value) ||
            value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    internal static bool NeedsOwnershipWrite(AutoRepairOwnership persisted, AutoRepairOwnership current) =>
        persisted != current;

    // A successful rollback must not erase PREVIOUS AI-owned changes in the
    // dimension not attempted. A failed rollback is not proof of restoration:
    // keep the crash-safe intent and block all future automatic SSD writes.
    internal static AutoRepairOwnership CreateRepairIntent(
        AutoRepairOwnership previous, bool trimAttempted, bool taskAttempted) =>
        previous with
        {
            TrimApplied = previous.TrimApplied || trimAttempted,
            TaskApplied = previous.TaskApplied || taskAttempted,
            TrimRecoveryPending = previous.TrimRecoveryPending || trimAttempted,
            TaskRecoveryPending = previous.TaskRecoveryPending || taskAttempted
        };

    internal static AutoRepairOwnership CompleteVerifiedRepair(
        AutoRepairOwnership intent, bool trimAttempted, bool taskAttempted) =>
        intent with
        {
            TrimRecoveryPending = intent.TrimRecoveryPending && !trimAttempted,
            TaskRecoveryPending = intent.TaskRecoveryPending && !taskAttempted
        };

    internal static AutoRepairOwnership ResolveFailedRepair(
        AutoRepairOwnership before, AutoRepairOwnership intent,
        bool trimAttempted, bool taskAttempted, bool rollbackUnverified) =>
        rollbackUnverified
            ? intent with
            {
                TrimSuppressed = before.TrimSuppressed || trimAttempted,
                TaskSuppressed = before.TaskSuppressed || taskAttempted,
                TrimRecoveryPending = before.TrimRecoveryPending || trimAttempted,
                TaskRecoveryPending = before.TaskRecoveryPending || taskAttempted
            }
            : before with
            {
                TrimSuppressed = before.TrimSuppressed || trimAttempted,
                TaskSuppressed = before.TaskSuppressed || taskAttempted
            };

    internal static bool MayAutomaticallyRepair(AutoRepairOwnership ownership) =>
        !ownership.TrimRecoveryPending && !ownership.TaskRecoveryPending;

    private static void SaveOwnership(AutoRepairOwnership owner)
    {
        Directory.CreateDirectory(LogService.Root);
        AtomicFileService.WriteText(OwnershipPath, JsonSerializer.Serialize(owner));
    }

    internal static bool IsScheduledScanDue(long lastStamp, long nowStamp, long frequency) =>
        nowStamp > 0 && frequency > 0 &&
        (lastStamp == 0 ||
         (nowStamp >= lastStamp &&
          (nowStamp - (double)lastStamp) / frequency >= CheckInterval.TotalSeconds));

    private static bool IsInternalSsd(StorageDeviceInsight disk)
    {
        string bus = (disk.BusType ?? string.Empty).Trim();
        string media = disk.MediaType ?? string.Empty;
        // A USB-to-NVMe/SCSI bridge still denotes an EXTERNAL disk. Never
        // enable computer-wide SSD auto-repair on that evidence alone.
        if (bus.Length == 0 ||
            new[] { "USB", "UASP", "External", "Removable", "iSCSI", "Virtual",
                    "FileBacked", "1394", "FireWire", "SD", "MMC" }
                .Any(kind => bus.Contains(kind, StringComparison.OrdinalIgnoreCase)))
            return false;
        // SCSI and RAID alone do not prove an internal physical device.
        bool internalBus = new[] { "NVMe", "SATA", "ATA", "SAS", "PCIe" }
            .Any(kind => bus.Equals(kind, StringComparison.OrdinalIgnoreCase));
        bool solid = bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase) ||
            media.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
            media.Contains("Solid", StringComparison.OrdinalIgnoreCase);
        return internalBus && solid;
    }

    // Only a positively healthy internal SSD is evidence sufficient for an
    // unattended system-wide TRIM/scheduled-optimization repair. Unknown SMART
    // status is observation-only; a missing temperature is tolerated because
    // many drivers do not expose it, but invalid/hot readings fail closed.
    internal static bool IsSafeForAutomaticStorageRepair(AiStorageDriveStatus drive) =>
        (string.Equals(drive.Health, "Healthy", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(drive.Health, "OK", StringComparison.OrdinalIgnoreCase)) &&
        (drive.TemperatureC is null ||
         (double.IsFinite(drive.TemperatureC.Value) &&
          drive.TemperatureC.Value is >= 0 and < 75));

    internal static bool HasVerifiedHealthyInternalSsds(
        IReadOnlyList<AiStorageDriveStatus> drives) =>
        drives.Count > 0 && drives.All(IsSafeForAutomaticStorageRepair);

    // AvailableFreeSpace is what the current user can really consume; using
    // TotalFreeSpace for the percentage while showing AvailableFreeSpace GiB
    // can hide quota-limited volumes and suppress low-disk warnings.
    internal static (AiStorageVolumeStatus Status, bool LowSpace) DescribeAvailableSpace(
        string name, long availableBytes, long totalBytes)
    {
        if (totalBytes <= 0 || availableBytes < 0 || availableBytes > totalBytes)
            throw new ArgumentOutOfRangeException(nameof(availableBytes));
        double percent = availableBytes * 100d / totalBytes;
        double freeGiB = availableBytes / 1073741824d;
        return (new AiStorageVolumeStatus(name, Math.Round(freeGiB, 1),
            Math.Round(percent, 1)), percent < 10 && freeGiB < 25);
    }

    public static async Task<AiStorageGuardianStatus> ScanAsync(CancellationToken token = default)
    {
        var all = await SystemInsightService.GetStorageAsync(token)
            .WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        StorageDeviceInsight[] ssds = all.Where(IsInternalSsd).ToArray();
        StorageOptimizationSnapshot settings = await StorageOptimizationService.ReadAsync(token)
            .WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
        var warnings = new List<string>();
        AiStorageDriveStatus[] drives = ssds.Select(d =>
        {
            if (d.TemperatureC is >= 75)
                warnings.Add($"{d.Name}: teplota {d.TemperatureC:0} °C – může zpomalovat SSD.");
            string h = string.IsNullOrWhiteSpace(d.Health) ? "Unknown" : d.Health;
            if (!string.Equals(h, "Healthy", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(h, "OK", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"{d.Name}: stav disku {h} – automatické opravy pozastaveny; prověřit SMART.");
            if (d.TemperatureC is double t && (!double.IsFinite(t) || t < 0))
                warnings.Add($"{d.Name}: neplatné měření teploty – automatické opravy pozastaveny.");
            return new AiStorageDriveStatus(
                d.Name, d.BusType, h, d.TemperatureC, Math.Round(d.SizeGb, 1));
        }).ToArray();

        var volumes = new List<AiStorageVolumeStatus>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0)
                    continue;
                var available = DescribeAvailableSpace(
                    drive.Name, drive.AvailableFreeSpace, drive.TotalSize);
                volumes.Add(available.Status);
                if (available.LowSpace)
                    warnings.Add($"{drive.Name} zbývá {available.Status.FreeGiB:0.0} GiB " +
                        $"({available.Status.FreePercent:0.0} %): málo místa.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Not every removable/virtual volume is currently accessible.
            }
        }

        string action = "Automatický dohled • nic nebylo potřeba měnit";
        if (settings.TrimEnabled == false)
            warnings.Add("NTFS TRIM je vypnutý.");
        if (settings.ScheduledOptimizeEnabled == false)
            warnings.Add("Plánovaná optimalizace jednotek Windows je vypnutá.");

        var owner = ReadOwnership();
        // The last known durable ownership state. Avoid rewriting identical
        // journal content on a read-only twelve-hour SSD check.
        var persistedOwner = owner;
        // If a user explicitly re-disables a value previously restored by AI,
        // respect it instead of fighting them on each scheduled check.
        if (owner.TrimApplied && settings.TrimEnabled == false && !owner.TrimRecoveryPending)
            owner = owner with { TrimApplied = false, TrimSuppressed = true };
        if (owner.TaskApplied && settings.ScheduledOptimizeEnabled == false && !owner.TaskRecoveryPending)
            owner = owner with { TaskApplied = false, TaskSuppressed = true };
        if (!MayAutomaticallyRepair(owner))
        {
            warnings.Add("SSD: předchozí automatická obnova není ověřená; další zásahy vypnuty, nutná ruční kontrola.");
            action = "SSD: neověřená obnova, automatické změny zablokovány.";
        }

        // Unknown SMART status was previously allowed: there was no warning,
        // so a missing health reading could authorize system-wide repair.
        bool healthyEnough = HasVerifiedHealthyInternalSsds(drives);
        bool canAutoRepair = healthyEnough && MayAutomaticallyRepair(owner) &&
            AiSystemSmoothnessGuardService.AllowsBackgroundExperiment() &&
            !AiPerformanceExperimentCoordinator.IsActive && !token.IsCancellationRequested;
        bool repairTrim = canAutoRepair && settings.TrimEnabled == false &&
                          !owner.TrimApplied && !owner.TrimSuppressed;
        bool repairTask = canAutoRepair && settings.ScheduledOptimizeEnabled == false &&
                          !owner.TaskApplied && !owner.TaskSuppressed;

        if ((repairTrim || repairTask) &&
            AiPerformanceExperimentCoordinator.TryAcquire("storage-maintenance"))
        {
            // Journal the automated intent before any Windows mutations. A crash
            // cannot cause endless automatic reapplication on the next launch.
            var intent = CreateRepairIntent(owner, repairTrim, repairTask);
            try
            {
                SaveOwnership(intent); // crash-safe intent before Windows mutation
                persistedOwner = intent;
                settings = await StorageOptimizationService.RepairSafeSsdDefaultsAsync(
                    repairTrim, repairTask, token).ConfigureAwait(false);
                owner = CompleteVerifiedRepair(intent, repairTrim, repairTask);
                action = $"Windows SSD údržba opravena a ověřena: " +
                         $"{(repairTrim ? "NTFS TRIM " : "")}{(repairTask ? "Scheduled Optimize" : "")}";
                warnings.RemoveAll(x => x is "NTFS TRIM je vypnutý." or
                    "Plánovaná optimalizace jednotek Windows je vypnutá.");
                LogService.Info("storage", "SSD_AI_SAFE_REPAIR_VERIFIED trim=" +
                    repairTrim + " scheduled=" + repairTask);
            }
            catch (Exception ex)
            {
                // Ordinary apply failures have a verified storage-service
                // rollback. AggregateException means rollback ALSO failed:
                // retain the original write-ahead ownership instead of
                // falsely recording restoration and losing recovery evidence.
                bool rollbackUnverified = ex is AggregateException;
                owner = ResolveFailedRepair(owner, intent, repairTrim, repairTask,
                    rollbackUnverified);
                action = rollbackUnverified
                    ? "SSD: automatická oprava i obnova selhala; stav neověřen, nutná ruční kontrola."
                    : "SSD: automatická oprava selhala; další pokusy pozastaveny.";
                if (rollbackUnverified)
                    warnings.Add("SSD: původní stav nelze ověřit; zachován záznam AI a další automatické opravy blokovány.");
                LogService.Error("storage", "SSD AI safe repair failed", ex);
            }
            finally { AiPerformanceExperimentCoordinator.Release("storage-maintenance"); }
        }
        // Retain the crash-safe write-ahead intent and persist any later
        // suppression/rollback changes, but skip redundant journal rewrites.
        if (NeedsOwnershipWrite(persistedOwner, owner))
            SaveOwnership(owner);
        var status = new AiStorageGuardianStatus(
            DateTime.UtcNow,
            drives.Count(x => x.Bus.Contains("NVMe", StringComparison.OrdinalIgnoreCase)),
            drives.Count(x => !x.Bus.Contains("NVMe", StringComparison.OrdinalIgnoreCase)),
            settings.TrimEnabled, settings.ScheduledOptimizeEnabled,
            drives, volumes.ToArray(), warnings.ToArray(), action);
        Directory.CreateDirectory(LogService.Root);
        await AtomicFileService.WriteTextAsync(
            StatusPath, JsonSerializer.Serialize(status), token).ConfigureAwait(false);
        StatusCache.InvalidateAfterWrite();
        return status;
    }

    internal static string RunHealthAndAvailableSpaceSmoke()
    {
        var healthy = new AiStorageDriveStatus("Internal", "NVMe", "Healthy", 48, 1000);
        var unknown = healthy with { Health = "Unknown" };
        var failing = healthy with { Health = "Warning" };
        var hot = healthy with { TemperatureC = 75 };
        var invalidTemperature = healthy with { TemperatureC = double.NaN };
        if (!HasVerifiedHealthyInternalSsds([healthy]) ||
            !HasVerifiedHealthyInternalSsds([healthy with { Health = "ok", TemperatureC = null }]) ||
            HasVerifiedHealthyInternalSsds([]) ||
            HasVerifiedHealthyInternalSsds([healthy, unknown]) ||
            HasVerifiedHealthyInternalSsds([failing]) ||
            HasVerifiedHealthyInternalSsds([hot]) ||
            HasVerifiedHealthyInternalSsds([invalidTemperature]))
            throw new InvalidOperationException("SSD unknown/unsafe SMART state authorized an automatic repair.");

        const long gib = 1073741824;
        var quotaLimited = DescribeAvailableSpace("Q:", 5 * gib, 100 * gib);
        var enough = DescribeAvailableSpace("C:", 30 * gib, 100 * gib);
        var lowPercentButEnoughGiB = DescribeAvailableSpace("D:", 50 * gib, 1000 * gib);
        if (!quotaLimited.LowSpace || quotaLimited.Status.FreeGiB != 5 ||
            quotaLimited.Status.FreePercent != 5 ||
            enough.LowSpace || enough.Status.FreePercent != 30 ||
            lowPercentButEnoughGiB.LowSpace || lowPercentButEnoughGiB.Status.FreePercent != 5)
            throw new InvalidOperationException("SSD available-space and quota warning disagreed.");
        bool invalidCapacityRejected = false;
        try { _ = DescribeAvailableSpace("Broken:", 200 * gib, 100 * gib); }
        catch (ArgumentOutOfRangeException) { invalidCapacityRejected = true; }
        if (!invalidCapacityRejected)
            throw new InvalidOperationException("SSD impossible free-space measurement was accepted.");
        return "SSD_AI_HEALTH_SPACE_SMOKE_OK unknownHealthDenied=true " +
            "invalidTemperatureDenied=true availableQuotaPercent=true lowSpace=true writes=0";
    }

    internal static string RunOfflineSmoke()
    {
        string healthSpaceSmoke = RunHealthAndAvailableSpaceSmoke();
        var nvme = new StorageDeviceInsight("NVMe", "SSD", "NVMe", "Healthy",
            "OK", 1000, 0, "", "", "", 48, true);
        var usb = nvme with { Name = "External", BusType = "USB" };
        var hdd = nvme with { Name = "HDD", MediaType = "Fixed hard disk media", BusType = "SATA" };
        var sataSsd = nvme with { Name = "SATA SSD", BusType = "SATA" };
        var usbNvme = nvme with { Name = "USB-to-NVMe", BusType = "USB NVMe" };
        var usbScsi = nvme with { Name = "USB-to-SCSI", BusType = "USB SCSI" };
        var scsiUnknown = nvme with { Name = "Ambiguous SCSI", BusType = "SCSI" };
        var raidUnknown = nvme with { Name = "Ambiguous RAID", BusType = "RAID" };
        var virtualNvme = nvme with { Name = "Virtual", BusType = "Virtual NVMe" };
        // An unsuccessful Trim repair must not drop an earlier AI-owned
        // Scheduled Optimize change; uncertain rollback keeps durable intent.
        var previous = new AutoRepairOwnership(TrimApplied: true);
        var proposed = CreateRepairIntent(previous, trimAttempted: false,
            taskAttempted: true);
        var committed = CompleteVerifiedRepair(proposed, trimAttempted: false,
            taskAttempted: true);
        var verifiedRollback = ResolveFailedRepair(previous, proposed,
            trimAttempted: false, taskAttempted: true, rollbackUnverified: false);
        var unverifiedRollback = ResolveFailedRepair(previous, proposed,
            trimAttempted: false, taskAttempted: true, rollbackUnverified: true);
        const string legacyJson = "{\"TrimApplied\":true,\"TaskApplied\":false," +
            "\"TrimSuppressed\":false,\"TaskSuppressed\":false}";
        if (!IsOwnershipJournalValid(legacyJson) ||
            !ParseOwnership(legacyJson).TrimApplied ||
            !proposed.TaskRecoveryPending || MayAutomaticallyRepair(proposed) ||
            committed.TaskRecoveryPending || !MayAutomaticallyRepair(committed) ||
            committed != previous with { TaskApplied = true } ||
            verifiedRollback != previous with { TaskSuppressed = true } ||
            !verifiedRollback.TrimApplied || verifiedRollback.TaskApplied ||
            !MayAutomaticallyRepair(verifiedRollback) ||
            !unverifiedRollback.TrimApplied || !unverifiedRollback.TaskApplied ||
            !unverifiedRollback.TaskRecoveryPending || !unverifiedRollback.TaskSuppressed ||
            MayAutomaticallyRepair(unverifiedRollback) ||
            !IsOwnershipJournalValid(JsonSerializer.Serialize(unverifiedRollback)) ||
            !ParseOwnership(JsonSerializer.Serialize(unverifiedRollback)).TaskRecoveryPending ||
            IsOwnershipJournalValid(legacyJson[..^1] +
                ",\"TrimRecoveryPending\":null}"))
            throw new InvalidOperationException(
                "SSD AI rollback journal lost ownership or admitted uncertain recovery.");
        const long freq = 10_000;
        const long stamp = 100_000;
        long deadline = stamp + 12 * 60 * 60 * freq;
        if (!IsInternalSsd(nvme) || !IsInternalSsd(sataSsd) ||
            IsInternalSsd(usb) || IsInternalSsd(hdd) ||
            IsInternalSsd(usbNvme) || IsInternalSsd(usbScsi) ||
            IsInternalSsd(scsiUnknown) || IsInternalSsd(raidUnknown) ||
            IsInternalSsd(virtualNvme) ||
            !IsScheduledScanDue(0, stamp, freq) ||
            IsScheduledScanDue(stamp, deadline - 1, freq) ||
            !IsScheduledScanDue(stamp, deadline, freq) ||
            IsScheduledScanDue(stamp, stamp - 1, freq) ||
            IsScheduledScanDue(stamp, deadline, 0) ||
            IsScheduledScanDue(0, 0, freq))
            throw new InvalidOperationException("SSD admission or monotonic 12-hour interval regression.");
        return "SSD_AI_OFFLINE_SMOKE_OK nvme=true sata=true usbBridgeExcluded=true " +
            "scsiRaidUnverified=true twelveHourMonotonic=true windowsWrites=0 " +
            healthSpaceSmoke;
    }
}
