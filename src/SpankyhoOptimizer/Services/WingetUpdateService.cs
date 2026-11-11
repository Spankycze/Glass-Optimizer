using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record WingetUpgradeItem(
    string Name,
    string Id,
    string InstalledVersion,
    string AvailableVersion,
    string Source);

public sealed record WingetStatus(string Version, int UpgradeCount, bool Available, string Message);

public static class WingetUpdateService
{
    static WingetUpdateService()
    {
        PersistentHardwareCacheService.Remove("winget-upgrades-v1");
        PersistentHardwareCacheService.Remove("winget-status-v1");
    }

    private static WingetStatus? _cachedStatus;
    private static DateTime _cachedStatusAtUtc;
    private static readonly SemaphoreSlim StatusGate = new(1, 1);
    private static readonly object UpgradesGate = new();
    private static IReadOnlyList<WingetUpgradeItem>? _cachedUpgrades;
    private static DateTime _cachedUpgradesAtUtc;
    private static Task<IReadOnlyList<WingetUpgradeItem>>? _upgradesFlight;
    private static int _upgradesFlightGeneration = -1;
    private static int _upgradesGeneration;
    private static int _upgradeScanCount;
    private static readonly TimeSpan UpgradesTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DisplayUpgradesTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StatusTtl = TimeSpan.FromMinutes(10);
    private const string PersistentStatusKey = "winget-status-v1";
    private const string PersistentUpgradesKey = "winget-upgrades-v1";

    public static async Task<WingetStatus> GetStatusAsync(CancellationToken token = default)
    {
        if (_cachedStatus != null && DateTime.UtcNow - _cachedStatusAtUtc < TimeSpan.FromMinutes(5))
            return _cachedStatus;
        var persisted = TryReadPersistentStatus();
        if (persisted != null)
        {
            _cachedStatus = persisted;
            _cachedStatusAtUtc = DateTime.UtcNow;
            LogService.Info("winget", $"Persistent Winget status cache hit upgrades={persisted.UpgradeCount}");
            return persisted;
        }

        await StatusGate.WaitAsync(token);
        try
        {
            if (_cachedStatus != null && DateTime.UtcNow - _cachedStatusAtUtc < TimeSpan.FromMinutes(5))
                return _cachedStatus;
            persisted = TryReadPersistentStatus();
            if (persisted != null)
            {
                _cachedStatus = persisted;
                _cachedStatusAtUtc = DateTime.UtcNow;
                return persisted;
            }
            // Avoid running two winget.exe instances concurrently. On some systems
            // a parallel --version + upgrade probe can make winget return 0x8A150001
            // (APPINSTALLER_CLI_ERROR_INTERNAL_ERROR) even though the upgrade scan succeeds.
            var versionResult = await ProcessRunner.RunAsync(
                "winget.exe", "--version", TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            if (!versionResult.Success)
                throw new InvalidOperationException(
                    $"winget --version selhal (exit {versionResult.ExitCode}): " +
                    (versionResult.StdErr + versionResult.StdOut).Trim());

            var upgrades = await GetUpgradesAsync(token).ConfigureAwait(false);
            string version = (versionResult.StdOut ?? string.Empty).Trim();
            bool versionKnown = !string.IsNullOrWhiteSpace(version);
            if (!versionKnown) version = "neznámá";
            _cachedStatus = new WingetStatus(version, upgrades.Count, true,
                upgrades.Count == 0 ? "Všechny Winget aplikace jsou aktuální." : $"Dostupné aktualizace: {upgrades.Count}");
            _cachedStatusAtUtc = DateTime.UtcNow;
            // A transient empty --version output must not pin "neznámá" in the persistent cache.
            if (versionKnown) WritePersistentStatus(_cachedStatus);
            return _cachedStatus;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Error("Winget status failed", ex);
            return new WingetStatus("není dostupný", 0, false, "Winget není dostupný nebo kontrola selhala.");
        }
        finally
        {
            StatusGate.Release();
        }
    }

    public static (IReadOnlyList<WingetUpgradeItem> Items, bool NeedsRefresh)? TryGetDisplayUpgrades()
    {
        lock (UpgradesGate)
        {
            if (_cachedUpgrades != null)
            {
                TimeSpan age = DateTime.UtcNow - _cachedUpgradesAtUtc;
                if (age <= DisplayUpgradesTtl)
                    return (_cachedUpgrades, age > UpgradesTtl);
            }

            if (!UserScopedCacheService.TryRead<WingetUpgradeItem[]>(
                    PersistentUpgradesKey, DisplayUpgradesTtl, out var persisted, out var persistedAge) ||
                persisted == null)
                return null;

            bool stale = persistedAge > UpgradesTtl;
            if (!stale)
            {
                _cachedUpgrades = persisted;
                _cachedUpgradesAtUtc = DateTime.UtcNow;
            }
            LogService.Info("winget", stale
                ? $"WINGET_STALE_DISPLAY_HIT count={persisted.Length} ageSec={persistedAge.TotalSeconds:0}"
                : $"WINGET_PERSISTENT_DISPLAY_HIT count={persisted.Length} ageSec={persistedAge.TotalSeconds:0}");
            return (persisted, stale);
        }
    }

    public static Task<IReadOnlyList<WingetUpgradeItem>> GetUpgradesAsync(CancellationToken token = default, bool forceRefresh = false)
    {
        Task<IReadOnlyList<WingetUpgradeItem>> task;
        lock (UpgradesGate)
        {
            if (!forceRefresh && _cachedUpgrades != null && DateTime.UtcNow - _cachedUpgradesAtUtc < UpgradesTtl)
                return Task.FromResult(_cachedUpgrades);

            if (!forceRefresh)
            {
                var persisted = TryReadPersistentUpgrades();
                if (persisted != null)
                {
                    _cachedUpgrades = persisted;
                    _cachedUpgradesAtUtc = DateTime.UtcNow;
                    LogService.Info("winget", $"Persistent Winget upgrades cache hit count={persisted.Length}");
                    return Task.FromResult<IReadOnlyList<WingetUpgradeItem>>(persisted);
                }
            }

            if (_upgradesFlight != null && !_upgradesFlight.IsCompleted &&
                (!forceRefresh || _upgradesFlightGeneration == _upgradesGeneration))
                return _upgradesFlight.WaitAsync(token);

            if (forceRefresh) _upgradesGeneration++;
            int generation = _upgradesGeneration;
            task = _upgradesFlight = LoadUpgradesCoreAsync(generation);
            _upgradesFlightGeneration = generation;
        }

        _ = task.ContinueWith(completed =>
        {
            if (completed.IsFaulted) _ = completed.Exception;
            lock (UpgradesGate)
            {
                if (ReferenceEquals(_upgradesFlight, completed))
                {
                    _upgradesFlight = null;
                    _upgradesFlightGeneration = -1;
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return task.WaitAsync(token);
    }

    private static async Task<IReadOnlyList<WingetUpgradeItem>> LoadUpgradesCoreAsync(int generation)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int scan = Interlocked.Increment(ref _upgradeScanCount);
        ProcessResult result;
        using (TryAcquireTemporarySourceUpdateGuard())
        {
            result = await ProcessRunner.RunAsync("winget.exe",
                "upgrade --accept-source-agreements --disable-interactivity",
                TimeSpan.FromMinutes(3), CancellationToken.None).ConfigureAwait(false);
        }
        if (!result.Success)
            throw new InvalidOperationException(
                $"Winget upgrade scan selhal (exit {result.ExitCode}): " +
                (result.StdErr + result.StdOut).Trim());

        string text = string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr : result.StdOut;
        var items = ParseUpgradeTable(text);
        bool publish;
        lock (UpgradesGate)
        {
            publish = generation == _upgradesGeneration;
            if (publish)
            {
                _cachedUpgrades = items;
                _cachedUpgradesAtUtc = DateTime.UtcNow;
            }
        }
        if (publish)
            WritePersistentUpgrades(items);
        LogService.Info("winget", $"WINGET_UPGRADES_SCAN scan={scan} count={items.Count} ms={sw.ElapsedMilliseconds}");
        return items;
    }

    private static IDisposable? TryAcquireTemporarySourceUpdateGuard()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                return null;

            string installedIndexPath = System.IO.Path.Combine(
                localAppData,
                "Packages",
                "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe",
                "LocalState",
                "Microsoft.Winget.Source_8wekyb3d8bbwe",
                "installed.db");
            if (!System.IO.File.Exists(installedIndexPath))
                return null;

            string wingetTempDir = System.IO.Path.Combine(localAppData, "Temp", "WinGet");
            System.IO.Directory.CreateDirectory(wingetTempDir);
            string msixTempPath = System.IO.Path.Combine(wingetTempDir, "Microsoft.Winget.Source_8wekyb3d8bbwe.msix");
            return new System.IO.FileStream(
                msixTempPath,
                System.IO.FileMode.OpenOrCreate,
                System.IO.FileAccess.ReadWrite,
                System.IO.FileShare.None);
        }
        catch
        {
            return null;
        }
    }

    internal static (int ScanCount, int CachedCount, bool InFlight) GetUpgradeCacheDiagnostics()
    {
        lock (UpgradesGate)
            return (_upgradeScanCount, _cachedUpgrades?.Count ?? -1, _upgradesFlight?.IsCompleted == false);
    }

    private static string ValidatePackageId(string? id)
    {
        // Package IDs are single ASCII identifiers, never command fragments.
        // Quotes, backslashes, whitespace and shell metacharacters can change
        // the command line or create misleading structured WinGet requests.
        if (string.IsNullOrEmpty(id) || id.Length > 160 ||
            !IsAsciiLetterOrDigit(id[0]) ||
            id.Any(ch => !IsAsciiLetterOrDigit(ch) && ch is not ('.' or '-' or '_' or '+')))
            throw new ArgumentException("Neplatné WinGet ID balíčku.", nameof(id));
        return id;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    internal static string RunPackageIdSafetySmoke()
    {
        string[] good = { "Microsoft.VisualStudioCode", "Git.Git", "7zip.7zip", "Mozilla.Firefox.en-US", "9NBLGGH4R315", "Vendor_App.Component+Plus" };
        string[] bad = { "", " ", "App Name", "Vendor.App --source other", "Vendor.App\" --all",
            "Vendor.App\\\" --all", "Vendor.App\n--all", "Vendor.App;drop", "Vendor.App&other",
            "../escape", "Vendor.App/other", "Vendor.App%TEST%", "Vendor.App|more", "Vendor.App*",
            ".leading", "-switch", "Vendor.App:stream", "Vendor.App\targ",
            "Véndor.App", new string('A', 161) };
        foreach (string value in good)
            if (!string.Equals(ValidatePackageId(value), value, StringComparison.Ordinal))
                throw new InvalidOperationException("Valid WinGet identifier was changed: " + value);
        foreach (string value in bad)
        {
            bool rejected = false;
            try { _ = ValidatePackageId(value); }
            catch (ArgumentException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException("Unsafe WinGet ID accepted: " + value);
        }
        return $"WINGET_PACKAGE_ID_SAFETY_SMOKE_OK valid={good.Length} invalidRejected={bad.Length} childProcesses=0";
    }

    public static async Task<ProcessResult> UpgradeAsync(string id, CancellationToken token = default)
    {
        string safe = ValidatePackageId(id);

        try
        {
            if (await PowerShellCenterService.IsWinGetClientAvailableAsync(token).ConfigureAwait(false))
            {
                var structured = await PowerShellCenterService.UpdateWinGetPackageAsync(safe, token).ConfigureAwait(false);
                if (structured.Success)
                {
                    LogService.Info("winget", $"WINGET_STRUCTURED_UPDATE_OK id={safe}");
                    InvalidateStatus();
                    return structured;
                }
                LogService.Info("winget", $"WINGET_STRUCTURED_UPDATE_FALLBACK id={safe} exit={structured.ExitCode}");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Error("winget", $"Structured WinGet update failed, falling back id={safe}", ex);
        }

        var result = await UnelevatedProcessRunner.RunAsync("winget.exe",
            $"upgrade --id \"{safe}\" --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity",
            TimeSpan.FromMinutes(25), token).ConfigureAwait(false);
        InvalidateStatus();
        return result;
    }

    public static async Task<ProcessResult> UpgradeAllAsync(CancellationToken token = default)
    {
        try
        {
            if (await PowerShellCenterService.IsWinGetClientAvailableAsync(token).ConfigureAwait(false))
            {
                var structured = await PowerShellCenterService.UpdateAllWinGetPackagesAsync(token).ConfigureAwait(false);
                if (structured.Success)
                {
                    LogService.Info("winget", "WINGET_STRUCTURED_UPDATE_ALL_OK");
                    InvalidateStatus();
                    return structured;
                }
                LogService.Info("winget", $"WINGET_STRUCTURED_UPDATE_ALL_FALLBACK exit={structured.ExitCode}");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Error("winget", "Structured WinGet update-all failed, falling back", ex);
        }

        var result = await UnelevatedProcessRunner.RunAsync("winget.exe",
            "upgrade --all --silent --accept-source-agreements --accept-package-agreements --disable-interactivity",
            TimeSpan.FromMinutes(45), token).ConfigureAwait(false);
        InvalidateStatus();
        return result;
    }

    public static async Task<ProcessResult> PinAsync(string id, CancellationToken token = default)
    {
        string safe = ValidatePackageId(id);
        var result = await UnelevatedProcessRunner.RunAsync("winget.exe", $"pin add --id \"{safe}\" --exact --accept-source-agreements --disable-interactivity", TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        InvalidateStatus();
        return result;
    }

    public static async Task<ProcessResult> UnpinAsync(string id, CancellationToken token = default)
    {
        string safe = ValidatePackageId(id);
        var result = await UnelevatedProcessRunner.RunAsync("winget.exe", $"pin remove --id \"{safe}\" --exact --disable-interactivity", TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        InvalidateStatus();
        return result;
    }

    internal static void ClearMemoryStatusCacheForSmoke()
    {
        _cachedStatus = null;
        _cachedStatusAtUtc = default;
    }

    internal static void ClearMemoryUpgradesCacheForSmoke()
    {
        lock (UpgradesGate)
        {
            _cachedUpgrades = null;
            _cachedUpgradesAtUtc = default;
        }
    }

    internal static void InvalidateStatusMetadataOnly()
    {
        ClearMemoryStatusCacheForSmoke();
        UserScopedCacheService.Remove(PersistentStatusKey);
    }

    public static void InvalidateStatus()
    {
        InvalidateStatusMetadataOnly();
        lock (UpgradesGate)
        {
            _cachedUpgrades = null;
            _cachedUpgradesAtUtc = default;
            _upgradesGeneration++;
        }
        UserScopedCacheService.Remove(PersistentUpgradesKey);
    }

    private static WingetStatus? TryReadPersistentStatus() =>
        UserScopedCacheService.TryRead<WingetStatus>(PersistentStatusKey, StatusTtl, out var value) ? value : null;

    private static void WritePersistentStatus(WingetStatus status) =>
        UserScopedCacheService.Write(PersistentStatusKey, status);

    private static WingetUpgradeItem[]? TryReadPersistentUpgrades() =>
        UserScopedCacheService.TryRead<WingetUpgradeItem[]>(PersistentUpgradesKey, UpgradesTtl, out var value) ? value : null;

    private static void WritePersistentUpgrades(IReadOnlyList<WingetUpgradeItem> items) =>
        UserScopedCacheService.Write(PersistentUpgradesKey, items.ToArray());

    private static IReadOnlyList<WingetUpgradeItem> ParseUpgradeTable(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<WingetUpgradeItem>();
        string[] lines = text.Replace("\r", string.Empty).Split('\n');
        int separator = Array.FindIndex(lines, line =>
        {
            string compact = line.Replace(" ", string.Empty).Trim();
            return compact.Length >= 8 && compact.All(c => c == '-');
        });
        if (separator < 1) return Array.Empty<WingetUpgradeItem>();

        string header = lines[separator - 1];
        int idStart = header.IndexOf("Id", StringComparison.OrdinalIgnoreCase);
        int installedStart = header.IndexOf("Version", StringComparison.OrdinalIgnoreCase);
        int availableStart = header.IndexOf("Available", StringComparison.OrdinalIgnoreCase);
        int sourceStart = header.IndexOf("Source", StringComparison.OrdinalIgnoreCase);
        if (idStart <= 0 || installedStart <= idStart || availableStart <= installedStart || sourceStart <= availableStart)
            return Array.Empty<WingetUpgradeItem>();
        var items = new List<WingetUpgradeItem>();
        for (int i = separator + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("The following", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("No applicable", StringComparison.OrdinalIgnoreCase)) break;
            if (line.Length <= sourceStart) continue;

            string name = Slice(line, 0, idStart);
            string id = Slice(line, idStart, installedStart);
            string installed = Slice(line, installedStart, availableStart);
            string available = Slice(line, availableStart, sourceStart);
            string source = line[sourceStart..].Trim();
            if (name.Length == 0 || id.Length == 0 || available.Length == 0) continue;
            items.Add(new WingetUpgradeItem(name, id, installed, available, source));
        }
        return items;
    }

    private static string Slice(string text, int start, int end)
    {
        if (start >= text.Length || end <= start) return string.Empty;
        int length = Math.Min(end, text.Length) - start;
        return text.Substring(start, length).Trim();
    }
}
