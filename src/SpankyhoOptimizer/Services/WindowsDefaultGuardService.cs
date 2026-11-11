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

/// <summary>
/// User-approved, conservative repair of documented problematic registry overrides.
/// Never changes BCD, services, drivers, Defender, NIC, DNS, TCP or Optimizer-managed game/privacy switches.
/// Missing values are healthy: Windows supplies its own version-specific defaults.
/// </summary>
public static class WindowsDefaultGuardService
{
    public sealed record Group(string Id, string Title, string Details);
    public sealed record Finding(string GroupId, string Name, string Current, bool Approved, bool NeedsRepair);
    private sealed record Rule(string GroupId, RegistryHive Hive, string Key, string Name, string? BadValue, int? BadAtMost, bool AnyOverride);
    // Scope and anti-flap semantics must not be independently selected by callers.
    private enum RepairInvocation { PeriodicGuard, ExplicitFixer, ExplicitUacWorker }
    private sealed record BackupRecord(DateTime AtUtc, string GroupId, string Hive, string Key, string Name,
        string OldKind, string OldValue, string Action)
    {
        // A preparation entry is not proof that a registry write happened.
        // An Applied receipt with the same ID follows only after readback.
        public string OperationId { get; init; } = Guid.NewGuid().ToString("N");
        public string Phase { get; init; } = "Prepared";
    }

    private static readonly Group[] AvailableGroups =
    [
        new("app-shutdown", "Ukončování aplikací", "AutoEndTasks a příliš krátké HungApp/WaitToKillApp timeouty"),
        new("service-shutdown", "Ukončování služeb", "Hlídá WaitToKillServiceTimeout=5000 ms"),
        new("gpu-tdr", "GPU timeout", "Odstraní ruční TdrDelay, aby platil výchozí timeout ovladače"),
        new("fullscreen", "Fullscreen kompatibilita", "Odstraní jen GameDVR_FSEBehaviorMode=2"),
        new("gpu-priority", "GPU Priority – výchozí", "Sleduje MMCSS Games GPU Priority; požadovaná hodnota 8"),
        new("memory-defaults", "Paměť a izolace služeb", "Hlídá DisablePagingExecutive=0, LargeSystemCache=0 a extrémní SvcHostSplitThreshold=0xFFFFFFFF"),
        new("mmcss-defaults", "Výchozí MMCSS", "Hlídá SystemResponsiveness=20"),
        new("green-ethernet", "Green Ethernet vypnutý", "Kontrola Realtek 2.5GbE. Změna vyžaduje výslovné kliknutí a oprávnění správce; bez restartu adaptéru.")
    ];

    private static readonly Rule[] Rules =
    [
        new("app-shutdown", RegistryHive.CurrentUser, @"Control Panel\Desktop", "AutoEndTasks", "1", null, false),
        new("app-shutdown", RegistryHive.CurrentUser, @"Control Panel\Desktop", "HungAppTimeout", null, 2000, false),
        new("app-shutdown", RegistryHive.CurrentUser, @"Control Panel\Desktop", "WaitToKillAppTimeout", null, 2000, false),
        new("service-shutdown", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", null, 4999, false),
        new("gpu-tdr", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrDelay", null, null, true),
        new("fullscreen", RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", "2", null, false),
        new("gpu-priority", RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games",
            "GPU Priority", null, null, false),
        new("memory-defaults", RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "DisablePagingExecutive", null, null, false),
        new("memory-defaults", RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "LargeSystemCache", null, null, false),
        new("memory-defaults", RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control",
            "SvcHostSplitThresholdInKB", null, null, true),
        new("mmcss-defaults", RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            "SystemResponsiveness", null, null, false)
    ];

    private static readonly object Sync = new();
    // Allow the first few unexpected reversions to be restored at 5-minute
    // intervals, but pause continuous external write fights after three
    // successful automatic repairs in a rolling hour.
    private const int MaxAutoRestoresPerHour = 3;
    private static readonly TimeSpan AntiFlapWindow = TimeSpan.FromHours(1);
    private static readonly Dictionary<string, List<DateTime>> RecentRepairs = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ReportedThrottles = new(StringComparer.Ordinal);
    private static bool? _lastGreenEthernetEnabled;
    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpankyhoOptimizer");
    private static string ConsentFile => Path.Combine(Folder, "windows-default-guard.json");
    private static string JournalFile => Path.Combine(Folder, "windows-default-guard-repairs.jsonl");
    public static IReadOnlyList<Group> Groups => AvailableGroups;
    public static TimeSpan Interval => TimeSpan.FromMinutes(5);
    public const int ServiceShutdownDefaultMs = 5000;
    public const string ServiceShutdownDefaultText = "5000";

    public static bool CanRepairMachineRegistry()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static bool NeedsAdministrator(string groupId) =>
        Rules.Any(rule => rule.GroupId == groupId && rule.Hive == RegistryHive.LocalMachine);

    // All approvals are false for new installations. App user must choose EACH group.
    public static bool IsApproved(string groupId)
    {
        ValidateGroup(groupId);
        lock (Sync) return ReadApprovals().Contains(groupId);
    }

    public static void SetApproved(string groupId, bool approved)
    {
        ValidateGroup(groupId);
        lock (Sync) WriteApprovalAtomically(ConsentFile, groupId, approved);
    }

    // One deliberate UI toggle for the 11 approved registry rules. The NIC
    // observation consent is excluded: it belongs to the Network page.
    // All seven approvals must change in ONE atomic file transaction.
    public static void SetRegistryGuardEnabled(bool enabled)
    {
        lock (Sync)
            WriteRegistryApprovalsAtomically(ConsentFile, enabled);
    }

    public static int CountApprovedRegistryGroups()
    {
        var current = ReadApprovals();
        return AvailableGroups.Count(group =>
            group.Id != "green-ethernet" && current.Contains(group.Id));
    }

    // Internal path override exists ONLY for isolated consent smoke tests.
    // The same interprocess lease protects each group and the bulk toggle.
    private static void WriteRegistryApprovalsAtomically(string path, bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fileLock = AcquireApprovalFileLock(path + ".lock");
        var before = ReadApprovalsForWrite(path);
        var after = new HashSet<string>(before, StringComparer.Ordinal);
        foreach (var group in AvailableGroups)
        {
            if (group.Id == "green-ethernet") continue;
            if (enabled) after.Add(group.Id);
            else after.Remove(group.Id);
        }
        if (after.SetEquals(before)) return;
        string candidate = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            File.WriteAllText(candidate, JsonSerializer.Serialize(after.OrderBy(x => x).ToArray()));
            File.Move(candidate, path, overwrite: true);
            if (!ReadApprovalsForWrite(path).SetEquals(after))
                throw new IOException("Společné schválení registrů se nepodařilo uložit.");
        }
        finally { try { if (File.Exists(candidate)) File.Delete(candidate); } catch { } }
    }

    // The UI and an independently started process may save different approvals
    // at the same time. Serialize the read/modify/write across processes,
    // without blocking read-only monitoring on the consent file itself.
    private static void WriteApprovalAtomically(string path, string groupId, bool approved)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fileLock = AcquireApprovalFileLock(path + ".lock");
        // Do not overwrite a corrupt, temporarily unreadable or newer-version
        // consent file with an empty default. Monitoring remains fail-closed.
        var after = ReadApprovalsForWrite(path);
        if (approved == after.Contains(groupId)) return; // No-op: avoid disk writes.
        if (approved) after.Add(groupId);
        else after.Remove(groupId);
        string candidate = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            File.WriteAllText(candidate, JsonSerializer.Serialize(after.OrderBy(x => x).ToArray()));
            File.Move(candidate, path, overwrite: true);
            if (!ReadApprovalsForWrite(path).SetEquals(after))
                throw new IOException("Schválení automatických oprav se nepodařilo bezpečně uložit.");
        }
        finally { try { if (File.Exists(candidate)) File.Delete(candidate); } catch { } }
    }

    private static FileStream AcquireApprovalFileLock(string lockPath,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        const int maxAttempts = 12;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException ex) when (IsJournalSharingViolation(ex) && attempt < maxAttempts - 1)
            {
                int waitMs = 20 * (attempt + 1);
                if (token.CanBeCanceled)
                {
                    if (token.WaitHandle.WaitOne(waitMs))
                        token.ThrowIfCancellationRequested();
                }
                else Thread.Sleep(waitMs);
            }
        }
        throw new IOException("Současný zápis schválení nelze bezpečně dokončit.");
    }

    // AI Runtime shows registry evidence only; network/NIC monitoring belongs
    // to the Network page. Background Guard uses the default full scan.
    public static IReadOnlyList<Finding> ScanReadOnly(CancellationToken token = default,
        bool includeNetworkObservation = true)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return [];
        var approved = ReadApprovals();
        var findings = new List<Finding>(Rules.Length);
        foreach (var rule in Rules)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using RegistryKey hive = RegistryKey.OpenBaseKey(rule.Hive, RegistryView.Registry64);
                using RegistryKey? key = hive.OpenSubKey(rule.Key, writable: false);
                object? current = key?.GetValue(rule.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                RegistryValueKind? kind = key?.GetValueKindSafe(rule.Name);
                bool knownType = key != null && (kind == null || IsExpectedKind(rule, kind.Value));
                bool match = knownType && ShouldRestore(rule, current, kind);
                string currentLabel = FormatReadOnlyValue(rule, key != null, current, kind);
                findings.Add(new(rule.GroupId, rule.Name,
                    currentLabel, approved.Contains(rule.GroupId), match));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                // Unknown is not the same as a confirmed change; never repair on failed detection.
                LogService.Info("default-guard", "Scan unavailable: " + rule.Name + " " + e.GetType().Name);
                findings.Add(new(rule.GroupId, rule.Name, "(nezjištěno)", approved.Contains(rule.GroupId), false));
            }
        }
        token.ThrowIfCancellationRequested();
        if (!includeNetworkObservation) return findings;
        try
        {
            string? property = NativeNicTuningService.ReadRealtekGreenEthernetStatus(token);
            token.ThrowIfCancellationRequested();
            string state = property ?? "(nepodporováno)";
            bool wrong = property == "Enabled";
            findings.Add(new("green-ethernet", "Green Ethernet", state,
                approved.Contains("green-ethernet"), wrong));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogService.Info("default-guard", "Green Ethernet read unavailable: " + ex.GetType().Name);
            findings.Add(new("green-ethernet", "Green Ethernet", "(nezjištěno)",
                approved.Contains("green-ethernet"), false));
        }
        return findings;
    }

    // The AI page summarizes ALL registry rules that Default Guard actually
    // monitors; its explicit green-NIC observation is handled separately.
    internal static string DescribeAiWindowsRegistryHealth(IReadOnlyList<Finding> findings,
        bool elevated)
    {
        string[] targets = Rules.Select(rule => rule.Name).ToArray();
        var rows = findings.Where(f => targets.Contains(f.Name, StringComparer.Ordinal))
            .ToDictionary(f => f.Name, StringComparer.Ordinal);
        var detail = new List<string>(targets.Length);
        int healthy = 0, drifted = 0, unknown = 0;
        foreach (string name in targets)
        {
            string desired = name switch
            {
                "AutoEndTasks" => "bez agresivního AutoEndTasks=1",
                "HungAppTimeout" => "bez příliš krátkého timeoutu (≤ 2000 ms)",
                "WaitToKillAppTimeout" => "bez příliš krátkého timeoutu (≤ 2000 ms)",
                "WaitToKillServiceTimeout" => "alespoň 5000 ms",
                "TdrDelay" => "Windows Default (bez ručního přepisu)",
                "GameDVR_FSEBehaviorMode" => "bez vynucené hodnoty 2",
                "GPU Priority" => "8 (MMCSS Games)",
                "DisablePagingExecutive" => "0 nebo výchozí Windows",
                "LargeSystemCache" => "0 nebo výchozí Windows",
                "SvcHostSplitThresholdInKB" => "bez extrémní hodnoty 0xFFFFFFFF",
                "SystemResponsiveness" => "20 (MMCSS)",
                _ => "bez nevhodného přepisu"
            };
            if (!rows.TryGetValue(name, out var item) ||
                item.Current is "(nezjištěno)" or "(nepodporováno)" ||
                item.Current.StartsWith("(nepodporovaný typ:", StringComparison.Ordinal))
            {
                unknown++;
                detail.Add(name + ": neověřeno");
                continue;
            }
            if (!item.NeedsRepair)
            {
                healthy++;
                detail.Add(name + ": OK (" + desired + ")");
                continue;
            }
            drifted++;
            // HKCU rules are repairable without elevation. Only HKLM
            // approval requires an already elevated process or explicit UAC.
            bool canRepair = elevated || Rules.Any(rule =>
                rule.Name == name && rule.Hive == RegistryHive.CurrentUser);
            bool paused = false;
            if (item.Approved && canRepair)
            {
                string tokenKey = item.GroupId + "/" + item.Name;
                lock (Sync)
                    paused = ShouldDeferRepeat(false,
                        RecentRepairs.TryGetValue(tokenKey, out var recent) ? recent : null,
                        DateTime.UtcNow);
            }
            string action = DescribeAutoRestoreAction(item.Approved, canRepair, paused);
            detail.Add(name + ": odchylka " + item.Current + " → " + desired + " (" + action + ")");
        }
        // Registry Rules cover narrow HKCU and HKLM overrides. HKLM repairs
        // require an already elevated process or explicit UAC; Green Ethernet
        // is monitor-only and may NEVER reconfigure the active NIC on a timer.
        string ethernet = "Green Ethernet: stav neověřen; požadováno vypnuto (ruční UAC u PC)";
        Finding? green = findings.FirstOrDefault(f => f.GroupId == "green-ethernet");
        if (green != null)
        {
            ethernet = green.Current switch
            {
                "Disabled" => "Green Ethernet: vypnuto (tvoje výjimka, OK)",
                "Enabled" => "Green Ethernet: zapnuto → vypnout ručně přes UAC přímo u PC",
                _ => "Green Ethernet: neověřeno (bez automatického síťového zásahu)"
            };
        }
        return "AI dohled nad registry (pravidlový, bez cloudového modelu): " +
            healthy + "/" + targets.Length + " v pořádku, " + drifted + " odchylek, " +
            unknown + " neověřeno.\n" +
            "Stav na AI stránce je jednorázový snímek při otevření. " +
            "Samostatný Windows Default Guard provádí kontrolu každých 5 minut " +
            "při běžícím aktuálním Optimizeru; opravy HKLM vyžadují správce nebo ruční UAC.\n" +
            string.Join("\n", detail) + "\n" + ethernet;
    }

    private static string DescribeAutoRestoreAction(bool approved, bool elevated, bool paused) =>
        !approved ? "bez povolení opravy" :
        !elevated ? "čeká na správce / ruční UAC" :
        paused ? "automatická obnova dočasně pozastavena (3 automatické opravy/hod)" :
        "automatická obnova povolena";

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Off the UI startup path; no worker/process is created when all groups are off.
            await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    // No Task allocation or registry/NIC work when every consent is off.
                    // Any later approval is picked up on the next five-minute tick.
                    var approved = ReadApprovals();
                    if (approved.Count > 0)
                        await Task.Run(() => RepairScopedOnce(approved,
                            RepairInvocation.PeriodicGuard, cancellationToken), cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // A transient scan/provider failure must not permanently stop
                    // monitoring. Retry at the normal 5-minute interval, not in a loop.
                    LogService.Error("default-guard", "Scan deferred until next cycle", ex);
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Error("default-guard", "Windows default monitoring stopped", ex); }
    }

    public static string[] ApprovedMachineGroups() =>
        ReadApprovals().Where(NeedsAdministrator).OrderBy(id => id, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Runs ONLY from the explicitly user-invoked, UAC-elevated worker.
    /// It never changes HKCU, NIC, BCD, services, Defender or an unapproved group.
    /// The group IDs are an allowlisted snapshot of the user's Settings consent.
    /// </summary>
    internal static DefaultGuardMachineRepairResult RepairApprovedMachineOnce(
        DefaultGuardMachineRepairRequest request, CancellationToken token)
    {
        if (!CanRepairMachineRegistry())
            throw new UnauthorizedAccessException("Systémové registry vyžadují oprávnění správce.");
        var allowed = ValidateMachineGroupRequest(request);
        if (allowed.Count == 0) return new(0, 0, 0, 0, 0);
        MachineScan before = ScanMachineStates(allowed, token);
        int applied = RepairScopedOnce(allowed, RepairInvocation.ExplicitUacWorker, token);
        MachineScan after = ScanMachineStates(allowed, token);
        return new(before.Drift, after.Drift, after.Tracked, after.Unverified, applied);
    }

    private static HashSet<string> ValidateMachineGroupRequest(
        DefaultGuardMachineRepairRequest request)
    {
        if (request.Groups == null || request.Groups.Length > AvailableGroups.Length ||
            request.Groups.Any(id => string.IsNullOrWhiteSpace(id) ||
                                     !AvailableGroups.Any(group => group.Id == id) ||
                                     !NeedsAdministrator(id)))
            throw new ArgumentException("Neplatné skupiny oprav systémového registru.");
        return new HashSet<string>(request.Groups, StringComparer.Ordinal);
    }

    private sealed record MachineScan(int Drift, int Unverified, int Tracked);
    private enum RegistryAssessment { Healthy, Drift, Unverified }

    // Pure decision, independent of Windows: unknown key/data type must never
    // be interpreted as a successful default restoration.
    private static RegistryAssessment AssessMachineValue(Rule rule, bool keyAccessible,
        object? value, RegistryValueKind? kind)
    {
        if (!keyAccessible) return RegistryAssessment.Unverified;
        if (value == null) return RegistryAssessment.Healthy; // Absent value = Windows default.
        if (!kind.HasValue || !IsExpectedKind(rule, kind.Value))
            return RegistryAssessment.Unverified;
        return ShouldRestore(rule, value, kind)
            ? RegistryAssessment.Drift : RegistryAssessment.Healthy;
    }

    private static MachineScan ScanMachineStates(HashSet<string> allowed, CancellationToken token)
    {
        int drift = 0, unverified = 0, tracked = 0;
        foreach (var rule in Rules)
        {
            if (rule.Hive != RegistryHive.LocalMachine || !allowed.Contains(rule.GroupId))
                continue;
            tracked++;
            token.ThrowIfCancellationRequested();
            RegistryAssessment status;
            try
            {
                using var hive = RegistryKey.OpenBaseKey(rule.Hive, RegistryView.Registry64);
                using var key = hive.OpenSubKey(rule.Key, writable: false);
                object? value = key?.GetValue(rule.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                RegistryValueKind? kind = key?.GetValueKindSafe(rule.Name);
                status = AssessMachineValue(rule, key != null, value, kind);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or ArgumentException)
            {
                LogService.Info("default-guard", "Unverified machine value: " +
                    rule.Name + " (" + ex.GetType().Name + ")");
                status = RegistryAssessment.Unverified;
            }
            if (status == RegistryAssessment.Drift) drift++;
            else if (status == RegistryAssessment.Unverified) unverified++;
        }
        return new(drift, unverified, tracked);
    }

    // Invoked by the explicit Fixer action, not by the periodic timer. Keep
    // current consent/revocation checks, but do not exhaust the auto-only budget.
    public static void RepairApprovedOnce(CancellationToken token = default)
    {
        _ = RepairScopedOnce(ReadApprovals(), RepairInvocation.ExplicitFixer, token);
    }

    // Fixer's older RAM-sized svchost override is intentionally outside the
    // periodic guard's single known-bad 0xFFFFFFFF rule. Preserve Fixer's
    // existing narrow heuristic, but use the SAME consent/journal/readback
    // safety contract instead of deleting the value without backup.
    internal static bool IsLegacyRamSizedSvcHostOverride(int valueKb, int installedRamKb) =>
        valueKb > 0 && installedRamKb > 0 &&
        Math.Abs((long)valueKb - installedRamKb) <= Math.Max(65536L, installedRamKb / 20L);

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

    /// <summary>
    /// Isolated filesystem-only regression. Forces a real file-sharing collision,
    /// then concurrent appends. Never accesses the live repair journal or registry.
    /// Cleans up its own temporary folder in all cases.
    /// </summary>
    public static string RunJournalContentionSmoke()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "SpankyGuardJournalSmoke-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "isolated.jsonl");
        Directory.CreateDirectory(directory);
        try
        {
            Task blockedWriter;
            using (var existing = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read))
            {
                using var starting = new ManualResetEventSlim(false);
                blockedWriter = Task.Run(() =>
                {
                    starting.Set();
                    AppendJournalLine(path, "{\"index\":0}", CancellationToken.None);
                });
                if (!starting.Wait(TimeSpan.FromSeconds(2)))
                    throw new IOException("Journal contention test writer did not start.");
                Thread.Sleep(100); // Force a live read-only-shared writer collision.
            }
            blockedWriter.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();

            // Cancelled PREPARE must not create a journal record, even without
            // a sharing collision; the actual repair checks cancellation
            // immediately after preparing and before touching the registry.
            string cancelledPath = Path.Combine(directory, "cancelled-prepared.jsonl");
            using (var alreadyCancelled = new CancellationTokenSource())
            {
                alreadyCancelled.Cancel();
                bool stopped = false;
                try { AppendJournalLine(cancelledPath, "{\"Phase\":\"Prepared\"}", alreadyCancelled.Token); }
                catch (OperationCanceledException) { stopped = true; }
                if (!stopped || File.Exists(cancelledPath))
                    throw new IOException("Cancelled Prepared journal unexpectedly wrote a record.");
            }

            var parallel = Enumerable.Range(1, 8).Select(i => Task.Run(() =>
                AppendJournalLine(path,
                    JsonSerializer.Serialize(new { index = i }), CancellationToken.None))).ToArray();
            Task.WhenAll(parallel).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

            // Cancellation while another process holds the journal must NOT
            // append a late line after the user cancels the operation.
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            using (var cancelled = new CancellationTokenSource())
            using (var started = new ManualResetEventSlim(false))
            {
                Task attempt = Task.Run(() =>
                {
                    started.Set();
                    AppendJournalLine(path, "{\"index\":999}", cancelled.Token);
                });
                if (!started.Wait(TimeSpan.FromSeconds(2)))
                    throw new IOException("Cancelled journal writer did not start.");
                Thread.Sleep(60);
                cancelled.Cancel();
                bool rejected = false;
                try { attempt.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { rejected = true; }
                if (!rejected)
                    throw new IOException("Cancelled journal write unexpectedly completed.");
            }

            string[] lines = File.ReadAllLines(path);
            if (lines.Length != 9)
                throw new IOException("Journal contention test lost or duplicated entries.");
            var ids = new HashSet<int>();
            foreach (string line in lines)
            {
                using var json = JsonDocument.Parse(line);
                if (!ids.Add(json.RootElement.GetProperty("index").GetInt32()))
                    throw new IOException("Journal contention test produced a duplicate entry.");
            }
            if (ids.Count != 9 || !ids.Contains(0) || !ids.Contains(8))
                throw new IOException("Journal contention test did not preserve all writers.");

            // Regression for independent Guard processes saving different
            // approvals concurrently. Only a disposable test file is used.
            string consentPath = Path.Combine(directory, "isolated-consents.json");
            var consentSaves = AvailableGroups.Select(g => Task.Run(() =>
                WriteApprovalAtomically(consentPath, g.Id, approved: true))).ToArray();
            Task.WhenAll(consentSaves).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var approvedGroups = ReadApprovals(consentPath);
            if (approvedGroups.Count != AvailableGroups.Length ||
                AvailableGroups.Any(group => !approvedGroups.Contains(group.Id)))
                throw new IOException("Concurrent consent writes lost an approved group.");
            WriteApprovalAtomically(consentPath, "gpu-tdr", approved: false);
            approvedGroups = ReadApprovals(consentPath);
            if (approvedGroups.Count != AvailableGroups.Length - 1 || approvedGroups.Contains("gpu-tdr") ||
                Directory.GetFiles(directory, "isolated-consents.json.*.new").Length != 0)
                throw new IOException("Atomic consent revocation or temporary cleanup failed.");

            // An in-flight repair holds the same exclusive lease as the user
            // consent writer. Simulate a journaled repair with a disposable
            // consent file: an independent writer must NOT revoke its approval
            // in between final verification and completion of the operation.
            WriteApprovalAtomically(consentPath, "gpu-tdr", approved: true);
            Task revoke;
            using (var repairLease = AcquireApprovalFileLock(consentPath + ".lock"))
            using (var started = new ManualResetEventSlim(false))
            {
                if (!ReadApprovals(consentPath).Contains("gpu-tdr"))
                    throw new IOException("Repair consent precheck unexpectedly missing.");
                revoke = Task.Run(() =>
                {
                    started.Set();
                    WriteApprovalAtomically(consentPath, "gpu-tdr", approved: false);
                });
                if (!started.Wait(TimeSpan.FromSeconds(2)))
                    throw new IOException("Concurrent approval revoker did not start.");
                Thread.Sleep(80);
                if (revoke.IsCompleted || !ReadApprovals(consentPath).Contains("gpu-tdr"))
                    throw new IOException("Approval was revoked during simulated repair lease.");
            }
            revoke.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            if (ReadApprovals(consentPath).Contains("gpu-tdr") ||
                ReadApprovals(consentPath).Count != AvailableGroups.Length - 1)
                throw new IOException("Revocation was not applied after repair lease release.");

            // Navigation/cycle cancellation must abort even when an unrelated
            // process is holding the approval file lease. A cancelled Guard
            // must not wait through all twelve contention retries.
            string cancelLockPath = Path.Combine(directory, "cancelled-approval.lock");
            using (var blocker = AcquireApprovalFileLock(cancelLockPath))
            using (var cancellation = new CancellationTokenSource())
            using (var entered = new ManualResetEventSlim(false))
            {
                var waiter = Task.Run(() =>
                {
                    entered.Set();
                    try
                    {
                        using var unexpected = AcquireApprovalFileLock(
                            cancelLockPath, cancellation.Token);
                        throw new IOException("Cancelled Guard unexpectedly acquired consent lease.");
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                });
                if (!entered.Wait(TimeSpan.FromSeconds(2)))
                    throw new IOException("Guard lock cancellation regression did not start.");
                Thread.Sleep(60);
                cancellation.Cancel();
                waiter.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            // New single-switch Registry Guard must save all seven registry
            // approvals atomically without touching the Network/NIC consent.
            string registryTogglePath = Path.Combine(directory, "registry-master.json");
            WriteApprovalAtomically(registryTogglePath, "green-ethernet", approved: true);
            WriteRegistryApprovalsAtomically(registryTogglePath, enabled: true);
            var allEnabled = ReadApprovalsForWrite(registryTogglePath);
            if (allEnabled.Count != AvailableGroups.Length ||
                AvailableGroups.Any(group => !allEnabled.Contains(group.Id)))
                throw new IOException("Registry master failed to atomically enable seven approvals.");
            WriteRegistryApprovalsAtomically(registryTogglePath, enabled: false);
            var allDisabled = ReadApprovalsForWrite(registryTogglePath);
            if (allDisabled.Count != 1 || !allDisabled.Contains("green-ethernet"))
                throw new IOException("Registry master removed NIC consent or left an approved registry.");
            WriteRegistryApprovalsAtomically(registryTogglePath, enabled: false);
            if (Directory.GetFiles(directory, "registry-master.json.*.new").Length != 0)
                throw new IOException("Registry master left temporary files behind.");
            File.WriteAllText(registryTogglePath, "{invalid json");
            bool refusedBulk = false;
            try { WriteRegistryApprovalsAtomically(registryTogglePath, enabled: true); }
            catch (InvalidDataException) { refusedBulk = true; }
            if (!refusedBulk || File.ReadAllText(registryTogglePath) != "{invalid json")
                throw new IOException("Registry master overwrote corrupt existing approvals.");

            // Corrupted JSON, explicit null and a future-version group must be
            // visible to the read-only monitor as unapproved, but never be
            // overwritten by a seemingly fresh/empty consent write.
            string unsafePath = Path.Combine(directory, "unreadable-consents.json");
            foreach (string unsafeContent in new[]
            {
                "{not valid json", "null", "[\"gpu-tdr\",\"future-version-group\"]"
            })
            {
                File.WriteAllText(unsafePath, unsafeContent);
                bool rejected = false;
                try { WriteApprovalAtomically(unsafePath, "service-shutdown", approved: true); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected || File.ReadAllText(unsafePath) != unsafeContent ||
                    Directory.GetFiles(directory, "unreadable-consents.json.*.new").Length != 0)
                    throw new IOException("Invalid or newer-version consent was overwritten.");
            }
            if (ReadApprovals(unsafePath).Contains("future-version-group"))
                throw new IOException("Read-only consent granted an unknown future group.");
            // Another process can temporarily deny reads of the actual JSON.
            // A failed read must not be mistaken for the first-ever approval.
            const string lockedContent = "[\"gpu-tdr\"]";
            File.WriteAllText(unsafePath, lockedContent);
            using (var unavailable = new FileStream(unsafePath, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None))
            {
                bool refused = false;
                try { WriteApprovalAtomically(unsafePath, "service-shutdown", approved: true); }
                catch (IOException) { refused = true; }
                if (!refused)
                    throw new IOException("Locked existing consent was treated as missing.");
            }
            if (File.ReadAllText(unsafePath) != lockedContent)
                throw new IOException("Temporarily unreadable consent was modified.");
            return "DEFAULT_GUARD_JOURNAL_CONTENTION_OK entries=9 collisions=protected consentWrites=8/8 invalidConsentPreserved=4 liveRegistryWrites=0";
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Info("default-guard", "Temporary journal test cleanup skipped: " +
                    ex.GetType().Name);
            }
        }
    }

    // Pure regression: synthetic objects only, no registry, filesystem or services.
    public static string RunPolicySmoke()
    {
        // A cancelled AI navigation must skip even the first registry/NIC probe.
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            bool stopped = false;
            try { _ = ScanReadOnly(cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            if (!stopped)
                throw new InvalidOperationException("Cancelled AI registry scan did not stop before probing.");
            stopped = false;
            try { _ = NativeNicTuningService.ReadRealtekGreenEthernetStatus(cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            if (!stopped)
                throw new InvalidOperationException("Cancelled Green Ethernet probe did not stop before discovery.");
        }
        var byName = Rules.ToDictionary(r => r.Name, StringComparer.Ordinal);
        if (ServiceShutdownDefaultMs != 5000 ||
            ServiceShutdownDefaultText != ServiceShutdownDefaultMs.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Windows shutdown timeout default consistency failed.");
        if (!ShouldRestore(byName["AutoEndTasks"], "1", RegistryValueKind.String) ||
            !ShouldRestore(byName["AutoEndTasks"], "0", RegistryValueKind.String) ||
            ShouldRestore(byName["AutoEndTasks"], null, null) ||
            ShouldRestore(byName["AutoEndTasks"], 1, RegistryValueKind.DWord) ||
            ShouldRestore(byName["HungAppTimeout"], new byte[] { 1, 2 }, RegistryValueKind.Binary) ||
            !ShouldRestore(byName["HungAppTimeout"], "2000", RegistryValueKind.String) ||
            !ShouldRestore(byName["HungAppTimeout"], "10000", RegistryValueKind.String) ||
            !ShouldRestore(byName["WaitToKillServiceTimeout"], "2000", RegistryValueKind.String) ||
            ShouldRestore(byName["WaitToKillServiceTimeout"], "5000", RegistryValueKind.String) ||
            ShouldRestore(byName["WaitToKillServiceTimeout"], 2000, RegistryValueKind.DWord) ||
            !ShouldRestore(byName["TdrDelay"], 10, RegistryValueKind.DWord) ||
            ShouldRestore(byName["TdrDelay"], null, null) ||
            ShouldRestore(byName["TdrDelay"], "10", RegistryValueKind.String) ||
            ShouldRestore(byName["TdrDelay"], new byte[] { 1, 2 }, RegistryValueKind.Binary) ||
            !ShouldRestore(byName["GameDVR_FSEBehaviorMode"], 2, RegistryValueKind.DWord) ||
            ShouldRestore(byName["GameDVR_FSEBehaviorMode"], "2", RegistryValueKind.String) ||
            ShouldRestore(byName["GameDVR_FSEBehaviorMode"], 0, RegistryValueKind.DWord) ||
            ShouldRestore(byName["GPU Priority"], 8, RegistryValueKind.DWord) ||
            !ShouldRestore(byName["GPU Priority"], 16, RegistryValueKind.DWord) ||
            ShouldRestore(byName["DisablePagingExecutive"], 0, RegistryValueKind.DWord) ||
            !ShouldRestore(byName["DisablePagingExecutive"], 1, RegistryValueKind.DWord) ||
            ShouldRestore(byName["LargeSystemCache"], 0, RegistryValueKind.DWord) ||
            !ShouldRestore(byName["LargeSystemCache"], 1, RegistryValueKind.DWord) ||
            ShouldRestore(byName["SystemResponsiveness"], 20, RegistryValueKind.DWord) ||
            !ShouldRestore(byName["SystemResponsiveness"], 10, RegistryValueKind.DWord) ||
            !ShouldRestore(byName["SvcHostSplitThresholdInKB"], -1, RegistryValueKind.DWord) ||
            ShouldRestore(byName["SvcHostSplitThresholdInKB"], 0x380000, RegistryValueKind.DWord) ||
            ShouldRestore(byName["SvcHostSplitThresholdInKB"], null, null) ||
            ShouldRestore(byName["SvcHostSplitThresholdInKB"], "4294967295", RegistryValueKind.String))
            throw new InvalidOperationException("Windows default guard decision policy failed.");

        // Read-only UI must not mislabel an inaccessible registry key as default.
        if (FormatReadOnlyValue(byName["SvcHostSplitThresholdInKB"], false, null, null) != "(nezjištěno)" ||
            FormatReadOnlyValue(byName["SvcHostSplitThresholdInKB"], true, null, null) != "(Windows default)" ||
            FormatReadOnlyValue(byName["SvcHostSplitThresholdInKB"], true, -1, RegistryValueKind.DWord) != "0xFFFFFFFF" ||
            FormatReadOnlyValue(byName["SvcHostSplitThresholdInKB"], true, 0x380000, RegistryValueKind.DWord) != "0x00380000" ||
            !FormatReadOnlyValue(byName["TdrDelay"], true, "10", RegistryValueKind.String).StartsWith("(nepodporovaný typ:", StringComparison.Ordinal))
            throw new InvalidOperationException("Default Guard read-only state classification failed.");

        // Service-host isolation override must be REMOVED (Windows-managed
        // default), never rewritten as DWORD 0 by the memory-defaults branch.
        if (!RestoresByRemovingValue(byName["SvcHostSplitThresholdInKB"]) ||
            RestoresByRemovingValue(byName["LargeSystemCache"]) ||
            RestoresByRemovingValue(byName["DisablePagingExecutive"]) ||
            !RestoresByRemovingValue(byName["TdrDelay"]) ||
            !RestoresByRemovingValue(byName["AutoEndTasks"]))
            throw new InvalidOperationException("Default Guard delete-vs-set restore policy failed.");

        // Preserve only the old, narrow RAM-size heuristic for Fixer. It
        // must never match 0xFFFFFFFF, unrelated values or unknown RAM size.
        if (!IsLegacyRamSizedSvcHostOverride(16 * 1024 * 1024, 16 * 1024 * 1024) ||
            !IsLegacyRamSizedSvcHostOverride(16 * 1024 * 1024 + 32768, 16 * 1024 * 1024) ||
            IsLegacyRamSizedSvcHostOverride(-1, 16 * 1024 * 1024) ||
            IsLegacyRamSizedSvcHostOverride(1024, 16 * 1024 * 1024) ||
            IsLegacyRamSizedSvcHostOverride(16 * 1024 * 1024, 0))
            throw new InvalidOperationException("Fixer legacy svchost recovery scope failed.");

        // Directly confirmed writes, not the before/after registry drift
        // delta, are the only honest count of changes attributable to us.
        var simulatedResult = new DefaultGuardMachineRepairResult(3, 0, 5, 0, 1);
        if (simulatedResult.AppliedChanges != 1 ||
            simulatedResult.DetectedBefore - simulatedResult.RemainingAfter == simulatedResult.AppliedChanges)
            throw new InvalidOperationException("Default Guard confirmed-write result contract failed.");

        // One or two restores in a rolling hour must not suspend ordinary
        // five-minute monitoring. Only a verified third restore pauses it.
        DateTime at = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        DateTime[] once = [at.AddMinutes(-5)];
        DateTime[] twice = [at.AddMinutes(-35), at.AddMinutes(-5)];
        DateTime[] thrice = [at.AddMinutes(-55), at.AddMinutes(-35), at.AddMinutes(-5)];
        DateTime[] expired = [at.AddHours(-2), at.AddHours(-2), at.AddMinutes(-5)];
        if (ShouldDeferRepeat(false, once, at) ||
            ShouldDeferRepeat(false, twice, at) ||
            !ShouldDeferRepeat(false, thrice, at) ||
            ShouldDeferRepeat(true, thrice, at) ||
            ShouldDeferRepeat(false, expired, at) ||
            ShouldDeferRepeat(false, null, at) ||
            !ShouldReportGreenChange(null, true) ||
            ShouldReportGreenChange(true, true) ||
            !ShouldReportGreenChange(true, false))
            throw new InvalidOperationException("Default Guard multi-restoration anti-flap or NIC log deduplication failed.");

        // Explicit Fixer must bypass auto-throttling WITHOUT bypassing the
        // current HKCU/HKLM consent check. UAC worker alone gets the validated
        // machine-only button-click snapshot; timer alone consumes auto budget.
        if (InvocationPolicy(RepairInvocation.PeriodicGuard) != (false, true) ||
            InvocationPolicy(RepairInvocation.ExplicitFixer) != (false, false) ||
            InvocationPolicy(RepairInvocation.ExplicitUacWorker) != (true, false))
            throw new InvalidOperationException("Default Guard Fixer/manual/periodic invocation policy failed.");

        // A manually confirmed repair (including Fixer) must never consume the
        // periodic Guard's three-auto-repairs/hour allowance.
        const string smokeBudgetKey = "policy-smoke/automatic-budget";
        lock (Sync)
        {
            try
            {
                RecordSuccessfulRepair(smokeBudgetKey, at, automatic: false);
                if (RecentRepairs.ContainsKey(smokeBudgetKey))
                    throw new InvalidOperationException("Manual repair consumed auto-restore budget.");
                RecordSuccessfulRepair(smokeBudgetKey, at, automatic: true);
                if (!RecentRepairs.TryGetValue(smokeBudgetKey, out var budget) || budget.Count != 1 ||
                    ShouldDeferRepeat(false, budget, at))
                    throw new InvalidOperationException("Automatic restore budget accounting failed.");
            }
            finally { RecentRepairs.Remove(smokeBudgetKey); }
        }
        if (!DescribeAutoRestoreAction(true, true, true).Contains("dočasně pozastavena", StringComparison.Ordinal) ||
            DescribeAutoRestoreAction(true, true, false) != "automatická obnova povolena" ||
            !DescribeAutoRestoreAction(true, false, true).Contains("čeká na správce", StringComparison.Ordinal) ||
            DescribeAutoRestoreAction(false, true, true) != "bez povolení opravy")
            throw new InvalidOperationException("AI anti-flap pause explanation is misleading.");

        // Journal preparation is NOT evidence of a committed repair. The
        // outcome receipt has the same OperationId and arrives after readback.
        var prepared = new BackupRecord(at, "gpu-tdr", "LocalMachine",
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrDelay",
            RegistryValueKind.DWord.ToString(), "10", "Remove override");
        var appliedReceipt = prepared with { Phase = "Applied" };
        if (prepared.Phase != "Prepared" || appliedReceipt.Phase != "Applied" ||
            prepared.OperationId != appliedReceipt.OperationId ||
            prepared.OldValue != appliedReceipt.OldValue ||
            !JsonSerializer.Serialize(appliedReceipt).Contains("\"OperationId\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Default Guard write-ahead journal receipt contract failed.");

        // Pure machine readback tests: absence is healthy, unexpected types
        // and inaccessible keys are UNVERIFIED rather than successful repairs.
        if (AssessMachineValue(byName["TdrDelay"], true, null, null) != RegistryAssessment.Healthy ||
            AssessMachineValue(byName["TdrDelay"], true, 10, RegistryValueKind.DWord) != RegistryAssessment.Drift ||
            AssessMachineValue(byName["TdrDelay"], true, "10", RegistryValueKind.String) != RegistryAssessment.Unverified ||
            AssessMachineValue(byName["TdrDelay"], false, null, null) != RegistryAssessment.Unverified ||
            AssessMachineValue(byName["WaitToKillServiceTimeout"], true, "5000", RegistryValueKind.String) != RegistryAssessment.Healthy ||
            AssessMachineValue(byName["WaitToKillServiceTimeout"], true, "2000", RegistryValueKind.String) != RegistryAssessment.Drift ||
            AssessMachineValue(byName["GPU Priority"], true, new byte[] { 1, 2 }, RegistryValueKind.Binary) != RegistryAssessment.Unverified)
            throw new InvalidOperationException("Default Guard unknown-status readback classification failed.");

        // The AI dashboard must count ALL actual Guard rules, not just the
        // original four showcased values. Missing findings are unverified,
        // never counted as healthy; Green Ethernet is a separate read-only NIC.
        if (Rules.Length != 11 || AvailableGroups.Length != 8 ||
            Rules.Any(rule => !AvailableGroups.Any(g => g.Id == rule.GroupId)))
            throw new InvalidOperationException("Registry dashboard group/rule inventory drifted.");
        var aiEvidence = DescribeAiWindowsRegistryHealth(
        [
            new("gpu-tdr", "TdrDelay", "10", true, true),
            new("mmcss-defaults", "SystemResponsiveness", "10", true, true),
            new("service-shutdown", "WaitToKillServiceTimeout", "2000", true, true),
            new("memory-defaults", "SvcHostSplitThresholdInKB", "(nezjištěno)", true, false)
        ], elevated: false);
        if (!aiEvidence.Contains("0/11 v pořádku, 3 odchylek, 8 neověřeno", StringComparison.Ordinal) ||
            !aiEvidence.Contains("Green Ethernet: stav neověřen", StringComparison.Ordinal) ||
            !aiEvidence.Contains("čeká na správce / ruční UAC", StringComparison.Ordinal) ||
            !aiEvidence.Contains("SvcHostSplitThresholdInKB: neověřeno", StringComparison.Ordinal))
            throw new InvalidOperationException("AI Windows registry incomplete-evidence policy failed.");
        var completeEvidence = DescribeAiWindowsRegistryHealth(
            Rules.Select(rule => new Finding(rule.GroupId, rule.Name,
                "(výchozí)", false, false)).ToArray(), elevated: false);
        if (!completeEvidence.Contains("11/11 v pořádku, 0 odchylek, 0 neověřeno", StringComparison.Ordinal) ||
            Rules.Any(rule => !completeEvidence.Contains(rule.Name + ": OK", StringComparison.Ordinal)))
            throw new InvalidOperationException("AI Windows registry complete-evidence policy failed.");
        // Without elevation, approved HKCU repairs are actionable; HKLM
        // repairs must still ask for administrator privileges.
        var mixedPrivilegeEvidence = DescribeAiWindowsRegistryHealth(
        [
            new("app-shutdown", "AutoEndTasks", "1", true, true),
            new("gpu-tdr", "TdrDelay", "10", true, true)
        ], elevated: false);
        if (!mixedPrivilegeEvidence.Contains("AutoEndTasks: odchylka 1 → bez agresivního AutoEndTasks=1 (automatická obnova povolena)", StringComparison.Ordinal) ||
            !mixedPrivilegeEvidence.Contains("TdrDelay: odchylka 10 → Windows Default (bez ručního přepisu) (čeká na správce / ruční UAC)", StringComparison.Ordinal))
            throw new InvalidOperationException("AI registry HKCU/HKLM permission explanation failed.");

        // A validated explicit UAC snapshot must not depend on which user's
        // LocalAppData contains the auto-monitor consent file. Ordinary timer
        // repairs MUST stop immediately if the current approval was revoked.
        var requested = new HashSet<string>(StringComparer.Ordinal) { "gpu-tdr" };
        var revoked = new HashSet<string>(StringComparer.Ordinal);
        if (!ShouldApplyWithConsent(true, "gpu-tdr", requested, revoked) ||
            ShouldApplyWithConsent(false, "gpu-tdr", requested, revoked) ||
            !ShouldApplyWithConsent(false, "gpu-tdr", requested, requested) ||
            ShouldApplyWithConsent(true, "mmcss-defaults", requested, requested))
            throw new InvalidOperationException("Default Guard approval snapshot/revocation rules failed.");

        var machine = ValidateMachineGroupRequest(
            new DefaultGuardMachineRepairRequest(["gpu-tdr", "gpu-priority", "mmcss-defaults"]));
        if (machine.Count != 3 || machine.Contains("app-shutdown") ||
            !WorkerPrivilegePolicy.RequiresElevation("default-guard-repair") ||
            !WorkerHostService.HandledActions.Contains("default-guard-repair") ||
            !WorkerPrivilegePolicy.RequiresElevation("green-ethernet-disable") ||
            !WorkerHostService.HandledActions.Contains("green-ethernet-disable"))
            throw new InvalidOperationException("Privileged Default Guard / NIC worker wiring failed.");
        foreach (string invalid in new[] { "app-shutdown", "green-ethernet", "unknown-action" })
        {
            bool blocked = false;
            try { ValidateMachineGroupRequest(new DefaultGuardMachineRepairRequest([invalid])); }
            catch (ArgumentException) { blocked = true; }
            if (!blocked) throw new InvalidOperationException("Unsafe worker scope was accepted: " + invalid);
        }
        // The Realtek advanced-property provider supplies display/registry
        // values in parallel. Localized labels must map by DWORD code, not
        // by hard-coded English "Disabled". Invalid/ambiguous maps fail closed.
        if (NativeNicTuningService.SelectGreenEthernetDisabledDisplayValue(
                ["Aktivováno", "Vypnuto"], ["1", "0"]) != "Vypnuto" ||
            NativeNicTuningService.SelectGreenEthernetDisabledDisplayValue(
                ["Off", "On"], ["0", "1"]) != "Off")
            throw new InvalidOperationException("Localized Green Ethernet target mapping failed.");
        foreach ((string[] display, string[] registry) invalid in new[]
        {
            (new[] { "Off" }, new[] { "1" }),
            (new[] { "Off", "Off again" }, new[] { "0", "0" }),
            (new[] { "Off" }, new[] { "0", "1" })
        })
        {
            bool rejected = false;
            try { _ = NativeNicTuningService.SelectGreenEthernetDisabledDisplayValue(invalid.display, invalid.registry); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException("Unsafe Green Ethernet target mapping was accepted.");
        }
        if (NativeNicTuningService.DecodeGreenEthernetRegistryValue("0") != "Disabled" ||
            NativeNicTuningService.DecodeGreenEthernetRegistryValue("1") != "Enabled" ||
            NativeNicTuningService.DecodeGreenEthernetRegistryValue("unknown") != null ||
            NativeNicTuningService.DecodeGreenEthernetRegistryValue(null) != null ||
            Rules.Any(r => r.Key.Contains("Services\\", StringComparison.OrdinalIgnoreCase) ||
                           r.Name is "NetworkThrottlingIndex" or "HwSchMode" or "EnableVirtualizationBasedSecurity") ||
            AvailableGroups.Length != 8 || Rules.Length != 11 ||
            AvailableGroups.Select(g => g.Id).Distinct().Count() != 8 ||
            !NeedsAdministrator("gpu-priority") ||
            !NeedsAdministrator("service-shutdown") ||
            NeedsAdministrator("app-shutdown") ||
            NeedsAdministrator("green-ethernet"))
            throw new InvalidOperationException("Windows default guard allowlist expanded unexpectedly.");
        return "WINDOWS_DEFAULT_GUARD_POLICY_SMOKE_OK groups=8 rules=11 noWindowsWrites=true";
    }

    private static RegistryValueKind? GetValueKindSafe(this RegistryKey key, string name)
    {
        try { return key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) ?
            key.GetValueKind(name) : null; }
        catch (IOException) { return null; }
    }
}
