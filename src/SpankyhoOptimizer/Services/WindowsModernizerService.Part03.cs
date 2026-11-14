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
