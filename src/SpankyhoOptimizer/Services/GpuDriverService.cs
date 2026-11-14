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

public static partial class GpuDriverService
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
}
