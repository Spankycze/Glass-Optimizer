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

public static partial class WindowsModernizerService
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
}
