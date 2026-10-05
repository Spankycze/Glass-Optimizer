using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record WindowsUpdateCenterStatus(bool RebootPending, bool Paused, DateTime? PauseUntil, string Services, int RecentErrorCount, string LastError);
public sealed record AvailableDriverUpdate(string Title, string DriverClass, string Provider, string Version);
public sealed record AppHealthCheck(string Name, bool Ok, string Detail);
public sealed record NetworkAnalyzerStatus(string Adapter, string Dns, bool Ipv6, string Gateway, double GatewayMs, bool Rsc, string Duplex, string Verdict);
public sealed record StorageAdvancedStatus(string Volumes, string Physical);

public static class AdvancedCenterService
{
    private static WindowsUpdateCenterStatus? _windowsUpdateCache;
    private static DateTime _windowsUpdateCacheAt;
    private static readonly object WindowsUpdateGate = new();
    private static Task<WindowsUpdateCenterStatus>? _windowsUpdateInFlight;
    private static int _windowsUpdateGeneration;
    private static IReadOnlyList<AppHealthCheck>? _appHealthCache;
    private static DateTime _appHealthCacheAt;
    private static readonly object WingetHealthGate = new();
    private static AppHealthCheck? _wingetHealthCache;
    private static Task<AppHealthCheck>? _wingetHealthInFlight;
    private static NetworkAnalyzerStatus? _networkCache;
    private static DateTime _networkCacheAt;
    private static readonly object NetworkGate = new();
    private static Task<NetworkAnalyzerStatus>? _networkInFlight;
    private static int _networkGeneration;
    private static StorageAdvancedStatus? _storageCache;
    private static DateTime _storageCacheAt;
    private static readonly object StorageGate = new();
    private static Task<StorageAdvancedStatus>? _storageInFlight;

    public static async Task<WindowsUpdateCenterStatus> GetWindowsUpdateStatusAsync(CancellationToken token = default)
    {
        Task<WindowsUpdateCenterStatus> task;
        lock (WindowsUpdateGate)
        {
            if (_windowsUpdateCache != null && DateTime.UtcNow - _windowsUpdateCacheAt < TimeSpan.FromSeconds(20))
                return _windowsUpdateCache;

            if (_windowsUpdateInFlight == null)
            {
                int generation = _windowsUpdateGeneration;
                var created = LoadWindowsUpdateStatusCoreAsync(generation);
                _windowsUpdateInFlight = created;
                task = created;
                _ = created.ContinueWith(t =>
                {
                    if (t.IsFaulted) _ = t.Exception;
                    lock (WindowsUpdateGate)
                        if (ReferenceEquals(_windowsUpdateInFlight, created))
                            _windowsUpdateInFlight = null;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else
            {
                task = _windowsUpdateInFlight;
            }
        }

        return await task.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task<WindowsUpdateCenterStatus> LoadWindowsUpdateStatusCoreAsync(int generation)
    {
        var result = await Task.Run(NativeSystemProbeService.ReadWindowsUpdateStatus).ConfigureAwait(false);
        lock (WindowsUpdateGate)
        {
            if (generation == _windowsUpdateGeneration)
            {
                _windowsUpdateCache = result;
                _windowsUpdateCacheAt = DateTime.UtcNow;
            }
        }
        return result;
    }

    public static async Task SetWindowsUpdatePauseAsync(bool pause, CancellationToken token = default)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", writable: true);
        if (key == null) throw new InvalidOperationException("Windows Update settings registry není dostupný.");
        if (pause)
        {
            var now = DateTime.UtcNow; var end = now.AddDays(7);
            key.SetValue("PauseUpdatesStartTime", now.ToString("o")); key.SetValue("PauseUpdatesExpiryTime", end.ToString("o"));
            key.SetValue("PauseFeatureUpdatesStartTime", now.ToString("o")); key.SetValue("PauseFeatureUpdatesEndTime", end.ToString("o"));
            key.SetValue("PauseQualityUpdatesStartTime", now.ToString("o")); key.SetValue("PauseQualityUpdatesEndTime", end.ToString("o"));
        }
        else
        {
            foreach (var n in new[] { "PauseUpdatesStartTime", "PauseUpdatesExpiryTime", "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime" })
                key.DeleteValue(n, throwOnMissingValue: false);
            await ProcessRunner.RunAsync("UsoClient.exe", "StartScan", TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }
        lock (WindowsUpdateGate)
        {
            _windowsUpdateGeneration++;
            _windowsUpdateCache = null;
            _windowsUpdateCacheAt = default;
            _windowsUpdateInFlight = null;
        }
        _appHealthCache = null; _appHealthCacheAt = default;
    }

    public static Task<IReadOnlyList<AvailableDriverUpdate>> GetAvailableDriverUpdatesAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.Run<IReadOnlyList<AvailableDriverUpdate>>(() => NativeSystemProbeService.ReadAvailableDriverUpdates(), token);
    }

    public static async Task RepairWindowsUpdateAsync(CancellationToken token = default)
    {
        var dism = await ProcessRunner.RunAsync("dism.exe", "/Online /Cleanup-Image /RestoreHealth", TimeSpan.FromMinutes(25), token).ConfigureAwait(false);
        if (dism.ExitCode != 0) throw new InvalidOperationException("DISM RestoreHealth selhal: " + dism.StdErr);
        var sfc = await ProcessRunner.RunAsync("sfc.exe", "/scannow", TimeSpan.FromMinutes(25), token).ConfigureAwait(false);
        if (sfc.ExitCode != 0 && sfc.ExitCode != 1) throw new InvalidOperationException("SFC selhal: " + sfc.StdErr);
    }

    public static async Task<IReadOnlyList<AppHealthCheck>> GetAppHealthAsync(CancellationToken token = default)
    {
        if (_appHealthCache != null && DateTime.UtcNow - _appHealthCacheAt < TimeSpan.FromSeconds(30)) return _appHealthCache;
        var list = new List<AppHealthCheck>();
        string exe = Path.Combine(AppContext.BaseDirectory, "SpankyhoOptimizer.exe");
        list.Add(new("Optimizer executable", File.Exists(exe), File.Exists(exe) ? exe : "Soubor chybí"));
        string resources = Path.Combine(AppContext.BaseDirectory, "SpankyhoOptimizer.pri");
        string legacyResources = Path.Combine(AppContext.BaseDirectory, "resources.pri");
        bool resourcesOk = File.Exists(resources) || File.Exists(legacyResources);
        list.Add(new("WinUI resources", resourcesOk, resourcesOk ? (File.Exists(resources) ? "SpankyhoOptimizer.pri OK" : "resources.pri OK") : "WinUI PRI resource chybí"));
        list.Add(new(".NET runtime", Environment.Version.Major >= 10, Environment.Version.ToString()));
        list.Add(await GetWingetHealthAsync(token).ConfigureAwait(false));
        try
        {
            Directory.CreateDirectory(LogService.Root);
            string probe = Path.Combine(LogService.Root, ".health-probe");
            await File.WriteAllTextAsync(probe, "ok", token).ConfigureAwait(false);
            File.Delete(probe);
            list.Add(new("ProgramData write", true, LogService.Root));
        }
        catch (Exception ex) { list.Add(new("ProgramData write", false, ex.Message)); }
        var wu = await GetWindowsUpdateStatusAsync(token).ConfigureAwait(false);
        list.Add(new("Windows Update services", wu.Services.Contains("wuauserv=Running", StringComparison.OrdinalIgnoreCase), wu.Services));
        _appHealthCache = list;
        _appHealthCacheAt = DateTime.UtcNow;
        return _appHealthCache;
    }

    public static Task<string> GetFixedStorageSummaryAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var volumes = DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed)
            .Select(x => $"{x.Name.TrimEnd('\\')}: {x.AvailableFreeSpace / 1073741824d:0.0} / {x.TotalSize / 1073741824d:0.0} GB free/total");
        var disks = NativeSystemProbeService.ReadStorage()
            .Select(x => $"{x.Name} | FW {x.Firmware} | {(x.TemperatureC.HasValue ? x.TemperatureC.Value.ToString("0") + " °C" : "teplota N/A")}");
        return Task.FromResult(string.Join(" || ", new[] { string.Join(" • ", volumes), string.Join(" • ", disks) }));
    }

    public static void InvalidateNetworkAnalyzer()
    {
        lock (NetworkGate)
        {
            _networkGeneration++;
            _networkCache = null;
            _networkCacheAt = default;
            _networkInFlight = null;
        }
    }

    public static async Task<NetworkAnalyzerStatus> GetNetworkAnalyzerAsync(CancellationToken token = default)
    {
        Task<NetworkAnalyzerStatus> task;
        lock (NetworkGate)
        {
            if (_networkCache != null && DateTime.UtcNow - _networkCacheAt < TimeSpan.FromMinutes(5)) return _networkCache;
            if (_networkInFlight == null)
            {
                int generation = _networkGeneration;
                var created = LoadNetworkAnalyzerCoreAsync(generation);
                _networkInFlight = created;
                task = created;
                _ = created.ContinueWith(t =>
                {
                    if (t.IsFaulted) _ = t.Exception;
                    lock (NetworkGate)
                        if (ReferenceEquals(_networkInFlight, created))
                            _networkInFlight = null;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else
            {
                task = _networkInFlight;
            }
        }
        return await task.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task<NetworkAnalyzerStatus> LoadNetworkAnalyzerCoreAsync(int generation)
    {
        var x = await NativeSystemProbeService.GetNetworkAsync(CancellationToken.None).ConfigureAwait(false);
        var result = x == null
            ? new NetworkAnalyzerStatus("Nenalezen", "", false, "", 0, false, "", "Aktivní adaptér nebyl nalezen.")
            : new NetworkAnalyzerStatus(x.Description, x.Dns, x.Ipv6, x.Gateway, x.GatewayMs, x.RscEnabled, x.Duplex, x.GatewayMs > 0 && x.GatewayMs < 5 ? "Síť vypadá v pořádku." : x.GatewayMs >= 20 ? "Vysoká latence už k lokální gateway — zkontroluj Wi-Fi/kabel/router." : "Bez zjevného problému v základním read-backu.");
        lock (NetworkGate)
        {
            if (generation == _networkGeneration)
            {
                _networkCache = result;
                _networkCacheAt = DateTime.UtcNow;
            }
        }
        return result;
    }

    public static async Task<StorageAdvancedStatus> GetStorageAdvancedAsync(CancellationToken token = default)
    {
        if (_storageCache != null && DateTime.UtcNow - _storageCacheAt < TimeSpan.FromSeconds(30)) return _storageCache;
        Task<StorageAdvancedStatus> task;
        lock (StorageGate)
        {
            if (_storageCache != null && DateTime.UtcNow - _storageCacheAt < TimeSpan.FromSeconds(30)) return _storageCache;
            if (_storageInFlight == null)
            {
                var created = LoadStorageAdvancedCoreAsync();
                _storageInFlight = created;
                task = created;
                _ = created.ContinueWith(t =>
                {
                    if (t.IsFaulted) _ = t.Exception;
                    lock (StorageGate)
                        if (ReferenceEquals(_storageInFlight, created))
                            _storageInFlight = null;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else
            {
                task = _storageInFlight;
            }
        }
        return await task.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task<StorageAdvancedStatus> LoadStorageAdvancedCoreAsync()
    {
        string volumes = string.Join(" • ", DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed)
            .Select(x => $"{x.Name.TrimEnd('\\')}: {x.AvailableFreeSpace / 1073741824d:0.0} / {x.TotalSize / 1073741824d:0.0} GB free/total"));
        var disks = await SystemInsightService.GetStorageAsync(CancellationToken.None).ConfigureAwait(false);
        string physical = string.Join(" • ", disks.Select(x =>
            $"{x.Name} | FW {x.Firmware} | {(x.TemperatureC.HasValue ? x.TemperatureC.Value.ToString("0") + " °C" : "teplota N/A")}"));
        var result = new StorageAdvancedStatus(string.IsNullOrWhiteSpace(volumes) ? "Nejsou data" : volumes, string.IsNullOrWhiteSpace(physical) ? "Nejsou data" : physical);
        lock (StorageGate)
        {
            _storageCache = result;
            _storageCacheAt = DateTime.UtcNow;
        }
        return result;
    }

    private static async Task<AppHealthCheck> GetWingetHealthAsync(CancellationToken token)
    {
        Task<AppHealthCheck> task;
        lock (WingetHealthGate)
        {
            if (_wingetHealthCache != null) return _wingetHealthCache;
            if (_wingetHealthInFlight == null)
            {
                var created = ProbeWingetHealthAsync();
                _wingetHealthInFlight = created;
                task = created;
                _ = ObserveWingetHealthAsync(created);
            }
            else
            {
                task = _wingetHealthInFlight;
            }
        }
        return await task.WaitAsync(token).ConfigureAwait(false);
    }

    private static async Task ObserveWingetHealthAsync(Task<AppHealthCheck> created)
    {
        try
        {
            AppHealthCheck result = await created.ConfigureAwait(false);
            lock (WingetHealthGate)
            {
                _wingetHealthCache = result;
                if (ReferenceEquals(_wingetHealthInFlight, created))
                    _wingetHealthInFlight = null;
            }
        }
        catch
        {
            lock (WingetHealthGate)
                if (ReferenceEquals(_wingetHealthInFlight, created))
                    _wingetHealthInFlight = null;
        }
    }
    private static async Task<AppHealthCheck> ProbeWingetHealthAsync()
    {
        try
        {
            string alias = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");

            if (File.Exists(alias))
            {
                string? version = null;
                try
                {
                    var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(alias);
                    version = info.ProductVersion ?? info.FileVersion;
                }
                catch { }

                string detail = string.IsNullOrWhiteSpace(version)
                    ? "Windows App Execution Alias je dostupný."
                    : $"Windows App Execution Alias • {version}";
                return new AppHealthCheck("Winget", true, detail);
            }

            var w = await ProcessRunner.RunAsync(
                "winget.exe", "--version", TimeSpan.FromSeconds(4), CancellationToken.None).ConfigureAwait(false);
            string detailText = string.IsNullOrWhiteSpace(w.StdOut) ? w.StdErr : w.StdOut;
            return new AppHealthCheck("Winget", w.ExitCode == 0, detailText.Trim());
        }
        catch (Exception ex) { return new AppHealthCheck("Winget", false, ex.Message); }
    }

}

