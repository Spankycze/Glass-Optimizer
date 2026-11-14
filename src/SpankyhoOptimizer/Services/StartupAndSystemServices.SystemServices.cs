using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed record StartupEntry(
    string Id, string Name, string Command, RegistryHive Hive, string RunPath, bool Enabled,
    string Publisher, string? FilePath, string SourceKind = "Registry", string? SourceId = null, bool CanToggle = true,
    RegistryView View = RegistryView.Registry64);

public sealed record StartupInventoryDelta(int Added, int Removed, int Changed)
{
    public bool HasChanges => Added > 0 || Removed > 0 || Changed > 0;
    public string Summary => HasChanges
        ? $"+{Added} / -{Removed} / ~{Changed}"
        : "Bez změn";
}
public static class GhostUsbService
{
    private const string PersistentCacheKey = "ghost-usb-v1";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(3);
    private static readonly object CacheLock = new();
    private static IReadOnlyList<string>? _cache;
    private static DateTime _cacheAt;
    private static Task<IReadOnlyList<string>>? _flight;

    public static IReadOnlyList<string>? TryGetFreshSnapshot()
    {
        lock (CacheLock)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < CacheTtl)
                return _cache;
        }

        if (PersistentHardwareCacheService.TryReadMachineStable<string[]>(
                PersistentCacheKey, CacheTtl, out var persisted) &&
            persisted != null)
        {
            lock (CacheLock)
            {
                _cache = persisted;
                _cacheAt = DateTime.UtcNow;
            }
            LogService.Info("performance", $"GHOST_USB_PERSISTENT_HIT count={persisted.Length}");
            return persisted;
        }

        return null;
    }

    public static Task<IReadOnlyList<string>> GetAsync()
    {
        var cached = TryGetFreshSnapshot();
        if (cached != null) return Task.FromResult(cached);

        lock (CacheLock)
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < CacheTtl) return Task.FromResult(_cache);
            if (_flight != null && !_flight.IsCompleted) return _flight;
            _flight = LoadAsync();
            return _flight;
        }
    }

    private static async Task<IReadOnlyList<string>> LoadAsync()
    {
        try
        {
            var result = await ProcessRunner.RunAsync("pnputil.exe", "/enum-devices /disconnected /class USB", TimeSpan.FromSeconds(30));
            IReadOnlyList<string> data = !result.Success ? Array.Empty<string>() : Regex.Matches(result.StdOut, "(?:USB|HID)\\[^\r\n]+", RegexOptions.IgnoreCase)
                .Select(x => x.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            PersistentHardwareCacheService.WriteMachineStable(PersistentCacheKey, data.ToArray());
            lock (CacheLock) { _cache = data; _cacheAt = DateTime.UtcNow; _flight = null; }
            return data;
        }
        catch { lock (CacheLock) _flight = null; throw; }
    }

    public static void InvalidateCache()
    {
        lock (CacheLock) { _cache = null; _cacheAt = default; }
        PersistentHardwareCacheService.Remove(PersistentCacheKey);
    }

    internal static async Task RunPersistentCacheSmokeAsync(CancellationToken token)
    {
        InvalidateCache();

        var freshSw = Stopwatch.StartNew();
        var fresh = await GetAsync().WaitAsync(token).ConfigureAwait(false);
        freshSw.Stop();

        lock (CacheLock)
        {
            _cache = null;
            _cacheAt = default;
            _flight = null;
        }

        var persistentSw = Stopwatch.StartNew();
        var persisted = await GetAsync().WaitAsync(token).ConfigureAwait(false);
        persistentSw.Stop();

        if (!fresh.SequenceEqual(persisted, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ghost USB persistent cache mismatch.");
        if (persistentSw.ElapsedMilliseconds > 150)
            throw new InvalidOperationException($"Ghost USB persistent cache is too slow: {persistentSw.ElapsedMilliseconds} ms.");

        LogService.Info($"GHOST_USB_CACHE_SMOKE_OK freshMs={freshSw.ElapsedMilliseconds} persistentMs={persistentSw.ElapsedMilliseconds} count={persisted.Count}");
    }

    private static async Task<bool> IsCurrentlyDisconnectedAsync(string instanceId)
    {
        var result = await ProcessRunner.RunAsync("pnputil.exe", "/enum-devices /disconnected", TimeSpan.FromSeconds(30));
        if (!result.Success) throw new InvalidOperationException("Fresh disconnected-device read-back selhal: " + (result.StdErr + result.StdOut).Trim());
        return Regex.Matches(result.StdOut, "Instance ID:\\s*(.+)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Trim())
            .Any(id => string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task RemoveAsync(string instanceId)
    {
        if (!Regex.IsMatch(instanceId, "^(USB|HID)\\\\", RegexOptions.IgnoreCase)) throw new ArgumentException("Neplatné USB ID.");
        if (!await IsCurrentlyDisconnectedAsync(instanceId).ConfigureAwait(false))
            throw new InvalidOperationException("Zařízení už není odpojené; odebrání bylo bezpečně zrušeno.");

        var result = await PrivilegedProcessRunner.RunAsync("pnputil.exe", $"/remove-device \"{instanceId}\"", TimeSpan.FromSeconds(30));
        if (!result.Success) throw new InvalidOperationException(result.StdErr + result.StdOut);
        if (await IsCurrentlyDisconnectedAsync(instanceId).ConfigureAwait(false))
            throw new InvalidOperationException("Read-back stále vidí zařízení mezi odpojenými položkami.");
        InvalidateCache();
        LogService.Info("Removed verified disconnected USB device: " + instanceId);
    }
}

public static class LauncherCacheService
{
    public static IReadOnlyDictionary<string, string[]> Catalog { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Steam"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam", "htmlcache") },
        ["Battle.net"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Battle.net", "Cache"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Battle.net", "Cache") },
        ["Epic Games"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "webcache*") },
        ["EA app"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Electronic Arts", "EA Desktop", "CEF", "Cache") },
        ["Ubisoft Connect"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ubisoft Game Launcher", "cache") },
        ["Riot Client"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Cache") },
        ["GOG Galaxy"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GOG Galaxy", "webcache") },
        ["Discord"] = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "Cache"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "Code Cache"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "GPUCache") }
    };

    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!path.Contains('*')) { yield return path; continue; }
            var parent = Path.GetDirectoryName(path); var pattern = Path.GetFileName(path);
            if (parent == null || !Directory.Exists(parent)) continue;
            foreach (var dir in Directory.EnumerateDirectories(parent, pattern))
            {
                // Never follow a junction/symlink into an unrelated tree.
                if (!IsReparsePoint(dir)) yield return dir;
            }
        }
    }

    public static Task<long> GetSizeAsync(IEnumerable<string> paths, CancellationToken token = default) => Task.Run(() =>
    {
        long total = 0;
        foreach (string path in Expand(paths))
        {
            token.ThrowIfCancellationRequested();
            total += DirectorySize(path, token);
        }
        return total;
    }, token);

    // Cache cleanup must never traverse a junction or symbolic link. A planted
    // or redirected link inside TEMP/a launcher cache would otherwise make the
    // recursive delete remove files outside the selected cache root.
    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; } // Unreadable path: fail closed, do not traverse.
    }

    private static (List<string> Files, List<string> Directories) EnumerateTreeNoReparse(
        string root, CancellationToken token)
    {
        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            if (IsReparsePoint(directory)) continue;
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsReparsePoint(file)) files.Add(file);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            try
            {
                foreach (string subdirectory in Directory.EnumerateDirectories(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (IsReparsePoint(subdirectory)) continue;
                    directories.Add(subdirectory);
                    pending.Push(subdirectory);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return (files, directories);
    }

    private static long DirectorySize(string path, CancellationToken token = default)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        var (files, _) = EnumerateTreeNoReparse(path, token);
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            try { total += new FileInfo(file).Length; } catch { }
        }
        return total;
    }

    public sealed record CleanupResult(long BeforeBytes, long AfterBytes, int FailedFiles)
    {
        public long FreedBytes => Math.Max(0, BeforeBytes - AfterBytes);
        public bool FullyCleared => AfterBytes == 0 && FailedFiles == 0;
    }

    public static Task<CleanupResult> ClearVerifiedAsync(IEnumerable<string> paths, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var expanded = Expand(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        long before = 0;
        foreach (string path in expanded)
        {
            token.ThrowIfCancellationRequested();
            before += DirectorySize(path, token);
        }

        int failed = 0;
        foreach (var path in expanded)
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(path)) continue;
            // A redirected cache root is refused, not followed. Report it as a
            // failure so the UI never claims a junctioned tree was cleared.
            if (IsReparsePoint(path)) { failed++; continue; }
            var (files, dirs) = EnumerateTreeNoReparse(path, token);

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                try { File.Delete(file); } catch { failed++; }
            }

            foreach (var dir in dirs.OrderByDescending(x => x.Length))
            {
                token.ThrowIfCancellationRequested();
                try { Directory.Delete(dir, false); } catch { if (Directory.Exists(dir)) failed++; }
            }
        }

        long after = 0;
        foreach (string path in expanded)
        {
            token.ThrowIfCancellationRequested();
            after += DirectorySize(path, token);
        }
        return new CleanupResult(before, after, failed);
    }, token);

    public static async Task ClearAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        _ = await ClearVerifiedAsync(paths, token);
    }
}

public static class LauncherGamingModeService
{
    private static readonly Dictionary<string, string[]> Processes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Steam"] = new[] { "steamwebhelper" },
        ["Battle.net"] = new[] { "Battle.net", "Agent" },
        ["Epic Games"] = new[] { "EpicWebHelper" },
        ["EA app"] = new[] { "EADesktop", "EABackgroundService" },
        ["Ubisoft Connect"] = new[] { "upc", "UbisoftConnect", "UplayWebCore" },
        ["Riot Client"] = new[] { "RiotClientServices", "RiotClientUxRender" },
        ["GOG Galaxy"] = new[] { "GalaxyClient Helper", "GalaxyClientService" },
        ["Discord"] = new[] { "Discord" }
    };

    public static Task<string> ApplyAsync(IEnumerable<string> selected) => Task.Run(() =>
    {
        var changed = new List<string>();
        var wanted = LauncherProcessMatcher.BuildIndex(selected, Processes);
        if (wanted.Count > 0)
        {
            // A single native snapshot replaces repeated full-table searches
            // for every selected launcher. Dispose *all* Process wrappers even
            // when an individual process cannot be inspected or modified.
            ProcessSnapshotCleanup.Inspect(Process.GetProcesses, processes =>
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (!wanted.TryGetValue(name, out List<string>? launchers)) continue;
                        process.PriorityClass = ProcessPriorityClass.BelowNormal;
                        foreach (string launcher in launchers)
                            changed.Add($"{launcher}: {name}");
                    }
                    catch { /* Protected or already exited process. */ }
                }
                return true;
            });
        }
        LogService.Info("Launcher gaming mode: " + string.Join(", ", changed));
        return changed.Count == 0 ? "Neběží žádný vybraný pomocný proces." : "Priorita BelowNormal nastavena:\n" + string.Join("\n", changed.Distinct());
    });

    public static async Task<string> SetStartupAsync(IEnumerable<string> selected, bool enabled)
    {
        var wanted = selected.ToArray();
        var entries = await StartupManagerService.GetAsync();
        var changed = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Enabled == enabled) continue;
            var launcher = wanted.FirstOrDefault(x =>
                Processes.TryGetValue(x, out var names) &&
                LauncherProcessMatcher.StartupMatches(x, names, entry.Name, entry.Command));
            if (launcher == null) continue;
            await StartupManagerService.SetEnabledAsync(entry, enabled); changed.Add(entry.Name);
        }
        return changed.Count == 0 ? "Nebyla nalezena odpovídající položka po spuštění." : $"{(enabled ? "Obnoveno" : "Vypnuto")}:\n" + string.Join("\n", changed.Distinct());
    }
}

public static class StorageMaintenanceService
{
    private static readonly object HealthGate = new();
    private static string? _healthCache;
    private static DateTime _healthCacheAt;
    private static Task<string>? _healthInFlight;

    public static async Task<string> GetHealthAsync()
    {
        Task<string> task;
        lock (HealthGate)
        {
            if (_healthCache != null && DateTime.UtcNow - _healthCacheAt < TimeSpan.FromMinutes(15)) return _healthCache;
            if (_healthInFlight == null)
            {
                var created = LoadHealthCoreAsync();
                _healthInFlight = created;
                task = created;
                _ = ObserveHealthAsync(created);
            }
            else
            {
                task = _healthInFlight;
            }
        }

        var completed = await Task.WhenAny(task, Task.Delay(800)).ConfigureAwait(false);
        if (ReferenceEquals(completed, task)) return await task.ConfigureAwait(false);
        return DriveFallback();
    }


    private static async Task ObserveHealthAsync(Task<string> created)
    {
        try
        {
            string result = await created.ConfigureAwait(false);
            lock (HealthGate)
            {
                _healthCache = result;
                _healthCacheAt = DateTime.UtcNow;
                if (ReferenceEquals(_healthInFlight, created)) _healthInFlight = null;
            }
        }
        catch
        {
            lock (HealthGate)
                if (ReferenceEquals(_healthInFlight, created)) _healthInFlight = null;
        }
    }

    private static Task<string> LoadHealthCoreAsync()
    {
        // wmic.exe is absent on current Windows 11 builds. Query the same
        // Win32_DiskDrive data through System.Management instead of silently
        // degrading every storage page to free-space-only output.
        return Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"\\.\root\CIMV2"),
                    new ObjectQuery("SELECT Model,Status,Size FROM Win32_DiskDrive"),
                    new System.Management.EnumerationOptions
                    {
                        Timeout = TimeSpan.FromSeconds(3),
                        ReturnImmediately = false,
                        Rewindable = false
                    });
                using var results = searcher.Get();
                var blocks = new List<string>(4);
                foreach (ManagementObject disk in results)
                {
                    using (disk)
                    {
                        string model = disk["Model"]?.ToString()?.Trim() ?? "Neznámý";
                        string status = disk["Status"]?.ToString()?.Trim() ?? "Unknown";
                        string size = disk["Size"]?.ToString() ?? "0";
                        blocks.Add($"Model={model}{Environment.NewLine}Status={status}{Environment.NewLine}Size={size}");
                    }
                }
                return blocks.Count == 0
                    ? DriveFallback()
                    : string.Join(Environment.NewLine + Environment.NewLine, blocks);
            }
            catch
            {
                return DriveFallback();
            }
        });
    }

    private static string DriveFallback() => string.Join("\n", DriveInfo.GetDrives().Where(x => x.IsReady)
        .Select(x => $"{x.Name} — {x.DriveFormat} — {x.AvailableFreeSpace / 1073741824d:0.0} GB volných"));

    public sealed record CleanupCategoryScan(string Id, string Name, IReadOnlyList<string> Paths, long Bytes);
    public sealed record CleanupScanResult(IReadOnlyList<CleanupCategoryScan> Categories, long TotalBytes);

    private static (string Id, string Name, string[] Paths)[] CleanupCategories()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            ("temp", "Uživatelský Temp", new[] { Path.GetTempPath() }),
            ("d3d", "Direct3D shader cache", new[] { Path.Combine(local, "D3DSCache") }),
            ("nvidia", "NVIDIA shader cache", new[] { Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache") }),
            ("amd", "AMD shader cache", new[] { Path.Combine(local, "AMD", "DxCache") })
        };
    }

    public static async Task<CleanupScanResult> ScanSafeCleanupAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var definitions = CleanupCategories();
        var tasks = new Task<long>[definitions.Length];
        for (int i = 0; i < definitions.Length; i++)
            tasks[i] = LauncherCacheService.GetSizeAsync(definitions[i].Paths, token);

        await Task.WhenAll(tasks).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var categories = new CleanupCategoryScan[definitions.Length];
        long total = 0;
        for (int i = 0; i < definitions.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            long bytes = await tasks[i].ConfigureAwait(false);
            categories[i] = new CleanupCategoryScan(definitions[i].Id, definitions[i].Name, definitions[i].Paths, bytes);
            total += bytes;
        }
        return new CleanupScanResult(categories, total);
    }

    public static async Task<long> GetSafeCleanupSizeAsync(CancellationToken token = default) =>
        (await ScanSafeCleanupAsync(token).ConfigureAwait(false)).TotalBytes;

    public static Task<LauncherCacheService.CleanupResult> ClearSafeCachesAsync(CancellationToken token = default) =>
        LauncherCacheService.ClearVerifiedAsync(SafeCleanupPaths(), token);

    public static Task<LauncherCacheService.CleanupResult> ClearSafeCachesAsync(IEnumerable<string> categoryIds, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var selected = categoryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = CleanupCategories()
            .Where(x => selected.Contains(x.Id))
            .SelectMany(x => x.Paths)
            .ToArray();
        return LauncherCacheService.ClearVerifiedAsync(paths, token);
    }

    public static Task<long> GetShaderCacheSizeAsync(CancellationToken token = default) => LauncherCacheService.GetSizeAsync(ShaderCachePaths(), token);
    public static Task<LauncherCacheService.CleanupResult> ClearShaderCachesAsync(CancellationToken token = default) => LauncherCacheService.ClearVerifiedAsync(ShaderCachePaths(), token);

    private static IEnumerable<string> SafeCleanupPaths()
    {
        foreach (var category in CleanupCategories())
            foreach (string path in category.Paths)
                yield return path;
    }

    private static IEnumerable<string> ShaderCachePaths()
    {
        foreach (var category in CleanupCategories())
        {
            if (category.Id.Equals("temp", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string path in category.Paths)
                yield return path;
        }
    }
}
