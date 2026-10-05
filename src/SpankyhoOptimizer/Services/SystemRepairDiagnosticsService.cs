using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace SpankyhoOptimizer.Services;

public sealed record RepairDiagnostic(string Area, string State, bool Healthy, string Detail, string? Expected = null, string? Actual = null);

public static class SystemRepairDiagnosticsService
{
    private static readonly object CacheLock = new();
    private static IReadOnlyList<RepairDiagnostic>? _scanCache;
    private static DateTime _scanCacheAt;
    private static Task<IReadOnlyList<RepairDiagnostic>>? _scanInFlight;

    public static async Task<IReadOnlyList<RepairDiagnostic>> ScanAsync(CancellationToken token = default)
    {
        if (!WorkerExecutionContext.IsWorker) return await WorkerClientService.RunAsync<RepairDiagnostic[]>("repair-scan", timeout: TimeSpan.FromMinutes(2), token: token).ConfigureAwait(false);
        return await ScanLocalAsync(token);
    }

    public static async Task<IReadOnlyList<RepairDiagnostic>> ScanCachedAsync(CancellationToken token = default)
    {
        lock (CacheLock)
        {
            if (_scanCache != null && DateTime.UtcNow - _scanCacheAt < TimeSpan.FromSeconds(20)) return _scanCache;
            _scanInFlight ??= LoadCachedCoreAsync();
        }
        Task<IReadOnlyList<RepairDiagnostic>> task;
        lock (CacheLock) task = _scanInFlight!;
        return await task.WaitAsync(token).ConfigureAwait(false);
    }

    public static void InvalidateCache()
    {
        lock (CacheLock) { _scanCache = null; _scanCacheAt = default; }
    }

    public static async Task<RepairDiagnostic?> VerifyAreaAsync(string area, CancellationToken token = default)
    {
        InvalidateCache();
        var fresh = await ScanAsync(token).ConfigureAwait(false);
        return fresh.FirstOrDefault(x => string.Equals(x.Area, area, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyList<RepairDiagnostic>> LoadCachedCoreAsync()
    {
        try
        {
            var result = await ScanAsync(CancellationToken.None).ConfigureAwait(false);
            lock (CacheLock) { _scanCache = result; _scanCacheAt = DateTime.UtcNow; }
            return result;
        }
        finally { lock (CacheLock) _scanInFlight = null; }
    }

    internal static async Task<IReadOnlyList<RepairDiagnostic>> ScanLocalAsync(CancellationToken token = default)
    {
        var result = new List<RepairDiagnostic>();
        result.Add(CheckServicePair("Windows Update", "wuauserv", "BITS"));
        result.Add(CheckStore());
        result.Add(CheckExplorer());
        result.Add(await CheckNetworkAsync(token));
        result.Add(await CheckBadTweaksAsync(token));
        result.Add(CheckGameBar());
        result.Add(await CheckNvidiaAsync(token));
        return result;
    }

    private static RepairDiagnostic CheckServicePair(string area, string a, string b)
    {
        try
        {
            using var sa = new ServiceController(a); using var sb = new ServiceController(b);
            bool ok = sa.StartType != ServiceStartMode.Disabled && sb.StartType != ServiceStartMode.Disabled;
            string actual = $"{a}: {sa.Status}/{sa.StartType} • {b}: {sb.Status}/{sb.StartType}";
            return new(area, ok ? "Ready" : "Needs attention", ok, actual,
                "Obě služby nesmí být zakázané", actual);
        }
        catch (Exception ex) { return new(area, "Unknown", false, ex.Message); }
    }

    private static RepairDiagnostic CheckStore()
    {
        try
        {
            var pm = new PackageManager();
            bool found = pm.FindPackagesForUser(string.Empty, "Microsoft.WindowsStore_8wekyb3d8bbwe").Any();
            string actual = found ? "Balíček Microsoft Store je přítomný" : "Balíček Microsoft Store chybí";
            return new("Microsoft Store", found ? "Installed" : "Missing", found,
                found ? "Windows Store package je přítomný." : "Windows Store package nebyl nalezen.",
                "Balíček Microsoft Store je nainstalovaný", actual);
        }
        catch (Exception ex) { return new("Microsoft Store", "Unknown", false, ex.Message); }
    }

    private static RepairDiagnostic CheckExplorer()
    {
        Process[] processes = Process.GetProcessesByName("explorer");
        try
        {
            bool running = processes.Length > 0;
            string actual = running ? "explorer.exe běží" : "explorer.exe neběží";
            return new("Explorer", running ? "Running" : "Stopped", running,
                running ? "explorer.exe běží." : "Explorer neběží.",
                "explorer.exe běží", actual);
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    private static async Task<RepairDiagnostic> CheckNetworkAsync(CancellationToken token)
    {
        var r = await NetworkDiagnosticsService.MeasureAsync(token: token);
        bool ok = r.Received > 0 && r.LossPercent < 25;
        string actual = r.Received > 0 ? $"{r.Target} • {r.AvgMs:0.0} ms • ztráta {r.LossPercent:0.0} %" : "Bez odpovědi";
        return new("Network", ok ? "Online" : "Needs attention", ok,
            r.Received > 0 ? $"{r.Target} • {r.AvgMs:0.0} ms • loss {r.LossPercent:0.0} %" : "Diagnostický cíl neodpověděl.",
            "Síť odpovídá a ztrátovost je pod 25 %", actual);
    }

    private static async Task<RepairDiagnostic> CheckBadTweaksAsync(CancellationToken token)
    {
        var issues = new List<string>();

        var binding = await ProcessRunner.RunAsync(
            "powershell.exe",
            "-NoProfile -Command \"Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object { $n=$_.Name; Get-NetAdapterBinding -Name $n | Where-Object { ($_.ComponentID -eq 'ms_tcpip6' -or $_.ComponentID -eq 'ms_pacer') -and -not $_.Enabled } | ForEach-Object { $n + ':' + $_.ComponentID } }\"",
            TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(binding.StdOut))
            issues.AddRange(binding.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => "Binding " + x.Trim()));

        string bcd = await BcdEditStateService.GetCurrentAsync(token).ConfigureAwait(false);
        if (System.Text.RegularExpressions.Regex.IsMatch(bcd, @"(?im)^\s*disabledynamictick\s+Yes\s*$") ||
            System.Text.RegularExpressions.Regex.IsMatch(bcd, @"(?im)^\s*useplatformclock\s+(Yes|True|No|False)\s*$"))
            issues.Add("BCD timer override (Dynamic Tick / HPET)");

        using (var boot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Dfrg\BootOptimizeFunction"))
        {
            if (string.Equals(boot?.GetValue("Enable")?.ToString(), "N", StringComparison.OrdinalIgnoreCase))
                issues.Add("BootOptimizeFunction disabled");
        }

        using (var game = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore"))
        {
            if (Convert.ToInt32(game?.GetValue("GameDVR_FSEBehaviorMode") ?? -1) == 2)
                issues.Add("Fullscreen Optimization compatibility override");
        }

        using (var dwm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Dwm"))
        {
            if (Convert.ToInt32(dwm?.GetValue("OverlayTestMode") ?? 0) == 5 ||
                Convert.ToInt32(dwm?.GetValue("EnableOverlay") ?? 1) == 0)
                issues.Add("MPO disabled override");
        }

        using (var graphics = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers"))
        {
            if (graphics?.GetValue("TdrDelay") != null)
                issues.Add("TdrDelay legacy GPU timeout override");
        }

        foreach (string thumbPath in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Thumbnail Cache",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Thumbnail Cache"
        })
        {
            using var thumb = Registry.LocalMachine.OpenSubKey(thumbPath);
            if (Convert.ToInt32(thumb?.GetValue("Autorun") ?? 3) != 0)
                issues.Add("Thumbnail cache auto-clean enabled");
        }

        try
        {
            using var mmcss = new ServiceController("MMCSS");
            if (mmcss.StartType == ServiceStartMode.Disabled)
                issues.Add("MMCSS service disabled");
        }
        catch { }

        using (var mm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"))
        {
            if (Convert.ToInt32(mm?.GetValue("DisablePagingExecutive") ?? 0) != 0)
                issues.Add("DisablePagingExecutive legacy tweak");
        }

        using (var multimedia = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"))
        {
            int responsiveness = Convert.ToInt32(multimedia?.GetValue("SystemResponsiveness") ?? 20);
            if (responsiveness != 20)
                issues.Add("SystemResponsiveness legacy override=" + responsiveness);
        }

        using (var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
        {
            if (string.Equals(desktop?.GetValue("AutoEndTasks")?.ToString(), "1", StringComparison.Ordinal))
                issues.Add("AutoEndTasks aggressive shutdown override");
            if (int.TryParse(desktop?.GetValue("HungAppTimeout")?.ToString(), out int hung) && hung <= 2000)
                issues.Add("HungAppTimeout aggressive override=" + hung);
            if (int.TryParse(desktop?.GetValue("WaitToKillAppTimeout")?.ToString(), out int waitApp) && waitApp <= 2000)
                issues.Add("WaitToKillAppTimeout aggressive override=" + waitApp);
        }
        using (var control = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control"))
        {
            if (int.TryParse(control?.GetValue("WaitToKillServiceTimeout")?.ToString(), out int waitSvc) && waitSvc < 20000)
                issues.Add("WaitToKillServiceTimeout aggressive override=" + waitSvc);
        }

        try
        {
            string memoryCompression = await WindowsActions.GetMemoryCompressionAsync(token).ConfigureAwait(false);
            if (!memoryCompression.Contains("True", StringComparison.OrdinalIgnoreCase))
                issues.Add("Memory Compression disabled");
        }
        catch { }

        try
        {
            var memory = await MemoryStateService.ReadAsync(token).ConfigureAwait(false);
            using var control = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            int value = Convert.ToInt32(control?.GetValue("SvcHostSplitThresholdInKB") ?? 0);
            int ramKb = checked((int)Math.Round(memory.TotalRamGb * 1024d * 1024d));
            if (value > 0 && ramKb > 0 && Math.Abs((long)value - ramKb) <= Math.Max(65536L, ramKb / 20L))
                issues.Add("SvcHostSplitThresholdInKB RAM-sized legacy override");
        }
        catch { }

        foreach (string serviceName in new[] { "DoSvc", "UsoSvc", "WaaSMedicSvc", "StorSvc", "ClipSVC", "InstallService", "LanmanServer", "PolicyAgent", "IKEEXT", "AppReadiness" })
        {
            try
            {
                using var svc = new ServiceController(serviceName);
                if (svc.StartType == ServiceStartMode.Disabled)
                    issues.Add(serviceName + " service disabled");
            }
            catch { }
        }

        using (var reserve = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager"))
        {
            if (Convert.ToInt32(reserve?.GetValue("ShippedWithReserves") ?? 1) == 0)
                issues.Add("Reserved Storage disabled");
        }

        using (var ux = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings"))
        using (var au = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"))
        {
            string? raw = ux?.GetValue("PauseUpdatesExpiryTime")?.ToString();
            if (DateTime.TryParse(raw, out DateTime expiry) && expiry.ToUniversalTime() > DateTime.UtcNow.AddYears(9))
                issues.Add("Windows Update paused for years");
            if (Convert.ToInt32(au?.GetValue("NoAutoUpdate") ?? 0) == 1)
                issues.Add("Windows Update NoAutoUpdate legacy policy");
        }

        try
        {
            var transition = await NetworkTransitionStateService.GetAsync(token).ConfigureAwait(false);
            string combined = transition.Teredo.StdOut + "\n" + transition.Isatap.StdOut + "\n" +
                              transition.SixToFour.StdOut + "\n" + transition.Privacy.StdOut + "\n" + transition.Global.StdOut;
            if (System.Text.RegularExpressions.Regex.IsMatch(combined, @"(?im)(teredo|isatap|6to4|privacy|temporary|randomize).*?(disabled|zakáz|vypnut)"))
                issues.Add("IPv6 transition/privacy legacy disable");
        }
        catch { }

        foreach (var (hive, path, valueName, label) in new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Mozilla\Firefox", "NetworkPrediction", "Firefox policy NetworkPrediction"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Google\Chrome", "NetworkPredictionOptions", "Chrome policy NetworkPrediction"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "NetworkPredictionOptions", "Edge policy NetworkPrediction"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Policies\BraveSoftware\Brave", "NetworkPredictionOptions", "Brave policy NetworkPrediction"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Vivaldi", "NetworkPredictionOptions", "Vivaldi policy NetworkPrediction")
        })
        {
            using RegistryKey? baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(path);
            if (key?.GetValue(valueName) != null) issues.Add(label);
        }

        try
        {
            var tasks = await new TaskSchedulerService().GetBasicAsync(false, token).ConfigureAwait(false);
            foreach (var task in tasks.Where(IsCriticalMaintenanceTask).Where(x => !x.Enabled).Take(12))
                issues.Add("Disabled maintenance task: " + task.Path);
        }
        catch { }

        foreach (var profile in FirefoxAdvancedService.Profiles())
        {
            string file = Path.Combine(profile, "user.js");
            if (!File.Exists(file)) continue;
            string text = await File.ReadAllTextAsync(file, token).ConfigureAwait(false);
            if (text.Contains("user_pref(\"network.prefetch-next\", false);", StringComparison.Ordinal) ||
                text.Contains("user_pref(\"network.dns.disablePrefetch\", true);", StringComparison.Ordinal) ||
                text.Contains("user_pref(\"network.dns.disablePrefetchFromHTTPS\", true);", StringComparison.Ordinal) ||
                text.Contains("user_pref(\"network.http.speculative-parallel-limit\", 0);", StringComparison.Ordinal) ||
                text.Contains("user_pref(\"browser.urlbar.speculativeConnect.enabled\", false);", StringComparison.Ordinal) ||
                text.Contains("user_pref(\"browser.cache.disk.enable\", false);", StringComparison.Ordinal))
            {
                issues.Add("Firefox legacy performance prefs: " + Path.GetFileName(profile));
            }
        }

        bool healthy = issues.Count == 0;
        string actual = healthy ? "Bez nalezeného driftu" : string.Join(" • ", issues);
        return new("Škodlivé / zastaralé úpravy", healthy ? "Clean" : "Needs repair", healthy,
            healthy ? "Nebyl nalezen tweak, který by zbytečně omezoval výkon nebo síť." : actual,
            "Výchozí bezpečné hodnoty bez agresivních override", actual);
    }

    private static bool IsCriticalMaintenanceTask(TaskInfo task)
    {
        return task.Path.Equals(@"\Microsoft\Windows\Defrag\ScheduledDefrag", StringComparison.OrdinalIgnoreCase) ||
               task.Path.StartsWith(@"\Microsoft\Windows\UpdateAssistant\", StringComparison.OrdinalIgnoreCase) ||
               task.Name.StartsWith("MicrosoftEdgeUpdateTask", StringComparison.OrdinalIgnoreCase);
    }

    private static RepairDiagnostic CheckGameBar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
            object? value = key?.GetValue("AppCaptureEnabled");
            return new("Game Bar / Capture", "Detected", true, value == null ? "Windows default" : "AppCaptureEnabled=" + value);
        }
        catch (Exception ex) { return new("Game Bar / Capture", "Unknown", false, ex.Message); }
    }

    private static async Task<RepairDiagnostic> CheckNvidiaAsync(CancellationToken token)
    {
        try
        {
            var gpu = await GpuDriverService.DetectPrimaryGpuAsync(token);
            if (gpu == null) return new("GPU driver", "Not detected", false, "GPU nebyla detekována.");
            bool healthy = !string.IsNullOrWhiteSpace(gpu.DisplayVersion);
            return new("GPU driver", healthy ? "Installed" : "Needs attention", healthy, gpu.Name + " • " + gpu.DisplayVersion);
        }
        catch (Exception ex) { return new("GPU driver", "Unknown", false, ex.Message); }
    }

    public static async Task RepairBadTweaksAsync(CancellationToken token = default)
    {
        if (!WorkerExecutionContext.IsWorker)
        {
            await WorkerClientService.RunAsync<WorkerAck>("repair-bad-tweaks", timeout: TimeSpan.FromMinutes(2), token: token).ConfigureAwait(false);
            InvalidateCache();
            return;
        }
        await RepairBadTweaksLocalAsync(token).ConfigureAwait(false);
    }

    internal static async Task<WorkerAck> RepairBadTweaksLocalAsync(CancellationToken token = default)
    {
        var binding = await ProcessRunner.RunAsync(
            "powershell.exe",
            "-NoProfile -Command \"Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object { Enable-NetAdapterBinding -Name $_.Name -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction SilentlyContinue; Enable-NetAdapterBinding -Name $_.Name -ComponentID ms_pacer -Confirm:$false -ErrorAction SilentlyContinue }\"",
            TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        if (!binding.Success)
            throw new InvalidOperationException(binding.StdErr + binding.StdOut);

        foreach (string arg in new[] { "/deletevalue disabledynamictick", "/deletevalue useplatformclock" })
        {
            var bcd = await ProcessRunner.RunAsync("bcdedit.exe", arg, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            if (!bcd.Success && bcd.ExitCode == 0)
                throw new InvalidOperationException(bcd.StdErr + bcd.StdOut);
        }
        BcdEditStateService.Invalidate();

        using (var boot = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Dfrg\BootOptimizeFunction", true))
            boot.SetValue("Enable", "Y", RegistryValueKind.String);

        using (var game = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore", true))
            game.DeleteValue("GameDVR_FSEBehaviorMode", false);
        using (var dwm = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\Dwm", true))
        {
            dwm.DeleteValue("OverlayTestMode", false);
            dwm.DeleteValue("EnableOverlay", false);
        }

        using (var graphics = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", true))
            graphics.DeleteValue("TdrDelay", false);

        foreach (string thumbPath in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Thumbnail Cache",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Thumbnail Cache"
        })
        {
            using var thumb = Registry.LocalMachine.CreateSubKey(thumbPath, true);
            thumb.SetValue("Autorun", 0, RegistryValueKind.DWord);
        }

        var mmcssCfg = await ProcessRunner.RunAsync("sc.exe", "config MMCSS start= auto", TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        if (!mmcssCfg.Success) throw new InvalidOperationException(mmcssCfg.StdErr + mmcssCfg.StdOut);
        _ = await ProcessRunner.RunAsync("sc.exe", "start MMCSS", TimeSpan.FromSeconds(20), token).ConfigureAwait(false);

        using (var mm = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", true))
            mm.SetValue("DisablePagingExecutive", 0, RegistryValueKind.DWord);
        using (var multimedia = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true))
            multimedia.SetValue("SystemResponsiveness", 20, RegistryValueKind.DWord);
        using (var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true))
        {
            desktop?.DeleteValue("AutoEndTasks", false);
            desktop?.DeleteValue("HungAppTimeout", false);
            desktop?.DeleteValue("WaitToKillAppTimeout", false);
        }
        using (var control = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control", true))
            control.SetValue("WaitToKillServiceTimeout", "20000", RegistryValueKind.String);
        await WindowsActions.SetMemoryCompressionAsync(true, token).ConfigureAwait(false);

        try
        {
            var memory = await MemoryStateService.ReadAsync(token).ConfigureAwait(false);
            using var control = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control", writable: true);
            int value = Convert.ToInt32(control?.GetValue("SvcHostSplitThresholdInKB") ?? 0);
            int ramKb = checked((int)Math.Round(memory.TotalRamGb * 1024d * 1024d));
            if (value > 0 && ramKb > 0 && Math.Abs((long)value - ramKb) <= Math.Max(65536L, ramKb / 20L))
                control?.DeleteValue("SvcHostSplitThresholdInKB", false);
        }
        catch { }

        foreach (var (serviceName, targetStart) in new[]
        {
            ("DoSvc", "demand"),
            ("UsoSvc", "auto"),
            ("WaaSMedicSvc", "demand"),
            ("StorSvc", "demand"),
            ("ClipSVC", "demand"),
            ("InstallService", "demand"),
            ("PolicyAgent", "demand"),
            ("IKEEXT", "demand"),
            ("AppReadiness", "demand"),
            ("LanmanServer", "auto")
        })
        {
            if (ServiceSafetyService.ReadStartType(serviceName) != 4)
                continue;

            var svc = await ProcessRunner.RunAsync("sc.exe", $"config {serviceName} start= {targetStart}", TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            if (!svc.Success)
                throw new InvalidOperationException(serviceName + ": " + svc.StdErr + svc.StdOut);
        }

        foreach (string args in new[]
        {
            "int teredo set state default",
            "int ipv6 isatap set state default",
            "int ipv6 6to4 set state state=default",
            "int ipv6 set privacy state=enabled",
            "int ipv6 set global randomizeidentifier=enabled"
        })
        {
            var net = await ProcessRunner.RunAsync("netsh.exe", args, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            if (!net.Success) throw new InvalidOperationException(args + ": " + net.StdErr + net.StdOut);
        }
        NetworkTransitionStateService.Invalidate();

        using (var reserve = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager", true))
            reserve.SetValue("ShippedWithReserves", 1, RegistryValueKind.DWord);

        using (var ux = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", writable: true))
        {
            string? raw = ux?.GetValue("PauseUpdatesExpiryTime")?.ToString();
            if (DateTime.TryParse(raw, out DateTime expiry) && expiry.ToUniversalTime() > DateTime.UtcNow.AddYears(9))
            {
                foreach (string name in new[] { "PauseUpdatesStartTime", "PauseUpdatesExpiryTime", "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime" })
                    ux?.DeleteValue(name, false);
            }
        }
        using (var au = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", writable: true))
        {
            if (Convert.ToInt32(au?.GetValue("NoAutoUpdate") ?? 0) == 1)
                au?.DeleteValue("NoAutoUpdate", false);
        }

        foreach (var (path, valueName) in new[]
        {
            (@"SOFTWARE\Policies\Mozilla\Firefox", "NetworkPrediction"),
            (@"SOFTWARE\Policies\Google\Chrome", "NetworkPredictionOptions"),
            (@"SOFTWARE\Policies\Microsoft\Edge", "NetworkPredictionOptions"),
            (@"SOFTWARE\Policies\BraveSoftware\Brave", "NetworkPredictionOptions"),
            (@"SOFTWARE\Policies\Vivaldi", "NetworkPredictionOptions")
        })
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: true);
            key?.DeleteValue(valueName, false);
        }

        var scheduler = new TaskSchedulerService();
        var allTasks = await scheduler.GetBasicAsync(false, token).ConfigureAwait(false);
        var disabledCritical = allTasks.Where(IsCriticalMaintenanceTask).Where(x => !x.Enabled)
            .Select(x => (x.Path, true)).ToArray();
        if (disabledCritical.Length > 0)
        {
            var errors = await scheduler.SetEnabledBatchAsync(disabledCritical, token).ConfigureAwait(false);
            if (errors.Length > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Take(8)));
        }

        await FirefoxAdvancedService.ApplyAsync(webRender: false, diskCacheOff: false, speculativeOff: false, dohMode2: false).ConfigureAwait(false);
        InvalidateCache();
        return new WorkerAck();
    }

    public static async Task FlushNetworkAsync(CancellationToken token = default)
    {
        if (!WorkerExecutionContext.IsWorker) { await WorkerClientService.RunAsync<WorkerAck>("repair-network", timeout: TimeSpan.FromMinutes(2), token: token).ConfigureAwait(false); return; }
        await FlushNetworkLocalAsync(token);
    }

    internal static async Task<WorkerAck> FlushNetworkLocalAsync(CancellationToken token = default)
    {
        await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(20), token);
        await ProcessRunner.RunAsync("netsh.exe", "winsock reset", TimeSpan.FromSeconds(25), token);
        return new WorkerAck();
    }
}
