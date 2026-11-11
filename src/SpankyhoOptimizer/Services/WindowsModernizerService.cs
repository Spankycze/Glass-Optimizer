using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record WindowsModernizerSnapshot(
    bool UiBaselineConfigured,
    bool SearchCleanupConfigured,
    bool FileDialogProfileConfigured,
    bool StartupDelayOff,
    bool BattleNetDelayed,
    bool SpotifyDelayed,
    bool MaintenanceEnabled,
    bool LltdioManual,
    int VendorServicesPresent,
    int VendorServicesTuned,
    bool ArmouryDownloadPresent,
    bool ArmouryDownloadDisabled,
    int? ActiveMtu,
    bool RssOverridesPresent);


public enum AdaptiveTweakState
{
    Needed,
    AlreadyOptimal,
    NotApplicable,
    ManualReview
}

public enum AdaptiveImpact
{
    Low,
    Medium,
    High
}

public sealed record AdaptiveTweakFinding(
    string Id,
    string Area,
    string Title,
    string Detail,
    AdaptiveTweakState State,
    bool SafeAutoApply,
    AdaptiveImpact Impact = AdaptiveImpact.Low);

public sealed record AdaptiveScanReport(
    IReadOnlyList<AdaptiveTweakFinding> Findings,
    int Needed,
    int AlreadyOptimal,
    int NotApplicable,
    int ManualReview)
{
    public int Total => Findings.Count;
}

internal sealed class WindowsModernizerBackup
{
    public Dictionary<string,string?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class WindowsModernizerService
{
    private static readonly string[] RssOverrideNames = { "*RssBaseProcGroup", "*RssBaseProcNumber", "*RSSMaxProcGroup", "*RssMaxProcNumber" };
    private static readonly string BackupFile = Path.Combine(LogService.Root, "windows-modernizer-backup.json");
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static AdaptiveScanReport? _adaptiveCache;
    private static DateTime _adaptiveCacheUtc;
    private static readonly TimeSpan AdaptiveCacheTtl = TimeSpan.FromSeconds(20);
    private static bool ShouldBrokerPrivilegedMutation =>
        !WorkerExecutionContext.IsWorker && !WorkerPrivilegePolicy.IsAdministrator();

    public static WindowsModernizerSnapshot ReadSnapshot()
    {
        return new WindowsModernizerSnapshot(
            UiBaselineMatches(),
            SearchCleanupMatches(),
            FileDialogProfileMatches(),
            ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec") == 0,
            TaskDelayMatches("WindowsModernizer - Battle.net delayed", 20),
            false,
            ReadDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "MaintenanceDisabled") != 1,
            QueryServiceStartMode("lltdio").Equals("DEMAND_START", StringComparison.OrdinalIgnoreCase),
            CountVendorServicesPresent(),
            CountVendorServicesTuned(),
            ServiceExists("ArmouryCrateDownloadTool"),
            QueryServiceStartMode("ArmouryCrateDownloadTool").Equals("DISABLED", StringComparison.OrdinalIgnoreCase),
            ReadActiveMtu(),
            HasRssOverrides());
    }


    public static async Task<AdaptiveScanReport> ScanAdaptiveSystemAsync(CancellationToken token = default, bool forceRefresh = false)
    {
        lock (Gate)
        {
            if (!forceRefresh && _adaptiveCache != null && DateTime.UtcNow - _adaptiveCacheUtc < AdaptiveCacheTtl)
                return _adaptiveCache;
        }

        AdaptiveScanReport report = await Task.Run(async () =>
        {
            var findings = new List<AdaptiveTweakFinding>();
            void Add(string id, string area, string title, string detail, AdaptiveTweakState state, bool safe = false, AdaptiveImpact impact = AdaptiveImpact.Low) =>
                findings.Add(new(id, area, title, detail, state, safe && state == AdaptiveTweakState.Needed, impact));

            token.ThrowIfCancellationRequested();
            WindowsModernizerSnapshot s = ReadSnapshot();
            var proc = await CaptureProcessUsageAsync(token).ConfigureAwait(false);

            Add("ui.baseline", "UI / Explorer", "Windows UI / Explorer baseline",
                s.UiBaselineConfigured ? "Explorer, Taskbar, thumbnails, animations a DWM baseline už odpovídají cíli." : "Windows UI/Explorer baseline má odchylky.",
                s.UiBaselineConfigured ? AdaptiveTweakState.AlreadyOptimal : AdaptiveTweakState.Needed, true, AdaptiveImpact.Medium);
            Add("ui.search", "UI / Explorer", "Windows Search cleanup",
                s.SearchCleanupConfigured ? "Search web/cloud/feed/location je už vyčištěný." : "Search obsahuje web/cloud/feed/location prvky.",
                s.SearchCleanupConfigured ? AdaptiveTweakState.AlreadyOptimal : AdaptiveTweakState.Needed, true, AdaptiveImpact.Low);
            Add("ui.filedialogs", "UI / Explorer", "Open / Save quick places",
                s.FileDialogProfileConfigured ? "File-dialog quick places už odpovídají profilu." : "File-dialog quick places nejsou nastavené.",
                s.FileDialogProfileConfigured ? AdaptiveTweakState.AlreadyOptimal : AdaptiveTweakState.Needed, true, AdaptiveImpact.Low);

            foreach (var kv in ContextMenuService.ReadOurEntryStates())
            {
                bool desired = kv.Key is "Klasické menu" or "Terminal" or "CMD" or "PowerShell" or "Admin PowerShell" or "Admin Terminal" or "Copy path" or "SHA256" or "New empty file" or "VS Code" or "Advanced startup" or "Restart Explorer" or "Task Manager" or "Device Manager";
                if (!desired) continue;
                Add("context:" + kv.Key, "Context Menu", kv.Key,
                    kv.Value ? "Položka je dostupná." : "Položka z WindowsModernizer profilu chybí.",
                    kv.Value ? AdaptiveTweakState.AlreadyOptimal : AdaptiveTweakState.Needed, true);
            }

            int staleShell = CountStaleShellHandlers();
            Add("shell.stale", "Shell", "Stale shell extensions",
                staleShell == 0 ? "Žádný context-menu handler neodkazuje na chybějící DLL." : $"{staleShell} handlerů odkazuje na chybějící DLL.",
                staleShell == 0 ? AdaptiveTweakState.AlreadyOptimal : AdaptiveTweakState.ManualReview,
                false, staleShell == 0 ? AdaptiveImpact.Low : AdaptiveImpact.High);
            int duplicateShell = CountDuplicateShellHandlerRegistrations();
            Add("shell.duplicates", "Shell", "Duplicitní shell handlery",
                duplicateShell == 0 ? "Žádné duplicitní CLSID registrace v context-menu roots." : $"{duplicateShell} handler CLSID je registrováno ve více shell roots.",
                duplicateShell == 0 ? AdaptiveTweakState.AlreadyOptimal : AdaptiveTweakState.ManualReview,
                false, duplicateShell == 0 ? AdaptiveImpact.Low : AdaptiveImpact.Medium);

            foreach (var run in EnumerateStartupRunEntries())
            {
                token.ThrowIfCancellationRequested();
                string? exe = ExtractExecutablePath(run.Command);
                bool exeMissing = !string.IsNullOrWhiteSpace(exe) && Path.IsPathRooted(exe) && !File.Exists(exe);
                (bool knownSafe, int delay) = ClassifySafeDelayedStartup(run.Name, run.Command);
                bool safe = knownSafe && run.UserScope && !exeMissing;
                string runtime = ProcessUsageForCommand(run.Command, proc);
                if (exeMissing)
                {
                    Add("startup.stale:" + run.Name, "Startup", run.Name,
                        $"Stale {(run.UserScope ? "HKCU" : "HKLM")} Run entry — executable chybí: {exe}",
                        AdaptiveTweakState.ManualReview, false, AdaptiveImpact.High);
                }
                else
                {
                    Add("startup.run:" + run.Name, "Startup", run.Name,
                        safe ? $"Bezpečný delayed startup {delay} s • {runtime} • {run.Command}" :
                               $"{(run.UserScope ? "HKCU" : "HKLM")} startup • {runtime} • {run.Command}",
                        safe ? AdaptiveTweakState.Needed : AdaptiveTweakState.ManualReview, safe,
                        safe ? AdaptiveImpact.Medium : AdaptiveImpact.Low);
                }
            }
            foreach (string startupItem in EnumerateStartupFolderItems())
                Add("startup.folder:" + startupItem, "Startup", startupItem, "Položka ve Startup folderu — ruční kontrola.", AdaptiveTweakState.ManualReview);
            Add("startup.delay", "Startup", "Windows startup delay",
                s.StartupDelayOff ? "StartupDelayInMSec už je 0 ms." : "Windows stále používá startup delay.",
                s.StartupDelayOff ? AdaptiveTweakState.AlreadyOptimal : AdaptiveTweakState.Needed, true, AdaptiveImpact.Medium);

            IReadOnlyList<TaskInfo> tasks;
            // Adaptive scan only needs task identity/enabled state here. Avoid the expensive
            // full COM inventory (XML/startup-trigger parsing); dead-target verification below
            // already reads the task file only for non-Microsoft enabled tasks.
            try { tasks = await new TaskSchedulerService().GetBasicAsync(false, token).ConfigureAwait(false); }
            catch { tasks = Array.Empty<TaskInfo>(); }
            foreach (TaskInfo task in tasks.Where(t => t.Enabled))
            {
                token.ThrowIfCancellationRequested();
                bool microsoft = task.Path.StartsWith("\\Microsoft\\", StringComparison.OrdinalIgnoreCase);
                bool dead = !microsoft && TaskExecutableMissing(task.Path);
                if (dead)
                {
                    bool knownAsus = task.Name.Equals("P508PowerAgent_sdk", StringComparison.OrdinalIgnoreCase);
                    Add(knownAsus ? "task.asus-p508" : "task.dead:" + task.Path,
                        "Task Scheduler", task.Name,
                        "Scheduled task odkazuje na chybějící executable.",
                        knownAsus ? AdaptiveTweakState.Needed : AdaptiveTweakState.ManualReview,
                        knownAsus, AdaptiveImpact.High);
                }
                else if (task.Name.Equals("StartAllBack Update", StringComparison.OrdinalIgnoreCase))
                    Add("task.startallback-update", "Task Scheduler", task.Name, "Updater task je zapnutý.", AdaptiveTweakState.Needed, true, AdaptiveImpact.Low);
                else if (!microsoft && task.StartupTrigger &&
                         !task.Name.StartsWith("WindowsModernizer", StringComparison.OrdinalIgnoreCase) &&
                         !task.Name.StartsWith("Spankyho Optimizer", StringComparison.OrdinalIgnoreCase))
                    Add("task.review:" + task.Path, "Task Scheduler", task.Name,
                        "Third-party startup scheduled task.", AdaptiveTweakState.ManualReview, false, AdaptiveImpact.Low);
            }

            Add("system.maintenance", "System Tweaks", "Windows Maintenance / LLTDIO",
                $"Maintenance={(s.MaintenanceEnabled ? "ON" : "OFF")} • LLTDIO={(s.LltdioManual ? "Manual" : "jinak")}",
                s.MaintenanceEnabled && s.LltdioManual ? AdaptiveTweakState.AlreadyOptimal : AdaptiveTweakState.Needed, true, AdaptiveImpact.Low);

            foreach (string name in GetVendorServiceNames())
            {
                string mode = QueryServiceStartMode(name);
                Add("vendor.service:" + name, "Services", name, $"Startup type: {mode}",
                    mode == "DEMAND_START" ? AdaptiveTweakState.AlreadyOptimal : AdaptiveTweakState.Needed, true, AdaptiveImpact.Medium);
            }
            if (s.ArmouryDownloadPresent)
                Add("vendor.armoury-download", "Services", "ArmouryCrateDownloadTool",
                    s.ArmouryDownloadDisabled ? "Service je Disabled." : "Service je aktivní nebo Manual.",
                    s.ArmouryDownloadDisabled ? AdaptiveTweakState.AlreadyOptimal : AdaptiveTweakState.Needed, true, AdaptiveImpact.Medium);

            foreach (var svc in EnumerateThirdPartyAutoServices())
            {
                if (IsVendorServiceName(svc.Name) || svc.Name.Equals("ArmouryCrateService", StringComparison.OrdinalIgnoreCase) ||
                    svc.Name.Equals("NvContainerLocalSystem", StringComparison.OrdinalIgnoreCase) ||
                    svc.Name.Equals("NVDisplay.ContainerLocalSystem", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? exe = ExtractExecutablePath(svc.ImagePath);
                bool missing = !string.IsNullOrWhiteSpace(exe) && Path.IsPathRooted(exe) && !File.Exists(exe);
                string runtime = ProcessUsageForCommand(svc.ImagePath, proc);
                Add("service.review:" + svc.Name, "Services", svc.Name,
                    missing
                        ? $"Stale Auto service — executable chybí: {exe}"
                        : $"Third-party Auto service • {runtime} • {svc.ImagePath}",
                    AdaptiveTweakState.ManualReview, false,
                    missing ? AdaptiveImpact.High : AdaptiveImpact.Low);
            }

            Add("network.rss-mtu", "Internet / Síť", "MTU / RSS override stav",
                $"Aktivní MTU={s.ActiveMtu?.ToString() ?? "N/A"} • RSS overrides={(s.RssOverridesPresent ? "nalezené" : "ne")}",
                s.RssOverridesPresent ? AdaptiveTweakState.Needed : AdaptiveTweakState.AlreadyOptimal, s.RssOverridesPresent, AdaptiveImpact.Medium);

            int fixedDrives = DriveInfo.GetDrives().Count(d => d.IsReady && d.DriveType == DriveType.Fixed);
            Add("storage.analyze", "Storage", "Storage analyze / reliability", fixedDrives > 0 ? $"{fixedDrives} fixed drive(s) lze analyzovat." : "Žádný fixed drive.", fixedDrives > 0 ? AdaptiveTweakState.ManualReview : AdaptiveTweakState.NotApplicable);
            Add("storage.retrim", "Storage", "SSD/NVMe Retrim", fixedDrives > 0 ? "Retrim je dostupný jako ruční maintenance akce." : "Žádný fixed drive.", fixedDrives > 0 ? AdaptiveTweakState.ManualReview : AdaptiveTweakState.NotApplicable);

            long crashDumpBytes = DirectorySizeSafe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"), 20000);
            Add("cleanup.crashdumps", "Cleaner", "Crash dumps",
                crashDumpBytes > 0 ? $"CrashDumps používají {crashDumpBytes / 1048576d:0.0} MB." : "CrashDumps jsou prázdné nebo neexistují.",
                crashDumpBytes >= 100 * 1048576L ? AdaptiveTweakState.ManualReview : AdaptiveTweakState.AlreadyOptimal);

            Add("audit.postlogin", "Diagnostics", "60s post-login audit", "CPU/RAM startup sampler je dostupný na vyžádání.", AdaptiveTweakState.ManualReview);

            return new AdaptiveScanReport(findings,
                findings.Count(x => x.State == AdaptiveTweakState.Needed),
                findings.Count(x => x.State == AdaptiveTweakState.AlreadyOptimal),
                findings.Count(x => x.State == AdaptiveTweakState.NotApplicable),
                findings.Count(x => x.State == AdaptiveTweakState.ManualReview));
        }, token).ConfigureAwait(false);

        lock (Gate)
        {
            _adaptiveCache = report;
            _adaptiveCacheUtc = DateTime.UtcNow;
        }
        return report;
    }

    public static void InvalidateAdaptiveScanCache()
    {
        lock (Gate)
        {
            _adaptiveCache = null;
            _adaptiveCacheUtc = default;
        }
    }

    public static async Task<IReadOnlyList<string>> ApplyAdaptiveRecommendedAsync(AdaptiveScanReport report, CancellationToken token)
    {
        if (ShouldBrokerPrivilegedMutation)
            return await WorkerClientService.RunAsync<string[]>(
                "modernizer-adaptive",
                WorkerPayloads.Encode(new ModernizerAdaptiveRequest(report)),
                TimeSpan.FromMinutes(10),
                token).ConfigureAwait(false);

        var applied = new List<string>();
        bool uiBaselineApplied = false;
        foreach (AdaptiveTweakFinding finding in report.Findings.Where(x => x.SafeAutoApply && x.State == AdaptiveTweakState.Needed))
        {
            token.ThrowIfCancellationRequested();
            switch (finding.Id)
            {
                case "ui.baseline":
                case "ui.search":
                    if (!uiBaselineApplied)
                    {
                        await ApplyUiBaselineAsync(token).ConfigureAwait(false);
                        uiBaselineApplied = true;
                        applied.Add("Windows UI / Search baseline");
                    }
                    break;
                case "ui.filedialogs":
                    ApplyFileDialogProfile();
                    applied.Add(finding.Title);
                    break;
                case "startup.delay":
                    BackupReg("startup.delay", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec");
                    SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0);
                    applied.Add(finding.Title);
                    break;
                case "system.maintenance":
                    await ApplySystemMaintenanceAsync(token).ConfigureAwait(false);
                    applied.Add(finding.Title);
                    break;
                case "vendor.armoury-download":
                    await ApplyArmouryDownloadOnlyAsync(token).ConfigureAwait(false);
                    applied.Add(finding.Title);
                    break;
                case "network.rss-mtu":
                    await OptimizeNetworkAsync(token).ConfigureAwait(false);
                    applied.Add(finding.Title);
                    break;
                case "task.startallback-update":
                    await SetTaskEnabledByNameAsync("StartAllBack Update", false, token).ConfigureAwait(false);
                    applied.Add(finding.Title);
                    break;
                case "task.asus-p508":
                    await SetTaskEnabledByNameAsync("P508PowerAgent_sdk", false, token).ConfigureAwait(false);
                    applied.Add(finding.Title);
                    break;
                default:
                    if (finding.Id.StartsWith("startup.run:", StringComparison.Ordinal))
                    {
                        string name = finding.Id["startup.run:".Length..];
                        var entry = EnumerateStartupRunEntries().FirstOrDefault(x => x.UserScope && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (entry.Name != null)
                        {
                            (_, int delay) = ClassifySafeDelayedStartup(entry.Name, entry.Command);
                            await StageRunEntryAsync(entry.Name, "Spankyho Optimizer - delayed - " + SanitizeTaskName(entry.Name), delay, token).ConfigureAwait(false);
                            applied.Add(finding.Title);
                        }
                    }
                    else if (finding.Id.StartsWith("vendor.service:", StringComparison.Ordinal))
                    {
                        string service = finding.Id["vendor.service:".Length..];
                        await ApplyVendorServiceOnlyAsync(service, token).ConfigureAwait(false);
                        applied.Add(finding.Title);
                    }
                    else if (finding.Id.StartsWith("context:", StringComparison.Ordinal))
                    {
                        ApplyContextMenuFinding(finding.Id["context:".Length..]);
                        applied.Add(finding.Title);
                    }
                    break;
            }
        }
        InvalidateAdaptiveScanCache();
        return applied;
    }


    private sealed record ProcessUsage(int Count, long PrivateBytes, double CpuSeconds)
    {
        public string Summary => $"{Count} proc • {PrivateBytes / 1048576d:0} MB private • {CpuSeconds:0.00}s CPU/250ms";
    }

    private static async Task<Dictionary<string, ProcessUsage>> CaptureProcessUsageAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var first = new Dictionary<int, double>();
        ProcessSnapshotCleanup.Inspect(() => Process.GetProcesses(), processes =>
        {
            foreach (Process process in processes)
            {
                token.ThrowIfCancellationRequested();
                try { first[process.Id] = process.TotalProcessorTime.TotalSeconds; }
                catch (Exception ex) when (ex is InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
            return true;
        });
        await Task.Delay(250, token).ConfigureAwait(false);
        var agg = new Dictionary<string, (int Count,long Private,double Cpu)>(StringComparer.OrdinalIgnoreCase);
        ProcessSnapshotCleanup.Inspect(() => Process.GetProcesses(), processes =>
        {
            foreach (Process process in processes)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!first.TryGetValue(process.Id, out double initialCpu)) continue;
                    double cpu = Math.Max(0, process.TotalProcessorTime.TotalSeconds - initialCpu);
                    long priv = process.PrivateMemorySize64;
                    string name = process.ProcessName;
                    if (!agg.TryGetValue(name, out var cur)) cur = default;
                    agg[name] = (cur.Count + 1, cur.Private + priv, cur.Cpu + cpu);
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
            return true;
        });
        token.ThrowIfCancellationRequested();
        return agg.ToDictionary(x => x.Key, x => new ProcessUsage(x.Value.Count, x.Value.Private, x.Value.Cpu), StringComparer.OrdinalIgnoreCase);
    }

    private static string? ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        if (expanded.StartsWith('"'))
        {
            int end = expanded.IndexOf('"', 1);
            if (end > 1) return expanded[1..end];
        }
        int exe = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0) return expanded[..(exe + 4)].Trim().Trim('"');
        int space = expanded.IndexOf(' ');
        return (space > 0 ? expanded[..space] : expanded).Trim().Trim('"');
    }

    private static string ProcessUsageForCommand(string command, Dictionary<string, ProcessUsage> proc)
    {
        string? exe = ExtractExecutablePath(command);
        string name = exe == null ? string.Empty : Path.GetFileNameWithoutExtension(exe);
        if (!string.IsNullOrWhiteSpace(name) && proc.TryGetValue(name, out var usage))
            return usage.Summary;
        return "proces neběží";
    }

    private static (string Name, string Command, bool UserScope)[] EnumerateStartupRunEntries()
    {
        var list = new List<(string Name,string Command,bool UserScope)>();
        foreach (var tuple in new[] {
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", true),
            (RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", false)
        })
        {
            try
            {
                using var b = RegistryKey.OpenBaseKey(tuple.Item1, RegistryView.Registry64);
                using var k = b.OpenSubKey(tuple.Item2);
                if (k == null) continue;
                foreach (string name in k.GetValueNames())
                {
                    string cmd = k.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(cmd)) list.Add((name, cmd, tuple.Item3));
                }
            }
            catch { }
        }
        return list.ToArray();
    }

    private static string[] EnumerateStartupFolderItems()
    {
        var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string path in Directory.EnumerateFileSystemEntries(dir))
                    items.Add(Path.GetFileName(path));
            }
            catch { }
        }
        return items.ToArray();
    }

    private static (bool Safe, int DelaySeconds) ClassifySafeDelayedStartup(string name, string command)
    {
        string text = (name + " " + command).ToLowerInvariant();
        if (text.Contains("battle.net")) return (true, 20);
        if (text.Contains("spotify")) return (true, 35);
        if (text.Contains("discord")) return (true, 25);
        if (text.Contains("epicgameslauncher") || text.Contains("epic games")) return (true, 30);
        if (text.Contains("steam") && !text.Contains("framepacer")) return (true, 20);
        if (text.Contains("adobe") && (text.Contains("update") || text.Contains("creative cloud"))) return (true, 45);
        if (text.Contains("teams") || text.Contains("ms-teams")) return (true, 30);
        return (false, 0);
    }

    private static (string Name, string ImagePath)[] EnumerateThirdPartyAutoServices()
    {
        var list = new List<(string,string)>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (root == null) return Array.Empty<(string,string)>();
            foreach (string name in root.GetSubKeyNames())
            {
                using var s = root.OpenSubKey(name);
                if (Convert.ToInt32(s?.GetValue("Start") ?? -1, CultureInfo.InvariantCulture) != 2) continue;
                string img = s?.GetValue("ImagePath", "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                string lower = img.ToLowerInvariant();
                if (lower.Contains("\\windows\\system32") || lower.Contains("%systemroot%") || lower.Contains("svchost.exe")) continue;
                if (!string.IsNullOrWhiteSpace(img)) list.Add((name, img));
            }
        }
        catch { }
        return list.ToArray();
    }

    private static long DirectorySizeSafe(string path, int maxFiles)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long total = 0; int count = 0;
            foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
                if (++count >= maxFiles) break;
            }
            return total;
        }
        catch { return 0; }
    }

    private static bool TaskExecutableMissing(string taskPath)
    {
        try
        {
            string rel = taskPath.TrimStart('\\');
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", rel);
            if (!File.Exists(file)) return false;
            string xml = File.ReadAllText(file);
            int a = xml.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
            int b = xml.IndexOf("</Command>", StringComparison.OrdinalIgnoreCase);
            if (a < 0 || b <= a) return false;
            string cmd = System.Net.WebUtility.HtmlDecode(xml[(a + 9)..b]).Trim().Trim('"');
            cmd = Environment.ExpandEnvironmentVariables(cmd);
            return Path.IsPathRooted(cmd) && !File.Exists(cmd);
        }
        catch { return false; }
    }

    private static async Task ApplyVendorServiceOnlyAsync(string service, CancellationToken token)
    {
        string old = QueryServiceStartMode(service);
        if (string.IsNullOrWhiteSpace(old)) return;
        BackupSimple("svc." + service, old);
        await SetServiceStartModeNativeAsync(service, "demand", token).ConfigureAwait(false);
    }

    private static async Task ApplyArmouryDownloadOnlyAsync(CancellationToken token)
    {
        string old = QueryServiceStartMode("ArmouryCrateDownloadTool");
        if (string.IsNullOrWhiteSpace(old)) return;
        BackupSimple("svc.ArmouryCrateDownloadTool", old);
        await TryStopVendorServiceNativeAsync("ArmouryCrateDownloadTool", token).ConfigureAwait(false);
        await SetServiceStartModeNativeAsync("ArmouryCrateDownloadTool", "disabled", token).ConfigureAwait(false);
    }

    private static async Task SetTaskEnabledByNameAsync(string name, bool enabled, CancellationToken token)
    {
        // Name lookup does not need task XML/startup-trigger parsing.
        var all = await new TaskSchedulerService().GetBasicAsync(false, token).ConfigureAwait(false);
        TaskInfo? item = all.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (item != null) await new TaskSchedulerService().SetEnabledAsync(item.Path, enabled, token).ConfigureAwait(false);
    }

    private static void ApplyContextMenuFinding(string name)
    {
        switch (name)
        {
            case "Terminal": ContextMenuService.SetTerminalHere(true); break;
            case "CMD": ContextMenuService.SetCmdHere(true); break;
            case "PowerShell": ContextMenuService.SetPowerShellHere(true); break;
            case "Admin PowerShell": ContextMenuService.SetAdminPowerShellHere(true); break;
            case "Admin Terminal": ContextMenuService.SetAdminTerminalHere(true); break;
            case "Copy path": ContextMenuService.SetCopyPath(true); break;
            case "SHA256": ContextMenuService.SetSha256(true); break;
            case "New empty file": ContextMenuService.SetNewEmptyFile(true); break;
            case "VS Code": ContextMenuService.SetVsCode(true); break;
            case "Advanced startup": ContextMenuService.SetAdvancedStartup(true); break;
            case "Restart Explorer": ContextMenuService.SetRestartExplorer(true); break;
            case "Task Manager": ContextMenuService.SetTaskManager(true); break;
            case "Device Manager": ContextMenuService.SetDeviceManager(true); break;
            case "Klasické menu": ContextMenuService.OptimizeContextMenuExperience(); break;
        }
    }

    private static string SanitizeTaskName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 60 ? name[..60] : name;
    }

    public static string ApplyFileDialogProfile()
    {
        BackupReg("filedlg.place0", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Comdlg32\PlacesBar", "Place0");
        BackupReg("filedlg.place1", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Comdlg32\PlacesBar", "Place1");
        BackupReg("filedlg.place2", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Comdlg32\PlacesBar", "Place2");
        BackupReg("filedlg.place3", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Comdlg32\PlacesBar", "Place3");
        BackupReg("filedlg.place4", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Comdlg32\PlacesBar", "Place4");
        string u = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        SetString(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Comdlg32\PlacesBar", "Place0", Path.Combine(u,"Desktop"));
        SetString(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Comdlg32\PlacesBar", "Place1", Path.Combine(u,"Downloads"));
        SetString(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Comdlg32\PlacesBar", "Place2", Path.Combine(u,"Projects"));
        SetString(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Comdlg32\PlacesBar", "Place3", Path.Combine(u,"Documents"));
        SetString(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Comdlg32\PlacesBar", "Place4", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
        InvalidateAdaptiveScanCache();
        var verify = ReadSnapshot();
        if (!verify.FileDialogProfileConfigured)
            throw new InvalidOperationException("File Dialog profil read-back nepotvrdil cílové rychlé položky.");
        return "Desktop • Downloads • Projects • Documents • This PC • read-back OK";
    }

    public static void ClearFileDialogHistory()
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32", writable:true);
        if (root == null) return;
        string[] targets = {"OpenSavePidlMRU","LastVisitedPidlMRU","LastVisitedPidlMRULegacy"};
        var errors = new List<Exception>();
        foreach (var n in targets)
        {
            try { root.DeleteSubKeyTree(n, false); }
            catch (Exception ex) { errors.Add(new InvalidOperationException($"Nelze odstranit {n}.", ex)); }
        }
        foreach (var n in targets)
        {
            using var verify = root.OpenSubKey(n);
            if (verify != null)
                errors.Add(new InvalidOperationException($"Historie {n} po smazání stále existuje."));
        }
        if (errors.Count > 0)
            throw new AggregateException("Historii Otevřít/Uložit se nepodařilo kompletně vyčistit.", errors);
        InvalidateAdaptiveScanCache();
    }

    public static int CountStaleShellHandlers()
    {
        int count = 0;
        foreach (var root in new[]{@"*\shellex\ContextMenuHandlers",@"Directory\shellex\ContextMenuHandlers",@"Directory\Background\shellex\ContextMenuHandlers",@"Folder\shellex\ContextMenuHandlers"})
        {
            using var key = Registry.ClassesRoot.OpenSubKey(root);
            if (key == null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                using var h = key.OpenSubKey(name);
                string clsid = (h?.GetValue(null)?.ToString() ?? "").Trim();
                if (string.IsNullOrWhiteSpace(clsid)) continue;
                using var c = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid + @"\InprocServer32");
                string path = Environment.ExpandEnvironmentVariables(c?.GetValue(null)?.ToString() ?? "");
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && !File.Exists(path)) count++;
            }
        }
        return count;
    }


    public static int CountDuplicateShellHandlerRegistrations()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]{@"*\shellex\ContextMenuHandlers",@"Directory\shellex\ContextMenuHandlers",@"Directory\Background\shellex\ContextMenuHandlers",@"Folder\shellex\ContextMenuHandlers"})
        {
            using var key = Registry.ClassesRoot.OpenSubKey(root);
            if (key == null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                using var h = key.OpenSubKey(name);
                string clsid = (h?.GetValue(null)?.ToString() ?? "").Trim();
                if (string.IsNullOrWhiteSpace(clsid)) continue;
                counts.TryGetValue(clsid, out int count);
                counts[clsid] = count + 1;
            }
        }
        return counts.Count(x => x.Value > 1);
    }

    public static async Task ApplyStartupProfileAsync(CancellationToken token)
    {
        if (ShouldBrokerPrivilegedMutation)
        {
            await WorkerClientService.RunAsync<WorkerAck>("modernizer-startup-apply", timeout: TimeSpan.FromMinutes(2), token: token).ConfigureAwait(false);
            InvalidateAdaptiveScanCache();
            return;
        }

        BackupReg("startup.delay", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec");
        SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0);
        await StageRunEntryAsync("Battle.net", "WindowsModernizer - Battle.net delayed", 20, token);
        await RunAsync("schtasks.exe", "/Delete /TN \"WindowsModernizer - Spotify delayed\" /F", token, ignoreExit:true);
        InvalidateAdaptiveScanCache();
    }

    public static async Task RestoreStartupProfileAsync(CancellationToken token)
    {
        if (ShouldBrokerPrivilegedMutation)
        {
            await WorkerClientService.RunAsync<WorkerAck>("modernizer-startup-restore", timeout: TimeSpan.FromMinutes(2), token: token).ConfigureAwait(false);
        }
        else
        {
            RestoreReg("startup.delay");
            var localBackup = LoadBackup();
            foreach (var pair in new[]{("Battle.net","WindowsModernizer - Battle.net delayed")})
            {
                await RunAsync("schtasks.exe", $"/Delete /TN \"{pair.Item2}\" /F", token, ignoreExit:true);
                if (localBackup.Values.TryGetValue("run."+pair.Item1, out var cmd))
                {
                    using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                    if (cmd == null) k?.DeleteValue(pair.Item1, false); else k?.SetValue(pair.Item1, cmd, RegistryValueKind.String);
                }
            }
        }

        InvalidateAdaptiveScanCache();
        var b = LoadBackup();
        if (!RegistryBackupMatches("startup.delay"))
            throw new InvalidOperationException("Startup restore read-back nepotvrdil původní StartupDelayInMSec.");
        if (TaskExists("WindowsModernizer - Battle.net delayed"))
            throw new InvalidOperationException("Startup restore read-back našel zbylou Battle.net delayed task.");
        if (b.Values.TryGetValue("run.Battle.net", out var expectedRun))
        {
            string? actualRun = ReadString(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Battle.net");
            if (!string.Equals(actualRun, expectedRun, StringComparison.Ordinal))
                throw new InvalidOperationException("Startup restore read-back nepotvrdil původní Battle.net Run hodnotu.");
        }
    }

    public static async Task ApplySystemMaintenanceAsync(CancellationToken token)
    {
        if (ShouldBrokerPrivilegedMutation)
        {
            await WorkerClientService.RunAsync<WorkerAck>("modernizer-maintenance-apply", timeout: TimeSpan.FromMinutes(2), token: token).ConfigureAwait(false);
            InvalidateAdaptiveScanCache();
            return;
        }

        BackupReg("maint.disabled", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "MaintenanceDisabled");
        SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "MaintenanceDisabled", 0);
        BackupSimple("svc.lltdio", QueryServiceStartMode("lltdio"));
        await SetServiceStartModeNativeAsync("lltdio", "demand", token);
        InvalidateAdaptiveScanCache();
    }

    public static async Task RestoreSystemMaintenanceAsync(CancellationToken token)
    {
        if (ShouldBrokerPrivilegedMutation)
        {
            await WorkerClientService.RunAsync<WorkerAck>("modernizer-maintenance-restore", timeout: TimeSpan.FromMinutes(2), token: token).ConfigureAwait(false);
        }
        else
        {
            RestoreReg("maint.disabled");
            var localBackup = LoadBackup();
            if (localBackup.Values.TryGetValue("svc.lltdio", out var mode) && !string.IsNullOrWhiteSpace(mode))
                await SetServiceStartModeNativeAsync("lltdio", ScMode(mode), token);
        }

        InvalidateAdaptiveScanCache();
        var b = LoadBackup();
        if (!RegistryBackupMatches("maint.disabled"))
            throw new InvalidOperationException("System Maintenance restore read-back nepotvrdil původní MaintenanceDisabled.");
        if (b.Values.TryGetValue("svc.lltdio", out var expectedMode) && !string.IsNullOrWhiteSpace(expectedMode) &&
            !QueryServiceStartMode("lltdio").Equals(expectedMode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("System Maintenance restore read-back nepotvrdil původní lltdio start mode.");
    }

    public static async Task ApplyVendorIdleAsync(CancellationToken token)
    {
        if (ShouldBrokerPrivilegedMutation)
        {
            await WorkerClientService.RunAsync<WorkerAck>("modernizer-vendor-apply", timeout: TimeSpan.FromMinutes(3), token: token).ConfigureAwait(false);
            InvalidateAdaptiveScanCache();
            return;
        }

        foreach (var s in GetVendorServiceNames())
        {
            string old = QueryServiceStartMode(s);
            if (!string.IsNullOrWhiteSpace(old))
            {
                BackupSimple("svc."+s, old);
                await SetServiceStartModeNativeAsync(s, "demand", token);
            }
        }
        string arm = QueryServiceStartMode("ArmouryCrateDownloadTool");
        if (!string.IsNullOrWhiteSpace(arm))
        {
            BackupSimple("svc.ArmouryCrateDownloadTool", arm);
            await TryStopVendorServiceNativeAsync("ArmouryCrateDownloadTool", token);
            await SetServiceStartModeNativeAsync("ArmouryCrateDownloadTool", "disabled", token);
        }
        bool? p508Enabled = ReadTaskEnabledState(@"\ASUS\P508PowerAgent_sdk");
        BackupSimple("task.P508PowerAgent_sdk", TaskExists(@"\ASUS\P508PowerAgent_sdk") ? "1":"0");
        BackupSimple("task.P508PowerAgent_sdk.enabled", p508Enabled.HasValue ? (p508Enabled.Value ? "1" : "0") : null);
        await RunAsync("schtasks.exe", "/Change /TN \"\\ASUS\\P508PowerAgent_sdk\" /Disable", token, true);
        InvalidateAdaptiveScanCache();
    }

    public static async Task RestoreVendorIdleAsync(CancellationToken token)
    {
        if (ShouldBrokerPrivilegedMutation)
        {
            await WorkerClientService.RunAsync<WorkerAck>("modernizer-vendor-restore", timeout: TimeSpan.FromMinutes(3), token: token).ConfigureAwait(false);
        }
        else
        {
            var localBackup = LoadBackup();
            var localServiceKeys = localBackup.Values.Keys
                .Where(k => k.StartsWith("svc.", StringComparison.OrdinalIgnoreCase))
                .Select(k => k["svc.".Length..])
                .Where(IsVendorServiceName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var s in localServiceKeys)
                if (localBackup.Values.TryGetValue("svc."+s, out var m) && !string.IsNullOrWhiteSpace(m))
                    await SetServiceStartModeNativeAsync(s, ScMode(m), token);
            if (localBackup.Values.TryGetValue("task.P508PowerAgent_sdk.enabled", out var enabledBackup) && enabledBackup != null)
                await RunAsync("schtasks.exe", $"/Change /TN \"\\ASUS\\P508PowerAgent_sdk\" /{(enabledBackup=="1" ? "Enable" : "Disable")}", token, true);
            else if (localBackup.Values.TryGetValue("task.P508PowerAgent_sdk", out var was) && was=="1")
                await RunAsync("schtasks.exe", "/Change /TN \"\\ASUS\\P508PowerAgent_sdk\" /Enable", token, true);
        }

        InvalidateAdaptiveScanCache();
        var b = LoadBackup();
        var serviceKeys = b.Values.Keys
            .Where(k => k.StartsWith("svc.", StringComparison.OrdinalIgnoreCase))
            .Select(k => k["svc.".Length..])
            .Where(IsVendorServiceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] mismatched = serviceKeys.Where(s =>
            b.Values.TryGetValue("svc."+s, out var expected) && !string.IsNullOrWhiteSpace(expected) &&
            !QueryServiceStartMode(s).Equals(expected, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (mismatched.Length > 0)
            throw new InvalidOperationException("Vendor restore read-back mismatch: " + string.Join(", ", mismatched));
        if (b.Values.TryGetValue("task.P508PowerAgent_sdk.enabled", out var expectedTask) && expectedTask != null)
        {
            bool? actualTask = ReadTaskEnabledState(@"\ASUS\P508PowerAgent_sdk");
            if (!actualTask.HasValue || actualTask.Value != (expectedTask == "1"))
                throw new InvalidOperationException("Vendor restore read-back nepotvrdil původní stav P508PowerAgent tasku.");
        }
    }

    public static async Task<int> OptimizeNetworkAsync(CancellationToken token)
    {
        if (ShouldBrokerPrivilegedMutation)
        {
            int result = await WorkerClientService.RunAsync<int>("modernizer-network-optimize", timeout: TimeSpan.FromMinutes(3), token: token).ConfigureAwait(false);
            InvalidateAdaptiveScanCache();
            return result;
        }

        var nic = NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.NetworkInterfaceType!=NetworkInterfaceType.Loopback && n.OperationalStatus==OperationalStatus.Up)
            .OrderByDescending(n=>n.Speed).FirstOrDefault() ?? throw new InvalidOperationException("Aktivní síťový adaptér nebyl nalezen.");
        string name = nic.Name;
        int oldMtu = ReadActiveMtu() ?? 1500;
        BackupSimple("network.mtu", oldMtu.ToString(CultureInfo.InvariantCulture));
        BackupSimple("network.mtu.adapter", name);
        string guid = nic.Id.Trim('{','}').ToLowerInvariant();
        string classPath = FindNicClassPath(guid) ?? throw new InvalidOperationException("NIC class registry key nebyl nalezen.");
        foreach (var n in new[]{"*RssBaseProcGroup","*RssBaseProcNumber","*RSSMaxProcGroup","*RssMaxProcNumber"})
        {
            BackupReg("network."+n, RegistryHive.LocalMachine, classPath, n);
            DeleteValue(RegistryHive.LocalMachine, classPath, n);
        }
        string gw = nic.GetIPProperties().GatewayAddresses.FirstOrDefault(x=>x.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "1.1.1.1";
        bool committed = false;
        try
        {
            foreach (int mtu in new[]{1500,1492,1488})
            {
                await RunAsync("netsh.exe", $"interface ipv4 set subinterface \"{name}\" mtu={mtu} store=active", token);
                bool g = await PingDfAsync(gw, mtu, token);
                bool i = await PingDfAsync("1.1.1.1", mtu, token);
                if (g && i)
                {
                    await RunAsync("netsh.exe", $"interface ipv4 set subinterface \"{name}\" mtu={mtu} store=persistent", token);
                    committed = true;
                    InvalidateAdaptiveScanCache();
                    return mtu;
                }
            }
        }
        finally
        {
            // Cancel/exception/no candidate: restore the original active MTU with a non-cancelled token.
            if (!committed)
                await RunAsync("netsh.exe", $"interface ipv4 set subinterface \"{name}\" mtu={oldMtu} store=active", CancellationToken.None, true);
        }
        InvalidateAdaptiveScanCache();
        return oldMtu;
    }

    public static async Task RestoreNetworkAsync(CancellationToken token)
    {
        if (ShouldBrokerPrivilegedMutation)
        {
            await WorkerClientService.RunAsync<WorkerAck>("modernizer-network-restore", timeout: TimeSpan.FromMinutes(3), token: token).ConfigureAwait(false);
            InvalidateAdaptiveScanCache();
            return;
        }

        var b = LoadBackup();
        foreach (var n in new[]{"*RssBaseProcGroup","*RssBaseProcNumber","*RSSMaxProcGroup","*RssMaxProcNumber"}) RestoreReg("network."+n);
        if (b.Values.TryGetValue("network.mtu", out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mtu))
        {
            // Restore on the adapter that was backed up; older backups without the name keep the previous fastest-active fallback.
            string? backedUpAdapter = b.Values.GetValueOrDefault("network.mtu.adapter");
            var all = NetworkInterface.GetAllNetworkInterfaces();
            var nic = (!string.IsNullOrWhiteSpace(backedUpAdapter) ? all.FirstOrDefault(n=>n.Name.Equals(backedUpAdapter, StringComparison.OrdinalIgnoreCase)) : null)
                ?? (string.IsNullOrWhiteSpace(backedUpAdapter) ? all.Where(n=>n.NetworkInterfaceType!=NetworkInterfaceType.Loopback && n.OperationalStatus==OperationalStatus.Up).OrderByDescending(n=>n.Speed).FirstOrDefault() : null);
            if (nic != null) await RunAsync("netsh.exe", $"interface ipv4 set subinterface \"{nic.Name}\" mtu={mtu} store=persistent", token, true);
        }
        InvalidateAdaptiveScanCache();
    }

    public static async Task<string> AnalyzeFixedDrivesAsync(CancellationToken token)
    {
        var sb = new StringBuilder();
        foreach (var d in DriveInfo.GetDrives().Where(d=>d.IsReady && d.DriveType==DriveType.Fixed))
        {
            string letter=d.Name.TrimEnd('\\');
            var r=await RunAsync("defrag.exe", $"{letter} /A /U /V", token, true);
            sb.Append(letter).Append(": ").Append(r.ExitCode==0?"OK":"check").Append(" | ");
        }
        return sb.ToString().TrimEnd(' ', '|');
    }

    public static async Task<string> RetrimFixedDrivesAsync(CancellationToken token)
    {
        if (ShouldBrokerPrivilegedMutation)
            return await WorkerClientService.RunAsync<string>("modernizer-retrim", timeout: TimeSpan.FromMinutes(15), token: token).ConfigureAwait(false);

        var sb = new StringBuilder();
        foreach (var d in DriveInfo.GetDrives().Where(d=>d.IsReady && d.DriveType==DriveType.Fixed))
        {
            string letter=d.Name.TrimEnd('\\');
            var r=await RunAsync("defrag.exe", $"{letter} /L /U /V", token, true);
            sb.Append(letter).Append(": ").Append(r.ExitCode==0?"Retrim OK":"check").Append(" | ");
        }
        return sb.ToString().TrimEnd(' ', '|');
    }

    public static async Task ApplyUiBaselineAsync(CancellationToken token)
    {
        var regs = new (string id, RegistryHive hive, string path, string name, int value)[] {
            ("ui.launchTo",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","LaunchTo",1),
            ("ui.hideExt",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","HideFileExt",0),
            ("ui.syncNotif",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","ShowSyncProviderNotifications",0),
            ("ui.trackDocs",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","Start_TrackDocs",0),
            ("ui.taskview",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","ShowTaskViewButton",0),
            ("ui.widgets",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","TaskbarDa",0),
            ("ui.chat",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","TaskbarMn",0),
            ("ui.altTab",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","MultiTaskingAltTabFilter",3),
            ("ui.snap",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","EnableSnapAssistFlyout",1),
            ("ui.endTask",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings","TaskbarEndTask",1),
            ("ui.searchWeb",RegistryHive.CurrentUser,@"Software\Policies\Microsoft\Windows\Explorer","DisableSearchBoxSuggestions",1),
            ("ui.searchHist",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\SearchSettings","IsDeviceSearchHistoryEnabled",0),
            ("ui.searchMsa",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\SearchSettings","IsMSACloudSearchEnabled",0),
            ("ui.searchAad",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\SearchSettings","IsAADCloudSearchEnabled",0),
            ("ui.searchFeed",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\SearchSettings","IsMSFTSearchFeedEnabled",0),
            ("ui.searchLoc",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Search","AllowSearchToUseLocation",0),
            ("ui.softLanding",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager","SoftLandingEnabled",0),
            ("ui.lockOverlay",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager","RotatingLockScreenOverlayEnabled",0),
            ("ui.content338388",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager","SubscribedContent-338388Enabled",0),
            ("ui.content338389",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager","SubscribedContent-338389Enabled",0),
            ("ui.content353694",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager","SubscribedContent-353694Enabled",0),
            ("ui.content353696",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager","SubscribedContent-353696Enabled",0),
            ("ui.hover1",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","ExtendedUIHoverTime",100),
            ("ui.hover2",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","ThumbnailLivePreviewHoverTime",100),
            ("ui.taskbarAnimations",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","TaskbarAnimations",1),
            ("ui.taskbarAlign",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","TaskbarAl",0),
            ("ui.taskbarLabels",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","TaskbarGlomLevel",2),
            ("ui.taskbarLabelsMulti",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","MMTaskbarGlomLevel",2),
            ("ui.taskbarSmall",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","TaskbarSmallIcons",0),
            ("ui.darkApps",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",0),
            ("ui.darkSystem",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","SystemUsesLightTheme",0),
            ("ui.transparency",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","EnableTransparency",1),
            ("ui.listAlpha",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","ListviewAlphaSelect",1),
            ("ui.listShadow",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","ListviewShadow",1),
            ("ui.previewDesktop",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","DisablePreviewDesktop",0),
            ("ui.thumbs",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","IconsOnly",0),
            ("ui.thumbCache",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","DisableThumbnailCache",0),
            ("ui.previewHandlers",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","ShowPreviewHandlers",1),
            ("ui.networkThumbsDb",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer","DisableThumbsDBOnNetworkFolders",1),
            ("ui.separateProcess",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced","SeparateProcess",0),
            ("ui.colorPrev",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\DWM","ColorPrevalence",0)
        };
        foreach(var x in regs){ BackupReg(x.id,x.hive,x.path,x.name); SetDword(x.hive,x.path,x.name,x.value); }
        BackupReg("ui.accentInactive",RegistryHive.CurrentUser,@"Software\Microsoft\Windows\DWM","AccentColorInactive");
        DeleteValue(RegistryHive.CurrentUser,@"Software\Microsoft\Windows\DWM","AccentColorInactive");
        await RestartExplorerAsync(token);
        InvalidateAdaptiveScanCache();
        if (!UiBaselineMatches())
            throw new InvalidOperationException("Windows UI baseline read-back nepotvrdil cílové nastavení.");
    }

    public static async Task RestoreUiBaselineAsync(CancellationToken token)
    {
        string[] ids={"ui.launchTo","ui.hideExt","ui.syncNotif","ui.trackDocs","ui.taskview","ui.widgets","ui.chat","ui.altTab","ui.snap","ui.endTask","ui.searchWeb","ui.searchHist","ui.searchMsa","ui.searchAad","ui.searchFeed","ui.searchLoc","ui.softLanding","ui.lockOverlay","ui.content338388","ui.content338389","ui.content353694","ui.content353696","ui.hover1","ui.hover2","ui.taskbarAnimations","ui.taskbarAlign","ui.taskbarLabels","ui.taskbarLabelsMulti","ui.taskbarSmall","ui.darkApps","ui.darkSystem","ui.transparency","ui.listAlpha","ui.listShadow","ui.previewDesktop","ui.thumbs","ui.thumbCache","ui.previewHandlers","ui.networkThumbsDb","ui.separateProcess","ui.colorPrev","ui.accentInactive"};
        foreach(var id in ids) RestoreReg(id);
        await RestartExplorerAsync(token);
        InvalidateAdaptiveScanCache();
        string[] mismatched=ids.Where(id=>!RegistryBackupMatches(id)).ToArray();
        if(mismatched.Length>0)
            throw new InvalidOperationException("Windows UI restore read-back mismatch: "+string.Join(", ",mismatched));
    }


    public static async Task<string> RunPostLoginAuditAsync(int seconds, CancellationToken token)
    {
        seconds = Math.Clamp(seconds, 10, 300);
        // Both native process snapshots must be released in full, including
        // when the audit is cancelled or a process exits while being inspected.
        var before = ProcessSnapshotCleanup.Inspect(Process.GetProcesses, processes =>
        {
            var cpu = new Dictionary<int, double>(processes.Length);
            foreach (Process process in processes)
            {
                token.ThrowIfCancellationRequested();
                try { cpu[process.Id] = SafeCpu(process); }
                catch (InvalidOperationException) { /* Exited during snapshot. */ }
            }
            return cpu;
        });
        await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
        var rows = new List<(string Name,double Cpu,long Private)>();
        ProcessSnapshotCleanup.Inspect(Process.GetProcesses, processes =>
        {
            foreach (Process process in processes)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    double now = SafeCpu(process);
                    if (!before.TryGetValue(process.Id, out double old)) continue;
                    double delta = Math.Max(0, now - old);
                    if (delta < 0.05) continue;
                    long priv = 0; try { priv = process.PrivateMemorySize64; } catch { }
                    rows.Add((process.ProcessName, delta, priv));
                }
                catch (InvalidOperationException) { /* Process exited. */ }
                catch (System.ComponentModel.Win32Exception) { /* Protected process. */ }
            }
            return 0;
        });
        var top = rows.OrderByDescending(x => x.Cpu).Take(5)
            .Select(x => $"{x.Name} {x.Cpu:0.0}s/{x.Private/1048576d:0}MB");
        return string.Join(" • ", top);
    }

    private static double SafeCpu(Process p)
    {
        try { return p.TotalProcessorTime.TotalSeconds; } catch { return 0; }
    }


    private static bool UiBaselineMatches()
    {
        return ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo") == 1
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarMn") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations") == 1
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarGlomLevel") == 2
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "MMTaskbarGlomLevel") == 2
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarSmallIcons") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency") == 1
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewAlphaSelect") == 1
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewShadow") == 1
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisablePreviewDesktop") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer", "DisableThumbsDBOnNetworkFolders") == 1
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "SeparateProcess") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\DWM", "ColorPrevalence") == 0;
    }

    private static bool SearchCleanupMatches()
    {
        return ReadDword(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions") == 1
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDeviceSearchHistoryEnabled") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsMSACloudSearchEnabled") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsAADCloudSearchEnabled") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsMSFTSearchFeedEnabled") == 0
            && ReadDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "AllowSearchToUseLocation") == 0;
    }

    private static bool FileDialogProfileMatches()
    {
        string root = @"Software\Microsoft\Windows\CurrentVersion\Policies\Comdlg32\PlacesBar";
        string u = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.Equals(ReadString(RegistryHive.CurrentUser, root, "Place0"), Path.Combine(u, "Desktop"), StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadString(RegistryHive.CurrentUser, root, "Place1"), Path.Combine(u, "Downloads"), StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadString(RegistryHive.CurrentUser, root, "Place2"), Path.Combine(u, "Projects"), StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadString(RegistryHive.CurrentUser, root, "Place3"), Path.Combine(u, "Documents"), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task StageRunEntryAsync(string runName,string taskName,int delay,CancellationToken token)
    {
        using var run=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true);
        string? cmd=run?.GetValue(runName)?.ToString();
        var b=LoadBackup(); if(!b.Values.ContainsKey("run."+runName)){b.Values["run."+runName]=cmd;SaveBackup(b);}
        if(string.IsNullOrWhiteSpace(cmd)) return;
        string delayText = $"0000:{delay:00}";
        string safeCommand = cmd.Replace("\"", "\\\"");
        string taskArgs = $"/Create /TN \"{taskName}\" /TR \"{safeCommand}\" /SC ONLOGON /DELAY {delayText} /F";
        var result = await RunAsync("schtasks.exe", taskArgs, token, true);
        if (result.ExitCode == 0) run?.DeleteValue(runName,false);
    }


    private static bool IsVendorServiceName(string name) =>
        name.Equals("asus", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AsusROGLSLService", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ArmouryCrateDownloadTool", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("GoogleUpdaterService", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("GoogleUpdaterInternalService", StringComparison.OrdinalIgnoreCase);

    private static string[] GetVendorServiceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string fixedName in new[] { "asus", "AsusROGLSLService" })
            if (ServiceExists(fixedName)) names.Add(fixedName);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (root != null)
            {
                foreach (string name in root.GetSubKeyNames())
                {
                    if (name.StartsWith("GoogleUpdaterService", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("GoogleUpdaterInternalService", StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }
        }
        catch { }
        return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TaskDelayMatches(string name, int expectedSeconds)
    {
        try
        {
            string rel = name.TrimStart('\\').Replace('/', '\\');
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", rel);
            if (!File.Exists(path)) return false;
            string xml = File.ReadAllText(path);
            string expected = $"<Delay>PT{expectedSeconds}S</Delay>";
            return xml.Contains(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static int CountVendorServicesPresent() => GetVendorServiceNames().Length;
    private static int CountVendorServicesTuned() => GetVendorServiceNames().Count(s => QueryServiceStartMode(s) == "DEMAND_START");
    private static bool ServiceExists(string name)
    {
        try { using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name); return key != null; }
        catch { return false; }
    }

    private static bool HasRssOverrides(){ var nic=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType!=NetworkInterfaceType.Loopback).OrderByDescending(n=>n.Speed).FirstOrDefault(); if(nic==null)return false; var p=FindNicClassPath(nic.Id.Trim('{','}').ToLowerInvariant()); if(p==null)return false; using var k=Registry.LocalMachine.OpenSubKey(p); return RssOverrideNames.Any(n=>k?.GetValue(n)!=null);}
    private static int? ReadActiveMtu(){try{var nic=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType!=NetworkInterfaceType.Loopback).OrderByDescending(n=>n.Speed).FirstOrDefault();return nic?.GetIPProperties().GetIPv4Properties()?.Mtu;}catch{return null;}}
    private static string? FindNicClassPath(string guid){const string root=@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";using var k=Registry.LocalMachine.OpenSubKey(root);if(k==null)return null;foreach(var n in k.GetSubKeyNames()){using var s=k.OpenSubKey(n);if(string.Equals((s?.GetValue("NetCfgInstanceId")?.ToString()??"").Trim('{','}'),guid,StringComparison.OrdinalIgnoreCase))return root+"\\"+n;}return null;}
    private static async Task<bool> PingDfAsync(string host,int mtu,CancellationToken token){var r=await RunAsync("ping.exe",$"-n 2 -w 1200 -f -l {mtu-28} {host}",token,true);return r.ExitCode==0 && r.Output.Contains("TTL=",StringComparison.OrdinalIgnoreCase);}
    private static async Task RestartExplorerAsync(CancellationToken token){await RunAsync("taskkill.exe","/F /IM explorer.exe",token,true);await Task.Delay(250,token);Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true});}
    private static bool TaskExists(string name)
    {
        try
        {
            string rel = name.TrimStart('\\').Replace('/', '\\');
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", rel);
            return File.Exists(path);
        }
        catch { return false; }
    }

    private static bool? ReadTaskEnabledState(string name)
    {
        try
        {
            string rel = name.TrimStart('\\').Replace('/', '\\');
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", rel);
            if (!File.Exists(path)) return null;
            string xml = File.ReadAllText(path);
            if (xml.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase)) return false;
            if (xml.Contains("<Enabled>true</Enabled>", StringComparison.OrdinalIgnoreCase)) return true;
            return true;
        }
        catch { return null; }
    }

    private static string QueryServiceStartMode(string s)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + s);
            object? raw = key?.GetValue("Start");
            if (raw == null) return string.Empty;
            int start = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            return start switch { 2 => "AUTO_START", 3 => "DEMAND_START", 4 => "DISABLED", _ => start.ToString(CultureInfo.InvariantCulture) };
        }
        catch { return string.Empty; }
    }
    private static string ScMode(string mode)=>mode switch{"DISABLED"=>"disabled","AUTO_START"=>"auto","DEMAND_START"=>"demand",_=>"demand"};

    // Native SCM change removes sc.exe process startup and locale-dependent
    // output parsing. Preserve existing caller backups and restore semantics.
    private static async Task TryStopVendorServiceNativeAsync(string name, CancellationToken token)
    {
        // Previous sc.exe /stop command deliberately ignored failures: preserve
        // that behavior while avoiding process startup and locale-specific output.
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var service = new System.ServiceProcess.ServiceController(name);
                service.Refresh();
                if (service.Status is System.ServiceProcess.ServiceControllerStatus.Stopped or
                    System.ServiceProcess.ServiceControllerStatus.StopPending) return;
                if (service.CanStop)
                {
                    service.Stop();
                    service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped,
                        TimeSpan.FromSeconds(15));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Info("modernizer", "Vendor service stop skipped: " + name + ": " + ex.Message);
            }
        }, token).ConfigureAwait(false);
    }

    private static async Task SetServiceStartModeNativeAsync(string service, string mode, CancellationToken token)
    {
        uint target = mode switch
        {
            "auto" => 2u,
            "demand" => 3u,
            "disabled" => 4u,
            _ => throw new ArgumentException("Unsupported service start mode: " + mode)
        };
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            string before = QueryServiceStartMode(service);
            if (string.IsNullOrWhiteSpace(before))
                throw new InvalidOperationException("Service not found: " + service);
            string expected = target switch { 2u => "AUTO_START", 3u => "DEMAND_START", _ => "DISABLED" };
            if (string.Equals(before, expected, StringComparison.OrdinalIgnoreCase)) return;
            if (!NativeCore.ChangeServiceStartType(service, target, out int error))
                throw new System.ComponentModel.Win32Exception(error, "Service configuration failed: " + service);
            string actual = QueryServiceStartMode(service);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Service start-mode read-back mismatch: " + service +
                    " expected=" + expected + " actual=" + actual);
        }, token).ConfigureAwait(false);
    }
    private static int Round256(double v)=>(int)(Math.Round(v/256d)*256);
    private static int? ReadDword(RegistryHive h,string p,string n){try{using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.OpenSubKey(p);object? v=k?.GetValue(n);return v==null?null:Convert.ToInt32(v,CultureInfo.InvariantCulture);}catch{return null;}}
    private static string? ReadString(RegistryHive h,string p,string n){try{using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.OpenSubKey(p);return k?.GetValue(n,null,RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();}catch{return null;}}
    private static void SetDword(RegistryHive h,string p,string n,int v){using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.CreateSubKey(p,true);k.SetValue(n,v,RegistryValueKind.DWord);}
    private static void SetString(RegistryHive h,string p,string n,string v){using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.CreateSubKey(p,true);k.SetValue(n,v,RegistryValueKind.String);}
    private static void DeleteValue(RegistryHive h,string p,string n){using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.OpenSubKey(p,true);k?.DeleteValue(n,false);}
    private static void BackupReg(string id,RegistryHive h,string p,string n){lock(Gate){var st=LoadBackup();if(st.Values.ContainsKey(id+".exists"))return;using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.OpenSubKey(p);bool ex=k?.GetValueNames().Contains(n,StringComparer.OrdinalIgnoreCase)==true;st.Values[id+".hive"]=h.ToString();st.Values[id+".path"]=p;st.Values[id+".name"]=n;st.Values[id+".exists"]=ex?"1":"0";if(ex){object? value=k!.GetValue(n,null,RegistryValueOptions.DoNotExpandEnvironmentNames);RegistryValueKind kind=k.GetValueKind(n);st.Values[id+".kind"]=kind.ToString();st.Values[id+".value"]=kind==RegistryValueKind.Binary&&value is byte[] bytes?Convert.ToBase64String(bytes):value?.ToString();}SaveBackup(st);}}
    private static void RestoreReg(string id){lock(Gate){var st=LoadBackup();if(!st.Values.TryGetValue(id+".path",out var p)||!st.Values.TryGetValue(id+".name",out var n)||p==null||n==null)return;var h=Enum.Parse<RegistryHive>(st.Values[id+".hive"]!);using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.CreateSubKey(p,true);if(st.Values[id+".exists"]!="1"){k.DeleteValue(n,false);return;}RegistryValueKind kind=Enum.TryParse(st.Values.GetValueOrDefault(id+".kind"),out RegistryValueKind parsed)?parsed:RegistryValueKind.String;string raw=st.Values.GetValueOrDefault(id+".value")??"";object value=kind switch{RegistryValueKind.DWord=>int.TryParse(raw,out var i)?i:0,RegistryValueKind.QWord=>long.TryParse(raw,out var l)?l:0L,RegistryValueKind.Binary=>Convert.FromBase64String(raw),_=>raw};k.SetValue(n,value,kind);}}
    private static bool RegistryBackupMatches(string id)
    {
        var st=LoadBackup();
        if(!st.Values.TryGetValue(id+".path",out var p)||!st.Values.TryGetValue(id+".name",out var n)||p==null||n==null) return true;
        if(!Enum.TryParse(st.Values.GetValueOrDefault(id+".hive"),out RegistryHive h)) return false;
        using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);
        using var k=b.OpenSubKey(p);
        bool exists=k?.GetValueNames().Contains(n,StringComparer.OrdinalIgnoreCase)==true;
        bool expectedExists=st.Values.GetValueOrDefault(id+".exists")=="1";
        if(exists!=expectedExists) return false;
        if(!expectedExists) return true;
        object? value=k!.GetValue(n,null,RegistryValueOptions.DoNotExpandEnvironmentNames);
        RegistryValueKind kind=k.GetValueKind(n);
        string actual=kind==RegistryValueKind.Binary&&value is byte[] bytes?Convert.ToBase64String(bytes):value?.ToString()??"";
        string expected=st.Values.GetValueOrDefault(id+".value")??"";
        string expectedKind=st.Values.GetValueOrDefault(id+".kind")??kind.ToString();
        return kind.ToString().Equals(expectedKind,StringComparison.OrdinalIgnoreCase) && actual==expected;
    }
    private static void BackupSimple(string id,string? value){lock(Gate){var st=LoadBackup();if(!st.Values.ContainsKey(id)){st.Values[id]=value;SaveBackup(st);}}}
    private static WindowsModernizerBackup LoadBackup(){try{if(File.Exists(BackupFile))return JsonSerializer.Deserialize<WindowsModernizerBackup>(File.ReadAllText(BackupFile))??new();}catch{}return new();}
    private static void SaveBackup(WindowsModernizerBackup b){Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);AtomicFileService.WriteText(BackupFile,JsonSerializer.Serialize(b,IndentedJson));}
    private sealed record RunResult(int ExitCode,string Output);
    private static async Task<RunResult> RunAsync(string file, string args, CancellationToken token, bool ignoreExit = false)
    {
        TimeSpan timeout = string.Equals(Path.GetFileName(file), "defrag.exe", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(5);
        var result = await ProcessRunner.RunAsync(file, args, timeout, token,
            logNonZeroExitAsError: !ignoreExit).ConfigureAwait(false);
        string all = result.StdOut + result.StdErr;
        if (!result.Success && !ignoreExit) throw new InvalidOperationException(all);
        return new RunResult(result.ExitCode, all);
    }
}
