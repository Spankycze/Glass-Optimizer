using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace SpankyhoOptimizer.Services;

public enum DebloatRisk { Safe, Optional, Advanced, Protected }
public enum DebloatCategory { MicrosoftExtras, Media, Gaming, Communication, Tools, ThirdParty, System }

public sealed record InstalledAppInfo(string PackageName, string PackageFullName, string FamilyName,
    string DisplayName, string Publisher, string? LogoPath, bool Recommended, DebloatCategory Category,
    DebloatRisk Risk, bool Protected, string Reason)
{
    public string RiskLabel => Risk switch { DebloatRisk.Safe => "Safe", DebloatRisk.Optional => "Optional", DebloatRisk.Advanced => "Advanced", _ => "Protected" };
    public string CategoryLabel => Category switch
    {
        DebloatCategory.MicrosoftExtras => "Microsoft Extras",
        DebloatCategory.Media => "Média",
        DebloatCategory.Gaming => "Gaming",
        DebloatCategory.Communication => "Komunikace",
        DebloatCategory.Tools => "Nástroje",
        DebloatCategory.ThirdParty => "Third-party",
        _ => "Systém"
    };
}

public static class InstalledAppService
{
    private static readonly object CacheLock = new();
    private const string PersistentSummaryKey = "installed-apps-summary-v2";
    private const string PersistentLogoMapKey = "installed-app-logo-map-v1";
    private static readonly TimeSpan PersistentSummaryTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DisplaySummaryTtl = TimeSpan.FromHours(2);
    private static readonly TimeSpan PersistentLogoMapTtl = TimeSpan.FromDays(7);
    private static InstalledAppInfo[]? _summaryCache;
    private static IReadOnlyList<InstalledAppInfo>? _detailCache;
    private static DateTime _summaryCacheAt;
    private static DateTime _detailCacheAt;
    private static Task<IReadOnlyList<InstalledAppInfo>>? _summaryFlight;
    private static Task<IReadOnlyList<InstalledAppInfo>>? _detailFlight;
    private static int _inventoryGeneration;
    private static int _summaryJoinDetailCount;
    private static bool _summaryNeedsBackgroundRefresh;
    internal static int SummaryJoinDetailCountForSmoke => Volatile.Read(ref _summaryJoinDetailCount);
    private static readonly FrozenSet<string> RecommendedNames = new[]
    {
        "Clipchamp.Clipchamp", "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.GetHelp",
        "Microsoft.Getstarted", "Microsoft.MicrosoftOfficeHub", "Microsoft.MicrosoftSolitaireCollection",
        "Microsoft.People", "Microsoft.WindowsFeedbackHub", "Microsoft.WindowsMaps", "Microsoft.PowerAutomateDesktop",
        "Microsoft.ZuneVideo", "MSTeams", "Microsoft.Windows.DevHome"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly BoundedLruCache<string, string?> LogoPathCache = new(128, StringComparer.OrdinalIgnoreCase);
    private static readonly object PersistentLogoMapLock = new();
    private static Dictionary<string, string?>? _persistentLogoMapMemory;

    private static readonly string[] ProtectedNames =
    {
        "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI",
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.LockApp",
        "Microsoft.Windows.CloudExperienceHost", "Microsoft.Windows.Search", "Microsoft.WindowsAppRuntime",
        "MicrosoftWindows.Client.CBS", "MicrosoftWindows.Client.Core", "MicrosoftWindows.Client.FileExp",
        "Microsoft.Win32WebViewHost", "Microsoft.WindowsAppRuntime.CBS"
    };

    public static IReadOnlyList<InstalledAppInfo>? TryGetMemorySnapshot(bool includeLogos = false)
    {
        lock (CacheLock)
        {
            var cache = includeLogos ? _detailCache : _summaryCache;
            var cacheAt = includeLogos ? _detailCacheAt : _summaryCacheAt;
            return cache != null && DateTime.UtcNow - cacheAt < TimeSpan.FromMinutes(5) ? cache : null;
        }
    }

    public static Task<bool> TryWarmDisplaySnapshotAsync(CancellationToken token = default) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return TryGetDisplaySnapshot() != null;
        }, token);

    public static IReadOnlyList<InstalledAppInfo>? TryGetDisplaySnapshot()
    {
        var memory = TryGetMemorySnapshot(includeLogos: false);
        if (memory != null) return memory;

        if (!UserScopedCacheService.TryRead<InstalledAppInfo[]>(
                PersistentSummaryKey, DisplaySummaryTtl, out var persisted, out var age) ||
            persisted is not { Length: > 0 })
            return null;

        bool stale = age > PersistentSummaryTtl;
        lock (CacheLock) _summaryNeedsBackgroundRefresh = stale;
        LogService.Info("debloat", stale
            ? $"DEBLOAT_STALE_DISPLAY_HIT count={persisted.Length} ageSec={age.TotalSeconds:0}"
            : $"DEBLOAT_PERSISTENT_DISPLAY_HIT count={persisted.Length} ageSec={age.TotalSeconds:0}");
        return persisted;
    }

    public static IReadOnlyList<InstalledAppInfo>? TryGetFreshSnapshot(bool includeLogos = false)
    {
        lock (CacheLock)
        {
            var cache = includeLogos ? _detailCache : _summaryCache;
            var cacheAt = includeLogos ? _detailCacheAt : _summaryCacheAt;
            if (cache != null && DateTime.UtcNow - cacheAt < TimeSpan.FromMinutes(5))
                return cache;
        }

        if (includeLogos)
        {
            var summary = TryGetFreshSnapshot(includeLogos: false);
            if (summary == null ||
                !UserScopedCacheService.TryRead<Dictionary<string, string?>>(
                    PersistentLogoMapKey, PersistentLogoMapTtl, out var persistedLogos) ||
                persistedLogos == null ||
                persistedLogos.Count == 0)
                return null;

            int covered = 0;
            int resolved = 0;
            var detailed = summary.Select(app =>
            {
                if (!persistedLogos.TryGetValue(app.PackageFullName, out var logo))
                    return app;

                covered++;
                if (logo != null && !File.Exists(logo))
                    return app;

                if (!string.IsNullOrWhiteSpace(logo)) resolved++;
                LogoPathCache.Set(app.PackageFullName, logo);
                return app with { LogoPath = logo };
            }).ToArray();

            if (covered == 0) return null;

            lock (CacheLock)
            {
                _detailCache = detailed;
                _detailCacheAt = DateTime.UtcNow;
            }
            LogService.Info("debloat",
                $"Persistent installed-app detail cache hydrated count={detailed.Length} coverage={covered} logos={resolved}");
            return detailed;
        }

        if (!UserScopedCacheService.TryRead<InstalledAppInfo[]>(PersistentSummaryKey, PersistentSummaryTtl, out var persisted) ||
            persisted is not { Length: > 0 })
            return null;

        lock (CacheLock)
        {
            if (_summaryCache == null || DateTime.UtcNow - _summaryCacheAt >= TimeSpan.FromMinutes(5))
            {
                _summaryCache = persisted;
                _summaryCacheAt = DateTime.UtcNow;
                _summaryNeedsBackgroundRefresh = false;
                LogService.Info("debloat", $"Persistent installed-app summary cache hydrated count={persisted.Length} refreshPending=false");
            }
            return _summaryCache;
        }
    }

    public static Task<IReadOnlyList<InstalledAppInfo>> GetInstalledAsync(CancellationToken token = default, bool forceRefresh = false, bool includeLogos = false)
    {
        lock (CacheLock)
        {
            var cache = includeLogos ? _detailCache : _summaryCache;
            var cacheAt = includeLogos ? _detailCacheAt : _summaryCacheAt;
            if (!forceRefresh && cache != null && DateTime.UtcNow - cacheAt < TimeSpan.FromMinutes(5))
                return Task.FromResult(cache);
            if (!forceRefresh && !includeLogos && _detailFlight != null && !_detailFlight.IsCompleted)
            {
                Interlocked.Increment(ref _summaryJoinDetailCount);
                LogService.Info("debloat", "INSTALLED_APP_SUMMARY_JOIN_DETAIL source=initial-cache-check");
                return AwaitDetailFlightAsSummaryAsync(_detailFlight, token);
            }

            var flight = includeLogos ? _detailFlight : _summaryFlight;
            if (flight != null && !flight.IsCompleted)
                return flight.WaitAsync(token);
        }

        if (!forceRefresh && !includeLogos &&
            UserScopedCacheService.TryRead<InstalledAppInfo[]>(PersistentSummaryKey, PersistentSummaryTtl, out var persisted) &&
            persisted is { Length: > 0 })
        {
            lock (CacheLock)
            {
                if (_summaryCache == null || DateTime.UtcNow - _summaryCacheAt >= TimeSpan.FromMinutes(5))
                {
                    _summaryCache = persisted;
                    _summaryCacheAt = DateTime.UtcNow;
                    _summaryNeedsBackgroundRefresh = false;
                }
                LogService.Info("debloat", $"Persistent installed-app summary cache hit count={_summaryCache.Length} refreshPending={_summaryNeedsBackgroundRefresh}");
                return Task.FromResult<IReadOnlyList<InstalledAppInfo>>(_summaryCache);
            }
        }

        lock (CacheLock)
        {
            var cache = includeLogos ? _detailCache : _summaryCache;
            var cacheAt = includeLogos ? _detailCacheAt : _summaryCacheAt;
            if (!forceRefresh && cache != null && DateTime.UtcNow - cacheAt < TimeSpan.FromMinutes(5))
                return Task.FromResult(cache);
            if (!forceRefresh && !includeLogos && _detailFlight != null && !_detailFlight.IsCompleted)
            {
                Interlocked.Increment(ref _summaryJoinDetailCount);
                LogService.Info("debloat", "INSTALLED_APP_SUMMARY_JOIN_DETAIL source=create-flight-check");
                return AwaitDetailFlightAsSummaryAsync(_detailFlight, token);
            }
            var flight = includeLogos ? _detailFlight : _summaryFlight;
            if (flight != null && !flight.IsCompleted)
                return flight.WaitAsync(token);

            if (forceRefresh) _inventoryGeneration++;
            int generation = _inventoryGeneration;
            var created = WorkerExecutionContext.IsWorker || !includeLogos
                ? Task.Run(() => Scan(CancellationToken.None, includeLogos, generation))
                : forceRefresh
                    ? RefreshDetailFromFreshSummaryAsync(generation, CancellationToken.None)
                    : GetViaWorkerAsync(includeLogos, generation, CancellationToken.None);
            if (includeLogos) _detailFlight = created; else _summaryFlight = created;
            _ = created.ContinueWith(t =>
            {
                if (t.IsFaulted) _ = t.Exception;
                lock (CacheLock)
                {
                    if (includeLogos)
                    {
                        if (ReferenceEquals(_detailFlight, created)) _detailFlight = null;
                    }
                    else if (ReferenceEquals(_summaryFlight, created))
                    {
                        _summaryFlight = null;
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return created.WaitAsync(token);
        }
    }

    private static async Task<IReadOnlyList<InstalledAppInfo>> RefreshDetailFromFreshSummaryAsync(
        int generation,
        CancellationToken token)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var summary = await Task.Run(
            () => Scan(token, includeLogos: false, generation),
            token).ConfigureAwait(false);
        long summaryMs = sw.ElapsedMilliseconds;

        if (!UserScopedCacheService.TryRead<Dictionary<string, string?>>(
                PersistentLogoMapKey, PersistentLogoMapTtl, out var persistedLogos) ||
            persistedLogos == null || persistedLogos.Count == 0)
        {
            LogService.Info("debloat", $"DEBLOAT_DETAIL_FAST_FALLBACK reason=no-logo-map summaryMs={summaryMs}");
            return await GetViaWorkerAsync(includeLogos: true, generation, token).ConfigureAwait(false);
        }

        var detailed = new InstalledAppInfo[summary.Count];
        int resolved = 0;
        bool complete = true;
        for (int i = 0; i < summary.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var app = summary[i];
            if (!persistedLogos.TryGetValue(app.PackageFullName, out var logo) ||
                (logo != null && !File.Exists(logo)))
            {
                complete = false;
                break;
            }

            if (!string.IsNullOrWhiteSpace(logo)) resolved++;
            LogoPathCache.Set(app.PackageFullName, logo);
            detailed[i] = app with { LogoPath = logo };
        }

        if (!complete)
        {
            LogService.Info("debloat", $"DEBLOAT_DETAIL_FAST_FALLBACK reason=coverage summaryMs={summaryMs}");
            return await GetViaWorkerAsync(includeLogos: true, generation, token).ConfigureAwait(false);
        }

        PublishCache(detailed, includeLogos: true, generation);
        LogService.Info("debloat",
            $"DEBLOAT_DETAIL_FAST_REFRESH count={detailed.Length} logos={resolved} summaryMs={summaryMs} totalMs={sw.ElapsedMilliseconds}");
        return detailed;
    }

    private static async Task<IReadOnlyList<InstalledAppInfo>> AwaitDetailFlightAsSummaryAsync(
        Task<IReadOnlyList<InstalledAppInfo>> detailFlight,
        CancellationToken token)
    {
        await detailFlight.WaitAsync(token).ConfigureAwait(false);
        lock (CacheLock)
        {
            if (_summaryCache != null && DateTime.UtcNow - _summaryCacheAt < TimeSpan.FromMinutes(5))
                return _summaryCache;
        }

        var detail = await detailFlight.ConfigureAwait(false);
        return detail.Select(x => string.IsNullOrWhiteSpace(x.LogoPath) ? x : x with { LogoPath = null }).ToArray();
    }

    private static async Task<IReadOnlyList<InstalledAppInfo>> GetViaWorkerAsync(bool includeLogos, int generation, CancellationToken token)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await WorkerClientService.RunAsync<InstalledAppInfo[]>("apps", includeLogos ? "logos" : null, TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
        PublishCache(result, includeLogos, generation);
        LogService.Info("debloat", $"Installed-app worker inventory logos={includeLogos} count={result.Length} ms={sw.ElapsedMilliseconds}");
        return result;
    }

    private static void PublishCache(IReadOnlyList<InstalledAppInfo> result, bool includeLogos, int generation)
    {
        InstalledAppInfo[] summary = includeLogos
            ? result.Select(x => string.IsNullOrWhiteSpace(x.LogoPath) ? x : x with { LogoPath = null }).ToArray()
            : result.ToArray();
        DateTime now = DateTime.UtcNow;
        lock (CacheLock)
        {
            if (generation != _inventoryGeneration) return;
            _summaryCache = summary;
            _summaryCacheAt = now;
            _summaryNeedsBackgroundRefresh = false;
            if (includeLogos)
            {
                _detailCache = result;
                _detailCacheAt = now;
            }
        }
        UserScopedCacheService.Write(PersistentSummaryKey, summary);
    }

    public static bool SummaryNeedsBackgroundRefresh
    {
        get { lock (CacheLock) return _summaryNeedsBackgroundRefresh; }
    }

    public static void TrimHeavyCache()
    {
        lock (CacheLock)
        {
            _inventoryGeneration++;
            _detailCache = null; _detailCacheAt = default;
            _detailFlight = null;
            LogoPathCache.Clear();
        }
    }

    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _inventoryGeneration++;
            _summaryCache = null; _detailCache = null;
            _summaryCacheAt = default; _detailCacheAt = default;
            _summaryFlight = null; _detailFlight = null;
            _summaryNeedsBackgroundRefresh = false;
            LogoPathCache.Clear();
        }
        UserScopedCacheService.Remove(PersistentSummaryKey);
    }
    private static IReadOnlyList<InstalledAppInfo> Scan(CancellationToken token, bool includeLogos, int generation)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pm = new PackageManager();
        var result = new List<InstalledAppInfo>();
        Dictionary<string, string?>? persistentLogos = null;
        if (includeLogos)
        {
            if (!UserScopedCacheService.TryRead<Dictionary<string, string?>>(
                    PersistentLogoMapKey, PersistentLogoMapTtl, out persistentLogos) ||
                persistentLogos == null)
            {
                persistentLogos = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            }
            else if (persistentLogos.Comparer != StringComparer.OrdinalIgnoreCase)
            {
                persistentLogos = new Dictionary<string, string?>(persistentLogos, StringComparer.OrdinalIgnoreCase);
            }
        }

        var currentPackageFullNames = includeLogos
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;
        int logoCacheHits = 0;
        int logoRebaseHits = 0;
        int logoCacheMisses = 0;

        foreach (Package p in pm.FindPackagesForUser(string.Empty))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (p.IsFramework || p.IsResourcePackage) continue;
                string name = p.Id.Name;
                string display = string.IsNullOrWhiteSpace(p.DisplayName) || p.DisplayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? name : p.DisplayName;
                string publisher = string.IsNullOrWhiteSpace(p.PublisherDisplayName) || p.PublisherDisplayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? p.Id.PublisherId : p.PublisherDisplayName;
                string? logo = null;
                if (includeLogos)
                {
                    currentPackageFullNames!.Add(p.Id.FullName);
                    logo = ResolveLogoCached(p, persistentLogos!, out bool logoCacheHit, out bool logoRebased);
                    if (logoCacheHit) logoCacheHits++; else logoCacheMisses++;
                    if (logoRebased) logoRebaseHits++;
                }
                var protectedApp = ProtectedNames.Any(x => name.Equals(x, StringComparison.OrdinalIgnoreCase) || name.StartsWith(x + ".", StringComparison.OrdinalIgnoreCase));
                var category = Classify(name, publisher, protectedApp);
                var recommended = !protectedApp && RecommendedNames.Contains(name);
                var risk = protectedApp ? DebloatRisk.Protected : recommended ? DebloatRisk.Safe : name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ? DebloatRisk.Advanced : DebloatRisk.Optional;
                string reason = protectedApp ? "Kritická součást Windows — odstranění je blokované." : recommended ? "Volitelná aplikace běžně nepotřebná pro základní funkce Windows." : risk == DebloatRisk.Advanced ? "Microsoft balík mimo Recommended — před odstraněním zkontroluj použití." : "Volitelná aplikace třetí strany.";
                result.Add(new InstalledAppInfo(name, p.Id.FullName, p.Id.FamilyName, display, publisher, logo, recommended, category, risk, protectedApp, reason));
            }
            catch (Exception ex) { LogService.Error("debloat-scan", $"Package scan failed: {p.Id.Name}", ex); }
        }
        var final = result.GroupBy(x => x.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

        if (includeLogos && persistentLogos != null && currentPackageFullNames != null)
        {
            foreach (string stale in persistentLogos.Keys.Where(x => !currentPackageFullNames.Contains(x)).ToArray())
                persistentLogos.Remove(stale);
            lock (PersistentLogoMapLock)
                _persistentLogoMapMemory = new Dictionary<string, string?>(persistentLogos, StringComparer.OrdinalIgnoreCase);
            UserScopedCacheService.Write(PersistentLogoMapKey, persistentLogos);
        }

        PublishCache(final, includeLogos, generation);
        LogService.Info("debloat", $"Installed-app local inventory logos={includeLogos} count={final.Count} logoCache={persistentLogos?.Count ?? 0} logoHits={logoCacheHits} logoRebaseHits={logoRebaseHits} logoMisses={logoCacheMisses} ms={sw.ElapsedMilliseconds}");
        return final;
    }

    private static string? ResolveLogoCached(
        Package package,
        Dictionary<string, string?> persistentLogos,
        out bool cacheHit,
        out bool rebased)
    {
        string key = package.Id.FullName;
        rebased = false;

        if (LogoPathCache.TryGet(key, out var memoryCached))
        {
            if (memoryCached == null || File.Exists(memoryCached))
            {
                cacheHit = true;
                return memoryCached;
            }
        }

        if (persistentLogos.TryGetValue(key, out var persisted))
        {
            if (persisted == null || File.Exists(persisted))
            {
                LogoPathCache.Set(key, persisted);
                cacheHit = true;
                return persisted;
            }

            persistentLogos.Remove(key);
        }

        if (TryRebasePersistedLogo(package, persistentLogos, out var rebasedPath))
        {
            LogoPathCache.Set(key, rebasedPath);
            persistentLogos[key] = rebasedPath;
            cacheHit = true;
            rebased = true;
            return rebasedPath;
        }

        string? resolved = ResolveLogo(package);
        LogoPathCache.Set(key, resolved);
        persistentLogos[key] = resolved;
        cacheHit = false;
        return resolved;
    }

    private static bool TryRebasePersistedLogo(
        Package package,
        IReadOnlyDictionary<string, string?> persistentLogos,
        out string? rebasedPath)
    {
        rebasedPath = null;
        string currentRoot;
        try { currentRoot = package.InstalledLocation.Path; }
        catch { return false; }
        return TryRebasePersistedLogo(package.Id.Name, package.Id.FullName, currentRoot, persistentLogos, out rebasedPath);
    }

    private static bool TryRebasePersistedLogo(
        string packageName,
        string currentFullName,
        string currentRoot,
        IReadOnlyDictionary<string, string?> persistentLogos,
        out string? rebasedPath)
    {
        rebasedPath = null;
        if (string.IsNullOrWhiteSpace(currentRoot)) return false;
        string prefix = packageName + "_";

        foreach (var pair in persistentLogos)
        {
            if (string.Equals(pair.Key, currentFullName, StringComparison.OrdinalIgnoreCase) ||
                !pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(pair.Value))
                continue;

            string oldPath = pair.Value!;
            int rootIndex = oldPath.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase);
            if (rootIndex < 0) continue;
            int relativeStart = rootIndex + pair.Key.Length;
            while (relativeStart < oldPath.Length &&
                   (oldPath[relativeStart] == Path.DirectorySeparatorChar ||
                    oldPath[relativeStart] == Path.AltDirectorySeparatorChar))
                relativeStart++;
            if (relativeStart >= oldPath.Length) continue;

            string candidate = Path.Combine(currentRoot, oldPath[relativeStart..]);
            if (!File.Exists(candidate)) continue;
            rebasedPath = candidate;
            return true;
        }

        return false;
    }

    internal static void RunLogoRebaseSmoke()
    {
        string root = Path.Combine(Path.GetTempPath(), "SpankyLogoRebaseSmoke-" + Guid.NewGuid().ToString("N"));
        const string packageName = "Smoke.App";
        const string oldFullName = "Smoke.App_1.0.0.0_x64__smoke";
        const string currentFullName = "Smoke.App_2.0.0.0_x64__smoke";
        string currentRoot = Path.Combine(root, currentFullName);
        string currentLogo = Path.Combine(currentRoot, "Assets", "Logo.targetsize-48.png");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(currentLogo)!);
            File.WriteAllBytes(currentLogo, new byte[] { 1, 2, 3, 4 });
            var persisted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [oldFullName] = Path.Combine(root, oldFullName, "Assets", "Logo.targetsize-48.png")
            };
            if (!TryRebasePersistedLogo(packageName, currentFullName, currentRoot, persisted, out var rebased) ||
                !string.Equals(rebased, currentLogo, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Installed-app logo rebase smoke failed.");
            LogService.Info("DEBLOAT_LOGO_REBASE_SMOKE_OK");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    public static Task<string?> GetLogoPathAsync(string packageFullName, CancellationToken token = default)
    {
        if (LogoPathCache.TryGet(packageFullName, out var cached))
        {
            if (cached == null || File.Exists(cached))
                return Task.FromResult(cached);
        }

        if (TryGetPersistedLogoPath(packageFullName, out var persisted))
        {
            LogoPathCache.Set(packageFullName, persisted);
            return Task.FromResult(persisted);
        }

        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var pm = new PackageManager();
                var package = pm.FindPackageForUser(string.Empty, packageFullName);
                string? resolved = package == null ? null : ResolveLogo(package);
                LogoPathCache.Set(packageFullName, resolved);
                StorePersistedLogoPath(packageFullName, resolved);
                return resolved;
            }
            catch { return null; }
        }, token);
    }

    private static bool TryGetPersistedLogoPath(string packageFullName, out string? path)
    {
        lock (PersistentLogoMapLock)
        {
            _persistentLogoMapMemory ??= LoadPersistentLogoMap();
            if (!_persistentLogoMapMemory.TryGetValue(packageFullName, out path))
                return false;

            if (path == null || File.Exists(path))
                return true;

            _persistentLogoMapMemory.Remove(packageFullName);
            path = null;
            return false;
        }
    }

    private static Dictionary<string, string?> LoadPersistentLogoMap()
    {
        if (UserScopedCacheService.TryRead<Dictionary<string, string?>>(
                PersistentLogoMapKey, PersistentLogoMapTtl, out var persisted) &&
            persisted != null)
            return new Dictionary<string, string?>(persisted, StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    private static void StorePersistedLogoPath(string packageFullName, string? path)
    {
        Dictionary<string, string?> snapshot;
        lock (PersistentLogoMapLock)
        {
            _persistentLogoMapMemory ??= LoadPersistentLogoMap();
            _persistentLogoMapMemory[packageFullName] = path;
            snapshot = new Dictionary<string, string?>(_persistentLogoMapMemory, StringComparer.OrdinalIgnoreCase);
        }
        UserScopedCacheService.Write(PersistentLogoMapKey, snapshot);
    }

    public static Task<bool> IsPackageInstalledAsync(string packageFullName, CancellationToken token = default)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var pm = new PackageManager();
                return pm.FindPackageForUser(string.Empty, packageFullName) != null;
            }
            catch (ArgumentException) { return false; }
        }, token);
    }

    internal static void ClearMemoryCacheForSmoke()
    {
        lock (CacheLock)
        {
            _summaryCache = null;
            _detailCache = null;
            _summaryCacheAt = default;
            _detailCacheAt = default;
            _summaryFlight = null;
            _detailFlight = null;
            _summaryNeedsBackgroundRefresh = false;
            LogoPathCache.Clear();
        }
        lock (PersistentLogoMapLock) _persistentLogoMapMemory = null;
    }

    internal static void ClearPersistentLogoCacheForSmoke()
    {
        lock (PersistentLogoMapLock) _persistentLogoMapMemory = null;
        UserScopedCacheService.Remove(PersistentLogoMapKey);
    }

    public static (int Summary, int Detail, int Logos) CacheStats()
    {
        lock (CacheLock) return (_summaryCache?.Length ?? 0, _detailCache?.Count ?? 0, LogoPathCache.Count);
    }

    private static string? ResolveLogo(Package p)
    {
        try
        {
            string root = p.InstalledLocation.Path;
            string rel = p.Logo?.LocalPath?.TrimStart('/', '\\') ?? string.Empty;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(rel)) return null;

            rel = Uri.UnescapeDataString(rel).Replace('/', Path.DirectorySeparatorChar);
            string candidate = Path.Combine(root, rel);
            if (File.Exists(candidate)) return candidate;

            string? directory = Path.GetDirectoryName(candidate);
            string extension = Path.GetExtension(candidate);
            string stem = Path.GetFileNameWithoutExtension(candidate);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem) || !Directory.Exists(directory))
                return null;

            return Directory.EnumerateFiles(directory, stem + "*" + extension, SearchOption.TopDirectoryOnly)
                .Select(path => new { Path = path, Score = LogoVariantScore(path) })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Path.Length)
                .Select(x => x.Path)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static int LogoVariantScore(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int score = 0;
        if (name.Contains("altform-unplated", StringComparison.OrdinalIgnoreCase)) score += 120;
        if (name.Contains("targetsize-48", StringComparison.OrdinalIgnoreCase)) score += 520;
        else if (name.Contains("targetsize-44", StringComparison.OrdinalIgnoreCase)) score += 510;
        else if (name.Contains("targetsize-64", StringComparison.OrdinalIgnoreCase)) score += 500;
        else if (name.Contains("targetsize-32", StringComparison.OrdinalIgnoreCase)) score += 490;
        else if (name.Contains("scale-200", StringComparison.OrdinalIgnoreCase)) score += 440;
        else if (name.Contains("scale-150", StringComparison.OrdinalIgnoreCase)) score += 430;
        else if (name.Contains("scale-125", StringComparison.OrdinalIgnoreCase)) score += 420;
        else if (name.Contains("scale-100", StringComparison.OrdinalIgnoreCase)) score += 410;
        else if (name.Contains("targetsize-", StringComparison.OrdinalIgnoreCase)) score += 360;
        else if (name.Contains("scale-", StringComparison.OrdinalIgnoreCase)) score += 320;
        if (name.Contains("contrast-", StringComparison.OrdinalIgnoreCase)) score -= 40;
        return score;
    }

    private static DebloatCategory Classify(string name, string publisher, bool protectedApp)
    {
        if (protectedApp) return DebloatCategory.System;
        if (name.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || name.Contains("Solitaire", StringComparison.OrdinalIgnoreCase)) return DebloatCategory.Gaming;
        if (name.Contains("Zune", StringComparison.OrdinalIgnoreCase) || name.Contains("Clipchamp", StringComparison.OrdinalIgnoreCase)) return DebloatCategory.Media;
        if (name.Contains("Teams", StringComparison.OrdinalIgnoreCase) || name.Contains("People", StringComparison.OrdinalIgnoreCase)) return DebloatCategory.Communication;
        if (name.Contains("DevHome", StringComparison.OrdinalIgnoreCase) || name.Contains("PowerAutomate", StringComparison.OrdinalIgnoreCase)) return DebloatCategory.Tools;
        if (name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) return DebloatCategory.MicrosoftExtras;
        return DebloatCategory.ThirdParty;
    }
}
