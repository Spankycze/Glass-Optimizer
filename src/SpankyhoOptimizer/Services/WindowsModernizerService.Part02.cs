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
}
