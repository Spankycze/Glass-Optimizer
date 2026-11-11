using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record GpuDriverInfo(string Name, string Vendor, string RawVersion, string DisplayVersion, DateTime? DriverDate);
public sealed record DriverReleaseInfo(string Source, string Version, string DownloadUrl, string DetailsUrl, bool Community, string Notes);
public sealed record DriverDownloadProgress(long Received, long? Total)
{
    public int Percent => Total.GetValueOrDefault() > 0 ? (int)Math.Clamp(Received * 100 / Total!.Value, 0, 100) : -1;
}

public static class GpuDriverService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private static readonly HashSet<string> KnownGpuInstallerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "setup64", "NVIDIA Installer", "NVIDIA Web Helper", "NVCleanstall"
    };

    internal static bool IsKnownGpuInstallerName(string? processName) =>
        processName is not null && KnownGpuInstallerNames.Contains(processName);
    private static readonly object DetectionCacheLock = new();
    private static readonly object CimScopeLock = new();
    private static readonly SemaphoreSlim DetectionGate = new(1, 1);
    private static readonly object InstallerLaunchLock = new();
    private static DateTime _lastInstallerLaunchUtc;
    private static string _lastInstallerPath = string.Empty;
    private static Task<GpuDriverInfo?>? _persistentWarmFlight;
    // Only a genuinely running disk read may be shared. A completed Task
    // must never become a second cache with an unbounded lifetime.
    private static Task<GpuDriverInfo?> ReuseOrStartPersistentWarmFlight(
        Task<GpuDriverInfo?>? flight, Func<Task<GpuDriverInfo?>> start) =>
        flight is { IsCompleted: false } ? flight : start();

    internal static string RunGpuPersistentWarmFlightPolicySmoke()
    {
        int starts = 0;
        var pending = new TaskCompletionSource<GpuDriverInfo?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<GpuDriverInfo?> Start()
        {
            starts++;
            return pending.Task;
        }
        var shared = ReuseOrStartPersistentWarmFlight(null, Start);
        if (!ReferenceEquals(shared, pending.Task) || starts != 1 ||
            !ReferenceEquals(ReuseOrStartPersistentWarmFlight(shared, Start), shared) ||
            starts != 1)
            throw new InvalidOperationException("Concurrent GPU persisted reads were not deduplicated.");

        pending.SetResult(null); // Missing cache must not stay cached forever.
        Task<GpuDriverInfo?> Success() => Task.FromResult<GpuDriverInfo?>(
            new GpuDriverInfo("Test GPU", "Test", "1", "1", null));
        var recovered = ReuseOrStartPersistentWarmFlight(shared, Success);
        if (ReferenceEquals(recovered, shared) || recovered.Result?.Name != "Test GPU")
            throw new InvalidOperationException("Completed missing GPU flight was incorrectly reused.");
        var newRead = ReuseOrStartPersistentWarmFlight(recovered, Start);
        if (!ReferenceEquals(newRead, pending.Task) || starts != 2)
            throw new InvalidOperationException("Completed successful GPU flight was incorrectly reused.");
        var faulted = Task.FromException<GpuDriverInfo?>(new IOException("test only"));
        if (ReferenceEquals(ReuseOrStartPersistentWarmFlight(faulted, Success), faulted))
            throw new InvalidOperationException("Faulted GPU flight was incorrectly reused.");
        var canceled = Task.FromCanceled<GpuDriverInfo?>(new CancellationToken(true));
        if (ReferenceEquals(ReuseOrStartPersistentWarmFlight(canceled, Success), canceled))
            throw new InvalidOperationException("Canceled GPU flight was incorrectly reused.");
        return "GPU_PERSISTENT_FLIGHT_POLICY_SMOKE_OK concurrentDedup=true completedMissRefresh=true completedHitRefresh=true faultRetry=true cancelRetry=true diskUnchanged=true";
    }
    private static ManagementScope? _cimv2Scope;
    private static GpuDriverInfo? _gpuCache;
    private static DateTime _gpuCacheAt;
    private static long _detectionCacheGeneration;
    public static long DetectionCacheGeneration => Interlocked.Read(ref _detectionCacheGeneration);
    public const string NvidiaDriversUrl = "https://www.nvidia.com/en-us/drivers/";
    public const string AmdDriversUrl = "https://www.amd.com/en/support/download/drivers.html";
    public const string IntelDriversUrl = "https://www.intel.com/content/www/us/en/download-center/home.html";
    public const string ExostenzaThreadUrl = "https://forums.guru3d.com/threads/exostenza-edition%E2%84%A2-rtx-616-92-back-catalogue-de-bloat-benchmarks.452356/";
    public static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpankyhoOptimizer", "DriverCache");
    public static async Task<GpuDriverInfo?> TryWarmPersistentDetectionCacheAsync(CancellationToken token = default)
    {
        long generation = DetectionCacheGeneration;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Task<GpuDriverInfo?> flight;
        lock (DetectionCacheLock)
        {
            if (_gpuCache != null && DateTime.UtcNow - _gpuCacheAt < TimeSpan.FromMinutes(10))
                return _gpuCache;

            // Expired memory state must force a fresh disk TTL check.
            // Completed success/miss/fault flights cannot be reused forever.
            // A still-running flight is shared across concurrent callers.
            flight = _persistentWarmFlight = ReuseOrStartPersistentWarmFlight(
                _persistentWarmFlight, () => Task.Run(() =>
                    PersistentHardwareCacheService.TryReadMachineStable<GpuDriverInfo>("gpu-primary", TimeSpan.FromDays(1), out var cached)
                        ? cached
                        : null));
            // Capture the owned Task in this same lock. Reading the shared
            // field in a later lock can pick up another caller's new flight.
        }
        var persisted = await flight.WaitAsync(token).ConfigureAwait(false);

        lock (DetectionCacheLock)
        {
            // PnP invalidation may arrive after the disk await and before
            // publishing. The generation check and publication must be atomic.
            if (generation != DetectionCacheGeneration) return null;
            if (persisted != null)
            {
                _gpuCache = persisted;
                _gpuCacheAt = DateTime.UtcNow;
            }
        }
        LogService.Info("performance", $"GPU_PERSISTENT_CACHE_READ hit={persisted != null} ms={sw.ElapsedMilliseconds}");
        return persisted;
    }

    public static async Task<GpuDriverInfo?> DetectPrimaryGpuAsync(CancellationToken token = default, bool forceRefresh = false)
    {
        long generation = DetectionCacheGeneration;
        lock (DetectionCacheLock)
        {
            if (!forceRefresh && _gpuCache != null && DateTime.UtcNow - _gpuCacheAt < TimeSpan.FromMinutes(10))
                return _gpuCache;
        }
        if (!forceRefresh)
        {
            var persisted = await TryWarmPersistentDetectionCacheAsync(token).ConfigureAwait(false);
            if (generation != DetectionCacheGeneration) return null;
            if (persisted != null) return persisted;
        }
        await DetectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (generation != DetectionCacheGeneration) return null;
            lock (DetectionCacheLock)
            {
                if (!forceRefresh && _gpuCache != null && DateTime.UtcNow - _gpuCacheAt < TimeSpan.FromMinutes(10))
                    return _gpuCache;
            }
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                // Registry exposes the same installed display-driver identity without paying
                // the ~100 ms cold WMI/CIM startup cost. WMI remains the authoritative fallback.
                var registryResult = TryDetectPrimaryGpuFromRegistry();
                if (registryResult != null) return registryResult;

                ManagementScope? scope = null;
                try
                {
                    lock (CimScopeLock)
                    {
                        scope = _cimv2Scope ??= new ManagementScope(@"\\.\root\CIMV2");
                        if (!scope.IsConnected) scope.Connect();
                    }

                    using var searcher = new ManagementObjectSearcher(
                        scope,
                        new ObjectQuery("SELECT Name,AdapterCompatibility,DriverVersion,DriverDate FROM Win32_VideoController"),
                        new System.Management.EnumerationOptions
                        {
                            Timeout = TimeSpan.FromSeconds(2),
                            ReturnImmediately = false,
                            Rewindable = false
                        });
                    using var results = searcher.Get();

                    foreach (ManagementObject item in results)
                    {
                        using var ownedItem = item;
                        token.ThrowIfCancellationRequested();
                        string name = item["Name"]?.ToString() ?? "GPU";
                        string manufacturer = item["AdapterCompatibility"]?.ToString() ?? "Unknown";
                        string raw = item["DriverVersion"]?.ToString() ?? "?";
                        DateTime? date = null;
                        string? rawDate = item["DriverDate"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(rawDate))
                        {
                            try { date = ManagementDateTimeConverter.ToDateTime(rawDate); } catch { }
                        }
                        string vendor = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" :
                                        name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD" :
                                        name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : manufacturer;
                        return new GpuDriverInfo(name, vendor, raw, NormalizeVersion(vendor, raw), date);
                    }
                    return null;
                }
                catch
                {
                    lock (CimScopeLock)
                    {
                        if (ReferenceEquals(_cimv2Scope, scope))
                            _cimv2Scope = null;
                    }
                    throw;
                }
            }, token).ConfigureAwait(false);
            if (result == null) return null;
            lock (DetectionCacheLock)
            {
                if (generation != DetectionCacheGeneration) return null;
                // Commit both caches under the same lock that PnP invalidation
                // uses, so an old scan cannot restore a removed cache file.
                PersistentHardwareCacheService.WriteMachineStable("gpu-primary", result);
                _gpuCache = result;
                _gpuCacheAt = DateTime.UtcNow;
            }
            return result;
        }
        finally { DetectionGate.Release(); }
    }

    private static GpuDriverInfo? TryDetectPrimaryGpuFromRegistry()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            if (root == null) return null;
            foreach (string adapter in root.GetSubKeyNames())
            foreach (string child in new[] { "0000", "0001", "0002" })
            {
                using var key = root.OpenSubKey(adapter + "\\" + child);
                string name = key?.GetValue("DriverDesc")?.ToString()?.Trim() ?? string.Empty;
                if (name.Length == 0 || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;
                string raw = key?.GetValue("DriverVersion")?.ToString()?.Trim() ?? "?";
                string vendor = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" :
                    name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD" :
                    name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Unknown";
                return new GpuDriverInfo(name, vendor, raw, NormalizeVersion(vendor, raw), null);
            }
        }
        catch { }
        return null;
    }

    public static void InvalidateDetectionCache()
    {
        lock (DetectionCacheLock)
        {
            Interlocked.Increment(ref _detectionCacheGeneration);
            _gpuCache = null;
            _gpuCacheAt = default;
            _persistentWarmFlight = null;
            // Same critical section as scan publish and its disk commit.
            PersistentHardwareCacheService.Remove("gpu-primary");
        }
    }

    public static string NormalizeVersion(string vendor, string raw)
    {
        if (!vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase)) return raw;
        string digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return raw;
        string tail = digits[^5..];
        return tail[..3] + "." + tail[3..];
    }
    public static async Task<DriverReleaseInfo?> CheckOfficialAsync(GpuDriverInfo gpu, CancellationToken token = default)
    {
        string cacheKey = "official:" + gpu.Vendor + ":" + gpu.Name;
        if (DriverReleaseCacheService.TryGet(cacheKey, out var cached)) return cached;
        if (!gpu.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase)) return DriverReleaseCacheService.Set(cacheKey, null);
        if (!gpu.Name.Contains("RTX 3060 Ti", StringComparison.OrdinalIgnoreCase)) return null;
        const string url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid=112&pfid=895&osID=57&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1";
        using var response = await Http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var info = doc.RootElement.GetProperty("IDS")[0].GetProperty("downloadInfo");
        string version = info.GetProperty("Version").GetString() ?? "?";
        string download = info.GetProperty("DownloadURL").GetString() ?? string.Empty;
        return DriverReleaseCacheService.Set(cacheKey, new DriverReleaseInfo("NVIDIA Official", version, download, NvidiaDriversUrl, false, "Game Ready DCH WHQL"));
    }

    public static string GetOfficialPage(GpuDriverInfo gpu) => gpu.Vendor switch
    {
        "NVIDIA" => NvidiaDriversUrl,
        "AMD" => AmdDriversUrl,
        "Intel" => IntelDriversUrl,
        _ => string.Empty
    };
    public static async Task<DriverReleaseInfo?> CheckExostenzaAsync(CancellationToken token = default)
    {
        const string cacheKey = "exostenza:latest";
        if (DriverReleaseCacheService.TryGet(cacheKey, out var cached)) return cached;
        string html = await Http.GetStringAsync(ExostenzaThreadUrl, token);
        var vm = Regex.Match(html, @"Latest release:</u>\s*RTX\s*(\d{3}\.\d{2})\s*Exostenza", RegexOptions.IgnoreCase);
        if (!vm.Success) vm = Regex.Match(html, @"Exostenza Edition[^<]*RTX\s*(\d{3}\.\d{2})", RegexOptions.IgnoreCase);
        string version = vm.Success ? vm.Groups[1].Value : "?";
        var pm = Regex.Match(html, @"href=""([^""]*post-\d+)""[^>]*>To download click here", RegexOptions.IgnoreCase);
        string details = pm.Success ? MakeAbsoluteGuru3d(pm.Groups[1].Value) : ExostenzaThreadUrl;
        return DriverReleaseCacheService.Set(cacheKey, new DriverReleaseInfo("Exostenza Edition", version, string.Empty, details, true,
            "Community de-bloated NVIDIA package z Guru3D. Není podporovaný NVIDIA."));
    }

    private static string MakeAbsoluteGuru3d(string href)
    {
        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return href;
        return "https://forums.guru3d.com" + (href.StartsWith('/') ? href : "/" + href);
    }

    public static bool IsNewer(string candidate, string installed)
    {
        if (Version.TryParse(candidate, out var c) && Version.TryParse(installed, out var i)) return c > i;
        return !string.Equals(candidate, installed, StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsExostenzaCompatible(GpuDriverInfo? gpu) =>
        gpu != null && gpu.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
        gpu.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase);

    public static Task<string> DownloadAsync(DriverReleaseInfo release, IProgress<DriverDownloadProgress>? progress = null, CancellationToken token = default) =>
        OperationCoordinator.RunExclusiveAsync("gpu-download", ct => DownloadCoreAsync(release, progress, ct), TimeSpan.FromMinutes(20), token);

    public static string? TryGetCachedPackage(DriverReleaseInfo release)
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return null;
            string safeSource = Regex.Replace(release.Source, @"[^A-Za-z0-9_-]+", "_");
            string prefix = safeSource + "_" + release.Version;
            return Directory.EnumerateFiles(CacheDir, prefix + ".*")
                .Where(x => !x.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                .Where(x =>
                {
                    try { return new FileInfo(x).Length > 1024 * 1024; }
                    catch { return false; }
                })
                .OrderByDescending(x => new FileInfo(x).LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static string RunExostenzaCacheReuseSmoke()
    {
        Directory.CreateDirectory(CacheDir);
        const string version = "999.99-smoke";
        var release = new DriverReleaseInfo(
            "Exostenza Edition", version, "https://example.invalid/smoke.rar",
            ExostenzaThreadUrl, true, "smoke");
        string file = Path.Combine(CacheDir, $"Exostenza_Edition_{version}.rar");
        string partial = file + ".partial";
        try
        {
            File.WriteAllBytes(file, new byte[(1024 * 1024) + 4096]);
            File.WriteAllBytes(partial, new byte[(1024 * 1024) + 8192]);
            string? found = TryGetCachedPackage(release);
            if (!string.Equals(found, file, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Exostenza cache reuse nevrátil hotový balík nebo použil .partial.");
            return "EXOSTENZA_CACHE_REUSE_SMOKE_OK file=" + Path.GetFileName(found);
        }
        finally
        {
            try { File.Delete(file); } catch { }
            try { File.Delete(partial); } catch { }
        }
    }

    private static string ExostenzaCleanupMarkerPath => Path.Combine(CacheDir, "exostenza-cleanup.pending");
    public static bool HasPendingExostenzaCleanup => File.Exists(ExostenzaCleanupMarkerPath);

    public static bool IsKnownGpuInstallerRunning()
    {
        try
        {
            return ProcessSnapshotCleanup.Inspect(() => Process.GetProcesses(), processes =>
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        if (IsKnownGpuInstallerName(process.ProcessName)) return true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or
                        System.ComponentModel.Win32Exception or NotSupportedException) { }
                }
                return false;
            });
        }
        catch { return false; }
    }

    public static string? TryReadPendingExostenzaMarkerVersion()
    {
        try
        {
            if (!File.Exists(ExostenzaCleanupMarkerPath)) return null;
            string? first = File.ReadLines(ExostenzaCleanupMarkerPath).FirstOrDefault();
            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch { return null; }
    }

    public static string? TryGetPendingExostenzaVersion()
    {
        try
        {
            if (!File.Exists(ExostenzaCleanupMarkerPath)) return null;
            string[] lines = File.ReadAllLines(ExostenzaCleanupMarkerPath);
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) return null;
            if (IsKnownGpuInstallerRunning()) return lines[0].Trim();
            if (lines.Length > 1 && DateTime.TryParse(lines[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime markedUtc) &&
                DateTime.UtcNow - markedUtc.ToUniversalTime() < TimeSpan.FromMinutes(10))
                return lines[0].Trim();
            return null;
        }
        catch (Exception ex)
        {
            LogService.Error("drivers", "Exostenza pending-state read failed", ex);
            return null;
        }
    }

    public static void MarkExostenzaCleanupPending(string version)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(ExostenzaCleanupMarkerPath, version + Environment.NewLine + DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex) { LogService.Error("drivers", "Exostenza cleanup marker failed", ex); }
    }

    public static async Task<string?> ReconcilePendingExostenzaAsync(CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(ExostenzaCleanupMarkerPath)) return null;
            string[] lines = await File.ReadAllLinesAsync(ExostenzaCleanupMarkerPath, token).ConfigureAwait(false);
            string version = lines.Length > 0 ? lines[0].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(version)) return null;
            if (IsKnownGpuInstallerRunning()) return $"Instaluje se Exostenza {version}";

            DateTime markedUtc = DateTime.MinValue;
            if (lines.Length > 1)
                _ = DateTime.TryParse(lines[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out markedUtc);
            if (markedUtc != DateTime.MinValue &&
                DateTime.UtcNow - markedUtc.ToUniversalTime() < TimeSpan.FromSeconds(20))
                return $"Čekám na potvrzení Exostenza {version}";

            InvalidateDetectionCache();
            var gpu = await DetectPrimaryGpuAsync(token, forceRefresh: true).ConfigureAwait(false);
            if (gpu == null) return $"Exostenza {version}: driver stav nelze ověřit";

            if (string.Equals(gpu.DisplayVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                CleanupExostenzaCache(version, removeCurrentVersion: true);
                try { File.Delete(ExostenzaCleanupMarkerPath); } catch { }
                LogService.Info("drivers", $"EXOSTENZA_INSTALL_VERIFIED version={version}");
                return $"Exostenza {version} nainstalována a ověřena";
            }

            if (markedUtc != DateTime.MinValue &&
                DateTime.UtcNow - markedUtc.ToUniversalTime() > TimeSpan.FromMinutes(10))
            {
                LogService.Info("drivers", $"EXOSTENZA_INSTALL_NOT_CONFIRMED expected={version} actual={gpu.DisplayVersion}");
                try { File.Delete(ExostenzaCleanupMarkerPath); } catch { }
                return $"Exostenza {version} nebyla potvrzena • aktuálně {gpu.DisplayVersion} • lze zkusit instalaci znovu";
            }

            return $"Čekám na potvrzení Exostenza {version} • aktuálně {gpu.DisplayVersion}";
        }
        catch (Exception ex)
        {
            LogService.Error("drivers", "Exostenza pending reconciliation failed", ex);
            return "Exostenza stav nelze ověřit";
        }
    }

    public static async Task<string?> MonitorPendingExostenzaAsync(string expectedVersion, CancellationToken token = default)
    {
        string? last = null;
        for (int i = 0; i < 12; i++)
        {
            token.ThrowIfCancellationRequested();
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            last = await ReconcilePendingExostenzaAsync(token).ConfigureAwait(false);
            string? pending = TryGetPendingExostenzaVersion();
            if (string.IsNullOrWhiteSpace(pending))
            {
                LogService.Info("drivers", $"EXOSTENZA_MONITOR_DONE expected={expectedVersion} state={last ?? "none"}");
                return last;
            }
        }

        LogService.Info("drivers", $"EXOSTENZA_MONITOR_TIMEOUT expected={expectedVersion} state={last ?? "pending"}");
        return last;
    }

    public static void CleanupPendingExostenzaCache()
    {
        try
        {
            if (!File.Exists(ExostenzaCleanupMarkerPath)) return;
            string[] lines = File.ReadAllLines(ExostenzaCleanupMarkerPath);
            string version = lines.Length > 0 ? lines[0].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(version)) { File.Delete(ExostenzaCleanupMarkerPath); return; }

            // Never race a still-running NVIDIA/Exostenza installer. A user can reopen
            // Optimizer while setup is still consuming the extracted payload.
            if (IsKnownGpuInstallerRunning())
            {
                LogService.Info("drivers", "Deferred Exostenza cleanup: installer process is still active; keeping marker.");
                return;
            }

            // Give a freshly launched installer a grace window even if its process name is
            // not one of the known NVIDIA setup names. The marker stays persistent for retry.
            if (lines.Length > 1 && DateTime.TryParse(lines[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime markedUtc)
                && DateTime.UtcNow - markedUtc.ToUniversalTime() < TimeSpan.FromMinutes(10))
            {
                LogService.Info("drivers", "Deferred Exostenza cleanup: marker grace period is still active.");
                return;
            }

            // Never delete the current package merely because the grace period expired.
            // Current-version payload is removed only by ReconcilePendingExostenzaAsync after
            // fresh driver read-back confirms the expected version.
            CleanupExostenzaCache(version, removeCurrentVersion: false);
            LogService.Info("drivers", "Pending Exostenza cleanup kept current package until driver read-back confirms success: " + version);
        }
        catch (Exception ex) { LogService.Error("drivers", "Pending Exostenza cleanup failed", ex); }
    }

    public static void CleanupExostenzaCache(string? keepVersion = null, bool removeCurrentVersion = false)
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            foreach (string file in Directory.EnumerateFiles(CacheDir, "Exostenza_Edition_*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                bool isCurrent = !string.IsNullOrWhiteSpace(keepVersion) && name.Contains("_" + keepVersion, StringComparison.OrdinalIgnoreCase);
                if (!isCurrent || removeCurrentVersion)
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            string extractedRoot = Path.Combine(CacheDir, "Extracted");
            if (!Directory.Exists(extractedRoot)) return;
            foreach (string dir in Directory.EnumerateDirectories(extractedRoot, "Exostenza_Edition_*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(dir);
                bool isCurrent = !string.IsNullOrWhiteSpace(keepVersion) && name.Contains("_" + keepVersion, StringComparison.OrdinalIgnoreCase);
                if (!isCurrent || removeCurrentVersion)
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) { LogService.Error("drivers", "Exostenza cache cleanup failed", ex); }
    }

    private static async Task<string> DownloadCoreAsync(DriverReleaseInfo release, IProgress<DriverDownloadProgress>? progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(release.DownloadUrl)) throw new InvalidOperationException("Pro tento zdroj není k dispozici přímý download.");
        Directory.CreateDirectory(CacheDir);
        string ext = Path.GetExtension(new Uri(release.DownloadUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
        string safeSource = Regex.Replace(release.Source, @"[^A-Za-z0-9_-]+", "_");
        string basePath = Path.Combine(CacheDir, $"{safeSource}_{release.Version}{ext}");
        string? cached = TryGetCachedPackage(release);
        if (cached != null)
        {
            var cachedInfo = new FileInfo(cached);
            progress?.Report(new DriverDownloadProgress(cachedInfo.Length, cachedInfo.Length));
            LogService.Info("drivers", "Using cached package: " + Path.GetFileName(cached));
            return cached;
        }

        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await DownloadAttemptAsync(release.DownloadUrl, basePath, progress, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex; LogService.Error("drivers", $"Download attempt {attempt}/3 failed: {release.Source} {release.Version}", ex);
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token);
            }
        }
        throw new InvalidOperationException("Stažení ovladače selhalo po 3 pokusech.", last);
    }

    // Keep the on-disk byte count paired to the offset sent in the Range
    // request. Do not silently resume from a different .partial file.
    private static (string Path, long Offset) ResolveResumeTarget(
        string partial, string resolvedPartial, long requestedOffset)
    {
        // "partial" was measured before we sent Range: requestedOffset.
        // Content-Disposition may specify a different extension, but a
        // similarly named partial at that extension belongs to a different
        // transfer and MUST NOT replace the file we actually requested.
        // Keep the requested file until a complete download can be renamed
        // to the resolved final filename.
        _ = resolvedPartial;
        return (partial, requestedOffset);
    }

    private static void VerifyPartialResponseRange(
        System.Net.HttpStatusCode status,
        System.Net.Http.Headers.ContentRangeHeaderValue? range,
        long requestedOffset)
    {
        if (status != System.Net.HttpStatusCode.PartialContent) return;
        // A 206 response cannot be appended unless it begins at the exact
        // offset sent in the request, even if the server reports success.
        if (range == null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            range.From != requestedOffset ||
            range.To == null || range.To < requestedOffset ||
            (range.Length.HasValue && range.To >= range.Length.Value))
            throw new InvalidDataException(
                $"GPU driver resume rejected inconsistent HTTP Content-Range for offset {requestedOffset}.");
    }

    internal static string RunResumeIntegritySmoke()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "spanky-gpu-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "driver.bin.partial");
            string competing = Path.Combine(root, "driver.rar.partial");
            File.WriteAllBytes(source, new byte[256]);
            File.WriteAllBytes(competing, new byte[512]);
            var chosen = ResolveResumeTarget(source, competing, requestedOffset: 256);
            if (!string.Equals(chosen.Path, source, StringComparison.OrdinalIgnoreCase) ||
                chosen.Offset != 256 ||
                new FileInfo(competing).Length != 512 ||
                new FileInfo(source).Length != 256)
                throw new InvalidOperationException(
                    "GPU resume reused a different partial than the HTTP Range request.");

            bool wrongStartRejected = false;
            try
            {
                VerifyPartialResponseRange(System.Net.HttpStatusCode.PartialContent,
                    new System.Net.Http.Headers.ContentRangeHeaderValue(128, 255, 512), 256);
            }
            catch (InvalidDataException) { wrongStartRejected = true; }
            if (!wrongStartRejected)
                throw new InvalidOperationException(
                    "GPU resume accepted a response whose Content-Range starts at the wrong byte.");

            VerifyPartialResponseRange(System.Net.HttpStatusCode.PartialContent,
                new System.Net.Http.Headers.ContentRangeHeaderValue(256, 511, 512), 256);
            VerifyPartialResponseRange(System.Net.HttpStatusCode.OK, null, 256);
            return "GPU_RESUME_INTEGRITY_SMOKE_OK competingPartialPreserved=true rangeMismatchRejected=true remoteCalls=0";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<string> DownloadAttemptAsync(string url, string basePath, IProgress<DriverDownloadProgress>? progress, CancellationToken token)
    {
        // The direct URL often has no extension while Google Drive supplies the real
        // .rar name in Content-Disposition. Resume the newest partial for this release
        // instead of looking only for the extensionless basePath.partial.
        string directory = Path.GetDirectoryName(basePath)!;
        string stem = Path.GetFileNameWithoutExtension(basePath);
        string? resumePartial = Directory.EnumerateFiles(directory, stem + ".*.partial")
            .OrderByDescending(x => new FileInfo(x).LastWriteTimeUtc)
            .FirstOrDefault();
        string partial = resumePartial ?? basePath + ".partial";
        long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        // HTTP 416 on a resumed download can mean the partial already contains the
        // complete object, but only promote it when the server gives us a trustworthy
        // object length. A missing Content-Range length is not proof of completeness.
        if (existing > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            long? remoteLength = response.Content.Headers.ContentRange?.Length;
            if (!remoteLength.HasValue)
            {
                try
                {
                    using var head = new HttpRequestMessage(HttpMethod.Head, url);
                    using var headResponse = await Http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, token);
                    if (headResponse.IsSuccessStatusCode)
                        remoteLength = headResponse.Content.Headers.ContentLength;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    LogService.Info("drivers", "HTTP 416 metadata HEAD unavailable; partial will not be promoted blindly.");
                }
            }

            if (remoteLength.HasValue && existing == remoteLength.Value)
            {
                string existingExt = Path.GetExtension(Path.GetFileNameWithoutExtension(partial));
                string completed = string.IsNullOrWhiteSpace(existingExt) ? basePath : Path.ChangeExtension(basePath, existingExt);
                if (File.Exists(completed)) File.Delete(completed);
                File.Move(partial, completed);
                LogService.Info("drivers", $"Promoted verified completed partial after HTTP 416: {Path.GetFileName(completed)} bytes={existing}");
                progress?.Report(new DriverDownloadProgress(existing, remoteLength));
                return completed;
            }

            if (remoteLength.HasValue && existing > remoteLength.Value)
            {
                LogService.Info("drivers", $"Discarding oversized partial after HTTP 416: local={existing} remote={remoteLength.Value}");
                try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                existing = 0;
            }
        }
        bool rangeIgnored = existing > 0 && response.StatusCode == System.Net.HttpStatusCode.OK;
        if (rangeIgnored)
        {
            // The server ignored Range and is sending the whole object again. Never append
            // that full response to stale bytes: force a clean/truncating write below.
            LogService.Info("drivers", $"Resume Range ignored by server; restarting download from byte 0 (stale={existing}).");
            existing = 0;
        }
        else if (existing > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            existing = 0;
        }
        response.EnsureSuccessStatusCode();
        VerifyPartialResponseRange(response.StatusCode, response.Content.Headers.ContentRange, existing);
        string headerName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ?? string.Empty;
        headerName = headerName.Trim('"');
        string ext = Path.GetExtension(headerName);
        string final = string.IsNullOrWhiteSpace(ext) ? basePath : Path.ChangeExtension(basePath, ext);
        string resolvedPartial = final + ".partial";
        (partial, existing) = ResolveResumeTarget(partial, resolvedPartial, existing);
        if (existing > 0 && !File.Exists(partial))
            throw new IOException("GPU driver partial disappeared after HTTP Range request.");
        long? content = response.Content.Headers.ContentLength;
        long? total = content.HasValue ? existing + content.Value : null;
        // Close both response/input and the exclusive output handle before promoting
        // the .partial file. Moving while the FileStream is still alive fails on Windows
        // with ERROR_SHARING_VIOLATION and then the next retry can hit HTTP 416.
        {
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(partial, existing > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
            if (existing > 0)
            {
                // An independent worker may have modified this file after the
                // Range request. Check the exact size under an exclusive file
                // handle; never append at a different offset.
                if (output.Length != existing)
                    throw new InvalidDataException("GPU driver partial length changed during HTTP resume.");
                output.Position = existing;
            }
            byte[] buffer = new byte[1024 * 128]; long received = existing;
            while (true)
            {
                int read = await input.ReadAsync(buffer, token); if (read <= 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), token); received += read;
                progress?.Report(new DriverDownloadProgress(received, total));
            }
            await output.FlushAsync(token);
        }
        if (File.Exists(final)) File.Delete(final);
        File.Move(partial, final);
        return final;
    }

    public static void LaunchInstaller(string path, string? arguments = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Instalační soubor nebyl nalezen.", path);
        string fullPath = Path.GetFullPath(path);
        lock (InstallerLaunchLock)
        {
            bool sameRecent = string.Equals(_lastInstallerPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
                              DateTime.UtcNow - _lastInstallerLaunchUtc < TimeSpan.FromMinutes(2);
            bool knownInstallerRunning = IsKnownGpuInstallerRunning();
            if (sameRecent || knownInstallerRunning)
                throw new InvalidOperationException("GPU instalátor už běží nebo byl právě spuštěn. Další instance nebyla otevřena.");

            using var started = Process.Start(new ProcessStartInfo(fullPath, arguments ?? string.Empty)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(fullPath)!
            });
            if (started == null) throw new InvalidOperationException("Instalátor ovladače se nepodařilo spustit.");
            _lastInstallerPath = fullPath;
            _lastInstallerLaunchUtc = DateTime.UtcNow;
            LogService.Info("drivers", "GPU_INSTALLER_LAUNCH_SINGLE_INSTANCE path=" + Path.GetFileName(fullPath));
        }
    }
    public static void OpenSource(string url)
    {
        if (!string.IsNullOrWhiteSpace(url)) ProcessRunner.Open(url);
    }
    public static async Task<DriverReleaseInfo?> ResolveExostenzaDownloadAsync(DriverReleaseInfo release, CancellationToken token = default)
    {
        string html = await Http.GetStringAsync(release.DetailsUrl, token);
        var ids = Regex.Matches(html, @"drive\.google\.com/file/d/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).Distinct().ToArray();
        foreach (string id in ids)
        {
            string direct = "https://drive.usercontent.google.com/download?id=" + id + "&export=download&confirm=t";
            using var req = new HttpRequestMessage(HttpMethod.Head, direct); using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
            if (!resp.IsSuccessStatusCode) continue;
            string cd = resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName ?? string.Empty; cd = cd.Trim('\"');
            if (cd.Contains(release.Version, StringComparison.OrdinalIgnoreCase) && cd.Contains("Exostenza", StringComparison.OrdinalIgnoreCase)) return release with { DownloadUrl = direct, Notes = release.Notes + " • " + cd };
        }
        return release;
    }

    public static async Task<string> ExtractArchiveAndFindSetupAsync(string archive, CancellationToken token = default)
    {
        await ArchiveSafetyService.ValidateAsync(archive, token);
        string dir = Path.Combine(CacheDir, "Extracted", Path.GetFileNameWithoutExtension(archive));
        if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
        var r = await ProcessRunner.RunAsync("tar.exe", $"-xf \"{archive}\" -C \"{dir}\"", TimeSpan.FromMinutes(3), token);
        if (!r.Success) throw new InvalidOperationException("Rozbalení driver balíku selhalo: " + (r.StdErr + " " + r.StdOut).Trim());
        ArchiveSafetyService.ValidateExtractedTree(dir);
        string? setup = Directory.EnumerateFiles(dir, "setup.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (setup == null) throw new FileNotFoundException("V rozbaleném driver balíku nebyl nalezen setup.exe.");
        return setup;
    }

}
