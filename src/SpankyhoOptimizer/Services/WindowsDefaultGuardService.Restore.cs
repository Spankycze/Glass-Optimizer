using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static partial class WindowsDefaultGuardService
{
    internal static bool RestoreLegacyRamSizedSvcHostOverride(int expectedValueKb, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !CanRepairMachineRegistry() || expectedValueKb <= 0)
            return false;
        const string groupId = "memory-defaults";
        const string valueName = "SvcHostSplitThresholdInKB";
        const string keyPath = @"SYSTEM\CurrentControlSet\Control";
        lock (Sync)
        {
            token.ThrowIfCancellationRequested();
            // Hold the same cross-process consent lease as the UI until the
            // explicit Fixer's journaled registry write and readback complete.
            // A different process cannot revoke approval between check/write.
            using var consentLease = AcquireApprovalFileLock(ConsentFile + ".lock", token);
            token.ThrowIfCancellationRequested();
            if (!ReadApprovals().Contains(groupId)) return false;
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(keyPath, writable: true);
            if (key == null || key.GetValue(valueName, null) is not int existing ||
                existing != expectedValueKb || key.GetValueKind(valueName) != RegistryValueKind.DWord)
                return false;
            var backup = new BackupRecord(DateTime.UtcNow, groupId,
                RegistryHive.LocalMachine.ToString(), keyPath, valueName,
                RegistryValueKind.DWord.ToString(),
                existing.ToString(CultureInfo.InvariantCulture), "Remove RAM-sized override");
            SaveRollback(backup, token);
            token.ThrowIfCancellationRequested();
            key.DeleteValue(valueName, throwOnMissingValue: false);
            if (key.GetValue(valueName, null) != null)
                throw new IOException("RAM-sized svchost override remains after restore.");
            SaveCompletedReceipt(backup);
            // Fixer is an explicit action, not an automatic Guard restore.
            RecordSuccessfulRepair(groupId + "/" + valueName, DateTime.UtcNow, automatic: false);
            LogService.Info("default-guard", "RESTORED legacy RAM-sized svchost threshold with rollback journal");
            return true;
        }
    }

    // One invocation kind defines BOTH scope and throttling. Fixer is explicit
    // but still requires current consent and may handle HKCU; UAC worker uses
    // the validated, machine-only button-click consent snapshot.
    private static (bool MachineOnly, bool Automatic) InvocationPolicy(RepairInvocation invocation) =>
        invocation switch
        {
            RepairInvocation.PeriodicGuard => (false, true),
            RepairInvocation.ExplicitFixer => (false, false),
            RepairInvocation.ExplicitUacWorker => (true, false),
            _ => throw new ArgumentOutOfRangeException(nameof(invocation))
        };

    private static int RepairScopedOnce(HashSet<string> approved,
        RepairInvocation invocation, CancellationToken token)
    {
        var policy = InvocationPolicy(invocation);
        bool machineOnly = policy.MachineOnly;
        bool explicitUserAction = !policy.Automatic;
        if (!OperatingSystem.IsWindows() || approved.Count == 0) return 0;
        int applied = 0;
        bool hasAdministratorRights = CanRepairMachineRegistry();
        if (machineOnly && !hasAdministratorRights)
            throw new UnauthorizedAccessException("Správcovské oprávnění chybí.");
        if (!machineOnly && approved.Contains("green-ethernet"))
        {
            // Monitoring only. Mutating an active NIC can sever the user's
            // remote connection even with -NoRestart; never do it on a timer.
            try
            {
                string? state = NativeNicTuningService.ReadRealtekGreenEthernetStatus(token);
                token.ThrowIfCancellationRequested();
                if (state is "Enabled" or "Disabled")
                {
                    bool enabled = state == "Enabled";
                    bool stateChanged;
                    lock (Sync)
                    {
                        stateChanged = ShouldReportGreenChange(_lastGreenEthernetEnabled, enabled);
                        _lastGreenEthernetEnabled = enabled;
                    }
                    if (stateChanged)
                        LogService.Info("default-guard", enabled
                            ? "Green Ethernet enabled; explicit local apply needed"
                            : "Green Ethernet confirmed disabled");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { LogService.Info("default-guard", "NIC monitor skipped: " + ex.GetType().Name); }
        }
        foreach (var rule in Rules)
        {
            token.ThrowIfCancellationRequested();
            if (!approved.Contains(rule.GroupId)) continue;
            if (machineOnly && rule.Hive != RegistryHive.LocalMachine)
                continue;
            if (rule.Hive == RegistryHive.LocalMachine && !hasAdministratorRights)
                continue; // Non-elevated instance monitors only; no failed HKLM write attempts.
            string tokenKey = rule.GroupId + "/" + rule.Name;
            bool deferred, announcePause = false;
            lock (Sync)
            {
                deferred = ShouldDeferRepeat(explicitUserAction,
                    RecentRepairs.TryGetValue(tokenKey, out var recent) ? recent : null,
                    DateTime.UtcNow);
                if (deferred) announcePause = ReportedThrottles.Add(tokenKey);
                else ReportedThrottles.Remove(tokenKey);
            }
            if (deferred)
            {
                if (announcePause)
                    LogService.Info("default-guard", "Repeatedly changed registry value paused after " +
                        MaxAutoRestoresPerHour + " auto-restores/hour: " + tokenKey);
                continue;
            }

            try
            {
                using RegistryKey hive = RegistryKey.OpenBaseKey(rule.Hive, RegistryView.Registry64);
                using RegistryKey? read = hive.OpenSubKey(rule.Key, writable: false);
                object? current = read?.GetValue(rule.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (current == null) continue;
                RegistryValueKind kind = read!.GetValueKind(rule.Name);
                if (!ShouldRestore(rule, current, kind)) continue;

                // Periodic monitoring always rechecks the user's CURRENT consent.
                // Explicit UAC worker instead uses the allowlisted approval snapshot
                // captured at the user's button click. An alternate admin account
                // may have a different LocalAppData profile or no consent file.
                lock (Sync)
                {
                    token.ThrowIfCancellationRequested();
                    // Same lease as SetApproved across ALL processes, not only
                    // this instance's Sync. No revoked consent may slip between
                    // final verification and the journaled registry write.
                    // The explicitly approved, machine-only UAC worker instead
                    // keeps its button-click snapshot (possibly another user).
                    using var consentLease = machineOnly ? null :
                        AcquireApprovalFileLock(ConsentFile + ".lock", token);
                    token.ThrowIfCancellationRequested();
                    if (!ShouldApplyWithConsent(machineOnly, rule.GroupId, approved,
                            currentApprovals: !machineOnly ? ReadApprovals() : null))
                        continue;
                    // Guard against a second automatic cycle racing while we
                    // were reading the current value outside the lock.
                    if (ShouldDeferRepeat(explicitUserAction,
                        RecentRepairs.TryGetValue(tokenKey, out var updated) ? updated : null,
                        DateTime.UtcNow))
                        continue;

                    using RegistryKey? writable = hive.OpenSubKey(rule.Key, writable: true);
                    if (writable == null) throw new IOException("Registr není přístupný pro zápis.");
                    object? recheck = writable.GetValue(rule.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (!Equals(recheck, current) || writable.GetValueKind(rule.Name) != kind)
                        continue; // A different process already changed this value.

                    // Journal the exact original value BEFORE any write.
                    bool removeOverride = RestoresByRemovingValue(rule);
                    string action = removeOverride ? "Remove override" : rule.GroupId switch
                    {
                        "service-shutdown" => "Set " + ServiceShutdownDefaultText,
                        "gpu-priority" => "Set 8",
                        "mmcss-defaults" => "Set 20",
                        "memory-defaults" => "Set 0",
                        _ => throw new InvalidOperationException("Unknown default-guard restore operation.")
                    };
                    var backup = new BackupRecord(DateTime.UtcNow, rule.GroupId, rule.Hive.ToString(),
                        rule.Key, rule.Name, kind.ToString(),
                        Convert.ToString(current, CultureInfo.InvariantCulture) ?? "", action);
                    SaveRollback(backup, token);
                    token.ThrowIfCancellationRequested();
                    if (removeOverride)
                        writable.DeleteValue(rule.Name, throwOnMissingValue: false);
                    else if (rule.GroupId == "service-shutdown")
                        writable.SetValue(rule.Name, ServiceShutdownDefaultText, RegistryValueKind.String);
                    else if (rule.GroupId == "gpu-priority")
                        writable.SetValue(rule.Name, 8, RegistryValueKind.DWord);
                    else if (rule.GroupId == "mmcss-defaults")
                        writable.SetValue(rule.Name, 20, RegistryValueKind.DWord);
                    else if (rule.GroupId == "memory-defaults")
                        writable.SetValue(rule.Name, 0, RegistryValueKind.DWord);
                    else
                        throw new InvalidOperationException("Unknown default-guard restore operation.");

                    object? verified = writable.GetValue(rule.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    bool restoreVerified = removeOverride ? verified == null :
                        rule.GroupId switch
                        {
                            "service-shutdown" => string.Equals(verified?.ToString(), ServiceShutdownDefaultText, StringComparison.Ordinal),
                            "gpu-priority" => Equals(verified, 8),
                            "mmcss-defaults" => Equals(verified, 20),
                            "memory-defaults" => Equals(verified, 0),
                            _ => false
                        };
                    if (!restoreVerified)
                        throw new IOException("Po zápisu nebyl potvrzen požadovaný stav registru.");
                    SaveCompletedReceipt(backup);
                    // Only scheduled Guard repairs consume the auto-only budget;
                    // explicit UAC and Fixer actions do not, regardless of scope.
                    RecordSuccessfulRepair(tokenKey, DateTime.UtcNow,
                        automatic: policy.Automatic);
                }
                applied++; // Count only our own successful, verified write.
                LogService.Info("default-guard", "RESTORED " + rule.GroupId + "/" + rule.Name +
                    " (original value recorded in rollback journal)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or ArgumentException or InvalidOperationException)
            {
                // No privilege escalation, service stop, retry storm or unbounded wait.
                LogService.Error("default-guard", "Skipped registry repair: " + rule.Name, ex);
            }
        }
        return applied;
    }

    // The user explicitly requested default restoration whenever settings
    // drift. One successful repair must not block the NEXT five-minute check.
    // Suspend only genuine repeated flip-flops after three verified writes.
    // Manual UAC operations always bypass this automatic-only throttling.
    private static bool ShouldDeferRepeat(bool explicitMachineRepair,
        IReadOnlyList<DateTime>? recent, DateTime nowUtc) =>
        !explicitMachineRepair && recent != null &&
        recent.Count(at => at <= nowUtc && nowUtc - at < AntiFlapWindow) >= MaxAutoRestoresPerHour;

    // Caller must hold Sync. Only periodic automatic restores contribute to
    // the anti-flap budget; explicit UAC and Fixer actions bypass it entirely.
    private static void RecordSuccessfulRepair(string key, DateTime nowUtc, bool automatic)
    {
        if (!automatic) return;
        if (!RecentRepairs.TryGetValue(key, out var entries))
        {
            entries = [];
            RecentRepairs[key] = entries;
        }
        entries.RemoveAll(at => at > nowUtc || nowUtc - at >= AntiFlapWindow);
        entries.Add(nowUtc);
    }

    private static bool ShouldReportGreenChange(bool? previousEnabled, bool enabled) =>
        previousEnabled != enabled;

    // For periodic repairs require persisted CURRENT approval. For a manually
    // confirmed elevated worker the validated button-click snapshot is authoritative,
    // regardless of which administrator profile UAC used.
    private static bool ShouldApplyWithConsent(bool manualMachineRepair, string groupId,
        HashSet<string> selected, HashSet<string>? currentApprovals) =>
        selected.Contains(groupId) &&
        (manualMachineRepair || (currentApprovals?.Contains(groupId) ?? false));

    private static string FormatReadOnlyValue(Rule rule, bool keyAccessible,
        object? value, RegistryValueKind? kind)
    {
        if (!keyAccessible) return "(nezjištěno)";
        if (value == null) return "(Windows default)";
        if (!kind.HasValue || !IsExpectedKind(rule, kind.Value))
            return "(nepodporovaný typ: " + kind?.ToString() + ")";
        if (rule.Name == "SvcHostSplitThresholdInKB" && value is int split)
            return "0x" + unchecked((uint)split).ToString("X8", CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(nezjištěno)";
    }

    private static bool RestoresByRemovingValue(Rule rule) =>
        rule.GroupId is "app-shutdown" or "gpu-tdr" or "fullscreen" ||
        rule.Name == "SvcHostSplitThresholdInKB";

    private static bool IsExpectedKind(Rule rule, RegistryValueKind kind) =>
        rule.GroupId is "app-shutdown" or "service-shutdown"
            ? kind == RegistryValueKind.String
            : kind == RegistryValueKind.DWord;

    private static bool ShouldRestore(Rule rule, object? raw, RegistryValueKind? kind)
    {
        if (raw == null || kind == null || !IsExpectedKind(rule, kind.Value)) return false;
        // Only remove overrides whose registry type is understood and recoverable.
        // Corrupt/unexpected value types are reported as unverified, NEVER deleted.
        if (rule.GroupId == "app-shutdown")
            return kind == RegistryValueKind.String; // Known user override; Windows uses missing value.
        if (rule.GroupId == "gpu-tdr")
            return kind == RegistryValueKind.DWord; // Driver timeout override, normally REG_DWORD.
        if (rule.Name == "SvcHostSplitThresholdInKB")
            return raw is int threshold && unchecked((uint)threshold) == uint.MaxValue;
            // Only undo the confirmed 0xFFFFFFFF service-grouping tweak, not normal Windows values.
        if (rule.GroupId == "service-shutdown")
            return kind == RegistryValueKind.String &&
                !string.Equals(Convert.ToString(raw, CultureInfo.InvariantCulture), ServiceShutdownDefaultText, StringComparison.Ordinal);
        if (rule.AnyOverride) return kind == RegistryValueKind.DWord;
        if (rule.GroupId == "fullscreen" && kind != RegistryValueKind.DWord) return false;
        if (rule.GroupId is "gpu-priority" or "mmcss-defaults" or "memory-defaults")
        {
            if (kind != RegistryValueKind.DWord) return false;
            long expected = rule.GroupId switch
            {
                "gpu-priority" => 8,
                "mmcss-defaults" => 20,
                _ => 0
            };
            return Convert.ToInt64(raw, CultureInfo.InvariantCulture) != expected;
        }
        string text = Convert.ToString(raw, CultureInfo.InvariantCulture)?.Trim() ?? "";
        if (rule.BadValue != null) return string.Equals(text, rule.BadValue, StringComparison.Ordinal);
        return rule.BadAtMost.HasValue &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms) &&
            ms >= 0 && ms <= rule.BadAtMost;
    }

    // UI writes must distinguish a truly missing file (fresh install) from a
    // corrupt or unreadable existing file. The latter must never be replaced
    // by an implicitly empty set, even when the user is enabling one group.
    private static HashSet<string> ReadApprovalsForWrite(string path)
    {
        string content;
        try { content = File.ReadAllText(path); }
        catch (FileNotFoundException) { return new(StringComparer.Ordinal); } // First-time setup only.
        // File.Exists would also return false for access errors. Never confuse
        // an unreadable existing file with missing consent during a write.
        string[] parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<string[]>(content)
                ?? throw new InvalidDataException("Soubor schválení obsahuje null místo seznamu.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Soubor schválení je poškozený; původní nastavení zůstalo zachováno.", ex);
        }
        if (parsed.Any(id => !AvailableGroups.Any(group => group.Id == id)))
            throw new InvalidDataException("Soubor schválení obsahuje neznámou skupinu; nesmí být přepsán starší verzí.");
        return new HashSet<string>(parsed, StringComparer.Ordinal);
    }

    private static HashSet<string> ReadApprovals(string? path = null)
    {
        try
        {
            string source = path ?? ConsentFile;
            if (!File.Exists(source)) return new(StringComparer.Ordinal);
            var parsed = JsonSerializer.Deserialize<string[]>(File.ReadAllText(source));
            return new HashSet<string>((parsed ?? []).Where(id => AvailableGroups.Any(g => g.Id == id)),
                StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unavailable config MUST NOT imply authorization.
            return new(StringComparer.Ordinal);
        }
    }

    private static void ValidateGroup(string id)
    {
        if (!AvailableGroups.Any(group => group.Id == id))
            throw new ArgumentOutOfRangeException(nameof(id), "Neznámá skupina oprav.");
    }

    // The periodic monitor, manual elevated HKLM worker, and explicitly
    // clicked Green Ethernet worker all append to the SAME journal. FileShare.Read
    // intentionally permits only one writer; retry ONLY transient sharing
    // violations with a strict time bound rather than risking interleaved JSON.
    internal static void AppendExternalJournalLine(string jsonLine, CancellationToken token = default) =>
        AppendJournalLine(JournalFile, jsonLine, token);

    private static bool IsJournalSharingViolation(IOException ex) =>
        OperatingSystem.IsWindows() && (ex.HResult & 0xFFFF) is 32 or 33;

    private static void AppendJournalLine(string filePath, string jsonLine, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(jsonLine) ||
            jsonLine.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Audit journal requires exactly one JSON line.", nameof(jsonLine));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        const int maxAttempts = 8;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var file = new FileStream(filePath, FileMode.Append, FileAccess.Write,
                    FileShare.Read, 4096, FileOptions.WriteThrough);
                using var writer = new StreamWriter(file);
                writer.WriteLine(jsonLine);
                writer.Flush();
                file.Flush(flushToDisk: true);
                return;
            }
            catch (IOException ex) when (IsJournalSharingViolation(ex) && attempt < maxAttempts - 1)
            {
                // Do not retry disk-full, access denied, corruption or other IO
                // faults: an unsaved Prepared record MUST prevent mutation.
                if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(25 * (attempt + 1))))
                    token.ThrowIfCancellationRequested();
            }
        }
    }

    private static void SaveRollback(BackupRecord record, CancellationToken token = default) =>
        AppendJournalLine(JournalFile, JsonSerializer.Serialize(record), token);

    // A successful registry readback is the source of truth even if writing
    // its second journal line fails (e.g. disk becomes full after preparation).
    // The original value is already durably saved in the Prepared record.
    // Never label a confirmed mutation as skipped simply for a receipt failure.
    private static void SaveCompletedReceipt(BackupRecord prepared)
    {
        try { SaveRollback(prepared with { Phase = "Applied" }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            LogService.Error("default-guard",
                "Registry was restored and verified, but the Applied journal receipt could not be saved: " +
                prepared.GroupId + "/" + prepared.Name, ex);
        }
    }
}
