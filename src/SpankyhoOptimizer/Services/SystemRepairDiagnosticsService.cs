using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace SpankyhoOptimizer.Services;

[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record RepairDiagnostic(string Area, string State, bool Healthy, string Detail, string? Expected = null, string? Actual = null)
{
    public RepairDiagnostic(string area, string state, bool healthy, string detail)
        : this(area, state, healthy, detail, null, null) { }

    public void Deconstruct(out string area, out string state, out bool healthy, out string detail)
    {
        area = Area;
        state = State;
        healthy = Healthy;
        detail = Detail;
    }
}

public static class SystemRepairDiagnosticsService
{
    private static readonly object CacheLock = new();
    private static IReadOnlyList<RepairDiagnostic>? _scanCache;
    private static DateTime _scanCacheAt;
    private static Task<IReadOnlyList<RepairDiagnostic>>? _scanInFlight;
    private static int _scanGeneration;

    public static async Task<IReadOnlyList<RepairDiagnostic>> ScanAsync(CancellationToken token = default)
    {
        if (!WorkerExecutionContext.IsWorker) return await WorkerClientService.RunAsync<RepairDiagnostic[]>("repair-scan", timeout: TimeSpan.FromMinutes(2), token: token).ConfigureAwait(false);
        return await ScanLocalAsync(token);
    }

    public static async Task<IReadOnlyList<RepairDiagnostic>> ScanCachedAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task<IReadOnlyList<RepairDiagnostic>> task;
        lock (CacheLock)
        {
            if (_scanCache != null && DateTime.UtcNow - _scanCacheAt < TimeSpan.FromSeconds(20))
                return _scanCache;
            int generation = _scanGeneration;
            // Starting worker IPC may have a synchronous pre-await prelude.
            // Keep it off the caller's WinUI thread and outside CacheLock;
            // the shared Task still guarantees one in-flight scan per generation.
            _scanInFlight ??= Task.Run(() => LoadCachedCoreAsync(generation));
            task = _scanInFlight;
        }
        return await task.WaitAsync(token).ConfigureAwait(false);
    }

    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _scanGeneration++;
            _scanCache = null;
            _scanCacheAt = default;
            _scanInFlight = null;
        }
    }

    public static async Task<RepairDiagnostic?> VerifyAreaAsync(string area, CancellationToken token = default)
    {
        InvalidateCache();
        var fresh = await ScanAsync(token).ConfigureAwait(false);
        return fresh.FirstOrDefault(x => string.Equals(x.Area, area, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyList<RepairDiagnostic>> LoadCachedCoreAsync(
        int generation, Func<Task<IReadOnlyList<RepairDiagnostic>>>? readOnlyTestLoader = null)
    {
        try
        {
            var result = await (readOnlyTestLoader?.Invoke() ??
                ScanAsync(CancellationToken.None)).ConfigureAwait(false);
            lock (CacheLock)
            {
                if (generation == _scanGeneration)
                {
                    _scanCache = result;
                    _scanCacheAt = DateTime.UtcNow;
                }
            }
            return result;
        }
        finally
        {
            // A failed scan must not poison the next request; an invalidated
            // old scan must not clear the newer generation's in-flight work.
            lock (CacheLock)
                if (generation == _scanGeneration) _scanInFlight = null;
        }
    }

    internal static async Task RunParallelScanCacheSmokeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Worker IPC must deserialize the six-field record even though it also
        // offers a four-argument compatibility constructor.
        var jsonProbe = new RepairDiagnostic("IPC", "Ready", true, "json", "expected", "actual");
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<RepairDiagnostic>(
            System.Text.Json.JsonSerializer.Serialize(jsonProbe));
        if (roundTrip != jsonProbe)
            throw new InvalidOperationException("RepairDiagnostic worker JSON roundtrip failed.");
        InvalidateCache();

        // Simulate an obsolete delayed worker result and a newer scan without
        // reading or changing actual system configuration.
        var obsoleteSignal = new TaskCompletionSource<IReadOnlyList<RepairDiagnostic>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var currentSignal = new TaskCompletionSource<IReadOnlyList<RepairDiagnostic>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<RepairDiagnostic>> obsolete;
        Task<IReadOnlyList<RepairDiagnostic>> current;
        lock (CacheLock)
        {
            obsolete = LoadCachedCoreAsync(_scanGeneration, () => obsoleteSignal.Task);
            _scanInFlight = obsolete;
        }
        InvalidateCache();
        lock (CacheLock)
        {
            current = LoadCachedCoreAsync(_scanGeneration, () => currentSignal.Task);
            _scanInFlight = current;
        }
        var oldValue = new RepairDiagnostic("Old", "Obsolete", false, "");
        var newValue = new RepairDiagnostic("New", "Ready", true, "");
        obsoleteSignal.SetResult([oldValue]);
        _ = await obsolete.ConfigureAwait(false);
        lock (CacheLock)
        {
            if (_scanCache != null || !ReferenceEquals(_scanInFlight, current))
                throw new InvalidOperationException("Obsolete repair scan overwrote current generation.");
        }
        currentSignal.SetResult([newValue]);
        _ = await current.ConfigureAwait(false);
        lock (CacheLock)
        {
            if (_scanCache == null || _scanCache[0].Area != "New" || _scanInFlight != null)
                throw new InvalidOperationException("Latest repair scan did not persist correctly.");
        }

        // Failed work must clear its in-flight slot so a new scan can retry.
        InvalidateCache();
        var failureSignal = new TaskCompletionSource<IReadOnlyList<RepairDiagnostic>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<RepairDiagnostic>> failed;
        lock (CacheLock)
        {
            failed = LoadCachedCoreAsync(_scanGeneration, () => failureSignal.Task);
            _scanInFlight = failed;
        }
        failureSignal.SetException(new InvalidOperationException("synthetic failure"));
        try { _ = await failed.ConfigureAwait(false); }
        catch (InvalidOperationException ex) when (ex.Message == "synthetic failure") { }
        lock (CacheLock)
        {
            if (_scanInFlight != null || _scanCache != null)
                throw new InvalidOperationException("Failed scan left poisoned cache.");
        }
        InvalidateCache();

        // Real read-only parallel path, with stable seven-area display order.
        var sw = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var items = await ScanLocalAsync(token).ConfigureAwait(false);
        string[] expected = ["Windows Update", "Microsoft Store", "Explorer", "Network",
            "Škodlivé / zastaralé úpravy", "Game Bar / Capture", "GPU driver"];
        if (items.Count != expected.Length ||
            items.Where((x, i) => !string.Equals(x.Area, expected[i],
                StringComparison.OrdinalIgnoreCase)).Any())
            throw new InvalidOperationException("Repair scan area order/content changed.");
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        long allocatedKb = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore) / 1024;
        LogService.Info("performance", "REPAIR_SCAN_PARALLEL_SMOKE_OK areas=" +
            items.Count + " wallMs=" + sw.ElapsedMilliseconds +
            $" cpuMs={cpuMs:F2} allocatedKB={allocatedKb} " +
            $"wsMB={process.WorkingSet64 / 1048576d:F2} privateMB={process.PrivateMemorySize64 / 1048576d:F2}" +
            " generationGuard=true failedRetry=true writes=false");

        // Additional read-only cache semantics regression; run after the
        // timed scan so network cache tests do not skew its measured wall/CPU.
        await NetworkDiagnosticsService.VerifyCacheReadOnlyAsync(token).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<RepairDiagnostic>> ScanLocalAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        // Windows PackageManager, SCM and explorer enumeration were synchronous
        // before the first await, delaying every independent diagnostic probe.
        // Run those three native providers off the caller thread and overlap them
        // with network, BCD, Task Scheduler and GPU reads. Keep display order.
        Task<RepairDiagnostic> service = Task.Run(
            () => CheckServicePair("Windows Update", "wuauserv", "BITS"), token);
        Task<RepairDiagnostic> store = Task.Run(CheckStore, token);
        Task<RepairDiagnostic> explorer = Task.Run(CheckExplorer, token);
        // Both async methods perform synchronous provider setup before their
        // first await (ping initialization, BCD/WMI/registry probes). Schedule
        // them off the caller thread to bound first-dispatch latency.
        Task<RepairDiagnostic> network = Task.Run(() => CheckNetworkAsync(token), token);
        Task<RepairDiagnostic> drift = Task.Run(() => CheckBadTweaksAsync(token), token);
        var gameBar = CheckGameBar();
        Task<RepairDiagnostic> graphics = CheckNvidiaAsync(token);
        double dispatchMs = sw.Elapsed.TotalMilliseconds;
        RepairDiagnostic[] completed = await Task.WhenAll(
            service, store, explorer, network, drift, graphics).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // Preserve the display order while using the awaited results directly.
        // Consume the values returned by the completed aggregate, without synchronous accessors.
        var result = new[]
        {
            completed[0], completed[1], completed[2], completed[3],
            completed[4], gameBar, completed[5]
        };
        LogService.Info("performance", "REPAIR_SCAN_PARALLEL ms=" +
            sw.ElapsedMilliseconds + $" dispatchMs={dispatchMs:F2} areas=" + result.Length);
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
        // Health status still uses all eight samples and the same 25% loss
        // threshold; only its pacing is shorter than the 80ms jitter/latency
        // benchmark. Preserve distinct cache keys for different pacing.
        var sw = Stopwatch.StartNew();
        var r = await NetworkDiagnosticsService.MeasureAsync(
            samples: 8, token: token, intervalMs: 30).ConfigureAwait(false);
        LogService.Info("performance",
            $"REPAIR_NETWORK_PROBE ms={sw.ElapsedMilliseconds} samples={r.Sent} intervalMs=30");
        bool ok = r.Received > 0 && r.LossPercent < 25;
        string actual = r.Received > 0 ? $"{r.Target} • {r.AvgMs:0.0} ms • ztráta {r.LossPercent:0.0} %" : "Bez odpovědi";
        return new("Network", ok ? "Online" : "Needs attention", ok,
            r.Received > 0 ? $"{r.Target} • {r.AvgMs:0.0} ms • loss {r.LossPercent:0.0} %" : "Diagnostický cíl neodpověděl.",
            "Síť odpovídá a ztrátovost je pod 25 %", actual);
    }

    internal static IReadOnlyList<string> GetDisabledActiveNetworkBindings(CancellationToken token = default) =>
        NativeNetworkBindingRepairService.ReadDisabled(token);

    private static async Task<RepairDiagnostic> CheckBadTweaksAsync(CancellationToken token)
    {
        var issues = new List<string>();
        bool transitionUnknown = false;

        // Start independent read-only providers once, while evaluating cheap
        // registry flags. They overlap WMI, BCD, netsh and TaskScheduler COM
        // latency rather than serially waiting between queries.
        var bindingProbe = Task.Run(() => GetDisabledActiveNetworkBindings(token), token);
        var bcdProbe = BcdEditStateService.GetCurrentAsync(token);
        var transitionProbe = NetworkTransitionStateService.GetAsync(token);
        var maintenanceProbe = new TaskSchedulerService().GetBasicAsync(false, token);
        // Overlap independent memory-state probes with the registry/service inventory.
        // Both tasks are observed below even when a provider is unavailable.
        var compressionProbe = WindowsActions.GetMemoryCompressionAsync(token);
        var memoryProbe = MemoryStateService.ReadAsync(token);

        // Read Microsoft_NetAdapterBindingSettingData through Windows WMI directly.
        // Avoid launching powershell.exe for an ordinary diagnostic probe.
        try
        {
            issues.AddRange(await bindingProbe.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // An unavailable WMI provider must not abort unrelated repair diagnostics
            // or be reported as an incorrectly healthy network configuration.
            LogService.Info("repair", "Native network binding query unavailable: " + ex.Message);
            issues.Add("Network binding status could not be verified");
        }

        string bcd = await bcdProbe.ConfigureAwait(false);
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
            if (int.TryParse(control?.GetValue("WaitToKillServiceTimeout")?.ToString(), out int waitSvc) &&
                waitSvc != WindowsDefaultGuardService.ServiceShutdownDefaultMs)
                issues.Add("WaitToKillServiceTimeout non-default override=" + waitSvc);
            if (control?.GetValue("SvcHostSplitThresholdInKB") is int split &&
                unchecked((uint)split) == uint.MaxValue)
                issues.Add("SvcHostSplitThresholdInKB forces shared service hosts (0xFFFFFFFF)");
        }

        try
        {
            string memoryCompression = await compressionProbe.ConfigureAwait(false);
            if (!memoryCompression.Contains("True", StringComparison.OrdinalIgnoreCase))
                issues.Add("Memory Compression disabled");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { }

        try
        {
            var memory = await memoryProbe.ConfigureAwait(false);
            using var control = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            int value = Convert.ToInt32(control?.GetValue("SvcHostSplitThresholdInKB") ?? 0);
            int ramKb = checked((int)Math.Round(memory.TotalRamGb * 1024d * 1024d));
            if (value > 0 && ramKb > 0 && Math.Abs((long)value - ramKb) <= Math.Max(65536L, ramKb / 20L))
                issues.Add("SvcHostSplitThresholdInKB RAM-sized legacy override");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
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
            var transition = await transitionProbe.ConfigureAwait(false);
            // Preserve native labels rather than splitting localized netsh
            // output; a failed query is unknown, never silently treated as clean.
            string combined = transition.Combined.StdOut;
            if (!transition.Combined.Success || string.IsNullOrWhiteSpace(combined))
                transitionUnknown = true;
            else if (System.Text.RegularExpressions.Regex.IsMatch(combined,
                         @"(?im)(teredo|isatap|6to4|privacy|temporary|randomize).*?(disabled|zakáz|vypnut)"))
                issues.Add("IPv6 transition/privacy legacy disable");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            transitionUnknown = true;
            LogService.Info("repair", "Read-only IPv6 transition query unavailable: " + ex.Message);
        }

        try
        {
            var tasks = await maintenanceProbe.ConfigureAwait(false);
            foreach (var task in tasks.Where(x => IsCriticalMaintenanceTask(x) && !x.Enabled).Take(12))
                issues.Add("Disabled maintenance task: " + task.Path);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { }

        bool healthy = issues.Count == 0 && !transitionUnknown;
        string actual = issues.Count > 0 ? string.Join(" • ", issues) :
            transitionUnknown ? "Stav IPv6 transition/privacy se nepodařilo ověřit" :
            "Bez nalezeného driftu";
        string status = healthy ? "Clean" : issues.Count > 0 ? "Needs repair" : "Unknown";
        return new("Škodlivé / zastaralé úpravy", status, healthy,
            healthy ? "Nebyl nalezen tweak, který by zbytečně omezoval výkon nebo síť." : actual,
            "Výchozí bezpečné hodnoty bez agresivních override", actual);
    }

    private static bool IsCriticalMaintenanceTask(TaskInfo task)
    {
        return task.Path.Equals(@"\Microsoft\Windows\Defrag\ScheduledDefrag", StringComparison.OrdinalIgnoreCase) ||
               task.Path.StartsWith(@"\Microsoft\Windows\UpdateAssistant\", StringComparison.OrdinalIgnoreCase);
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

    // The Windows Service Control Manager already has a trusted native wrapper.
    // Read back every change instead of relying on localized sc.exe output.
    private static void SetServiceStartModeNative(string name, uint startMode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (startMode is not (2u or 3u))
            throw new ArgumentOutOfRangeException(nameof(startMode), "Repair permits only Automatic or Manual.");
        if (!NativeCore.ChangeServiceStartType(name, startMode, out int error))
            throw new System.ComponentModel.Win32Exception(error, "Cannot restore service " + name);
        int? actual = ServiceSafetyService.ReadStartType(name);
        if (actual != startMode)
            throw new InvalidOperationException("Service start-mode read-back mismatch: " + name +
                " expected=" + startMode + " actual=" + (actual?.ToString() ?? "missing"));
    }

    internal static void VerifyNativeServiceRepairReadOnly()
    {
        // No write operations: validate the same registry/status providers as the
        // real repair action, on the existing MMCSS Windows service.
        using var controller = new ServiceController("MMCSS");
        int? registryMode = ServiceSafetyService.ReadStartType("MMCSS");
        if (registryMode == null || registryMode != (int)controller.StartType)
            throw new InvalidOperationException("Native service state provider mismatch for MMCSS.");
    }

    internal static async Task<WorkerAck> RepairBadTweaksLocalAsync(CancellationToken token = default)
    {
        // Explicit repair only: WMI's native binding Enable method replaces
        // the external PowerShell pipeline. Verify changes and roll back any
        // partially changed binding if the provider reports an error.
        await Task.Run(() => NativeNetworkBindingRepairService.EnableDisabled(token), token)
            .ConfigureAwait(false);

        foreach (string arg in new[] { "/deletevalue disabledynamictick", "/deletevalue useplatformclock" })
        {
            var bcd = await ProcessRunner.RunAsync("bcdedit.exe", arg, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            var current = await ProcessRunner.RunAsync("bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            if (!current.Success) throw new InvalidOperationException(current.StdErr + current.StdOut);
            string name = arg["/deletevalue ".Length..];
            if (System.Text.RegularExpressions.Regex.IsMatch(current.StdOut,
                @"(?im)^\s*" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s+"))
                throw new InvalidOperationException("BCD read-back nepotvrdil odstranění " + name + ": " + bcd.StdErr + bcd.StdOut);
        }
        BcdEditStateService.Invalidate();

        using (var boot = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Dfrg\BootOptimizeFunction", true))
            boot.SetValue("Enable", "Y", RegistryValueKind.String);

        // Fullscreen compatibility override is managed by the user's
        // approved Windows Default Guard group, with rollback journaling.
        using (var dwm = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\Dwm", true))
        {
            dwm.DeleteValue("OverlayTestMode", false);
            dwm.DeleteValue("EnableOverlay", false);
        }

        // TdrDelay is repaired by Windows Default Guard only when approved,
        // using its precise type check, write-ahead journal and readback.

        foreach (string thumbPath in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Thumbnail Cache",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Thumbnail Cache"
        })
        {
            using var thumb = Registry.LocalMachine.CreateSubKey(thumbPath, true);
            thumb.SetValue("Autorun", 0, RegistryValueKind.DWord);
        }

        // Native Service Control Manager avoids launching sc.exe for an idempotent repair.
        SetServiceStartModeNative("MMCSS", 2u, token);
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var mmcss = new ServiceController("MMCSS");
            mmcss.Refresh();
            if (mmcss.Status == ServiceControllerStatus.Stopped) mmcss.Start();
            if (mmcss.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StartPending)
                mmcss.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            mmcss.Refresh();
            if (mmcss.Status != ServiceControllerStatus.Running)
                throw new InvalidOperationException("MMCSS se nepodařilo spustit: " + mmcss.Status);
        }, token).ConfigureAwait(false);




        // One approved source of truth for shared Windows-default registry
        // values. The guard journals BEFORE changes, verifies each readback
        // and respects disabled groups. Fixer does not bypass user consent.
        WindowsDefaultGuardService.RepairApprovedOnce(token);
        await WindowsActions.SetMemoryCompressionAsync(true, token).ConfigureAwait(false);

        try
        {
            var memory = await MemoryStateService.ReadAsync(token).ConfigureAwait(false);
            using var control = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control", writable: false);
            int value = Convert.ToInt32(control?.GetValue("SvcHostSplitThresholdInKB") ?? 0);
            int ramKb = checked((int)Math.Round(memory.TotalRamGb * 1024d * 1024d));
            if (WindowsDefaultGuardService.IsLegacyRamSizedSvcHostOverride(value, ramKb))
                WindowsDefaultGuardService.RestoreLegacyRamSizedSvcHostOverride(value, token);
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

            SetServiceStartModeNative(serviceName, targetStart == "auto" ? 2u : 3u, token);
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

        var scheduler = new TaskSchedulerService();
        var allTasks = await scheduler.GetBasicAsync(false, token).ConfigureAwait(false);
        var disabledCritical = allTasks.Where(IsCriticalMaintenanceTask).Where(x => !x.Enabled)
            .Select(x => (x.Path, true)).ToArray();
        if (disabledCritical.Length > 0)
        {
            var errors = await scheduler.SetEnabledBatchAsync(disabledCritical, token).ConfigureAwait(false);
            if (errors.Length > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Take(8)));
        }

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
        var dns = await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(20), token);
        if (!dns.Success) throw new InvalidOperationException(dns.StdErr + dns.StdOut);
        var winsock = await ProcessRunner.RunAsync("netsh.exe", "winsock reset", TimeSpan.FromSeconds(25), token);
        if (!winsock.Success) throw new InvalidOperationException(winsock.StdErr + winsock.StdOut);
        return new WorkerAck();
    }
}
