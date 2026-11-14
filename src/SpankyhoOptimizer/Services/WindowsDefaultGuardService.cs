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

public static partial class WindowsDefaultGuardService
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
}
