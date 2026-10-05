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
    bool RssOverridesPresent,
    bool FirefoxDynamicRamConfigured,
    bool? FirefoxDefaultAgentEnabled);


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
            HasRssOverrides(),
            FirefoxHasDynamicRamBlock(),
            ReadFirefoxDefaultAgentEnabled());
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
                bool desired = kv.Key is "Terminal" or "CMD" or "PowerShell" or "Admin PowerShell" or "Copy path" or "SHA256" or "New empty file" or "VS Code" or "Advanced startup" or "Restart Explorer";
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
                else if (task.Name.Contains("Firefox Default Browser Agent", StringComparison.OrdinalIgnoreCase))
                    Add("firefox.agent", "Firefox", task.Name, "Default Browser Agent task je zapnutý.", AdaptiveTweakState.Needed, true, AdaptiveImpact.Low);
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

            bool firefox = FirefoxProfileExists();
            if (!firefox)
                Add("firefox.ram", "Firefox", "Firefox Dynamic RAM", "Firefox profil nebyl nalezen.", AdaptiveTweakState.NotApplicable);
            else
            {
                Add("firefox.ram", "Firefox", "Firefox Dynamic RAM",
                    s.FirefoxDynamicRamConfigured ? "Dynamic RAM profil je aktivní." : "Dynamic RAM profil není aktivní.",
                    s.FirefoxDynamicRamConfigured ? AdaptiveTweakState.AlreadyOptimal : AdaptiveTweakState.Needed, true, AdaptiveImpact.Medium);
                if (s.FirefoxDefaultAgentEnabled == false)
                    Add("firefox.agent-state", "Firefox", "Firefox Default Browser Agent", "Task je vypnutý.", AdaptiveTweakState.AlreadyOptimal);
                else if (s.FirefoxDefaultAgentEnabled == true && !findings.Any(x => x.Id == "firefox.agent"))
                    Add("firefox.agent", "Firefox", "Firefox Default Browser Agent", "Task je zapnutý.", AdaptiveTweakState.Needed, true);
            }

            int fixedDrives = DriveInfo.GetDrives().Count(d => d.IsReady && d.DriveType == DriveType.Fixed);
            Add("storage.analyze", "Storage", "Storage analyze / reliability", fixedDrives > 0 ? $"{fixedDrives} fixed drive(s) lze analyzovat." : "Žádný fixed drive.", fixedDrives > 0 ? AdaptiveTweakState.ManualReview : AdaptiveTweakState.NotApplicable);
            Add("storage.retrim", "Storage", "SSD/NVMe Retrim", fixedDrives > 0 ? "Retrim je dostupný jako ruční maintenance akce." : "Žádný fixed drive.", fixedDrives > 0 ? AdaptiveTweakState.ManualReview : AdaptiveTweakState.NotApplicable);

            long crashDumpBytes = DirectorySizeSafe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"), 20000);
            Add("cleanup.crashdumps", "Cleaner", "Crash dumps",
                crashDumpBytes > 0 ? $"CrashDumps používají {crashDumpBytes / 1048576d:0.0} MB." : "CrashDumps jsou prázdné nebo neexistují.",
                crashDumpBytes >= 100 * 1048576L ? AdaptiveTweakState.ManualReview : AdaptiveTweakState.AlreadyOptimal);

            Add("audit.postlogin", "Diagnostics", "60s post-login audit", "CPU/RAM startup sampler je dostupný na vyžádání.", AdaptiveTweakState.ManualReview);
            Add("audit.firefox-soak", "Diagnostics", "Firefox RAM soak", "RAM growth audit je dostupný na vyžádání.", firefox ? AdaptiveTweakState.ManualReview : AdaptiveTweakState.NotApplicable);

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
                case "firefox.ram":
                    ApplyFirefoxDynamicRam();
                    applied.Add(finding.Title);
                    break;
                case "firefox.agent":
                    await SetFirefoxDefaultAgentEnabledAsync(false, token).ConfigureAwait(false);
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
        var first = new Dictionary<int, (string Name,double Cpu,long Private)>();
        foreach (var p in Process.GetProcesses())
        {
            try { first[p.Id] = (p.ProcessName, p.TotalProcessorTime.TotalSeconds, p.PrivateMemorySize64); }
            catch { }
            finally { p.Dispose(); }
        }
        await Task.Delay(250, token).ConfigureAwait(false);
        var agg = new Dictionary<string, (int Count,long Private,double Cpu)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!first.TryGetValue(p.Id, out var a)) continue;
                double cpu = Math.Max(0, p.TotalProcessorTime.TotalSeconds - a.Cpu);
                long priv = p.PrivateMemorySize64;
                if (!agg.TryGetValue(p.ProcessName, out var cur)) cur = default;
                agg[p.ProcessName] = (cur.Count + 1, cur.Private + priv, cur.Cpu + cpu);
            }
            catch { }
            finally { p.Dispose(); }
        }
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

    private static bool FirefoxProfileExists()
    {
        try { _ = ResolveFirefoxProfile(); return true; } catch { return false; }
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
        await RunAsync("sc.exe", $"config \"{service}\" start= demand", token, true).ConfigureAwait(false);
    }

    private static async Task ApplyArmouryDownloadOnlyAsync(CancellationToken token)
    {
        string old = QueryServiceStartMode("ArmouryCrateDownloadTool");
        if (string.IsNullOrWhiteSpace(old)) return;
        BackupSimple("svc.ArmouryCrateDownloadTool", old);
        await RunAsync("sc.exe", "stop ArmouryCrateDownloadTool", token, true).ConfigureAwait(false);
        await RunAsync("sc.exe", "config ArmouryCrateDownloadTool start= disabled", token, true).ConfigureAwait(false);
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
            case "Copy path": ContextMenuService.SetCopyPath(true); break;
            case "SHA256": ContextMenuService.SetSha256(true); break;
            case "New empty file": ContextMenuService.SetNewEmptyFile(true); break;
            case "VS Code": ContextMenuService.SetVsCode(true); break;
            case "Advanced startup": ContextMenuService.SetAdvancedStartup(true); break;
            case "Restart Explorer": ContextMenuService.SetRestartExplorer(true); break;
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
        return "Desktop • Downloads • Projects • Documents • This PC";
    }

    public static void ClearFileDialogHistory()
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32", writable:true);
        if (root == null) return;
        foreach (var n in new[]{"OpenSavePidlMRU","LastVisitedPidlMRU","LastVisitedPidlMRULegacy"})
            try { root.DeleteSubKeyTree(n, false); } catch {}
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
        BackupReg("startup.delay", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec");
        SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0);
        await StageRunEntryAsync("Battle.net", "WindowsModernizer - Battle.net delayed", 20, token);
        await RunAsync("schtasks.exe", "/Delete /TN \"WindowsModernizer - Spotify delayed\" /F", token, ignoreExit:true);
        InvalidateAdaptiveScanCache();
    }

    public static async Task RestoreStartupProfileAsync(CancellationToken token)
    {
        RestoreReg("startup.delay");
        var b = LoadBackup();
        foreach (var pair in new[]{("Battle.net","WindowsModernizer - Battle.net delayed")})
        {
            await RunAsync("schtasks.exe", $"/Delete /TN \"{pair.Item2}\" /F", token, ignoreExit:true);
            if (b.Values.TryGetValue("run."+pair.Item1, out var cmd))
            {
                using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (cmd == null) k?.DeleteValue(pair.Item1, false); else k?.SetValue(pair.Item1, cmd, RegistryValueKind.String);
            }
        }
        InvalidateAdaptiveScanCache();
    }

    public static async Task ApplySystemMaintenanceAsync(CancellationToken token)
    {
        BackupReg("maint.disabled", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "MaintenanceDisabled");
        SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "MaintenanceDisabled", 0);
        BackupSimple("svc.lltdio", QueryServiceStartMode("lltdio"));
        await RunAsync("sc.exe", "config lltdio start= demand", token);
        InvalidateAdaptiveScanCache();
    }

    public static async Task RestoreSystemMaintenanceAsync()
    {
        RestoreReg("maint.disabled");
        var b = LoadBackup();
        if (b.Values.TryGetValue("svc.lltdio", out var mode) && !string.IsNullOrWhiteSpace(mode))
            await RunAsync("sc.exe", "config lltdio start= " + ScMode(mode), CancellationToken.None, ignoreExit:true);
        InvalidateAdaptiveScanCache();
    }

    public static async Task ApplyVendorIdleAsync(CancellationToken token)
    {
        foreach (var s in GetVendorServiceNames())
        {
            string old = QueryServiceStartMode(s);
            if (!string.IsNullOrWhiteSpace(old))
            {
                BackupSimple("svc."+s, old);
                await RunAsync("sc.exe", $"config \"{s}\" start= demand", token, true);
            }
        }
        string arm = QueryServiceStartMode("ArmouryCrateDownloadTool");
        if (!string.IsNullOrWhiteSpace(arm))
        {
            BackupSimple("svc.ArmouryCrateDownloadTool", arm);
            await RunAsync("sc.exe", "stop ArmouryCrateDownloadTool", token, true);
            await RunAsync("sc.exe", "config ArmouryCrateDownloadTool start= disabled", token, true);
        }
        BackupSimple("task.P508PowerAgent_sdk", TaskExists(@"\ASUS\P508PowerAgent_sdk") ? "1":"0");
        await RunAsync("schtasks.exe", "/Change /TN \"\\ASUS\\P508PowerAgent_sdk\" /Disable", token, true);
        InvalidateAdaptiveScanCache();
    }

    public static async Task RestoreVendorIdleAsync(CancellationToken token)
    {
        var b = LoadBackup();
        var serviceKeys = b.Values.Keys
            .Where(k => k.StartsWith("svc.", StringComparison.OrdinalIgnoreCase))
            .Select(k => k["svc.".Length..])
            .Where(IsVendorServiceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var s in serviceKeys)
            if (b.Values.TryGetValue("svc."+s, out var m) && !string.IsNullOrWhiteSpace(m))
                await RunAsync("sc.exe", $"config \"{s}\" start= {ScMode(m)}", token, true);
        if (b.Values.TryGetValue("task.P508PowerAgent_sdk", out var was) && was=="1")
            await RunAsync("schtasks.exe", "/Change /TN \"\\ASUS\\P508PowerAgent_sdk\" /Enable", token, true);
        InvalidateAdaptiveScanCache();
    }

    public static async Task<int> OptimizeNetworkAsync(CancellationToken token)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.NetworkInterfaceType!=NetworkInterfaceType.Loopback && n.OperationalStatus==OperationalStatus.Up)
            .OrderByDescending(n=>n.Speed).FirstOrDefault() ?? throw new InvalidOperationException("Aktivní síťový adaptér nebyl nalezen.");
        string name = nic.Name;
        int oldMtu = ReadActiveMtu() ?? 1500;
        BackupSimple("network.mtu", oldMtu.ToString(CultureInfo.InvariantCulture));
        string guid = nic.Id.Trim('{','}').ToLowerInvariant();
        string classPath = FindNicClassPath(guid) ?? throw new InvalidOperationException("NIC class registry key nebyl nalezen.");
        foreach (var n in new[]{"*RssBaseProcGroup","*RssBaseProcNumber","*RSSMaxProcGroup","*RssMaxProcNumber"})
        {
            BackupReg("network."+n, RegistryHive.LocalMachine, classPath, n);
            DeleteValue(RegistryHive.LocalMachine, classPath, n);
        }
        string gw = nic.GetIPProperties().GatewayAddresses.FirstOrDefault(x=>x.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "1.1.1.1";
        foreach (int mtu in new[]{1500,1492,1488})
        {
            await RunAsync("netsh.exe", $"interface ipv4 set subinterface \"{name}\" mtu={mtu} store=active", token);
            bool g = await PingDfAsync(gw, mtu, token);
            bool i = await PingDfAsync("1.1.1.1", mtu, token);
            if (g && i)
            {
                await RunAsync("netsh.exe", $"interface ipv4 set subinterface \"{name}\" mtu={mtu} store=persistent", token);
                InvalidateAdaptiveScanCache();
                return mtu;
            }
        }
        await RunAsync("netsh.exe", $"interface ipv4 set subinterface \"{name}\" mtu={oldMtu} store=active", token, true);
        InvalidateAdaptiveScanCache();
        return oldMtu;
    }

    public static async Task RestoreNetworkAsync(CancellationToken token)
    {
        var b = LoadBackup();
        foreach (var n in new[]{"*RssBaseProcGroup","*RssBaseProcNumber","*RSSMaxProcGroup","*RssMaxProcNumber"}) RestoreReg("network."+n);
        if (b.Values.TryGetValue("network.mtu", out var s) && int.TryParse(s, out int mtu))
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.NetworkInterfaceType!=NetworkInterfaceType.Loopback && n.OperationalStatus==OperationalStatus.Up).OrderByDescending(n=>n.Speed).FirstOrDefault();
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
        var sb = new StringBuilder();
        foreach (var d in DriveInfo.GetDrives().Where(d=>d.IsReady && d.DriveType==DriveType.Fixed))
        {
            string letter=d.Name.TrimEnd('\\');
            var r=await RunAsync("defrag.exe", $"{letter} /L /U /V", token, true);
            sb.Append(letter).Append(": ").Append(r.ExitCode==0?"Retrim OK":"check").Append(" | ");
        }
        return sb.ToString().TrimEnd(' ', '|');
    }

    public static string ApplyFirefoxDynamicRam()
    {
        string profile=ResolveFirefoxProfile();
        string userJs=Path.Combine(profile,"user.js");
        Directory.CreateDirectory(profile);
        if (!File.Exists(userJs)) File.WriteAllText(userJs,"");
        BackupSimple("firefox.userjs.backup", EnsureFirefoxBackup(userJs));
        int totalMb=(int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes/1024/1024);
        if (totalMb<=0) totalMb=16384;
        int cacheKb, inactiveMs, undo, lowCommit;
        if(totalMb<=8192){lowCommit=2048;cacheKb=131072;inactiveMs=300000;undo=7;}
        else if(totalMb<=16384){lowCommit=Round256(totalMb*0.25);cacheKb=262144;inactiveMs=300000;undo=10;}
        else if(totalMb<=32768){lowCommit=Round256(totalMb*0.25);cacheKb=393216;inactiveMs=900000;undo=15;}
        else {lowCommit=Math.Min(8192,(int)Math.Round(totalMb*0.20));cacheKb=524288;inactiveMs=1200000;undo=20;}
        string start="// === RAM OPTIMIZATION START ===", end="// === RAM OPTIMIZATION END ===";
        string block=$"{start}\nuser_pref(\"browser.tabs.unloadOnLowMemory\", true);\nuser_pref(\"browser.tabs.min_inactive_duration_before_unload\", {inactiveMs});\nuser_pref(\"browser.cache.memory.capacity\", {cacheKb});\nuser_pref(\"browser.sessionstore.max_tabs_undo\", {undo});\nuser_pref(\"browser.sessionstore.restore_on_demand\", true);\nuser_pref(\"browser.sessionstore.restore_pinned_tabs_on_demand\", true);\nuser_pref(\"browser.sessionstore.restore_hidden_tabs\", false);\nuser_pref(\"browser.newtab.preload\", false);\nuser_pref(\"default-browser-agent.enabled\", false);\nuser_pref(\"browser.low_commit_space_threshold_mb\", {lowCommit});\n{end}";
        string txt=File.ReadAllText(userJs);
        int a=txt.IndexOf(start,StringComparison.Ordinal), z=a>=0?txt.IndexOf(end,a,StringComparison.Ordinal):-1;
        if(a>=0 && z>=0) txt=txt[..a]+block+txt[(z+end.Length)..]; else txt=txt.TrimEnd()+Environment.NewLine+block+Environment.NewLine;
        File.WriteAllText(userJs,txt,new UTF8Encoding(false));
        InvalidateAdaptiveScanCache();
        return $"RAM {Math.Round(totalMb/1024d,1)} GB • cache {cacheKb/1024} MB • inactive {inactiveMs/60000} min • undo {undo}";
    }

    public static bool RestoreFirefoxDynamicRam()
    {
        var b=LoadBackup();
        if(!b.Values.TryGetValue("firefox.userjs.backup", out var backup) || string.IsNullOrWhiteSpace(backup) || !File.Exists(backup)) return false;
        string profile=ResolveFirefoxProfile(); File.Copy(backup,Path.Combine(profile,"user.js"),true); InvalidateAdaptiveScanCache(); return true;
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
    }

    public static async Task RestoreUiBaselineAsync(CancellationToken token)
    {
        foreach(var id in new[]{"ui.launchTo","ui.hideExt","ui.syncNotif","ui.trackDocs","ui.taskview","ui.widgets","ui.chat","ui.altTab","ui.snap","ui.endTask","ui.searchWeb","ui.searchHist","ui.searchMsa","ui.searchAad","ui.searchFeed","ui.searchLoc","ui.softLanding","ui.lockOverlay","ui.content338388","ui.content338389","ui.content353694","ui.content353696","ui.hover1","ui.hover2","ui.taskbarAnimations","ui.listAlpha","ui.listShadow","ui.previewDesktop","ui.thumbs","ui.thumbCache","ui.previewHandlers","ui.networkThumbsDb","ui.separateProcess","ui.colorPrev","ui.accentInactive"}) RestoreReg(id);
        await RestartExplorerAsync(token);
        InvalidateAdaptiveScanCache();
    }


    public static async Task<string> SetFirefoxDefaultAgentEnabledAsync(bool enabled, CancellationToken token)
    {
        var svc = new TaskSchedulerService();
        var tasks = await svc.GetBasicAsync(forceRefresh: true, token).ConfigureAwait(false);
        var item = tasks.FirstOrDefault(x => x.Name.Contains("Firefox Default Browser Agent", StringComparison.OrdinalIgnoreCase));
        if (item == null) return "Firefox Default Browser Agent task nebyl nalezen.";
        BackupSimple("firefox.agent.path", item.Path);
        BackupSimple("firefox.agent.enabled", item.Enabled ? "1" : "0");
        await svc.SetEnabledAsync(item.Path, enabled, token).ConfigureAwait(false);
        InvalidateAdaptiveScanCache();
        return enabled ? "Firefox Default Browser Agent zapnutý." : "Firefox Default Browser Agent vypnutý.";
    }

    public static async Task<string> RestoreFirefoxDefaultAgentAsync(CancellationToken token)
    {
        var b = LoadBackup();
        if (!b.Values.TryGetValue("firefox.agent.path", out var path) || string.IsNullOrWhiteSpace(path))
            return "Backup Firefox Default Browser Agent nebyl nalezen.";
        bool enabled = b.Values.GetValueOrDefault("firefox.agent.enabled") == "1";
        await new TaskSchedulerService().SetEnabledAsync(path, enabled, token).ConfigureAwait(false);
        InvalidateAdaptiveScanCache();
        return "Firefox Default Browser Agent obnoven do původního stavu.";
    }

    public static async Task<string> RunFirefoxSoakAuditAsync(int seconds, CancellationToken token)
    {
        seconds = Math.Clamp(seconds, 10, 1800);
        long startPrivate = FirefoxPrivateBytes();
        long maxPrivate = startPrivate;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            long now = FirefoxPrivateBytes();
            if (now > maxPrivate) maxPrivate = now;
        }
        long endPrivate = FirefoxPrivateBytes();
        long delta = endPrivate - startPrivate;
        return $"Firefox private: start {startPrivate / 1048576d:0} MB • end {endPrivate / 1048576d:0} MB • delta {delta / 1048576d:+0;-0;0} MB • peak {maxPrivate / 1048576d:0} MB";
    }

    public static async Task<string> RunPostLoginAuditAsync(int seconds, CancellationToken token)
    {
        seconds = Math.Clamp(seconds, 10, 300);
        var before = Process.GetProcesses().ToDictionary(p => p.Id, p => SafeCpu(p));
        await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
        var rows = new List<(string Name,double Cpu,long Private)>();
        foreach (var p in Process.GetProcesses())
        {
            double now = SafeCpu(p);
            if (!before.TryGetValue(p.Id, out double old)) continue;
            double delta = Math.Max(0, now - old);
            if (delta < 0.05) continue;
            long priv = 0; try { priv = p.PrivateMemorySize64; } catch {}
            rows.Add((p.ProcessName, delta, priv));
        }
        var top = rows.OrderByDescending(x => x.Cpu).Take(5)
            .Select(x => $"{x.Name} {x.Cpu:0.0}s/{x.Private/1048576d:0}MB");
        return string.Join(" • ", top);
    }

    private static long FirefoxPrivateBytes()
    {
        long total = 0;
        foreach (var p in Process.GetProcessesByName("firefox"))
            try { total += p.PrivateMemorySize64; } catch {}
        return total;
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

    private static bool? ReadFirefoxDefaultAgentEnabled()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks", "Mozilla");
            if (!Directory.Exists(dir)) return null;
            string? task = Directory.GetFiles(dir, "Firefox Default Browser Agent*").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (task == null) return null;
            string xml = File.ReadAllText(task);
            if (xml.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase)) return false;
            if (xml.Contains("<Enabled>true</Enabled>", StringComparison.OrdinalIgnoreCase)) return true;
            return true;
        }
        catch { return null; }
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
    private static bool FirefoxHasDynamicRamBlock(){try{return File.ReadAllText(Path.Combine(ResolveFirefoxProfile(),"user.js")).Contains("// === RAM OPTIMIZATION START ===",StringComparison.Ordinal);}catch{return false;}}
    private static string ResolveFirefoxProfile(){string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Mozilla","Firefox");string ini=Path.Combine(root,"profiles.ini");foreach(var l in File.ReadLines(ini)){if(l.StartsWith("Default=",StringComparison.OrdinalIgnoreCase)){string rel=l[8..].Trim().Replace('/','\\');string p=Path.Combine(root,rel);if(Directory.Exists(p))return p;}}var d=Directory.GetDirectories(Path.Combine(root,"Profiles"),"*.default-release").FirstOrDefault();return d??throw new DirectoryNotFoundException("Firefox default-release profil nenalezen.");}
    private static string EnsureFirefoxBackup(string userJs){string p=userJs+".spanky-original";if(!File.Exists(p))File.Copy(userJs,p,true);return p;}
    private static int Round256(double v)=>(int)(Math.Round(v/256d)*256);
    private static int? ReadDword(RegistryHive h,string p,string n){try{using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.OpenSubKey(p);object? v=k?.GetValue(n);return v==null?null:Convert.ToInt32(v,CultureInfo.InvariantCulture);}catch{return null;}}
    private static string? ReadString(RegistryHive h,string p,string n){try{using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.OpenSubKey(p);return k?.GetValue(n,null,RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();}catch{return null;}}
    private static void SetDword(RegistryHive h,string p,string n,int v){using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.CreateSubKey(p,true);k.SetValue(n,v,RegistryValueKind.DWord);}
    private static void SetString(RegistryHive h,string p,string n,string v){using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.CreateSubKey(p,true);k.SetValue(n,v,RegistryValueKind.String);}
    private static void DeleteValue(RegistryHive h,string p,string n){using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.OpenSubKey(p,true);k?.DeleteValue(n,false);}
    private static void BackupReg(string id,RegistryHive h,string p,string n){lock(Gate){var st=LoadBackup();if(st.Values.ContainsKey(id+".exists"))return;using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.OpenSubKey(p);bool ex=k?.GetValueNames().Contains(n,StringComparer.OrdinalIgnoreCase)==true;st.Values[id+".hive"]=h.ToString();st.Values[id+".path"]=p;st.Values[id+".name"]=n;st.Values[id+".exists"]=ex?"1":"0";if(ex){object? value=k!.GetValue(n,null,RegistryValueOptions.DoNotExpandEnvironmentNames);RegistryValueKind kind=k.GetValueKind(n);st.Values[id+".kind"]=kind.ToString();st.Values[id+".value"]=kind==RegistryValueKind.Binary&&value is byte[] bytes?Convert.ToBase64String(bytes):value?.ToString();}SaveBackup(st);}}
    private static void RestoreReg(string id){lock(Gate){var st=LoadBackup();if(!st.Values.TryGetValue(id+".path",out var p)||!st.Values.TryGetValue(id+".name",out var n)||p==null||n==null)return;var h=Enum.Parse<RegistryHive>(st.Values[id+".hive"]!);using var b=RegistryKey.OpenBaseKey(h,RegistryView.Registry64);using var k=b.CreateSubKey(p,true);if(st.Values[id+".exists"]!="1"){k.DeleteValue(n,false);return;}RegistryValueKind kind=Enum.TryParse(st.Values.GetValueOrDefault(id+".kind"),out RegistryValueKind parsed)?parsed:RegistryValueKind.String;string raw=st.Values.GetValueOrDefault(id+".value")??"";object value=kind switch{RegistryValueKind.DWord=>int.TryParse(raw,out var i)?i:0,RegistryValueKind.QWord=>long.TryParse(raw,out var l)?l:0L,RegistryValueKind.Binary=>Convert.FromBase64String(raw),_=>raw};k.SetValue(n,value,kind);}}
    private static void BackupSimple(string id,string? value){lock(Gate){var st=LoadBackup();if(!st.Values.ContainsKey(id)){st.Values[id]=value;SaveBackup(st);}}}
    private static WindowsModernizerBackup LoadBackup(){try{if(File.Exists(BackupFile))return JsonSerializer.Deserialize<WindowsModernizerBackup>(File.ReadAllText(BackupFile))??new();}catch{}return new();}
    private static void SaveBackup(WindowsModernizerBackup b){Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);File.WriteAllText(BackupFile,JsonSerializer.Serialize(b,IndentedJson));}
    private sealed record RunResult(int ExitCode,string Output);
    private static async Task<RunResult> RunAsync(string file,string args,CancellationToken token,bool ignoreExit=false){using var p=Process.Start(new ProcessStartInfo(file,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;Task<string> o=p.StandardOutput.ReadToEndAsync(token),e=p.StandardError.ReadToEndAsync(token);await p.WaitForExitAsync(token);string all=(await o)+(await e);if(p.ExitCode!=0&&!ignoreExit)throw new InvalidOperationException(all);return new(p.ExitCode,all);}
}
